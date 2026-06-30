using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TLAHStudio.Core.Llm;
using TLAHStudio.Core.Models;
using TLAHStudio.Core.Services;
using TLAHStudio.Core.Services.Tools;
using TLAHStudio.Core.Services.Tools.Models;

namespace TLAHStudio.Core.Tests;

public class AgentLifecyclePersistenceTests
{
    [Fact]
    public async Task RunAgentTaskAsync_PersistsLifecycleProgressAndRollbackEvents()
    {
        await using var db = TestDb.Create();
        var chatService = new ChatService(db);
        var settingsService = new SettingsService(db);
        await settingsService.UpdateGlobalSettingsAsync(new GlobalSettingsUpdateDto(
            Provider: "openai",
            ApiKey: "sk-test",
            BaseUrl: "https://api.example.com",
            Model: "model-a"));
        var chat = await chatService.CreateChatAsync("Lifecycle");

        var responses = new Queue<string>([
            """
            {
              "choices": [{
                "message": {
                  "content": null,
                  "tool_calls": [{
                    "id": "call-lifecycle",
                    "type": "function",
                    "function": {
                      "name": "fake_v3",
                      "arguments": "{\"value\":\"alpha\"}"
                    }
                  }]
                }
              }]
            }
            """,
            """
            {
              "choices": [{ "message": { "content": "Lifecycle finished." } }]
            }
            """
        ]);

        var handler = new MapHttpMessageHandler(_ => MapHttpMessageHandler.Json(HttpStatusCode.OK, responses.Dequeue()));
        using var client = new HttpClient(handler);
        var sandbox = new SandboxCommandService(
            Path.Combine(Path.GetTempPath(), "TLAHStudio.AgentLifecycle.Tests", Guid.NewGuid().ToString("N")));
        var registry = new AgentToolRegistry([new PersistingV3Tool()]);
        var service = new LlmService(
            db,
            chatService,
            settingsService,
            new StaticHttpClientFactory(client),
            sandbox,
            registry);

        var result = await service.RunAgentTaskAsync(
            chat.Id,
            "Run fake lifecycle.",
            options: new AgentRunOptions(MaxSteps: 4, AutoApproveTools: true));

        Assert.Equal(AgentRunStatuses.Completed, result.AgentRun!.Status);
        var events = await db.Set<AgentEvent>().OrderBy(e => e.SequenceNumber).ToListAsync();
        Assert.Contains(events, e => e.EventType == AgentEventTypes.ToolProgress);
        Assert.Contains(events, e => e.EventType == AgentEventTypes.ToolRollbackPlan);

        var invocation = Assert.Single(await db.Set<ToolInvocation>().ToListAsync());
        Assert.Contains("effectPlan", invocation.SafetyJson);
        Assert.Contains(events, e =>
            e.EventType == AgentEventTypes.ToolRollbackPlan &&
            e.DataJson.Contains("rollbackPlan", StringComparison.Ordinal));
    }

    private sealed class PersistingV3Tool : AgentToolV3Base
    {
        public override LlmToolDefinition Definition { get; } = new(
            "fake_v3",
            "Fake V3 tool for agent lifecycle persistence.",
            new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object>
                {
                    ["value"] = new Dictionary<string, object> { ["type"] = "string" }
                },
                ["required"] = new[] { "value" }
            });

        public override bool RequiresApproval => false;

        public override Task<ToolSafetyClassification> ClassifySafetyAsync(
            string argumentsJson,
            Guid chatId,
            ISandboxCommandService sandbox,
            CancellationToken ct = default) =>
            Task.FromResult(new ToolSafetyClassification(
                ToolSafetyLevels.Low,
                "test",
                true,
                false,
                false,
                false,
                "Fake V3 tool is safe.",
                null,
                null));

        public override Task<ToolEffectPlan> PlanEffectsAsync(
            string argumentsJson,
            Guid chatId,
            ISandboxCommandService sandbox,
            CancellationToken ct = default)
        {
            var value = ReadValue(argumentsJson);
            return Task.FromResult(ToolEffectPlan.Write([], [$"{value}.txt"], hasRollback: true));
        }

        public override Task<AgentToolResult> ExecuteAsync(
            AgentToolExecutionContext context,
            string argumentsJson,
            CancellationToken ct) =>
            Task.FromResult(new AgentToolResult(true, $"executed {ReadValue(argumentsJson)}"));

        public override Task<AgentToolResult> ExecuteWithProgressAsync(
            AgentToolExecutionContext context,
            string argumentsJson,
            IProgress<AgentToolProgress>? progress = null,
            CancellationToken ct = default)
        {
            progress?.Report(new AgentToolProgress("running", 50, "Fake tool halfway"));
            return ExecuteAsync(context, argumentsJson, ct);
        }

        public override Task<ToolRollbackPlan?> CreateRollbackPlanAsync(
            string argumentsJson,
            AgentToolResult result,
            CancellationToken ct = default) =>
            Task.FromResult<ToolRollbackPlan?>(new ToolRollbackPlan(
                true,
                "Delete fake output.",
                null,
                [$"{ReadValue(argumentsJson)}.txt"]));

        private static string ReadValue(string argumentsJson)
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            return doc.RootElement.GetProperty("value").GetString() ?? string.Empty;
        }
    }
}
