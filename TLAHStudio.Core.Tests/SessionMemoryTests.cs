using System.Reflection;
using TLAHStudio.Core.Helpers;
using TLAHStudio.Core.Llm;
using TLAHStudio.Core.Services.SessionMemory;

namespace TLAHStudio.Core.Tests;

public class SessionMemoryTests
{
    [Fact]
    public async Task WaitForExtractionAsync_Timeout_DoesNotReleaseUnownedSemaphore()
    {
        var service = new SessionMemoryService();
        var semaphore = (SemaphoreSlim)typeof(SessionMemoryService)
            .GetField("_lock", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service)!;
        var startedField = typeof(SessionMemoryService)
            .GetField("_extractionStartedAt", BindingFlags.Instance | BindingFlags.NonPublic)!;

        await semaphore.WaitAsync();
        startedField.SetValue(service, DateTime.UtcNow);
        try
        {
            await service.WaitForExtractionAsync(TimeSpan.FromMilliseconds(20), CancellationToken.None);

            Assert.Equal(0, semaphore.CurrentCount);
        }
        finally
        {
            startedField.SetValue(service, null);
            if (semaphore.CurrentCount == 0)
                semaphore.Release();
        }
    }

    [Fact]
    public async Task ExtractAsync_ToolPreview_RedactsBearerToken()
    {
        var root = Path.Combine(Path.GetTempPath(), "TLAHStudio.SessionMemory.Tests", Guid.NewGuid().ToString("N"));
        const string token = "abcdefghijklmnopqrstuvwxyz123456";
        var service = new SessionMemoryService();
        var messages = new[]
        {
            new MessagePayload("user", "Test session memory."),
            new MessagePayload("assistant", string.Empty, ToolCalls:
            [
                new LlmToolCall("call-1", "terminal_exec",
                    $$"""{"command":"curl -H 'Authorization: Bearer {{token}}' https://example.test"}""")
            ])
        };

        try
        {
            await service.ExtractAsync(
                Guid.NewGuid(),
                Guid.NewGuid(),
                messages,
                root,
                [],
                [$"curl -H 'Authorization: Bearer {token}' https://example.test"],
                [],
                [],
                [],
                CancellationToken.None);

            var content = await File.ReadAllTextAsync(service.GetPath(root));
            Assert.DoesNotContain(token, content, StringComparison.Ordinal);
            Assert.Contains(SecretRedactor.Redacted, content, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
