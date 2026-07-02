using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TLAHStudio.Core.Helpers;
using TLAHStudio.Core.Models;

namespace TLAHStudio.Core.Services;

public sealed record AgentTaskInput(
    Guid? Id,
    string Title,
    string? Description = null,
    string? Status = null,
    string? Priority = null,
    Guid? ParentTaskId = null,
    string MetadataJson = "{}");

public sealed record AgentTaskSnapshot(
    Guid Id,
    Guid ChatId,
    Guid? AgentRunId,
    string Title,
    string Description,
    string Status,
    string Priority,
    string Source,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? CompletedAt,
    string MetadataJson);

public interface IAgentTaskService
{
    Task<IReadOnlyList<AgentTaskSnapshot>> ReplaceTodosAsync(
        Guid chatId,
        Guid? agentRunId,
        IReadOnlyList<AgentTaskInput> todos,
        CancellationToken ct = default);

    Task<AgentTaskSnapshot> CreateAsync(
        Guid chatId,
        Guid? agentRunId,
        AgentTaskInput input,
        string source = AgentTaskSources.TaskCreate,
        CancellationToken ct = default);

    Task<AgentTaskSnapshot?> UpdateAsync(
        Guid chatId,
        Guid taskId,
        AgentTaskInput input,
        CancellationToken ct = default);

    Task<IReadOnlyList<AgentTaskSnapshot>> ListAsync(
        Guid chatId,
        bool includeCompleted = false,
        int limit = 80,
        CancellationToken ct = default);

    Task<string> BuildOpenTaskSummaryAsync(Guid chatId, int limit = 12, CancellationToken ct = default);
}

public sealed partial class AgentTaskService : IAgentTaskService
{
    private readonly DbContext _db;

