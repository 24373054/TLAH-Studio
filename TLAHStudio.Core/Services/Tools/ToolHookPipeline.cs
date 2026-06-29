using TLAHStudio.Core.Services.Tools.Models;

namespace TLAHStudio.Core.Services.Tools;

/// <summary>
/// M2.9.0: Hook trigger points for tool execution pipeline.
/// </summary>
[Flags]
public enum ToolHookTriggers
{
    None = 0,
    BeforeUse = 1 << 0,
    AfterUse = 1 << 1,
    AfterFailedUse = 1 << 2,
    All = BeforeUse | AfterUse | AfterFailedUse
}

/// <summary>
/// Context passed to tool hooks.
/// </summary>
public sealed record ToolHookContext(
    Guid ChatId,
    Guid RunId,
    string ToolName,
    string ArgumentsJson,
    AgentToolResult? Result,
    ToolEffectPlan? EffectPlan
);

/// <summary>
/// Result from a tool hook execution.
/// </summary>
public sealed record ToolHookResult(
    bool Allowed,
    string? Reason = null,
    string? ModifiedArgumentsJson = null
)
{
    public static ToolHookResult Allow() => new(true);
    public static ToolHookResult Block(string reason) => new(false, reason);
}

/// <summary>
/// A single tool hook handler.
/// </summary>
public interface IToolHook
{
    ToolHookTriggers Triggers { get; }
    string Name { get; }
    Task<ToolHookResult> ExecuteAsync(ToolHookContext context, CancellationToken ct = default);
}

/// <summary>
/// Registry for tool hooks. Hooks are called in registration order.
/// </summary>
public interface IToolHookRegistry
{
    void Register(IToolHook hook);
    void Unregister(IToolHook hook);
    IReadOnlyList<IToolHook> GetHooks(ToolHookTriggers trigger);
}

/// <summary>
/// Pipeline that runs all registered hooks for a given trigger.
/// If any hook blocks, the tool invocation is denied.
/// </summary>
public class ToolHookRegistry : IToolHookRegistry
{
    private readonly List<IToolHook> _hooks = [];
    private readonly object _lock = new();

    public void Register(IToolHook hook)
    {
        lock (_lock) _hooks.Add(hook);
    }

    public void Unregister(IToolHook hook)
    {
        lock (_lock) _hooks.Remove(hook);
    }

    public IReadOnlyList<IToolHook> GetHooks(ToolHookTriggers trigger)
    {
        lock (_lock)
            return _hooks.Where(h => h.Triggers.HasFlag(trigger)).ToList();
    }
}

/// <summary>
/// Executes hooks in sequence. Stops at first blocking hook.
/// </summary>
public static class ToolHookPipeline
{
    public static async Task<ToolHookResult> RunAsync(
        IToolHookRegistry registry,
        ToolHookTriggers trigger,
        ToolHookContext context,
        CancellationToken ct = default)
    {
        foreach (var hook in registry.GetHooks(trigger))
        {
            var result = await hook.ExecuteAsync(context, ct);
            if (!result.Allowed)
                return result;
        }
        return ToolHookResult.Allow();
    }
}

/// <summary>
/// Built-in hook that ensures secrets are redacted from tool results.
/// </summary>
public sealed class SecretRedactionHook : IToolHook
{
    public ToolHookTriggers Triggers => ToolHookTriggers.AfterUse | ToolHookTriggers.AfterFailedUse;
    public string Name => "SecretRedaction";

    public Task<ToolHookResult> ExecuteAsync(ToolHookContext context, CancellationToken ct = default)
    {
        // Secrets are already redacted by SecretRedactor in the engine.
        // This hook serves as an audit point and can be extended.
        return Task.FromResult(ToolHookResult.Allow());
    }
}
