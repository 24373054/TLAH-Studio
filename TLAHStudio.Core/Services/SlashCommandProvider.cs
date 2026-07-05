using System.Threading;
using System.Threading.Tasks;
using TLAHStudio.Core.Models;

namespace TLAHStudio.Core.Services;

/// <summary>
/// M4.9.5 Phase E2: A unified slash-command descriptor aggregated from every
/// command source (built-in chat commands, skills, agent tools, MCP tools).
/// The input box surfaces these as a completion list; the ViewModel dispatches
/// the selected command.
/// </summary>
public sealed record SlashCommand(
    string Name,              // e.g. "clear", "code-review", "file_read", "mcp:server:tool" (no leading /)
    string Description,       // one-line summary
    string Category,          // Chat | Skill | Tool | MCP
    string? ArgumentHint,     // e.g. "<query>" or "<file> <pattern>" — shown after the name
    string SourceLabel,       // e.g. "skill", "tool", "mcp:github" — badge text
    SlashCommandKind Kind);   // how to dispatch

public enum SlashCommandKind
{
    /// <summary>Built-in chat command (clear/new/agent/help/stop) — executed locally.</summary>
    BuiltIn,
    /// <summary>A skill — dispatched as an agent skill invocation.</summary>
    Skill,
    /// <summary>An agent tool — dispatched as an agent tool call.</summary>
    Tool,
    /// <summary>An MCP tool — dispatched via mcp_call.</summary>
    Mcp
}

public interface ISlashCommandProvider
{
    /// <summary>Aggregate all available slash commands, ordered by category priority
    /// (Chat > Skill > Tool > MCP) then by name.</summary>
    Task<IReadOnlyList<SlashCommand>> GetCommandsAsync(Guid chatId, CancellationToken ct = default);
}
