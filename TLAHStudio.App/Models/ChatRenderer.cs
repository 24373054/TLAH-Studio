using System.Collections.ObjectModel;
using TLAHStudio.Core.Llm;
using TLAHStudio.Core.Models;
using TLAHStudio.Core.Services;

#pragma warning disable CA1416

namespace TLAHStudio.App.Models;

/// <summary>
/// M2.8.0: Converts core Message objects into observable ChatMessageBlock collections.
/// Handles parsing of thinking, tool use/results, attachments, and streaming state.
/// Replaces the imperative UI element building in ChatPage.xaml.cs.
/// </summary>
public class ChatRenderer
{
    /// <summary>
    /// Convert all messages into blocks. Returns a full replacement collection.
    /// </summary>
    public ObservableCollection<ChatMessageBlock> RenderAll(IEnumerable<Message> messages)
    {
        var blocks = new ObservableCollection<ChatMessageBlock>();
        foreach (var message in messages)
            AddMessageBlocks(blocks, message);
        return blocks;
    }

    /// <summary>
    /// Update a streaming message in-place. Mutates existing blocks rather than replacing.
    /// Critical for M2.8.0: no full tree rebuild during streaming.
    /// </summary>
    public void UpdateStreamingBlock(ObservableCollection<ChatMessageBlock> blocks, Message streamingMessage)
    {
        // Find the last block belonging to this message
        for (int i = blocks.Count - 1; i >= 0; i--)
        {
            if (blocks[i].MessageId != streamingMessage.Id)
                continue;

            var block = blocks[i];
            // Re-parse content for thinking changes
            if (AssistantContentFormatter.TryParse(
                    streamingMessage.Content,
                    out var thinkingContent, out var answerContent, out var isExpanded))
            {
                if (!string.IsNullOrWhiteSpace(thinkingContent))
                {
                    block.BlockType = ChatBlockType.Thinking;
                    block.Content = AssistantContentFormatter.Compose(answerContent, thinkingContent, isExpanded);
                    block.IsStreaming = true;
                }
                else
                {
                    block.Content = answerContent;
                    block.IsStreaming = true;
                }
            }
            else
            {
                block.Content = streamingMessage.Content;
                block.IsStreaming = true;
            }
            break;
        }
    }

    /// <summary>
    /// Mark streaming as complete for a message's blocks.
    /// </summary>
    public void FinalizeStreaming(ObservableCollection<ChatMessageBlock> blocks, Message message)
    {
        foreach (var block in blocks)
        {
            if (block.MessageId == message.Id)
            {
                block.IsStreaming = false;
                if (block.BlockType == ChatBlockType.Thinking)
                    block.IsThinkingCollapsed = true;
            }
        }
    }

    /// <summary>
    /// Add blocks for a single message to the collection.
    /// </summary>
    public void AddMessageBlocks(ObservableCollection<ChatMessageBlock> blocks, Message message)
    {
        switch (message.Role)
        {
            case "user":
            case "system":
                blocks.Add(ChatMessageBlock.TextBlock(message.Id, message.Role, message.Content, blocks.Count));
                break;

            case "assistant":
                RenderAssistantBlocks(blocks, message);
                break;

            case "tool":
                blocks.Add(ChatMessageBlock.ToolResultBlock(message.Id, true, message.Content,
                    new { role = "tool" }, blocks.Count));
                break;
        }
    }

    private static void RenderAssistantBlocks(ObservableCollection<ChatMessageBlock> blocks, Message message)
    {
        // Try to parse structured content
        if (AssistantContentFormatter.TryParse(
                message.Content,
                out var thinkingText, out var answerText, out var thinkingCollapsed))
        {
            if (!string.IsNullOrWhiteSpace(thinkingText))
            {
                blocks.Add(ChatMessageBlock.ThinkingBlock(
                    message.Id, thinkingText, thinkingCollapsed, blocks.Count));
            }

            if (!string.IsNullOrWhiteSpace(answerText))
            {
                blocks.Add(ChatMessageBlock.TextBlock(
                    message.Id, "assistant", answerText, blocks.Count));
            }
        }
        else
        {
            // Check for tool request markers in content
            if (message.Content.StartsWith("## Step "))
            {
                blocks.Add(ChatMessageBlock.ToolUseBlock(
                    message.Id, "agent", message.Content, null, blocks.Count));
            }
            else
            {
                blocks.Add(ChatMessageBlock.TextBlock(
                    message.Id, "assistant", message.Content, blocks.Count));
            }
        }

        // Render attachments
        var attachments = MessageAttachmentFormatter.Extract(message.Content);
        if (attachments.Attachments is { Count: > 0 })
        {
            foreach (var att in attachments.Attachments)
            {
                blocks.Add(ChatMessageBlock.FileBlock(
                    message.Id, att.RelativePath, att.SizeBytes, att.Sha256, blocks.Count));
            }
        }
    }

    /// <summary>
    /// Create structured approval blocks with effect preview.
    /// </summary>
    public ChatMessageBlock CreateApprovalBlock(
        Guid messageId,
        string toolName,
        string argumentsJson,
        string safetyLevel,
        string safetySummary,
        string safetyJson,
        IReadOnlyList<string>? pathsRead = null,
        IReadOnlyList<string>? pathsWritten = null,
        IReadOnlyList<string>? domains = null,
        IReadOnlyList<string>? commands = null)
    {
        var effect = new ToolEffectPreview(
            pathsRead ?? Array.Empty<string>(),
            pathsWritten ?? Array.Empty<string>(),
            domains ?? Array.Empty<string>(),
            commands ?? Array.Empty<string>(),
            safetyLevel,
            toolName,
            safetyLevel,
            safetySummary);

        return new ChatMessageBlock
        {
            BlockType = ChatBlockType.ApprovalNeeded,
            Role = "system",
            MessageId = messageId,
            Content = $"Approve tool: {toolName}",
            Metadata = effect
        };
    }
}
