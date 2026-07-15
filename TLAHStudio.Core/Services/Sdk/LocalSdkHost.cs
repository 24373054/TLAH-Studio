using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TLAHStudio.Core.Services.AgentRuntime;

namespace TLAHStudio.Core.Services.Sdk;

/// <summary>
/// M2.14.0: SDK request/response contracts.
/// </summary>
public sealed record SdkStartRunRequest(Guid ChatId, string Content, int MaxSteps = 48, string? Role = null);
public sealed record SdkStartRunResponse(Guid RunId, string Status);
public sealed record SdkApproveRequest(
    Guid InvocationId,
    bool Approved,
    string Scope = "once",
    Guid? RunId = null,
    string? UpdatedArgumentsJson = null);
public sealed record SdkSendMessageRequest(Guid ChatId, string Content, string? Role = null);
public sealed record SdkErrorResponse(string Error, int Code = 400);

/// <summary>
/// M2.14.0: Local SDK host serving HTTP on localhost and named pipe.
/// Provides programmatic access to agent runs and chat operations.
/// </summary>
public interface ILocalSdkHost
{
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    bool IsRunning { get; }
    int Port { get; }
}

public class LocalSdkHost : ILocalSdkHost
{
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly ILlmService? _testLlmService;
    private readonly IChatService? _testChatService;
    private CancellationTokenSource? _cts;
    private Task? _serverTask;
    private int _port = 0;

    public bool IsRunning => _serverTask is { IsCompleted: false };
    public int Port => _port;

    public LocalSdkHost(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _port = 18280 + Random.Shared.Next(0, 100); // Random port in range
    }

    /// <summary>
    /// Test seam that avoids constructing the complete application service graph.
    /// Production always uses <see cref="IServiceScopeFactory"/> so a singleton SDK
    /// listener never captures a scoped service or DbContext.
    /// </summary>
    internal LocalSdkHost(ILlmService llmService, IChatService chatService)
    {
        _testLlmService = llmService ?? throw new ArgumentNullException(nameof(llmService));
        _testChatService = chatService ?? throw new ArgumentNullException(nameof(chatService));
        _port = 18280 + Random.Shared.Next(0, 100); // Random port in range
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        if (IsRunning) return Task.CompletedTask;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _serverTask = RunServerAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        _cts?.Cancel();
        _serverTask = null;
        return Task.CompletedTask;
    }

