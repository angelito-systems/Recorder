using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;

namespace Angelito.ScreenStudio.Desktop;

public partial class App : Application
{
    public const string Product = "Angelito Screen Studio Pro";

    /// <summary>Informational version (includes the commit when built from git, e.g. "0.2.0+4f13c27…").</summary>
    public static string Version { get; } =
        System.Reflection.Assembly.GetExecutingAssembly().GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";

    public static string DataDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AngelitoScreenStudio");
    public static string RecordingsDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Angelito Studio");

    protected override void OnStartup(StartupEventArgs e)
    {
        Directory.CreateDirectory(DataDir);
        RecordVersion();
        DispatcherUnhandledException += OnUnhandled;
        // Background-thread failures can't be recovered in-process; log them (no user content) for diagnosis.
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => Log("fatal: " + ev.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, ev) => { Log("task: " + ev.Exception); ev.SetObserved(); };
        base.OnStartup(e);
        // A project path argument (e.g. double-clicking a .angelstudio) opens the editor directly.
        if (e.Args.Length == 1 && File.Exists(e.Args[0])) new EditorWindow(e.Args[0]).Show();
        else new RecorderWindow().Show();
    }

    /// <summary>Logs the error (no user content) and keeps the app alive; the project is autosaved separately.</summary>
    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception.ToString());
        MessageBox.Show(e.Exception.Message, Product, MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    /// <summary>Local version history: one line the first time each version runs on this machine.</summary>
    private static void RecordVersion()
    {
        try
        {
            var path = Path.Combine(DataDir, "versions.log");
            var last = File.Exists(path) ? File.ReadLines(path).LastOrDefault() : null;
            if (last == null || !last.EndsWith(" " + Version)) File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm} {Version}{Environment.NewLine}");
        }
        catch (IOException) { }
    }

    public static void Log(string text)
    {
        try { File.AppendAllText(Path.Combine(DataDir, "incidents.log"), $"[{DateTime.Now:O}] {text}\n"); } catch (IOException) { }
    }
}
