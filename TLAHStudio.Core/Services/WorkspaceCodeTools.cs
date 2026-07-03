using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TLAHStudio.Core.Llm;
using TLAHStudio.Core.Services.Tools;

namespace TLAHStudio.Core.Services;

internal static partial class WorkspaceCodeToolSupport
{
    public static string Resolve(ISandboxCommandService sandbox, Guid chatId, string path) =>
        AgentToolSupport.ResolveSandboxPath(sandbox, chatId, path);

    public static string Relative(ISandboxCommandService sandbox, Guid chatId, string path) =>
        Path.GetRelativePath(sandbox.GetSandboxRoot(chatId), path);

    public static async Task<string> ReadTextAsync(string path, CancellationToken ct)
    {
        var snapshot = await ReadTextSnapshotAsync(path, ct);
        return snapshot.Content;
    }

    public static async Task<WorkspaceTextSnapshot> ReadTextSnapshotAsync(string path, CancellationToken ct)
    {
        var existed = File.Exists(path);
        if (!existed)
        {
            return new WorkspaceTextSnapshot(
                string.Empty,
                new UTF8Encoding(false),
                HasBom: false,
                NewLine: Environment.NewLine,
                Sha256: string.Empty,
                LastWriteTimeUtc: null,
                Existed: false);
        }

        var bytes = await File.ReadAllBytesAsync(path, ct);
        var detection = DetectEncoding(bytes);
        var preambleLength = detection.HasBom ? detection.Encoding.GetPreamble().Length : 0;
        var content = detection.Encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
        return new WorkspaceTextSnapshot(
            content,
            detection.Encoding,
            detection.HasBom,
            DetectNewLine(content),
            Sha256Bytes(bytes),
            File.GetLastWriteTimeUtc(path),
            true);
    }

    public static async Task WriteTextAsync(string path, string content, CancellationToken ct)
    {
        await WriteTextPreservingAsync(path, content, null, ct);
    }

    public static async Task WriteTextPreservingAsync(
        string path,
        string content,
        WorkspaceTextSnapshot? previous,
        CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encoding = previous?.Encoding ?? new UTF8Encoding(false);
        var normalized = NormalizeNewLines(content, previous?.NewLine ?? Environment.NewLine);
        var bytes = encoding.GetBytes(normalized);
        if (previous?.HasBom == true)
        {
            var preamble = encoding.GetPreamble();
            if (preamble.Length > 0)
            {
                var withPreamble = new byte[preamble.Length + bytes.Length];
                Buffer.BlockCopy(preamble, 0, withPreamble, 0, preamble.Length);
                Buffer.BlockCopy(bytes, 0, withPreamble, preamble.Length, bytes.Length);
                bytes = withPreamble;
            }
        }
        await File.WriteAllBytesAsync(path, bytes, ct);
    }

    public static string ComputeFileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static string VerifyExpectedHash(
        string path,
        string expectedSha256)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256))
            return string.Empty;
        if (!File.Exists(path))
            return "The target file no longer exists; refusing to edit because the expected hash cannot be verified.";
        var actual = ComputeFileSha256(path);
        return actual.Equals(expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : $"File changed since it was inspected. Expected SHA256 {expectedSha256}, found {actual}.";
    }

    public static string NormalizeNewLines(string content, string newLine)
    {
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return newLine == "\n"
            ? normalized
            : normalized.Replace("\n", newLine, StringComparison.Ordinal);
    }

    public static string DisplayNewLine(string value) =>
        value switch
        {
            "\r\n" => "CRLF",
            "\n" => "LF",
            "\r" => "CR",
            _ => "unknown"
        };

    public static string FormatSnapshot(WorkspaceTextSnapshot snapshot) =>
        snapshot.Existed
            ? $"SHA256: {snapshot.Sha256}\nEncoding: {snapshot.Encoding.WebName}{(snapshot.HasBom ? " with BOM" : string.Empty)}\nNewline: {DisplayNewLine(snapshot.NewLine)}\nLast write UTC: {snapshot.LastWriteTimeUtc:O}"
            : "File does not exist yet.";

    public static string BuildDiff(string oldContent, string newContent, string label = "file")
    {
        var oldLines = SplitLines(oldContent);
        var newLines = SplitLines(newContent);
        var sb = new StringBuilder();
        sb.AppendLine($"--- {label}");
        sb.AppendLine($"+++ {label}");
        var max = Math.Max(oldLines.Length, newLines.Length);
        for (var i = 0; i < max && sb.Length < 24_000; i++)
        {
            var oldLine = i < oldLines.Length ? oldLines[i] : null;
            var newLine = i < newLines.Length ? newLines[i] : null;
            if (oldLine == newLine)
                continue;
            sb.AppendLine($"@@ line {i + 1} @@");
            if (oldLine != null)
                sb.AppendLine($"- {oldLine}");
            if (newLine != null)
                sb.AppendLine($"+ {newLine}");
        }

        return sb.Length <= $"--- {label}{Environment.NewLine}+++ {label}{Environment.NewLine}".Length
            ? "No textual changes."
            : sb.ToString().TrimEnd();
    }

    public static string[] SplitLines(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    public static bool ShouldSkipPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/.tlah_code_backups/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/.git/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/node_modules/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/packages/", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith("/.tlah_code_backups", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith("/bin", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith("/obj", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith("/.git", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith("/node_modules", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith("/packages", StringComparison.OrdinalIgnoreCase);
    }

    public static Dictionary<string, string> ReadExpectedHashes(JsonElement root)
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("expected_sha256_by_path", out var element) ||
            element.ValueKind != JsonValueKind.Object)
        {
            return hashes;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
                hashes[property.Name.Replace('\\', '/')] = property.Value.GetString() ?? string.Empty;
        }

        return hashes;
    }

    public static string WildcardToRegex(string pattern)
    {
        var normalized = pattern.Replace('\\', '/');
        var escaped = Regex.Escape(normalized)
            .Replace(@"\*\*", ".*", StringComparison.Ordinal)
            .Replace(@"\*", @"[^/]*", StringComparison.Ordinal)
            .Replace(@"\?", ".", StringComparison.Ordinal);
        return $"^{escaped}$";
    }

    public static string HashRelativePath(string relativePath)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(relativePath.Replace('\\', '/').ToLowerInvariant()));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    private static (Encoding Encoding, bool HasBom) DetectEncoding(byte[] bytes)
    {
        if (bytes.Length >= 3 &&
            bytes[0] == 0xEF &&
            bytes[1] == 0xBB &&
            bytes[2] == 0xBF)
        {
            return (new UTF8Encoding(true), true);
        }

        if (bytes.Length >= 2)
        {
            if (bytes[0] == 0xFF && bytes[1] == 0xFE)
                return (Encoding.Unicode, true);
            if (bytes[0] == 0xFE && bytes[1] == 0xFF)
                return (Encoding.BigEndianUnicode, true);
        }

        return (new UTF8Encoding(false), false);
    }

    private static string DetectNewLine(string content)
    {
        var rn = content.IndexOf("\r\n", StringComparison.Ordinal);
        var n = content.IndexOf('\n');
        var r = content.IndexOf('\r');
        if (rn >= 0 && (n < 0 || rn <= n) && (r < 0 || rn <= r))
            return "\r\n";
        if (n >= 0 && (r < 0 || n <= r))
            return "\n";
        if (r >= 0)
            return "\r";
        return Environment.NewLine;
    }

    private static string Sha256Bytes(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static string ReadString(JsonElement root, string name, string fallback = "") =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    public static int ReadInt(JsonElement root, string name, int fallback) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)
            ? number
            : fallback;

    public static bool ReadBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    public static IReadOnlyList<string> ExtractPatchPaths(string patch)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in SplitLines(patch))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("*** Add File: ", StringComparison.Ordinal) ||
                line.StartsWith("*** Update File: ", StringComparison.Ordinal) ||
                line.StartsWith("*** Delete File: ", StringComparison.Ordinal))
            {
                paths.Add(line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim());
                continue;
            }

            if (line.StartsWith("+++ ", StringComparison.Ordinal) ||
                line.StartsWith("--- ", StringComparison.Ordinal))
            {
                var path = line[4..].Trim();
                if (path == "/dev/null")
                    continue;
                if (path.StartsWith("a/", StringComparison.Ordinal) ||
                    path.StartsWith("b/", StringComparison.Ordinal))
                {
                    path = path[2..];
                }
                paths.Add(path);
                continue;
            }

            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in parts.Skip(2).Take(2))
                {
                    var path = part.StartsWith("a/", StringComparison.Ordinal) ||
                               part.StartsWith("b/", StringComparison.Ordinal)
                        ? part[2..]
                        : part;
                    paths.Add(path);
                }
            }
        }

        return paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

