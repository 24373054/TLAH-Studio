using System.Text;
using System.Text.Json;

namespace TLAHStudio.Core.Llm;

public sealed record MessageAttachment(
    string RelativePath,
    string ContentType,
    long SizeBytes,
    string Sha256,
    string? Caption = null);

public static class MessageAttachmentFormatter
{
    public const string AttachmentsStart = "<tlah-attachments>";
    public const string AttachmentsEnd = "</tlah-attachments>";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string Compose(string? body, IReadOnlyList<MessageAttachment>? attachments)
    {
        var safeBody = body ?? string.Empty;
        if (attachments is not { Count: > 0 })
            return safeBody;

        var builder = new StringBuilder();
        builder.Append(safeBody.TrimEnd());
        if (builder.Length > 0)
            builder.AppendLine().AppendLine();
        builder.AppendLine(AttachmentsStart);
        builder.AppendLine(JsonSerializer.Serialize(attachments, JsonOptions));
        builder.Append(AttachmentsEnd);
        return builder.ToString();
    }

    public static AttachmentParseResult Extract(string? content)
    {
        var text = content ?? string.Empty;
        var attachments = new List<MessageAttachment>();
        var builder = new StringBuilder();
        var cursor = 0;

        while (cursor < text.Length)
        {
            var start = text.IndexOf(AttachmentsStart, cursor, StringComparison.Ordinal);
            if (start < 0)
            {
                builder.Append(text, cursor, text.Length - cursor);
                break;
            }

            builder.Append(text, cursor, start - cursor);
            var payloadStart = start + AttachmentsStart.Length;
            var end = text.IndexOf(AttachmentsEnd, payloadStart, StringComparison.Ordinal);
            if (end < 0)
            {
                builder.Append(text, start, text.Length - start);
                break;
            }

            var payload = text[payloadStart..end].Trim();
            if (!string.IsNullOrWhiteSpace(payload))
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<List<MessageAttachment>>(payload, JsonOptions);
                    if (parsed != null)
                        attachments.AddRange(parsed.Where(a => !string.IsNullOrWhiteSpace(a.RelativePath)));
                }
                catch (JsonException)
                {
                    builder.Append(text, start, end + AttachmentsEnd.Length - start);
                }
            }

            cursor = end + AttachmentsEnd.Length;
        }

        return new AttachmentParseResult(builder.ToString().Trim(), attachments);
    }

    public static string StripAttachments(string? content) => Extract(content).Body;
}

public sealed record AttachmentParseResult(
    string Body,
    IReadOnlyList<MessageAttachment> Attachments);
