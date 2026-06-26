using TLAHStudio.Core.Llm;

namespace TLAHStudio.Core.Tests;

public class MessageAttachmentFormatterTests
{
    [Fact]
    public void ComposeExtractsAttachmentsAndVisibleBody()
    {
        var content = MessageAttachmentFormatter.Compose(
            "Here is the file.",
            [
                new MessageAttachment(
                    "reports/out.zip",
                    "application/zip",
                    42,
                    "abcdef1234567890")
            ]);

        var parsed = MessageAttachmentFormatter.Extract(content);

        Assert.Equal("Here is the file.", parsed.Body);
        var attachment = Assert.Single(parsed.Attachments);
        Assert.Equal("reports/out.zip", attachment.RelativePath);
        Assert.Equal("application/zip", attachment.ContentType);
        Assert.DoesNotContain(MessageAttachmentFormatter.AttachmentsStart, MessageAttachmentFormatter.StripAttachments(content));
    }

    [Fact]
    public void StripAttachmentsLeavesPlainMessagesUntouched()
    {
        Assert.Equal("hello", MessageAttachmentFormatter.StripAttachments("hello"));
    }
}
