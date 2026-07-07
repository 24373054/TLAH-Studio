using Microsoft.EntityFrameworkCore;
using System.Text.Encodings.Web;
using System.Text.Json;
using TLAHStudio.Core.Models;

namespace TLAHStudio.Core.Services;

/// <summary>
/// Chat management service.
/// Maps 1:1 from services/chat_service.py.
/// </summary>
public class ChatService : IChatService
{
    private readonly DbContext _db;

    public ChatService(DbContext db)
    {
        _db = db;
    }

    public async Task<Chat> CreateChatAsync(string title = "New Chat", CancellationToken ct = default)
    {
        var chat = new Chat { Title = title };
        _db.Set<Chat>().Add(chat);
        await _db.SaveChangesAsync(ct);
        await LogAuditAsync("create", "chat", chat.Id.ToString("D"), $"Created chat \"{chat.Title}\".", chat.ProjectSpaceId, chat.Id, new { chat.Title }, ct);
        return chat;
    }

    public async Task<Chat?> GetChatAsync(Guid chatId, CancellationToken ct = default)
    {
        return await _db.Set<Chat>()
            .Include(c => c.Messages.OrderBy(m => m.SequenceNum))
            .Include(c => c.ProjectSpace)
            .Include(c => c.ConfigProfile)
            .FirstOrDefaultAsync(c => c.Id == chatId, ct);
    }

    public async Task<Chat> GetChatOrThrowAsync(Guid chatId, CancellationToken ct = default)
    {
        var chat = await GetChatAsync(chatId, ct);
        if (chat == null)
            throw new InvalidOperationException($"Chat not found: {chatId}");
        return chat;
    }

    public async Task<List<ChatSummaryDto>> ListChatsAsync(string? search = null, bool includeArchived = false, CancellationToken ct = default)
    {
        var query = _db.Set<Chat>()
            .Where(c => c.DeletedAt == null);

        if (!includeArchived)
            query = query.Where(c => !c.IsArchived);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = $"%{search.Trim()}%";
            // M4.9.6: search message content in addition to titles so users can
            // find chats by what was said, not just by chat name.
            query = query.Where(c =>
                EF.Functions.Like(c.Title, term) ||
                c.Messages.Any(m => EF.Functions.Like(m.Content, term)));
        }

        var chats = await query
            .OrderByDescending(c => c.IsPinned)
            .ThenByDescending(c => c.UpdatedAt)
            .Select(c => new
            {
                c.Id,
                c.Title,
                c.UpdatedAt,
                c.IsPinned,
                c.IsArchived,
                c.DeletedAt,
                c.ProjectSpaceId,
                ProjectName = c.ProjectSpace == null ? null : c.ProjectSpace.Name,
                c.ConfigProfileId,
                ConfigProfileName = c.ConfigProfile == null ? null : c.ConfigProfile.Name,
                MessageCount = c.Messages.Count
            })
            .ToListAsync(ct);

