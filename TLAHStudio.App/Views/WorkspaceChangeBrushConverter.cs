using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

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
        return Application.Current.Resources.TryGetValue(key, out var brush) && brush is Brush typed
            ? typed
            : new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
