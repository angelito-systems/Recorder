using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Angelito.ScreenStudio.Desktop;

public partial class App : Application
{
    public const string Product = "Angelito Screen Studio Pro";

    public static string DataDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AngelitoScreenStudio");
    public static string RecordingsDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Angelito Studio");

    protected override void OnStartup(StartupEventArgs e)
    {
        Directory.CreateDirectory(DataDir);
        DispatcherUnhandledException += OnUnhandled;
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

    public static void Log(string text)
    {
        try { File.AppendAllText(Path.Combine(DataDir, "incidents.log"), $"[{DateTime.Now:O}] {text}\n"); } catch (IOException) { }
    }
}
