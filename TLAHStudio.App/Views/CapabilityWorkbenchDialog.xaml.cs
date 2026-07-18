using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using TLAHStudio.App.ViewModels;
using Windows.Storage;
using Windows.System;

namespace TLAHStudio.App.Views;

public sealed partial class CapabilityWorkbenchDialog : ContentDialog
{
    private CancellationTokenSource? _operationCancellation;
    private FrameworkElement? _hostContent;
    private string _selectedPage = "research";
    private bool _operationActive;

    public CapabilityWorkbenchDialog()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        Closing += OnClosing;
        ShowPage("research");
    }

    public void PrepareForHost(double availableWidth, double availableHeight = 0) =>
        ApplyResponsiveLayout(availableWidth, availableHeight);

    public void SelectPage(string page) =>
        ShowPage(page is "research" or "spreadsheet" or "document" or "diagram" or "quality"
            ? page
            : "research");

    private CapabilityWorkbenchViewModel ViewModel =>
        (CapabilityWorkbenchViewModel)DataContext;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _hostContent = App.MainWindow?.Content as FrameworkElement;
        if (_hostContent != null)
        {
            _hostContent.SizeChanged += HostContent_SizeChanged;
            _hostContent.ActualThemeChanged += HostContent_ActualThemeChanged;
            RequestedTheme = _hostContent.ActualTheme;
            ApplyResponsiveLayout(_hostContent.ActualWidth, _hostContent.ActualHeight);
        }

        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        UpdateBusyState();
        UpdateResultState();
        await RunOperationAsync(ViewModel.LoadAsync);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _operationCancellation?.Cancel();
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        if (_hostContent != null)
        {
            _hostContent.SizeChanged -= HostContent_SizeChanged;
            _hostContent.ActualThemeChanged -= HostContent_ActualThemeChanged;
        }
        _hostContent = null;
    }

    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (!ViewModel.IsBusy && !_operationActive)
            return;

        args.Cancel = true;
        _operationCancellation?.Cancel();
        ViewModel.StatusMessage = "Cancelling the current operation…";
    }

    private void HostContent_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyResponsiveLayout(e.NewSize.Width, e.NewSize.Height);

    private void HostContent_ActualThemeChanged(FrameworkElement sender, object args) =>
        RequestedTheme = sender.ActualTheme;

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CapabilityWorkbenchViewModel.IsBusy))
            DispatcherQueue.TryEnqueue(UpdateBusyState);
        if (e.PropertyName is nameof(CapabilityWorkbenchViewModel.ResultSummary)
            or nameof(CapabilityWorkbenchViewModel.ResultPreview)
            or nameof(CapabilityWorkbenchViewModel.PrimaryResultPath)
            or nameof(CapabilityWorkbenchViewModel.StatusMessage))
            DispatcherQueue.TryEnqueue(UpdateResultState);
    }

    private void Navigation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string page })
            ShowPage(page);
    }

    private void ShowPage(string page)
    {
        _selectedPage = page;
        ResearchPanel.Visibility = page == "research" ? Visibility.Visible : Visibility.Collapsed;
        SpreadsheetPanel.Visibility = page == "spreadsheet" ? Visibility.Visible : Visibility.Collapsed;
        DocumentPanel.Visibility = page == "document" ? Visibility.Visible : Visibility.Collapsed;
        DiagramPanel.Visibility = page == "diagram" ? Visibility.Visible : Visibility.Collapsed;
        QualityPanel.Visibility = page == "quality" ? Visibility.Visible : Visibility.Collapsed;
        CapabilityScrollViewer.ChangeView(null, 0, null, disableAnimation: false);

        SetSelected(ResearchNavButton, page == "research");
        SetSelected(SpreadsheetNavButton, page == "spreadsheet");
        SetSelected(DocumentNavButton, page == "document");
        SetSelected(DiagramNavButton, page == "diagram");
        SetSelected(QualityNavButton, page == "quality");
    }

    private void SetSelected(Button button, bool selected)
    {
        button.Style = (Style)Resources[
            selected
                ? "WorkbenchSelectedNavButtonStyle"
                : "WorkbenchNavButtonStyle"];
        AutomationProperties.SetItemStatus(button, selected ? "Selected" : "Not selected");
    }

    private async void RunResearch_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync(ViewModel.RunResearchAsync);

    private async void CreateSpreadsheet_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync(ViewModel.CreateSpreadsheetAsync);

    private async void CreateDocument_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync(ViewModel.CreateDocumentAsync);

    private async void CreateDiagram_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync(ViewModel.CreateDiagramAsync);

    private async void RefreshQuality_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync(ViewModel.RefreshToolQualityAsync);

    private async Task RunOperationAsync(Func<CancellationToken, Task> operation)
    {
        if (_operationActive)
            return;

        _operationActive = true;
        var operationCancellation = new CancellationTokenSource();
        _operationCancellation = operationCancellation;
        UpdateBusyState();
        try
        {
            await operation(operationCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            ViewModel.StatusMessage = "Operation cancelled. No unfinished result was published.";
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = $"Could not complete this operation: {ex.Message}";
            ViewModel.ResultPreview =
                "Check the active workspace and input, then try again. Existing files were left unchanged.";
        }
        finally
        {
            if (ReferenceEquals(_operationCancellation, operationCancellation))
                _operationCancellation = null;
            operationCancellation.Dispose();
            _operationActive = false;
            UpdateBusyState();
            UpdateResultState();
        }
    }

    private void CancelOperation_Click(object sender, RoutedEventArgs e) =>
        _operationCancellation?.Cancel();

    private async void OpenResult_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ViewModel.PrimaryResultPath) ||
            !File.Exists(ViewModel.PrimaryResultPath))
        {
            ViewModel.StatusMessage = "The result file is no longer available at its saved location.";
            return;
        }

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(ViewModel.PrimaryResultPath);
            if (!await Launcher.LaunchFileAsync(file))
                ViewModel.StatusMessage = "Windows could not find an application for this file type.";
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = $"Could not open the result: {ex.Message}";
        }
    }

    private async void OpenResultFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = ViewModel.PrimaryResultPath;
        var folder = string.IsNullOrWhiteSpace(path) ? null : Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            ViewModel.StatusMessage = "The result folder is no longer available.";
            return;
        }

        try
        {
            if (!await Launcher.LaunchFolderPathAsync(folder))
                ViewModel.StatusMessage = "Windows could not open the result folder.";
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = $"Could not open the result folder: {ex.Message}";
        }
    }

    private void UpdateBusyState()
    {
        var busy = ViewModel.IsBusy || _operationActive;
        WorkbenchBody.IsHitTestVisible = !busy;
        WorkbenchBody.Opacity = busy ? 0.72 : 1;
        WorkbenchProgress.IsActive = busy;
        WorkbenchProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelOperationButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy)
            CancelOperationButton.Focus(FocusState.Programmatic);
        RunResearchButton.IsEnabled = !busy;
        CreateSpreadsheetButton.IsEnabled = !busy;
        CreateDocumentButton.IsEnabled = !busy;
        CreateDiagramButton.IsEnabled = !busy;
    }

    private void UpdateResultState()
    {
        var hasResult =
            !string.IsNullOrWhiteSpace(ViewModel.ResultSummary) ||
            !string.IsNullOrWhiteSpace(ViewModel.ResultPreview) ||
            ViewModel.ResultArtifacts.Count > 0;
        ResultSurface.Visibility = hasResult ? Visibility.Visible : Visibility.Collapsed;
        OpenResultButton.IsEnabled =
            !string.IsNullOrWhiteSpace(ViewModel.PrimaryResultPath) &&
            File.Exists(ViewModel.PrimaryResultPath);
        OpenResultFolderButton.IsEnabled =
            !string.IsNullOrWhiteSpace(ViewModel.PrimaryResultPath) &&
            Directory.Exists(Path.GetDirectoryName(ViewModel.PrimaryResultPath));
        _ = LoadResultImageAsync();
        if (hasResult && !ViewModel.IsBusy)
        {
            DispatcherQueue.TryEnqueue(() =>
                CapabilityScrollViewer.ChangeView(
                    null,
                    CapabilityScrollViewer.ScrollableHeight,
                    null,
                    disableAnimation: false));
        }
    }

    private async Task LoadResultImageAsync()
    {
        var path = ViewModel.ResultArtifacts
            .Select(artifact => artifact.FullPath)
            .FirstOrDefault(candidate =>
                candidate.EndsWith(".png", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(candidate));
        if (string.IsNullOrWhiteSpace(path))
        {
            ResultImage.Source = null;
            ResultImageSurface.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            await using var stream = await file.OpenStreamForReadAsync();
            var image = new BitmapImage();
            await image.SetSourceAsync(stream.AsRandomAccessStream());
            if (!ViewModel.ResultArtifacts.Any(artifact =>
                    string.Equals(artifact.FullPath, path, StringComparison.OrdinalIgnoreCase)))
                return;
            ResultImage.Source = image;
            ResultImageSurface.Visibility = Visibility.Visible;
        }
        catch
        {
            ResultImage.Source = null;
            ResultImageSurface.Visibility = Visibility.Collapsed;
        }
    }

    private void ApplyResponsiveLayout(double availableWidth, double availableHeight)
    {
        if (availableWidth <= 0)
            return;

        var horizontalInset = availableWidth < 760 ? 24 : 96;
        var width = Math.Clamp(availableWidth - horizontalInset, 260, 1060);
        var height = availableHeight > 0
            ? Math.Clamp(
                availableHeight - Math.Clamp(availableHeight * 0.22, 96, 190),
                170,
                720)
            : 640;
        WorkbenchRoot.Width = width;
        WorkbenchRoot.Height = height;
        WorkbenchHeader.Visibility =
            availableHeight > 0 && availableHeight < 520
                ? Visibility.Collapsed
                : Visibility.Visible;
        var compact = width < 820;

        if (compact)
        {
            WorkbenchTopRow.Height = GridLength.Auto;
            WorkbenchBottomRow.Height = new GridLength(1, GridUnitType.Star);
            WorkbenchBody.ColumnSpacing = 0;
            WorkbenchBody.RowSpacing = 10;
            WorkbenchNavColumn.Width = new GridLength(1, GridUnitType.Star);
            WorkbenchContentColumn.Width = new GridLength(0);
            Grid.SetRow(WorkbenchNavSurface, 0);
            Grid.SetColumn(WorkbenchNavSurface, 0);
            Grid.SetColumnSpan(WorkbenchNavSurface, 2);
            Grid.SetRow(WorkbenchContent, 1);
            Grid.SetColumn(WorkbenchContent, 0);
            Grid.SetColumnSpan(WorkbenchContent, 2);
            WorkbenchNavStack.Orientation = Orientation.Horizontal;
            foreach (var button in WorkbenchNavStack.Children.OfType<Button>())
            {
                button.HorizontalAlignment = HorizontalAlignment.Stretch;
                button.HorizontalContentAlignment = HorizontalAlignment.Center;
                button.Padding = new Thickness(9, 8, 9, 8);
            }

            Grid.SetRow(WorkspaceBadge, 1);
            Grid.SetColumn(WorkspaceBadge, 0);
            Grid.SetColumnSpan(WorkspaceBadge, 2);
            WorkspaceBadge.Margin = new Thickness(0, 9, 0, 0);
            WorkspaceBadge.HorizontalAlignment = HorizontalAlignment.Stretch;
            ApplyCompactFieldLayout();
        }
        else
        {
            WorkbenchTopRow.Height = new GridLength(1, GridUnitType.Star);
            WorkbenchBottomRow.Height = new GridLength(0);
            WorkbenchBody.ColumnSpacing = 14;
            WorkbenchBody.RowSpacing = 0;
            WorkbenchNavColumn.Width = new GridLength(198);
            WorkbenchContentColumn.Width = new GridLength(1, GridUnitType.Star);
            Grid.SetRow(WorkbenchNavSurface, 0);
            Grid.SetColumn(WorkbenchNavSurface, 0);
            Grid.SetColumnSpan(WorkbenchNavSurface, 1);
            Grid.SetRow(WorkbenchContent, 0);
            Grid.SetColumn(WorkbenchContent, 1);
            Grid.SetColumnSpan(WorkbenchContent, 1);
            WorkbenchNavStack.Orientation = Orientation.Vertical;
            foreach (var button in WorkbenchNavStack.Children.OfType<Button>())
            {
                button.HorizontalAlignment = HorizontalAlignment.Stretch;
                button.HorizontalContentAlignment = HorizontalAlignment.Left;
                button.Padding = new Thickness(12, 10, 12, 10);
            }

            Grid.SetRow(WorkspaceBadge, 0);
            Grid.SetColumn(WorkspaceBadge, 1);
            Grid.SetColumnSpan(WorkspaceBadge, 1);
            WorkspaceBadge.Margin = new Thickness(0);
            WorkspaceBadge.HorizontalAlignment = HorizontalAlignment.Right;
            ApplyWideFieldLayout();
        }

        ShowPage(_selectedPage);
    }

    private void ApplyCompactFieldLayout()
    {
        PlaceField(ResearchDepthField, 0, 3);
        PlaceField(ResearchRecencyField, 1, 3);
        PlaceField(ResearchLanguageField, 2, 3);
        PlaceField(ResearchAllowedDomainsField, 0, 2);
        PlaceField(ResearchBlockedDomainsField, 1, 2);
        PlaceField(SpreadsheetTitleField, 0, 3);
        PlaceField(SpreadsheetFileNameField, 1, 3);
        PlaceField(SpreadsheetSheetNameField, 2, 3);
        PlaceField(DocumentTitleField, 0, 3);
        PlaceField(DocumentFileNameField, 1, 3);
        PlaceField(DocumentFormatField, 2, 3);
        PlaceField(DiagramTitleField, 0, 4);
        PlaceField(DiagramFileNameField, 1, 4);
        PlaceField(DiagramTypeField, 2, 4);
        PlaceField(DiagramThemeField, 3, 4);
    }

    private void ApplyWideFieldLayout()
    {
        PlaceField(ResearchDepthField, 0, 1, 0);
        PlaceField(ResearchRecencyField, 0, 1, 1);
        PlaceField(ResearchLanguageField, 0, 1, 2);
        PlaceField(ResearchAllowedDomainsField, 0, 1, 0);
        PlaceField(ResearchBlockedDomainsField, 0, 1, 1);
        PlaceField(SpreadsheetTitleField, 0, 1, 0);
        PlaceField(SpreadsheetFileNameField, 0, 1, 1);
        PlaceField(SpreadsheetSheetNameField, 0, 1, 2);
        PlaceField(DocumentTitleField, 0, 1, 0);
        PlaceField(DocumentFileNameField, 0, 1, 1);
        PlaceField(DocumentFormatField, 0, 1, 2);
        PlaceField(DiagramTitleField, 0, 1, 0);
        PlaceField(DiagramFileNameField, 0, 1, 1);
        PlaceField(DiagramTypeField, 0, 1, 2);
        PlaceField(DiagramThemeField, 0, 1, 3);
    }

    private static void PlaceField(
        FrameworkElement field,
        int row,
        int columnSpan,
        int column = 0)
    {
        Grid.SetRow(field, row);
        Grid.SetColumn(field, column);
        Grid.SetColumnSpan(field, columnSpan);
    }
}
