using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TLAHStudio.Core.Services;

namespace TLAHStudio.App.ViewModels;

public partial class TeamWorkspaceViewModel : ObservableObject
{
    private readonly IWorkspaceService _workspaceService;
    private readonly IAppStateService _appState;

    public ObservableCollection<ProjectSpaceDto> Projects { get; } = new();
    public ObservableCollection<ConfigProfileDto> ConfigProfiles { get; } = new();
    public ObservableCollection<PromptTemplateDto> PromptTemplates { get; } = new();
    public ObservableCollection<AuditLogDto> AuditLogs { get; } = new();

    [ObservableProperty] private ProjectSpaceDto? _selectedProject;
    [ObservableProperty] private ConfigProfileDto? _selectedProfile;
    [ObservableProperty] private PromptTemplateDto? _selectedTemplate;
    [ObservableProperty] private ConfigProfileDto? _selectedChatProfile;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _statusMessage;

    public Guid? CurrentChatId => _appState.CurrentChatId;
    public bool HasCurrentChat => _appState.CurrentChatId != null;

    public TeamWorkspaceViewModel(IWorkspaceService workspaceService, IAppStateService appState)
    {
        _workspaceService = workspaceService;
        _appState = appState;
    }

    public async Task LoadAsync()
    {
        IsLoading = true;
        StatusMessage = null;
        try
        {
            await _workspaceService.EnsureDefaultProjectAsync();
            var projects = await _workspaceService.ListProjectsAsync();
            Replace(Projects, projects);

            if (_appState.CurrentChatId != null)
            {
                var chatWorkspace = await _workspaceService.GetChatWorkspaceAsync(_appState.CurrentChatId.Value);
                SelectedProject = Projects.FirstOrDefault(p => p.Id == chatWorkspace?.ProjectSpaceId)
                    ?? Projects.FirstOrDefault();
            }
            else
            {
                SelectedProject = Projects.FirstOrDefault();
            }

            await LoadProjectChildrenAsync();
            OnPropertyChanged(nameof(CurrentChatId));
            OnPropertyChanged(nameof(HasCurrentChat));
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task LoadProjectChildrenAsync()
    {
        var projectId = SelectedProject?.Id;
        Replace(ConfigProfiles, await _workspaceService.ListConfigProfilesAsync(projectId));
        Replace(PromptTemplates, await _workspaceService.ListPromptTemplatesAsync(projectId));
        Replace(AuditLogs, await _workspaceService.ListAuditLogsAsync(projectId));

        SelectedProfile = ConfigProfiles.FirstOrDefault();
        SelectedTemplate = PromptTemplates.FirstOrDefault();
        SelectedChatProfile = null;
        if (_appState.CurrentChatId != null)
        {
            var chatWorkspace = await _workspaceService.GetChatWorkspaceAsync(_appState.CurrentChatId.Value);
            SelectedChatProfile = ConfigProfiles.FirstOrDefault(p => p.Id == chatWorkspace?.ConfigProfileId)
                ?? ConfigProfiles.FirstOrDefault(p => p.Id == SelectedProject?.DefaultConfigProfileId);
        }
    }

    public async Task<ProjectSpaceDto> SaveProjectAsync(ProjectSpaceUpdateDto update)
    {
        var project = await _workspaceService.SaveProjectAsync(update);
        await LoadAsync();
        SelectedProject = Projects.FirstOrDefault(p => p.Id == project.Id);
        await LoadProjectChildrenAsync();
        StatusMessage = "Workspace saved.";
        return project;
    }

    public async Task DeleteSelectedProjectAsync()
    {
        if (SelectedProject == null)
            return;

        await _workspaceService.DeleteProjectAsync(SelectedProject.Id);
        await LoadAsync();
        StatusMessage = "Workspace deleted.";
    }

    public async Task AssignCurrentChatAsync(Guid? profileId)
    {
        if (_appState.CurrentChatId == null || SelectedProject == null)
            return;

        await _workspaceService.AssignChatAsync(_appState.CurrentChatId.Value, SelectedProject.Id, profileId);
        StatusMessage = "Current chat assigned to workspace.";
        await LoadProjectChildrenAsync();
    }

    public async Task<ConfigProfileDto> SaveProfileAsync(ConfigProfileUpdateDto update)
    {
        var profile = await _workspaceService.SaveConfigProfileAsync(update);
        await LoadProjectChildrenAsync();
        SelectedProfile = ConfigProfiles.FirstOrDefault(p => p.Id == profile.Id);
        StatusMessage = "Profile saved.";
        return profile;
    }

    public async Task DeleteSelectedProfileAsync()
    {
        if (SelectedProfile == null)
            return;

        await _workspaceService.DeleteConfigProfileAsync(SelectedProfile.Id);
        await LoadProjectChildrenAsync();
        StatusMessage = "Profile deleted.";
    }

    public async Task<PromptTemplateDto> SaveTemplateAsync(PromptTemplateUpdateDto update)
    {
        var template = await _workspaceService.SavePromptTemplateAsync(update);
        await LoadProjectChildrenAsync();
        SelectedTemplate = PromptTemplates.FirstOrDefault(t => t.Id == template.Id);
        StatusMessage = "Template saved.";
        return template;
    }

    public async Task DeleteSelectedTemplateAsync()
    {
        if (SelectedTemplate == null)
            return;

        await _workspaceService.DeletePromptTemplateAsync(SelectedTemplate.Id);
        await LoadProjectChildrenAsync();
        StatusMessage = "Template deleted.";
    }

    public Task<string> ExportSelectedProjectAsync()
    {
        if (SelectedProject == null)
            throw new InvalidOperationException("Select a workspace first.");

        return _workspaceService.ExportProjectAsync(SelectedProject.Id);
    }

    public async Task ImportProjectAsync(string json)
    {
        var project = await _workspaceService.ImportProjectAsync(json);
        await LoadAsync();
        SelectedProject = Projects.FirstOrDefault(p => p.Id == project.Id);
        await LoadProjectChildrenAsync();
        StatusMessage = "Workspace imported.";
    }

    public async Task RefreshAuditAsync()
    {
        Replace(AuditLogs, await _workspaceService.ListAuditLogsAsync(SelectedProject?.Id));
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values)
            target.Add(value);
    }
}
