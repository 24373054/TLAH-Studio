using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TLAHStudio.Core.Llm;
using TLAHStudio.Core.Models;
using TLAHStudio.Core.Services.Background;

namespace TLAHStudio.Core.Services;

public sealed class TodoWriteAgentTool : IAgentTool
{
    private readonly IAgentTaskService _tasks;

    public TodoWriteAgentTool(IAgentTaskService tasks)
    {
        _tasks = tasks;
    }

    public LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        AgentToolNames.TodoWrite,
        "Persist the current todo list for the chat. Use this proactively for multi-step work and keep exactly one active item in_progress when possible.",
        new Dictionary<string, object>
        {
            ["todos"] = new Dictionary<string, object>
            {
                ["type"] = "array",
                ["description"] = "The complete current todo list.",
                ["items"] = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = TaskProperties(includeId: true),
                    ["required"] = new[] { "title" },
                    ["additionalProperties"] = false
                }
            },
            ["reason"] = AgentToolSupport.StringProperty("Why the todo list is being updated.")
        },
        ["todos"]);

    public bool RequiresApproval => false;

    public async Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct = default)
    {
        if (!AgentToolSupport.TryParse(argumentsJson, out var root, out var error))
            return new AgentToolResult(false, string.Empty, error);
        if (!root.TryGetProperty("todos", out var todosElement) || todosElement.ValueKind != JsonValueKind.Array)
            return new AgentToolResult(false, string.Empty, "todos must be an array.");

        var inputs = new List<AgentTaskInput>();
        foreach (var item in todosElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;
            inputs.Add(ReadTaskInput(item));
        }

        var saved = await _tasks.ReplaceTodosAsync(context.ChatId, context.AgentRunId, inputs, ct);
        return new AgentToolResult(true, FormatTasks("Todo list updated", saved));
    }

    private static Dictionary<string, object> TaskProperties(bool includeId) => TaskToolSchemas.TaskProperties(includeId);
    private static AgentTaskInput ReadTaskInput(JsonElement root) => TaskToolSchemas.ReadTaskInput(root);
    private static string FormatTasks(string title, IReadOnlyList<AgentTaskSnapshot> tasks) => TaskToolSchemas.FormatTasks(title, tasks);
}

public sealed class TaskCreateAgentTool : IAgentTool
{
    private readonly IAgentTaskService _tasks;
    private readonly IBackgroundTaskService _background;
    private readonly ISandboxCommandService _sandbox;

    public TaskCreateAgentTool(
        IAgentTaskService tasks,
        IBackgroundTaskService background,
        ISandboxCommandService sandbox)
    {
        _tasks = tasks;
        _background = background;
        _sandbox = sandbox;
    }