internal sealed record WorkspaceTextSnapshot(
    string Content,
    Encoding Encoding,
    bool HasBom,
    string NewLine,
    string Sha256,
    DateTime? LastWriteTimeUtc,
    bool Existed);

internal sealed record WorkspaceBackupMetadata(
    string Id,
    string RelativePath,
    bool Existed,
    DateTime CreatedAtUtc,
    string? BackupRelativePath,
    string Sha256 = "",
    long SizeBytes = 0,
    string EncodingName = "utf-8",
    bool HasBom = false,
    string NewLine = "\n");

internal static class WorkspaceBackupStore
{
    public static async Task<WorkspaceBackupMetadata> CreateAsync(
        ISandboxCommandService sandbox,
        Guid chatId,
        string fullPath,
        CancellationToken ct)
    {
        var root = sandbox.GetSandboxRoot(chatId);
        var relative = Path.GetRelativePath(root, fullPath);
        var hash = WorkspaceCodeToolSupport.HashRelativePath(relative);
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
        var backupDir = Path.Combine(root, ".tlah_code_backups", hash);
        Directory.CreateDirectory(backupDir);
        var existed = File.Exists(fullPath);
        var snapshot = await WorkspaceCodeToolSupport.ReadTextSnapshotAsync(fullPath, ct);
        var backupPath = existed ? Path.Combine(backupDir, $"{stamp}.bak") : null;
        if (backupPath != null)
            File.Copy(fullPath, backupPath, overwrite: true);

        var metadata = new WorkspaceBackupMetadata(
            $"{hash}/{stamp}",
            relative,
            existed,
            DateTime.UtcNow,
            backupPath == null ? null : Path.GetRelativePath(root, backupPath),
            snapshot.Sha256,
            existed ? new FileInfo(fullPath).Length : 0,
            snapshot.Encoding.WebName,
            snapshot.HasBom,
            snapshot.NewLine);
        await File.WriteAllTextAsync(
            Path.Combine(backupDir, $"{stamp}.json"),
            JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false),
            ct);
        return metadata;
    }

    public static async Task<WorkspaceBackupMetadata?> FindAsync(
        ISandboxCommandService sandbox,
        Guid chatId,
        string path,
        string? backupId,
        CancellationToken ct)
    {
        var root = sandbox.GetSandboxRoot(chatId);
        string? metadataPath = null;
        if (!string.IsNullOrWhiteSpace(backupId))
        {
            var safeId = backupId.Replace('\\', '/').Trim('/');
            var parts = safeId.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
                metadataPath = Path.Combine(root, ".tlah_code_backups", parts[0], $"{parts[1]}.json");
        }
        else
        {
            var fullPath = WorkspaceCodeToolSupport.Resolve(sandbox, chatId, path);
            var relative = Path.GetRelativePath(root, fullPath);
            var hash = WorkspaceCodeToolSupport.HashRelativePath(relative);
            var dir = Path.Combine(root, ".tlah_code_backups", hash);
            metadataPath = Directory.Exists(dir)
                ? Directory.EnumerateFiles(dir, "*.json").OrderByDescending(p => p).FirstOrDefault()
                : null;
        }

        if (string.IsNullOrWhiteSpace(metadataPath) || !File.Exists(metadataPath))
            return null;

        var json = await File.ReadAllTextAsync(metadataPath, ct);
        return JsonSerializer.Deserialize<WorkspaceBackupMetadata>(json);
    }
}

public sealed class CodeReadAgentTool : IAgentTool
{
    private readonly ISandboxCommandService _sandbox;
    private readonly IReadFileTracker? _readFileTracker;

    public CodeReadAgentTool(ISandboxCommandService sandbox, IReadFileTracker? readFileTracker = null)
    {
        _sandbox = sandbox;
        _readFileTracker = readFileTracker;
    }

    public LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        AgentToolNames.CodeRead,
        "Read a text file from the chat workspace with optional line slicing.",
        new Dictionary<string, object>
        {
            ["path"] = AgentToolSupport.StringProperty("Relative file path inside the chat workspace."),
            ["start_line"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "1-based first line to read." },
            ["line_count"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Maximum number of lines to read." },
            ["reason"] = AgentToolSupport.StringProperty("Why this file needs to be read.")
        },
        ["path"]);

    public bool RequiresApproval => false;

    public async Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct = default)
    {
        try
        {
            if (!AgentToolSupport.TryParse(argumentsJson, out var root, out var error))
                return new AgentToolResult(false, string.Empty, error);
            var path = WorkspaceCodeToolSupport.Resolve(_sandbox, context.ChatId, WorkspaceCodeToolSupport.ReadString(root, "path"));
            if (!File.Exists(path))
                return new AgentToolResult(false, string.Empty, "File not found.");

            var snapshot = await WorkspaceCodeToolSupport.ReadTextSnapshotAsync(path, ct);
            var lines = WorkspaceCodeToolSupport.SplitLines(snapshot.Content);
            var start = Math.Max(1, WorkspaceCodeToolSupport.ReadInt(root, "start_line", 1));
            var count = Math.Clamp(WorkspaceCodeToolSupport.ReadInt(root, "line_count", 220), 1, 2000);
            var selected = lines.Skip(start - 1).Take(count).Select((line, index) => $"{start + index,5}: {line}");
            // M4.5.0: Track the read for subsequent write/edit guards.
            _readFileTracker?.MarkRead(path, System.IO.File.GetLastWriteTimeUtc(path));
            var output = $"File: {WorkspaceCodeToolSupport.Relative(_sandbox, context.ChatId, path)}\nLines: {lines.Length}\n{WorkspaceCodeToolSupport.FormatSnapshot(snapshot)}\n\n" +
                         string.Join(Environment.NewLine, selected);
            return new AgentToolResult(true, AgentToolSupport.Limit(output, context.MaxOutputChars));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AgentToolResult(false, string.Empty, ex.Message);
        }
    }
}

