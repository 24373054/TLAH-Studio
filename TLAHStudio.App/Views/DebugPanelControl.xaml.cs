using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TLAHStudio.App.ViewModels;
using Windows.ApplicationModel.DataTransfer;

namespace TLAHStudio.App.Views;

public sealed partial class DebugPanelControl : UserControl
{
    private const double MinOpenWidth = 520;
    private const double MaxOpenWidth = 720;
    private const double OpenWidthRatio = 0.44;
    private DebugPanelViewModel? _vm;
    private string _tab = "request";
    private FrameworkElement? _hostElement;
    private bool _wrapText = true; // M4.7.0: word-wrap toggle

    public DebugPanelControl()
    {
        App.Log("DebugPanelControl ctor entered.");
        InitializeComponent();
        App.Log("DebugPanelControl XAML initialized.");
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += (_, _) =>
        {
            Load(CurrentJson());
            UpdateTabs();
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow?.Content is not FrameworkElement host || ReferenceEquals(_hostElement, host))
            return;

        if (_hostElement != null)
            _hostElement.SizeChanged -= Host_SizeChanged;
        _hostElement = host;
        _hostElement.SizeChanged += Host_SizeChanged;
        if (_vm?.IsOpen == true)
            ApplyOpenWidth();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_hostElement != null)
            _hostElement.SizeChanged -= Host_SizeChanged;
        _hostElement = null;
    }

    private void Host_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_vm?.IsOpen == true)
            ApplyOpenWidth();
    }

    public void Bind(DebugPanelViewModel vm)
    {
        if (_vm == vm) return;

        if (_vm != null)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.MetaItems.CollectionChanged -= OnMetaItemsChanged;
        }

        _vm = vm;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _vm.MetaItems.CollectionChanged += OnMetaItemsChanged;

        ApplyOpenState(_vm.IsOpen);
        _tab = _vm.SelectedTab;
        Load(CurrentJson());
        UpdateMetaSummary();
        UpdateTabs();
        UpdateStatus();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_vm == null) return;

            switch (args.PropertyName)
            {
                case nameof(DebugPanelViewModel.IsOpen):
                    ApplyOpenState(_vm.IsOpen);
                    break;
                case nameof(DebugPanelViewModel.SelectedTab):
                    _tab = _vm.SelectedTab;
                    Load(CurrentJson());
                    UpdateTabs();
                    break;
                case nameof(DebugPanelViewModel.RequestJson):
                    if (_tab == "request") Load(_vm.RequestJson);
                    break;
                case nameof(DebugPanelViewModel.ResponseJson):
                    if (_tab == "response") Load(_vm.ResponseJson);
                    break;
                case nameof(DebugPanelViewModel.ContextJson):
                case nameof(DebugPanelViewModel.TokensJson):
                case nameof(DebugPanelViewModel.TimingJson):
                case nameof(DebugPanelViewModel.ErrorsJson):
                case nameof(DebugPanelViewModel.HistoryJson):
                case nameof(DebugPanelViewModel.CompareJson):
                case nameof(DebugPanelViewModel.DiffJson):
                case nameof(DebugPanelViewModel.CostJson):
                case nameof(DebugPanelViewModel.ReplayJson):
                case nameof(DebugPanelViewModel.AbJson):
                case nameof(DebugPanelViewModel.ReplayStatus):
                    Load(CurrentJson());
                    break;
            }

            UpdateMetaSummary();
            UpdateStatus();
        });

    private void OnMetaItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        DispatcherQueue.TryEnqueue(UpdateMetaSummary);

    private void ApplyOpenState(bool isOpen)
    {
        if (isOpen)
        {
            Visibility = Visibility.Visible;
            ApplyOpenWidth();
        }
        else
        {
            Width = 0;
            MinWidth = 0;
            MaxWidth = 0;
            Visibility = Visibility.Collapsed;
        }
    }

    private void ApplyOpenWidth()
    {
        var hostWidth = _hostElement?.ActualWidth;
        if (hostWidth is null or <= 0)
            hostWidth = (App.MainWindow?.Content as FrameworkElement)?.ActualWidth;

        var width = Math.Clamp((hostWidth is > 0 ? hostWidth.Value : 1280) * OpenWidthRatio, MinOpenWidth, MaxOpenWidth);
        Width = width;
        MinWidth = width;
        MaxWidth = width;
    }

    private string CurrentJson() =>
        _vm == null ? string.Empty : _vm.CurrentPayload();

    private void Load(string json)
    {
        JsonStack.Children.Clear();
        if (string.IsNullOrWhiteSpace(json))
        {
            JsonScrollViewer.ChangeView(null, 0, null, true);
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonStack.Children.Add(BuildJsonNode(doc.RootElement, null, 0));
        }
        catch
        {
            JsonStack.Children.Add(CreateTextBlock(json));
        }

        JsonScrollViewer.ChangeView(null, 0, null, true);
    }

    private void UpdateMetaSummary()
    {
        if (_vm == null || _vm.MetaItems.Count == 0)
        {
            MetaSummaryText.Text = string.Empty;
            return;
        }

        MetaSummaryText.Text = string.Join("    ",
            _vm.MetaItems.Select(item => $"{item.Key}: {item.Value}"));
    }

    private FrameworkElement BuildJsonNode(JsonElement element, string? key, int depth)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => BuildContainer(element, key, depth, "{", "}", element.EnumerateObject().Count(), "keys"),
            JsonValueKind.Array => BuildContainer(element, key, depth, "[", "]", element.GetArrayLength(), "items"),
            _ => BuildPrimitive(element, key, depth)
        };
    }

    private FrameworkElement BuildContainer(
        JsonElement element,
        string? key,
        int depth,
        string open,
        string close,
        int count,
        string unit)
    {
        var children = new StackPanel
        {
            Spacing = 2,
            Margin = new Thickness(16, 3, 0, 5)
        };

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
                children.Children.Add(BuildJsonNode(property.Value, property.Name, depth + 1));
        }
        else
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
                children.Children.Add(BuildJsonNode(item, index++.ToString(), depth + 1));
        }

        var header = key == null
            ? $"{open} {count} {unit} {close}"
            : $"\"{key}\": {open} {count} {unit} {close}";

        var isExpanded = depth < 2;
        children.Visibility = isExpanded ? Visibility.Visible : Visibility.Collapsed;

        var glyph = new TextBlock
        {
            Text = isExpanded ? "-" : "+",
            Width = 18,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 13,
            Foreground = Brush("TextMutedBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };

        var headerRow = new Grid
        {
            Background = TransparentBrush(),
            Padding = new Thickness(10, 8, 10, 8),
            IsTapEnabled = true
        };
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerRow.Children.Add(glyph);

        var headerText = CreateTextBlock(header, muted: count == 0);
        Grid.SetColumn(headerText, 1);
        headerRow.Children.Add(headerText);

        headerRow.Tapped += (_, args) =>
        {
            args.Handled = true;
            children.Visibility = children.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
            glyph.Text = children.Visibility == Visibility.Visible ? "-" : "+";
        };
        headerRow.PointerEntered += (_, _) => headerRow.Background = Brush("AccentSoftBrush");
        headerRow.PointerExited += (_, _) => headerRow.Background = TransparentBrush();

        var panel = new StackPanel { Spacing = 0 };
        panel.Children.Add(headerRow);
        panel.Children.Add(children);

        return new Border
        {
            Background = depth == 0 ? Brush("InputBackgroundBrush") : Brush("SurfaceBrush"),
            BorderBrush = Brush("BorderSubtleBrush"),
            BorderThickness = new Thickness(depth <= 1 ? 1 : 0),
            CornerRadius = new CornerRadius(depth == 0 ? 8 : 5),
            Margin = new Thickness(0, depth == 0 ? 4 : 3, 0, 4),
            Child = panel
        };
    }

    private FrameworkElement BuildPrimitive(JsonElement element, string? key, int depth)
    {
        var prefix = key == null ? string.Empty : $"\"{key}\": ";
        var text = $"{prefix}{PrimitiveText(element)}";
        var block = CreateTextBlock(text, false, JsonBrushKey(element.ValueKind));

        return new Border
            {
                Background = depth == 0 ? Brush("InputBackgroundBrush") : TransparentBrush(),
                BorderBrush = Brush("BorderSubtleBrush"),
                BorderThickness = new Thickness(depth == 0 ? 1 : 0),
                CornerRadius = new CornerRadius(depth == 0 ? 8 : 0),
                Padding = new Thickness(depth == 0 ? 8 : 16, 4, 8, 4),
                Child = block
            };
    }

    private TextBlock CreateTextBlock(string text, bool muted = false) =>
        CreateTextBlock(text, muted, null);

    private TextBlock CreateTextBlock(string text, bool muted, string? brushKey) => new()
    {
        Text = text,
        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
        FontSize = 12,
        LineHeight = 18,
        TextWrapping = _wrapText ? TextWrapping.Wrap : TextWrapping.NoWrap,
        Foreground = brushKey != null
            ? Brush(brushKey)
            : muted ? Brush("TextMutedBrush") : Brush("TextPrimaryBrush")
    };

    private static string JsonBrushKey(JsonValueKind kind) => kind switch
    {
        JsonValueKind.String => "JsonStringBrush",
        JsonValueKind.Number => "JsonNumberBrush",
        JsonValueKind.True or JsonValueKind.False => "JsonBoolBrush",
        JsonValueKind.Null or JsonValueKind.Undefined => "JsonNullBrush",
        _ => "TextPrimaryBrush"
    };

    private static string PrimitiveText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => $"\"{EscapeString(element.GetString() ?? string.Empty)}\"",
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        JsonValueKind.Undefined => "undefined",
        _ => element.ToString()
    };

    private static string EscapeString(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);

    private void UpdateTabs()
    {
        SetTab(ReqTab, _tab == "request");
        SetTab(RespTab, _tab == "response");
        SetTab(ContextTab, _tab == "context");
        SetTab(TokensTab, _tab == "tokens");
        SetTab(TimingTab, _tab == "timing");
        SetTab(ErrorsTab, _tab == "errors");
        SetTab(HistoryTab, _tab == "history");
        SetTab(CompareTab, _tab == "compare");
        SetTab(DiffTab, _tab == "diff");
        SetTab(CostTab, _tab == "cost");
        SetTab(ReplayTab, _tab == "replay");
        SetTab(AbTab, _tab is "a/b" or "ab");
    }

    private void SetTab(Button button, bool selected)
    {
        button.Background = selected ? Brush("AccentSoftBrush") : TransparentBrush();
        button.Foreground = selected ? Brush("AccentBrush") : Brush("TextMutedBrush");
        button.BorderBrush = selected ? Brush("AccentBrush") : TransparentBrush();
        button.BorderThickness = selected ? new Thickness(1) : new Thickness(0);
        button.FontWeight = selected
            ? Microsoft.UI.Text.FontWeights.SemiBold
            : Microsoft.UI.Text.FontWeights.Normal;
    }

    private void UpdateStatus()
    {
        if (_vm == null)
        {
            SetStatus("Inspector is not ready yet.", false);
            return;
        }

        if (_vm.IsLoading)
        {
            SetStatus("Loading raw prompt data...", true);
            return;
        }

        if (!string.IsNullOrWhiteSpace(_vm.ErrorMessage))
        {
            SetStatus(_vm.ErrorMessage, false);
            return;
        }

        if (string.IsNullOrWhiteSpace(CurrentJson()))
        {
            SetStatus($"No {_tab} JSON was recorded for this turn.", false);
            return;
        }

        StatusLayer.Visibility = Visibility.Collapsed;
        StatusRing.IsActive = false;
        StatusRing.Visibility = Visibility.Collapsed;
    }

    private void SetStatus(string text, bool loading)
    {
        StatusText.Text = text;
        StatusLayer.Visibility = Visibility.Visible;
        StatusRing.IsActive = loading;
        StatusRing.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Close_Click(object sender, RoutedEventArgs e) =>
        _vm?.CloseDebugCommand.Execute(null);

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
            return;

        _vm?.SelectTabCommand.Execute(button.Content?.ToString()?.ToLowerInvariant() ?? "request");
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var json = CurrentJson();
        if (string.IsNullOrWhiteSpace(json)) return;

        var package = new DataPackage();
        package.SetText(json);
        Clipboard.SetContent(package);
    }

    private void CopyCurl_Click(object sender, RoutedEventArgs e) =>
        _vm?.CopyCurlCommand.Execute(null);

    private void CopyPrompt_Click(object sender, RoutedEventArgs e) =>
        _vm?.CopyPromptCommand.Execute(null);

    private void Replay_Click(object sender, RoutedEventArgs e) =>
        _vm?.ReplayTurnCommand.Execute(null);

    private void CopyAb_Click(object sender, RoutedEventArgs e) =>
        _vm?.CopyAbPackCommand.Execute(null);

    private void WrapToggle_Click(object sender, RoutedEventArgs e)
    {
        _wrapText = !_wrapText;
        WrapToggle.Background = _wrapText
            ? Brush("SurfaceElevatedBrush")
            : Brush("AccentSoftBrush");
        Load(CurrentJson());
    }

    private void ExportBundle_Click(object sender, RoutedEventArgs e) =>
        _vm?.ExportDebugBundleCommand.Execute(null);

    private Brush Brush(string key)
    {
        var light = IsLightTheme();
        return key switch
        {
            "InputBackgroundBrush" => ColorBrush(light, 0xFF, 0xFF, 0xFF, 0xFF, 0xFA, 0x0C, 0x12, 0x1A),
            "SurfaceBrush" => ColorBrush(light, 0xFF, 0xFF, 0xFF, 0xFF, 0xEB, 0x18, 0x22, 0x31),
            "BorderSubtleBrush" => ColorBrush(light, 0xFF, 0xD3, 0xDD, 0xE9, 0x52, 0x6F, 0x7D, 0x91),
            "TextPrimaryBrush" => ColorBrush(light, 0xFF, 0x16, 0x1D, 0x28, 0xFF, 0xFF, 0xFF, 0xFF),
            "TextMutedBrush" => ColorBrush(light, 0xFF, 0x84, 0x90, 0xA1, 0xFF, 0x9A, 0xA8, 0xBA),
            "AccentBrush" => ColorBrush(light, 0xFF, 0x25, 0x63, 0xEB, 0xFF, 0x6A, 0xA7, 0xFF),
            "AccentSoftBrush" => ColorBrush(light, 0xFF, 0xDD, 0xEB, 0xFF, 0x30, 0x6A, 0xA7, 0xFF),
            "JsonStringBrush" => ColorBrush(light, 0xFF, 0x16, 0x7A, 0x46, 0xFF, 0x79, 0xE2, 0xA7),
            "JsonNumberBrush" => ColorBrush(light, 0xFF, 0xB4, 0x53, 0x09, 0xFF, 0xF5, 0xB9, 0x5F),
            "JsonBoolBrush" => ColorBrush(light, 0xFF, 0x1D, 0x4E, 0x89, 0xFF, 0x9C, 0xC7, 0xFF),
            "JsonNullBrush" => ColorBrush(light, 0xFF, 0x7C, 0x3A, 0xED, 0xFF, 0xC4, 0xA7, 0xFF),
            _ => ColorBrush(light, 0xFF, 0x16, 0x1D, 0x28, 0xFF, 0xFF, 0xFF, 0xFF)
        };
    }

    private bool IsLightTheme()
    {
        if (ActualTheme == Microsoft.UI.Xaml.ElementTheme.Light) return true;
        if (ActualTheme == Microsoft.UI.Xaml.ElementTheme.Dark) return false;

        return App.MainWindow?.Content is FrameworkElement root &&
               root.ActualTheme == Microsoft.UI.Xaml.ElementTheme.Light;
    }

    private static SolidColorBrush ColorBrush(
        bool light,
        byte lightA,
        byte lightR,
        byte lightG,
        byte lightB,
        byte darkA,
        byte darkR,
        byte darkG,
        byte darkB)
    {
        var color = light
            ? Microsoft.UI.ColorHelper.FromArgb(lightA, lightR, lightG, lightB)
            : Microsoft.UI.ColorHelper.FromArgb(darkA, darkR, darkG, darkB);
        return new SolidColorBrush(color);
    }

    private static SolidColorBrush TransparentBrush() =>
        new(Microsoft.UI.Colors.Transparent);
}