        return chats.Select(c => new ChatSummaryDto(
            c.Id,
            c.Title,
            c.UpdatedAt,
            c.MessageCount,
            c.IsPinned,
            c.IsArchived,
            c.DeletedAt,
            c.ProjectSpaceId,
            c.ProjectName,
            c.ConfigProfileId,
            c.ConfigProfileName)).ToList();
    }

    public async Task<Chat> UpdateChatAsync(Guid chatId, string? title = null, string? systemPrompt = null, CancellationToken ct = default)
    {
        var chat = await GetChatOrThrowAsync(chatId, ct);

        if (title != null)
            chat.Title = title;
        if (systemPrompt != null)
            chat.SystemPrompt = systemPrompt;

        chat.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await LogAuditAsync("update", "chat", chat.Id.ToString("D"), $"Updated chat \"{chat.Title}\".", chat.ProjectSpaceId, chat.Id, new { title, systemPromptChanged = systemPrompt != null }, ct);
        return chat;
    }

    public async Task<Chat> SetPinnedAsync(Guid chatId, bool isPinned, CancellationToken ct = default)
    {
        var chat = await GetChatOrThrowAsync(chatId, ct);
        chat.IsPinned = isPinned;
        chat.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await LogAuditAsync("pin", "chat", chat.Id.ToString("D"), $"{(isPinned ? "Pinned" : "Unpinned")} chat \"{chat.Title}\".", chat.ProjectSpaceId, chat.Id, new { isPinned }, ct);
        return chat;
    }

    public async Task<Chat> SetArchivedAsync(Guid chatId, bool isArchived, CancellationToken ct = default)
    {
        var chat = await GetChatOrThrowAsync(chatId, ct);
        chat.IsArchived = isArchived;
        chat.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await LogAuditAsync("archive", "chat", chat.Id.ToString("D"), $"{(isArchived ? "Archived" : "Restored")} chat \"{chat.Title}\".", chat.ProjectSpaceId, chat.Id, new { isArchived }, ct);
        return chat;
    }

    public async Task DeleteChatAsync(Guid chatId, CancellationToken ct = default)
    {
        var chat = await _db.Set<Chat>()
            .FirstOrDefaultAsync(c => c.Id == chatId, ct);
        if (chat != null)
        {
            chat.DeletedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            await LogAuditAsync("delete", "chat", chat.Id.ToString("D"), $"Deleted chat \"{chat.Title}\".", chat.ProjectSpaceId, chat.Id, new { chat.Title }, ct);
        }
    }

    public async Task RestoreChatAsync(Guid chatId, CancellationToken ct = default)
    {
        var chat = await _db.Set<Chat>()
            .FirstOrDefaultAsync(c => c.Id == chatId, ct);
        if (chat == null)
            return;

        chat.DeletedAt = null;
        chat.IsArchived = false;
        chat.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await LogAuditAsync("restore", "chat", chat.Id.ToString("D"), $"Restored chat \"{chat.Title}\".", chat.ProjectSpaceId, chat.Id, new { chat.Title }, ct);
    }

    public async Task<string> ExportChatJsonAsync(Guid chatId, CancellationToken ct = default)
    {
        var chat = await _db.Set<Chat>()
            .Include(c => c.Messages.OrderBy(m => m.SequenceNum))
            .Include(c => c.ProjectSpace)
            .Include(c => c.ConfigProfile)
            .FirstOrDefaultAsync(c => c.Id == chatId, ct)
            ?? throw new InvalidOperationException($"Chat not found: {chatId}");

        var turns = await _db.Set<Turn>()
            .Where(t => t.ChatId == chatId)
            .OrderBy(t => t.TurnNumber)
            .Select(t => new
            {
                t.Id,
                t.TurnNumber,
                t.CreatedAt,
                RawRequest = t.RawRequest == null ? null : new
                {
                    t.RawRequest.Provider,
                    t.RawRequest.EndpointUrl,
                    t.RawRequest.RequestJson,
                    t.RawRequest.CreatedAt
                },
                RawResponse = t.RawResponse == null ? null : new
                {
                    t.RawResponse.Provider,
                    t.RawResponse.HttpStatusCode,
                    t.RawResponse.LatencyMs,
                    t.RawResponse.TokenUsageJson,
                    t.RawResponse.ResponseJson,
                    t.RawResponse.CreatedAt
                }
            })
            .ToListAsync(ct);

        var payload = new
        {
            ExportedAt = DateTime.UtcNow,
            Chat = new
            {
                chat.Id,
                chat.Title,
                chat.SystemPrompt,
                chat.CreatedAt,
                chat.UpdatedAt,
                chat.IsPinned,
                chat.IsArchived,
                chat.ProjectSpaceId,
                Project = chat.ProjectSpace == null ? null : new { chat.ProjectSpace.Id, chat.ProjectSpace.Name },
                chat.ConfigProfileId,
                ConfigProfile = chat.ConfigProfile == null ? null : new { chat.ConfigProfile.Id, chat.ConfigProfile.Name }
            },
            Messages = chat.Messages
                .OrderBy(m => m.SequenceNum)
                .Select(m => new { m.Id, m.Role, m.Content, m.TurnId, m.SequenceNum, m.CreatedAt }),
            Turns = turns
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    public async Task<List<Message>> GetChatMessagesAsync(Guid chatId, CancellationToken ct = default)
    {
        return await _db.Set<Message>()
            .Where(m => m.ChatId == chatId)
            .OrderBy(m => m.SequenceNum)
            .ToListAsync(ct);
    }

    public async Task<int> GetNextSequenceAsync(Guid chatId, CancellationToken ct = default)
    {
        var maxSeq = await _db.Set<Message>()
            .Where(m => m.ChatId == chatId)
            .MaxAsync(m => (int?)m.SequenceNum, ct);
        return (maxSeq ?? 0) + 1;
    }

    public async Task DeleteMessagesAfterAsync(Guid messageId, bool includeSelected = false, CancellationToken ct = default)
    {
        var message = await _db.Set<Message>()
            .Include(m => m.Chat)
            .FirstOrDefaultAsync(m => m.Id == messageId, ct)
            ?? throw new InvalidOperationException($"Message not found: {messageId}");

        var cutoff = message.SequenceNum + (includeSelected ? 0 : 1);
        var messages = await _db.Set<Message>()
            .Where(m => m.ChatId == message.ChatId && m.SequenceNum >= cutoff)
            .ToListAsync(ct);

        _db.Set<Message>().RemoveRange(messages);
        var chat = await _db.Set<Chat>().FirstOrDefaultAsync(c => c.Id == message.ChatId, ct);
        if (chat != null)
            chat.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        await LogAuditAsync("truncate", "chat", message.ChatId.ToString("D"), "Deleted a message tail from chat history.", chat?.ProjectSpaceId, message.ChatId, new { messageId, includeSelected, removed = messages.Count }, ct);
    }

    public async Task<Message> UpdateMessageContentAsync(Guid messageId, string content, CancellationToken ct = default)
    {
        var message = await _db.Set<Message>()
            .FirstOrDefaultAsync(m => m.Id == messageId, ct)
            ?? throw new InvalidOperationException($"Message not found: {messageId}");
        message.Content = content;
        await _db.SaveChangesAsync(ct);
        await LogAuditAsync("edit", "message", message.Id.ToString("D"), "Edited message content.", message.Chat?.ProjectSpaceId, message.ChatId, new { messageId = message.Id }, ct);
        return message;
    }

    private async Task LogAuditAsync(
        string eventType,
        string entityType,
        string entityId,
        string summary,
        Guid? projectId,
        Guid? chatId,
        object metadata,
        CancellationToken ct)
    {
        _db.Set<AuditLogEntry>().Add(new AuditLogEntry
        {
            ProjectSpaceId = projectId,
            ChatId = chatId,
            EventType = eventType,
            EntityType = entityType,
            EntityId = entityId,
            Summary = summary,
            MetadataJson = JsonSerializer.Serialize(metadata, new JsonSerializerOptions
            {
                WriteIndented = false,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }),
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(ct);
    }
}
