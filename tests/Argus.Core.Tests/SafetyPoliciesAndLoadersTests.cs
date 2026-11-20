using Argus.Core.Configuration;
using Argus.Core.Contracts.Maintenance;
using Argus.Core.Contracts.Migration;
using Argus.Core.Events;
using Argus.Core.Extensions;
using Argus.Core.Journal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Argus.Core.Tests;

public sealed class SafetyPoliciesAndLoadersTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ArgusSafetyTests_{Guid.NewGuid():N}");

    public SafetyPoliciesAndLoadersTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Theory]
    [InlineData(StorageMediaKind.Hdd, DiskOptimizationAction.DefragmentHdd)]
    [InlineData(StorageMediaKind.SataSsd, DiskOptimizationAction.TrimSsd)]
    [InlineData(StorageMediaKind.NvmeSsd, DiskOptimizationAction.TrimSsd)]
    [InlineData(StorageMediaKind.RemovableUsb, DiskOptimizationAction.AnalyzeOnly)]
    [InlineData(StorageMediaKind.Unknown, DiskOptimizationAction.AnalyzeOnly)]
    public void DiskOptimizationPolicy_NeverDefragmentsSolidStateDrives(
        StorageMediaKind mediaKind,
        DiskOptimizationAction expected)
    {
        Assert.Equal(expected, DiskOptimizationPolicy.ResolveSafeAction(mediaKind));
    }

    [Fact]
    public void MigrationConfirmationPolicy_RequiresExactDiskToken()
    {
        Assert.Equal("WIPE-DISK-2", MigrationConfirmationPolicy.BuildExpectedToken(2));
        Assert.True(MigrationConfirmationPolicy.IsValidConfirmation(2, "WIPE-DISK-2"));
        Assert.True(MigrationConfirmationPolicy.IsValidConfirmation(2, "  WIPE-DISK-2  "));
        Assert.False(MigrationConfirmationPolicy.IsValidConfirmation(2, "wipe-disk-2"));
        Assert.False(MigrationConfirmationPolicy.IsValidConfirmation(2, "WIPE-DISK-1"));
        Assert.False(MigrationConfirmationPolicy.IsValidConfirmation(2, "YES"));
        Assert.False(MigrationConfirmationPolicy.IsValidConfirmation(2, ""));
    }

    [Fact]
    public async Task CleanerRuleLoader_BlocksDangerousPathsAndDuplicatesWhileLoadingValidRules()
    {
        string rulesJson = """
        {
          "rules": [
            {
              "id": "win.temp",
              "displayName": "Windows Temp",
              "category": "WindowsTemp",
              "description": "Temporary OS files",
              "paths": ["%TEMP%", "C:\\Windows", "C:\\Windows\\System32", "%SystemRoot%\\..\\evil"],
              "filePatterns": ["*.tmp", "*.log"]
            },
            {
              "id": "win.temp",
              "displayName": "Duplicate Rule",
              "category": "WindowsTemp",
              "description": "Must be skipped",
              "paths": ["%TEMP%"],
              "filePatterns": ["*"]
            },
            {
              "id": "danger.only",
              "displayName": "Dangerous Only",
              "category": "Custom",
              "description": "Must be rejected entirely",
              "paths": ["C:\\", "%SystemDrive%\\"],
              "filePatterns": ["*"]
            }
          ]
        }
        """;

        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "builtin.json"), rulesJson, ct);
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "broken.json"), "{ invalid", ct);

        var loader = new JsonCleanerRuleLoader();
        var result = await loader.LoadRulesAsync(_tempDir, ct);

        Assert.Single(result.ValidRules);
        Assert.Equal("win.temp", result.ValidRules[0].Id);
        Assert.Equal(["%TEMP%"], result.ValidRules[0].Paths);
        Assert.True(result.Warnings.Count >= 5);
    }

    [Theory]
    [InlineData("C:/Windows", true)]
    [InlineData(@"C:\Windows\System32\drivers", true)]
    [InlineData(@"C:\Windows\WinSxS\amd64", true)]
    [InlineData(@"\\?\C:\Windows\Temp", true)]
    [InlineData(@"\\localhost\c$\Temp", true)]
    [InlineData(@"D:\", true)]
    [InlineData(@"C:\Windows\Temp", false)]
    [InlineData(@"%LOCALAPPDATA%\Temp", false)]
    public void IsDangerousSystemPath_HandlesSlashNormalizationSubtreesAndUnc(string path, bool expectedDangerous)
    {
        Assert.Equal(expectedDangerous, JsonCleanerRuleLoader.IsDangerousSystemPath(path));
    }

    [Fact]
    public async Task ExtensionDiscovery_LoadsValidManifestAndBlocksPathTraversal()
    {
        var ct = TestContext.Current.CancellationToken;
        string ext1Dir = Path.Combine(_tempDir, "ext-good");
        string ext2Dir = Path.Combine(_tempDir, "ext-bad");
        Directory.CreateDirectory(ext1Dir);
        Directory.CreateDirectory(ext2Dir);

        await File.WriteAllTextAsync(
            Path.Combine(ext1Dir, "argus-extension.json"),
            """
            {
              "id": "vendor.sensors.custom",
              "name": "Custom Sensor Pack",
              "version": "1.0.0",
              "kind": "NativeSensorLibrary",
              "entryPoint": "sensor.dll",
              "minimumCoreVersion": "0.2.0",
              "requiresElevation": false
            }
            """,
            ct);

        await File.WriteAllTextAsync(
            Path.Combine(ext2Dir, "argus-extension.json"),
            """
            {
              "id": "vendor.evil",
              "name": "Traversal",
              "version": "1.0.0",
              "kind": "ManagedPlugin",
              "entryPoint": "..\\..\\evil.dll",
              "minimumCoreVersion": "0.2.0",
              "requiresElevation": false
            }
            """,
            ct);

        var discovery = new JsonExtensionDiscovery(NullLogger<JsonExtensionDiscovery>.Instance);
        var discovered = await discovery.DiscoverAsync(_tempDir, ct);

        Assert.Single(discovered);
        Assert.Equal("vendor.sensors.custom", discovered[0].Id);
        Assert.Equal(ArgusExtensionKind.NativeSensorLibrary, discovered[0].Kind);
    }

    [Fact]
    public void AddArgusCore_ResolvesFullCoreServiceGraph()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddArgusCore(
            settingsFilePath: Path.Combine(_tempDir, "di-settings.json"),
            journalFilePath: Path.Combine(_tempDir, "di-journal.jsonl"));

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IArgusEventBus>());
        Assert.NotNull(provider.GetRequiredService<IArgusSettingsStore>());
        Assert.NotNull(provider.GetRequiredService<IOperationJournal>());
        Assert.NotNull(provider.GetRequiredService<ICleanerRuleLoader>());
        Assert.NotNull(provider.GetRequiredService<IArgusExtensionDiscovery>());

        var coreModule = Assert.Single(provider.GetServices<IArgusModule>());
        Assert.Equal(ArgusModuleStage.Ready, coreModule.Descriptor.Stage);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // best-effort temp cleanup
        }
    }
}
