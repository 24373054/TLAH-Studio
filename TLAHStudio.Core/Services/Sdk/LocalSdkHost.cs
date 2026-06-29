using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using TLAHStudio.Core.Services.AgentRuntime;

namespace TLAHStudio.Core.Services.Sdk;

/// <summary>
/// M2.14.0: SDK request/response contracts.
/// </summary>
public sealed record SdkStartRunRequest(Guid ChatId, string Content, int MaxSteps = 48, string? Role = null);
public sealed record SdkStartRunResponse(Guid RunId, string Status);
public sealed record SdkApproveRequest(Guid InvocationId, bool Approved, string Scope = "once");
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
    private readonly ILlmService _llmService;
    private readonly IChatService _chatService;
    private readonly IAgentEventSubscriptionService _eventSubscriptions;
    private CancellationTokenSource? _cts;
    private Task? _serverTask;
    private int _port = 0;

    public bool IsRunning => _serverTask is { IsCompleted: false };
    public int Port => _port;

    public LocalSdkHost(
        ILlmService llmService,
        IChatService chatService,
        IAgentEventSubscriptionService eventSubscriptions)
    {
        _llmService = llmService;
        _chatService = chatService;
        _eventSubscriptions = eventSubscriptions;
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

    private async Task<string> HandleRequestAsync(string requestJson, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(requestJson);
            var root = doc.RootElement;
            var method = root.TryGetProperty("method", out var m) ? m.GetString() : "";
            var id = root.TryGetProperty("id", out var i) ? i.GetInt32() : 0;

            return method switch
            {
                "send_message" => await HandleSendMessage(root, ct),
                "start_run" => await HandleStartRun(root, ct),
                "approve_tool" => await HandleApprove(root, ct),
                "list_chats" => await HandleListChats(ct),
                "get_run_status" => JsonSerializer.Serialize(new SdkErrorResponse("Not implemented", 501)),
                _ => JsonSerializer.Serialize(new SdkErrorResponse($"Unknown method: {method}", 404))
            };
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new SdkErrorResponse(ex.Message, 500));
        }
    }

    private async Task<string> HandleSendMessage(JsonElement root, CancellationToken ct)
    {
        var chatId = Guid.Parse(root.GetProperty("chat_id").GetString()!);
        var content = root.GetProperty("content").GetString() ?? "";
        var result = await _llmService.SendMessageAsync(chatId, content, ct: ct);
        return JsonSerializer.Serialize(new
        {
            turn_id = result.Turn.Id,
            assistant_content = result.AssistantMessage.Content
        });
    }

    private async Task<string> HandleStartRun(JsonElement root, CancellationToken ct)
    {
        var chatId = Guid.Parse(root.GetProperty("chat_id").GetString()!);
        var content = root.GetProperty("content").GetString() ?? "";
        var maxSteps = root.TryGetProperty("max_steps", out var ms) ? ms.GetInt32() : 48;
        var result = await _llmService.RunAgentTaskAsync(chatId, content,
            options: new AgentRunOptions(MaxSteps: maxSteps), ct: ct);
        return JsonSerializer.Serialize(new
        {
            run_id = result.AgentRun?.Id,
            status = result.AgentRun?.Status,
            assistant_content = result.AssistantMessage.Content
        });
    }

    private async Task<string> HandleApprove(JsonElement root, CancellationToken ct)
    {
        var invocationId = Guid.Parse(root.GetProperty("invocation_id").GetString()!);
        var approved = root.GetProperty("approved").GetBoolean();
        var scope = root.TryGetProperty("scope", out var s) ? s.GetString() ?? "once" : "once";
        await _llmService.SetAgentToolApprovalAsync(invocationId, approved, scope, ct);
        return JsonSerializer.Serialize(new { approved, scope });
    }

    private async Task<string> HandleListChats(CancellationToken ct)
    {
        var chats = await _chatService.ListChatsAsync(ct: ct);
        return JsonSerializer.Serialize(new { chats = chats.Select(c => new { c.Id, c.Title, c.UpdatedAt, c.MessageCount }) });
    }
}
