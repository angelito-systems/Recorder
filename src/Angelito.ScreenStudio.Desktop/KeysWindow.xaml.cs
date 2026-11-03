using System.IO;
using System.Windows;
using Angelito.ScreenStudio.Core;

namespace Angelito.ScreenStudio.Desktop;

/// <summary>Edits keys.json: extra allowed shortcuts and excluded apps. Lines that would log typed text are refused.</summary>
public partial class KeysWindow : Window
{
    private static string SettingsPath => Path.Combine(App.DataDir, "keys.json");

    public KeysWindow()
    {
        InitializeComponent();
        var s = KeySettings.Load(SettingsPath);
        AllowedBox.Text = string.Join(Environment.NewLine, s.ExtraAllowed);
        AppsBox.Text = string.Join(Environment.NewLine, s.ExcludedApps);
        DefaultsBox.Text = "Atajos:\n" + string.Join("  ", KeyFilter.DefaultAllowed) + "\n\nProgramas excluidos:\n" + string.Join("  ", KeyFilter.DefaultExcludedApps);
    }

    public static void ShowFor(Window owner) => new KeysWindow { Owner = owner }.ShowDialog();

    private static string[] Lines(string text) => text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var (ok, bad) = KeySettings.ParseAllowed(Lines(AllowedBox.Text));
        if (bad.Count > 0)
        {
            MessageBox.Show(this, "Estas líneas no son atajos válidos o escribirían texto, así que no se pueden permitir:\n\n" + string.Join("\n", bad.Select(b => "• " + b)),
                App.Product, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            new KeySettings(ok, Lines(AppsBox.Text).Select(a => a.ToLowerInvariant()).Distinct().ToList()).Save(SettingsPath);
            DialogResult = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.Log(ex.ToString());
            MessageBox.Show(this, "No se pudo guardar la lista.\n\n" + ex.Message, App.Product, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
