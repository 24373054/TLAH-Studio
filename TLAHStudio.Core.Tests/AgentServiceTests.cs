using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TLAHStudio.Core.Helpers;
using TLAHStudio.Core.Llm;
using TLAHStudio.Core.Models;
using TLAHStudio.Core.Services;

namespace TLAHStudio.Core.Tests;

public class AgentServiceTests
{
    [Fact]
    public async Task RunAgentTaskAsync_InjectsOpenTaskSummaryIntoProviderRequest()
    {
        await using var db = TestDb.Create();
        var chatService = new ChatService(db);
        var settingsService = new SettingsService(db);
        await settingsService.UpdateGlobalSettingsAsync(new GlobalSettingsUpdateDto(
            Provider: "openai",
            ApiKey: "sk-agentsecretvalue123456",
            BaseUrl: "https://api.example.com",
            Model: "model-a"));
        var chat = await chatService.CreateChatAsync("Tasks");
        await new AgentTaskService(db).CreateAsync(
            chat.Id,
            null,
            new AgentTaskInput(null, "Verify runtime task injection", "Must appear in the model request.", "in_progress", "high"));

        var bodies = new List<string>();
        var handler = new MapHttpMessageHandler(request =>
        {
            using var reader = new StreamReader(request.Content!.ReadAsStream());
            bodies.Add(reader.ReadToEnd());
            return MapHttpMessageHandler.Json(HttpStatusCode.OK,
                """
                { "choices": [{ "message": { "content": "Task context received." } }] }
                """);
        });
        using var client = new HttpClient(handler);
        var sandbox = new SandboxCommandService(Path.Combine(Path.GetTempPath(), "TLAHStudio.Agent.Tests", Guid.NewGuid().ToString("N")));
        var service = new LlmService(db, chatService, settingsService, new StaticHttpClientFactory(client), sandbox);

        var result = await service.RunAgentTaskAsync(
            chat.Id,
            "Continue.",
            options: new AgentRunOptions(MaxSteps: 1));

        // The model received the task context but did not close the durable
        // in-progress task, so the completion gate must pause rather than
        // falsely report success.
        Assert.Equal(AgentRunStatuses.Paused, result.AgentRun!.Status);
        var body = Assert.Single(bodies);
        Assert.Contains("Open tracked tasks", body);
        Assert.Contains("Verify runtime task injection", body);
    }

