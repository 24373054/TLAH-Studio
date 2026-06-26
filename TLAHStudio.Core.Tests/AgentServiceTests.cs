using System.Net;
using Microsoft.EntityFrameworkCore;
using TLAHStudio.Core.Models;
using TLAHStudio.Core.Services;

namespace TLAHStudio.Core.Tests;

public class AgentServiceTests
{
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

        Assert.Equal("Done. The sandbox command created note.txt and returned agent-ok.", result.AssistantMessage.Content);
        Assert.Equal(AgentRunStatuses.Completed, result.AgentRun!.Status);
        Assert.Equal(2, handler.Requests.Count);
        var messages = await db.Set<Message>().Where(m => m.ChatId == chat.Id).OrderBy(m => m.SequenceNum).ToListAsync();
        Assert.Contains(messages, m => m.Role == "tool" && m.Content.Contains("agent-ok", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("sk-agentsecretvalue123456", result.RawRequest.RequestJson);
        Assert.Single(await db.Set<AgentRun>().ToListAsync());
        Assert.Equal(2, await db.Set<AgentStep>().CountAsync());
        Assert.Single(await db.Set<ToolInvocation>().ToListAsync());
        Assert.NotEmpty(await db.Set<AgentCheckpoint>().ToListAsync());
        Assert.Contains(await db.Set<AgentArtifact>().ToListAsync(), a => a.RelativePath == "note.txt");
        var events = await db.Set<AgentEvent>().OrderBy(e => e.SequenceNumber).ToListAsync();
        Assert.Contains(events, e => e.EventType == AgentEventTypes.ModelRequest);
        Assert.Contains(events, e => e.EventType == AgentEventTypes.ToolRequest);
        Assert.Contains(events, e => e.EventType == AgentEventTypes.ToolResult);
        Assert.Contains(events, e => e.EventType == AgentEventTypes.RunCompleted);
        Assert.Contains(progress, e => e.EventType == AgentEventTypes.ToolRequest);
        Assert.Contains(progress, e => e.EventType == AgentEventTypes.ToolResult);
        Assert.Contains(progress, e => e.EventType == AgentEventTypes.RunCompleted);
        Assert.All(progress, e => Assert.Equal(chat.Id, e.Run.ChatId));
    }

    [Fact]
    public async Task RunAgentTaskAsync_PersistsApprovalAndResumesFromCheckpoint()
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
                      "arguments": "{\"command\":\"Set-Content approved.txt 'ok'; Get-Content approved.txt\",\"reason\":\"Verify approval resume.\"}"
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

        var pending = await service.RunAgentTaskAsync(chat.Id, "Create an approved file.");

        Assert.Equal(AgentRunStatuses.AwaitingApproval, pending.AgentRun!.Status);
        Assert.NotNull(pending.AgentRun.PendingApproval);
        Assert.False(File.Exists(Path.Combine(sandbox.GetSandboxRoot(chat.Id), "approved.txt")));

        await service.SetAgentToolApprovalAsync(
            pending.AgentRun.PendingApproval!.Id,
            approved: true,
            policyScope: ToolPolicyScopes.Chat);
        var completed = await service.ResumeAgentTaskAsync(pending.AgentRun.Id);

        Assert.Equal(AgentRunStatuses.Completed, completed.AgentRun!.Status);
        Assert.Equal("Approved command completed.", completed.AssistantMessage.Content);
        Assert.True(File.Exists(Path.Combine(sandbox.GetSandboxRoot(chat.Id), "approved.txt")));
        Assert.True(await db.Set<ToolPolicyRule>().AnyAsync(
            p => p.ChatId == chat.Id &&
                 p.ToolName == "sandbox_exec" &&
                 p.Decision == ToolPolicyDecisions.Allow));
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
        Assert.Contains("Invalid tool definition", result.AgentRun.ErrorMessage);
        Assert.Contains("API Error 400", result.AssistantMessage.Content);
        Assert.DoesNotContain("Agent completed", result.AssistantMessage.Content);
        var step = Assert.Single(await db.Set<AgentStep>().ToListAsync());
        Assert.Equal("provider_error", step.Kind);
        Assert.Equal(AgentStepStatuses.Failed, step.Status);
        Assert.Contains(await db.Set<AgentEvent>().ToListAsync(), e => e.EventType == AgentEventTypes.Error);
    }

    [Fact]
    public async Task RunAgentTaskAsync_RequiresManualApprovalForHighRiskToolsEvenWhenAutoApproveIsEnabled()
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
            options: new AgentRunOptions(MaxSteps: 3, AutoApproveTools: true));

        Assert.Equal(AgentRunStatuses.AwaitingApproval, result.AgentRun!.Status);
        Assert.NotNull(result.AgentRun.PendingApproval);
        Assert.Equal("terminal_exec", result.AgentRun.PendingApproval!.ToolName);
        var invocation = Assert.Single(await db.Set<ToolInvocation>().ToListAsync());
        Assert.Equal(ToolSafetyLevels.High, invocation.SafetyLevel);
        Assert.Equal(ToolInvocationStatuses.AwaitingApproval, invocation.Status);
        Assert.Single(handler.Requests);
        Assert.Contains(await db.Set<AgentEvent>().ToListAsync(), e => e.EventType == AgentEventTypes.ApprovalRequested);
    }

    private sealed class CollectingAgentProgress(List<AgentProgressUpdate> events) : IProgress<AgentProgressUpdate>
    {
        public void Report(AgentProgressUpdate value) => events.Add(value);
    }
}
