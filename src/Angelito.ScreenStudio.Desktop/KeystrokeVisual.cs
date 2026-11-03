using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Angelito.ScreenStudio.Core;

namespace Angelito.ScreenStudio.Desktop;

/// <summary>The one drawing of a shortcut badge: shown live in the editor and rendered to PNG for export.</summary>
internal static class KeystrokeVisual
{
    /// <summary>Badge for <paramref name="combo"/> with its font size derived from <paramref name="unit"/> (short side of the frame).</summary>
    public static FrameworkElement Create(string combo, KeystrokeStyle s, double unit)
    {
        var px = Math.Max(10, s.Size * unit);
        var parts = KeyFilter.DisplayParts(combo);
        FrameworkElement el;
        if (s.Look == KeyLook.Minimal)
        {
            el = new TextBlock
            {
                Text = string.Join("  +  ", parts), FontFamily = new FontFamily("Segoe UI"), FontSize = px, FontWeight = FontWeights.Bold,
                Foreground = Brushes.White, Margin = new Thickness(px * 0.4),
                Effect = new DropShadowEffect { BlurRadius = px * 0.5, ShadowDepth = 0, Opacity = 0.9 },
            };
        }
        else
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            for (var i = 0; i < parts.Length; i++)
            {
                if (i > 0) row.Children.Add(new TextBlock { Text = "+", FontSize = px * 0.8, Foreground = Brushes.White, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(px * 0.3, 0, px * 0.3, 0) });
                row.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(px * 0.3), Padding = new Thickness(px * 0.45, px * 0.12, px * 0.45, px * 0.16),
                    Background = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
                    BorderThickness = new Thickness(Math.Max(1, px / 18)),
                    Child = new TextBlock { Text = parts[i], FontFamily = new FontFamily("Segoe UI"), FontSize = px, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White },
                });
            }
            el = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xD9, 0x1C, 0x1A, 0x29)), CornerRadius = new CornerRadius(px * 0.9),
                Padding = new Thickness(px * 0.5, px * 0.35, px * 0.5, px * 0.35), Child = row,
            };
        }
        el.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        el.Arrange(new Rect(el.DesiredSize));
        return el;
    }

    public static (string Png, int W, int H) RenderPng(string combo, KeystrokeStyle s, double unit, string path)
    {
        var el = Create(combo, s, unit);
        int w = (int)Math.Ceiling(el.DesiredSize.Width), h = (int)Math.Ceiling(el.DesiredSize.Height);
        var bmp = new RenderTargetBitmap(Math.Max(1, w), Math.Max(1, h), 96, 96, PixelFormats.Pbgra32);
        bmp.Render(el);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using (var fs = File.Create(path)) enc.Save(fs);
        return (path, w, h);
    }
}
