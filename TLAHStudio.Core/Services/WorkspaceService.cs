using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TLAHStudio.Core.Helpers;
using TLAHStudio.Core.Models;

#pragma warning disable CA1416 // DPAPI is intentionally Windows-only in the Windows desktop client.

namespace TLAHStudio.Core.Services;

public class WorkspaceService : IWorkspaceService
{
    private readonly DbContext _db;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public WorkspaceService(DbContext db)
    {
        _db = db;
    }

    public async Task<ProjectSpaceDto> EnsureDefaultProjectAsync(CancellationToken ct = default)
    {
        var existing = await _db.Set<ProjectSpace>()
            .OrderBy(p => p.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (existing != null)
            return await ToProjectDtoAsync(existing, ct);

        var project = new ProjectSpace
        {
            Name = "Personal Workspace",
            Description = "Local workspace for chats, profiles, templates, and team notes."
        };
        _db.Set<ProjectSpace>().Add(project);
        await _db.SaveChangesAsync(ct);
        await LogAsync("create", "project", project.Id.ToString("D"), $"Created project \"{project.Name}\".", project.Id, metadata: new { project.Name }, ct: ct);
        return await ToProjectDtoAsync(project, ct);
    }

    public async Task<IReadOnlyList<ProjectSpaceDto>> ListProjectsAsync(CancellationToken ct = default)
    {
        await EnsureDefaultProjectAsync(ct);
        var projects = await _db.Set<ProjectSpace>()
            .OrderBy(p => p.Name)
            .ToListAsync(ct);

        var result = new List<ProjectSpaceDto>();
        foreach (var project in projects)
            result.Add(await ToProjectDtoAsync(project, ct));
        return result;
    }

    public async Task<ProjectSpaceDto?> GetProjectAsync(Guid projectId, CancellationToken ct = default)
    {
        var project = await _db.Set<ProjectSpace>().FirstOrDefaultAsync(p => p.Id == projectId, ct);
        return project == null ? null : await ToProjectDtoAsync(project, ct);
    }

    public async Task<ProjectSpaceDto> SaveProjectAsync(ProjectSpaceUpdateDto update, CancellationToken ct = default)
    {
        ProjectSpace project;
        var isNew = update.Id == null || update.Id == Guid.Empty;
        if (isNew)
        {
            project = new ProjectSpace();
            _db.Set<ProjectSpace>().Add(project);
        }
        else
        {
            var projectId = update.Id.GetValueOrDefault();
            project = await _db.Set<ProjectSpace>().FirstOrDefaultAsync(p => p.Id == projectId, ct)
                ?? throw new InvalidOperationException($"Project not found: {update.Id}");
        }

        if (update.Name != null) project.Name = NormalizeName(update.Name, "Project");
        if (update.Description != null) project.Description = update.Description.Trim();
        if (update.SharedPrompt != null) project.SharedPrompt = update.SharedPrompt.Trim();
        if (update.TeamNorms != null) project.TeamNorms = update.TeamNorms.Trim();
        if (update.CloudSyncEnabled.HasValue) project.CloudSyncEnabled = update.CloudSyncEnabled.Value;
        if (update.SyncFolderPath != null) project.SyncFolderPath = string.IsNullOrWhiteSpace(update.SyncFolderPath) ? null : update.SyncFolderPath.Trim();
        project.DefaultConfigProfileId = update.DefaultConfigProfileId;
        project.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        await LogAsync(isNew ? "create" : "update", "project", project.Id.ToString("D"), $"{(isNew ? "Created" : "Updated")} project \"{project.Name}\".", project.Id, metadata: update, ct: ct);
        return await ToProjectDtoAsync(project, ct);
    }

    public async Task DeleteProjectAsync(Guid projectId, CancellationToken ct = default)
    {
        var project = await _db.Set<ProjectSpace>().FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw new InvalidOperationException($"Project not found: {projectId}");

        var chats = await _db.Set<Chat>().Where(c => c.ProjectSpaceId == projectId).ToListAsync(ct);
        foreach (var chat in chats)
        {
            chat.ProjectSpaceId = null;
            chat.ConfigProfileId = null;
            chat.UpdatedAt = DateTime.UtcNow;
        }

        var profiles = await _db.Set<ConfigProfile>().Where(p => p.ProjectSpaceId == projectId).ToListAsync(ct);
        var templates = await _db.Set<PromptTemplate>().Where(t => t.ProjectSpaceId == projectId).ToListAsync(ct);
        _db.Set<ConfigProfile>().RemoveRange(profiles);
        _db.Set<PromptTemplate>().RemoveRange(templates);
        _db.Set<ProjectSpace>().Remove(project);
        await _db.SaveChangesAsync(ct);
        await LogAsync("delete", "project", projectId.ToString("D"), $"Deleted project \"{project.Name}\".", metadata: new { project.Name }, ct: ct);
    }

    public async Task<ChatWorkspaceDto?> GetChatWorkspaceAsync(Guid chatId, CancellationToken ct = default)
    {
        var chat = await _db.Set<Chat>()
            .Include(c => c.ProjectSpace)
            .Include(c => c.ConfigProfile)
            .FirstOrDefaultAsync(c => c.Id == chatId, ct);
        if (chat == null)
            return null;

        return new ChatWorkspaceDto(
            chat.Id,
            chat.ProjectSpaceId,
            chat.ProjectSpace?.Name,
            chat.ConfigProfileId,
            chat.ConfigProfile?.Name);
    }

    public async Task AssignChatAsync(Guid chatId, Guid? projectId, Guid? configProfileId, CancellationToken ct = default)
    {
        var chat = await _db.Set<Chat>().FirstOrDefaultAsync(c => c.Id == chatId, ct)
            ?? throw new InvalidOperationException($"Chat not found: {chatId}");

        if (projectId != null && !await _db.Set<ProjectSpace>().AnyAsync(p => p.Id == projectId.Value, ct))
            throw new InvalidOperationException($"Project not found: {projectId}");

        if (configProfileId != null && !await _db.Set<ConfigProfile>().AnyAsync(p => p.Id == configProfileId.Value, ct))
            throw new InvalidOperationException($"Config profile not found: {configProfileId}");

        chat.ProjectSpaceId = projectId;
        chat.ConfigProfileId = configProfileId;
        chat.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await LogAsync("assign", "chat", chat.Id.ToString("D"), $"Assigned chat \"{chat.Title}\" to workspace settings.", projectId, chat.Id, new { projectId, configProfileId }, ct);
    }

    public async Task<IReadOnlyList<ConfigProfileDto>> ListConfigProfilesAsync(Guid? projectId = null, CancellationToken ct = default)
    {
        var query = _db.Set<ConfigProfile>().AsQueryable();
        if (projectId != null)
            query = query.Where(p => p.ProjectSpaceId == projectId);

        var profiles = await query.OrderBy(p => p.Name).ToListAsync(ct);
        var result = new List<ConfigProfileDto>();
        foreach (var profile in profiles)
            result.Add(ToConfigProfileDto(profile));
        return result;
    }

    public async Task<ConfigProfileDto?> GetConfigProfileAsync(Guid profileId, CancellationToken ct = default)
    {
        var profile = await _db.Set<ConfigProfile>().FirstOrDefaultAsync(p => p.Id == profileId, ct);
        return profile == null ? null : ToConfigProfileDto(profile);
    }

    public async Task<ConfigProfileDto> SaveConfigProfileAsync(ConfigProfileUpdateDto update, CancellationToken ct = default)
    {
        ConfigProfile profile;
        var isNew = update.Id == null || update.Id == Guid.Empty;
        if (isNew)
        {
            profile = new ConfigProfile();
            _db.Set<ConfigProfile>().Add(profile);
        }
        else
        {
            var profileId = update.Id.GetValueOrDefault();
            profile = await _db.Set<ConfigProfile>().FirstOrDefaultAsync(p => p.Id == profileId, ct)
                ?? throw new InvalidOperationException($"Config profile not found: {update.Id}");
        }

        profile.ProjectSpaceId = update.ProjectSpaceId;
        if (update.Name != null) profile.Name = NormalizeName(update.Name, "Profile");
        if (update.Provider != null) profile.Provider = update.Provider.Trim();
        if (update.ApiKey != null && !ApiKeyMasker.IsMasked(update.ApiKey))
            profile.ApiKey = string.IsNullOrWhiteSpace(update.ApiKey) ? null : ProtectedSecret.Protect(update.ApiKey.Trim());
        if (update.BaseUrl != null) profile.BaseUrl = update.BaseUrl.Trim();
        if (update.Model != null) profile.Model = update.Model.Trim();
        if (update.Temperature.HasValue) profile.Temperature = update.Temperature.Value;
        if (update.MaxTokens.HasValue) profile.MaxTokens = update.MaxTokens.Value;
        if (update.UserRole != null) profile.UserRole = update.UserRole.Trim();
        if (update.SystemPrompt != null) profile.SystemPrompt = update.SystemPrompt.Trim();
        if (update.IsShared.HasValue) profile.IsShared = update.IsShared.Value;
        profile.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        await LogAsync(isNew ? "create" : "update", "profile", profile.Id.ToString("D"), $"{(isNew ? "Created" : "Updated")} profile \"{profile.Name}\".", profile.ProjectSpaceId, metadata: new { profile.Name, profile.Provider, profile.Model }, ct: ct);
        return ToConfigProfileDto(profile);
    }

    public async Task DeleteConfigProfileAsync(Guid profileId, CancellationToken ct = default)
    {
        var profile = await _db.Set<ConfigProfile>().FirstOrDefaultAsync(p => p.Id == profileId, ct)
            ?? throw new InvalidOperationException($"Config profile not found: {profileId}");

        var chats = await _db.Set<Chat>().Where(c => c.ConfigProfileId == profileId).ToListAsync(ct);
        foreach (var chat in chats)
            chat.ConfigProfileId = null;

        var projects = await _db.Set<ProjectSpace>().Where(p => p.DefaultConfigProfileId == profileId).ToListAsync(ct);
        foreach (var project in projects)
            project.DefaultConfigProfileId = null;

        _db.Set<ConfigProfile>().Remove(profile);
        await _db.SaveChangesAsync(ct);
        await LogAsync("delete", "profile", profileId.ToString("D"), $"Deleted profile \"{profile.Name}\".", profile.ProjectSpaceId, metadata: new { profile.Name }, ct: ct);
    }

    public async Task<IReadOnlyList<PromptTemplateDto>> ListPromptTemplatesAsync(Guid? projectId = null, CancellationToken ct = default)
    {
        var query = _db.Set<PromptTemplate>().AsQueryable();
        if (projectId != null)
            query = query.Where(t => t.ProjectSpaceId == projectId);

        return await query
            .OrderBy(t => t.Category)
            .ThenBy(t => t.Name)
            .Select(t => new PromptTemplateDto(t.Id, t.ProjectSpaceId, t.Name, t.Category, t.Content, t.IsShared, t.UpdatedAt))
            .ToListAsync(ct);
    }

    public async Task<PromptTemplateDto?> GetPromptTemplateAsync(Guid templateId, CancellationToken ct = default)
    {
        var t = await _db.Set<PromptTemplate>().FirstOrDefaultAsync(t => t.Id == templateId, ct);
        return t == null ? null : new PromptTemplateDto(t.Id, t.ProjectSpaceId, t.Name, t.Category, t.Content, t.IsShared, t.UpdatedAt);
    }

    public async Task<PromptTemplateDto> SavePromptTemplateAsync(PromptTemplateUpdateDto update, CancellationToken ct = default)
    {
        PromptTemplate template;
        var isNew = update.Id == null || update.Id == Guid.Empty;
        if (isNew)
        {
            template = new PromptTemplate();
            _db.Set<PromptTemplate>().Add(template);
        }
        else
        {
            var templateId = update.Id.GetValueOrDefault();
            template = await _db.Set<PromptTemplate>().FirstOrDefaultAsync(t => t.Id == templateId, ct)
                ?? throw new InvalidOperationException($"Prompt template not found: {update.Id}");
        }

        template.ProjectSpaceId = update.ProjectSpaceId;
        if (update.Name != null) template.Name = NormalizeName(update.Name, "Template");
        if (update.Category != null) template.Category = NormalizeName(update.Category, "General");
        if (update.Content != null) template.Content = update.Content.Trim();
        if (update.IsShared.HasValue) template.IsShared = update.IsShared.Value;
        template.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        await LogAsync(isNew ? "create" : "update", "template", template.Id.ToString("D"), $"{(isNew ? "Created" : "Updated")} template \"{template.Name}\".", template.ProjectSpaceId, metadata: new { template.Name, template.Category }, ct: ct);
        return new PromptTemplateDto(template.Id, template.ProjectSpaceId, template.Name, template.Category, template.Content, template.IsShared, template.UpdatedAt);
    }

    public async Task DeletePromptTemplateAsync(Guid templateId, CancellationToken ct = default)
    {
        var template = await _db.Set<PromptTemplate>().FirstOrDefaultAsync(t => t.Id == templateId, ct)
            ?? throw new InvalidOperationException($"Prompt template not found: {templateId}");

        _db.Set<PromptTemplate>().Remove(template);
        await _db.SaveChangesAsync(ct);
        await LogAsync("delete", "template", templateId.ToString("D"), $"Deleted template \"{template.Name}\".", template.ProjectSpaceId, metadata: new { template.Name }, ct: ct);
    }

    public async Task<IReadOnlyList<AuditLogDto>> ListAuditLogsAsync(Guid? projectId = null, int take = 200, CancellationToken ct = default)
    {
        var query = _db.Set<AuditLogEntry>().AsQueryable();
        if (projectId != null)
            query = query.Where(a => a.ProjectSpaceId == projectId);

        return await query
            .OrderByDescending(a => a.CreatedAt)
            .Take(Math.Clamp(take, 1, 1000))
            .Select(a => new AuditLogDto(a.Id, a.ProjectSpaceId, a.ChatId, a.EventType, a.EntityType, a.EntityId, a.Summary, a.MetadataJson, a.CreatedAt))
            .ToListAsync(ct);
    }

    public async Task LogAsync(string eventType, string entityType, string entityId, string summary, Guid? projectId = null, Guid? chatId = null, object? metadata = null, CancellationToken ct = default)
    {
        var metadataJson = metadata == null
            ? "{}"
            : SecretRedactor.RedactJson(JsonSerializer.Serialize(metadata, JsonOptions));

        _db.Set<AuditLogEntry>().Add(new AuditLogEntry
        {
            ProjectSpaceId = projectId,
            ChatId = chatId,
            EventType = eventType,
            EntityType = entityType,
            EntityId = entityId,
            Summary = summary,
            MetadataJson = metadataJson
        });
        await _db.SaveChangesAsync(ct);
    }

    public async Task<string> ExportProjectAsync(Guid projectId, CancellationToken ct = default)
    {
        var project = await _db.Set<ProjectSpace>().FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw new InvalidOperationException($"Project not found: {projectId}");
        var profiles = await _db.Set<ConfigProfile>().Where(p => p.ProjectSpaceId == projectId).OrderBy(p => p.Name).ToListAsync(ct);
        var templates = await _db.Set<PromptTemplate>().Where(t => t.ProjectSpaceId == projectId).OrderBy(t => t.Name).ToListAsync(ct);

        var payload = new
        {
            Schema = "tlah-project-workspace-v1",
            ExportedAt = DateTime.UtcNow,
            Project = new
            {
                project.Name,
                project.Description,
                project.SharedPrompt,
                project.TeamNorms,
                project.CloudSyncEnabled,
                project.SyncFolderPath
            },
            ConfigProfiles = profiles.Select(p => new
            {
                p.Name,
                p.Provider,
                ApiKey = (string?)null,
                p.BaseUrl,
                p.Model,
                p.Temperature,
                p.MaxTokens,
                p.UserRole,
                p.SystemPrompt,
                p.IsShared
            }),
            PromptTemplates = templates.Select(t => new
            {
                t.Name,
                t.Category,
                t.Content,
                t.IsShared
            })
        };

        await LogAsync("export", "project", project.Id.ToString("D"), $"Exported project \"{project.Name}\".", project.Id, metadata: new { project.Name }, ct: ct);
        return SecretRedactor.RedactJson(JsonSerializer.Serialize(payload, JsonOptions));
    }

    public async Task<ProjectSpaceDto> ImportProjectAsync(string json, CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var projectElement = root.GetProperty("Project");
        var project = new ProjectSpace
        {
            Name = ReadString(projectElement, "Name") ?? "Imported Workspace",
            Description = ReadString(projectElement, "Description") ?? string.Empty,
            SharedPrompt = ReadString(projectElement, "SharedPrompt") ?? string.Empty,
            TeamNorms = ReadString(projectElement, "TeamNorms") ?? string.Empty,
            CloudSyncEnabled = ReadBool(projectElement, "CloudSyncEnabled") ?? false,
            SyncFolderPath = ReadString(projectElement, "SyncFolderPath")
        };
        _db.Set<ProjectSpace>().Add(project);
        await _db.SaveChangesAsync(ct);

        if (root.TryGetProperty("ConfigProfiles", out var profiles) && profiles.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in profiles.EnumerateArray())
            {
                _db.Set<ConfigProfile>().Add(new ConfigProfile
                {
                    ProjectSpaceId = project.Id,
                    Name = ReadString(item, "Name") ?? "Imported Profile",
                    Provider = ReadString(item, "Provider") ?? "openai",
                    BaseUrl = ReadString(item, "BaseUrl") ?? "https://api.openai.com",
                    Model = ReadString(item, "Model") ?? "gpt-4o",
                    Temperature = ReadDouble(item, "Temperature") ?? 0.7,
                    MaxTokens = ReadInt(item, "MaxTokens") ?? 4096,
                    UserRole = ReadString(item, "UserRole") ?? "user",
                    SystemPrompt = ReadString(item, "SystemPrompt") ?? string.Empty,
                    IsShared = ReadBool(item, "IsShared") ?? true
                });
            }
        }

        if (root.TryGetProperty("PromptTemplates", out var templates) && templates.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in templates.EnumerateArray())
            {
                _db.Set<PromptTemplate>().Add(new PromptTemplate
                {
                    ProjectSpaceId = project.Id,
                    Name = ReadString(item, "Name") ?? "Imported Template",
                    Category = ReadString(item, "Category") ?? "General",
                    Content = ReadString(item, "Content") ?? string.Empty,
                    IsShared = ReadBool(item, "IsShared") ?? true
                });
            }
        }

