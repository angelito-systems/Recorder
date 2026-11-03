using System.Diagnostics;
using System.Windows;

namespace Angelito.ScreenStudio.Desktop;

public partial class RenderQueueWindow : Window
{
    private static RenderQueueWindow? s_instance;

    private RenderQueueWindow()
    {
        InitializeComponent();
        List.ItemsSource = RenderQueue.Jobs;
    }

    /// <summary>One shared window for the app-wide queue.</summary>
    public static void ShowQueue()
    {
        s_instance ??= new RenderQueueWindow();
        s_instance.Show();
        s_instance.Activate();
    }

    private static RenderJob Job(object sender) => (RenderJob)((FrameworkElement)sender).DataContext;

    private void OnCancel(object sender, RoutedEventArgs e) => RenderQueue.Cancel(Job(sender));

    private void OnReveal(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{Job(sender).OutputPath}\"") { UseShellExecute = true });

    private void OnClearFinished(object sender, RoutedEventArgs e)
    {
        foreach (var j in RenderQueue.Jobs.Where(j => !j.IsActive).ToList()) RenderQueue.Jobs.Remove(j);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (RenderQueue.Busy)
        {
            if (MessageBox.Show(this, "Hay exportaciones en curso. ¿Cancelarlas?", App.Product, MessageBoxButton.YesNo) != MessageBoxResult.Yes) { e.Cancel = true; return; }
            foreach (var j in RenderQueue.Jobs.Where(j => j.IsActive).ToList()) RenderQueue.Cancel(j);
        }
        s_instance = null;
        base.OnClosing(e);
    }
}
