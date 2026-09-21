using JevBrowse.Domain;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace JevBrowse.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();

        // A failure on the UI thread used to end the process with nothing recorded: the Windows crash report names a
        // module and a stowed-exception code, not the thing the app was doing. Log the real exception first, then let
        // the runtime carry on as it would have — this records a crash, it does not hide one.
        UnhandledException += (_, e) => Record("UI thread", e.Exception, e.Message);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Record("AppDomain", e.ExceptionObject as Exception, null);
        TaskScheduler.UnobservedTaskException += (_, e) => Record("unobserved task", e.Exception, null);
    }

    /// <summary>Where the last unhandled failure is written, next to the benchmarks and the data it concerns.</summary>
    internal static string CrashLogPath =>
        Path.Combine(Environment.GetEnvironmentVariable("JEVBROWSE_DATA_DIR") ?? Path.Combine(AppContext.BaseDirectory, "data"), "crash.log");

    private static void Record(string where, Exception? ex, string? message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
            File.AppendAllText(CrashLogPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{where}] {message}\n{ex?.GetType().FullName}: {ex?.Message}\n{ex?.StackTrace}\n" +
                (ex?.InnerException is { } inner ? $"  inner: {inner.GetType().FullName}: {inner.Message}\n{inner.StackTrace}\n" : "") + "\n");
        }
        catch (Exception) { /* never let the recorder be the second failure */ }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // One browser per data folder. A second start (a link clicked in another program while JevBrowse is open) hands its link to the running one and exits.
        // Keyed by the data folder so that separate profiles, and the automated checks that use their own folders, stay separate instances.
        try
        {
            var dataDir = Environment.GetEnvironmentVariable("JEVBROWSE_DATA_DIR") ?? Path.Combine(AppContext.BaseDirectory, "data");
            var key = "jevbrowse-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(dataDir).ToLowerInvariant())))[..16];
            var main = AppInstance.FindOrRegisterForKey(key);
            if (!main.IsCurrent)
            {
                var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
                var handedOver = Task.Run(async () => { await main.RedirectActivationToAsync(activation); }).Wait(TimeSpan.FromSeconds(10));
                if (handedOver) { Environment.Exit(0); return; }
            }
            else main.Activated += (_, e) =>
            {
                var link = e.Kind == ExtendedActivationKind.Launch && e.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs la
                    ? LaunchArgs.ExtractUrl(la.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries)) : null;
                var w = _window as MainWindow;
                w?.DispatcherQueue.TryEnqueue(async () => { if (link is not null) await w.OpenExternalLinkAsync(link); else w.Activate(); });
            };
        }
        catch (Exception ex) { Record("single instance", ex, "continuing as a separate instance"); }
        _window = new MainWindow();
        _window.Activate();
    }
}
