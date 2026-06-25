using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TLAHStudio.App.ViewModels;
using TLAHStudio.Core.Services;
using Windows.Storage.Pickers;

namespace TLAHStudio.App.Views;

public sealed partial class TeamWorkspaceDialog : ContentDialog
{
    private TeamWorkspaceViewModel? _vm;
    private bool _loaded;
    private bool _syncing;
    private string _currentTab = "project";
    private FrameworkElement? _hostContent;

    public TeamWorkspaceDialog()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public void PrepareForHost(double availableWidth) =>
        ApplyResponsiveLayout(availableWidth);

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
            return;

        _loaded = true;
        AttachResponsiveHost();
        _vm = DataContext as TeamWorkspaceViewModel;
        if (_vm == null)
            return;

        await _vm.LoadAsync();
        ProjectCombo.ItemsSource = _vm.Projects;
        ProfileList.ItemsSource = _vm.ConfigProfiles;
        TemplateList.ItemsSource = _vm.PromptTemplates;
        AuditList.ItemsSource = _vm.AuditLogs;
        DefaultProfileCombo.ItemsSource = _vm.ConfigProfiles;
        ProjectCombo.SelectedItem = _vm.SelectedProject;
        FillProject();
        FillProfile();
        FillTemplate();
        UpdateStatus();
        ShowTab("project");
    }

    private async void ProjectCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm == null || _syncing)
            return;

        _vm.SelectedProject = ProjectCombo.SelectedItem as ProjectSpaceDto;
        await _vm.LoadProjectChildrenAsync();
        DefaultProfileCombo.ItemsSource = _vm.ConfigProfiles;
        ProfileList.ItemsSource = _vm.ConfigProfiles;
        TemplateList.ItemsSource = _vm.PromptTemplates;
        AuditList.ItemsSource = _vm.AuditLogs;
        FillProject();
        FillProfile();
        FillTemplate();
        UpdateStatus();
    }

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tab })
            ShowTab(tab);
    }

    private void NewProject_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null)
            return;

        _vm.SelectedProject = null;
        ProjectCombo.SelectedItem = null;
        ProjectNameBox.Text = "New Project";
        ProjectDescriptionBox.Text = string.Empty;
        SharedPromptBox.Text = string.Empty;
        TeamNormsBox.Text = string.Empty;
        CloudSyncCheck.IsChecked = false;
        SyncFolderBox.Text = string.Empty;
        DefaultProfileCombo.SelectedItem = null;
        UpdateStatus("Draft workspace. Save it when ready.");
    }

    private async void SaveProject_Click(object sender, RoutedEventArgs e) =>
        await SaveProjectAsync();

    private async Task SaveProjectAsync()
    {
        if (_vm == null)
            return;

        var defaultProfile = DefaultProfileCombo.SelectedItem as ConfigProfileDto;
        await _vm.SaveProjectAsync(new ProjectSpaceUpdateDto(
            Id: _vm.SelectedProject?.Id,
            Name: ProjectNameBox.Text,
            Description: ProjectDescriptionBox.Text,
            SharedPrompt: SharedPromptBox.Text,
            TeamNorms: TeamNormsBox.Text,
            CloudSyncEnabled: CloudSyncCheck.IsChecked == true,
            SyncFolderPath: SyncFolderBox.Text,
            DefaultConfigProfileId: defaultProfile?.Id));
        RebindProjectLists();
        FillProject();
        UpdateStatus();
    }

    private async void AssignChat_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null)
            return;

        var profile = DefaultProfileCombo.SelectedItem as ConfigProfileDto;
        await _vm.AssignCurrentChatAsync(profile?.Id);
        UpdateStatus();
    }

    private void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm == null || _syncing)
            return;

        _vm.SelectedProfile = ProfileList.SelectedItem as ConfigProfileDto;
        FillProfile();
    }

    private void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null)
            return;

        _vm.SelectedProfile = null;
        ProfileList.SelectedItem = null;
        ProfileNameBox.Text = "New Profile";
        ProfileProviderBox.Text = "openai";
        ProfileApiKeyBox.Password = string.Empty;
        ProfileBaseUrlBox.Text = "https://api.openai.com";
        ProfileModelBox.Text = "gpt-4o";
        ProfileTempBox.Value = 0.7;
        ProfileMaxTokensBox.Value = 4096;
        ProfileUserRoleBox.Text = "user";
        ProfileSystemPromptBox.Text = string.Empty;
        ProfileSharedCheck.IsChecked = true;
        UpdateStatus("Draft profile. Save it when ready.");
    }

    private async void SaveProfile_Click(object sender, RoutedEventArgs e) =>
        await SaveProfileAsync();

    private async Task SaveProfileAsync()
    {
        if (_vm == null)
            return;

        await _vm.SaveProfileAsync(new ConfigProfileUpdateDto(
            Id: _vm.SelectedProfile?.Id,
            ProjectSpaceId: _vm.SelectedProject?.Id,
            Name: ProfileNameBox.Text,
            Provider: ProfileProviderBox.Text,
            ApiKey: ProfileApiKeyBox.Password,
            BaseUrl: ProfileBaseUrlBox.Text,
            Model: ProfileModelBox.Text,
            Temperature: double.IsNaN(ProfileTempBox.Value) ? 0.7 : ProfileTempBox.Value,
            MaxTokens: double.IsNaN(ProfileMaxTokensBox.Value) ? 4096 : (int)ProfileMaxTokensBox.Value,
            UserRole: ProfileUserRoleBox.Text,
            SystemPrompt: ProfileSystemPromptBox.Text,
            IsShared: ProfileSharedCheck.IsChecked == true));
        RebindProjectLists();
        FillProfile();
        UpdateStatus();
    }

    private async void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null)
            return;

        await _vm.DeleteSelectedProfileAsync();
        RebindProjectLists();
        FillProfile();
        UpdateStatus();
    }

    private void TemplateList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm == null || _syncing)
            return;

        _vm.SelectedTemplate = TemplateList.SelectedItem as PromptTemplateDto;
        FillTemplate();
    }

    private void NewTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null)
            return;

        _vm.SelectedTemplate = null;
        TemplateList.SelectedItem = null;
        TemplateNameBox.Text = "New Template";
        TemplateCategoryBox.Text = "General";
        TemplateContentBox.Text = string.Empty;
        TemplateSharedCheck.IsChecked = true;
        UpdateStatus("Draft template. Save it when ready.");
    }

    private async void SaveTemplate_Click(object sender, RoutedEventArgs e) =>
        await SaveTemplateAsync();

    private async Task SaveTemplateAsync()
    {
        if (_vm == null)
            return;

        await _vm.SaveTemplateAsync(new PromptTemplateUpdateDto(
            Id: _vm.SelectedTemplate?.Id,
            ProjectSpaceId: _vm.SelectedProject?.Id,
            Name: TemplateNameBox.Text,
            Category: TemplateCategoryBox.Text,
            Content: TemplateContentBox.Text,
            IsShared: TemplateSharedCheck.IsChecked == true));
        RebindProjectLists();
        FillTemplate();
        UpdateStatus();
    }

    private async void PrimaryButton_Click(
        ContentDialog sender,
        ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        var deferral = args.GetDeferral();
        try
        {
            switch (_currentTab)
            {
                case "profiles":
                    await SaveProfileAsync();
                    break;
                case "templates":
                    await SaveTemplateAsync();
                    break;
                case "sync":
                case "project":
                    await SaveProjectAsync();
                    break;
            }
        }
        catch (Exception ex)
        {
            UpdateStatus(ex.Message);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void DeleteTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null)
            return;

        await _vm.DeleteSelectedTemplateAsync();
        RebindProjectLists();
        FillTemplate();
        UpdateStatus();
    }

    private void InsertTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is not MainWindow window || string.IsNullOrWhiteSpace(TemplateContentBox.Text))
            return;

        var current = window.ChatVM.InputText;
        window.ChatVM.InputText = string.IsNullOrWhiteSpace(current)
            ? TemplateContentBox.Text
            : current.TrimEnd() + Environment.NewLine + Environment.NewLine + TemplateContentBox.Text;
        window.FocusMessageInput();
        UpdateStatus("Template inserted into the current message.");
    }

    private async void RefreshAudit_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null)
            return;

        await _vm.RefreshAuditAsync();
        AuditList.ItemsSource = _vm.AuditLogs;
        UpdateStatus("Audit log refreshed.");
    }

    private async void ExportWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null || App.MainWindow is not MainWindow window)
            return;

        var picker = new FileSavePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.SuggestedFileName = SafeFileName($"{_vm.SelectedProject?.Name ?? "workspace"}-{DateTime.Now:yyyyMMdd-HHmmss}");
        picker.FileTypeChoices.Add("TLAH Workspace", new List<string> { ".json" });
        var file = await picker.PickSaveFileAsync();
        if (file == null)
            return;

        await Windows.Storage.FileIO.WriteTextAsync(file, await _vm.ExportSelectedProjectAsync());
        UpdateStatus("Workspace package exported.");
    }

    private async void ImportWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null || App.MainWindow is not MainWindow window)
            return;

        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add(".json");
        var file = await picker.PickSingleFileAsync();
        if (file == null)
            return;

        await _vm.ImportProjectAsync(await Windows.Storage.FileIO.ReadTextAsync(file));
        RebindProjectLists();
        FillProject();
        FillProfile();
        FillTemplate();
        UpdateStatus();
    }

    private void FillProject()
    {
        if (_vm == null)
            return;

        var project = _vm.SelectedProject;
        ProjectNameBox.Text = project?.Name ?? string.Empty;
        ProjectDescriptionBox.Text = project?.Description ?? string.Empty;
        SharedPromptBox.Text = project?.SharedPrompt ?? string.Empty;
        TeamNormsBox.Text = project?.TeamNorms ?? string.Empty;
        CloudSyncCheck.IsChecked = project?.CloudSyncEnabled ?? false;
        SyncFolderBox.Text = project?.SyncFolderPath ?? string.Empty;
        DefaultProfileCombo.SelectedItem = _vm.ConfigProfiles.FirstOrDefault(p => p.Id == project?.DefaultConfigProfileId);
        ProjectStatsText.Text = project == null
            ? "No workspace selected."
            : $"{project.ChatCount} chats · {project.ConfigProfileCount} profiles · {project.PromptTemplateCount} templates";
    }

    private void FillProfile()
    {
        if (_vm == null)
            return;

        _syncing = true;
        ProfileList.SelectedItem = _vm.SelectedProfile;
        _syncing = false;

        var profile = _vm.SelectedProfile;
        ProfileNameBox.Text = profile?.Name ?? string.Empty;
        ProfileProviderBox.Text = profile?.Provider ?? "openai";
        ProfileApiKeyBox.Password = profile?.ApiKey ?? string.Empty;
        ProfileBaseUrlBox.Text = profile?.BaseUrl ?? "https://api.openai.com";
        ProfileModelBox.Text = profile?.Model ?? "gpt-4o";
        ProfileTempBox.Value = profile?.Temperature ?? 0.7;
        ProfileMaxTokensBox.Value = profile?.MaxTokens ?? 4096;
        ProfileUserRoleBox.Text = profile?.UserRole ?? "user";
        ProfileSystemPromptBox.Text = profile?.SystemPrompt ?? string.Empty;
        ProfileSharedCheck.IsChecked = profile?.IsShared ?? true;
    }

    private void FillTemplate()
    {
        if (_vm == null)
            return;

        _syncing = true;
        TemplateList.SelectedItem = _vm.SelectedTemplate;
        _syncing = false;

        var template = _vm.SelectedTemplate;
        TemplateNameBox.Text = template?.Name ?? string.Empty;
        TemplateCategoryBox.Text = template?.Category ?? "General";
        TemplateContentBox.Text = template?.Content ?? string.Empty;
        TemplateSharedCheck.IsChecked = template?.IsShared ?? true;
    }

    private void RebindProjectLists()
    {
        if (_vm == null)
            return;

        _syncing = true;
        ProjectCombo.ItemsSource = _vm.Projects;
        ProjectCombo.SelectedItem = _vm.SelectedProject;
        ProfileList.ItemsSource = _vm.ConfigProfiles;
        TemplateList.ItemsSource = _vm.PromptTemplates;
        AuditList.ItemsSource = _vm.AuditLogs;
        DefaultProfileCombo.ItemsSource = _vm.ConfigProfiles;
        _syncing = false;
    }

    private void UpdateStatus(string? message = null)
    {
        if (_vm != null && message == null)
            message = _vm.StatusMessage;
        StatusText.Text = message ?? string.Empty;
    }

    private void ShowTab(string tab)
    {
        _currentTab = tab;
        ProjectPanel.Visibility = tab == "project" ? Visibility.Visible : Visibility.Collapsed;
        ProfilesPanel.Visibility = tab == "profiles" ? Visibility.Visible : Visibility.Collapsed;
        TemplatesPanel.Visibility = tab == "templates" ? Visibility.Visible : Visibility.Collapsed;
        AuditPanel.Visibility = tab == "audit" ? Visibility.Visible : Visibility.Collapsed;
        SyncPanel.Visibility = tab == "sync" ? Visibility.Visible : Visibility.Collapsed;

        SetTab(ProjectTabButton, tab == "project");
        SetTab(ProfilesTabButton, tab == "profiles");
        SetTab(TemplatesTabButton, tab == "templates");
        SetTab(AuditTabButton, tab == "audit");
        SetTab(SyncTabButton, tab == "sync");

        IsPrimaryButtonEnabled = tab != "audit";
        PrimaryButtonText = tab switch
        {
            "profiles" => "Save profile",
            "templates" => "Save template",
            "sync" => "Save settings",
            "audit" => "Save",
            _ => "Save project"
        };
    }

    private static void SetTab(Button button, bool selected)
    {
        button.Background = selected ? Brush("AccentSoftBrush") : TransparentBrush();
        button.Foreground = selected ? Brush("AccentBrush") : Brush("TextSecondaryBrush");
        button.FontWeight = selected
            ? Microsoft.UI.Text.FontWeights.SemiBold
            : Microsoft.UI.Text.FontWeights.Normal;
    }

    private static Brush Brush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
            ? brush
            : TransparentBrush();

    private static SolidColorBrush TransparentBrush() =>
        new(Microsoft.UI.Colors.Transparent);

    private void AttachResponsiveHost()
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
        WorkspaceRoot.Width = targetWidth;
        var compact = targetWidth < 820;

        WorkspaceBodyGrid.ColumnSpacing = compact ? 0 : 14;
        WorkspaceBodyGrid.RowSpacing = compact ? 12 : 0;
        WorkspaceRailColumn.Width = compact
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(228);
        WorkspaceDetailColumn.Width = compact
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);

        Grid.SetRow(ProjectRail, 0);
        Grid.SetColumn(ProjectRail, 0);
        Grid.SetColumnSpan(ProjectRail, compact ? 2 : 1);
        ProjectRail.Height = compact ? 150 : double.NaN;

        Grid.SetRow(WorkspaceDetail, compact ? 1 : 0);
        Grid.SetColumn(WorkspaceDetail, compact ? 0 : 1);
        Grid.SetColumnSpan(WorkspaceDetail, compact ? 2 : 1);

        ApplyEditorLayout(
            compact,
            ProfilesListColumn,
            ProfileEditorColumn,
            ProfilesListSection,
            ProfileEditorSection,
            ProfileList);
        ApplyEditorLayout(
            compact,
            TemplatesListColumn,
            TemplateEditorColumn,
            TemplatesListSection,
            TemplateEditorSection,
            TemplateList);

        AuditList.Height = compact ? 400 : 500;
    }

    private static void ApplyEditorLayout(
        bool compact,
        ColumnDefinition listColumn,
        ColumnDefinition editorColumn,
        FrameworkElement listSection,
        FrameworkElement editorSection,
        ListView list)
    {
        listColumn.Width = compact ? new GridLength(1, GridUnitType.Star) : new GridLength(230);
        editorColumn.Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetRow(listSection, 0);
        Grid.SetColumn(listSection, 0);
        Grid.SetColumnSpan(listSection, compact ? 2 : 1);
        Grid.SetRow(editorSection, compact ? 1 : 0);
        Grid.SetColumn(editorSection, compact ? 0 : 1);
        Grid.SetColumnSpan(editorSection, compact ? 2 : 1);
        list.Height = compact ? 180 : 430;
    }

    private static string SafeFileName(string value)
    {
        foreach (var ch in Path.GetInvalidFileNameChars())
            value = value.Replace(ch, '-');
        return value;
    }
}
