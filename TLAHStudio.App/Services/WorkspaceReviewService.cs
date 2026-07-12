using System.Diagnostics;
using System.Text;
using TLAHStudio.App.Models;

namespace TLAHStudio.App.Services;

public interface IWorkspaceReviewService
{
    Task<WorkspaceReviewSnapshot> LoadAsync(string? workspacePath, CancellationToken cancellationToken = default);
}

/// <summary>Read-only, argument-safe Git integration for the Changes workbench.</summary>
public sealed class WorkspaceReviewService : IWorkspaceReviewService
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(12);

    public async Task<WorkspaceReviewSnapshot> LoadAsync(string? workspacePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
            return WorkspaceReviewSnapshot.Unavailable("Choose a workspace to review local changes.");

        var root = await RunGitAsync(workspacePath, ["rev-parse", "--show-toplevel"], cancellationToken);
        if (root.ExitCode != 0)
            return WorkspaceReviewSnapshot.Unavailable("This workspace is not a Git repository.", root.Error);

        var repositoryRoot = root.Output.Trim();
        var status = await RunGitAsync(repositoryRoot, ["status", "--porcelain=v1", "-z"], cancellationToken);
        if (status.ExitCode != 0)
            return WorkspaceReviewSnapshot.Unavailable("Git status could not be loaded.", status.Error);

        var changes = ParseStatus(status.Output);
        var unstaged = await RunGitAsync(repositoryRoot, ["diff", "--no-ext-diff", "--no-color", "--unified=3"], cancellationToken);
        var staged = await RunGitAsync(repositoryRoot, ["diff", "--cached", "--no-ext-diff", "--no-color", "--unified=3"], cancellationToken);
        var diffByPath = BuildDiffMap($"{unstaged.Output}\n{staged.Output}", changes);
        var name = Path.GetFileName(repositoryRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var summary = changes.Count == 0
            ? "Working tree is clean"
            : $"{changes.Count} changed file{(changes.Count == 1 ? string.Empty : "s")}";

        return new WorkspaceReviewSnapshot(name, summary, changes, diffByPath);
    }

    private static List<WorkspaceChange> ParseStatus(string status)
    {
        var result = new List<WorkspaceChange>();
        var entries = status.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            if (entry.Length < 4)
                continue;

            var code = entry[..2];
            var path = entry[3..];
            var isRenameOrCopy = code[0] is 'R' or 'C' || code[1] is 'R' or 'C';
            if (isRenameOrCopy && index + 1 < entries.Length)
                path = entries[++index];

            result.Add(new WorkspaceChange(code.Trim(), path, code[0] != ' ' && code[0] != '?'));
        }

        return result;
    }

    private static IReadOnlyDictionary<string, string> BuildDiffMap(string diff, IReadOnlyList<WorkspaceChange> changes)
    {
        var map = new Dictionary<string, StringBuilder>(StringComparer.OrdinalIgnoreCase);
        string? currentPath = null;
        foreach (var line in diff.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.StartsWith("diff --git a/", StringComparison.Ordinal))
            {
                var separator = line.IndexOf(" b/", StringComparison.Ordinal);
                currentPath = separator > 13 ? line[13..separator] : null;
                if (currentPath != null && !map.ContainsKey(currentPath))
                    map[currentPath] = new StringBuilder();
            }

            if (currentPath != null)
                map[currentPath].AppendLine(line);
        }

        var result = map.ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes.Where(change => !result.ContainsKey(change.Path)))
        {
            if (change.Status.Contains('?', StringComparison.Ordinal))
                result[change.Path] = $"Untracked file: {change.Path}\n\nGit does not create a textual diff until this file is added.";
        }
        return result;
    }

    private static async Task<GitResult> RunGitAsync(string workingDirectory, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(GitTimeout);
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    WorkingDirectory = workingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                }
            };
            foreach (var argument in arguments)
                process.StartInfo.ArgumentList.Add(argument);

            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return new GitResult(process.ExitCode, await outputTask, await errorTask);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new GitResult(-1, string.Empty, ex.Message);
        }
    }

    private sealed record GitResult(int ExitCode, string Output, string Error);
}
