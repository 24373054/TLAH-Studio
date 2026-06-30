using System.Text.Json;
using TLAHStudio.Core.Helpers;
using TLAHStudio.Core.Services.Tools;
using TLAHStudio.Core.Services.Tools.Models;

namespace TLAHStudio.Core.Services;

public sealed record ToolLifecyclePreview(
    IAgentTool? Tool,
    AgentToolMetadata Metadata,
    ToolSafetyAssessment Safety,
    ToolEffectPlan? EffectPlan,
    AgentToolResult? ValidationFailure = null);

public interface IToolLifecycleRunner
{
    Task<ToolLifecyclePreview> PreviewAsync(
        Guid chatId,
        string toolName,
        string argumentsJson,
        CancellationToken ct = default);

    Task<ToolExecutionOutcome> ExecuteAsync(
        ToolExecutionRequest request,
        CancellationToken ct = default);
}

public sealed class DefaultToolLifecycleRunner : IToolLifecycleRunner
{
    private static readonly JsonSerializerOptions PreviewJsonOptions = new() { WriteIndented = true };

    private readonly IAgentToolRegistry _registry;
    private readonly ISandboxCommandService _sandbox;
    private readonly IToolHookRegistry _hookRegistry;

    public DefaultToolLifecycleRunner(
        IAgentToolRegistry registry,
        ISandboxCommandService sandbox,
        IToolHookRegistry? hookRegistry = null)
    {
        _registry = registry;
        _sandbox = sandbox;
        _hookRegistry = hookRegistry ?? CreateDefaultHookRegistry();
    }

    public async Task<ToolLifecyclePreview> PreviewAsync(
        Guid chatId,
        string toolName,
        string argumentsJson,
        CancellationToken ct = default)
    {
        var normalizedTool = AgentToolNames.Normalize(toolName);
        if (!_registry.TryGet(normalizedTool, out var tool))
        {
            var metadata = AgentToolMetadata.For(normalizedTool, requiresApproval: true);
            var safety = ToolSafetyAssessment.Blocked(
                "tool",
                $"Unknown tool: {normalizedTool}",
                "The requested tool is not registered.");
            return new ToolLifecyclePreview(tool, metadata, safety, null);
        }

        var validation = tool.ValidateInput(argumentsJson);
        if (!validation.Success)
        {
            var safety = ToolSafetyAssessment.Blocked(
                "protocol",
                "Tool arguments failed validation.",
                validation.Error ?? "Invalid tool arguments.");
            return new ToolLifecyclePreview(
                tool,
                tool.Metadata,
                safety,
                null,
                new AgentToolResult(false, string.Empty, validation.Error));
        }

        if (tool is IAgentToolV3 v3)
        {
            var classification = await v3.ClassifySafetyAsync(argumentsJson, chatId, _sandbox, ct);
            var effectPlan = await v3.PlanEffectsAsync(argumentsJson, chatId, _sandbox, ct);
            if (classification.EffectPlan != null)
                effectPlan = classification.EffectPlan;

            return new ToolLifecyclePreview(
                tool,
                tool.Metadata,
                FromV3SafetyClassification(classification, effectPlan),
                effectPlan);
        }

        var safetyAssessment = ToolSafetyKernel.Assess(
            _sandbox,
            chatId,
            normalizedTool,
            argumentsJson);
        if (safetyAssessment.IsBlocked &&
            string.Equals(safetyAssessment.Category, "tool", StringComparison.OrdinalIgnoreCase))
        {
            safetyAssessment = SafetyFromRegisteredToolMetadata(tool);
        }
        return new ToolLifecyclePreview(
            tool,
            tool.Metadata,
            safetyAssessment,
            EffectPlanFromLegacySafety(safetyAssessment));
    }

