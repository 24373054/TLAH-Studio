using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using TLAHStudio.Core.Helpers;
using TLAHStudio.Core.Models;

#pragma warning disable CA1416

namespace TLAHStudio.Core.Services;

public sealed class ToolPlatformService : IToolPlatformService
{
    private readonly DbContext _db;

    public ToolPlatformService(DbContext db)
    {
        _db = db;
    }

    public async Task<ToolPlatformSettings> GetSettingsAsync(CancellationToken ct = default)
    {
        var settings = await _db.Set<ToolPlatformSettings>().FirstOrDefaultAsync(s => s.Id == 1, ct);
        if (settings != null)
            return settings;

        settings = new ToolPlatformSettings();
        _db.Set<ToolPlatformSettings>().Add(settings);
        await _db.SaveChangesAsync(ct);
        return settings;
    }

    public async Task<ToolPlatformSettings> UpdateSettingsAsync(
        ToolPlatformSettingsUpdate update,
        CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(ct);
        settings.DefaultBackend = IsBackend(update.DefaultBackend)
            ? update.DefaultBackend
            : ToolExecutionBackends.RestrictedLocal;
        settings.NetworkAllowlist = NormalizeLines(update.NetworkAllowlist);
        settings.MaxRuntimeSeconds = Math.Clamp(update.MaxRuntimeSeconds, 1, 600);
        settings.MaxOutputChars = Math.Clamp(update.MaxOutputChars, 1000, 200000);
        settings.MaxFileBytes = Math.Clamp(update.MaxFileBytes, 1024, 100 * 1024 * 1024);
        settings.MaxMemoryMb = Math.Clamp(update.MaxMemoryMb, 128, 8192);
        settings.MaxProcesses = Math.Clamp(update.MaxProcesses, 1, 64);
        settings.WslDistribution = update.WslDistribution.Trim();
        settings.DockerImage = update.DockerImage.Trim();
        settings.RemoteEndpoint = update.RemoteEndpoint.Trim();
        settings.RemoteCredentialName = update.RemoteCredentialName.Trim();
        settings.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return settings;
    }

    public async Task<ToolPolicyEvaluation> EvaluatePolicyAsync(
        Guid chatId,
        string toolName,
        CancellationToken ct = default)
    {
        toolName = AgentToolNames.Normalize(toolName);
        var projectId = await _db.Set<Chat>()
            .Where(c => c.Id == chatId)
            .Select(c => c.ProjectSpaceId)
            .FirstOrDefaultAsync(ct);

        var rules = await _db.Set<ToolPolicyRule>()
            .AsNoTracking()
            .Where(r => r.ToolName == toolName || r.ToolName == "*")
            .ToListAsync(ct);

        var globalDeny = rules.FirstOrDefault(r =>
            r.Scope == ToolPolicyScopes.Global &&
            r.Decision == ToolPolicyDecisions.Deny);
        if (globalDeny != null)
            return new ToolPolicyEvaluation(globalDeny.Decision, globalDeny.Scope);

        var chatRule = rules
            .Where(r => r.Scope == ToolPolicyScopes.Chat && r.ChatId == chatId)
            .OrderByDescending(r => r.ToolName == toolName)
            .FirstOrDefault();
        if (chatRule != null)
            return new ToolPolicyEvaluation(chatRule.Decision, chatRule.Scope);

        if (projectId != null)
        {
            var projectRule = rules
                .Where(r => r.Scope == ToolPolicyScopes.Project && r.ProjectSpaceId == projectId)
                .OrderByDescending(r => r.ToolName == toolName)
                .FirstOrDefault();
            if (projectRule != null)
                return new ToolPolicyEvaluation(projectRule.Decision, projectRule.Scope);
        }

        var legacyAllow = await _db.Set<ToolPermission>().AnyAsync(
            p => p.ChatId == chatId &&
                 (p.ToolName == toolName ||
                  (toolName == AgentToolNames.SandboxExec &&
                   p.ToolName == AgentToolNames.LegacySandboxExec)) &&
                 p.Decision == ToolPolicyDecisions.Allow,
            ct);
        return legacyAllow
            ? new ToolPolicyEvaluation(ToolPolicyDecisions.Allow, ToolPolicyScopes.Chat)
            : new ToolPolicyEvaluation(null, null);
    }