public sealed class CodeGlobAgentTool : IAgentTool
{
    private readonly ISandboxCommandService _sandbox;

    public CodeGlobAgentTool(ISandboxCommandService sandbox)
    {
        _sandbox = sandbox;
    }

    public LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        AgentToolNames.CodeGlob,
        "Find files and folders in the chat workspace using wildcard patterns such as **/*.cs.",
        new Dictionary<string, object>
        {
            ["pattern"] = AgentToolSupport.StringProperty("Wildcard pattern relative to the workspace root."),
            ["path"] = AgentToolSupport.StringProperty("Relative directory to search from. Defaults to root."),
            ["max_results"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Maximum paths to return." },
            ["reason"] = AgentToolSupport.StringProperty("Why this glob search is needed.")
        },
        ["pattern"]);

    public bool RequiresApproval => false;

    public Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct = default)
    {
        try
        {
            if (!AgentToolSupport.TryParse(argumentsJson, out var root, out var error))
                return Task.FromResult(new AgentToolResult(false, string.Empty, error));
            var searchRoot = WorkspaceCodeToolSupport.Resolve(_sandbox, context.ChatId, WorkspaceCodeToolSupport.ReadString(root, "path", "."));
            if (!Directory.Exists(searchRoot))
                return Task.FromResult(new AgentToolResult(false, string.Empty, "Search path is not a directory."));
            var pattern = WorkspaceCodeToolSupport.ReadString(root, "pattern", "**/*");
            var regex = new Regex(WorkspaceCodeToolSupport.WildcardToRegex(pattern), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var max = Math.Clamp(WorkspaceCodeToolSupport.ReadInt(root, "max_results", 200), 1, 2000);
            var baseRoot = _sandbox.GetSandboxRoot(context.ChatId);
            var matches = Directory
                .EnumerateFileSystemEntries(searchRoot, "*", SearchOption.AllDirectories)
                .Where(p => !WorkspaceCodeToolSupport.ShouldSkipPath(p))
                .Select(p => Path.GetRelativePath(baseRoot, p).Replace('\\', '/'))
                .Where(p => !p.StartsWith(".tlah_code_backups/", StringComparison.OrdinalIgnoreCase))
                .Where(p => regex.IsMatch(p))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .Take(max)
                .ToArray();
            return Task.FromResult(new AgentToolResult(
                true,
                matches.Length == 0 ? "No matches." : string.Join(Environment.NewLine, matches)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Task.FromResult(new AgentToolResult(false, string.Empty, ex.Message));
        }
    }
}

public sealed class CodeGrepAgentTool : IAgentTool
{
    private readonly ISandboxCommandService _sandbox;

    public CodeGrepAgentTool(ISandboxCommandService sandbox)
    {
        _sandbox = sandbox;
    }

    public LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        AgentToolNames.CodeGrep,
        "Search text files in the chat workspace using literal text or regex.",
        new Dictionary<string, object>
        {
            ["query"] = AgentToolSupport.StringProperty("Text or regex to search for."),
            ["path"] = AgentToolSupport.StringProperty("Relative file or directory path. Defaults to root."),
            ["regex"] = AgentToolSupport.BooleanProperty("Treat query as a .NET regular expression."),
            ["case_sensitive"] = AgentToolSupport.BooleanProperty("Use case-sensitive matching."),
            ["max_results"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Maximum matches to return." },
            ["reason"] = AgentToolSupport.StringProperty("Why this search is needed.")
        },
        ["query"]);

    public bool RequiresApproval => false;

    public async Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct = default)
    {
        try
        {
            if (!AgentToolSupport.TryParse(argumentsJson, out var root, out var error))
                return new AgentToolResult(false, string.Empty, error);
            var query = WorkspaceCodeToolSupport.ReadString(root, "query");
            if (string.IsNullOrWhiteSpace(query))
                return new AgentToolResult(false, string.Empty, "The query argument is required.");
            var start = WorkspaceCodeToolSupport.Resolve(_sandbox, context.ChatId, WorkspaceCodeToolSupport.ReadString(root, "path", "."));
            var files = File.Exists(start)
                ? [start]
                : Directory.Exists(start)
                    ? Directory.EnumerateFiles(start, "*", SearchOption.AllDirectories)
                        .Where(p => !WorkspaceCodeToolSupport.ShouldSkipPath(p))
                        .ToArray()
                    : [];
            var max = Math.Clamp(WorkspaceCodeToolSupport.ReadInt(root, "max_results", 200), 1, 2000);
            var caseSensitive = WorkspaceCodeToolSupport.ReadBool(root, "case_sensitive");
            var useRegex = WorkspaceCodeToolSupport.ReadBool(root, "regex");
            var regex = useRegex
                ? new Regex(query, RegexOptions.CultureInvariant | (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase))
                : null;
            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var output = new StringBuilder();
            var count = 0;
            foreach (var file in files.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                if (ct.IsCancellationRequested || count >= max || IsProbablyBinary(file))
                    continue;
                var text = await WorkspaceCodeToolSupport.ReadTextAsync(file, ct);
                var lines = WorkspaceCodeToolSupport.SplitLines(text);
                for (var i = 0; i < lines.Length && count < max; i++)
                {
                    var matched = regex?.IsMatch(lines[i]) ?? lines[i].Contains(query, comparison);
                    if (!matched)
                        continue;
                    output.AppendLine($"{WorkspaceCodeToolSupport.Relative(_sandbox, context.ChatId, file)}:{i + 1}: {lines[i]}");
                    count++;
                }
            }

            return new AgentToolResult(true, count == 0 ? "No matches." : output.ToString().TrimEnd());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AgentToolResult(false, string.Empty, ex.Message);
        }
    }

    private static bool IsProbablyBinary(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".zip" or ".7z" or ".rar" or ".dll" or ".exe";
    }
}

public sealed class CodeSymbolsAgentTool : IAgentTool
{
    private static readonly string[] SupportedExtensions =
    [
        ".cs", ".xaml", ".xml", ".ts", ".tsx", ".js", ".jsx", ".py", ".md", ".razor"
    ];

    private static readonly Regex CSharpTypeRegex = new(
        @"^\s*(?:\[[^\]]+\]\s*)*(?:public|private|protected|internal|sealed|static|abstract|partial|readonly|record|\s)*\s*(?<kind>class|interface|record|struct|enum)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CSharpMethodRegex = new(
        @"^\s*(?:public|private|protected|internal|static|async|virtual|override|sealed|partial|extern|new|\s)+\s+(?<return>[A-Za-z_][A-Za-z0-9_<>,\[\]\.? ]*)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex JsSymbolRegex = new(
        @"^\s*(?:export\s+)?(?:(?<kind>class|function)\s+(?<name>[A-Za-z_$][A-Za-z0-9_$]*)|(?:const|let|var)\s+(?<name>[A-Za-z_$][A-Za-z0-9_$]*)\s*=\s*(?:async\s*)?(?:\([^)]*\)|[A-Za-z_$][A-Za-z0-9_$]*)\s*=>)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PythonSymbolRegex = new(
        @"^\s*(?<kind>class|def|async\s+def)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*[\(:]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MarkdownHeadingRegex = new(
        @"^\s*(?<marks>#{1,6})\s+(?<name>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ISandboxCommandService _sandbox;

    public CodeSymbolsAgentTool(ISandboxCommandService sandbox)
    {
        _sandbox = sandbox;
    }

    public LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        AgentToolNames.CodeSymbols,
        "List lightweight code symbols in a file or directory: classes, interfaces, records, structs, enums, methods, functions, and markdown headings.",
        new Dictionary<string, object>
        {
            ["path"] = AgentToolSupport.StringProperty("Relative file or directory path. Defaults to root."),
            ["pattern"] = AgentToolSupport.StringProperty("Optional wildcard path filter such as **/*.cs."),
            ["max_results"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Maximum symbols to return." },
            ["reason"] = AgentToolSupport.StringProperty("Why symbol discovery is needed.")
        });

    public bool RequiresApproval => false;

    public async Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct = default)
    {
        try
        {
            if (!AgentToolSupport.TryParse(argumentsJson, out var root, out var error))
                return new AgentToolResult(false, string.Empty, error);
            var start = WorkspaceCodeToolSupport.Resolve(_sandbox, context.ChatId, WorkspaceCodeToolSupport.ReadString(root, "path", "."));
            var pattern = WorkspaceCodeToolSupport.ReadString(root, "pattern");
            var max = Math.Clamp(WorkspaceCodeToolSupport.ReadInt(root, "max_results", 300), 1, 2000);
            var baseRoot = _sandbox.GetSandboxRoot(context.ChatId);
            var pathRegex = string.IsNullOrWhiteSpace(pattern)
                ? null
                : new Regex(WorkspaceCodeToolSupport.WildcardToRegex(pattern), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            var output = new StringBuilder();
            var count = 0;
            foreach (var file in EnumerateCandidateFiles(start).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(baseRoot, file).Replace('\\', '/');
                if (pathRegex != null && !pathRegex.IsMatch(relative))
                    continue;
                foreach (var symbol in await ExtractSymbolsAsync(file, relative, ct))
                {
                    output.AppendLine($"{symbol.Path}:{symbol.Line}: {symbol.Kind} {symbol.Name} - {symbol.Preview}");
                    count++;
                    if (count >= max)
                        return new AgentToolResult(true, output.ToString().TrimEnd());
                }
            }

            return new AgentToolResult(true, count == 0 ? "No symbols found." : output.ToString().TrimEnd());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AgentToolResult(false, string.Empty, ex.Message);
        }
    }

    private static IEnumerable<string> EnumerateCandidateFiles(string start)
    {
        if (File.Exists(start))
        {
            if (IsSupported(start))
                yield return start;
            yield break;
        }

        if (!Directory.Exists(start))
            yield break;

        foreach (var file in Directory.EnumerateFiles(start, "*", SearchOption.AllDirectories))
        {
            if (WorkspaceCodeToolSupport.ShouldSkipPath(file) || !IsSupported(file))
                continue;
            yield return file;
        }
    }

    private static bool IsSupported(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant(), StringComparer.OrdinalIgnoreCase);

    private static async Task<IReadOnlyList<SymbolHit>> ExtractSymbolsAsync(string file, string relative, CancellationToken ct)
    {
        var extension = Path.GetExtension(file).ToLowerInvariant();
        var text = await WorkspaceCodeToolSupport.ReadTextAsync(file, ct);
        var lines = WorkspaceCodeToolSupport.SplitLines(text);
        var symbols = new List<SymbolHit>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var symbol = extension switch
            {
                ".cs" or ".razor" => ExtractCSharpSymbol(line),
                ".ts" or ".tsx" or ".js" or ".jsx" => ExtractJavaScriptSymbol(line),
                ".py" => ExtractPythonSymbol(line),
                ".md" => ExtractMarkdownSymbol(line),
                ".xaml" or ".xml" => ExtractXmlSymbol(line),
                _ => null
            };
            if (symbol == null)
                continue;
            symbols.Add(new SymbolHit(relative, i + 1, symbol.Value.Kind, symbol.Value.Name, TrimPreview(line)));
        }

        return symbols;
    }

    private static (string Kind, string Name)? ExtractCSharpSymbol(string line)
    {
        var type = CSharpTypeRegex.Match(line);
        if (type.Success)
            return (type.Groups["kind"].Value, type.Groups["name"].Value);
        var method = CSharpMethodRegex.Match(line);
        if (method.Success)
        {
            var name = method.Groups["name"].Value;
            if (name is "if" or "for" or "foreach" or "while" or "switch" or "catch")
                return null;
            return ("method", name);
        }

        return null;
    }

    private static (string Kind, string Name)? ExtractJavaScriptSymbol(string line)
    {
        var match = JsSymbolRegex.Match(line);
        if (!match.Success)
            return null;
        var kind = match.Groups["kind"].Success && !string.IsNullOrWhiteSpace(match.Groups["kind"].Value)
            ? match.Groups["kind"].Value
            : "function";
        return (kind, match.Groups["name"].Value);
    }

    private static (string Kind, string Name)? ExtractPythonSymbol(string line)
    {
        var match = PythonSymbolRegex.Match(line);
        if (!match.Success)
            return null;
        var kind = match.Groups["kind"].Value.Replace("async ", string.Empty, StringComparison.Ordinal);
        return (kind == "def" ? "function" : kind, match.Groups["name"].Value);
    }

    private static (string Kind, string Name)? ExtractMarkdownSymbol(string line)
    {
        var match = MarkdownHeadingRegex.Match(line);
        if (!match.Success)
            return null;
        return ($"heading{match.Groups["marks"].Value.Length}", match.Groups["name"].Value.Trim());
    }

    private static (string Kind, string Name)? ExtractXmlSymbol(string line)
    {
        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith('<') || trimmed.StartsWith("</", StringComparison.Ordinal))
            return null;
        var end = trimmed.IndexOfAny([' ', '>', '/']);
        if (end <= 1)
            return null;
        return ("element", trimmed[1..end]);
    }

    private static string TrimPreview(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length <= 180 ? trimmed : trimmed[..180] + "...";
    }

    private sealed record SymbolHit(string Path, int Line, string Kind, string Name, string Preview);
}

