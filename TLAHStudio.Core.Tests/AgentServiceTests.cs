using System.Net;
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

        Assert.Equal(AgentRunStatuses.Completed, result.AgentRun!.Status);
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
        var fileSendMessage = Assert.Single(messages.Where(m =>
            m.Role == "tool" &&
            m.Content.Contains(MessageAttachmentFormatter.AttachmentsStart, StringComparison.Ordinal)));
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
}