        await _db.SaveChangesAsync(ct);
        await LogAsync("import", "project", project.Id.ToString("D"), $"Imported project \"{project.Name}\".", project.Id, metadata: new { project.Name }, ct: ct);
        return await ToProjectDtoAsync(project, ct);
    }

    private async Task<ProjectSpaceDto> ToProjectDtoAsync(ProjectSpace project, CancellationToken ct)
    {
        var chatCount = await _db.Set<Chat>().CountAsync(c => c.ProjectSpaceId == project.Id && c.DeletedAt == null, ct);
        var profileCount = await _db.Set<ConfigProfile>().CountAsync(p => p.ProjectSpaceId == project.Id, ct);
        var templateCount = await _db.Set<PromptTemplate>().CountAsync(t => t.ProjectSpaceId == project.Id, ct);
        return new ProjectSpaceDto(
            project.Id,
            project.Name,
            project.Description,
            project.SharedPrompt,
            project.TeamNorms,
            project.CloudSyncEnabled,
            project.SyncFolderPath,
            project.DefaultConfigProfileId,
            chatCount,
            profileCount,
            templateCount,
            project.UpdatedAt);
    }

    private static ConfigProfileDto ToConfigProfileDto(ConfigProfile profile)
    {
        var apiKey = ProtectedSecret.Reveal(profile.ApiKey);
        return new ConfigProfileDto(
            profile.Id,
            profile.ProjectSpaceId,
            profile.Name,
            profile.Provider,
            string.IsNullOrWhiteSpace(apiKey) ? null : ApiKeyMasker.Mask(apiKey),
            profile.BaseUrl,
            profile.Model,
            profile.Temperature,
            profile.MaxTokens,
            profile.UserRole,
            profile.SystemPrompt,
            profile.IsShared,
            profile.UpdatedAt);
    }

    private static string NormalizeName(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string? ReadString(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? ReadBool(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static int? ReadInt(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var number)
            ? number
            : null;

    private static double? ReadDouble(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out var number)
            ? number
            : null;
}
