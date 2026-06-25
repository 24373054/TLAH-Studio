using TLAHStudio.Core.Models;

namespace TLAHStudio.Core.Services;

/// <summary>
/// Chat management service interface.
/// Maps from services/chat_service.py.
/// </summary>
public interface IChatService
{
    Task<Chat> CreateChatAsync(string title = "New Chat", CancellationToken ct = default);
    Task<Chat?> GetChatAsync(Guid chatId, CancellationToken ct = default);
    Task<Chat> GetChatOrThrowAsync(Guid chatId, CancellationToken ct = default);
    Task<List<ChatSummaryDto>> ListChatsAsync(string? search = null, bool includeArchived = false, CancellationToken ct = default);
    Task<Chat> UpdateChatAsync(Guid chatId, string? title = null, string? systemPrompt = null, CancellationToken ct = default);
    Task<Chat> SetPinnedAsync(Guid chatId, bool isPinned, CancellationToken ct = default);
    Task<Chat> SetArchivedAsync(Guid chatId, bool isArchived, CancellationToken ct = default);
    Task DeleteChatAsync(Guid chatId, CancellationToken ct = default);
    Task RestoreChatAsync(Guid chatId, CancellationToken ct = default);
    Task<string> ExportChatJsonAsync(Guid chatId, CancellationToken ct = default);
    Task<List<Message>> GetChatMessagesAsync(Guid chatId, CancellationToken ct = default);
    Task<int> GetNextSequenceAsync(Guid chatId, CancellationToken ct = default);
    Task DeleteMessagesAfterAsync(Guid messageId, bool includeSelected = false, CancellationToken ct = default);
    Task<Message> UpdateMessageContentAsync(Guid messageId, string content, CancellationToken ct = default);
}

public record ChatSummaryDto(
    Guid Id,
    string Title,
    DateTime UpdatedAt,
    int MessageCount,
    bool IsPinned = false,
    bool IsArchived = false,
    DateTime? DeletedAt = null,
    Guid? ProjectSpaceId = null,
    string? ProjectName = null,
    Guid? ConfigProfileId = null,
    string? ConfigProfileName = null);