    public async Task<ToolExecutionOutcome> ExecuteAsync(
        ToolExecutionRequest request,
        CancellationToken ct = default)
    {
        var preview = await PreviewAsync(
            request.Run.ChatId,
            request.Invocation.ToolName,
            request.Invocation.ArgumentsJson,
            ct);

        if (preview.Tool == null)
        {
            return new ToolExecutionOutcome(
                request,
                null,
                preview.Metadata,
                preview.Safety,
                new AgentToolResult(false, string.Empty, preview.Safety.Warning ?? preview.Safety.Summary),
                preview.EffectPlan);
        }

        if (preview.ValidationFailure != null)
        {
            return new ToolExecutionOutcome(
                request,
                preview.Tool,
                preview.Metadata,
                preview.Safety,
                preview.ValidationFailure,
                preview.EffectPlan);
        }

        var tool = preview.Tool;
        var progressEvents = new List<AgentToolProgress>();
        var beforeHook = await RunHooksAsync(
            tool,
            ToolHookTriggers.BeforeUse,
            new ToolHookContext(
                request.Run.ChatId,
                request.Run.Id,
                request.Invocation.ToolName,
                request.Invocation.ArgumentsJson,
                null,
                preview.EffectPlan),
            ct);

        if (beforeHook.ModifiedArgumentsJson != null &&
            !preview.Tool.InputsEquivalent(request.Invocation.ArgumentsJson, beforeHook.ModifiedArgumentsJson))
        {
            request.Invocation.ArgumentsJson = SecretRedactor.RedactJson(beforeHook.ModifiedArgumentsJson);
            preview = await PreviewAsync(
                request.Run.ChatId,
                request.Invocation.ToolName,
                request.Invocation.ArgumentsJson,
                ct);
            if (preview.Tool == null)
            {
                return new ToolExecutionOutcome(
                    request,
                    null,
                    preview.Metadata,
                    preview.Safety,
                    new AgentToolResult(false, string.Empty, preview.Safety.Warning ?? preview.Safety.Summary),
                    preview.EffectPlan,
                    null,
                    progressEvents);
            }

            tool = preview.Tool;
        }

        if (!beforeHook.Allowed)
        {
            var reason = beforeHook.Reason ?? "Tool hook blocked this invocation.";
            progressEvents.Add(new AgentToolProgress("hook_blocked", 0, reason));
            return new ToolExecutionOutcome(
                request,
                tool,
                preview.Metadata,
                preview.Safety,
                new AgentToolResult(false, string.Empty, reason),
                preview.EffectPlan,
                null,
                progressEvents);
        }

        if (preview.ValidationFailure != null)
        {
            return new ToolExecutionOutcome(
                request,
                tool,
                preview.Metadata,
                preview.Safety,
                preview.ValidationFailure,
                preview.EffectPlan,
                null,
                progressEvents);
        }

        if (preview.Safety.IsBlocked)
        {
            return new ToolExecutionOutcome(
                request,
                tool,
                preview.Metadata,
                preview.Safety,
                new AgentToolResult(
                    false,
                    string.Empty,
                    $"Safety policy blocked this tool call. {preview.Safety.Warning ?? preview.Safety.Summary}"),
                preview.EffectPlan,
                null,
                progressEvents);
        }

        var maxOutputChars = Math.Max(
            1,
            Math.Min(request.MaxOutputChars, preview.Metadata.MaxResultSizeChars));
        var progress = new Progress<AgentToolProgress>(p => progressEvents.Add(p));
        var result = tool is IAgentToolV3 v3Tool
            ? await v3Tool.ExecuteWithProgressAsync(
                new AgentToolExecutionContext(
                    request.Run.ChatId,
                    request.Run.Id,
                    request.Invocation.Id,
                    request.TimeoutSeconds,
                    maxOutputChars),
                request.Invocation.ArgumentsJson,
                progress,
                ct)
            : await tool.ExecuteAsync(
                new AgentToolExecutionContext(
                    request.Run.ChatId,
                    request.Run.Id,
                    request.Invocation.Id,
                    request.TimeoutSeconds,
                    maxOutputChars),
                request.Invocation.ArgumentsJson,
                ct);

        result = LimitResult(result, maxOutputChars);

        var afterHook = await RunHooksAsync(
            tool,
            result.Success ? ToolHookTriggers.AfterUse : ToolHookTriggers.AfterFailedUse,
            new ToolHookContext(
                request.Run.ChatId,
                request.Run.Id,
                request.Invocation.ToolName,
                request.Invocation.ArgumentsJson,
                result,
                preview.EffectPlan),
            ct);

        if (!afterHook.Allowed)
        {
            var reason = afterHook.Reason ?? "Tool hook rejected the completed invocation.";
            progressEvents.Add(new AgentToolProgress("hook_blocked", 100, reason));
            result = new AgentToolResult(false, result.Output, reason, result.Artifacts);
        }

        ToolRollbackPlan? rollbackPlan = null;
        if (result.Success && tool is IAgentToolV3 rollbackTool)
        {
            rollbackPlan = await rollbackTool.CreateRollbackPlanAsync(
                request.Invocation.ArgumentsJson,
                result,
                ct);
        }

        return new ToolExecutionOutcome(
            request,
            tool,
            preview.Metadata,
            preview.Safety,
            result,
            preview.EffectPlan,
            rollbackPlan,
            progressEvents);
    }