    public LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        AgentToolNames.TaskCreate,
        "Create a persistent task. Optionally launch a local background task with a prompt or sandbox command and an output file.",
        new Dictionary<string, object>
        {
            ["title"] = AgentToolSupport.StringProperty("Short task title."),
            ["description"] = AgentToolSupport.StringProperty("Detailed task description or acceptance criteria."),
            ["status"] = AgentToolSupport.StringProperty("Initial status: pending, in_progress, blocked, completed, or cancelled."),
            ["priority"] = AgentToolSupport.StringProperty("Priority: low, medium, high, or critical."),
            ["parent_task_id"] = AgentToolSupport.StringProperty("Optional parent task id."),
            ["background"] = AgentToolSupport.BooleanProperty("Whether to create a local background task record and output file."),
            ["prompt"] = AgentToolSupport.StringProperty("Prompt/directive for a local background agent task."),
            ["command"] = AgentToolSupport.StringProperty("Optional sandbox command to run in the background."),
            ["model"] = AgentToolSupport.StringProperty("Optional model preference for future agent execution."),
            ["workspace_isolation"] = AgentToolSupport.StringProperty("Isolation mode. 4.0 supports local; worktree is reserved for later.")
        },
        ["title"]);

    public bool RequiresApproval => true;

    public async Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct = default)
    {
        if (!AgentToolSupport.TryParse(argumentsJson, out var root, out var error))
            return new AgentToolResult(false, string.Empty, error);

        var snapshot = await _tasks.CreateAsync(
            context.ChatId,
            context.AgentRunId,
            TaskToolSchemas.ReadTaskInput(root),
            AgentTaskSources.TaskCreate,
            ct);

        var background = TaskToolSchemas.GetBool(root, "background");
        if (!background)
            return new AgentToolResult(true, TaskToolSchemas.FormatTasks("Task created", [snapshot]));

        var command = TaskToolSchemas.GetString(root, "command");
        var prompt = TaskToolSchemas.GetString(root, "prompt");
        var outputPath = BuildBackgroundOutputPath(_sandbox.GetSandboxRoot(context.ChatId), snapshot.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(outputPath, BuildBackgroundHeader(snapshot, prompt, command), ct);

        await _background.CreateAsync(
            context.ChatId,
            snapshot.Title,
            async token =>
            {
                if (!string.IsNullOrWhiteSpace(command))
                {
                    var result = await _sandbox.ExecuteAsync(
                        context.ChatId,
                        command,
                        new SandboxCommandOptions(context.TimeoutSeconds, Math.Max(context.MaxOutputChars, 20_000)),
                        token);
                    var body = $"""

                    ## Command result

                    Exit code: {result.ExitCode}
                    Timed out: {result.TimedOut}
                    Blocked: {result.WasBlocked}

                    stdout:
                    {result.StandardOutput}

                    stderr:
                    {result.StandardError}
                    """;
                    await File.AppendAllTextAsync(outputPath, body, token);
                    return;
                }

                await File.AppendAllTextAsync(
                    outputPath,
                    "\n## Local agent\n\nThe task is queued as a local background agent placeholder. Full autonomous subagent execution is reserved for the next worktree-backed iteration.\n",
                    token);
            },
            ct,
            taskId: snapshot.Id,
            kind: string.IsNullOrWhiteSpace(command) ? "agent" : "shell",
            outputPath: outputPath,
            inputJson: argumentsJson);

        var relative = Path.GetRelativePath(_sandbox.GetSandboxRoot(context.ChatId), outputPath);
        return new AgentToolResult(true,
            $"{TaskToolSchemas.FormatTasks("Background task created", [snapshot])}\nOutput file: {relative}");
    }

    private static string BuildBackgroundOutputPath(string root, Guid id) =>
        Path.Combine(root, ".tlah_context", "background-tasks", $"{id:N}.md");

    private static string BuildBackgroundHeader(AgentTaskSnapshot task, string prompt, string command) =>
        $"""
        # Background Task {task.Id}

        Title: {task.Title}
        Status: running
        Created: {DateTime.UtcNow:O}

        ## Prompt

        {prompt}

        ## Command

        {command}
        """;
}

public sealed class TaskUpdateAgentTool : IAgentTool
{
    private readonly IAgentTaskService _tasks;

    public TaskUpdateAgentTool(IAgentTaskService tasks)
    {
        _tasks = tasks;
    }

    public LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        AgentToolNames.TaskUpdate,
        "Update a persistent task status, title, description, priority, or metadata.",
        TaskToolSchemas.TaskProperties(includeId: true),
        ["id"]);

    public bool RequiresApproval => false;

    public async Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct = default)
    {
        if (!AgentToolSupport.TryParse(argumentsJson, out var root, out var error))
            return new AgentToolResult(false, string.Empty, error);
        var idText = TaskToolSchemas.GetString(root, "id");
        if (!Guid.TryParse(idText, out var id))
            return new AgentToolResult(false, string.Empty, "id must be a task GUID.");

        var updated = await _tasks.UpdateAsync(context.ChatId, id, TaskToolSchemas.ReadTaskInput(root), ct);
        if (updated == null)
            return new AgentToolResult(false, string.Empty, $"Task {id} was not found.");

        return new AgentToolResult(true, TaskToolSchemas.FormatTasks("Task updated", [updated]));
    }
}

public sealed class TaskListAgentTool : IAgentTool
{
    private readonly IAgentTaskService _tasks;

    public TaskListAgentTool(IAgentTaskService tasks)
    {
        _tasks = tasks;
    }

    public LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        AgentToolNames.TaskList,
        "List persistent tasks for the current chat.",
        new Dictionary<string, object>
        {
            ["include_completed"] = AgentToolSupport.BooleanProperty("Include completed and cancelled tasks."),
            ["limit"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Maximum tasks to return." }
        });

