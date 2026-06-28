using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TLAHStudio.App.ViewModels;

namespace TLAHStudio.App.Views;

public sealed partial class ToolPlatformDialog : ContentDialog
{
    private FrameworkElement? _hostContent;

    public ToolPlatformDialog()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public void PrepareForHost(double availableWidth) =>
        ApplyResponsiveLayout(availableWidth);

    private ToolPlatformViewModel ViewModel =>
        (ToolPlatformViewModel)DataContext;

    private async void SaveSettings_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(ViewModel.SaveSettingsAsync);

    private async void PrimaryButton_Click(
        ContentDialog sender,
        ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        var deferral = args.GetDeferral();
        try
        {
            await ViewModel.SaveAllAsync();
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = ex.Message;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void NewMcp_Click(object sender, RoutedEventArgs e) =>
        ViewModel.NewMcpServer();

    private async void SaveMcp_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(ViewModel.SaveMcpServerAsync);

    private async void TestMcp_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(ViewModel.TestMcpServerAsync);

    private void ApplyMcpExample_Click(object sender, RoutedEventArgs e) =>
        ViewModel.ApplySelectedMcpExample();

    private async void DeleteMcp_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(ViewModel.DeleteMcpServerAsync);

    private void NewCredential_Click(object sender, RoutedEventArgs e)
    {
        CredentialSecretBox.Password = string.Empty;
        ViewModel.NewCredential();
    }

    private async void SaveCredential_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(ViewModel.SaveCredentialAsync);
        CredentialSecretBox.Password = string.Empty;
    }

    private async void DeleteCredential_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(ViewModel.DeleteCredentialAsync);
        CredentialSecretBox.Password = string.Empty;
    }

    private void CredentialSecretBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        ViewModel.CredentialSecret = CredentialSecretBox.Password;
    }

    private async void DeletePolicy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Guid id })
            await RunAsync(ct => ViewModel.DeletePolicyAsync(id, ct));
    }

    private async void SavePolicyRule_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(ViewModel.SavePolicyRuleAsync);

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        try
        {
            await action(CancellationToken.None);
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = ex.Message;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _hostContent = App.MainWindow?.Content as FrameworkElement;
        if (_hostContent == null)
            return;
        _hostContent.SizeChanged += HostContent_SizeChanged;
        ApplyResponsiveLayout(_hostContent.ActualWidth);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_hostContent != null)
            _hostContent.SizeChanged -= HostContent_SizeChanged;
        _hostContent = null;
    }

    private void HostContent_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyResponsiveLayout(e.NewSize.Width);

    private void ApplyResponsiveLayout(double availableWidth)
    {
        if (availableWidth <= 0)
            return;

        var targetWidth = Math.Clamp(availableWidth - 96, 520, 1120);
        ToolRoot.Width = targetWidth;
        var compact = targetWidth < 820;

        ApplyThreeFieldLayout(compact);
        ApplyTwoFieldLayout(
            compact,
            BackendOptionsGrid,
            WslColumn,
            DockerColumn,
            WslPanel,
            DockerPanel);
        ApplyEditorLayout(
            compact,
            McpLayoutGrid,
            McpListColumn,
            McpEditorColumn,
            McpServerList,
            McpEditorPanel,
            250);
        ApplyEditorLayout(
            compact,
            CredentialLayoutGrid,
            CredentialListColumn,
            CredentialEditorColumn,
            CredentialList,
            CredentialEditorPanel,
            200);
    }

    private void ApplyThreeFieldLayout(bool compact)
    {
        RuntimeColumn.Width = new GridLength(1, GridUnitType.Star);
        OutputColumn.Width = new GridLength(1, GridUnitType.Star);
        FileSizeColumn.Width = compact
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);

        Grid.SetRow(RuntimePanel, 0);
        Grid.SetColumn(RuntimePanel, 0);
        Grid.SetRow(OutputPanel, 0);
        Grid.SetColumn(OutputPanel, 1);
        Grid.SetRow(FileSizePanel, compact ? 1 : 0);
        Grid.SetColumn(FileSizePanel, compact ? 0 : 2);
        Grid.SetColumnSpan(FileSizePanel, compact ? 2 : 1);
    }

    private static void ApplyTwoFieldLayout(
        bool compact,
        Grid grid,
        ColumnDefinition firstColumn,
        ColumnDefinition secondColumn,
        FrameworkElement first,
        FrameworkElement second)
    {
        grid.ColumnSpacing = compact ? 0 : 12;
        firstColumn.Width = new GridLength(1, GridUnitType.Star);
        secondColumn.Width = compact
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        Grid.SetRow(first, 0);
        Grid.SetColumn(first, 0);
        Grid.SetRow(second, compact ? 1 : 0);
        Grid.SetColumn(second, compact ? 0 : 1);
        Grid.SetColumnSpan(second, compact ? 2 : 1);
    }

    private static void ApplyEditorLayout(
        bool compact,
        Grid grid,
        ColumnDefinition listColumn,
        ColumnDefinition editorColumn,
        FrameworkElement list,
        FrameworkElement editor,
        double compactListHeight)
    {
        grid.ColumnSpacing = compact ? 0 : 14;
        listColumn.Width = compact
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(250);
        editorColumn.Width = compact
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        Grid.SetRow(list, 0);
        Grid.SetColumn(list, 0);
        Grid.SetColumnSpan(list, compact ? 2 : 1);
        Grid.SetRow(editor, compact ? 1 : 0);
        Grid.SetColumn(editor, compact ? 0 : 1);
        Grid.SetColumnSpan(editor, compact ? 2 : 1);
        list.Height = compact ? compactListHeight : double.NaN;
    }
}
