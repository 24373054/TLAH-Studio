using CommunityToolkit.Mvvm.ComponentModel;

namespace TLAHStudio.App.Models;

/// <summary>
/// M2.8.0: Typed content blocks for chat messages.
/// Replaces the monolithic message string with structured, independently renderable blocks.
/// </summary>
public enum ChatBlockType
{
    Text,
    Thinking,
    ToolUse,
    ToolResult,
    FileAttachment,
    ImageAttachment,
    Error,
    ApprovalNeeded,
    SystemNotice
}

public partial class ChatMessageBlock : ObservableObject
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public ChatBlockType BlockType { get; set; }
    public string Role { get; init; } = "assistant";
    public Guid MessageId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    [ObservableProperty]
    private string _content = string.Empty;

    [ObservableProperty]
    private bool _isStreaming;

    [ObservableProperty]
    private bool _isThinkingCollapsed = true;

    /// <summary>
    /// Tool-specific metadata: tool name, safety level, render hint, paths, etc.
    /// </summary>
    public object? Metadata { get; init; }

    /// <summary>
    /// Stable index within a message's block list.
    /// </summary>
    public int BlockIndex { get; set; }

    /// <summary>
    /// Child blocks for expandable content (e.g., file list inside tool result).
    /// </summary>
    public IReadOnlyList<ChatMessageBlock>? Children { get; set; }

    public static ChatMessageBlock TextBlock(Guid messageId, string role, string content, int index) => new()
    {
        Id = Guid.NewGuid(),
        BlockType = ChatBlockType.Text,
        Role = role,
        MessageId = messageId,
        Content = content,
        BlockIndex = index
    };

    public static ChatMessageBlock ThinkingBlock(Guid messageId, string content, bool collapsed = true, int index = 0) => new()
    {
        Id = Guid.NewGuid(),
        BlockType = ChatBlockType.Thinking,
        Role = "assistant",
        MessageId = messageId,
        Content = content,
        IsThinkingCollapsed = collapsed,
        BlockIndex = index
    };

    public static ChatMessageBlock ToolUseBlock(Guid messageId, string toolName, string content, object? metadata = null, int index = 0) => new()
    {
        Id = Guid.NewGuid(),
        BlockType = ChatBlockType.ToolUse,
        Role = "assistant",
        MessageId = messageId,
        Content = content,
        Metadata = metadata,
        BlockIndex = index
    };

    public static ChatMessageBlock ToolResultBlock(Guid messageId, bool success, string content, object? metadata = null, int index = 0) => new()
    {
        Id = Guid.NewGuid(),
        BlockType = ChatBlockType.ToolResult,
        Role = "tool",
        MessageId = messageId,
        Content = content,
        Metadata = metadata,
        BlockIndex = index
    };

    public static ChatMessageBlock ErrorBlock(Guid messageId, string content, int index = 0) => new()
    {
        Id = Guid.NewGuid(),
        BlockType = ChatBlockType.Error,
        Role = "system",
        MessageId = messageId,
        Content = content,
        BlockIndex = index
    };

    public static ChatMessageBlock ApprovalBlock(Guid messageId, string toolName, object metadata, int index = 0) => new()
    {
        Id = Guid.NewGuid(),
        BlockType = ChatBlockType.ApprovalNeeded,
        Role = "system",
        MessageId = messageId,
        Content = $"Approve {toolName}?",
        Metadata = metadata,
        BlockIndex = index
    };

    public static ChatMessageBlock FileBlock(Guid messageId, string fileName, long sizeBytes, string? hash = null, int index = 0) => new()
    {
        Id = Guid.NewGuid(),
        BlockType = ChatBlockType.FileAttachment,
        Role = "assistant",
        MessageId = messageId,
        Content = fileName,
        Metadata = new { fileName, sizeBytes, sha256 = hash ?? "" },
        BlockIndex = index
    };
}

/// <summary>
/// Metadata for tool effect preview in approval dialogs.
/// </summary>
public sealed record ToolEffectPreview(
    IReadOnlyList<string> PathsRead,
    IReadOnlyList<string> PathsWritten,
    IReadOnlyList<string> Domains,
    IReadOnlyList<string> Commands,
    string RiskLevel,
    string ToolName,
    string SafetyLevel,
    string SafetySummary);