    [Fact]
    public async Task RunAgentTaskAsync_ExecutesSandboxCommandAndContinuesToFinalAnswer()
    {
        if (!OperatingSystem.IsWindows())
            return;

        await using var db = TestDb.Create();
        var chatService = new ChatService(db);
        var settingsService = new SettingsService(db);
        await settingsService.UpdateGlobalSettingsAsync(new GlobalSettingsUpdateDto(
            Provider: "openai",
            ApiKey: "sk-agentsecretvalue123456",
            BaseUrl: "https://api.example.com",
            Model: "model-a"));
        var chat = await chatService.CreateChatAsync("Agent");

        var responses = new Queue<string>([
            """
            {
              "choices": [
                { "message": { "content": "{\"tlah_tool\":\"sandbox.exec\",\"command\":\"'agent-ok' | Set-Content note.txt; Get-Content note.txt\",\"reason\":\"Create a file and verify it.\"}" } }
              ],
              "usage": { "prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2 }
            }
            """,
            """
            {
              "choices": [
                { "message": { "content": "Done. The sandbox command created note.txt and returned agent-ok." } }
              ],
              "usage": { "prompt_tokens": 2, "completion_tokens": 2, "total_tokens": 4 }
            }
            """
        ]);

        var handler = new MapHttpMessageHandler(_ => MapHttpMessageHandler.Json(HttpStatusCode.OK, responses.Dequeue()));
        using var client = new HttpClient(handler);
        var sandbox = new SandboxCommandService(Path.Combine(Path.GetTempPath(), "TLAHStudio.Agent.Tests", Guid.NewGuid().ToString("N")));
        var service = new LlmService(db, chatService, settingsService, new StaticHttpClientFactory(client), sandbox);
        var progress = new List<AgentProgressUpdate>();

        var result = await service.RunAgentTaskAsync(
            chat.Id,
            "Create a note file.",
            options: new AgentRunOptions(
                MaxSteps: 3,
                AutoApproveTools: true,
                Progress: new CollectingAgentProgress(progress)));

        // V2 engine: content may include tool request formatting
        Assert.Contains("agent-ok", result.AssistantMessage.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AgentRunStatuses.Completed, result.AgentRun!.Status);
        Assert.Equal(2, handler.Requests.Count);
        var messages = await db.Set<Message>().Where(m => m.ChatId == chat.Id).OrderBy(m => m.SequenceNum).ToListAsync();
        Assert.Contains(messages, m => m.Role == "tool" && m.Content.Contains("agent-ok", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("sk-agentsecretvalue123456", result.RawRequest.RequestJson);
        Assert.Single(await db.Set<AgentRun>().ToListAsync());
        Assert.NotEmpty(await db.Set<AgentStep>().ToListAsync());
        Assert.True(await db.Set<ToolInvocation>().CountAsync() >= 1);
        Assert.NotEmpty(await db.Set<AgentCheckpoint>().ToListAsync());
        var events = await db.Set<AgentEvent>().OrderBy(e => e.SequenceNumber).ToListAsync();
        Assert.NotEmpty(events);
        Assert.Contains(events, e => e.EventType == AgentEventTypes.RunCompleted);
        Assert.NotEmpty(progress);
    }

    [Fact]
    public async Task RunAgentTaskAsync_PersistsApprovalAndResumesFromCheckpoint()
    {
        if (!OperatingSystem.IsWindows())
            return;

        const string toolToken = "abcdefghijklmnopqrstuvwxyz123456";

        await using var db = TestDb.Create();
        var chatService = new ChatService(db);
        var settingsService = new SettingsService(db);
        await settingsService.UpdateGlobalSettingsAsync(new GlobalSettingsUpdateDto(
            Provider: "openai",
            ApiKey: "sk-agentsecretvalue123456",
            BaseUrl: "https://api.example.com",
            Model: "model-a"));
        var chat = await chatService.CreateChatAsync("Approval");
        var responses = new Queue<string>([
            """
            {
              "choices": [{
                "message": {
                  "content": null,
                  "tool_calls": [{
                    "id": "call-1",
                    "type": "function",
                    "function": {
                      "name": "sandbox_exec",
                      "arguments": "{\"command\":\"Set-Content approved.txt 'Bearer abcdefghijklmnopqrstuvwxyz123456'; Get-Content approved.txt\",\"reason\":\"Verify approval resume.\"}"
                    }
                  }]
                }
              }]
            }
            """,
            """
            {
              "choices": [{ "message": { "content": "Approved command completed." } }]
            }
            """
        ]);
        var handler = new MapHttpMessageHandler(_ => MapHttpMessageHandler.Json(HttpStatusCode.OK, responses.Dequeue()));
        using var client = new HttpClient(handler);
        var sandbox = new SandboxCommandService(Path.Combine(Path.GetTempPath(), "TLAHStudio.Agent.Tests", Guid.NewGuid().ToString("N")));
        var service = new LlmService(db, chatService, settingsService, new StaticHttpClientFactory(client), sandbox);

        var pending = await service.RunAgentTaskAsync(
            chat.Id,
            "Create an approved file.",
            options: new AgentRunOptions(PermissionMode: AgentPermissionModes.RequestApproval));

        Assert.Equal(AgentRunStatuses.AwaitingApproval, pending.AgentRun!.Status);
        Assert.NotNull(pending.AgentRun.PendingApproval);
        Assert.False(File.Exists(Path.Combine(sandbox.GetSandboxRoot(chat.Id), "approved.txt")));
        Assert.DoesNotContain(toolToken, pending.AgentRun.PendingApproval!.ArgumentsJson, StringComparison.Ordinal);
        Assert.Contains(SecretRedactor.Redacted, pending.AgentRun.PendingApproval.ArgumentsJson, StringComparison.Ordinal);
        var storedInvocation = await db.Set<ToolInvocation>().SingleAsync();
        Assert.DoesNotContain(toolToken, storedInvocation.ArgumentsJson, StringComparison.Ordinal);
        Assert.True(ProtectedSecret.IsProtected(storedInvocation.ProtectedArgumentsJson));

        await service.SetAgentToolApprovalAsync(
            pending.AgentRun.PendingApproval!.Id,
            approved: true,
            policyScope: ToolPolicyScopes.Chat);
        var completed = await service.ResumeAgentTaskAsync(pending.AgentRun.Id);

        Assert.Equal(AgentRunStatuses.Completed, completed.AgentRun!.Status);
        // V2 engine: content format differs, verify completion with non-empty content
        Assert.True(!string.IsNullOrWhiteSpace(completed.AssistantMessage.Content));
        // Tool should have been executed (file exists) — V2 engine executes on resume
        Assert.True(File.Exists(Path.Combine(sandbox.GetSandboxRoot(chat.Id), "approved.txt")),
            "Tool should have been executed after approval and resume");
        Assert.Contains(toolToken, await File.ReadAllTextAsync(
            Path.Combine(sandbox.GetSandboxRoot(chat.Id), "approved.txt")));
        Assert.True(await db.Set<ToolPolicyRule>().AnyAsync(
            p => p.ChatId == chat.Id &&
                 p.ToolName == "sandbox_exec" &&
                 p.Decision == ToolPolicyDecisions.Allow));
    }

    [Fact]
    public async Task RunAgentTaskAsync_FileSendCreatesVisibleAttachmentAndLiveProgress()
    {
        if (!OperatingSystem.IsWindows())
            return;

        await using var db = TestDb.Create();
        var chatService = new ChatService(db);
        var settingsService = new SettingsService(db);
        await settingsService.UpdateGlobalSettingsAsync(new GlobalSettingsUpdateDto(
            Provider: "openai",
            ApiKey: "sk-agentsecretvalue123456",
            BaseUrl: "https://api.example.com",
            Model: "model-a"));
        var chat = await chatService.CreateChatAsync("File send");

        var responses = new Queue<string>([
            """
            {
              "choices": [{
                "message": {
                  "content": null,
                  "tool_calls": [
                    {
                      "id": "call-write",
                      "type": "function",
                      "function": {
                        "name": "file_write",
                        "arguments": "{\"path\":\"landscape.svg\",\"content\":\"<svg xmlns='http://www.w3.org/2000/svg' width='64' height='32'><rect width='64' height='32' fill='skyblue'/></svg>\",\"reason\":\"Create the SVG file.\"}"
                      }
                    },
                    {
                      "id": "call-send",
                      "type": "function",
                      "function": {
                        "name": "file_send",
                        "arguments": "{\"path\":\"landscape.svg\",\"caption\":\"SVG landscape\"}"
                      }
                    }
                  ]
                }
              }]
            }
            """,
            """
            {
              "choices": [
                { "message": { "content": "Done. The SVG file has been sent." } }
              ]
            }
            """
        ]);

        var handler = new MapHttpMessageHandler(_ => MapHttpMessageHandler.Json(HttpStatusCode.OK, responses.Dequeue()));
        using var client = new HttpClient(handler);
        var sandbox = new SandboxCommandService(Path.Combine(Path.GetTempPath(), "TLAHStudio.Agent.Tests", Guid.NewGuid().ToString("N")));
        var service = new LlmService(db, chatService, settingsService, new StaticHttpClientFactory(client), sandbox);
        var progress = new List<AgentProgressUpdate>();

        var result = await service.RunAgentTaskAsync(
            chat.Id,
            "Create and send an SVG.",
            options: new AgentRunOptions(
                MaxSteps: 4,
                AutoApproveTools: true,
                Progress: new CollectingAgentProgress(progress)));

        Assert.Equal(AgentRunStatuses.Completed, result.AgentRun!.Status);
        Assert.Contains(progress, p => p.EventType == AgentEventTypes.ToolRequest && p.Run.CurrentStep >= 1);
        var messages = await db.Set<Message>()
            .Where(m => m.ChatId == chat.Id)
            .OrderBy(m => m.SequenceNum)
            .ToListAsync();
        var fileSendMessage = Assert.Single(messages, m =>
            m.Role == "tool" &&
            m.Content.Contains(MessageAttachmentFormatter.AttachmentsStart, StringComparison.Ordinal));
        var parsed = MessageAttachmentFormatter.Extract(fileSendMessage.Content);
        var attachment = Assert.Single(parsed.Attachments);
        Assert.Equal("landscape.svg", attachment.RelativePath);
        Assert.Equal("image/svg+xml", attachment.ContentType);
        Assert.True(File.Exists(Path.Combine(sandbox.GetSandboxRoot(chat.Id), "landscape.svg")));
        Assert.Equal(1, await db.Set<AgentArtifact>()
            .CountAsync(a => a.AgentRunId == result.AgentRun.Id && a.RelativePath == "landscape.svg"));
    }

    [Fact]
    public async Task RunAgentTaskAsync_MarksProviderErrorsAsFailed()
    {
        await using var db = TestDb.Create();
        var chatService = new ChatService(db);
        var settingsService = new SettingsService(db);
        await settingsService.UpdateGlobalSettingsAsync(new GlobalSettingsUpdateDto(
            Provider: "openai",
            ApiKey: "sk-agentsecretvalue123456",
            BaseUrl: "https://api.example.com",
            Model: "model-a"));
        var chat = await chatService.CreateChatAsync("Provider error");
        var handler = new MapHttpMessageHandler(_ => MapHttpMessageHandler.Json(
            HttpStatusCode.BadRequest,
            """{"error":{"message":"Invalid tool definition"}}"""));
        using var client = new HttpClient(handler);
        var service = new LlmService(
            db,
            chatService,
            settingsService,
            new StaticHttpClientFactory(client));

        var result = await service.RunAgentTaskAsync(chat.Id, "Run a task.");

        Assert.Equal(AgentRunStatuses.Failed, result.AgentRun!.Status);
        Assert.NotNull(result.AgentRun.ErrorMessage);
        Assert.DoesNotContain("Agent completed", result.AssistantMessage.Content);
        var steps = await db.Set<AgentStep>().ToListAsync();
        Assert.Contains(steps, s => s.Status == AgentStepStatuses.Failed || s.Kind == "error" || s.Kind == "provider_error");
        Assert.Contains(await db.Set<AgentEvent>().ToListAsync(), e => e.EventType == AgentEventTypes.Error);
    }

    [Fact]
    public async Task RunAgentTaskAsync_RetriesTransientProviderStatusesBeforeSucceeding()
    {
        await using var db = TestDb.Create();
        var chatService = new ChatService(db);
        var settingsService = new SettingsService(db);
        await settingsService.UpdateGlobalSettingsAsync(new GlobalSettingsUpdateDto(
            Provider: "openai", ApiKey: "sk-agentsecretvalue123456",
            BaseUrl: "https://api.example.com", Model: "model-a"));
        var chat = await chatService.CreateChatAsync("Transient provider");
        var calls = 0;
        var handler = new MapHttpMessageHandler(_ =>
        {
            calls++;
            return calls switch
            {
                1 => MapHttpMessageHandler.Json(HttpStatusCode.ServiceUnavailable,
                    """{"error":{"message":"temporary outage"}}"""),
                2 => MapHttpMessageHandler.Json(HttpStatusCode.TooManyRequests,
                    """{"error":{"message":"rate limited"}}"""),
                _ => MapHttpMessageHandler.Json(HttpStatusCode.OK,
                    """{"choices":[{"message":{"content":"Recovered after retry."}}]}""")
            };
        });
        using var client = new HttpClient(handler);
        var service = new LlmService(
            db, chatService, settingsService, new StaticHttpClientFactory(client));

        var result = await service.RunAgentTaskAsync(
            chat.Id, "Retry transient provider errors.",
            options: new AgentRunOptions(MaxSteps: 2));

        Assert.Equal(AgentRunStatuses.Completed, result.AgentRun!.Status);
        Assert.Equal(3, calls);
        Assert.Contains("Recovered after retry", result.AssistantMessage.Content);
    }

    [Fact]
    public async Task RunAgentTaskAsync_ExhaustedTransientProviderFailuresPauseWithCheckpoint()
    {
        await using var db = TestDb.Create();
        var chatService = new ChatService(db);
        var settingsService = new SettingsService(db);
        await settingsService.UpdateGlobalSettingsAsync(new GlobalSettingsUpdateDto(
            Provider: "openai", ApiKey: "sk-agentsecretvalue123456",
            BaseUrl: "https://api.example.com", Model: "model-a"));
        var chat = await chatService.CreateChatAsync("Transient provider pause");
        var calls = 0;
        var handler = new MapHttpMessageHandler(_ =>
        {
            calls++;
            return MapHttpMessageHandler.Json(
                HttpStatusCode.ServiceUnavailable,
                """{"error":{"message":"temporary outage"}}""");
        });
        using var client = new HttpClient(handler);
        var service = new LlmService(
            db, chatService, settingsService, new StaticHttpClientFactory(client));

        var result = await service.RunAgentTaskAsync(
            chat.Id, "Pause after exhausted provider retries.",
            options: new AgentRunOptions(MaxSteps: 2));

        Assert.Equal(3, calls);
        Assert.Equal(AgentRunStatuses.Paused, result.AgentRun!.Status);
        Assert.NotNull(result.AgentRun.ErrorMessage);
        Assert.Contains("resume", result.AssistantMessage.Content, StringComparison.OrdinalIgnoreCase);
        var storedRun = await db.Set<AgentRun>().SingleAsync(r => r.Id == result.AgentRun.Id);
        Assert.Equal(AgentRunStatuses.Paused, storedRun.Status);
        Assert.Null(storedRun.CompletedAt);
        var checkpoint = await db.Set<AgentCheckpoint>()
            .Where(c => c.AgentRunId == storedRun.Id)
            .OrderByDescending(c => c.CreatedAt)
            .FirstAsync();
        var checkpointState = System.Text.Json.JsonSerializer.Deserialize<
            TLAHStudio.Core.Services.AgentRuntime.AgentRunState>(
                ProtectedLocalData.Reveal(checkpoint.StateJson));
        Assert.NotNull(checkpointState);
        Assert.Equal(AgentRunStatuses.Paused, checkpointState.Status);
        Assert.Equal(3, checkpointState.ConsecutiveProviderFailures);
        var events = await db.Set<AgentEvent>().ToListAsync();
        Assert.Contains(events, e => e.EventType == AgentEventTypes.RunPaused);
        Assert.DoesNotContain(events, e => e.EventType == AgentEventTypes.RunCompleted);
    }

    [Fact]
    public async Task RunAgentTaskAsync_RetriesIncompleteSuccessfulProviderResponse()
    {
        await using var db = TestDb.Create();
        var chatService = new ChatService(db);
        var settingsService = new SettingsService(db);
        await settingsService.UpdateGlobalSettingsAsync(new GlobalSettingsUpdateDto(
            Provider: "openai", ApiKey: "sk-agentsecretvalue123456",
            BaseUrl: "https://api.example.com", Model: "model-a"));
        var chat = await chatService.CreateChatAsync("Incomplete provider response");
        var calls = 0;
        var streamUpdates = new List<LlmStreamUpdate>();
        var handler = new MapHttpMessageHandler(_ =>
        {
            calls++;
            return calls < 3
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n",
                        System.Text.Encoding.UTF8,
                        "text/event-stream")
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "data: {\"choices\":[{\"delta\":{\"content\":\"Recovered from incomplete response.\"},\"finish_reason\":\"stop\"}]}\n\n" +
                        "data: [DONE]\n\n",
                        System.Text.Encoding.UTF8,
                        "text/event-stream")
                };
        });
        using var client = new HttpClient(handler);
        var service = new LlmService(
            db, chatService, settingsService, new StaticHttpClientFactory(client));

