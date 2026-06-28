using TLAHStudio.Core.Models;

namespace TLAHStudio.Core.Services;

public sealed record ToolPlatformSettingsUpdate(
    string DefaultBackend,
    string NetworkAllowlist,
    int MaxRuntimeSeconds,
    int MaxOutputChars,
    int MaxFileBytes,
    int MaxMemoryMb,
    int MaxProcesses,
    string WslDistribution,
    string DockerImage,
    string RemoteEndpoint,
    string RemoteCredentialName);

public sealed record ToolPolicyEvaluation(
    string? Decision,
    string? Scope,
    Guid? RuleId = null,
    string? SubjectKind = null,
    string? Pattern = null,
    string? MatchedValue = null,
    string? Description = null)
{
    public bool IsAllowed => Decision == ToolPolicyDecisions.Allow;
    public bool IsDenied => Decision == ToolPolicyDecisions.Deny;
}

public sealed record ToolPolicyRuleUpdate(
    Guid? Id,
    string SubjectKind,
    string Pattern,
    string Scope,
    string Decision,
    string Description = "",
    Guid? ChatId = null,
    Guid? ProjectSpaceId = null);

public sealed record McpServerConfigDto(
    Guid Id,
    Guid? ProjectSpaceId,
    string Name,
    string Transport,
    string Command,
    string ArgumentsJson,
    string Endpoint,
    string HeadersJson,
    string EnvironmentJson,
    bool Enabled);

public sealed record CredentialEntryDto(
    Guid Id,
    string Name,
    string AllowedDomains,
    string AllowedTools,
    bool HasSecret);

public interface IToolPlatformService
{
    Task<ToolPlatformSettings> GetSettingsAsync(CancellationToken ct = default);
    Task<ToolPlatformSettings> UpdateSettingsAsync(
        ToolPlatformSettingsUpdate update,
        CancellationToken ct = default);

    Task<ToolPolicyEvaluation> EvaluatePolicyAsync(
        Guid chatId,
        string toolName,
        string argumentsJson = "{}",
        ToolSafetyAssessment? safety = null,
        CancellationToken ct = default);

    Task SavePolicyAsync(
        Guid chatId,
        string toolName,
        string scope,
        string decision,
        string? subjectKind = null,
        string? pattern = null,
        string? description = null,
        CancellationToken ct = default);

    Task<ToolPolicyRule> SavePolicyRuleAsync(
        ToolPolicyRuleUpdate update,
        CancellationToken ct = default);

    Task<IReadOnlyList<ToolPolicyRule>> ListPoliciesAsync(CancellationToken ct = default);
    Task DeletePolicyAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<McpServerConfigDto>> ListMcpServersAsync(
        Guid? projectSpaceId = null,
        CancellationToken ct = default);
    Task<McpServerConfigDto> SaveMcpServerAsync(
        McpServerConfigDto config,
        CancellationToken ct = default);
    Task DeleteMcpServerAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<CredentialEntryDto>> ListCredentialsAsync(CancellationToken ct = default);
    Task<CredentialEntryDto> SaveCredentialAsync(
        Guid? id,
        string name,
        string? secret,
        string allowedDomains,
        string allowedTools,
        CancellationToken ct = default);
    Task DeleteCredentialAsync(Guid id, CancellationToken ct = default);
    Task<string?> ResolveCredentialAsync(
        string name,
        string toolName,
        string domain,
        CancellationToken ct = default);
}
