using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace Argus.App.Services;

/// <summary>
/// Cross-elevation single-instance guard using a named Mutex and a per-user NamedPipeServerStream.
/// Ensures only one ARGUS instance runs per Windows user session and forwards activation requests
/// (including from non-elevated shortcuts to an already-running elevated instance).
/// </summary>
public sealed partial class SingleInstanceGuard : IDisposable
{
    public const string DefaultMutexName = @"Local\Argus_Desktop_SingleInstance";
    public const string DefaultPipeName = "Argus_Desktop_SingleInstance_Pipe";

    private const int SwRestore = 9;
    private const uint AsfwAny = uint.MaxValue;

    private readonly string _mutexName;
    private readonly string _pipeName;
    private Mutex? _mutex;
    private bool _ownsMutex;
    private CancellationTokenSource? _listenerCts;
    private Task? _listenerTask;
    private bool _disposed;

    public SingleInstanceGuard(
        string mutexName = DefaultMutexName,
        string pipeName = DefaultPipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutexName);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _mutexName = mutexName;
        _pipeName = pipeName;
    }

    public bool IsPrimaryInstance { get; private set; }

    /// <summary>
    /// Attempts to acquire the single-instance mutex.
    /// When <paramref name="waitForPreviousExit"/> is true (e.g. during UAC elevation handoff),
    /// waits up to <paramref name="handoffTimeout"/> for the outgoing instance to release the mutex.
    /// </summary>
    public bool TryAcquire(bool waitForPreviousExit = false, TimeSpan? handoffTimeout = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        TimeSpan timeout = handoffTimeout ?? TimeSpan.FromSeconds(5);
        DateTime deadlineUtc = DateTime.UtcNow + timeout;

        while (true)
        {
            if (TryCreateOrOpenMutex(out Mutex? mutex, out bool createdNew, out bool accessDenied))
            {
                if (createdNew)
                {
                    _mutex = mutex;
                    _ownsMutex = true;
                    IsPrimaryInstance = true;
                    return true;
                }

                if (waitForPreviousExit && mutex is not null)
                {
                    try
                    {
                        TimeSpan remaining = deadlineUtc - DateTime.UtcNow;
                        if (remaining > TimeSpan.Zero && mutex.WaitOne(remaining))
                        {
                            _mutex = mutex;
                            _ownsMutex = true;
                            IsPrimaryInstance = true;
                            return true;
                        }
                    }
                    catch (AbandonedMutexException)
                    {
                        _mutex = mutex;
                        _ownsMutex = true;
                        IsPrimaryInstance = true;
                        return true;
                    }
                }

                mutex?.Dispose();
                IsPrimaryInstance = false;
                return false;
            }

            if (accessDenied)
            {
                if (waitForPreviousExit && DateTime.UtcNow < deadlineUtc)
                {
                    Thread.Sleep(150);
                    continue;
                }

                // An elevated instance already owns the mutex with a restricted ACL.
                IsPrimaryInstance = false;
                return false;
            }

            IsPrimaryInstance = false;
            return false;
        }
    }

    /// <summary>
    /// Starts listening on the named pipe for activation signals from secondary launches.
    /// </summary>
    public void StartActivationListener(Action<string?> onActivate)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(onActivate);

        if (_listenerCts is not null)
        {
            return;
        }

        _listenerCts = new CancellationTokenSource();
        CancellationToken ct = _listenerCts.Token;
        _listenerTask = Task.Run(() => ListenLoopAsync(onActivate, ct), ct);
    }

    /// <summary>
    /// Signals the already-running primary instance to restore and focus its main window.
    /// </summary>
    public bool SignalExistingInstance(string? sectionArg = null, int timeoutMs = 1500)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                _ = AllowSetForegroundWindow(AsfwAny);
            }

            string payload = string.IsNullOrWhiteSpace(sectionArg)
                ? "ACTIVATE"
                : $"ACTIVATE:{sectionArg.Trim()}";

            using var client = new NamedPipeClientStream(
                serverName: ".",
                pipeName: _pipeName,
                direction: PipeDirection.Out,
                options: PipeOptions.None);

            client.Connect(timeoutMs);
            byte[] bytes = Encoding.UTF8.GetBytes(payload);
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
            return true;
        }
        catch
        {
            // Fallback: best-effort Win32 window restore if pipe is unavailable.
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    IntPtr hwnd = FindWindowW(null, "ARGUS");
                    if (hwnd != IntPtr.Zero)
                    {
                        ShowWindow(hwnd, SwRestore);
                        SetForegroundWindow(hwnd);
                        return true;
                    }
                }
                catch
                {
                    // Ignore fallback errors
                }
            }

            return false;
        }
    }

    private async Task ListenLoopAsync(Action<string?> onActivate, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = CreatePipeServer();
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                byte[] buffer = new byte[256];
                int bytesRead = await server.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                string message = bytesRead > 0
                    ? Encoding.UTF8.GetString(buffer, 0, bytesRead).Trim()
                    : "ACTIVATE";

                string? section = null;
                if (message.StartsWith("ACTIVATE:", StringComparison.OrdinalIgnoreCase))
                {
                    section = message["ACTIVATE:".Length..].Trim();
                }

                onActivate(section);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Keep listening even if a single client disconnects abruptly.
                try
                {
                    await Task.Delay(100, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            finally
            {
                if (server is not null)
                {
                    await server.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private NamedPipeServerStream CreatePipeServer()
    {
        if (OperatingSystem.IsWindows())
        {
            return CreateWindowsPipeServer();
        }

        return new NamedPipeServerStream(
            _pipeName,
            PipeDirection.In,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
    }

    [SupportedOSPlatform("windows")]
    private NamedPipeServerStream CreateWindowsPipeServer()
    {
        var pipeSecurity = new PipeSecurity();
        SecurityIdentifier? currentUser = WindowsIdentity.GetCurrent().User;
        if (currentUser is not null)
        {
            pipeSecurity.AddAccessRule(new PipeAccessRule(
                currentUser,
                PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
                AccessControlType.Allow));
        }

        return NamedPipeServerStreamAcl.Create(
            _pipeName,
            PipeDirection.In,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 512,
            outBufferSize: 0,
            pipeSecurity);
    }

    private bool TryCreateOrOpenMutex(out Mutex? mutex, out bool createdNew, out bool accessDenied)
    {
        mutex = null;
        createdNew = false;
        accessDenied = false;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                mutex = CreateWindowsMutex(out createdNew);
            }
            else
            {
                mutex = new Mutex(initiallyOwned: true, _mutexName, out createdNew);
            }

            return true;
        }
        catch (UnauthorizedAccessException)
        {
            accessDenied = true;
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private Mutex CreateWindowsMutex(out bool createdNew)
    {
        var security = new MutexSecurity();
        SecurityIdentifier? currentUser = WindowsIdentity.GetCurrent().User;
        if (currentUser is not null)
        {
            security.AddAccessRule(new MutexAccessRule(
                currentUser,
                MutexRights.FullControl,
                AccessControlType.Allow));
        }

        return MutexAcl.Create(initiallyOwned: true, _mutexName, out createdNew, security);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _listenerCts?.Cancel();
        }
        catch
        {
            // Ignore cancellation errors during shutdown
        }

        try
        {
            _listenerTask?.Wait(TimeSpan.FromMilliseconds(300));
        }
        catch
        {
            // Ignore shutdown timeout
        }

        _listenerCts?.Dispose();
        _listenerCts = null;
        _listenerTask = null;

        if (_mutex is not null)
        {
            if (_ownsMutex)
            {
                try
                {
                    _mutex.ReleaseMutex();
                }
                catch
                {
                    // Ignore if already released
                }
            }

            _mutex.Dispose();
            _mutex = null;
            _ownsMutex = false;
        }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(uint dwProcessId);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr FindWindowW(string? lpClassName, string lpWindowName);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hWnd);
}