    public bool RequiresApproval => false;

    public async Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct = default)
    {
        if (!AgentToolSupport.TryParse(argumentsJson, out var root, out var error))
            return new AgentToolResult(false, string.Empty, error);
        var tasks = await _tasks.ListAsync(
            context.ChatId,
            TaskToolSchemas.GetBool(root, "include_completed"),
            TaskToolSchemas.GetInt(root, "limit", 80),
            ct);
        return new AgentToolResult(true, TaskToolSchemas.FormatTasks("Tracked tasks", tasks));
    }
}

public sealed class ReadPersistedOutputAgentTool : IAgentTool
{
    private readonly ISandboxCommandService _sandbox;
    private readonly DbContext? _db;

    public ReadPersistedOutputAgentTool(ISandboxCommandService sandbox, DbContext? db = null)
    {
        _sandbox = sandbox;
        _db = db;
    }

    public LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        AgentToolNames.ReadPersistedOutput,
        "Read a large tool output previously persisted under .tlah_context/tool-results.",
        new Dictionary<string, object>
        {
            ["path"] = AgentToolSupport.StringProperty("Relative path under .tlah_context/tool-results (or use tool_call_id instead)."),
            ["tool_call_id"] = AgentToolSupport.StringProperty("Tool call ID to look up persisted output (alternative to path)."),
            ["max_chars"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Maximum characters to return." }
        },
        ["path"]);

    public bool RequiresApproval => false;

    public async Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct = default)
    {
        if (!AgentToolSupport.TryParse(argumentsJson, out var root, out var error))
            return new AgentToolResult(false, string.Empty, error);

        // M4.8.0: Support looking up by tool_call_id (from Microcompact references).
        var toolCallId = TaskToolSchemas.GetString(root, "tool_call_id");
        if (!string.IsNullOrWhiteSpace(toolCallId))
        {
            if (_db == null)
                return new AgentToolResult(false, string.Empty,
                    "Database not available for tool_call_id lookup. Use 'path' parameter instead.");
            var inv = await _db.Set<ToolInvocation>()
                .FirstOrDefaultAsync(i => i.ProviderCallId == toolCallId, ct);
            if (inv == null)
                return new AgentToolResult(false, string.Empty, "Tool invocation not found for the given tool_call_id.");
            var safeName = string.Join("-", inv.ToolName.Split(Path.GetInvalidFileNameChars()));
            var inferredPath = Path.Combine(".tlah_context", "tool-results",
                $"{inv.Id:N}-{safeName}.txt");
            var inferredFull = AgentToolSupport.ResolveSandboxPath(_sandbox, context.ChatId, inferredPath);
            if (!File.Exists(inferredFull))
                return new AgentToolResult(false, string.Empty,
                    $"Persisted output file not found for tool_call_id {toolCallId}. Expected: {inferredPath}");
            var inferredMax = Math.Clamp(TaskToolSchemas.GetInt(root, "max_chars", 24_000), 512, 200_000);
            var inferredText = await File.ReadAllTextAsync(inferredFull, ct);
            return new AgentToolResult(true, AgentToolSupport.Limit(inferredText, inferredMax));
        }

        var path = TaskToolSchemas.GetString(root, "path").Replace('/', Path.DirectorySeparatorChar);
        if (!path.StartsWith(Path.Combine(".tlah_context", "tool-results"), StringComparison.OrdinalIgnoreCase))
            return new AgentToolResult(false, string.Empty, "path must be under .tlah_context/tool-results.");

        var fullPath = AgentToolSupport.ResolveSandboxPath(_sandbox, context.ChatId, path);
        var allowed = Path.GetFullPath(Path.Combine(_sandbox.GetSandboxRoot(context.ChatId), ".tlah_context", "tool-results"));
        var normalizedAllowed = allowed.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if ((!fullPath.Equals(normalizedAllowed, StringComparison.OrdinalIgnoreCase) &&
             !fullPath.StartsWith(normalizedAllowed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) ||
            !File.Exists(fullPath))
            return new AgentToolResult(false, string.Empty, "Persisted output was not found.");

        var max = Math.Clamp(TaskToolSchemas.GetInt(root, "max_chars", 24_000), 512, 200_000);
        var text = await File.ReadAllTextAsync(fullPath, ct);
        return new AgentToolResult(true, AgentToolSupport.Limit(text, max));
    }
}

