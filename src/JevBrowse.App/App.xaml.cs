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
    internal static string CrashLogPath => Path.Combine(DataLocation.Resolve(), "crash.log");

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

    private InstanceLock? _instanceLock;
    private const string NL = "\r\n";

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    /// <summary>Two processes must never open the same data folder. Says so and stops; opening it anyway could corrupt the person's data.</summary>
    private static void Refuse(string why)
    {
        Record("single instance", null, why);
        MessageBoxW(IntPtr.Zero, "JevBrowse is already using this data folder, so this copy will not open it." + NL + NL + why + NL + NL + "Switch to the JevBrowse window that is already open, or close it and try again.", "JevBrowse", 0x10);
        Environment.Exit(1);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // One browser per data folder, decided by ONE resolver (DataLocation) that storage also uses. A second start (a link clicked in another program while
        // JevBrowse is open) hands its link to the running one and exits. If the hand-over cannot be completed, this copy does NOT carry on: it stops.
        // Independently of the app-instance service, an exclusive lock file in the data folder is held for the life of the process, so that even if that service
        // misbehaves, two processes cannot both open the same database.
        var dataDir = DataLocation.Resolve();
        try
        {
            var key = "jevbrowse-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(dataDir.ToLowerInvariant())))[..16];
            var main = AppInstance.FindOrRegisterForKey(key);
            if (!main.IsCurrent)
            {
                var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
                var handedOver = false;
                try { handedOver = Task.Run(async () => { await main.RedirectActivationToAsync(activation); }).Wait(TimeSpan.FromSeconds(10)); }
                catch (Exception ex) { Record("single instance hand-over", ex, null); }
                if (handedOver) Environment.Exit(0);
                Refuse("The running JevBrowse did not accept the link in time.");
            }
            else main.Activated += (_, e) =>
            {
                var link = e.Kind == ExtendedActivationKind.Launch && e.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs la
                    ? LaunchArgs.ExtractUrl(la.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries)) : null;
                var w = _window as MainWindow;
                w?.DispatcherQueue.TryEnqueue(async () => { if (link is not null) await w.OpenExternalLinkAsync(link); else w.Activate(); });
            };
        }
        catch (Exception ex) { Record("single instance", ex, "the app-instance service failed; the data-folder lock decides"); }
        _instanceLock = InstanceLock.TryAcquire(dataDir);
        if (_instanceLock is null) Refuse("Another copy of JevBrowse has it open.");
        _window = new MainWindow();
        _window.Activate();
    }
}
