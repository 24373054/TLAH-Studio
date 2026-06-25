using TLAHStudio.Core.Helpers;
using TLAHStudio.Core.Models;
using TLAHStudio.Core.Services;

namespace TLAHStudio.Core.Tests;

public class WorkspaceServiceTests
{
    [Fact]
    public async Task WorkspaceService_CreatesProjectProfileTemplateAndAudit()
    {
        await using var db = TestDb.Create();
        var workspace = new WorkspaceService(db);

        var project = await workspace.SaveProjectAsync(new ProjectSpaceUpdateDto(
            Name: "Research",
            SharedPrompt: "Use project voice.",
            TeamNorms: "Cite assumptions."));
        var profile = await workspace.SaveConfigProfileAsync(new ConfigProfileUpdateDto(
            ProjectSpaceId: project.Id,
            Name: "DeepSeek",
            Provider: "anthropic",
            BaseUrl: "https://api.deepseek.com/anthropic",
            Model: "deepseek-v4-pro",
            Temperature: 0.2,
            MaxTokens: 2048,
            UserRole: "user",
            SystemPrompt: "Profile base prompt."));
        var template = await workspace.SavePromptTemplateAsync(new PromptTemplateUpdateDto(
            ProjectSpaceId: project.Id,
            Name: "Review",
            Category: "QA",
            Content: "Review this carefully."));

        var projects = await workspace.ListProjectsAsync();
        var profiles = await workspace.ListConfigProfilesAsync(project.Id);
        var templates = await workspace.ListPromptTemplatesAsync(project.Id);
        var audit = await workspace.ListAuditLogsAsync(project.Id);

        Assert.Contains(projects, p => p.Id == project.Id);
        Assert.Contains(profiles, p => p.Id == profile.Id);
        Assert.Contains(templates, t => t.Id == template.Id);
        Assert.True(audit.Count >= 3);
    }

    [Fact]
    public async Task SettingsService_UsesChatProfileBeforeGlobalSettings()
    {
        await using var db = TestDb.Create();
        var workspace = new WorkspaceService(db);
        var settings = new SettingsService(db);
        var chatService = new ChatService(db);
        var chat = await chatService.CreateChatAsync("Profile Chat");
        var project = await workspace.SaveProjectAsync(new ProjectSpaceUpdateDto(Name: "Team"));
        var profile = await workspace.SaveConfigProfileAsync(new ConfigProfileUpdateDto(
            ProjectSpaceId: project.Id,
            Name: "Profile",
            Provider: "anthropic",
            BaseUrl: "https://example.test",
            Model: "team-model",
            Temperature: 0.1,
            MaxTokens: 1234,
            UserRole: "operator"));

        await settings.UpdateGlobalSettingsAsync(new GlobalSettingsUpdateDto(ApiKey: "sk-global-secret"));
        await workspace.AssignChatAsync(chat.Id, project.Id, profile.Id);

        var effective = await settings.GetEffectiveSettingsAsync(chat.Id);

        Assert.Equal("anthropic", effective.Provider);
        Assert.Equal("https://example.test", effective.BaseUrl);
        Assert.Equal("team-model", effective.Model);
        Assert.Equal(0.1, effective.Temperature);
        Assert.Equal(1234, effective.MaxTokens);
        Assert.Equal("operator", effective.UserRole);
    }

    [Fact]
    public async Task SystemPromptBuilder_AppendsProjectPromptNormsAndProfilePrompt()
    {
        await using var db = TestDb.Create();
        var workspace = new WorkspaceService(db);
        var chatService = new ChatService(db);
        var chat = await chatService.CreateChatAsync("Prompt Chat");
        var project = await workspace.SaveProjectAsync(new ProjectSpaceUpdateDto(
            Name: "Team",
            SharedPrompt: "Shared voice.",
            TeamNorms: "Team rule."));
        var profile = await workspace.SaveConfigProfileAsync(new ConfigProfileUpdateDto(
            ProjectSpaceId: project.Id,
            Name: "Profile",
            SystemPrompt: "Profile prompt."));
        await workspace.AssignChatAsync(chat.Id, project.Id, profile.Id);

        var prompt = await SystemPromptBuilder.BuildAsync(
            db.Chats,
            db.GlobalSettings,
            db.AgentFiles,
            db.ProjectSpaces,
            db.ConfigProfiles,
            chat.Id);

        Assert.Contains("Profile prompt.", prompt);
        Assert.Contains("Shared voice.", prompt);
        Assert.Contains("Team rule.", prompt);
    }

    [Fact]
    public async Task WorkspaceExport_DoesNotIncludeProfileApiKey()
    {
        if (!OperatingSystem.IsWindows())
            return;

        await using var db = TestDb.Create();
        var workspace = new WorkspaceService(db);
        var project = await workspace.SaveProjectAsync(new ProjectSpaceUpdateDto(Name: "Sync"));
        await workspace.SaveConfigProfileAsync(new ConfigProfileUpdateDto(
            ProjectSpaceId: project.Id,
            Name: "Secret Profile",
            ApiKey: "sk-workspace-secret-123456"));

        var json = await workspace.ExportProjectAsync(project.Id);

        Assert.DoesNotContain("sk-workspace-secret", json);
        Assert.Contains("\"ApiKey\": null", json);
    }
}
