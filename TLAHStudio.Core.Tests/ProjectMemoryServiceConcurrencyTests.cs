using TLAHStudio.Core.Services;
using TLAHStudio.Data;

namespace TLAHStudio.Core.Tests;

public sealed class ProjectMemoryServiceConcurrencyTests
{
    private const string InitialMemoryContent =
        "# Project Memory\n\nUse this file for stable project facts, preferences, and recurring instructions.\n";

    [Fact]
    public async Task ReadAsync_ConcurrentFirstReadsPublishOneCompletePersonalMemoryFile()
    {
        var appDataRoot = Path.Combine(
            Path.GetTempPath(),
            "TLAHStudio.ProjectMemory.Tests",
            Guid.NewGuid().ToString("N"));
        var databases = new List<TlahDbContext>();

        try
        {
            var services = Enumerable.Range(0, 32)
                .Select(_ =>
                {
                    var db = TestDb.Create();
                    databases.Add(db);
                    return new ProjectMemoryService(db, appDataRoot);
                })
                .ToArray();
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var reads = services
                .Select(async service =>
                {
                    await start.Task;
                    return await service.ReadAsync(Guid.NewGuid());
                })
                .ToArray();

            start.SetResult();
            var results = await Task.WhenAll(reads);

            Assert.All(results, content => Assert.Equal(InitialMemoryContent, content));

            var memoryDirectory = Path.Combine(appDataRoot, "memory", "personal");
            Assert.Equal(InitialMemoryContent, await File.ReadAllTextAsync(Path.Combine(memoryDirectory, "MEMORY.md")));
            Assert.Empty(Directory.EnumerateFiles(memoryDirectory, "*.tmp"));
        }
        finally
        {
            foreach (var db in databases)
                await db.DisposeAsync();

            if (Directory.Exists(appDataRoot))
                Directory.Delete(appDataRoot, recursive: true);
        }
    }
}
