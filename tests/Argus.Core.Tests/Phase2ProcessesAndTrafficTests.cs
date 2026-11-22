using System.Buffers.Binary;
using Argus.Core.Contracts.System;
using Argus.Core.Journal;
using Argus.Maintenance.SystemServices;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Argus.Core.Tests;

public sealed class Phase2ProcessesAndTrafficTests
{
    private sealed class StubPrivilegeService(bool isAdmin) : IPrivilegeService
    {
        public bool IsAdministrator { get; } = isAdmin;
        public bool TryRelaunchElevated(string[]? arguments = null) => false;
    }

    private sealed class InMemoryJournal : IOperationJournal
    {
        public List<OperationJournalEntry> Entries { get; } = [];

        public Task RecordAsync(
            string moduleId,
            OperationSeverity severity,
            string action,
            string details,
            bool succeeded = true,
            CancellationToken cancellationToken = default)
        {
            Entries.Add(new OperationJournalEntry(Guid.NewGuid(), DateTimeOffset.UtcNow, moduleId, severity, action, details, succeeded));
            return Task.CompletedTask;
        }

        public IReadOnlyList<OperationJournalEntry> GetRecent(int maxCount = 200) =>
            Entries.TakeLast(maxCount).ToArray();
    }

    [Theory]
    [InlineData(0, "Idle", true)]
    [InlineData(4, "System", true)]
    [InlineData(312, "smss", true)]
    [InlineData(420, "csrss", true)]
    [InlineData(510, "wininit", true)]
    [InlineData(612, "services", true)]
    [InlineData(640, "lsass", true)]
    [InlineData(1200, "explorer", true)]
    [InlineData(1200, "explorer.exe", true)]
    [InlineData(2048, "svchost", true)]
    [InlineData(8888, "notepad", false)]
    [InlineData(9999, "chrome", false)]
    public void CriticalProcessProtection_ProtectsCoreWindowsProcesses(int pid, string name, bool expectedCritical)
    {
        Assert.Equal(expectedCritical, CriticalProcessProtection.IsCriticalProcess(pid, name, currentProcessId: 77777));
    }

    [Fact]
    public void ProcessMetricsMath_ComputesCpuPercentAndDiskRateAccurately()
    {
        // 2 seconds of CPU time across 4 logical cores in 1 wall second => 50.0%
        double cpu = WindowsProcessMonitorService.ComputeCpuPercent(20_000_000, elapsedSeconds: 1.0, logicalProcessors: 4);
        Assert.Equal(50.0, cpu);

        // Clamped to 100%
        double clamped = WindowsProcessMonitorService.ComputeCpuPercent(100_000_000, elapsedSeconds: 1.0, logicalProcessors: 2);
        Assert.Equal(100.0, clamped);

        // 10240 bytes in 2 seconds => 5120 B/s
        double rate = WindowsProcessMonitorService.ComputeRateBytesPerSec(10240, elapsedSeconds: 2.0);
        Assert.Equal(5120.0, rate);
    }

    [Fact]
    public async Task ProcessMonitorService_HardBlocksSystemAndSelfTerminationAndReadsLiveMetrics()
    {
        var ct = TestContext.Current.CancellationToken;
        var journal = new InMemoryJournal();
        var svc = new WindowsProcessMonitorService(
            new StubPrivilegeService(isAdmin: true),
            journal,
            NullLogger<WindowsProcessMonitorService>.Instance);

        var killSystem = await svc.TerminateProcessAsync(4, ct);
        Assert.False(killSystem.Succeeded);
        Assert.False(killSystem.RequiresElevation);

        var killSelf = await svc.TerminateProcessAsync(Environment.ProcessId, ct);
        Assert.False(killSelf.Succeeded);
        Assert.False(killSelf.RequiresElevation);

        if (OperatingSystem.IsWindows())
        {
            var metrics = await svc.GetProcessMetricsAsync(ct);
            Assert.NotEmpty(metrics);
            Assert.Contains(metrics, m => m.Pid == Environment.ProcessId && m.WorkingSetBytes > 0);
            Assert.Contains(metrics, static m => m.IsCriticalSystem);
        }
    }

    [Theory]
    [InlineData((ushort)10, 4200u, 1500u, true)]
    [InlineData((ushort)11, 4200u, 4096u, false)]
    [InlineData((ushort)26, 8100u, 512u, true)]
    [InlineData((ushort)27, 8100u, 2048u, false)]
    [InlineData((ushort)42, 9100u, 256u, true)]
    [InlineData((ushort)43, 9100u, 1024u, false)]
    [InlineData((ushort)58, 9200u, 128u, true)]
    [InlineData((ushort)59, 9200u, 768u, false)]
    public void KernelNetworkEtwParser_ExtractsPidSizeAndDirection(
        ushort eventId,
        uint payloadPid,
        uint sizeBytes,
        bool expectedIsSend)
    {
        byte[] payload = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), payloadPid);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), sizeBytes);

        bool parsed = WindowsProcessNetworkTrafficService.TryParseKernelNetworkEvent(
            eventId,
            payload,
            headerPid: 999,
            out int pid,
            out uint bytes,
            out bool isSend);

        Assert.True(parsed);
        Assert.Equal((int)payloadPid, pid);
        Assert.Equal(sizeBytes, bytes);
        Assert.Equal(expectedIsSend, isSend);
    }

    [Fact]
    public void KernelNetworkEtwParser_RejectsUnsupportedEventIdsAndInvalidSizes()
    {
        byte[] payload = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), 1234);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), 1024);

        Assert.False(WindowsProcessNetworkTrafficService.TryParseKernelNetworkEvent(99, payload, 1234, out _, out _, out _));
        Assert.False(WindowsProcessNetworkTrafficService.TryParseKernelNetworkEvent(10, payload.AsSpan(0, 4), 1234, out _, out _, out _));

        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), 0);
        Assert.False(WindowsProcessNetworkTrafficService.TryParseKernelNetworkEvent(10, payload, 1234, out _, out _, out _));
    }

    [Fact]
    public async Task NetworkTrafficService_NonElevatedFallbackAggregatesActiveConnectionsCleanly()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var priv = new StubPrivilegeService(isAdmin: false);
        var netMgr = new WindowsNetworkManagerService(priv, new InMemoryJournal(), NullLogger<WindowsNetworkManagerService>.Instance);
        using var traffic = new WindowsProcessNetworkTrafficService(priv, netMgr, NullLogger<WindowsProcessNetworkTrafficService>.Instance);

        traffic.Start();
        Assert.False(traffic.IsEtwActive);

        var snap = await traffic.GetSnapshotAsync(cancellationToken: ct);
        Assert.Equal(NetworkTrafficSourceKind.SocketAndIoEstimate, snap.ActiveSourceKind);
        Assert.False(snap.IsEtwActive);
        Assert.NotEmpty(snap.Processes);

        traffic.Stop();
    }
}
