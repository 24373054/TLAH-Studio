using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;
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
        {
            if (EnsureBuiltInNetworkDomains(settings))
                await _db.SaveChangesAsync(ct);
            return settings;
        }

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

    private static bool EnsureBuiltInNetworkDomains(ToolPlatformSettings settings)
    {
        var lines = NormalizeLines(settings.NetworkAllowlist)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        var changed = false;
        foreach (var domain in new[] { "html.duckduckgo.com", "lite.duckduckgo.com" })
        {
            if (lines.Any(line => string.Equals(line, domain, StringComparison.OrdinalIgnoreCase)))
                continue;
            lines.Add(domain);
            changed = true;
        }

        if (changed)
            settings.NetworkAllowlist = string.Join('\n', lines);
        return changed;
    }

    public async Task<ToolPolicyEvaluation> EvaluatePolicyAsync(
        Guid chatId,
        string toolName,
        string argumentsJson = "{}",
        ToolSafetyAssessment? safety = null,
        CancellationToken ct = default)
    {
        toolName = AgentToolNames.Normalize(toolName);
        var projectId = await _db.Set<Chat>()
            .Where(c => c.Id == chatId)
            .Select(c => c.ProjectSpaceId)
            .FirstOrDefaultAsync(ct);

        var rules = await _db.Set<ToolPolicyRule>()
            .AsNoTracking()
            .Where(r =>
                r.SubjectKind == ToolPolicySubjects.Tool ||
                r.SubjectKind == ToolPolicySubjects.Path ||
                r.SubjectKind == ToolPolicySubjects.Domain ||
                r.SubjectKind == ToolPolicySubjects.Command ||
                r.SubjectKind == string.Empty)
            .ToListAsync(ct);

        var request = ToolPolicyRequest.From(toolName, argumentsJson, safety);
        var matches = rules
            .Select(rule => MatchRule(rule, request, chatId, projectId))
            .Where(match => match != null)
            .Select(match => match!)
            .ToArray();

        var globalDeny = matches
            .Where(match => match.Rule.Scope == ToolPolicyScopes.Global &&
                            match.Rule.Decision == ToolPolicyDecisions.Deny)
            .OrderByDescending(match => match.Score)
            .FirstOrDefault();
        if (globalDeny != null)
            return ToEvaluation(globalDeny);

        var scopedDeny = matches
            .Where(match =>
                match.Rule.Decision == ToolPolicyDecisions.Deny &&
                ScopeApplies(match.Rule, chatId, projectId))
            .OrderByDescending(match => ScopeScore(match.Rule.Scope))
            .ThenByDescending(match => match.Score)
            .FirstOrDefault();
        if (scopedDeny != null)
            return ToEvaluation(scopedDeny);

        var allow = matches
            .Where(match =>
                match.Rule.Decision == ToolPolicyDecisions.Allow &&
                ScopeApplies(match.Rule, chatId, projectId))
            .OrderByDescending(match => ScopeScore(match.Rule.Scope))
            .ThenByDescending(match => match.Score)
            .FirstOrDefault();
        if (allow != null)
            return ToEvaluation(allow);

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
        string? subjectKind = null,
        string? pattern = null,
        string? description = null,
        CancellationToken ct = default)
    {
        toolName = AgentToolNames.Normalize(toolName);
        if (scope is not (ToolPolicyScopes.Chat or ToolPolicyScopes.Project or ToolPolicyScopes.Global))
            throw new ArgumentOutOfRangeException(nameof(scope));
        if (decision is not (ToolPolicyDecisions.Allow or ToolPolicyDecisions.Deny))
            throw new ArgumentOutOfRangeException(nameof(decision));
        subjectKind = NormalizeSubjectKind(subjectKind);
        pattern = NormalizePattern(subjectKind, pattern ?? toolName);

        var projectId = scope == ToolPolicyScopes.Project
            ? await _db.Set<Chat>().Where(c => c.Id == chatId).Select(c => c.ProjectSpaceId).FirstOrDefaultAsync(ct)
            : null;
        if (scope == ToolPolicyScopes.Project && projectId == null)
            scope = ToolPolicyScopes.Chat;

        var chatScopeId = scope == ToolPolicyScopes.Chat ? chatId : (Guid?)null;
        var projectScopeId = scope == ToolPolicyScopes.Project ? projectId : null;
        var rule = await _db.Set<ToolPolicyRule>().FirstOrDefaultAsync(
            r => r.SubjectKind == subjectKind &&
                 r.Pattern == pattern &&
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
                SubjectKind = subjectKind,
                Pattern = pattern,
                Scope = scope,
                Decision = decision,
                Description = description?.Trim() ?? string.Empty
            };
            _db.Set<ToolPolicyRule>().Add(rule);
        }
        else
        {
            rule.ToolName = toolName;
            rule.Decision = decision;
            rule.Description = description?.Trim() ?? rule.Description;
            rule.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task<ToolPolicyRule> SavePolicyRuleAsync(
        ToolPolicyRuleUpdate update,
        CancellationToken ct = default)
    {
        var scope = update.Scope;
        if (scope is not (ToolPolicyScopes.Chat or ToolPolicyScopes.Project or ToolPolicyScopes.Global))
            throw new ArgumentOutOfRangeException(nameof(update.Scope));
        if (update.Decision is not (ToolPolicyDecisions.Allow or ToolPolicyDecisions.Deny))
            throw new ArgumentOutOfRangeException(nameof(update.Decision));

        var subjectKind = NormalizeSubjectKind(update.SubjectKind);
        var pattern = NormalizePattern(subjectKind, update.Pattern);
        var toolName = subjectKind == ToolPolicySubjects.Tool
            ? ToolPatternToToolName(pattern)
            : pattern;

        Guid? chatId = scope == ToolPolicyScopes.Chat
            ? update.ChatId ?? throw new InvalidOperationException("Chat-scoped policy rules require a chat id.")
            : null;
        var projectId = scope == ToolPolicyScopes.Project
            ? update.ProjectSpaceId
            : null;

        var rule = update.Id is { } id && id != Guid.Empty
            ? await _db.Set<ToolPolicyRule>().FirstOrDefaultAsync(r => r.Id == id, ct)
            : null;
        if (rule == null)
        {
            rule = await _db.Set<ToolPolicyRule>().FirstOrDefaultAsync(
                r => r.Scope == scope &&
                     r.ChatId == chatId &&
                     r.ProjectSpaceId == projectId &&
                     r.SubjectKind == subjectKind &&
                     r.Pattern == pattern,
                ct);
        }
        if (rule == null)
        {
            rule = new ToolPolicyRule
            {
                ChatId = chatId,
                ProjectSpaceId = projectId,
                CreatedAt = DateTime.UtcNow
            };
            _db.Set<ToolPolicyRule>().Add(rule);
        }

        rule.ToolName = toolName;
        rule.SubjectKind = subjectKind;
        rule.Pattern = pattern;
        rule.Scope = scope;
        rule.Decision = update.Decision;
        rule.Description = update.Description.Trim();
        rule.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return rule;
    }

    public async Task<IReadOnlyList<ToolPolicyRule>> ListPoliciesAsync(CancellationToken ct = default) =>
        await _db.Set<ToolPolicyRule>()
            .AsNoTracking()
            .OrderBy(r => r.Scope)
            .ThenBy(r => r.SubjectKind)
            .ThenBy(r => r.Pattern)
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

    private static ToolPolicyEvaluation ToEvaluation(ToolPolicyMatch match) =>
        new(
            match.Rule.Decision,
            match.Rule.Scope,
            match.Rule.Id,
            NormalizeSubjectKind(match.Rule.SubjectKind),
            EffectivePattern(match.Rule),
            match.MatchedValue,
            match.Rule.Description);

    private static ToolPolicyMatch? MatchRule(
        ToolPolicyRule rule,
        ToolPolicyRequest request,
        Guid chatId,
        Guid? projectId)
    {
        if (!ScopeCanMatch(rule, chatId, projectId))
            return null;

        var subjectKind = NormalizeSubjectKind(rule.SubjectKind);
        var pattern = EffectivePattern(rule);
        var candidates = subjectKind switch
        {
            ToolPolicySubjects.Path => request.Paths,
            ToolPolicySubjects.Domain => request.Domains,
            ToolPolicySubjects.Command => request.Commands,
            _ => request.ToolCandidates
        };

        foreach (var candidate in candidates)
        {
            if (MatchesSubject(subjectKind, pattern, candidate))
            {
                return new ToolPolicyMatch(
                    rule,
                    candidate,
                    PatternScore(pattern, candidate));
            }
        }

        return null;
    }

    private static bool ScopeCanMatch(ToolPolicyRule rule, Guid chatId, Guid? projectId) =>
        rule.Scope switch
        {
            ToolPolicyScopes.Chat => rule.ChatId == chatId,
            ToolPolicyScopes.Project => projectId != null && rule.ProjectSpaceId == projectId,
            ToolPolicyScopes.Global => true,
            _ => false
        };

    private static bool ScopeApplies(ToolPolicyRule rule, Guid chatId, Guid? projectId) =>
        ScopeCanMatch(rule, chatId, projectId);

    private static int ScopeScore(string scope) =>
        scope switch
        {
            ToolPolicyScopes.Chat => 300,
            ToolPolicyScopes.Project => 200,
            ToolPolicyScopes.Global => 100,
            _ => 0
        };

    private static bool MatchesSubject(string subjectKind, string pattern, string candidate)
    {
        pattern = NormalizePattern(subjectKind, pattern);
        candidate = NormalizeCandidate(subjectKind, candidate);
        return subjectKind switch
        {
            ToolPolicySubjects.Domain => MatchesDomainList(pattern, candidate),
            _ => WildcardMatch(pattern, candidate)
        };
    }

    private static int PatternScore(string pattern, string candidate)
    {
        if (string.Equals(pattern, candidate, StringComparison.OrdinalIgnoreCase))
            return 1000 + pattern.Length;
        if (!pattern.Contains('*') && !pattern.Contains('?'))
            return 800 + pattern.Length;
        return pattern.Count(ch => ch is not '*' and not '?');
    }

    private static bool WildcardMatch(string pattern, string candidate)
    {
        if (pattern == "*")
            return true;
        var escaped = Regex.Escape(pattern)
            .Replace(@"\*", ".*", StringComparison.Ordinal)
            .Replace(@"\?", ".", StringComparison.Ordinal);
        return Regex.IsMatch(
            candidate,
            $"^{escaped}$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string EffectivePattern(ToolPolicyRule rule)
    {
        if (!string.IsNullOrWhiteSpace(rule.Pattern))
            return rule.Pattern.Trim();
        return string.IsNullOrWhiteSpace(rule.ToolName)
            ? "*"
            : rule.ToolName.Trim();
    }

    private static string NormalizeSubjectKind(string? subjectKind) =>
        subjectKind?.Trim().ToLowerInvariant() switch
        {
            ToolPolicySubjects.Path => ToolPolicySubjects.Path,
            ToolPolicySubjects.Domain => ToolPolicySubjects.Domain,
            ToolPolicySubjects.Command => ToolPolicySubjects.Command,
            _ => ToolPolicySubjects.Tool
        };

    private static string NormalizePattern(string subjectKind, string pattern)
    {
        pattern = string.IsNullOrWhiteSpace(pattern) ? "*" : pattern.Trim();
        if (subjectKind == ToolPolicySubjects.Tool &&
            pattern.StartsWith("tool(", StringComparison.OrdinalIgnoreCase) &&
            pattern.EndsWith(')'))
        {
            pattern = pattern[5..^1].Trim();
        }

        return NormalizeCandidate(subjectKind, pattern);
    }

    private static string NormalizeCandidate(string subjectKind, string value)
    {
        value = value.Trim();
        return subjectKind switch
        {
            ToolPolicySubjects.Path => value.Replace('\\', '/').TrimStart('/'),
            ToolPolicySubjects.Domain => value.TrimEnd('.').ToLowerInvariant(),
            ToolPolicySubjects.Command => value.ToLowerInvariant(),
            _ => AgentToolNames.Normalize(value).ToLowerInvariant()
        };
    }

    private static string ToolPatternToToolName(string pattern)
    {
        pattern = NormalizePattern(ToolPolicySubjects.Tool, pattern);
        return pattern.Contains('*') || pattern.Contains('?')
            ? pattern
            : AgentToolNames.Normalize(pattern);
    }

    private sealed record ToolPolicyMatch(
        ToolPolicyRule Rule,
        string MatchedValue,
        int Score);

    private sealed record ToolPolicyRequest(
        IReadOnlyList<string> ToolCandidates,
        IReadOnlyList<string> Paths,
        IReadOnlyList<string> Domains,
        IReadOnlyList<string> Commands)
    {
        public static ToolPolicyRequest From(
            string toolName,
            string argumentsJson,
            ToolSafetyAssessment? safety)
        {
            var toolCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                NormalizeCandidate(ToolPolicySubjects.Tool, toolName)
            };
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var commands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
                var root = document.RootElement;
                AddPath(root, "path", paths);
                AddPath(root, "target", paths);
                AddPath(root, "file", paths);
                AddDomain(root, "domain", domains);
                AddUrl(root, "url", domains);
                AddUrl(root, "endpoint", domains);

                if (string.Equals(toolName, AgentToolNames.McpCall, StringComparison.OrdinalIgnoreCase))
                {
                    var server = ReadString(root, "server");
                    var mcpTool = ReadString(root, "tool");
                    if (!string.IsNullOrWhiteSpace(server) && !string.IsNullOrWhiteSpace(mcpTool))
                    {
                        toolCandidates.Add($"mcp__{SanitizeMcpName(server)}__{SanitizeMcpName(mcpTool)}");
                        toolCandidates.Add($"mcp__{SanitizeMcpName(server)}__*");
                        toolCandidates.Add($"mcp__*__{SanitizeMcpName(mcpTool)}");
                    }
                }

                if (root.TryGetProperty("patch", out var patch) &&
                    patch.ValueKind == JsonValueKind.String)
                {
                    foreach (var path in WorkspaceCodeToolSupport.ExtractPatchPaths(patch.GetString() ?? string.Empty))
                        paths.Add(NormalizeCandidate(ToolPolicySubjects.Path, path));
                }
            }
            catch
            {
            }

            if (safety != null)
            {
                try
                {
                    using var preview = JsonDocument.Parse(safety.PreviewJson);
                    CollectPreview(preview.RootElement, paths, domains);
                }
                catch
                {
                }
            }

            // M4.6.0: Extract command text from terminal_exec / sandbox_exec arguments
            // so users can write rules like: Pattern="git *", SubjectKind="command", Decision="allow"
            if (string.Equals(toolName, AgentToolNames.SandboxExec, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(toolName, AgentToolNames.TerminalExec, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
                    if (doc.RootElement.TryGetProperty("command", out var cmdEl) &&
                        cmdEl.ValueKind == JsonValueKind.String)
                    {
                        var cmd = cmdEl.GetString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(cmd))
                        {
                            commands.Add(NormalizeCandidate(ToolPolicySubjects.Command, cmd));
                            // Also add the binary name as a shorter candidate
                            var binary = cmd.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0];
                            if (binary.Length > 0 && binary.Length < cmd.Length)
                                commands.Add(NormalizeCandidate(ToolPolicySubjects.Command, binary));
                        }
                    }
                }
                catch { }
            }

            return new ToolPolicyRequest(
                toolCandidates.ToArray(),
                paths.ToArray(),
                domains.ToArray(),
                commands.ToArray());
        }

        private static void CollectPreview(
            JsonElement element,
            HashSet<string> paths,
            HashSet<string> domains)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        var value = property.Value.GetString() ?? string.Empty;
                        if (property.Name.Contains("path", StringComparison.OrdinalIgnoreCase))
                            paths.Add(NormalizeCandidate(ToolPolicySubjects.Path, value));
                        if (property.Name.Contains("domain", StringComparison.OrdinalIgnoreCase))
                            domains.Add(NormalizeCandidate(ToolPolicySubjects.Domain, value));
                        if (property.Name.Contains("url", StringComparison.OrdinalIgnoreCase))
                            AddDomainFromUrl(value, domains);
                    }
                    else
                    {
                        CollectPreview(property.Value, paths, domains);
                    }
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                    CollectPreview(item, paths, domains);
            }
        }

        private static string? ReadString(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static void AddPath(JsonElement root, string name, HashSet<string> paths)
        {
            var value = ReadString(root, name);
            if (!string.IsNullOrWhiteSpace(value))
                paths.Add(NormalizeCandidate(ToolPolicySubjects.Path, value));
        }

        private static void AddDomain(JsonElement root, string name, HashSet<string> domains)
        {
            var value = ReadString(root, name);
            if (!string.IsNullOrWhiteSpace(value))
                domains.Add(NormalizeCandidate(ToolPolicySubjects.Domain, value));
        }

        private static void AddUrl(JsonElement root, string name, HashSet<string> domains)
        {
            var value = ReadString(root, name);
            if (!string.IsNullOrWhiteSpace(value))
                AddDomainFromUrl(value, domains);
        }

        private static void AddDomainFromUrl(string value, HashSet<string> domains)
        {
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
                domains.Add(NormalizeCandidate(ToolPolicySubjects.Domain, uri.IdnHost));
        }

        private static string SanitizeMcpName(string value)
        {
            var cleaned = Regex.Replace(value.Trim().ToLowerInvariant(), @"[^a-z0-9_-]+", "_");
            return string.IsNullOrWhiteSpace(cleaned) ? "*" : cleaned;
        }
    }

    private static bool IsBackend(string value) =>
        value is ToolExecutionBackends.RestrictedLocal or ToolExecutionBackends.UnrestrictedLocal or ToolExecutionBackends.Wsl
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