public sealed class CodeDiffAgentTool : IAgentTool
{
    private readonly ISandboxCommandService _sandbox;

    public CodeDiffAgentTool(ISandboxCommandService sandbox)
    {
        _sandbox = sandbox;
    }

    public LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        AgentToolNames.CodeDiff,
        "Preview a textual diff between an existing workspace file and proposed content.",
        new Dictionary<string, object>
        {
            ["path"] = AgentToolSupport.StringProperty("Relative file path inside the workspace."),
            ["proposed_content"] = AgentToolSupport.StringProperty("Proposed full file content to compare."),
            ["reason"] = AgentToolSupport.StringProperty("Why this diff is needed.")
        },
        ["path", "proposed_content"]);

    public bool RequiresApproval => false;

    public async Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct = default)
    {
        try
        {
            if (!AgentToolSupport.TryParse(argumentsJson, out var root, out var error))
                return new AgentToolResult(false, string.Empty, error);
            var path = WorkspaceCodeToolSupport.Resolve(_sandbox, context.ChatId, WorkspaceCodeToolSupport.ReadString(root, "path"));
            var snapshot = await WorkspaceCodeToolSupport.ReadTextSnapshotAsync(path, ct);
            var oldContent = snapshot.Content;
            var proposed = WorkspaceCodeToolSupport.ReadString(root, "proposed_content");
            var normalizedProposed = WorkspaceCodeToolSupport.NormalizeNewLines(proposed, snapshot.NewLine);
            var diff = WorkspaceCodeToolSupport.BuildDiff(oldContent, normalizedProposed, WorkspaceCodeToolSupport.Relative(_sandbox, context.ChatId, path));
            var output = $"File: {WorkspaceCodeToolSupport.Relative(_sandbox, context.ChatId, path)}\n{WorkspaceCodeToolSupport.FormatSnapshot(snapshot)}\n\n{diff}";
            return new AgentToolResult(true, AgentToolSupport.Limit(output, context.MaxOutputChars));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AgentToolResult(false, string.Empty, ex.Message);
        }
    }
}

