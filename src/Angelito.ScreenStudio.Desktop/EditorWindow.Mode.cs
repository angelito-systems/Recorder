using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace Angelito.ScreenStudio.Desktop;

/// <summary>User preferences (settings.json). The editor mode lives here, not in the project, so switching never edits a project.</summary>
public sealed record AppSettings(bool Advanced = false)
{
    private static string PathOf => System.IO.Path.Combine(App.DataDir, "settings.json");

    public static AppSettings Load()
    {
        try { return File.Exists(PathOf) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(PathOf)) ?? new() : new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public void Save()
    {
        try { Core.AtomicFile.WriteAllText(PathOf, JsonSerializer.Serialize(this)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { App.Log("settings: " + e.Message); }
    }
}

/// <summary>
/// Simple mode = record, trim, zoom, export. It only hides tools: everything in the project (texts, annotations,
/// subtitles, framing…) is kept, previewed and exported exactly the same.
/// </summary>
public partial class EditorWindow
{
    private bool _advanced = AppSettings.Load().Advanced;

    private void ApplyMode()
    {
        var adv = _advanced;
        ModeBox.SelectedIndex = adv ? 1 : 0;
        var v = adv ? Visibility.Visible : Visibility.Collapsed;
        AdvancedTools.Visibility = FormatSection.Visibility = v;
        ExportFormatBox.Visibility = ExportHeight.Visibility = ExportQualityBox.Visibility = ExportFpsBox.Visibility = v;
        MicSync.Visibility = MicSyncLabel.Visibility = SysSync.Visibility = SysSyncLabel.Visibility = v;
        const string keysHint = "Rueda: desplazar · Espacio: reproducir · S: dividir · Supr: eliminar tramo · ←/→: fotograma · Ctrl+Z/Y";
        RowsHint.Text = (adv ? "Filas: clips · zoom · texto · subtítulos · anotaciones · audio · clics    " : "Filas: clips · zoom · audio · clics    ") + keysHint;

        var hidden = new List<string>();
        if (P.Texts.Count > 0) hidden.Add($"{P.Texts.Count} textos");
        if (P.Subtitles.Count > 0) hidden.Add($"{P.Subtitles.Count} subtítulos");
        if (P.Annotations.Count > 0) hidden.Add($"{P.Annotations.Count} anotaciones");
        if (_keyEvents.Count > 0 && P.Keys.Visible) hidden.Add("atajos de teclado");
        if (P.Canvas.Aspect != Core.CanvasAspect.Source) hidden.Add("formato " + ((ComboBoxItem)FormatBox.Items[(int)P.Canvas.Aspect]).Content);
        SimpleNote.Visibility = !adv && hidden.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SimpleNote.Text = $"Este proyecto incluye {string.Join(", ", hidden)}. Se conservan y se exportan; cambia a modo avanzado para editarlos.";
    }

    private void OnAbout(object sender, RoutedEventArgs e) => AboutWindow.ShowFor(this);

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || ModeBox.SelectedIndex < 0) return;
        _advanced = ModeBox.SelectedIndex == 1;
        new AppSettings(_advanced).Save();
        if (!_advanced) Select(zoom: _selectedZoom, refresh: false); // drop text/subtitle/annotation selections, keep zoom
        Refresh();
    }
}
