using Argus.App.Services;
using Argus.Core.Configuration;
using Argus.Core.Logging;
using Avalonia;
using Microsoft.Extensions.Logging;

namespace Argus.App;

internal static class Program
{
    internal static SingleInstanceGuard? InstanceGuard { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        bool isElevationHandoff = args.Any(static a =>
            a.Equals("--allow-multi-instance", StringComparison.OrdinalIgnoreCase));

        string? requestedSection = ExtractSectionArg(args);

        var guard = new SingleInstanceGuard();
        if (!guard.TryAcquire(waitForPreviousExit: isElevationHandoff))
        {
            guard.SignalExistingInstance(requestedSection);
            guard.Dispose();
            return;
        }

        InstanceGuard = guard;

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
            InstanceGuard = null;
            guard.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static string? ExtractSectionArg(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--section", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(args[i + 1]))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