    public async Task SavePolicyAsync(
        Guid chatId,
        string toolName,
        string scope,
        string decision,
        CancellationToken ct = default)
    {
        toolName = AgentToolNames.Normalize(toolName);
        if (scope is not (ToolPolicyScopes.Chat or ToolPolicyScopes.Project or ToolPolicyScopes.Global))
            throw new ArgumentOutOfRangeException(nameof(scope));
        if (decision is not (ToolPolicyDecisions.Allow or ToolPolicyDecisions.Deny))
            throw new ArgumentOutOfRangeException(nameof(decision));

        var projectId = scope == ToolPolicyScopes.Project
            ? await _db.Set<Chat>().Where(c => c.Id == chatId).Select(c => c.ProjectSpaceId).FirstOrDefaultAsync(ct)
            : null;
        if (scope == ToolPolicyScopes.Project && projectId == null)
            scope = ToolPolicyScopes.Chat;

        var chatScopeId = scope == ToolPolicyScopes.Chat ? chatId : (Guid?)null;
        var projectScopeId = scope == ToolPolicyScopes.Project ? projectId : null;
        var rule = await _db.Set<ToolPolicyRule>().FirstOrDefaultAsync(
            r => r.ToolName == toolName &&
                 r.Scope == scope &&
                 r.ChatId == chatScopeId &&
                 r.ProjectSpaceId == projectScopeId,
            ct);
        if (rule == null)
        {
            rule = new ToolPolicyRule
            {
                ChatId = chatScopeId,
                ProjectSpaceId = projectScopeId,
                ToolName = toolName,
                Scope = scope,
                Decision = decision
            };
            _db.Set<ToolPolicyRule>().Add(rule);
        }
        else
        {
            rule.Decision = decision;
            rule.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<ToolPolicyRule>> ListPoliciesAsync(CancellationToken ct = default) =>
        await _db.Set<ToolPolicyRule>()
            .AsNoTracking()
            .OrderBy(r => r.Scope)
            .ThenBy(r => r.ToolName)
            .ToListAsync(ct);

    public async Task DeletePolicyAsync(Guid id, CancellationToken ct = default)
    {
        var rule = await _db.Set<ToolPolicyRule>().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (rule == null)
            return;
        _db.Set<ToolPolicyRule>().Remove(rule);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<McpServerConfigDto>> ListMcpServersAsync(
        Guid? projectSpaceId = null,
        CancellationToken ct = default)
    {
        var query = _db.Set<McpServerConfig>().AsNoTracking();
        if (projectSpaceId != null)
            query = query.Where(s => s.ProjectSpaceId == null || s.ProjectSpaceId == projectSpaceId);
        return await query.OrderBy(s => s.Name)
            .Select(s => new McpServerConfigDto(
                s.Id, s.ProjectSpaceId, s.Name, s.Transport, s.Command,
                s.ArgumentsJson, s.Endpoint, s.HeadersJson, s.EnvironmentJson, s.Enabled))
            .ToListAsync(ct);
    }

    public async Task<McpServerConfigDto> SaveMcpServerAsync(
        McpServerConfigDto config,
        CancellationToken ct = default)
    {
        var name = RequireName(config.Name, "MCP server");
        var duplicate = await _db.Set<McpServerConfig>()
            .AnyAsync(s => s.Name == name && s.Id != config.Id, ct);
        if (duplicate)
            throw new InvalidOperationException($"An MCP server named \"{name}\" already exists.");

        var transport = config.Transport == McpTransportTypes.StreamableHttp
            ? McpTransportTypes.StreamableHttp
            : McpTransportTypes.Stdio;
        var command = config.Command.Trim();
        var endpoint = config.Endpoint.Trim();
        if (transport == McpTransportTypes.Stdio && string.IsNullOrWhiteSpace(command))
            throw new InvalidOperationException("The STDIO command is required.");
        if (transport == McpTransportTypes.StreamableHttp &&
            (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri) ||
             endpointUri.Scheme is not ("http" or "https")))
            throw new InvalidOperationException("A valid HTTP or HTTPS MCP endpoint is required.");

        var entity = config.Id == Guid.Empty
            ? null
            : await _db.Set<McpServerConfig>().FirstOrDefaultAsync(s => s.Id == config.Id, ct);
        if (entity == null)
        {
            entity = new McpServerConfig();
            _db.Set<McpServerConfig>().Add(entity);
        }

        entity.ProjectSpaceId = config.ProjectSpaceId;
        entity.Name = name;
        entity.Transport = transport;
        entity.Command = command;
        entity.ArgumentsJson = NormalizeStringArrayJson(config.ArgumentsJson);
        entity.Endpoint = endpoint;
        entity.HeadersJson = NormalizeStringMapJson(config.HeadersJson, "Headers");
        entity.EnvironmentJson = NormalizeStringMapJson(config.EnvironmentJson, "Environment");
        entity.Enabled = config.Enabled;
        entity.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return new McpServerConfigDto(
            entity.Id, entity.ProjectSpaceId, entity.Name, entity.Transport,
            entity.Command, entity.ArgumentsJson, entity.Endpoint,
            entity.HeadersJson, entity.EnvironmentJson, entity.Enabled);
    }

    public async Task DeleteMcpServerAsync(Guid id, CancellationToken ct = default)
    {
        var server = await _db.Set<McpServerConfig>().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (server == null)
            return;
        _db.Set<McpServerConfig>().Remove(server);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<CredentialEntryDto>> ListCredentialsAsync(CancellationToken ct = default) =>
        await _db.Set<CredentialEntry>()
            .AsNoTracking()
            .OrderBy(c => c.Name)
            .Select(c => new CredentialEntryDto(
                c.Id, c.Name, c.AllowedDomains, c.AllowedTools, c.ProtectedValue != string.Empty))
            .ToListAsync(ct);

    public async Task<CredentialEntryDto> SaveCredentialAsync(
        Guid? id,
        string name,
        string? secret,
        string allowedDomains,
        string allowedTools,
        CancellationToken ct = default)
    {
        name = RequireName(name, "Credential");
        var duplicate = await _db.Set<CredentialEntry>()
            .AnyAsync(c => c.Name == name && c.Id != id, ct);
        if (duplicate)
            throw new InvalidOperationException($"A credential named \"{name}\" already exists.");

        var entity = id == null
            ? null
            : await _db.Set<CredentialEntry>().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (entity == null)
        {
            entity = new CredentialEntry();
            _db.Set<CredentialEntry>().Add(entity);
        }

        entity.Name = name;
        if (!string.IsNullOrWhiteSpace(secret))
            entity.ProtectedValue = ProtectedSecret.Protect(secret);
        entity.AllowedDomains = NormalizeLines(allowedDomains);
        entity.AllowedTools = NormalizeLines(allowedTools);
        entity.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return new CredentialEntryDto(
            entity.Id, entity.Name, entity.AllowedDomains, entity.AllowedTools,
            !string.IsNullOrWhiteSpace(entity.ProtectedValue));
    }

    public async Task DeleteCredentialAsync(Guid id, CancellationToken ct = default)
    {
        var credential = await _db.Set<CredentialEntry>().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (credential == null)
            return;
        _db.Set<CredentialEntry>().Remove(credential);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<string?> ResolveCredentialAsync(
        string name,
        string toolName,
        string domain,
        CancellationToken ct = default)
    {
        var entity = await _db.Set<CredentialEntry>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Name == name, ct);
        if (entity == null)
            return null;
        if (!MatchesList(entity.AllowedTools, AgentToolNames.Normalize(toolName)) ||
            !MatchesDomainList(entity.AllowedDomains, domain))
            return null;
        return ProtectedSecret.Reveal(entity.ProtectedValue);
    }

    private static bool IsBackend(string value) =>
        value is ToolExecutionBackends.RestrictedLocal or ToolExecutionBackends.Wsl
            or ToolExecutionBackends.Docker or ToolExecutionBackends.Remote;

    private static string RequireName(string value, string kind)
    {
        value = value.Trim();
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{kind} name is required.");
        return value;
    }

    private static string NormalizeLines(string value) =>
        string.Join("\n", SplitList(value).Distinct(StringComparer.OrdinalIgnoreCase));

    private static IEnumerable<string> SplitList(string value) =>
        value.Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool MatchesList(string value, string candidate)
    {
        var entries = SplitList(value).ToArray();
        return entries.Length == 0 ||
               entries.Any(e => e == "*" || e.Equals(candidate, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool MatchesDomainList(string value, string domain)
    {
        domain = domain.Trim().TrimEnd('.').ToLowerInvariant();
        var entries = SplitList(value).Select(e => e.ToLowerInvariant()).ToArray();
        return entries.Length == 0 || entries.Any(e =>
            e == "*" ||
            e == domain ||
            (e.StartsWith("*.", StringComparison.Ordinal) &&
             domain.EndsWith(e[1..], StringComparison.Ordinal) &&
             domain.Length > e.Length - 1));
    }

    private static string NormalizeStringArrayJson(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "[]";
        using var document = JsonDocument.Parse(value);
        if (document.RootElement.ValueKind != JsonValueKind.Array ||
            document.RootElement.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
            throw new InvalidOperationException("STDIO arguments JSON must be an array of strings.");
        return value.Trim();
    }

    private static string NormalizeStringMapJson(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "{}";
        using var document = JsonDocument.Parse(value);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            document.RootElement.EnumerateObject().Any(item => item.Value.ValueKind != JsonValueKind.String))
            throw new InvalidOperationException($"{field} JSON must be an object whose values are strings.");
        return value.Trim();
    }
}
