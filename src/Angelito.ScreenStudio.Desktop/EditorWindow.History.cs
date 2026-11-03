using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Angelito.ScreenStudio.Core;

namespace Angelito.ScreenStudio.Desktop;

/// <summary>
/// Recovery history: besides the live file and its .bak, a snapshot is kept every couple of minutes in ".history" next to
/// the project (newest 20). Restoring is an ordinary edit, so it can be undone.
/// </summary>
public partial class EditorWindow
{
    private const int MaxSnapshots = 20;
    private static readonly TimeSpan SnapshotEvery = TimeSpan.FromMinutes(2);
    private DateTime _lastSnapshot = DateTime.MinValue;

    private string HistoryDir => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(_path))!, ".history");

    private void Snapshot()
    {
        if (DateTime.Now - _lastSnapshot < SnapshotEvery) return;
        try
        {
            Directory.CreateDirectory(HistoryDir);
            var name = $"{System.IO.Path.GetFileNameWithoutExtension(_path)}-{DateTime.Now:yyyyMMdd-HHmmss}{StudioProject.Extension}";
            File.Copy(_path, System.IO.Path.Combine(HistoryDir, name), overwrite: true);
            _lastSnapshot = DateTime.Now;
            foreach (var old in Snapshots().Skip(MaxSnapshots)) File.Delete(old.Path);
        }
        catch (IOException ex) { App.Log("snapshot: " + ex.Message); } // history is best-effort; the save itself succeeded
    }

    /// <summary>Snapshots of this project, newest first.</summary>
    private List<(string Path, DateTime When)> Snapshots()
    {
        if (!Directory.Exists(HistoryDir)) return [];
        var prefix = System.IO.Path.GetFileNameWithoutExtension(_path) + "-";
        return Directory.GetFiles(HistoryDir, prefix + "*" + StudioProject.Extension)
            .Select(f => (Path: f, Stamp: System.IO.Path.GetFileNameWithoutExtension(f)[prefix.Length..]))
            .Select(x => (x.Path, When: DateTime.TryParseExact(x.Stamp, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : DateTime.MinValue))
            .Where(x => x.When != DateTime.MinValue)
            .OrderByDescending(x => x.When).ToList();
    }

    private void OnVersions(object sender, RoutedEventArgs e)
    {
        Save(); // the current state becomes a version too, so restoring never loses it
        _lastSnapshot = DateTime.MinValue;
        Snapshot();
        var snaps = Snapshots();
        var list = new ListBox { ItemsSource = snaps.Select(s => s.When.ToString("dddd d 'de' MMMM, HH:mm:ss", CultureInfo.CurrentCulture)).ToList(), MinHeight = 220 };
        if (snaps.Count > 0) list.SelectedIndex = Math.Min(1, snaps.Count - 1); // 0 is "now"
        var restore = new Button { Content = "Restaurar", Padding = new Thickness(14, 4, 14, 4), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var dlg = new Window
        {
            Title = "Versiones del proyecto", Owner = this, Width = 420, Height = 400, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new DockPanel
            {
                Margin = new Thickness(16),
                Children =
                {
                    new TextBlock { Text = "Versiones guardadas automáticamente (la más reciente arriba). Restaurar se puede deshacer con Ctrl+Z.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) },
                    restore, list,
                },
            },
        };
        DockPanel.SetDock((UIElement)((DockPanel)dlg.Content).Children[0], Dock.Top);
        DockPanel.SetDock(restore, Dock.Bottom);
        restore.Click += (_, _) => dlg.DialogResult = list.SelectedIndex >= 0;
        if (dlg.ShowDialog() != true) return;
        try
        {
            var restored = StudioProject.Load(snaps[list.SelectedIndex].Path) with { SessionDir = P.SessionDir };
            Select(refresh: false);
            Commit(restored, "restore" + Guid.NewGuid());
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            MessageBox.Show(this, "No se pudo abrir esa versión: " + ex.Message, App.Product);
        }
    }
}
