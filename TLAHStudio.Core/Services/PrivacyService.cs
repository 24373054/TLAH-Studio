using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TLAHStudio.Core.Helpers;
using TLAHStudio.Core.Models;

namespace TLAHStudio.Core.Services;

public class PrivacyService : IPrivacyService
{
    private readonly DbContext _db;

    public PrivacyService(DbContext db)
    {
        _db = db;
    }

    public async Task<PrivacySummary> GetSummaryAsync(CancellationToken ct = default)
    {
        var dbPath = _db.Database.GetDbConnection().DataSource;
        var configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TLAH Studio",
            "config");

        return new PrivacySummary(
            ChatCount: await _db.Set<Chat>().CountAsync(c => c.DeletedAt == null, ct),
            MessageCount: await _db.Set<Message>().CountAsync(ct),
            TurnCount: await _db.Set<Turn>().CountAsync(ct),
            RawRequestCount: await _db.Set<RawRequest>().CountAsync(ct),
            RawResponseCount: await _db.Set<RawResponse>().CountAsync(ct),
            DatabaseSizeBytes: File.Exists(dbPath) ? new FileInfo(dbPath).Length : 0,
            DatabasePath: dbPath,
            ConfigDirectory: configDir,
            CheckedAtUtc: DateTime.UtcNow);
    }

    public async Task<string> ExportAllDataAsync(string targetPath, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var chats = await _db.Set<Chat>()
            .AsNoTracking()
            .Where(c => c.DeletedAt == null)
            .Include(c => c.Messages)
            .Include(c => c.Turns)
                .ThenInclude(t => t.RawRequest)
            .Include(c => c.Turns)
                .ThenInclude(t => t.RawResponse)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(ct);

        var agentFiles = await _db.Set<AgentFile>()
            .AsNoTracking()
            .ToDictionaryAsync(a => a.ChatId, ct);

        var payload = new
        {
            Format = "tlah-studio-export/v1",
            ExportedAtUtc = DateTime.UtcNow,
            Privacy = new
            {
                ApiKeysIncluded = false,
                RawPayloadsRedacted = true
            },
            GlobalSettings = await _db.Set<GlobalSettings>()
                .AsNoTracking()
                .Select(g => new
                {
                    g.Id,
                    g.Provider,
                    ApiKey = string.Empty,
                    g.BaseUrl,
                    g.Model,
                    g.Temperature,
                    g.MaxTokens,
                    g.SystemPrompt,
                    g.UserRole,
                    g.UpdatedAt
                })
                .FirstOrDefaultAsync(ct),
            Chats = chats.Select(c => new
                {
                    c.Id,
                    c.Title,
                    c.SystemPrompt,
                    c.CreatedAt,
                    c.UpdatedAt,
                    c.IsPinned,
                    c.IsArchived,
                    c.ProjectSpaceId,
                    c.ConfigProfileId,
                    Messages = c.Messages
                        .OrderBy(m => m.SequenceNum)
                        .Select(m => new { m.Id, m.Role, m.Content, m.TurnId, m.SequenceNum, m.CreatedAt }),
                    Turns = c.Turns
                        .OrderBy(t => t.TurnNumber)
                        .Select(t => new
                        {
                            t.Id,
                            t.TurnNumber,
                            t.CreatedAt,
                            RawRequest = t.RawRequest == null ? null : new
                            {
                                t.RawRequest.Provider,
                                t.RawRequest.EndpointUrl,
                                RequestJson = SecretRedactor.RedactJson(t.RawRequest.RequestJson),
                                t.RawRequest.CreatedAt
                            },
                            RawResponse = t.RawResponse == null ? null : new
                            {
                                t.RawResponse.Provider,
                                ResponseJson = SecretRedactor.RedactJson(t.RawResponse.ResponseJson),
                                t.RawResponse.HttpStatusCode,
                                t.RawResponse.LatencyMs,
                                t.RawResponse.TokenUsageJson,
                                t.RawResponse.CreatedAt
                            }
                        }),
                    AgentFile = agentFiles.TryGetValue(c.Id, out var agent)
                        ? new { agent.Filename, agent.Content, agent.SizeBytes, agent.CreatedAt, agent.UpdatedAt }
                        : null
                }).ToList()
            ,
            ProjectSpaces = await _db.Set<ProjectSpace>()
                .AsNoTracking()
                .Select(p => new
                {
                    p.Id,
                    p.Name,
                    p.Description,
                    p.SharedPrompt,
                    p.TeamNorms,
                    p.CloudSyncEnabled,
                    p.SyncFolderPath,
                    p.DefaultConfigProfileId,
                    p.CreatedAt,
                    p.UpdatedAt
                })
                .ToListAsync(ct),
            ConfigProfiles = await _db.Set<ConfigProfile>()
                .AsNoTracking()
                .Select(p => new
                {
                    p.Id,
                    p.ProjectSpaceId,
                    p.Name,
                    p.Provider,
                    ApiKey = string.Empty,
                    p.BaseUrl,
                    p.Model,
                    p.Temperature,
                    p.MaxTokens,
                    p.UserRole,
                    p.SystemPrompt,
                    p.IsShared,
                    p.CreatedAt,
                    p.UpdatedAt
                })
                .ToListAsync(ct),
            PromptTemplates = await _db.Set<PromptTemplate>()
                .AsNoTracking()
                .Select(t => new
                {
                    t.Id,
                    t.ProjectSpaceId,
                    t.Name,
                    t.Category,
                    t.Content,
                    t.IsShared,
                    t.CreatedAt,
                    t.UpdatedAt
                })
                .ToListAsync(ct),
            AuditLogEntries = await _db.Set<AuditLogEntry>()
                .AsNoTracking()
                .OrderByDescending(a => a.CreatedAt)
                .Take(1000)
                .Select(a => new
                {
                    a.Id,
                    a.ProjectSpaceId,
                    a.ChatId,
                    a.EventType,
                    a.EntityType,
                    a.EntityId,
                    a.Summary,
                    MetadataJson = SecretRedactor.RedactJson(a.MetadataJson),
                    a.CreatedAt
                })
                .ToListAsync(ct),
            AgentRuns = await _db.Set<AgentRun>()
                .AsNoTracking()
                .OrderBy(r => r.CreatedAt)
                .Select(r => new
                {
                    r.Id,
                    r.ChatId,
                    r.TurnId,
                    r.Status,
                    r.UserRequest,
                    r.CurrentStep,
                    r.MaxSteps,
                    r.ErrorMessage,
                    r.CreatedAt,
                    r.UpdatedAt,
                    r.CompletedAt
                })
                .ToListAsync(ct),
            AgentSteps = await _db.Set<AgentStep>()
                .AsNoTracking()
                .OrderBy(s => s.StartedAt)
                .Select(s => new
                {
                    s.Id,
                    s.AgentRunId,
                    s.StepNumber,
                    s.Kind,
                    s.Status,
                    s.Summary,
                    InputJson = SecretRedactor.RedactJson(s.InputJson),
                    OutputJson = SecretRedactor.RedactJson(s.OutputJson),
                    s.StartedAt,
                    s.CompletedAt
                })
                .ToListAsync(ct),
            ToolInvocations = await _db.Set<ToolInvocation>()
                .AsNoTracking()
                .OrderBy(i => i.CreatedAt)
                .Select(i => new
                {
                    i.Id,
                    i.AgentRunId,
                    i.AgentStepId,
                    i.ToolName,
                    ArgumentsJson = SecretRedactor.RedactJson(i.ArgumentsJson),
                    ResultJson = SecretRedactor.RedactJson(i.ResultJson),
                    i.Status,
                    i.RequiresApproval,
                    i.Approved,
                    i.CreatedAt,
                    i.CompletedAt
                })
                .ToListAsync(ct),
            ToolPlatformSettings = await _db.Set<ToolPlatformSettings>()
                .AsNoTracking()
                .Select(s => new
                {
                    s.DefaultBackend,
                    s.NetworkAllowlist,
                    s.MaxRuntimeSeconds,
                    s.MaxOutputChars,
                    s.MaxFileBytes,
                    s.MaxMemoryMb,
                    s.MaxProcesses,
                    s.WslDistribution,
                    s.DockerImage,
                    s.RemoteEndpoint,
                    s.RemoteCredentialName,
                    s.UpdatedAt
                })
                .FirstOrDefaultAsync(ct),
            ToolPolicyRules = await _db.Set<ToolPolicyRule>()
                .AsNoTracking()
                .Select(r => new
                {
                    r.Id,
                    r.ChatId,
                    r.ProjectSpaceId,
                    r.ToolName,
                    r.Scope,
                    r.Decision,
                    r.CreatedAt,
                    r.UpdatedAt
                })
                .ToListAsync(ct),
            McpServerConfigs = await _db.Set<McpServerConfig>()
                .AsNoTracking()
                .Select(s => new
                {
                    s.Id,
                    s.ProjectSpaceId,
                    s.Name,
                    s.Transport,
                    s.Command,
                    s.ArgumentsJson,
                    s.Endpoint,
                    HeadersJson = SecretRedactor.RedactJson(s.HeadersJson),
                    EnvironmentJson = SecretRedactor.RedactJson(s.EnvironmentJson),
                    s.Enabled,
                    s.CreatedAt,
                    s.UpdatedAt
                })
                .ToListAsync(ct),
            Credentials = await _db.Set<CredentialEntry>()
                .AsNoTracking()
                .Select(c => new
                {
                    c.Id,
                    c.Name,
                    c.AllowedDomains,
                    c.AllowedTools,
                    HasSecret = c.ProtectedValue != string.Empty,
                    c.CreatedAt,
                    c.UpdatedAt
                })
                .ToListAsync(ct)
        };

        var json = JsonSerializer.Serialize(payload, ExportJsonOptions);
        await File.WriteAllTextAsync(targetPath, json, ct);
        return targetPath;
    }

    public async Task ImportAllDataAsync(string sourcePath, CancellationToken ct = default)
    {
        var text = await File.ReadAllTextAsync(sourcePath, ct);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (!root.TryGetProperty("Format", out var format) ||
            format.GetString() != "tlah-studio-export/v1")
        {
            throw new InvalidOperationException("This file is not a TLAH Studio data export.");
        }

        if (!root.TryGetProperty("Chats", out var chats) || chats.ValueKind != JsonValueKind.Array)
            return;

        foreach (var chatElement in chats.EnumerateArray())
        {
            var chatId = ReadGuid(chatElement, "Id") ?? Guid.NewGuid();
            if (await _db.Set<Chat>().AnyAsync(c => c.Id == chatId, ct))
                chatId = Guid.NewGuid();

            var chat = new Chat
            {
                Id = chatId,
                Title = ReadString(chatElement, "Title", "Imported Chat"),
                SystemPrompt = ReadString(chatElement, "SystemPrompt", string.Empty),
                CreatedAt = ReadDate(chatElement, "CreatedAt") ?? DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                IsPinned = ReadBool(chatElement, "IsPinned"),
                IsArchived = ReadBool(chatElement, "IsArchived"),
                ProjectSpaceId = ReadGuid(chatElement, "ProjectSpaceId"),
                ConfigProfileId = ReadGuid(chatElement, "ConfigProfileId")
            };
            _db.Set<Chat>().Add(chat);

            var turnMap = new Dictionary<Guid, Guid>();
            if (chatElement.TryGetProperty("Turns", out var turns) && turns.ValueKind == JsonValueKind.Array)
            {
                foreach (var turnElement in turns.EnumerateArray())
                {
                    var oldTurnId = ReadGuid(turnElement, "Id") ?? Guid.NewGuid();
                    var turnId = Guid.NewGuid();
                    turnMap[oldTurnId] = turnId;
                    _db.Set<Turn>().Add(new Turn
                    {
                        Id = turnId,
                        ChatId = chatId,
                        TurnNumber = ReadInt(turnElement, "TurnNumber") ?? (turnMap.Count + 1),
                        CreatedAt = ReadDate(turnElement, "CreatedAt") ?? DateTime.UtcNow
                    });
                }
            }

            if (chatElement.TryGetProperty("Messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
            {
                foreach (var messageElement in messages.EnumerateArray())
                {
                    var oldTurnId = ReadGuid(messageElement, "TurnId");
                    _db.Set<Message>().Add(new Message
                    {
                        Id = Guid.NewGuid(),
                        ChatId = chatId,
                        Role = ReadString(messageElement, "Role", "user"),
                        Content = ReadString(messageElement, "Content", string.Empty),
                        SequenceNum = ReadInt(messageElement, "SequenceNum") ?? 0,
                        CreatedAt = ReadDate(messageElement, "CreatedAt") ?? DateTime.UtcNow,
                        TurnId = oldTurnId != null && turnMap.TryGetValue(oldTurnId.Value, out var newTurnId)
                            ? newTurnId
                            : null
                    });
                }
            }

            if (chatElement.TryGetProperty("AgentFile", out var agent) && agent.ValueKind == JsonValueKind.Object)
            {
                var content = ReadString(agent, "Content", string.Empty);
                if (!string.IsNullOrWhiteSpace(content))
                {
                    _db.Set<AgentFile>().Add(new AgentFile
                    {
                        ChatId = chatId,
                        Filename = ReadString(agent, "Filename", "AGENT.md"),
                        Content = content,
                        SizeBytes = ReadInt(agent, "SizeBytes") ?? content.Length,
                        CreatedAt = ReadDate(agent, "CreatedAt") ?? DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    });
                }
            }
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task ClearAllDataAsync(CancellationToken ct = default)
    {
        _db.Set<AuditLogEntry>().RemoveRange(_db.Set<AuditLogEntry>());
        _db.Set<AgentArtifact>().RemoveRange(_db.Set<AgentArtifact>());
        _db.Set<AgentCheckpoint>().RemoveRange(_db.Set<AgentCheckpoint>());
        _db.Set<ToolInvocation>().RemoveRange(_db.Set<ToolInvocation>());
        _db.Set<AgentStep>().RemoveRange(_db.Set<AgentStep>());
        _db.Set<AgentRun>().RemoveRange(_db.Set<AgentRun>());
        _db.Set<ToolPermission>().RemoveRange(_db.Set<ToolPermission>());
        _db.Set<ToolPolicyRule>().RemoveRange(_db.Set<ToolPolicyRule>());
        _db.Set<McpServerConfig>().RemoveRange(_db.Set<McpServerConfig>());
        _db.Set<CredentialEntry>().RemoveRange(_db.Set<CredentialEntry>());
        _db.Set<ToolPlatformSettings>().RemoveRange(_db.Set<ToolPlatformSettings>());
        _db.Set<RawResponse>().RemoveRange(_db.Set<RawResponse>());
        _db.Set<RawRequest>().RemoveRange(_db.Set<RawRequest>());
        _db.Set<Message>().RemoveRange(_db.Set<Message>());
        _db.Set<Turn>().RemoveRange(_db.Set<Turn>());
        _db.Set<AgentFile>().RemoveRange(_db.Set<AgentFile>());
        _db.Set<ChatSettings>().RemoveRange(_db.Set<ChatSettings>());
        _db.Set<Chat>().RemoveRange(_db.Set<Chat>());
        _db.Set<PromptTemplate>().RemoveRange(_db.Set<PromptTemplate>());
        _db.Set<ConfigProfile>().RemoveRange(_db.Set<ConfigProfile>());
        _db.Set<ProjectSpace>().RemoveRange(_db.Set<ProjectSpace>());
        _db.Set<GlobalSettings>().RemoveRange(_db.Set<GlobalSettings>());
        _db.Set<GlobalSettings>().Add(new GlobalSettings { Id = 1 });
        _db.Set<ToolPlatformSettings>().Add(new ToolPlatformSettings { Id = 1 });
        await _db.SaveChangesAsync(ct);
    }

    private static string ReadString(JsonElement root, string name, string fallback) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static Guid? ReadGuid(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        Guid.TryParse(value.GetString(), out var id)
            ? id
            : null;

    private static int? ReadInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var number)
            ? number
            : null;

    private static bool ReadBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        value.GetBoolean();

    private static DateTime? ReadDate(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        DateTime.TryParse(value.GetString(), out var date)
            ? date
            : null;

    private static readonly JsonSerializerOptions ExportJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