public sealed class ToolSearchAgentTool : IAgentTool
{
    public LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        AgentToolNames.ToolSearch,
        "Search the TLAH tool catalog by natural language or tool name. Use this when you need an infrequent tool or want to discover task, MCP, code, memory, browser, or persisted-output tools.",
        new Dictionary<string, object>
        {
            ["query"] = AgentToolSupport.StringProperty("Search query."),
            ["limit"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Maximum matches to return." }
        },
        ["query"]);

    public bool RequiresApproval => false;

    public Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct = default)
    {
        if (!AgentToolSupport.TryParse(argumentsJson, out var root, out var error))
            return Task.FromResult(new AgentToolResult(false, string.Empty, error));

        var query = TaskToolSchemas.GetString(root, "query").Trim();
        var limit = Math.Clamp(TaskToolSchemas.GetInt(root, "limit", 12), 1, 40);
        var terms = query.Split([' ', ',', ';', '/', '\\', '-', '_'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matches = Catalog()
            .Select(entry => new
            {
                entry,
                display = AgentToolUx.UserFacingName(entry.Name),
                activity = AgentToolUx.ActivityDescription(entry.Name),
                meta = AgentToolMetadata.For(entry.Name, requiresApproval: false)
            })
            .Select(item => new
            {
                item,
                score = Score(item.entry.Name, item.display, item.activity, item.entry.Category, item.entry.Aliases, terms)
            })
            .Where(x => x.score > 0 || string.IsNullOrWhiteSpace(query))
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.item.entry.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(x => new
            {
                name = x.item.entry.Name,
                x.item.display,
                x.item.activity,
                category = x.item.entry.Category,
                aliases = x.item.entry.Aliases,
                readOnly = x.item.meta.IsReadOnly,
                x.item.meta.IsOpenWorld
            })
            .ToList();

        return Task.FromResult(new AgentToolResult(true, JsonSerializer.Serialize(matches, new JsonSerializerOptions { WriteIndented = true })));
    }

    private static int Score(string name, string display, string activity, string category, string aliases, string[] terms)
    {
        if (terms.Length == 0)
            return 1;
        var haystack = $"{name} {display} {activity} {category} {aliases}".ToLowerInvariant();
        return terms.Sum(t =>
        {
            var term = t.ToLowerInvariant();
            if (string.Equals(name, term, StringComparison.OrdinalIgnoreCase))
                return 12;
            if (name.Contains(term, StringComparison.OrdinalIgnoreCase))
                return 6;
            return haystack.Contains(term, StringComparison.Ordinal) ? 3 : 0;
        });
    }

    private static IReadOnlyList<ToolCatalogEntry> Catalog() =>
    [
        new(AgentToolNames.ToolSearch, "catalog", "discover find available tools capability"),
        new(AgentToolNames.TodoWrite, "planning", "todo checklist plan task status"),
        new(AgentToolNames.TaskCreate, "background task", "subtask async worker long running"),
        new(AgentToolNames.TaskUpdate, "background task", "task status title priority"),
        new(AgentToolNames.TaskList, "background task", "list todos tasks"),
        new(AgentToolNames.TaskOutput, "background task", "read background output logs"),
        new(AgentToolNames.TaskStop, "background task", "cancel stop background"),
        new(AgentToolNames.TaskSendMessage, "background task", "message ask background"),
        new(AgentToolNames.ReadPersistedOutput, "context", "large output artifact persisted read"),
        new(AgentToolNames.FileList, "file", "ls dir directory folder list"),
        new(AgentToolNames.FileRead, "file", "cat open load text"),
        new(AgentToolNames.FileWrite, "file", "create replace append save"),
        new(AgentToolNames.FileSend, "file", "attach download preview deliver"),
        new(AgentToolNames.FileSearch, "file", "search find grep regex content filename"),
        new(AgentToolNames.FileInfo, "file", "stat metadata sha256 size encoding inspect"),
        new(AgentToolNames.FileMkdir, "file", "mkdir create folder directory"),
        new(AgentToolNames.FileMove, "file", "move copy rename relocate duplicate"),
        new(AgentToolNames.FileDelete, "file", "delete remove rm erase cleanup"),
        new(AgentToolNames.CodeRead, "code", "read source lines inspect"),
        new(AgentToolNames.CodeGrep, "code", "grep search regex references"),
        new(AgentToolNames.CodeGlob, "code", "glob find files paths"),
        new(AgentToolNames.CodeSymbols, "code", "symbols outline class method function definitions"),
        new(AgentToolNames.CodeEdit, "code", "edit replace exact change"),
        new(AgentToolNames.CodeMultiEdit, "code", "multiple edits replacements"),
        new(AgentToolNames.CodeDiff, "code", "diff preview compare"),
        new(AgentToolNames.CodeApplyPatch, "code", "patch apply diff"),
        new(AgentToolNames.CodeRollback, "code", "rollback restore backup undo"),
        new(AgentToolNames.CodeDiagnostics, "code", "diagnostics lint build validate"),
        new(AgentToolNames.WebSearch, "network", "internet search web google duckduckgo"),
        new(AgentToolNames.BrowserRead, "network", "fetch page read url browser"),
        new(AgentToolNames.HttpRequest, "network", "api rest http request"),
        new(AgentToolNames.McpListTools, "mcp", "discover mcp tools"),
        new(AgentToolNames.McpListResources, "mcp", "discover mcp resources"),
        new(AgentToolNames.McpReadResource, "mcp", "read mcp resource"),
        new(AgentToolNames.McpCall, "mcp", "call mcp server tool"),
        new(AgentToolNames.MemoryRead, "memory", "project memory read"),
        new(AgentToolNames.MemoryWrite, "memory", "project memory write save"),
        new(AgentToolNames.TerminalExec, "command", "shell powershell command terminal"),
        new(AgentToolNames.Git, "git", "status diff log commit branch")
    ];

    private sealed record ToolCatalogEntry(string Name, string Category, string Aliases);
}

public sealed class TaskOutputAgentTool : IAgentTool
{
    private readonly IBackgroundTaskService _background;

    public TaskOutputAgentTool(IBackgroundTaskService background)
    {
        _background = background;
    }

    public LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        AgentToolNames.TaskOutput,
        "Read a local background task output file.",
        new Dictionary<string, object>
        {
            ["id"] = AgentToolSupport.StringProperty("Background task id."),
            ["max_chars"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Maximum characters to return." }
        },
        ["id"]);

    public bool RequiresApproval => false;

    public async Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct = default)
    {
        if (!AgentToolSupport.TryParse(argumentsJson, out var root, out var error))
            return new AgentToolResult(false, string.Empty, error);
        if (!Guid.TryParse(TaskToolSchemas.GetString(root, "id"), out var id))
            return new AgentToolResult(false, string.Empty, "id must be a task GUID.");
        var task = await _background.GetAsync(id, ct);
        if (task == null)
            return new AgentToolResult(false, string.Empty, $"Background task {id} was not found.");
        if (string.IsNullOrWhiteSpace(task.OutputPath) || !File.Exists(task.OutputPath))
            return new AgentToolResult(true, $"Task {id} status: {task.Status}. No output file is available yet.");

        var max = Math.Clamp(TaskToolSchemas.GetInt(root, "max_chars", 24_000), 512, 200_000);
        var text = await File.ReadAllTextAsync(task.OutputPath, ct);
        return new AgentToolResult(true, $"Task {id} status: {task.Status}\nOutput path: {task.OutputPath}\n\n{AgentToolSupport.Limit(text, max)}");
    }
}