        var result = await service.RunAgentTaskAsync(
            chat.Id, "Retry an incomplete response.",
            options: new AgentRunOptions(
                MaxSteps: 2,
                OutputStream: new CollectingLlmStreamProgress(streamUpdates)));

        Assert.Equal(AgentRunStatuses.Completed, result.AgentRun!.Status);
        Assert.Equal(3, calls);
        Assert.Equal(2, streamUpdates.Count(u => u.EventType == LlmStreamEventTypes.RetryReset));
        Assert.Contains("Recovered from incomplete response", result.AssistantMessage.Content);
    }

    [Fact]
    public async Task RunAgentTaskAsync_RetriesNonUserProviderTimeout()
    {
        await using var db = TestDb.Create();
        var chatService = new ChatService(db);
        var settingsService = new SettingsService(db);
        await settingsService.UpdateGlobalSettingsAsync(new GlobalSettingsUpdateDto(
            Provider: "openai", ApiKey: "sk-agentsecretvalue123456",
            BaseUrl: "https://api.example.com", Model: "model-a"));
        var chat = await chatService.CreateChatAsync("Provider timeout");
        var calls = 0;
        var handler = new MapHttpMessageHandler(_ =>
        {
            calls++;
            if (calls < 3)
                throw new TaskCanceledException("synthetic provider timeout");
            return MapHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"choices":[{"message":{"content":"Recovered from timeout."}}]}""");
        });
        using var client = new HttpClient(handler);
        var service = new LlmService(
            db, chatService, settingsService, new StaticHttpClientFactory(client));

        var result = await service.RunAgentTaskAsync(
            chat.Id, "Retry a timeout.",
            options: new AgentRunOptions(MaxSteps: 2));

        Assert.Equal(AgentRunStatuses.Completed, result.AgentRun!.Status);
        Assert.Equal(3, calls);
        Assert.Contains("Recovered from timeout", result.AssistantMessage.Content);
    }

    [Fact]
    public async Task RunAgentTaskAsync_RetriesProviderIOException()
    {
        await using var db = TestDb.Create();
        var chatService = new ChatService(db);
        var settingsService = new SettingsService(db);
        await settingsService.UpdateGlobalSettingsAsync(new GlobalSettingsUpdateDto(
            Provider: "openai", ApiKey: "sk-agentsecretvalue123456",
            BaseUrl: "https://api.example.com", Model: "model-a"));
        var chat = await chatService.CreateChatAsync("Provider IO retry");
        var adapter = new IOExceptionThenSuccessProviderStreamAdapter();
        using var client = new HttpClient(new MapHttpMessageHandler(_ =>
            throw new InvalidOperationException("The injected provider adapter should own this call.")));
        var service = new LlmService(
            db,
            chatService,
            settingsService,
            new StaticHttpClientFactory(client),
            providerStreamAdapter: adapter);

        var result = await service.RunAgentTaskAsync(
            chat.Id,
            "Retry an interrupted provider stream.",
            options: new AgentRunOptions(MaxSteps: 2));

        Assert.Equal(AgentRunStatuses.Completed, result.AgentRun!.Status);
        Assert.Equal(3, adapter.Calls);
        Assert.Contains("Recovered from IO interruption", result.AssistantMessage.Content);
    }

    [Fact]
    public async Task RunAgentTaskAsync_ThrowingProgressObserversDoNotChangeCompletion()
    {
        await using var db = TestDb.Create();
        var chatService = new ChatService(db);
        var settingsService = new SettingsService(db);
        await settingsService.UpdateGlobalSettingsAsync(new GlobalSettingsUpdateDto(
            Provider: "openai", ApiKey: "sk-agentsecretvalue123456",
            BaseUrl: "https://api.example.com", Model: "model-a"));
        var chat = await chatService.CreateChatAsync("Throwing telemetry");
        var handler = new MapHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "data: {\"choices\":[{\"delta\":{\"content\":\"Telemetry did not own the run.\"},\"finish_reason\":\"stop\"}]}\n\n" +
                "data: [DONE]\n\n",
                System.Text.Encoding.UTF8,
                "text/event-stream")
        });
        using var client = new HttpClient(handler);
        var service = new LlmService(
            db, chatService, settingsService, new StaticHttpClientFactory(client));

        var result = await service.RunAgentTaskAsync(
            chat.Id,
            "Complete even if observers fail.",
            options: new AgentRunOptions(
                MaxSteps: 2,
                OutputStream: new ThrowingProgress<LlmStreamUpdate>(),
                Progress: new ThrowingProgress<AgentProgressUpdate>()));

        Assert.Equal(AgentRunStatuses.Completed, result.AgentRun!.Status);
        Assert.Contains("Telemetry did not own", result.AssistantMessage.Content);
    }

    [Fact]
    public async Task RunAgentTaskAsync_UncertainWriteOutcomePausesWithoutReplay()
    {
        await using var db = TestDb.Create();
        var chatService = new ChatService(db);
        var settingsService = new SettingsService(db);
        await settingsService.UpdateGlobalSettingsAsync(new GlobalSettingsUpdateDto(
            Provider: "openai", ApiKey: "sk-agentsecretvalue123456",
            BaseUrl: "https://api.example.com", Model: "model-a"));
        var chat = await chatService.CreateChatAsync("Uncertain write outcome");
        var handler = new MapHttpMessageHandler(_ => MapHttpMessageHandler.Json(
            HttpStatusCode.OK,
            """
            {"choices":[{"message":{"content":null,"tool_calls":[{
              "id":"uncertain-write-call","type":"function","function":{
                "name":"uncertain_write_test","arguments":"{}"
              }}]}}]}
            """));
        using var client = new HttpClient(handler);
        var uncertainTool = new UncertainWriteAgentTool();
        var registry = new AgentToolRegistry([uncertainTool]);
        var service = new LlmService(
            db,
            chatService,
            settingsService,
            new StaticHttpClientFactory(client),
            agentTools: registry);

        var result = await service.RunAgentTaskAsync(
            chat.Id,
            "Perform one write whose transport may disconnect.",
            options: new AgentRunOptions(MaxSteps: 2, AutoApproveTools: true));

        Assert.Equal(AgentRunStatuses.Paused, result.AgentRun!.Status);
        Assert.Equal(1, uncertainTool.ExecutionCount);
        Assert.Single(handler.Requests);
        var invocation = await db.Set<ToolInvocation>().SingleAsync();
        Assert.Equal(ToolInvocationStatuses.UnknownOutcome, invocation.Status);
        Assert.Null(invocation.CompletedAt);
        Assert.Contains("may have committed", invocation.ResultJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Do not replay", invocation.ResultJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAgentTaskAsync_ThrowingTelemetryFlushDoesNotReplaceCompletion()
    {
        await using var db = TestDb.Create();
        var chatService = new ChatService(db);
        var settingsService = new SettingsService(db);
        await settingsService.UpdateGlobalSettingsAsync(new GlobalSettingsUpdateDto(
            Provider: "openai", ApiKey: "sk-agentsecretvalue123456",
            BaseUrl: "https://api.example.com", Model: "model-a"));
        var chat = await chatService.CreateChatAsync("Throwing telemetry flush");
        var handler = new MapHttpMessageHandler(_ => MapHttpMessageHandler.Json(
            HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"The main run completed."}}]}"""));
        using var client = new HttpClient(handler);
        var service = new LlmService(
            db,
            chatService,
            settingsService,
            new StaticHttpClientFactory(client),
            agentEventStream: new ThrowingFlushEventStream());

        var result = await service.RunAgentTaskAsync(
            chat.Id,
            "Ignore telemetry flush failure.",
            options: new AgentRunOptions(MaxSteps: 2));

        Assert.Equal(AgentRunStatuses.Completed, result.AgentRun!.Status);
        Assert.Contains("main run completed", result.AssistantMessage.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAgentTaskAsync_UncertainReadOnlyOutcomeRemainsOrdinaryFailure()
    {
        await using var db = TestDb.Create();
        var chatService = new ChatService(db);
        var settingsService = new SettingsService(db);
        await settingsService.UpdateGlobalSettingsAsync(new GlobalSettingsUpdateDto(
            Provider: "openai", ApiKey: "sk-agentsecretvalue123456",
            BaseUrl: "https://api.example.com", Model: "model-a"));
        var chat = await chatService.CreateChatAsync("Uncertain read outcome");
        var handler = new MapHttpMessageHandler(_ => MapHttpMessageHandler.Json(
            HttpStatusCode.OK,
            """
            {"choices":[{"message":{"content":null,"tool_calls":[{
              "id":"uncertain-read-call","type":"function","function":{
                "name":"uncertain_read_test","arguments":"{}"
              }}]}}]}
            """));
        using var client = new HttpClient(handler);
        var uncertainTool = new UncertainReadAgentTool();
        var service = new LlmService(
            db,
            chatService,
            settingsService,
            new StaticHttpClientFactory(client),
            agentTools: new AgentToolRegistry([uncertainTool]));

        var result = await service.RunAgentTaskAsync(
            chat.Id,
            "Perform one uncertain read.",
            options: new AgentRunOptions(MaxSteps: 1, AutoApproveTools: true));

        Assert.Equal(AgentRunStatuses.Paused, result.AgentRun!.Status);
        Assert.Equal(1, uncertainTool.ExecutionCount);
        var invocation = await db.Set<ToolInvocation>().SingleAsync();
        Assert.Equal(ToolInvocationStatuses.Failed, invocation.Status);
        Assert.NotNull(invocation.CompletedAt);
    }

    [Fact]
    public async Task RunAgentTaskAsync_UnresolvedToolFailureAsksUserInsteadOfCompleting()
    {
        if (!OperatingSystem.IsWindows())
            return;

        await using var db = TestDb.Create();
        var chatService = new ChatService(db);
        var settingsService = new SettingsService(db);
        await settingsService.UpdateGlobalSettingsAsync(new GlobalSettingsUpdateDto(
            Provider: "openai", ApiKey: "sk-agentsecretvalue123456",
            BaseUrl: "https://api.example.com", Model: "model-a"));
        var chat = await chatService.CreateChatAsync("Failure recovery");
        var responses = new Queue<string>([
            """
            {"choices":[{"message":{"content":null,"tool_calls":[{
              "id":"call-fail","type":"function","function":{
                "name":"sandbox_exec",
                "arguments":"{\"command\":\"Write-Error 'boom'; exit 1\",\"reason\":\"Exercise recovery.\"}"
              }}]}}]}
            """,
            """{"choices":[{"message":{"content":"The command failed, so I am stopping."}}]}""",
            """{"choices":[{"message":{"content":"I still cannot recover automatically."}}]}"""
        ]);
        var handler = new MapHttpMessageHandler(_ =>
            MapHttpMessageHandler.Json(HttpStatusCode.OK, responses.Dequeue()));
        using var client = new HttpClient(handler);
        var sandbox = new SandboxCommandService(
            Path.Combine(Path.GetTempPath(), "TLAHStudio.Agent.Tests", Guid.NewGuid().ToString("N")));
        var service = new LlmService(
            db, chatService, settingsService, new StaticHttpClientFactory(client), sandbox);

        var result = await service.RunAgentTaskAsync(
            chat.Id,
            "Run a command and recover if it fails.",
            options: new AgentRunOptions(MaxSteps: 6, AutoApproveTools: true));

        Assert.Equal(AgentRunStatuses.AwaitingApproval, result.AgentRun!.Status);
        Assert.Equal(AgentToolNames.AskUserQuestion, result.AgentRun.PendingApproval!.ToolName);
        Assert.Empty(responses);
        Assert.DoesNotContain(
            await db.Set<AgentEvent>().ToListAsync(),
            e => e.EventType == AgentEventTypes.RunCompleted);
    }

    [Fact]
    public async Task RunAgentTaskAsync_SuppressesIdenticalFailedInvocationAndAsksUser()
    {
        if (!OperatingSystem.IsWindows())
            return;

        await using var db = TestDb.Create();
        var chatService = new ChatService(db);
        var settingsService = new SettingsService(db);
        await settingsService.UpdateGlobalSettingsAsync(new GlobalSettingsUpdateDto(
            Provider: "openai", ApiKey: "sk-agentsecretvalue123456",
            BaseUrl: "https://api.example.com", Model: "model-a"));
        var chat = await chatService.CreateChatAsync("Repeated failure suppression");
        const string arguments =
            "{\"command\":\"'x' | Add-Content attempts.txt; exit 1\",\"reason\":\"Exercise repeated failure suppression.\"}";
        string ToolCallResponse(string id) => System.Text.Json.JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        content = (string?)null,
                        tool_calls = new[]
                        {
                            new
                            {
                                id,
                                type = "function",
                                function = new { name = "sandbox_exec", arguments }
                            }
                        }
                    }
                }
            }
        });
        var responses = new Queue<string>([
            ToolCallResponse("call-fail-1"),
            ToolCallResponse("call-fail-2"),
            """{"choices":[{"message":{"content":"The repeated command is still failing."}}]}""",
            """{"choices":[{"message":{"content":"I need the user to choose another route."}}]}"""
        ]);
        var handler = new MapHttpMessageHandler(_ =>
            MapHttpMessageHandler.Json(HttpStatusCode.OK, responses.Dequeue()));
        using var client = new HttpClient(handler);
        var sandbox = new SandboxCommandService(
            Path.Combine(Path.GetTempPath(), "TLAHStudio.Agent.Tests", Guid.NewGuid().ToString("N")));
        var service = new LlmService(
            db, chatService, settingsService, new StaticHttpClientFactory(client), sandbox);

        var result = await service.RunAgentTaskAsync(
            chat.Id,
            "Do not loop forever on the same failed command.",
            options: new AgentRunOptions(MaxSteps: 6, AutoApproveTools: true));

        Assert.Equal(AgentRunStatuses.AwaitingApproval, result.AgentRun!.Status);
        Assert.Equal(AgentToolNames.AskUserQuestion, result.AgentRun.PendingApproval!.ToolName);
        var attemptsPath = Path.Combine(sandbox.GetSandboxRoot(chat.Id), "attempts.txt");
        Assert.True(File.Exists(attemptsPath));
        Assert.Single(await File.ReadAllLinesAsync(attemptsPath));
        Assert.Contains(
            await db.Set<AgentEvent>().ToListAsync(),
            e => e.EventType == AgentEventTypes.ProtocolRepair &&
                 e.Summary.Contains("identical failed invocation", StringComparison.OrdinalIgnoreCase));
        Assert.Single(
            await db.Set<ToolInvocation>()
                .Where(i => i.ToolName == AgentToolNames.SandboxExec)
                .ToListAsync());
    }

    [Fact]
    public async Task RunAgentTaskAsync_HousekeepingSuccessDoesNotEraseUnresolvedFailure()
    {
        if (!OperatingSystem.IsWindows())
            return;

        await using var db = TestDb.Create();
        var chatService = new ChatService(db);
        var settingsService = new SettingsService(db);
        await settingsService.UpdateGlobalSettingsAsync(new GlobalSettingsUpdateDto(
            Provider: "openai", ApiKey: "sk-agentsecretvalue123456",
            BaseUrl: "https://api.example.com", Model: "model-a"));
        var chat = await chatService.CreateChatAsync("Housekeeping recovery gate");
        var responses = new Queue<string>([
            """{"choices":[{"message":{"content":null,"tool_calls":[{"id":"fail","type":"function","function":{"name":"sandbox_exec","arguments":"{\"command\":\"exit 1\",\"reason\":\"Create an unresolved failure.\"}"}}]}}]}""",
            """{"choices":[{"message":{"content":null,"tool_calls":[{"id":"list","type":"function","function":{"name":"file_list","arguments":"{\"path\":\".\",\"recursive\":false}"}}]}}]}""",
            """{"choices":[{"message":{"content":"I listed the directory, so I am done."}}]}""",
            """{"choices":[{"message":{"content":"No recovery action was completed."}}]}"""
        ]);
        var handler = new MapHttpMessageHandler(_ =>
            MapHttpMessageHandler.Json(HttpStatusCode.OK, responses.Dequeue()));
        using var client = new HttpClient(handler);
        var sandbox = new SandboxCommandService(
            Path.Combine(Path.GetTempPath(), "TLAHStudio.Agent.Tests", Guid.NewGuid().ToString("N")));
        var service = new LlmService(
            db, chatService, settingsService, new StaticHttpClientFactory(client), sandbox);

        var result = await service.RunAgentTaskAsync(
            chat.Id,
            "Do not confuse housekeeping with recovery.",
            options: new AgentRunOptions(MaxSteps: 6, AutoApproveTools: true));

        Assert.Equal(AgentRunStatuses.AwaitingApproval, result.AgentRun!.Status);
        Assert.Equal(AgentToolNames.AskUserQuestion, result.AgentRun.PendingApproval!.ToolName);
        Assert.DoesNotContain(
            await db.Set<AgentEvent>().ToListAsync(),
            e => e.EventType == AgentEventTypes.RunCompleted);
    }

    [Fact]
    public async Task RunAgentTaskAsync_ReadOnlyTerminalDiagnosticDoesNotEraseUnresolvedFailure()
    {
        if (!OperatingSystem.IsWindows())
            return;

        await using var db = TestDb.Create();
        var chatService = new ChatService(db);
        var settingsService = new SettingsService(db);
        await settingsService.UpdateGlobalSettingsAsync(new GlobalSettingsUpdateDto(
            Provider: "openai", ApiKey: "sk-agentsecretvalue123456",
            BaseUrl: "https://api.example.com", Model: "model-a"));
        var chat = await chatService.CreateChatAsync("Read-only recovery gate");
        var responses = new Queue<string>([
            """{"choices":[{"message":{"content":null,"tool_calls":[{"id":"fail","type":"function","function":{"name":"sandbox_exec","arguments":"{\"command\":\"exit 1\",\"reason\":\"Create an unresolved failure.\"}"}}]}}]}""",
            """{"choices":[{"message":{"content":null,"tool_calls":[{"id":"inspect","type":"function","function":{"name":"sandbox_exec","arguments":"{\"command\":\"Get-Command dotnet\",\"reason\":\"Inspect the environment after the failure.\"}"}}]}}]}""",
            """{"choices":[{"message":{"content":"The diagnostic succeeded, so I am done."}}]}""",
            """{"choices":[{"message":{"content":"The failed operation is still unresolved."}}]}"""
        ]);
        var handler = new MapHttpMessageHandler(_ =>
            MapHttpMessageHandler.Json(HttpStatusCode.OK, responses.Dequeue()));
        using var client = new HttpClient(handler);
        var sandbox = new SandboxCommandService(
            Path.Combine(Path.GetTempPath(), "TLAHStudio.Agent.Tests", Guid.NewGuid().ToString("N")));
        var service = new LlmService(
            db, chatService, settingsService, new StaticHttpClientFactory(client), sandbox);

        var result = await service.RunAgentTaskAsync(
            chat.Id,
            "Do not confuse a terminal diagnostic with recovery.",
            options: new AgentRunOptions(MaxSteps: 6, AutoApproveTools: true));

        Assert.Equal(AgentRunStatuses.AwaitingApproval, result.AgentRun!.Status);
        Assert.Equal(AgentToolNames.AskUserQuestion, result.AgentRun.PendingApproval!.ToolName);
        Assert.Empty(responses);
        Assert.DoesNotContain(
            await db.Set<AgentEvent>().ToListAsync(),
            e => e.EventType == AgentEventTypes.RunCompleted);
    }

    [Fact]
    public async Task RunAgentTaskAsync_ReadOnlyResearchFallbackResolvesFailedRead()
    {
        await using var db = TestDb.Create();
        var chatService = new ChatService(db);
        var settingsService = new SettingsService(db);
        await settingsService.UpdateGlobalSettingsAsync(new GlobalSettingsUpdateDto(
            Provider: "openai", ApiKey: "sk-agentsecretvalue123456",
            BaseUrl: "https://api.example.com", Model: "model-a"));
        var chat = await chatService.CreateChatAsync("Read-only research recovery");
        var responses = new Queue<string>([
            """{"choices":[{"message":{"content":null,"tool_calls":[{"id":"search-fail","type":"function","function":{"name":"web_search","arguments":"{\"query\":\"verified source\"}"}}]}}]}""",
            """{"choices":[{"message":{"content":null,"tool_calls":[{"id":"read-success","type":"function","function":{"name":"browser_read","arguments":"{\"url\":\"https://example.test/source\"}"}}]}}]}""",
            """{"choices":[{"message":{"content":"Recovered with a direct source read."}}]}"""
        ]);
        var handler = new MapHttpMessageHandler(_ =>
            MapHttpMessageHandler.Json(HttpStatusCode.OK, responses.Dequeue()));
        using var client = new HttpClient(handler);
        var sandbox = new SandboxCommandService(
            Path.Combine(Path.GetTempPath(), "TLAHStudio.Agent.Tests", Guid.NewGuid().ToString("N")));
        var registry = new AgentToolRegistry([
            new StaticReadOnlyAgentTool(
                AgentToolNames.WebSearch,
                new AgentToolResult(
                    false,
                    string.Empty,
                    "Search endpoint was temporarily unavailable.",
                    ErrorCode: "network_unavailable",
                    Retryable: true)),
            new StaticReadOnlyAgentTool(
                AgentToolNames.BrowserRead,
                new AgentToolResult(true, "Authoritative source content."))
        ]);
        var service = new LlmService(
            db, chatService, settingsService,
            new StaticHttpClientFactory(client), sandbox, registry);

        var result = await service.RunAgentTaskAsync(
            chat.Id,
            "Find and verify the source even if search needs a fallback.",
            options: new AgentRunOptions(MaxSteps: 5, AutoApproveTools: true));

        Assert.Equal(AgentRunStatuses.Completed, result.AgentRun!.Status);
        Assert.Equal("Recovered with a direct source read.", result.AssistantMessage.Content);
        Assert.Empty(responses);
        Assert.Contains(
            await db.Set<AgentEvent>().ToListAsync(),
            e => e.EventType == AgentEventTypes.RunCompleted);
    }

    [Fact]
    public async Task RunAgentTaskAsync_UnresolvedFailureAtBudgetPausesWithoutFalseSummary()
    {
        if (!OperatingSystem.IsWindows())
            return;

        await using var db = TestDb.Create();
        var chatService = new ChatService(db);
        var settingsService = new SettingsService(db);
        await settingsService.UpdateGlobalSettingsAsync(new GlobalSettingsUpdateDto(
            Provider: "openai", ApiKey: "sk-agentsecretvalue123456",
            BaseUrl: "https://api.example.com", Model: "model-a"));
        var chat = await chatService.CreateChatAsync("Failure at budget");
        var handler = new MapHttpMessageHandler(_ => MapHttpMessageHandler.Json(
            HttpStatusCode.OK,
            """{"choices":[{"message":{"content":null,"tool_calls":[{"id":"fail","type":"function","function":{"name":"sandbox_exec","arguments":"{\"command\":\"exit 1\",\"reason\":\"Fail at the step boundary.\"}"}}]}}]}"""));
        using var client = new HttpClient(handler);
        var sandbox = new SandboxCommandService(
            Path.Combine(Path.GetTempPath(), "TLAHStudio.Agent.Tests", Guid.NewGuid().ToString("N")));
        var service = new LlmService(
            db, chatService, settingsService, new StaticHttpClientFactory(client), sandbox);

        var result = await service.RunAgentTaskAsync(
            chat.Id,
            "Pause rather than claim success.",
            options: new AgentRunOptions(MaxSteps: 1, AutoApproveTools: true));

        Assert.Equal(AgentRunStatuses.Paused, result.AgentRun!.Status);
        Assert.Single(handler.Requests);
        Assert.Contains("unresolved failure", result.AssistantMessage.Content, StringComparison.OrdinalIgnoreCase);
        var events = await db.Set<AgentEvent>().ToListAsync();
        Assert.Contains(events, e => e.EventType == AgentEventTypes.RunPaused);
        Assert.DoesNotContain(events, e => e.EventType == AgentEventTypes.RunCompleted);
    }

    [Fact]
    public async Task RunAgentTaskAsync_FinalizesWithSummaryWhenStepBudgetIsReached()
    {
        if (!OperatingSystem.IsWindows())
            return;

        await using var db = TestDb.Create();
        var chatService = new ChatService(db);
        var settingsService = new SettingsService(db);
        await settingsService.UpdateGlobalSettingsAsync(new GlobalSettingsUpdateDto(
            Provider: "openai",
            ApiKey: "sk-agentsecretvalue123456",
            BaseUrl: "https://api.example.com",
            Model: "model-a"));
        var chat = await chatService.CreateChatAsync("Step budget");

        var responses = new Queue<string>([
            """
            {
              "choices": [ {
                "message": {
                  "content": null,
                  "tool_calls": [ {
                    "id": "call-budget",
                    "type": "function",
                    "function": {
                      "name": "sandbox_exec",
                      "arguments": "{\"command\":\"'budget-ok' | Set-Content budget.txt; Get-Content budget.txt\",\"reason\":\"Create one artifact before the budget is reached.\"}"
                    }
                  } ]
                }
              } ]
            }
            """,
            """
            {
              "choices": [
                { "message": { "content": "I created budget.txt and confirmed it contains budget-ok." } }
              ]
            }
            """
        ]);

        var handler = new MapHttpMessageHandler(_ => MapHttpMessageHandler.Json(HttpStatusCode.OK, responses.Dequeue()));
        using var client = new HttpClient(handler);
        var sandbox = new SandboxCommandService(Path.Combine(Path.GetTempPath(), "TLAHStudio.Agent.Tests", Guid.NewGuid().ToString("N")));
        var service = new LlmService(db, chatService, settingsService, new StaticHttpClientFactory(client), sandbox);

        var result = await service.RunAgentTaskAsync(
            chat.Id,
            "Do one thing and report back.",
            options: new AgentRunOptions(MaxSteps: 1, AutoApproveTools: true));

        Assert.Equal(AgentRunStatuses.Completed, result.AgentRun!.Status);
        // V2 engine: content format differs, verify content contains expected text
        Assert.Contains("budget-ok", result.AssistantMessage.Content, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.AgentRun.CurrentStep >= 1);
        var allSteps = await db.Set<AgentStep>().ToListAsync();
        Assert.NotEmpty(allSteps);
        Assert.Contains(allSteps, s => s.Kind == "final" || s.Kind == "final_summary");
        Assert.Contains(await db.Set<AgentEvent>().ToListAsync(), e => e.EventType == AgentEventTypes.RunCompleted);
    }

    [Fact]
    public async Task RunAgentTaskAsync_RequestApprovalModeRequiresManualApprovalForHighRiskTools()
    {
        await using var db = TestDb.Create();
        var chatService = new ChatService(db);
        var settingsService = new SettingsService(db);
        await settingsService.UpdateGlobalSettingsAsync(new GlobalSettingsUpdateDto(
            Provider: "openai",
            ApiKey: "sk-agentsecretvalue123456",
            BaseUrl: "https://api.example.com",
            Model: "model-a"));
        var chat = await chatService.CreateChatAsync("High risk approval");
        var handler = new MapHttpMessageHandler(_ => MapHttpMessageHandler.Json(HttpStatusCode.OK, """
        {
          "choices": [{
            "message": {
              "content": null,
              "tool_calls": [{
                "id": "call-risky",
                "type": "function",
                "function": {
                  "name": "terminal_exec",
                  "arguments": "{\"command\":\"git reset --hard\",\"reason\":\"Reset the sandbox repository.\"}"
                }
              }]
            }
          }]
        }
        """));
        using var client = new HttpClient(handler);
        var sandbox = new SandboxCommandService(Path.Combine(Path.GetTempPath(), "TLAHStudio.Agent.Tests", Guid.NewGuid().ToString("N")));
        var service = new LlmService(db, chatService, settingsService, new StaticHttpClientFactory(client), sandbox);

        var result = await service.RunAgentTaskAsync(
            chat.Id,
            "Reset the repo.",
            options: new AgentRunOptions(
                MaxSteps: 3,
                PermissionMode: AgentPermissionModes.RequestApproval));

        Assert.Equal(AgentRunStatuses.AwaitingApproval, result.AgentRun!.Status);
        Assert.NotNull(result.AgentRun.PendingApproval);
        Assert.Equal("terminal_exec", result.AgentRun.PendingApproval!.ToolName);
        var invocation = Assert.Single(await db.Set<ToolInvocation>().ToListAsync());
        Assert.Equal(ToolSafetyLevels.High, invocation.SafetyLevel);
        Assert.Equal(ToolInvocationStatuses.AwaitingApproval, invocation.Status);
        Assert.Single(handler.Requests);
        Assert.Contains(await db.Set<AgentEvent>().ToListAsync(), e => e.EventType == AgentEventTypes.ApprovalRequested);
    }

    [Theory]
    [InlineData(AgentPermissionModes.AutoApprove)]
    [InlineData(AgentPermissionModes.BypassPermissions)]
    public async Task RunAgentTaskAsync_UserInteractionToolAlwaysWaitsForApproval(string permissionMode)
    {
        await using var db = TestDb.Create();
        var chatService = new ChatService(db);
        var settingsService = new SettingsService(db);
        await settingsService.UpdateGlobalSettingsAsync(new GlobalSettingsUpdateDto(
            Provider: "openai", ApiKey: "sk-agentsecretvalue123456",
            BaseUrl: "https://api.example.com", Model: "model-a"));
        var chat = await chatService.CreateChatAsync("Interaction approval");
        var handler = new MapHttpMessageHandler(_ => MapHttpMessageHandler.Json(HttpStatusCode.OK, """
        { "choices": [{ "message": { "content": null, "tool_calls": [{
          "id": "call-question", "type": "function", "function": {
            "name": "ask_user_question",
            "arguments": "{\"questions\":[{\"question\":\"Continue?\",\"header\":\"Confirm\",\"options\":[{\"label\":\"Yes\",\"description\":\"Proceed.\"},{\"label\":\"No\",\"description\":\"Stop.\"}]}]}"
          }
        }] } }] }
        """));
        using var client = new HttpClient(handler);
        var service = new LlmService(
            db, chatService, settingsService, new StaticHttpClientFactory(client),
            new SandboxCommandService(Path.Combine(Path.GetTempPath(), "TLAHStudio.Agent.Tests", Guid.NewGuid().ToString("N"))));

        var result = await service.RunAgentTaskAsync(
            chat.Id, "Ask for confirmation.",
            options: new AgentRunOptions(MaxSteps: 1, PermissionMode: permissionMode));

        Assert.Equal(AgentRunStatuses.AwaitingApproval, result.AgentRun!.Status);
        Assert.Equal(AgentToolNames.AskUserQuestion, result.AgentRun.PendingApproval!.ToolName);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task RunAgentTaskAsync_FullAccessIsolatesUserQuestionFromSiblingTools()
    {
        await using var db = TestDb.Create();
        var chatService = new ChatService(db);
        var settingsService = new SettingsService(db);
        await settingsService.UpdateGlobalSettingsAsync(new GlobalSettingsUpdateDto(
            Provider: "openai", ApiKey: "sk-agentsecretvalue123456",
            BaseUrl: "https://api.example.com", Model: "model-a"));
        var chat = await chatService.CreateChatAsync("Full access user question batch");
        var handler = new MapHttpMessageHandler(_ => MapHttpMessageHandler.Json(HttpStatusCode.OK, """
        { "choices": [{ "message": { "content": null, "tool_calls": [
          { "id": "call-write", "type": "function", "function": {
            "name": "file_write", "arguments": "{\"path\":\"must-not-run.txt\",\"content\":\"unexpected\",\"reason\":\"Sibling call.\"}"
          }},
          { "id": "call-question", "type": "function", "function": {
            "name": "ask_user_question", "arguments": "{\"questions\":[{\"question\":\"Continue?\",\"header\":\"Confirm\",\"options\":[{\"label\":\"Yes\",\"description\":\"Proceed.\"},{\"label\":\"No\",\"description\":\"Stop.\"}]}]}"
          }}
        ] } }] }
        """));
        using var client = new HttpClient(handler);
        var sandbox = new SandboxCommandService(
            Path.Combine(Path.GetTempPath(), "TLAHStudio.Agent.Tests", Guid.NewGuid().ToString("N")));
        var service = new LlmService(
            db, chatService, settingsService, new StaticHttpClientFactory(client), sandbox);

        var result = await service.RunAgentTaskAsync(
            chat.Id,
            "Ask before continuing.",
            options: new AgentRunOptions(
                MaxSteps: 2,
                PermissionMode: AgentPermissionModes.BypassPermissions));

        Assert.Equal(AgentRunStatuses.AwaitingApproval, result.AgentRun!.Status);
        Assert.Equal(AgentToolNames.AskUserQuestion, result.AgentRun.PendingApproval!.ToolName);
        Assert.False(File.Exists(Path.Combine(sandbox.GetSandboxRoot(chat.Id), "must-not-run.txt")));
        Assert.DoesNotContain(
            await db.Set<ToolInvocation>().ToListAsync(),
            i => i.ToolName == AgentToolNames.FileWrite);
        Assert.Contains(
            await db.Set<AgentEvent>().ToListAsync(),
            e => e.EventType == AgentEventTypes.ProtocolRepair &&
                 e.Summary.Contains("Isolated user question", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAgentTaskAsync_PlanModeBlocksAllowedFileWriteUntilApproval()
    {
        await using var db = TestDb.Create();
        var chatService = new ChatService(db);
        var settingsService = new SettingsService(db);
        await settingsService.UpdateGlobalSettingsAsync(new GlobalSettingsUpdateDto(
            Provider: "openai", ApiKey: "sk-agentsecretvalue123456",
            BaseUrl: "https://api.example.com", Model: "model-a"));
        var chat = await chatService.CreateChatAsync("Plan write approval");
        await new ToolPlatformService(db).SavePolicyAsync(
            chat.Id, AgentToolNames.FileWrite, ToolPolicyScopes.Chat, ToolPolicyDecisions.Allow);
        var handler = new MapHttpMessageHandler(_ => MapHttpMessageHandler.Json(HttpStatusCode.OK, """
        { "choices": [{ "message": { "content": null, "tool_calls": [{
          "id": "call-write", "type": "function", "function": {
            "name": "file_write", "arguments": "{\"path\":\"plan-blocked.txt\",\"content\":\"blocked\",\"reason\":\"Test plan guard.\"}"
          }
        }] } }] }
        """));
        using var client = new HttpClient(handler);
        var sandbox = new SandboxCommandService(Path.Combine(Path.GetTempPath(), "TLAHStudio.Agent.Tests", Guid.NewGuid().ToString("N")));
        var service = new LlmService(db, chatService, settingsService, new StaticHttpClientFactory(client), sandbox);

        var result = await service.RunAgentTaskAsync(
            chat.Id, "Plan the change.",
            options: new AgentRunOptions(MaxSteps: 1, PermissionMode: AgentPermissionModes.Plan));

        Assert.Equal(AgentRunStatuses.AwaitingApproval, result.AgentRun!.Status);
        Assert.Equal(AgentToolNames.FileWrite, result.AgentRun.PendingApproval!.ToolName);
        Assert.False(File.Exists(Path.Combine(sandbox.GetSandboxRoot(chat.Id), "plan-blocked.txt")));
    }

    [Fact]
    public async Task ResumeAgentTaskAsync_ApprovedPlanExitRestoresRequestApprovalMode()
    {
        await using var db = TestDb.Create();
        var chatService = new ChatService(db);
        var settingsService = new SettingsService(db);
        await settingsService.UpdateGlobalSettingsAsync(new GlobalSettingsUpdateDto(
            Provider: "openai", ApiKey: "sk-agentsecretvalue123456",
            BaseUrl: "https://api.example.com", Model: "model-a"));
        var chat = await chatService.CreateChatAsync("Plan exit");
        var requestBodies = new List<string>();
        var responses = new Queue<string>([
            """
            { "choices": [{ "message": { "content": null, "tool_calls": [{
              "id": "call-exit-plan", "type": "function", "function": {
                "name": "exit_plan_mode", "arguments": "{\"plan\":\"1. Inspect.\\n2. Implement.\"}"
              }
            }] } }] }
            """,
            """{ "choices": [{ "message": { "content": "Plan approved and execution can continue." } }] }"""
        ]);
        var handler = new MapHttpMessageHandler(request =>
        {
            using var reader = new StreamReader(request.Content!.ReadAsStream());
            requestBodies.Add(reader.ReadToEnd());
            return MapHttpMessageHandler.Json(HttpStatusCode.OK, responses.Dequeue());
        });
        using var client = new HttpClient(handler);
        var sandbox = new SandboxCommandService(Path.Combine(Path.GetTempPath(), "TLAHStudio.Agent.Tests", Guid.NewGuid().ToString("N")));
        var service = new LlmService(db, chatService, settingsService, new StaticHttpClientFactory(client), sandbox);

        var pending = await service.RunAgentTaskAsync(
            chat.Id, "Prepare a plan.",
            options: new AgentRunOptions(MaxSteps: 3, PermissionMode: AgentPermissionModes.Plan));
        Assert.Equal(AgentRunStatuses.AwaitingApproval, pending.AgentRun!.Status);
        Assert.Equal(AgentToolNames.ExitPlanMode, pending.AgentRun.PendingApproval!.ToolName);

        await service.SetAgentToolApprovalAsync(pending.AgentRun.PendingApproval.Id, approved: true);
        var completed = await service.ResumeAgentTaskAsync(
            pending.AgentRun.Id,
            options: new AgentRunOptions(MaxSteps: 3, PermissionMode: AgentPermissionModes.Plan));

        Assert.Equal(AgentRunStatuses.Completed, completed.AgentRun!.Status);
        Assert.Contains("Permission mode: Ask approval", requestBodies.Last());
        var planPath = Path.Combine(sandbox.GetSandboxRoot(chat.Id), ".tlah_context", "plans", $"{chat.Id:D}-plan.md");
        Assert.Equal("1. Inspect.\n2. Implement.", await File.ReadAllTextAsync(planPath));
    }

    [Fact]
    public async Task RunAgentTaskAsync_FullAccessOverridesOrdinaryDeniedPolicy()
    {
        await using var db = TestDb.Create();
        var chatService = new ChatService(db);
        var settingsService = new SettingsService(db);
        await settingsService.UpdateGlobalSettingsAsync(new GlobalSettingsUpdateDto(
            Provider: "openai", ApiKey: "sk-agentsecretvalue123456",
            BaseUrl: "https://api.example.com", Model: "model-a"));
        var chat = await chatService.CreateChatAsync("Denied bypass");
        await new ToolPlatformService(db).SavePolicyAsync(
            chat.Id, AgentToolNames.FileWrite, ToolPolicyScopes.Chat, ToolPolicyDecisions.Deny);
        var handler = new MapHttpMessageHandler(_ => MapHttpMessageHandler.Json(HttpStatusCode.OK, """
        { "choices": [{ "message": { "content": null, "tool_calls": [{
          "id": "call-denied", "type": "function", "function": {
            "name": "file_write", "arguments": "{\"path\":\"policy-blocked.txt\",\"content\":\"blocked\",\"reason\":\"Test policy guard.\"}"
          }
        }] } }] }
        """));
        using var client = new HttpClient(handler);
        var sandbox = new SandboxCommandService(Path.Combine(Path.GetTempPath(), "TLAHStudio.Agent.Tests", Guid.NewGuid().ToString("N")));
        var service = new LlmService(db, chatService, settingsService, new StaticHttpClientFactory(client), sandbox);

        await service.RunAgentTaskAsync(
            chat.Id, "Write despite the deny rule.",
            options: new AgentRunOptions(MaxSteps: 1, PermissionMode: AgentPermissionModes.BypassPermissions));

        Assert.True(File.Exists(Path.Combine(sandbox.GetSandboxRoot(chat.Id), "policy-blocked.txt")));
        Assert.DoesNotContain(await db.Set<AgentEvent>().ToListAsync(), e => e.Summary.Contains("denied_by_policy", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAgentTaskAsync_StoredAllowReachesHostPathExecutionBoundary()
    {
        await using var db = TestDb.Create();
        var chatService = new ChatService(db);
        var settingsService = new SettingsService(db);
        await settingsService.UpdateGlobalSettingsAsync(new GlobalSettingsUpdateDto(
            Provider: "openai", ApiKey: "sk-agentsecretvalue123456",
            BaseUrl: "https://api.example.com", Model: "model-a"));
        var chat = await chatService.CreateChatAsync("Stored allow host path");
        await new ToolPlatformService(db).SavePolicyAsync(
            chat.Id,
            AgentToolNames.FileRead,
            ToolPolicyScopes.Chat,
            ToolPolicyDecisions.Allow);

        var hostRoot = Path.Combine(
            Path.GetTempPath(),
            "TLAHStudio.StoredPolicy.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(hostRoot);
        var hostFile = Path.Combine(hostRoot, "allowed.txt");
        await File.WriteAllTextAsync(hostFile, "policy-host-content");
        var argumentsJson = JsonSerializer.Serialize(new
        {
            path = hostFile,
            reason = "Verify stored policy execution propagation."
        });
        var responses = new Queue<string>([
            $$"""
            { "choices": [{ "message": { "content": null, "tool_calls": [{
              "id": "call-policy-host-read", "type": "function", "function": {
                "name": "file_read", "arguments": {{JsonSerializer.Serialize(argumentsJson)}}
              }
            }] } }] }
            """,
            """{ "choices": [{ "message": { "content": "The approved host file was read." } }] }"""
        ]);
        var handler = new MapHttpMessageHandler(_ =>
            MapHttpMessageHandler.Json(HttpStatusCode.OK, responses.Dequeue()));
        using var client = new HttpClient(handler);
        var sandbox = new SandboxCommandService(Path.Combine(
            Path.GetTempPath(), "TLAHStudio.Agent.Tests", Guid.NewGuid().ToString("N")));
        var service = new LlmService(
            db, chatService, settingsService, new StaticHttpClientFactory(client), sandbox);

        try
        {
            var result = await service.RunAgentTaskAsync(
                chat.Id,
                "Read the stored-policy host file.",
                options: new AgentRunOptions(
                    MaxSteps: 2,
                    PermissionMode: AgentPermissionModes.RequestApproval));

            Assert.Equal(AgentRunStatuses.Completed, result.AgentRun!.Status);
            Assert.Contains("policy-host-content", string.Join(
                "\n",
                await db.Set<Message>()
                    .Where(message => message.ChatId == chat.Id && message.Role == "tool")
                    .Select(message => message.Content)
                    .ToListAsync()));
            Assert.DoesNotContain(
                await db.Set<ToolInvocation>().Where(i => i.AgentRunId == result.AgentRun.Id).ToListAsync(),
                invocation => invocation.Status == ToolInvocationStatuses.AwaitingApproval);
        }
        finally
        {
            Directory.Delete(hostRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CancelAgentRunAsync_PreservesLatestPersistedStep()
    {
        await using var db = TestDb.Create();
        var chatService = new ChatService(db);
        var settingsService = new SettingsService(db);
        var chat = await chatService.CreateChatAsync("Cancel");
        var turn = new Turn { ChatId = chat.Id, TurnNumber = 1 };
        db.Set<Turn>().Add(turn);
        await db.SaveChangesAsync();
        var run = new AgentRun
        {
            ChatId = chat.Id,
            TurnId = turn.Id,
            Status = AgentRunStatuses.Running,
            UserRequest = "Long task",
            CurrentStep = 0,
            MaxSteps = 48
        };
        db.Set<AgentRun>().Add(run);
        await db.SaveChangesAsync();
        db.Set<AgentStep>().Add(new AgentStep
        {
            AgentRunId = run.Id,
            StepNumber = 5,
            Kind = "file_write",
            Status = AgentStepStatuses.Running,
            Summary = "Writing file"
        });
        await db.SaveChangesAsync();
        var service = new LlmService(db, chatService, settingsService, new StaticHttpClientFactory(new HttpClient(new MapHttpMessageHandler(_ => MapHttpMessageHandler.Json(HttpStatusCode.OK, "{}")))));

        await service.CancelAgentRunAsync(run.Id);

        var cancelled = await db.Set<AgentRun>().FirstAsync(r => r.Id == run.Id);
        Assert.Equal(AgentRunStatuses.Cancelled, cancelled.Status);
        Assert.Equal(5, cancelled.CurrentStep);
    }

    [Fact]
    public async Task GetAgentActivityAsync_ReturnsPersistentRunsAndOrderedEvents()
    {
        await using var db = TestDb.Create();
        var chatService = new ChatService(db);
        var settingsService = new SettingsService(db);
        var chat = await chatService.CreateChatAsync("Activity");
        var olderTurn = new Turn { ChatId = chat.Id, TurnNumber = 1 };
        var newerTurn = new Turn { ChatId = chat.Id, TurnNumber = 2 };
        db.Set<Turn>().AddRange(olderTurn, newerTurn);
        await db.SaveChangesAsync();

        var olderRun = new AgentRun
        {
            ChatId = chat.Id,
            TurnId = olderTurn.Id,
            Status = AgentRunStatuses.Completed,
            UserRequest = "Older request",
            CurrentStep = 2,
            MaxSteps = 48,
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-9),
            CompletedAt = DateTime.UtcNow.AddMinutes(-9)
        };
        var newerRun = new AgentRun
        {
            ChatId = chat.Id,
            TurnId = newerTurn.Id,
            Status = AgentRunStatuses.Running,
            UserRequest = "Newer request",
            CurrentStep = 1,
            MaxSteps = 48,
            CreatedAt = DateTime.UtcNow.AddMinutes(-1),
            UpdatedAt = DateTime.UtcNow
        };
        db.Set<AgentRun>().AddRange(olderRun, newerRun);
        await db.SaveChangesAsync();

        db.Set<AgentEvent>().AddRange(
            new AgentEvent
            {
                AgentRunId = newerRun.Id,
                SequenceNumber = 2,
                EventType = AgentEventTypes.ToolResult,
                Summary = "Result ready"
            },
            new AgentEvent
            {
                AgentRunId = newerRun.Id,
                SequenceNumber = 1,
                EventType = AgentEventTypes.ModelRequest,
                Summary = "Planning",
                DataJson = """{"stepNumber":1}"""
            },
            new AgentEvent
            {
                AgentRunId = olderRun.Id,
                SequenceNumber = 1,
                EventType = AgentEventTypes.RunCompleted,
                Summary = "Done"
            });
        db.Set<AgentArtifact>().Add(new AgentArtifact
        {
            AgentRunId = newerRun.Id,
            RelativePath = "result.txt",
            SizeBytes = 12,
            Sha256 = "abc"
        });
        await db.SaveChangesAsync();

        var service = new LlmService(
            db,
            chatService,
            settingsService,
            new StaticHttpClientFactory(new HttpClient(new MapHttpMessageHandler(_ =>
                MapHttpMessageHandler.Json(HttpStatusCode.OK, "{}")))));

        var activity = await service.GetAgentActivityAsync(chat.Id);

        Assert.Equal([newerRun.Id, olderRun.Id], activity.Select(r => r.Id).ToArray());
        Assert.Equal("Newer request", activity[0].UserRequest);
        Assert.Equal(1, activity[0].ArtifactCount);
        Assert.Equal([1, 2], activity[0].Events.Select(e => e.SequenceNumber).ToArray());
        Assert.Equal(AgentEventTypes.RunCompleted, Assert.Single(activity[1].Events).EventType);
    }

    private sealed class CollectingAgentProgress(List<AgentProgressUpdate> events) : IProgress<AgentProgressUpdate>
    {
        public void Report(AgentProgressUpdate value) => events.Add(value);
    }

    private sealed class CollectingLlmStreamProgress(List<LlmStreamUpdate> updates) : IProgress<LlmStreamUpdate>
    {
        public void Report(LlmStreamUpdate value) => updates.Add(value);
    }

    private sealed class ThrowingProgress<T> : IProgress<T>
    {
        public void Report(T value) =>
            throw new InvalidOperationException("Synthetic telemetry observer failure.");
    }

    private sealed class IOExceptionThenSuccessProviderStreamAdapter : IProviderStreamAdapter
    {
        public int Calls { get; private set; }

        public Task<LlmResponse> ChatAsync(
            ProviderStreamRequest request,
            CancellationToken ct = default)
        {
            Calls++;
            if (Calls < 3)
                throw new IOException("Synthetic interrupted response stream.");

            return Task.FromResult(new LlmResponse(
                new Dictionary<string, object>(),
                new Dictionary<string, object>(),
                200,
                1,
                "Recovered from IO interruption."));
        }
    }

    private sealed class UncertainWriteAgentTool : IAgentTool
    {
        public int ExecutionCount { get; private set; }

        public LlmToolDefinition Definition { get; } = new(
            "uncertain_write_test",
            "Test-only write operation whose remote outcome is uncertain.",
            new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object>()
            });

        public bool RequiresApproval => false;

        public Task<AgentToolResult> ExecuteAsync(
            AgentToolExecutionContext context,
            string argumentsJson,
            CancellationToken ct = default)
        {
            ExecutionCount++;
            return Task.FromResult(new AgentToolResult(
                false,
                string.Empty,
                "Remote connection closed after dispatch.",
                OutcomeUncertain: true,
                MayHaveCommitted: true));
        }
    }

    private sealed class ThrowingFlushEventStream : IAgentEventStream
    {
        private int _sequence;

        public Task<AgentEvent> AppendAsync(
            AgentEventAppendRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new AgentEvent
            {
                AgentRunId = request.Run.Id,
                AgentStepId = request.StepId,
                ToolInvocationId = request.ToolInvocationId,
                SequenceNumber = ++_sequence,
                EventType = request.EventType,
                Severity = request.Severity,
                Summary = request.Summary,
                DataJson = "{}"
            });

        public IDisposable BeginRun(AgentRun run) => new NoopScope();

        public Task FlushAsync(CancellationToken ct = default) =>
            throw new IOException("Synthetic telemetry flush failure.");

        public AgentEventStreamMetrics GetMetrics() => AgentEventStreamMetrics.Empty;

        private sealed class NoopScope : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    private sealed class UncertainReadAgentTool : IAgentTool
    {
        public int ExecutionCount { get; private set; }

        public LlmToolDefinition Definition { get; } = new(
            "uncertain_read_test",
            "Test-only read whose transport result is uncertain.",
            new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object>()
            });

        public bool RequiresApproval => false;

        public AgentToolMetadata Metadata { get; } = new(
            "uncertain_read_test",
            RequiresApproval: false,
            IsReadOnly: true,
            IsConcurrencySafe: true,
            IsDestructive: false,
            AgentToolRenderHints.Text,
            2_000,
            AgentToolResultPersistenceModes.Inline);

        public Task<AgentToolResult> ExecuteAsync(
            AgentToolExecutionContext context,
            string argumentsJson,
            CancellationToken ct = default)
        {
            ExecutionCount++;
            return Task.FromResult(new AgentToolResult(
                false,
                string.Empty,
                "Read response stream closed early.",
                OutcomeUncertain: true,
                MayHaveCommitted: true));
        }
    }

    private sealed class StaticReadOnlyAgentTool(
        string name,
        AgentToolResult result) : IAgentTool
    {
        public LlmToolDefinition Definition { get; } = new(
            name,
            "Test-only deterministic read operation.",
            new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object>(),
                ["additionalProperties"] = true
            });

        public bool RequiresApproval => false;

        public AgentToolMetadata Metadata { get; } = new(
            name,
            RequiresApproval: false,
            IsReadOnly: true,
            IsConcurrencySafe: true,
            IsDestructive: false,
            AgentToolRenderHints.Text,
            2_000,
            AgentToolResultPersistenceModes.Inline,
            IsOpenWorld: true);

        public Task<AgentToolResult> ExecuteAsync(
            AgentToolExecutionContext context,
            string argumentsJson,
            CancellationToken ct = default) => Task.FromResult(result);
    }
}
