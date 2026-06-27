using TLAHStudio.Core.Models;

namespace TLAHStudio.Core.Services;

public interface IWorkspaceService
{
    Task<ProjectSpaceDto> EnsureDefaultProjectAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ProjectSpaceDto>> ListProjectsAsync(CancellationToken ct = default);
    Task<ProjectSpaceDto?> GetProjectAsync(Guid projectId, CancellationToken ct = default);
    Task<ProjectSpaceDto> SaveProjectAsync(ProjectSpaceUpdateDto update, CancellationToken ct = default);
    Task DeleteProjectAsync(Guid projectId, CancellationToken ct = default);

    Task<ChatWorkspaceDto?> GetChatWorkspaceAsync(Guid chatId, CancellationToken ct = default);
    Task AssignChatAsync(Guid chatId, Guid? projectId, Guid? configProfileId, CancellationToken ct = default);

    Task<IReadOnlyList<ConfigProfileDto>> ListConfigProfilesAsync(Guid? projectId = null, CancellationToken ct = default);
    Task<ConfigProfileDto?> GetConfigProfileAsync(Guid profileId, CancellationToken ct = default);
    Task<ConfigProfileDto> SaveConfigProfileAsync(ConfigProfileUpdateDto update, CancellationToken ct = default);
    Task DeleteConfigProfileAsync(Guid profileId, CancellationToken ct = default);

    Task<IReadOnlyList<PromptTemplateDto>> ListPromptTemplatesAsync(Guid? projectId = null, CancellationToken ct = default);
    Task<PromptTemplateDto?> GetPromptTemplateAsync(Guid templateId, CancellationToken ct = default);
    Task<PromptTemplateDto> SavePromptTemplateAsync(PromptTemplateUpdateDto update, CancellationToken ct = default);
    Task DeletePromptTemplateAsync(Guid templateId, CancellationToken ct = default);

    Task<IReadOnlyList<AuditLogDto>> ListAuditLogsAsync(Guid? projectId = null, int take = 200, CancellationToken ct = default);
    Task LogAsync(string eventType, string entityType, string entityId, string summary, Guid? projectId = null, Guid? chatId = null, object? metadata = null, CancellationToken ct = default);

    Task<string> ExportProjectAsync(Guid projectId, CancellationToken ct = default);
    Task<ProjectSpaceDto> ImportProjectAsync(string json, CancellationToken ct = default);
}

public record ProjectSpaceDto(
    Guid Id,
    string Name,
    string Description,
    string SharedPrompt,
    string TeamNorms,
    bool CloudSyncEnabled,
    string? SyncFolderPath,
    Guid? DefaultConfigProfileId,
    int ChatCount,
    int ConfigProfileCount,
    int PromptTemplateCount,
    DateTime UpdatedAt);

public record ProjectSpaceUpdateDto(
    Guid? Id = null,
    string? Name = null,
    string? Description = null,
    string? SharedPrompt = null,
    string? TeamNorms = null,
    bool? CloudSyncEnabled = null,
    string? SyncFolderPath = null,
    Guid? DefaultConfigProfileId = null);

public record ChatWorkspaceDto(
    Guid ChatId,
    Guid? ProjectSpaceId,
    string? ProjectName,
    Guid? ConfigProfileId,
    string? ConfigProfileName);

public record ConfigProfileDto(
    Guid Id,
    Guid? ProjectSpaceId,
    string Name,
    string Provider,
    string? ApiKey,
    string BaseUrl,
    string Model,
    bool UseLongContext,
    string ThinkingDepth,
    double Temperature,
    int MaxTokens,
    string UserRole,
    string SystemPrompt,
    bool IsShared,
    DateTime UpdatedAt);

public record ConfigProfileUpdateDto(
    Guid? Id = null,
    Guid? ProjectSpaceId = null,
    string? Name = null,
    string? Provider = null,
    string? ApiKey = null,
    string? BaseUrl = null,
    string? Model = null,
    bool? UseLongContext = null,
    string? ThinkingDepth = null,
    double? Temperature = null,
    int? MaxTokens = null,
    string? UserRole = null,
    string? SystemPrompt = null,
    bool? IsShared = null);

public record PromptTemplateDto(
    Guid Id,
    Guid? ProjectSpaceId,
    string Name,
    string Category,
    string Content,
    bool IsShared,
    DateTime UpdatedAt);

public record PromptTemplateUpdateDto(
    Guid? Id = null,
    Guid? ProjectSpaceId = null,
    string? Name = null,
    string? Category = null,
    string? Content = null,
    bool? IsShared = null);

public record AuditLogDto(
    Guid Id,
    Guid? ProjectSpaceId,
    Guid? ChatId,
    string EventType,
    string EntityType,
    string EntityId,
    string Summary,
    string MetadataJson,
    DateTime CreatedAt);