    public AgentTaskService(DbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<AgentTaskSnapshot>> ReplaceTodosAsync(
        Guid chatId,
        Guid? agentRunId,
        IReadOnlyList<AgentTaskInput> todos,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        agentRunId = await ExistingRunIdOrNullAsync(agentRunId, ct);
        var existing = await _db.Set<AgentTaskItem>()
            .Where(t => t.ChatId == chatId && t.Source == AgentTaskSources.TodoWrite)
            .ToListAsync(ct);
        var openExisting = existing
            .Where(t => !IsTerminal(t.Status))
            .ToDictionary(t => NormalizeKey(t.Title), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<Guid>();
        var saved = new List<AgentTaskItem>();

        foreach (var todo in todos)
        {
            var title = CleanTitle(todo.Title);
            if (string.IsNullOrWhiteSpace(title))
                continue;

            var item = todo.Id.HasValue
                ? existing.FirstOrDefault(t => t.Id == todo.Id.Value)
                : null;
            item ??= openExisting.TryGetValue(NormalizeKey(title), out var match) ? match : null;
            if (item == null)
            {
                item = new AgentTaskItem
                {
                    ChatId = chatId,
                    AgentRunId = agentRunId,
                    Source = AgentTaskSources.TodoWrite,
                    CreatedAt = now
                };
                _db.Set<AgentTaskItem>().Add(item);
            }

            Apply(item, todo, title, now);
            item.AgentRunId ??= agentRunId;
            item.Source = AgentTaskSources.TodoWrite;
            seen.Add(item.Id);
            saved.Add(item);
        }

        // M4.4.0: Protect existing tasks when the model sends an empty todo list.
        // After context compaction, the model may forget its task list and send zero
        // todos, which would otherwise cancel all open todo-sourced tasks.
        if (todos.Count == 0 && openExisting.Count > 0)
        {
            await _db.SaveChangesAsync(ct);
            return saved.OrderBy(t => t.CreatedAt).Select(ToSnapshot).ToList();
        }

        foreach (var item in openExisting.Values.Where(t => !seen.Contains(t.Id)))
        {
            item.Status = AgentTaskStatuses.Cancelled;
            item.UpdatedAt = now;
            item.CompletedAt = now;
            item.MetadataJson = MergeMetadata(item.MetadataJson, new { supersededByTodoWrite = true });
            saved.Add(item);
        }

        await _db.SaveChangesAsync(ct);
        return saved.OrderBy(t => t.CreatedAt).Select(ToSnapshot).ToList();
    }

    public async Task<AgentTaskSnapshot> CreateAsync(
        Guid chatId,
        Guid? agentRunId,
        AgentTaskInput input,
        string source = AgentTaskSources.TaskCreate,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        agentRunId = await ExistingRunIdOrNullAsync(agentRunId, ct);
        var title = CleanTitle(input.Title);
        if (string.IsNullOrWhiteSpace(title))
            title = "Untitled task";

        var item = new AgentTaskItem
        {
            ChatId = chatId,
            AgentRunId = agentRunId,
            Source = source,
            CreatedAt = now
        };
        Apply(item, input, title, now);
        _db.Set<AgentTaskItem>().Add(item);
        await _db.SaveChangesAsync(ct);
        return ToSnapshot(item);
    }

    public async Task<AgentTaskSnapshot?> UpdateAsync(
        Guid chatId,
        Guid taskId,
        AgentTaskInput input,
        CancellationToken ct = default)
    {
        var item = await _db.Set<AgentTaskItem>()
            .FirstOrDefaultAsync(t => t.ChatId == chatId && t.Id == taskId, ct);
        if (item == null)
            return null;

        Apply(item, input, string.IsNullOrWhiteSpace(input.Title) ? item.Title : CleanTitle(input.Title), DateTime.UtcNow);
        await _db.SaveChangesAsync(ct);
        return ToSnapshot(item);
    }

    public async Task<IReadOnlyList<AgentTaskSnapshot>> ListAsync(
        Guid chatId,
        bool includeCompleted = false,
        int limit = 80,
        CancellationToken ct = default)
    {
        var query = _db.Set<AgentTaskItem>().Where(t => t.ChatId == chatId);
        if (!includeCompleted)
            query = query.Where(t => t.Status != AgentTaskStatuses.Completed && t.Status != AgentTaskStatuses.Cancelled);

        var rows = await query
            .OrderBy(t => t.Status == AgentTaskStatuses.Completed || t.Status == AgentTaskStatuses.Cancelled)
            .ThenByDescending(t => t.UpdatedAt)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(ct);
        return rows.Select(ToSnapshot).ToList();
    }

    public async Task<string> BuildOpenTaskSummaryAsync(Guid chatId, int limit = 12, CancellationToken ct = default)
    {
        var tasks = await ListAsync(chatId, includeCompleted: false, limit, ct);
        if (tasks.Count == 0)
            return "No open tasks are currently tracked.";

        var sb = new StringBuilder();
        sb.AppendLine("Open tracked tasks:");
        foreach (var task in tasks)
        {
            var desc = string.IsNullOrWhiteSpace(task.Description)
                ? string.Empty
                : $" — {Trim(task.Description, 140)}";
            sb.AppendLine($"- [{task.Status}] {task.Title} (id: {task.Id}, priority: {task.Priority}){desc}");
        }
        return sb.ToString().TrimEnd();
    }

    private static void Apply(AgentTaskItem item, AgentTaskInput input, string title, DateTime now)
    {
        item.Title = title;
        if (input.Description != null)
            item.Description = SecretRedactor.RedactText(input.Description.Trim());
        if (input.ParentTaskId.HasValue)
            item.ParentTaskId = input.ParentTaskId;
        item.Status = NormalizeStatus(input.Status ?? item.Status);
        item.Priority = NormalizePriority(input.Priority ?? item.Priority);
        item.MetadataJson = NormalizeMetadata(input.MetadataJson);
        item.UpdatedAt = now;
        item.CompletedAt = IsTerminal(item.Status) ? (item.CompletedAt ?? now) : null;
    }

    private static AgentTaskSnapshot ToSnapshot(AgentTaskItem item) => new(
        item.Id,
        item.ChatId,
        item.AgentRunId,
        item.Title,
        item.Description,
        item.Status,
        item.Priority,
        item.Source,
        item.CreatedAt,
        item.UpdatedAt,
        item.CompletedAt,
        item.MetadataJson);

    private static string CleanTitle(string title) => SecretRedactor.RedactText((title ?? string.Empty).Trim());

    private static string NormalizeKey(string value) =>
        RegexWhitespace().Replace(value.Trim().ToLowerInvariant(), " ");

    private static string NormalizeStatus(string value)
    {
        value = (value ?? string.Empty).Trim().ToLowerInvariant().Replace("-", "_");
        return value switch
        {
            AgentTaskStatuses.Pending or AgentTaskStatuses.InProgress or AgentTaskStatuses.Completed or
                AgentTaskStatuses.Blocked or AgentTaskStatuses.Cancelled => value,
            "done" or "complete" => AgentTaskStatuses.Completed,
            "doing" or "active" => AgentTaskStatuses.InProgress,
            "todo" => AgentTaskStatuses.Pending,
            _ => AgentTaskStatuses.Pending
        };
    }

    private static string NormalizePriority(string value)
    {
        value = (value ?? string.Empty).Trim().ToLowerInvariant();
        return value is "low" or "medium" or "high" or "critical" ? value : "medium";
    }

    private static bool IsTerminal(string status) =>
        status is AgentTaskStatuses.Completed or AgentTaskStatuses.Cancelled;

    private static string NormalizeMetadata(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return "{}";
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement);
        }
        catch
        {
            return "{}";
        }
    }

    private static string MergeMetadata(string json, object value)
    {
        var current = new Dictionary<string, object?>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var property in doc.RootElement.EnumerateObject())
                current[property.Name] = property.Value.ToString();
        }
        catch
        {
            // Ignore malformed historical metadata.
        }

        foreach (var property in value.GetType().GetProperties())
            current[property.Name] = property.GetValue(value);

        return JsonSerializer.Serialize(current);
    }

    private static string Trim(string value, int maxChars) =>
        value.Length <= maxChars ? value : value[..maxChars] + "...";

    private async Task<Guid?> ExistingRunIdOrNullAsync(Guid? agentRunId, CancellationToken ct)
    {
        if (!agentRunId.HasValue)
            return null;
        return await _db.Set<AgentRun>().AnyAsync(r => r.Id == agentRunId.Value, ct)
            ? agentRunId
            : null;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\s+")]
    private static partial System.Text.RegularExpressions.Regex RegexWhitespace();
}