    private async Task RunServerAsync(CancellationToken ct)
    {
        var pipeName = "tlah-studio-sdk";
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 4,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(ct);

                var buffer = new byte[65536];
                var read = await server.ReadAsync(buffer, ct);
                var requestJson = Encoding.UTF8.GetString(buffer, 0, read);

                var response = await HandleRequestAsync(requestJson, ct);
                var responseBytes = Encoding.UTF8.GetBytes(response);
                await server.WriteAsync(responseBytes, ct);
            }
            catch (OperationCanceledException) { break; }
            catch { /* Continue listening */ }
        }
    }

    internal async Task<string> HandleRequestAsync(string requestJson, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(requestJson);
            var root = doc.RootElement;

            if (_scopeFactory != null)
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var llmService = scope.ServiceProvider.GetRequiredService<ILlmService>();
                var chatService = scope.ServiceProvider.GetRequiredService<IChatService>();
                return await DispatchRequestAsync(root, llmService, chatService, ct);
            }

            return await DispatchRequestAsync(
                root,
                _testLlmService ?? throw new InvalidOperationException("SDK LLM service is unavailable."),
                _testChatService ?? throw new InvalidOperationException("SDK chat service is unavailable."),
                ct);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new SdkErrorResponse(ex.Message, 500));
        }
    }

    private static Task<string> DispatchRequestAsync(
        JsonElement root,
        ILlmService llmService,
        IChatService chatService,
        CancellationToken ct)
    {
        var method = root.TryGetProperty("method", out var m) ? m.GetString() : "";
        return method switch
        {
            "send_message" => HandleSendMessage(root, llmService, ct),
            "start_run" => HandleStartRun(root, llmService, ct),
            "approve_tool" => HandleApprove(root, llmService, ct),
            "resume_run" => HandleResumeRun(root, llmService, ct),
            "list_chats" => HandleListChats(chatService, ct),
            "get_run_status" => HandleRunStatus(root, llmService, ct),
            "get_run_events_jsonl" => HandleRunEventsJsonl(root, llmService, ct),
            _ => Task.FromResult(JsonSerializer.Serialize(
                new SdkErrorResponse($"Unknown method: {method}", 404)))
        };
    }

    private static async Task<string> HandleSendMessage(
        JsonElement root,
        ILlmService llmService,
        CancellationToken ct)
    {
        var chatId = Guid.Parse(root.GetProperty("chat_id").GetString()!);
        var content = root.GetProperty("content").GetString() ?? "";
        var result = await llmService.SendMessageAsync(chatId, content, ct: ct);
        return JsonSerializer.Serialize(new
        {
            turn_id = result.Turn.Id,
            assistant_content = result.AssistantMessage.Content
        });
    }

    private static async Task<string> HandleStartRun(
        JsonElement root,
        ILlmService llmService,
        CancellationToken ct)
    {
        var chatId = Guid.Parse(root.GetProperty("chat_id").GetString()!);
        var content = root.GetProperty("content").GetString() ?? "";
        var result = await llmService.RunAgentTaskAsync(chatId, content,
            options: ReadRunOptions(root, preservePermissionWhenOmitted: false), ct: ct);
        return JsonSerializer.Serialize(new
        {
            run_id = result.AgentRun?.Id,
            status = result.AgentRun?.Status,
            assistant_content = result.AssistantMessage.Content
        });
    }

    private static async Task<string> HandleApprove(
        JsonElement root,
        ILlmService llmService,
        CancellationToken ct)
    {
        var invocationId = Guid.Parse(root.GetProperty("invocation_id").GetString()!);
        var approved = root.GetProperty("approved").GetBoolean();
        var scope = root.TryGetProperty("scope", out var s) ? s.GetString() ?? "once" : "once";
        var updatedArgumentsJson = root.TryGetProperty("updated_arguments_json", out var arguments)
            ? arguments.GetString()
            : null;
        await llmService.SetAgentToolApprovalAsync(
            invocationId,
            approved,
            scope,
            ct,
            updatedArgumentsJson);

        // Approval changes the persisted invocation but execution happens when
        // its run resumes. SDK clients may provide run_id for a combined
        // approve-and-continue request; otherwise make the required next action
        // explicit instead of reporting a misleading "approved" completion.
        if (root.TryGetProperty("run_id", out var runIdElement) &&
            Guid.TryParse(runIdElement.GetString(), out var runId))
        {
            var result = await llmService.ResumeAgentTaskAsync(
                runId,
                ReadRunOptions(root, preservePermissionWhenOmitted: true),
                ct);
            return JsonSerializer.Serialize(new
            {
                approved,
                scope,
                resumed = true,
                run_id = result.AgentRun?.Id ?? runId,
                status = result.AgentRun?.Status,
                assistant_content = result.AssistantMessage.Content
            });
        }

        return JsonSerializer.Serialize(new
        {
            approved,
            scope,
            resumed = false,
            requires_resume = true
        });
    }

    private static async Task<string> HandleResumeRun(
        JsonElement root,
        ILlmService llmService,
        CancellationToken ct)
    {
        var runId = Guid.Parse(root.GetProperty("run_id").GetString()!);
        var result = await llmService.ResumeAgentTaskAsync(
            runId,
            ReadRunOptions(root, preservePermissionWhenOmitted: true),
            ct);
        return JsonSerializer.Serialize(new
        {
            run_id = result.AgentRun?.Id ?? runId,
            status = result.AgentRun?.Status,
            assistant_content = result.AssistantMessage.Content
        });
    }

    private static AgentRunOptions ReadRunOptions(
        JsonElement root,
        bool preservePermissionWhenOmitted)
    {
        var maxSteps = root.TryGetProperty("max_steps", out var steps) && steps.TryGetInt32(out var parsedSteps)
            ? parsedSteps
            : 48;
        var timeoutSeconds = root.TryGetProperty("command_timeout_seconds", out var timeout) &&
                             timeout.TryGetInt32(out var parsedTimeout)
            ? parsedTimeout
            : 120;
        var autoApprove = root.TryGetProperty("auto_approve_tools", out var auto) &&
                          auto.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                          auto.GetBoolean();
        var permissionMode = root.TryGetProperty("permission_mode", out var mode)
            ? AgentPermissionModes.Normalize(mode.GetString())
            : preservePermissionWhenOmitted
                ? string.Empty
                : AgentPermissionModes.RequestApproval;

        return new AgentRunOptions(
            MaxSteps: Math.Max(1, maxSteps),
            CommandTimeoutSeconds: Math.Max(1, timeoutSeconds),
            AutoApproveTools: autoApprove,
            PermissionMode: permissionMode);
    }

    private static async Task<string> HandleListChats(IChatService chatService, CancellationToken ct)
    {
        var chats = await chatService.ListChatsAsync(ct: ct);
        return JsonSerializer.Serialize(new { chats = chats.Select(c => new { c.Id, c.Title, c.UpdatedAt, c.MessageCount }) });
    }

    private static async Task<string> HandleRunStatus(
        JsonElement root,
        ILlmService llmService,
        CancellationToken ct)
    {
        var chatId = Guid.Parse(root.GetProperty("chat_id").GetString()!);
        var runId = Guid.Parse(root.GetProperty("run_id").GetString()!);
        var runs = await llmService.GetAgentActivityAsync(chatId, ct);
        var run = runs.FirstOrDefault(r => r.Id == runId);
        return run == null
            ? JsonSerializer.Serialize(new SdkErrorResponse($"Run not found: {runId}", 404))
            : JsonSerializer.Serialize(new
            {
                run.Id,
                run.ChatId,
                run.Status,
                run.CurrentStep,
                run.MaxSteps,
                run.ErrorMessage,
                run.ArtifactCount,
                EventCount = run.Events.Count,
                TaskCount = run.Tasks?.Count ?? 0
            });
    }

    private static async Task<string> HandleRunEventsJsonl(
        JsonElement root,
        ILlmService llmService,
        CancellationToken ct)
    {
        var chatId = Guid.Parse(root.GetProperty("chat_id").GetString()!);
        var runId = Guid.Parse(root.GetProperty("run_id").GetString()!);
        var runs = await llmService.GetAgentActivityAsync(chatId, ct);
        var run = runs.FirstOrDefault(r => r.Id == runId);
        if (run == null)
            return JsonSerializer.Serialize(new SdkErrorResponse($"Run not found: {runId}", 404));

        var sb = new StringBuilder();
        foreach (var evt in run.Events.OrderBy(e => e.SequenceNumber))
        {
            sb.AppendLine(JsonSerializer.Serialize(new
            {
                type = "agent_event",
                run_id = evt.AgentRunId,
                sequence = evt.SequenceNumber,
                event_type = evt.EventType,
                severity = evt.Severity,
                summary = evt.Summary,
                data_json = evt.DataJson,
                created_at = evt.CreatedAt
            }));
        }

        return JsonSerializer.Serialize(new { run_id = runId, jsonl = sb.ToString() });
    }
}
