using Microsoft.UI.Xaml;

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
        _window = new MainWindow();
        _window.Activate();
    }
}
