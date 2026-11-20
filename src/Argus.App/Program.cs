using System.Runtime.InteropServices;
using Argus.Core.Configuration;
using Argus.Core.Logging;
using Avalonia;
using Microsoft.Extensions.Logging;

namespace Argus.App;

internal static partial class Program
{
    private const string SingleInstanceMutexName = @"Local\Argus_Desktop_SingleInstance";
    private const string ActivationEventName = @"Local\Argus_Desktop_ActivateEvent";
    private const int SwRestore = 9;
    private const uint AsfwAny = uint.MaxValue;

    internal static EventWaitHandle? ActivationEvent { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        bool allowMultiInstance = args.Any(static a =>
            a.Equals("--allow-multi-instance", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--section", StringComparison.OrdinalIgnoreCase));

        Mutex? singleInstanceMutex = null;
        if (!allowMultiInstance)
        {
            singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
            if (!createdNew)
            {
                SignalExistingInstance();
                singleInstanceMutex.Dispose();
                return;
            }

            ActivationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
        }

        using var crashLoggerProvider = new ArgusFileLoggerProvider(ArgusPaths.GetDefaultLogsDirectory());
        var crashLogger = crashLoggerProvider.CreateLogger("Argus.CrashGuard");

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                crashLogger.LogCritical(ex, "Unhandled AppDomain exception (IsTerminating={IsTerminating}).", e.IsTerminating);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            crashLogger.LogError(e.Exception, "Unobserved background task exception.");
            e.SetObserved();
        };

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            ActivationEvent?.Dispose();
            ActivationEvent = null;
            singleInstanceMutex?.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static void SignalExistingInstance()
    {
        try
        {
            _ = AllowSetForegroundWindow(AsfwAny);
            if (EventWaitHandle.TryOpenExisting(ActivationEventName, out var activateEvent))
            {
                using (activateEvent)
                {
                    activateEvent.Set();
                }

                return;
            }

            IntPtr hwnd = FindWindowW(null, "ARGUS");
            if (hwnd != IntPtr.Zero)
            {
                ShowWindow(hwnd, SwRestore);
                SetForegroundWindow(hwnd);
            }
        }
        catch
        {
            // best effort window activation
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