public sealed class CodeEditAgentTool : IAgentTool
{
    private readonly ISandboxCommandService _sandbox;
    private readonly IReadFileTracker? _readFileTracker;

    public CodeEditAgentTool(ISandboxCommandService sandbox, IReadFileTracker? readFileTracker = null)
    {
        _sandbox = sandbox;
        _readFileTracker = readFileTracker;
    }

    public LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        AgentToolNames.CodeEdit,
        "Edit one file by replacing exact text, or create it when create_if_missing is true.",
        new Dictionary<string, object>
        {
            ["path"] = AgentToolSupport.StringProperty("Relative file path inside the workspace."),
            ["old_text"] = AgentToolSupport.StringProperty("Exact text to replace. Required unless creating a missing file."),
            ["new_text"] = AgentToolSupport.StringProperty("Replacement text or new file content."),
            ["replace_all"] = AgentToolSupport.BooleanProperty("Replace every occurrence instead of only the first."),
            ["create_if_missing"] = AgentToolSupport.BooleanProperty("Create the file if it does not exist."),
            ["expected_sha256"] = AgentToolSupport.StringProperty("Optional SHA256 from a prior read/diff. The edit is rejected if the file changed."),
            ["reason"] = AgentToolSupport.StringProperty("Why this edit is needed.")
        },
        ["path", "new_text"]);

    public bool RequiresApproval => true;

    public async Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct = default)
    {
        try
        {
            if (!AgentToolSupport.TryParse(argumentsJson, out var root, out var error))
                return new AgentToolResult(false, string.Empty, error);
            var path = WorkspaceCodeToolSupport.Resolve(_sandbox, context.ChatId, WorkspaceCodeToolSupport.ReadString(root, "path"));
            var oldText = WorkspaceCodeToolSupport.ReadString(root, "old_text");
            var newText = WorkspaceCodeToolSupport.ReadString(root, "new_text");
            var replaceAll = WorkspaceCodeToolSupport.ReadBool(root, "replace_all");
            var createIfMissing = WorkspaceCodeToolSupport.ReadBool(root, "create_if_missing");
            var expectedHash = WorkspaceCodeToolSupport.ReadString(root, "expected_sha256");
            // M4.5.0: Require the file to have been read before editing.
            if (File.Exists(path) && _readFileTracker != null && !_readFileTracker.WasRead(path))
                return new AgentToolResult(false, string.Empty,
                    $"Cannot edit — the file has not been read in this session. Use the read tool first.");
            // M4.5.0: Detect stale edits — file modified externally after being read.
            if (File.Exists(path) && _readFileTracker != null)
            {
                var recordedMtime = _readFileTracker.GetLastReadMtimeUtc(path);
                var currentMtime = File.GetLastWriteTimeUtc(path);
                if (recordedMtime.HasValue && currentMtime != recordedMtime.Value)
                    return new AgentToolResult(false, string.Empty,
                        $"Cannot edit — the file was modified externally since it was last read. Re-read first.");
            }
            var conflict = WorkspaceCodeToolSupport.VerifyExpectedHash(path, expectedHash);
            if (!string.IsNullOrWhiteSpace(conflict))
                return new AgentToolResult(false, string.Empty, conflict);

            var before = await WorkspaceCodeToolSupport.ReadTextSnapshotAsync(path, ct);
            var oldContent = before.Content;
            if (!File.Exists(path) && !createIfMissing)
                return new AgentToolResult(false, string.Empty, "File not found. Set create_if_missing to true to create it.");

            string newContent;
            if (!File.Exists(path))
            {
                newContent = newText;
            }
            else
            {
                if (string.IsNullOrEmpty(oldText))
                    return new AgentToolResult(false, string.Empty, "old_text is required when editing an existing file.");
                if (!oldContent.Contains(oldText, StringComparison.Ordinal))
                    return new AgentToolResult(false, string.Empty, "old_text was not found exactly once in the file.");
                newContent = replaceAll
                    ? oldContent.Replace(oldText, newText, StringComparison.Ordinal)
                    : ReplaceFirst(oldContent, oldText, newText);
            }

            var backup = await WorkspaceBackupStore.CreateAsync(_sandbox, context.ChatId, path, ct);
            await WorkspaceCodeToolSupport.WriteTextPreservingAsync(path, newContent, before, ct);
            var diff = WorkspaceCodeToolSupport.BuildDiff(oldContent, newContent, WorkspaceCodeToolSupport.Relative(_sandbox, context.ChatId, path));
            var artifact = await AgentToolSupport.ArtifactAsync(_sandbox.GetSandboxRoot(context.ChatId), path, ct);
            return new AgentToolResult(
                true,
                $"Edited {artifact.RelativePath}\nBackup: {backup.Id}\nBefore SHA256: {before.Sha256}\nAfter SHA256: {WorkspaceCodeToolSupport.ComputeFileSha256(path)}\nEncoding: {before.Encoding.WebName}{(before.HasBom ? " with BOM" : string.Empty)}\nNewline: {WorkspaceCodeToolSupport.DisplayNewLine(before.NewLine)}\n\n{AgentToolSupport.Limit(diff, context.MaxOutputChars)}",
                Artifacts: [artifact]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AgentToolResult(false, string.Empty, ex.Message);
        }
    }

    private static string ReplaceFirst(string text, string oldText, string newText)
    {
        var index = text.IndexOf(oldText, StringComparison.Ordinal);
        return index < 0
            ? text
            : text[..index] + newText + text[(index + oldText.Length)..];
    }
}

public sealed class CodeMultiEditAgentTool : IAgentTool
{
    private readonly ISandboxCommandService _sandbox;
    private readonly IReadFileTracker? _readFileTracker;

    public CodeMultiEditAgentTool(ISandboxCommandService sandbox, IReadFileTracker? readFileTracker = null)
    {
        _sandbox = sandbox;
        _readFileTracker = readFileTracker;
    }