public sealed class TaskStopAgentTool : IAgentTool
{
    private readonly IBackgroundTaskService _background;

    public TaskStopAgentTool(IBackgroundTaskService background)
    {
        _background = background;
    }

    public LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        AgentToolNames.TaskStop,
        "Stop a running local background task.",
        new Dictionary<string, object>
        {
            ["id"] = AgentToolSupport.StringProperty("Background task id.")
        },
        ["id"]);

    public bool RequiresApproval => false;

    public async Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct = default)
    {
        if (!AgentToolSupport.TryParse(argumentsJson, out var root, out var error))
            return new AgentToolResult(false, string.Empty, error);
        if (!Guid.TryParse(TaskToolSchemas.GetString(root, "id"), out var id))
            return new AgentToolResult(false, string.Empty, "id must be a task GUID.");
        await _background.StopAsync(id, ct);
        return new AgentToolResult(true, $"Stop requested for background task {id}.");
    }
}

public sealed class TaskSendMessageAgentTool : IAgentTool
{
    private readonly IBackgroundTaskService _background;

    public TaskSendMessageAgentTool(IBackgroundTaskService background)
    {
        _background = background;
    }

    public LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        AgentToolNames.TaskSendMessage,
        "Send a message to a local background task mailbox. 4.0 appends the message to the task output file for the worker to consume.",
        new Dictionary<string, object>
        {
            ["id"] = AgentToolSupport.StringProperty("Background task id."),
            ["message"] = AgentToolSupport.StringProperty("Message to send.")
        },
        ["id", "message"]);

    public bool RequiresApproval => false;

    public async Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct = default)
    {
        if (!AgentToolSupport.TryParse(argumentsJson, out var root, out var error))
            return new AgentToolResult(false, string.Empty, error);
        if (!Guid.TryParse(TaskToolSchemas.GetString(root, "id"), out var id))
            return new AgentToolResult(false, string.Empty, "id must be a task GUID.");
        var message = TaskToolSchemas.GetString(root, "message");
        await _background.SendMessageAsync(id, message, ct);
        return new AgentToolResult(true, $"Message sent to background task {id}.");
    }
}

