using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace TLAHStudio.App.Views;

/// <summary>Maps Git porcelain status to the quiet semantic tones used by the diff workbench.</summary>
public sealed class WorkspaceChangeBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var status = value as string ?? string.Empty;
        var key = status.Contains('A', StringComparison.OrdinalIgnoreCase) ||
                  status.Contains('?', StringComparison.Ordinal)
            ? "DiffAddedBrush"
            : status.Contains('D', StringComparison.OrdinalIgnoreCase)
                ? "DiffRemovedBrush"
                : "AccentBrush";
        return WorkspaceReviewBrushes.Resolve(key);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

internal static class WorkspaceReviewBrushes
{
    public static Brush Resolve(string key)
    {
        try
        {
            if (new AccessibilitySettings().HighContrast &&
                Application.Current.Resources.TryGetValue(key, out var highContrast) &&
                highContrast is Brush highContrastBrush)
            {
                return highContrastBrush;
            }
        }
        catch
        {
        }

        var light = App.MainWindow is MainWindow window &&
            window.CurrentAppTheme == Microsoft.UI.Xaml.ElementTheme.Light;
        var color = (key, light) switch
        {
            ("DiffAddedBrush", true) => Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x08, 0x7F, 0x6B),
            ("DiffAddedBrush", false) => Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x62, 0xD8, 0xB4),
            ("DiffRemovedBrush", true) => Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xD6, 0x4B, 0x63),
            ("DiffRemovedBrush", false) => Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xFF, 0x9A, 0xA8),
            ("AccentBrush", true) => Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x66, 0x5C, 0xD7),
            ("AccentBrush", false) => Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x90, 0x85, 0xFF),
            ("TextPrimaryBrush", true) => Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x1D, 0x1B, 0x24),
            ("TextPrimaryBrush", false) => Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xF4, 0xF2, 0xEE),
            _ => Microsoft.UI.Colors.Gray
        };
        return new SolidColorBrush(color);
    }
}