    public LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        AgentToolNames.CodeMultiEdit,
        "Apply multiple exact-text replacements to one file in order.",
        new Dictionary<string, object>
        {
            ["path"] = AgentToolSupport.StringProperty("Relative file path inside the workspace."),
            ["edits"] = new Dictionary<string, object>
            {
                ["type"] = "array",
                ["description"] = "Array of {old_text,new_text,replace_all} edits.",
                ["items"] = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["old_text"] = AgentToolSupport.StringProperty("Exact text to replace."),
                        ["new_text"] = AgentToolSupport.StringProperty("Replacement text."),
                        ["replace_all"] = AgentToolSupport.BooleanProperty("Replace all occurrences.")
                    },
                    ["required"] = new[] { "old_text", "new_text" },
                    ["additionalProperties"] = false
                }
            },
            ["expected_sha256"] = AgentToolSupport.StringProperty("Optional SHA256 from a prior read/diff. The edit is rejected if the file changed."),
            ["reason"] = AgentToolSupport.StringProperty("Why these edits are needed.")
        },
        ["path", "edits"]);

    public bool RequiresApproval => true;

    public async Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct = default)
    {
        try
        {
            if (!AgentToolSupport.TryParse(argumentsJson, out var root, out var error))
                return new AgentToolResult(false, string.Empty, error);
            var path = WorkspaceCodeToolSupport.Resolve(_sandbox, context.ChatId, WorkspaceCodeToolSupport.ReadString(root, "path"));
            // M4.5.0: Read-before-edit guard.
            if (File.Exists(path) && _readFileTracker != null && !_readFileTracker.WasRead(path))
                return new AgentToolResult(false, string.Empty,
                    "Cannot multi-edit — the file has not been read in this session. Use the read tool first.");
            if (File.Exists(path) && _readFileTracker != null)
            {
                var recordedMtime = _readFileTracker.GetLastReadMtimeUtc(path);
                var currentMtime = File.GetLastWriteTimeUtc(path);
                if (recordedMtime.HasValue && currentMtime != recordedMtime.Value)
                    return new AgentToolResult(false, string.Empty,
                        "Cannot multi-edit — the file was modified externally since it was last read. Re-read first.");
            }
            if (!File.Exists(path))
                return new AgentToolResult(false, string.Empty, "File not found.");
            var expectedHash = WorkspaceCodeToolSupport.ReadString(root, "expected_sha256");
            var conflict = WorkspaceCodeToolSupport.VerifyExpectedHash(path, expectedHash);
            if (!string.IsNullOrWhiteSpace(conflict))
                return new AgentToolResult(false, string.Empty, conflict);
            if (!root.TryGetProperty("edits", out var editsElement) || editsElement.ValueKind != JsonValueKind.Array)
                return new AgentToolResult(false, string.Empty, "edits must be an array.");

            var before = await WorkspaceCodeToolSupport.ReadTextSnapshotAsync(path, ct);
            var oldContent = before.Content;
            var newContent = oldContent;
            var applied = 0;
            foreach (var edit in editsElement.EnumerateArray())
            {
                var oldText = WorkspaceCodeToolSupport.ReadString(edit, "old_text");
                var newText = WorkspaceCodeToolSupport.ReadString(edit, "new_text");
                var replaceAll = WorkspaceCodeToolSupport.ReadBool(edit, "replace_all");
                if (string.IsNullOrEmpty(oldText))
                    return new AgentToolResult(false, string.Empty, "Each edit requires old_text.");
                if (!newContent.Contains(oldText, StringComparison.Ordinal))
                    return new AgentToolResult(false, string.Empty, $"old_text for edit {applied + 1} was not found.");
                newContent = replaceAll
                    ? newContent.Replace(oldText, newText, StringComparison.Ordinal)
                    : ReplaceFirst(newContent, oldText, newText);
                applied++;
            }

            var backup = await WorkspaceBackupStore.CreateAsync(_sandbox, context.ChatId, path, ct);
            await WorkspaceCodeToolSupport.WriteTextPreservingAsync(path, newContent, before, ct);
            var diff = WorkspaceCodeToolSupport.BuildDiff(oldContent, newContent, WorkspaceCodeToolSupport.Relative(_sandbox, context.ChatId, path));
            var artifact = await AgentToolSupport.ArtifactAsync(_sandbox.GetSandboxRoot(context.ChatId), path, ct);
            return new AgentToolResult(
                true,
                $"Applied {applied} edits to {artifact.RelativePath}\nBackup: {backup.Id}\nBefore SHA256: {before.Sha256}\nAfter SHA256: {WorkspaceCodeToolSupport.ComputeFileSha256(path)}\nEncoding: {before.Encoding.WebName}{(before.HasBom ? " with BOM" : string.Empty)}\nNewline: {WorkspaceCodeToolSupport.DisplayNewLine(before.NewLine)}\n\n{AgentToolSupport.Limit(diff, context.MaxOutputChars)}",
                Artifacts: [artifact]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AgentToolResult(false, string.Empty, ex.Message);
        }
    }

    private static string ReplaceFirst(string text, string oldText, string newText)
    {
        var index = text.IndexOf(oldText, StringComparison.Ordinal);
        return index < 0
            ? text
            : text[..index] + newText + text[(index + oldText.Length)..];
    }
}

public sealed class CodeApplyPatchAgentTool : IAgentTool
{
    private readonly ISandboxCommandService _sandbox;

    public CodeApplyPatchAgentTool(ISandboxCommandService sandbox)
    {
        _sandbox = sandbox;
    }