internal static class TaskToolSchemas
{
    public static Dictionary<string, object> TaskProperties(bool includeId)
    {
        var props = new Dictionary<string, object>
        {
            ["title"] = AgentToolSupport.StringProperty("Task title."),
            ["description"] = AgentToolSupport.StringProperty("Task details."),
            ["status"] = AgentToolSupport.StringProperty("pending, in_progress, completed, blocked, or cancelled."),
            ["priority"] = AgentToolSupport.StringProperty("low, medium, high, or critical."),
            ["parent_task_id"] = AgentToolSupport.StringProperty("Optional parent task GUID."),
            ["metadata_json"] = AgentToolSupport.StringProperty("Optional JSON metadata.")
        };
        if (includeId)
            props["id"] = AgentToolSupport.StringProperty("Task GUID.");
        return props;
    }

    public static AgentTaskInput ReadTaskInput(JsonElement root)
    {
        Guid? id = Guid.TryParse(GetString(root, "id"), out var parsedId) ? parsedId : null;
        Guid? parent = Guid.TryParse(GetString(root, "parent_task_id"), out var parsedParent) ? parsedParent : null;
        return new AgentTaskInput(
            id,
            GetString(root, "title"),
            GetNullableString(root, "description"),
            GetNullableString(root, "status"),
            GetNullableString(root, "priority"),
            parent,
            GetString(root, "metadata_json", "{}"));
    }

    public static string FormatTasks(string title, IReadOnlyList<AgentTaskSnapshot> tasks)
    {
        var sb = new StringBuilder();
        sb.AppendLine(title);
        if (tasks.Count == 0)
        {
            sb.AppendLine("No tasks.");
            return sb.ToString().TrimEnd();
        }

        foreach (var task in tasks)
        {
            sb.AppendLine($"- [{task.Status}] {task.Title}");
            sb.AppendLine($"  id: {task.Id}");
            sb.AppendLine($"  priority: {task.Priority}; source: {task.Source}; updated: {task.UpdatedAt:O}");
            if (!string.IsNullOrWhiteSpace(task.Description))
                sb.AppendLine($"  description: {task.Description}");
        }
        return sb.ToString().TrimEnd();
    }

    public static string GetString(JsonElement root, string name, string fallback = "") =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    public static string? GetNullableString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static bool GetBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) &&
        (value.ValueKind == JsonValueKind.True ||
         (value.ValueKind == JsonValueKind.String &&
          bool.TryParse(value.GetString(), out var parsed) && parsed));

    public static int GetInt(JsonElement root, string name, int fallback) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var parsed)
            ? parsed
            : fallback;
}