    private async Task<ToolHookResult> RunHooksAsync(
        IAgentTool tool,
        ToolHookTriggers trigger,
        ToolHookContext context,
        CancellationToken ct)
    {
        if (tool is IAgentToolV3 v3 && !v3.SupportedHooks.HasFlag(trigger))
            return ToolHookResult.Allow();

        string? modifiedArgumentsJson = null;
        foreach (var hook in _hookRegistry.GetHooks(trigger))
        {
            var hookContext = modifiedArgumentsJson == null
                ? context
                : context with { ArgumentsJson = modifiedArgumentsJson };
            var result = await hook.ExecuteAsync(hookContext, ct);
            if (!string.IsNullOrWhiteSpace(result.ModifiedArgumentsJson))
                modifiedArgumentsJson = result.ModifiedArgumentsJson;
            if (!result.Allowed)
            {
                return new ToolHookResult(
                    false,
                    $"{hook.Name}: {result.Reason ?? "blocked"}",
                    modifiedArgumentsJson);
            }
        }

        return modifiedArgumentsJson == null
            ? ToolHookResult.Allow()
            : new ToolHookResult(true, ModifiedArgumentsJson: modifiedArgumentsJson);
    }

    private static ToolSafetyAssessment FromV3SafetyClassification(
        ToolSafetyClassification classification,
        ToolEffectPlan? effectPlan)
    {
        var preview = JsonSerializer.Serialize(
            new
            {
                classification.Summary,
                classification.Warning,
                effectPlan
            },
            PreviewJsonOptions);

        return new ToolSafetyAssessment(
            classification.Level,
            classification.Category,
            classification.IsReadOnly,
            classification.IsDestructive,
            classification.RequiresExplicitApproval,
            classification.IsBlocked,
            classification.Summary,
            classification.Warning,
            preview);
    }

    private static ToolSafetyAssessment SafetyFromRegisteredToolMetadata(IAgentTool tool)
    {
        var summary = $"Registered tool {tool.Definition.Name} is not covered by the built-in safety kernel.";
        if (tool.Metadata.IsReadOnly)
            return ToolSafetyAssessment.LowRead("tool", summary);

        return ToolSafetyAssessment.Medium(
            "tool",
            isReadOnly: false,
            isWrite: !tool.Metadata.IsReadOnly,
            summary);
    }

    private static ToolEffectPlan EffectPlanFromLegacySafety(ToolSafetyAssessment safety)
    {
        var paths = new List<string>();
        var commands = new List<string>();
        var domains = new List<string>();

        try
        {
            using var doc = JsonDocument.Parse(safety.PreviewJson);
            var root = doc.RootElement;
            AddStringProperty(root, "path", paths);
            if (root.TryGetProperty("paths", out var pathsElement) &&
                pathsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in pathsElement.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                        paths.Add(item.GetString() ?? string.Empty);
                }
            }

            AddStringProperty(root, "command", commands);
            AddStringProperty(root, "url", domains, value =>
            {
                if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
                    return uri.Host;
                return value;
            });
        }
        catch
        {
            // Preview JSON is best-effort metadata; fall back to a conservative empty plan.
        }

        paths = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        commands = commands.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        domains = domains.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (commands.Count > 0)
            return ToolEffectPlan.Command(commands, safety.Level);
        if (domains.Count > 0)
            return ToolEffectPlan.Network(domains, safety.Level);
        if (safety.IsWriteOperation)
            return ToolEffectPlan.Write(paths, paths, hasRollback: safety.Category.StartsWith("code_", StringComparison.OrdinalIgnoreCase));
        if (safety.IsReadOnly)
            return ToolEffectPlan.ReadOnly(paths);
        return ToolEffectPlan.Empty;

        static void AddStringProperty(JsonElement root, string property, List<string> target, Func<string, string>? map = null)
        {
            if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
                return;
            var text = value.GetString();
            if (string.IsNullOrWhiteSpace(text))
                return;
            target.Add(map == null ? text : map(text));
        }
    }

    private static AgentToolResult LimitResult(AgentToolResult result, int maxChars)
    {
        if (result.Output.Length <= maxChars)
            return result;

        return result with
        {
            Output = result.Output[..maxChars] + "\n[tool output truncated by scheduler]"
        };
    }

    private static ToolHookRegistry CreateDefaultHookRegistry()
    {
        var registry = new ToolHookRegistry();
        registry.Register(new SecretRedactionHook());
        return registry;
    }
}