    public LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        AgentToolNames.CodeApplyPatch,
        "Apply a unified diff patch to files inside the chat workspace.",
        new Dictionary<string, object>
        {
            ["patch"] = AgentToolSupport.StringProperty("Unified diff patch. Paths must be relative to the workspace root."),
            ["preview_only"] = AgentToolSupport.BooleanProperty("Validate the patch and list affected files without applying it."),
            ["expected_sha256_by_path"] = new Dictionary<string, object>
            {
                ["type"] = "object",
                ["description"] = "Optional map of relative path to SHA256 from a prior read/diff. The patch is rejected if any file changed.",
                ["additionalProperties"] = new Dictionary<string, object> { ["type"] = "string" }
            },
            ["reason"] = AgentToolSupport.StringProperty("Why this patch is needed.")
        },
        ["patch"]);

    public bool RequiresApproval => true;

    public async Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct = default)
    {
        try
        {
            if (!AgentToolSupport.TryParse(argumentsJson, out var root, out var error))
                return new AgentToolResult(false, string.Empty, error);
            var patch = WorkspaceCodeToolSupport.ReadString(root, "patch");
            if (string.IsNullOrWhiteSpace(patch))
                return new AgentToolResult(false, string.Empty, "patch is required.");
            var rootPath = _sandbox.GetSandboxRoot(context.ChatId);
            var previewOnly = WorkspaceCodeToolSupport.ReadBool(root, "preview_only");
            var paths = WorkspaceCodeToolSupport.ExtractPatchPaths(patch);
            if (paths.Count == 0)
                return new AgentToolResult(false, string.Empty, "No relative paths were found in the patch.");

            var expectedHashes = WorkspaceCodeToolSupport.ReadExpectedHashes(root);
            foreach (var relative in paths)
            {
                var normalized = relative.Replace('\\', '/');
                if (!expectedHashes.TryGetValue(normalized, out var expectedHash) &&
                    !expectedHashes.TryGetValue(Path.GetFileName(normalized), out expectedHash))
                {
                    continue;
                }

                var full = WorkspaceCodeToolSupport.Resolve(_sandbox, context.ChatId, relative);
                var conflict = WorkspaceCodeToolSupport.VerifyExpectedHash(full, expectedHash);
                if (!string.IsNullOrWhiteSpace(conflict))
                    return new AgentToolResult(false, string.Empty, $"{relative}: {conflict}");
            }

            var check = await RunGitApplyAsync(rootPath, patch, "apply --check --whitespace=nowarn -", ct);
            if (check.ExitCode != 0)
            {
                return new AgentToolResult(
                    false,
                    AgentToolSupport.Limit($"Patch check failed.\nstdout:\n{check.Stdout}\n\nstderr:\n{check.Stderr}", context.MaxOutputChars),
                    "git apply --check failed.");
            }

            if (previewOnly)
            {
                var preview = new StringBuilder();
                preview.AppendLine("Patch check passed. No files were changed.");
                foreach (var path in paths)
                {
                    var full = WorkspaceCodeToolSupport.Resolve(_sandbox, context.ChatId, path);
                    var snapshot = await WorkspaceCodeToolSupport.ReadTextSnapshotAsync(full, ct);
                    preview.AppendLine($"- {path}: {(snapshot.Existed ? snapshot.Sha256 : "new file")}");
                }

                return new AgentToolResult(true, preview.ToString().TrimEnd());
            }

            var backups = new List<WorkspaceBackupMetadata>();
            foreach (var path in paths)
            {
                var full = WorkspaceCodeToolSupport.Resolve(_sandbox, context.ChatId, path);
                backups.Add(await WorkspaceBackupStore.CreateAsync(_sandbox, context.ChatId, full, ct));
            }

            var apply = await RunGitApplyAsync(rootPath, patch, "apply --whitespace=nowarn -", ct);
            if (apply.ExitCode != 0)
                await RestoreBackupsAsync(rootPath, backups, ct);

            var artifacts = new List<AgentToolArtifact>();
            foreach (var path in paths)
            {
                var full = WorkspaceCodeToolSupport.Resolve(_sandbox, context.ChatId, path);
                if (File.Exists(full))
                    artifacts.Add(await AgentToolSupport.ArtifactAsync(rootPath, full, ct));
            }

            var hashes = new StringBuilder();
            foreach (var path in paths)
            {
                var full = WorkspaceCodeToolSupport.Resolve(_sandbox, context.ChatId, path);
                hashes.AppendLine(File.Exists(full)
                    ? $"- {path}: {WorkspaceCodeToolSupport.ComputeFileSha256(full)}"
                    : $"- {path}: deleted");
            }

            var output = $"""
                Exit code: {apply.ExitCode}
                Backups:
                {string.Join(Environment.NewLine, backups.Select(b => $"- {b.RelativePath.Replace('\\', '/')}: {b.Id}"))}
                Result SHA256:
                {hashes}

                stdout:
                {apply.Stdout}

                stderr:
                {apply.Stderr}
                """;
            return new AgentToolResult(apply.ExitCode == 0, AgentToolSupport.Limit(output, context.MaxOutputChars), apply.ExitCode == 0 ? null : "git apply failed and changes were rolled back.", artifacts);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AgentToolResult(false, string.Empty, ex.Message);
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunGitApplyAsync(
        string rootPath,
        string patch,
        string arguments,
        CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = rootPath,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };
        process.Start();
        await process.StandardInput.WriteAsync(patch.AsMemory(), ct);
        process.StandardInput.Close();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static async Task RestoreBackupsAsync(
        string rootPath,
        IReadOnlyList<WorkspaceBackupMetadata> backups,
        CancellationToken ct)
    {
        foreach (var backup in backups)
        {
            var target = Path.GetFullPath(Path.Combine(rootPath, backup.RelativePath));
            if (!backup.Existed)
            {
                if (File.Exists(target))
                    File.Delete(target);
                continue;
            }

            var backupPath = Path.GetFullPath(Path.Combine(rootPath, backup.BackupRelativePath ?? string.Empty));
            if (!File.Exists(backupPath))
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var source = File.OpenRead(backupPath);
            await using var destination = File.Create(target);
            await source.CopyToAsync(destination, ct);
        }
    }
}

public sealed class CodeRollbackAgentTool : IAgentTool
{
    private readonly ISandboxCommandService _sandbox;

    public CodeRollbackAgentTool(ISandboxCommandService sandbox)
    {
        _sandbox = sandbox;
    }

    public LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        AgentToolNames.CodeRollback,
        "Restore a file to a backup created by edit, multi_edit, or apply_patch.",
        new Dictionary<string, object>
        {
            ["path"] = AgentToolSupport.StringProperty("Relative file path. Used to find the latest backup when backup_id is omitted."),
            ["backup_id"] = AgentToolSupport.StringProperty("Backup id returned by a write tool, for example abcd1234/20260627010203000."),
            ["preview_only"] = AgentToolSupport.BooleanProperty("Show what rollback would change without restoring files."),
            ["expected_sha256"] = AgentToolSupport.StringProperty("Optional current SHA256. The rollback is rejected if the target changed."),
            ["reason"] = AgentToolSupport.StringProperty("Why this rollback is needed.")
        },
        ["path"]);

    public bool RequiresApproval => true;

    public async Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct = default)
    {
        try
        {
            if (!AgentToolSupport.TryParse(argumentsJson, out var root, out var error))
                return new AgentToolResult(false, string.Empty, error);
            var pathArg = WorkspaceCodeToolSupport.ReadString(root, "path");
            var backupId = WorkspaceCodeToolSupport.ReadString(root, "backup_id");
            var previewOnly = WorkspaceCodeToolSupport.ReadBool(root, "preview_only");
            var backup = await WorkspaceBackupStore.FindAsync(_sandbox, context.ChatId, pathArg, backupId, ct);
            if (backup == null)
                return new AgentToolResult(false, string.Empty, "No matching backup was found.");
            var rootPath = _sandbox.GetSandboxRoot(context.ChatId);
            var target = WorkspaceCodeToolSupport.Resolve(_sandbox, context.ChatId, backup.RelativePath);
            var expectedHash = WorkspaceCodeToolSupport.ReadString(root, "expected_sha256");
            var conflict = WorkspaceCodeToolSupport.VerifyExpectedHash(target, expectedHash);
            if (!string.IsNullOrWhiteSpace(conflict))
                return new AgentToolResult(false, string.Empty, conflict);

            var current = await WorkspaceCodeToolSupport.ReadTextSnapshotAsync(target, ct);
            if (!backup.Existed)
            {
                if (previewOnly)
                {
                    return new AgentToolResult(
                        true,
                        File.Exists(target)
                            ? $"Rollback preview: {backup.RelativePath} would be deleted.\nCurrent SHA256: {current.Sha256}"
                            : $"Rollback preview: {backup.RelativePath} is already absent.");
                }

                if (File.Exists(target))
                    File.Delete(target);
                return new AgentToolResult(true, $"Rolled back {backup.RelativePath} by deleting the file created after backup {backup.Id}.");
            }

            var backupPath = Path.Combine(rootPath, backup.BackupRelativePath ?? string.Empty);
            if (!File.Exists(backupPath))
                return new AgentToolResult(false, string.Empty, "Backup content file is missing.");
            var backupSnapshot = await WorkspaceCodeToolSupport.ReadTextSnapshotAsync(backupPath, ct);
            if (previewOnly)
            {
                var diff = WorkspaceCodeToolSupport.BuildDiff(current.Content, backupSnapshot.Content, backup.RelativePath);
                return new AgentToolResult(
                    true,
                    AgentToolSupport.Limit(
                        $"Rollback preview for {backup.RelativePath}\nBackup: {backup.Id}\nCurrent SHA256: {current.Sha256}\nBackup SHA256: {backupSnapshot.Sha256}\n\n{diff}",
                        context.MaxOutputChars));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(backupPath, target, overwrite: true);
            var artifact = await AgentToolSupport.ArtifactAsync(rootPath, target, ct);
            return new AgentToolResult(
                true,
                $"Restored {backup.RelativePath} from backup {backup.Id}.\nBefore SHA256: {current.Sha256}\nAfter SHA256: {WorkspaceCodeToolSupport.ComputeFileSha256(target)}",
                Artifacts: [artifact]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AgentToolResult(false, string.Empty, ex.Message);
        }
    }
}

public sealed class CodeDiagnosticsAgentTool : IAgentTool
{
    private readonly ISandboxCommandService _sandbox;

    public CodeDiagnosticsAgentTool(ISandboxCommandService sandbox)
    {
        _sandbox = sandbox;
    }

    public LlmToolDefinition Definition { get; } = AgentToolSupport.Definition(
        AgentToolNames.CodeDiagnostics,
        "Run lightweight diagnostics for JSON, XML/XAML, and C# files in the chat workspace.",
        new Dictionary<string, object>
        {
            ["path"] = AgentToolSupport.StringProperty("Relative file or directory path. Defaults to root."),
            ["include_build"] = AgentToolSupport.BooleanProperty("Run dotnet build when a solution or project is present. Defaults to true."),
            ["reason"] = AgentToolSupport.StringProperty("Why diagnostics are needed.")
        });

    public bool RequiresApproval => false;

    public async Task<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, string argumentsJson, CancellationToken ct = default)
    {
        try
        {
            if (!AgentToolSupport.TryParse(argumentsJson, out var root, out var error))
                return new AgentToolResult(false, string.Empty, error);
            var start = WorkspaceCodeToolSupport.Resolve(_sandbox, context.ChatId, WorkspaceCodeToolSupport.ReadString(root, "path", "."));
            var includeBuild = !root.TryGetProperty("include_build", out var includeBuildElement) ||
                               includeBuildElement.ValueKind != JsonValueKind.False;
            var files = File.Exists(start)
                ? [start]
                : Directory.Exists(start)
                    ? Directory.EnumerateFiles(start, "*", SearchOption.AllDirectories)
                        .Where(p => !WorkspaceCodeToolSupport.ShouldSkipPath(p))
                        .ToArray()
                    : [];
            var diagnostics = new StringBuilder();
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is not (".json" or ".xml" or ".xaml" or ".cs"))
                    continue;
                var rel = WorkspaceCodeToolSupport.Relative(_sandbox, context.ChatId, file);
                try
                {
                    var text = await WorkspaceCodeToolSupport.ReadTextAsync(file, ct);
                    if (ext == ".json")
                    {
                        JsonDocument.Parse(text);
                    }
                    else if (ext is ".xml" or ".xaml")
                    {
                        XDocument.Parse(text);
                    }
                    else
                    {
                        var open = text.Count(c => c == '{');
                        var close = text.Count(c => c == '}');
                        if (open != close)
                            diagnostics.AppendLine($"{rel}: brace count differs ({{={open}, }}={close}).");
                    }
                }
                catch (Exception ex)
                {
                    diagnostics.AppendLine($"{rel}: {ex.Message}");
                }
            }

            if (includeBuild)
            {
                var rootPath = _sandbox.GetSandboxRoot(context.ChatId);
                var buildTarget = FindDotnetBuildTarget(rootPath, start);
                if (buildTarget != null)
                {
                    var build = await RunDotnetBuildAsync(rootPath, buildTarget, context.TimeoutSeconds, ct);
                    diagnostics.AppendLine();
                    diagnostics.AppendLine($"dotnet build {Path.GetRelativePath(rootPath, buildTarget)}");
                    diagnostics.AppendLine($"Exit code: {build.ExitCode}");
                    if (!string.IsNullOrWhiteSpace(build.Stdout))
                        diagnostics.AppendLine(AgentToolSupport.Limit(build.Stdout.TrimEnd(), 12_000));
                    if (!string.IsNullOrWhiteSpace(build.Stderr))
                    {
                        diagnostics.AppendLine("stderr:");
                        diagnostics.AppendLine(AgentToolSupport.Limit(build.Stderr.TrimEnd(), 4_000));
                    }
                }
            }

            var output = diagnostics.ToString().Trim();
            return new AgentToolResult(true, string.IsNullOrWhiteSpace(output) ? "No diagnostics found." : AgentToolSupport.Limit(output, context.MaxOutputChars));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AgentToolResult(false, string.Empty, ex.Message);
        }
    }

    private static string? FindDotnetBuildTarget(string rootPath, string start)
    {
        var startDirectory = File.Exists(start)
            ? Path.GetDirectoryName(start) ?? rootPath
            : Directory.Exists(start) ? start : rootPath;

        foreach (var directory in EnumerateParents(startDirectory, rootPath))
        {
            var solution = Directory.EnumerateFiles(directory, "*.sln", SearchOption.TopDirectoryOnly)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (solution != null)
                return solution;

            var project = Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (project != null)
                return project;
        }

        return Directory.EnumerateFiles(rootPath, "*.sln", SearchOption.AllDirectories)
                   .Where(p => !WorkspaceCodeToolSupport.ShouldSkipPath(p))
                   .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                   .FirstOrDefault()
               ?? Directory.EnumerateFiles(rootPath, "*.csproj", SearchOption.AllDirectories)
                   .Where(p => !WorkspaceCodeToolSupport.ShouldSkipPath(p))
                   .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                   .FirstOrDefault();
    }

    private static IEnumerable<string> EnumerateParents(string startDirectory, string rootPath)
    {
        var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var current = Path.GetFullPath(startDirectory);
        while (current.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            yield return current;
            var parent = Directory.GetParent(current)?.FullName;
            if (string.IsNullOrWhiteSpace(parent) ||
                parent.Equals(current, StringComparison.OrdinalIgnoreCase))
            {
                yield break;
            }

            current = parent;
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunDotnetBuildAsync(
        string rootPath,
        string buildTarget,
        int timeoutSeconds,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 15, 120)));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"build \"{buildTarget}\" --no-restore -v:minimal",
                WorkingDirectory = rootPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return (-1, await SafeReadAsync(stdout), "dotnet build timed out.");
        }

        return (process.ExitCode, await SafeReadAsync(stdout), await SafeReadAsync(stderr));
    }

    private static async Task<string> SafeReadAsync(Task<string> task)
    {
        try { return await task; }
        catch { return string.Empty; }
    }
}
