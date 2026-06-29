using System.Text.RegularExpressions;

namespace TLAHStudio.Core.Services.Memory;

/// <summary>
/// M2.10.0: Typed memory file info.
/// </summary>
public sealed record MemoryFileInfo(
    string FileName,
    string Type,        // user, feedback, project, reference
    string Description,
    int SizeBytes,
    DateTime UpdatedAt
);

/// <summary>
/// M2.10.0: Memory search result.
/// </summary>
public sealed record MemorySearchResult(
    string FileName,
    string Type,
    string Description,
    double RelevanceScore,
    string Snippet
);

/// <summary>
/// M2.10.0: File-based memory directory service.
/// Manages a MEMORY.md index and individual typed memory files.
/// </summary>
public interface IMemoryDirectoryService
{
    Task<IReadOnlyList<MemoryFileInfo>> ListFilesAsync(Guid projectId, CancellationToken ct = default);
    Task<string> ReadFileAsync(Guid projectId, string fileName, CancellationToken ct = default);
    Task WriteFileAsync(Guid projectId, string fileName, string type, string content, string description, CancellationToken ct = default);
    Task DeleteFileAsync(Guid projectId, string fileName, CancellationToken ct = default);
    Task<IReadOnlyList<MemorySearchResult>> SearchAsync(Guid projectId, string query, int maxResults = 5, CancellationToken ct = default);
    Task<string> BuildContextAsync(Guid projectId, int maxChars = 8000, CancellationToken ct = default);
}

public class MemoryDirectoryService : IMemoryDirectoryService
{
    private static readonly Regex FrontmatterRegex = new(
        @"^---\s*\n(.*?)\n---\s*\n", RegexOptions.Singleline | RegexOptions.Compiled);

    private string GetDir(Guid projectId)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TLAH Studio", "memory", projectId.ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public Task<IReadOnlyList<MemoryFileInfo>> ListFilesAsync(Guid projectId, CancellationToken ct = default)
    {
        var dir = GetDir(projectId);
        if (!Directory.Exists(dir))
            return Task.FromResult<IReadOnlyList<MemoryFileInfo>>(Array.Empty<MemoryFileInfo>());

        var files = Directory.GetFiles(dir, "*.md")
            .Select(f =>
            {
                var fi = new FileInfo(f);
                var (type, description) = ParseFrontmatter(File.ReadAllText(f));
                return new MemoryFileInfo(
                    Path.GetFileName(f), type, description,
                    (int)fi.Length, fi.LastWriteTimeUtc);
            })
            .ToList();
        return Task.FromResult<IReadOnlyList<MemoryFileInfo>>(files);
    }

    public Task<string> ReadFileAsync(Guid projectId, string fileName, CancellationToken ct = default)
    {
        var path = Path.Combine(GetDir(projectId), fileName);
        if (!File.Exists(path)) return Task.FromResult(string.Empty);
        return Task.FromResult(File.ReadAllText(path));
    }

    public async Task WriteFileAsync(Guid projectId, string fileName, string type, string content, string description, CancellationToken ct = default)
    {
        var dir = GetDir(projectId);
        var path = Path.Combine(dir, fileName);
        var frontmatter = $@"---
type: {type}
description: {description}
updated: {DateTime.UtcNow:O}
---
";
        await File.WriteAllTextAsync(path, frontmatter + content, ct);
        await UpdateIndexAsync(projectId, ct);
    }

    public Task DeleteFileAsync(Guid projectId, string fileName, CancellationToken ct = default)
    {
        var path = Path.Combine(GetDir(projectId), fileName);
        if (File.Exists(path)) File.Delete(path);
        return UpdateIndexAsync(projectId, ct);
    }

    public async Task<IReadOnlyList<MemorySearchResult>> SearchAsync(
        Guid projectId, string query, int maxResults = 5, CancellationToken ct = default)
    {
        var files = await ListFilesAsync(projectId, ct);
        var results = new List<MemorySearchResult>();
        var terms = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        foreach (var file in files)
        {
            var content = await ReadFileAsync(projectId, file.FileName, ct);
            var body = StripFrontmatter(content).ToLowerInvariant();
            var score = terms.Count(t => body.Contains(t)) / (double)terms.Length;
            if (score > 0)
            {
                var snippet = body.Length > 200 ? body[..200] + "..." : body;
                results.Add(new MemorySearchResult(file.FileName, file.Type, file.Description, score, snippet));
            }
        }

        return results.OrderByDescending(r => r.RelevanceScore).Take(maxResults).ToList();
    }

    public async Task<string> BuildContextAsync(Guid projectId, int maxChars = 8000, CancellationToken ct = default)
    {
        var files = await ListFilesAsync(projectId, ct);
        var ctx = new System.Text.StringBuilder();
        ctx.AppendLine("[project memory]");
        var remaining = maxChars;

        foreach (var file in files)
        {
            var content = await ReadFileAsync(projectId, file.FileName, ct);
            var body = StripFrontmatter(content);
            var entry = $"\n## {file.FileName} ({file.Type})\n{file.Description}\n{body}\n";
            if (entry.Length > remaining)
                entry = entry[..remaining] + "\n[truncated]";
            ctx.Append(entry);
            remaining -= entry.Length;
            if (remaining <= 0) break;
        }

        return ctx.ToString();
    }

    private async Task UpdateIndexAsync(Guid projectId, CancellationToken ct)
    {
        var files = await ListFilesAsync(projectId, ct);
        var index = new System.Text.StringBuilder();
        index.AppendLine("# MEMORY.md");
        index.AppendLine();
        index.AppendLine("Memory index for this project. Each file holds one typed memory fact.");
        index.AppendLine();
        foreach (var file in files)
            index.AppendLine($"- [{file.FileName}]({file.FileName}) — {file.Description} (type: {file.Type})");

        var indexPath = Path.Combine(GetDir(projectId), "MEMORY.md");
        await File.WriteAllTextAsync(indexPath, index.ToString(), ct);
    }

    private static (string type, string description) ParseFrontmatter(string content)
    {
        var match = FrontmatterRegex.Match(content);
        if (!match.Success) return ("reference", "");
        var fm = match.Groups[1].Value;
        var type = ExtractField(fm, "type") ?? "reference";
        var desc = ExtractField(fm, "description") ?? "";
        return (type, desc);
    }

    private static string StripFrontmatter(string content) =>
        FrontmatterRegex.Replace(content, "").Trim();

    private static string? ExtractField(string frontmatter, string field)
    {
        var match = Regex.Match(frontmatter, $@"{field}:\s*(.+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }
}
