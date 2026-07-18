using System.Security.Cryptography;
using System.Text.Json;

namespace TLAHStudio.Core.Services.Artifacts;

public sealed partial class ArtifactWorkbenchService : IArtifactWorkbenchService
{
    private readonly ISandboxCommandService _sandbox;

    public ArtifactWorkbenchService(ISandboxCommandService sandbox)
    {
        _sandbox = sandbox;
    }

    private string ResolveExistingPath(ArtifactExecutionScope scope, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("A workspace-relative path is required.");

        var fullPath = AgentToolSupport.ResolveSandboxPath(
            _sandbox,
            scope.ChatId,
            path,
            scope.PermissionMode);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Artifact not found: {path}", fullPath);
        return fullPath;
    }

    private string ResolveOutputPath(
        ArtifactExecutionScope scope,
        string requestedPath,
        string defaultRelativePath,
        IReadOnlySet<string> supportedExtensions,
        bool overwrite)
    {
        var path = string.IsNullOrWhiteSpace(requestedPath)
            ? defaultRelativePath
            : requestedPath.Trim();
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (!supportedExtensions.Contains(extension))
        {
            throw new InvalidOperationException(
                $"Unsupported artifact format '{extension}'. Expected: {string.Join(", ", supportedExtensions.Order())}.");
        }

        var resolved = AgentToolSupport.ResolveSandboxPath(
            _sandbox,
            scope.ChatId,
            path,
            scope.PermissionMode);
        Directory.CreateDirectory(Path.GetDirectoryName(resolved)!);
        return overwrite ? resolved : ChooseConflictSafePath(resolved);
    }

    private static string ChooseConflictSafePath(string requestedPath)
    {
        if (!File.Exists(requestedPath))
            return requestedPath;

        var directory = Path.GetDirectoryName(requestedPath)!;
        var name = Path.GetFileNameWithoutExtension(requestedPath);
        var extension = Path.GetExtension(requestedPath);
        for (var suffix = 2; suffix < 10_000; suffix++)
        {
            var candidate = Path.Combine(directory, $"{name}-{suffix}{extension}");
            if (!File.Exists(candidate))
                return candidate;
        }

        throw new IOException($"Could not choose a conflict-safe output name for '{requestedPath}'.");
    }

    private static string CreateTemporaryPath(string finalPath)
    {
        var extension = Path.GetExtension(finalPath);
        return Path.Combine(
            Path.GetDirectoryName(finalPath)!,
            $".{Path.GetFileNameWithoutExtension(finalPath)}.{Guid.NewGuid():N}.tmp{extension}");
    }

    private static void CommitTemporaryFile(string temporaryPath, string finalPath)
    {
        if (!File.Exists(temporaryPath))
            throw new IOException("Artifact generator did not produce its expected temporary file.");

        if (File.Exists(finalPath))
            File.Move(temporaryPath, finalPath, overwrite: true);
        else
            File.Move(temporaryPath, finalPath);
    }

    private async Task<AgentToolArtifact> BuildArtifactAsync(
        ArtifactExecutionScope scope,
        string path,
        CancellationToken ct)
    {
        var root = _sandbox.GetSandboxRoot(scope.ChatId);
        var info = new FileInfo(path);
        await using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
        return new AgentToolArtifact(
            Path.GetRelativePath(root, path),
            ArtifactContentType(path),
            info.Length,
            hash);
    }

    private static string ArtifactContentType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".csv" => "text/csv",
            ".md" => "text/markdown",
            ".pdf" => "application/pdf",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            _ => AgentToolSupport.ContentType(path)
        };

    private static ArtifactWorkbenchResult Success(
        string summary,
        object structuredData,
        IReadOnlyList<AgentToolArtifact> artifacts) =>
        new(
            true,
            summary,
            JsonSerializer.SerializeToElement(structuredData, ArtifactJson.Options),
            artifacts);

    private static ArtifactWorkbenchResult Failure(Exception exception) =>
        ArtifactWorkbenchResult.Failed(exception.Message);

    private static async Task WriteAllTextAtomicAsync(
        string finalPath,
        string content,
        Func<string, Task> validator,
        CancellationToken ct)
    {
        var temporaryPath = CreateTemporaryPath(finalPath);
        try
        {
            await File.WriteAllTextAsync(temporaryPath, content, ct);
            await validator(temporaryPath);
            ct.ThrowIfCancellationRequested();
            CommitTemporaryFile(temporaryPath, finalPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static async Task GenerateAtomicAsync(
        string finalPath,
        Func<string, Task> generator,
        Func<string, Task> validator,
        CancellationToken ct)
    {
        var temporaryPath = CreateTemporaryPath(finalPath);
        try
        {
            await generator(temporaryPath);
            await validator(temporaryPath);
            ct.ThrowIfCancellationRequested();
            CommitTemporaryFile(temporaryPath, finalPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}

internal static class ArtifactJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };
}
