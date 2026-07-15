using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using TLAHStudio.Core.Models;
using TLAHStudio.Core.Services;
using TLAHStudio.Core.Services.Tools;

namespace TLAHStudio.Core.Tests;

public sealed class TypedToolPermissionParityTests
{
    [Theory]
    [InlineData(AgentPermissionModes.BypassPermissions, false)]
    [InlineData(AgentPermissionModes.RequestApproval, true)]
    public async Task HostFileRead_FullOrExactApprovalExecutes(string mode, bool approved)
    {
        await using var db = TestDb.Create();
        using var paths = new TemporaryPaths();
        var target = paths.CreateHostFile("read.txt", "host-content");
        var sandbox = new SandboxCommandService(paths.SandboxBase);
        var tool = new FileReadAgentTool(sandbox, new ToolPlatformService(db));

        var result = await tool.ExecuteAsync(
            Context(mode, approved),
            JsonSerializer.Serialize(new { path = target }));

        Assert.True(result.Success, result.Error);
        Assert.Contains("host-content", result.Output);
    }

    [Theory]
    [InlineData(AgentPermissionModes.RequestApproval)]
    [InlineData(AgentPermissionModes.AutoApprove)]
    public async Task HostFileRead_OrdinaryModeKeepsSandboxBoundary(string mode)
    {
        await using var db = TestDb.Create();
        using var paths = new TemporaryPaths();
        var target = paths.CreateHostFile("read-blocked.txt", "host-content");
        var sandbox = new SandboxCommandService(paths.SandboxBase);
        var tool = new FileReadAgentTool(sandbox, new ToolPlatformService(db));

        var result = await tool.ExecuteAsync(
            Context(mode, approved: false),
            JsonSerializer.Serialize(new { path = target }));

        Assert.False(result.Success);
        Assert.Contains("Absolute paths", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(AgentPermissionModes.BypassPermissions, false)]
    [InlineData(AgentPermissionModes.RequestApproval, true)]
    public async Task HostFileWrite_FullOrExactApprovalBypassesReadAndStaleGuards(
        string mode,
        bool approved)
    {
        await using var db = TestDb.Create();
        using var paths = new TemporaryPaths();
        var target = paths.CreateHostFile($"write-{approved}.txt", "old");
        var tracker = new ReadFileTracker();
        tracker.MarkRead(target, File.GetLastWriteTimeUtc(target));
        File.SetLastWriteTimeUtc(target, DateTime.UtcNow.AddMinutes(1));
        var tool = new FileWriteAgentTool(
            new SandboxCommandService(paths.SandboxBase),
            new ToolPlatformService(db),
            tracker);

        var result = await tool.ExecuteAsync(
            Context(mode, approved),
            JsonSerializer.Serialize(new { path = target, content = "new" }));

        Assert.True(result.Success, result.Error);
        Assert.Equal("new", await File.ReadAllTextAsync(target));
    }

    [Theory]
    [InlineData(AgentPermissionModes.RequestApproval)]
    [InlineData(AgentPermissionModes.AutoApprove)]
    public async Task FileWrite_OrdinaryModePreservesReadBeforeWriteAndStaleChecks(string mode)
    {
        await using var db = TestDb.Create();
        using var paths = new TemporaryPaths();
        var sandbox = new SandboxCommandService(paths.SandboxBase);
        var context = Context(mode, approved: false);
        var sandboxRoot = sandbox.GetSandboxRoot(context.ChatId);
        Directory.CreateDirectory(sandboxRoot);
        var target = Path.Combine(sandboxRoot, "guarded.txt");
        await File.WriteAllTextAsync(target, "old");
        var tracker = new ReadFileTracker();
        var tool = new FileWriteAgentTool(sandbox, new ToolPlatformService(db), tracker);

        var unread = await tool.ExecuteAsync(
            context,
            """{"path":"guarded.txt","content":"new"}""");
        Assert.False(unread.Success);
        Assert.Contains("has not been read", unread.Error, StringComparison.OrdinalIgnoreCase);

        tracker.MarkRead(target, File.GetLastWriteTimeUtc(target));
        File.SetLastWriteTimeUtc(target, DateTime.UtcNow.AddMinutes(1));
        var stale = await tool.ExecuteAsync(
            context,
            """{"path":"guarded.txt","content":"new"}""");
        Assert.False(stale.Success);
        Assert.Contains("modified externally", stale.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(AgentPermissionModes.BypassPermissions, false)]
    [InlineData(AgentPermissionModes.RequestApproval, true)]
    public async Task HostCodeEdit_FullOrExactApprovalBypassesPathReadAndHashGates(
        string mode,
        bool approved)
    {
        using var paths = new TemporaryPaths();
        var target = paths.CreateHostFile($"edit-{approved}.txt", "alpha beta");
        var tool = new CodeEditAgentTool(
            new SandboxCommandService(paths.SandboxBase),
            new ReadFileTracker());

        var result = await tool.ExecuteAsync(
            Context(mode, approved),
            JsonSerializer.Serialize(new
            {
                path = target,
                old_text = "beta",
                new_text = "gamma",
                expected_sha256 = new string('0', 64)
            }));

        Assert.True(result.Success, result.Error);
        Assert.Equal("alpha gamma", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task HostCodeEdit_UnapprovedAskDoesNotCrossSandbox()
    {
        using var paths = new TemporaryPaths();
        var target = paths.CreateHostFile("edit-blocked.txt", "alpha beta");
        var tool = new CodeEditAgentTool(
            new SandboxCommandService(paths.SandboxBase),
            new ReadFileTracker());

        var result = await tool.ExecuteAsync(
            Context(AgentPermissionModes.RequestApproval, approved: false),
            JsonSerializer.Serialize(new { path = target, old_text = "beta", new_text = "gamma" }));

        Assert.False(result.Success);
        Assert.Contains("Absolute paths", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("alpha beta", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task HostCodeRollback_FullAccessRestoresApprovedEditBackup()
    {
        using var paths = new TemporaryPaths();
        var target = paths.CreateHostFile("rollback.txt", "before");
        var sandbox = new SandboxCommandService(paths.SandboxBase);
        var context = Context(AgentPermissionModes.BypassPermissions, approved: false);
        var edit = await new CodeEditAgentTool(sandbox).ExecuteAsync(
            context,
            JsonSerializer.Serialize(new { path = target, old_text = "before", new_text = "after" }));
        Assert.True(edit.Success, edit.Error);
        var backupId = edit.Output.Split('\n')
            .Select(line => line.Trim())
            .First(line => line.StartsWith("Backup:", StringComparison.Ordinal))
            .Replace("Backup:", string.Empty, StringComparison.Ordinal)
            .Trim();

        var rollback = await new CodeRollbackAgentTool(sandbox).ExecuteAsync(
            context,
            JsonSerializer.Serialize(new { path = target, backup_id = backupId }));

        Assert.True(rollback.Success, rollback.Error);
        Assert.Equal("before", await File.ReadAllTextAsync(target));
    }

    [Theory]
    [InlineData(AgentPermissionModes.BypassPermissions, false)]
    [InlineData(AgentPermissionModes.RequestApproval, true)]
    public async Task HostFileDelete_FullOrExactApprovalDeletesOrdinaryTarget(
        string mode,
        bool approved)
    {
        using var paths = new TemporaryPaths();
        var target = paths.CreateHostDirectory($"delete-{approved}");
        await File.WriteAllTextAsync(Path.Combine(target, "artifact.txt"), "delete me");
        var tool = new FileDeleteAgentTool(new SandboxCommandService(paths.SandboxBase));

        var result = await tool.ExecuteAsync(
            Context(mode, approved),
            JsonSerializer.Serialize(new { path = target, recursive = true }));

        Assert.True(result.Success, result.Error);
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public async Task HostFileDelete_UnapprovedAskDoesNotCrossSandbox()
    {
        using var paths = new TemporaryPaths();
        var target = paths.CreateHostDirectory("delete-blocked");
        await File.WriteAllTextAsync(Path.Combine(target, "keep.txt"), "keep");
        var tool = new FileDeleteAgentTool(new SandboxCommandService(paths.SandboxBase));

        var result = await tool.ExecuteAsync(
            Context(AgentPermissionModes.RequestApproval, approved: false),
            JsonSerializer.Serialize(new { path = target, recursive = true }));

        Assert.False(result.Success);
        Assert.True(Directory.Exists(target));
    }

    [Theory]
    [InlineData(AgentPermissionModes.BypassPermissions, false)]
    [InlineData(AgentPermissionModes.RequestApproval, true)]
    public async Task HostFileToolMatrix_FullOrExactApprovalReachesExecution(
        string mode,
        bool approved)
    {
        await using var db = TestDb.Create();
        using var paths = new TemporaryPaths();
        var host = paths.CreateHostDirectory($"matrix-{approved}");
        var source = Path.Combine(host, "source.txt");
        await File.WriteAllTextAsync(source, "matrix needle");
        var sandbox = new SandboxCommandService(paths.SandboxBase);
        var platform = new ToolPlatformService(db);
        var context = Context(mode, approved);

        Assert.True((await new FileListAgentTool(sandbox).ExecuteAsync(
            context,
            JsonSerializer.Serialize(new { path = host }))).Success);
        Assert.True((await new FileInfoAgentTool(sandbox).ExecuteAsync(
            context,
            JsonSerializer.Serialize(new { path = source }))).Success);
        Assert.True((await new FileSearchAgentTool(sandbox, platform).ExecuteAsync(
            context,
            JsonSerializer.Serialize(new { path = host, query = "needle" }))).Success);
        Assert.True((await new FileSendAgentTool(sandbox).ExecuteAsync(
            context,
            JsonSerializer.Serialize(new { path = source }))).Success);

        var created = Path.Combine(host, "created");
        Assert.True((await new FileMkdirAgentTool(sandbox).ExecuteAsync(
            context,
            JsonSerializer.Serialize(new { path = created }))).Success);
        var copied = Path.Combine(created, "copy.txt");
        Assert.True((await new FileMoveAgentTool(sandbox).ExecuteAsync(
            context,
            JsonSerializer.Serialize(new
            {
                from_path = source,
                to_path = copied,
                mode = "copy"
            }))).Success);
        Assert.Equal("matrix needle", await File.ReadAllTextAsync(copied));
    }

    [Fact]
    public void HostPathSafety_AsksThenHonorsApprovalWhileRootDeleteRemainsImmutable()
    {
        using var paths = new TemporaryPaths();
        var sandbox = new SandboxCommandService(paths.SandboxBase);
        var chatId = Guid.NewGuid();
        var hostPath = paths.CreateHostFile("policy.txt", "content");
        var safety = ToolSafetyKernel.Assess(
            sandbox,
            chatId,
            AgentToolNames.FileRead,
            JsonSerializer.Serialize(new { path = hostPath }));

        Assert.True(safety.IsBlocked);
        Assert.True(safety.CanOverrideBlock);
        var before = Authorize(AgentPermissionModes.RequestApproval, safety, explicitlyApproved: false);
        var after = Authorize(AgentPermissionModes.RequestApproval, safety, explicitlyApproved: true);
        var full = Authorize(AgentPermissionModes.BypassPermissions, safety, explicitlyApproved: false);
        Assert.True(before.RequiresApproval);
        Assert.False(after.IsBlocked);
        Assert.False(after.RequiresApproval);
        Assert.False(full.IsBlocked);

        var immutable = ToolSafetyKernel.Assess(
            sandbox,
            chatId,
            AgentToolNames.FileDelete,
            """{"path":".","recursive":true}""");
        var immutableFull = Authorize(
            AgentPermissionModes.BypassPermissions,
            immutable,
            explicitlyApproved: true);
        Assert.True(immutable.IsBlocked);
        Assert.False(immutable.CanOverrideBlock);
        Assert.True(immutableFull.IsBlocked);
        Assert.Equal("immutable_safety_block", immutableFull.ReasonCode);
    }

    [Fact]
    public void CriticalHostRecursiveDeletes_AreImmutableEvenForFullAndExactApproval()
    {
        using var paths = new TemporaryPaths();
        var sandbox = new SandboxCommandService(paths.SandboxBase);
        var criticalTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddIfPresent(criticalTargets, Path.GetPathRoot(Environment.SystemDirectory));
        AddIfPresent(criticalTargets, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        AddIfPresent(criticalTargets, Environment.SystemDirectory);
        if (!string.IsNullOrWhiteSpace(Environment.SystemDirectory))
            AddIfPresent(criticalTargets, Path.Combine(Environment.SystemDirectory, "drivers"));
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        AddIfPresent(criticalTargets, profile);
        if (!string.IsNullOrWhiteSpace(profile))
        {
            var usersRoot = Directory.GetParent(profile)?.FullName;
            AddIfPresent(criticalTargets, usersRoot);
            if (!string.IsNullOrWhiteSpace(usersRoot))
                AddIfPresent(criticalTargets, Path.Combine(usersRoot, "AnotherUser"));
        }

        Assert.NotEmpty(criticalTargets);
        foreach (var target in criticalTargets)
        {
            var safety = ToolSafetyKernel.Assess(
                sandbox,
                Guid.NewGuid(),
                AgentToolNames.FileDelete,
                JsonSerializer.Serialize(new { path = target, recursive = true }));
            Assert.True(safety.IsBlocked);
            Assert.False(safety.CanOverrideBlock);
            Assert.True(Authorize(
                AgentPermissionModes.BypassPermissions,
                safety,
                explicitlyApproved: true).IsBlocked);
        }
    }

    [Theory]
    [InlineData("rg \"C:\\Windows\" docs")]
    [InlineData("rg \".git\" docs")]
    [InlineData("rg -n -g \"*.md\" \"C:\\Windows\" docs")]
    public void SearchPatternThatLooksSensitive_IsStillReadOnly(string command)
    {
        using var paths = new TemporaryPaths();
        var safety = ToolSafetyKernel.Assess(
            new SandboxCommandService(paths.SandboxBase),
            Guid.NewGuid(),
            AgentToolNames.TerminalExec,
            JsonSerializer.Serialize(new { command }));

        Assert.False(safety.IsBlocked);
        Assert.Equal(ToolSafetyLevels.Low, safety.Level);
        Assert.True(safety.IsReadOnly);
    }

    [Theory]
    [InlineData("rg foo C:\\Windows")]
    [InlineData("rg -n \"foo\" C:\\Windows\\System32")]
    public void SearchTargetingHostPath_RemainsContextuallyRestricted(string command)
    {
        using var paths = new TemporaryPaths();
        var safety = ToolSafetyKernel.Assess(
            new SandboxCommandService(paths.SandboxBase),
            Guid.NewGuid(),
            AgentToolNames.TerminalExec,
            JsonSerializer.Serialize(new { command }));

        Assert.True(safety.IsBlocked);
        Assert.True(safety.CanOverrideBlock);
        Assert.True(safety.IsReadOnly);
    }

    [Theory]
    [InlineData("fetch")]
    [InlineData("pull")]
    [InlineData("push")]
    [InlineData("merge")]
    [InlineData("rebase")]
    [InlineData("cherry-pick")]
    [InlineData("revert")]
    [InlineData("remote")]
    [InlineData("tag")]
    public async Task GitIntegratingOperations_UnapprovedAskAndAutoRemainConstrained(string operation)
    {
        using var paths = new TemporaryPaths();
        var sandbox = new SandboxCommandService(paths.SandboxBase);
        var tool = new GitAgentTool(sandbox);

        foreach (var mode in new[] { AgentPermissionModes.RequestApproval, AgentPermissionModes.AutoApprove })
        {
            var result = await tool.ExecuteAsync(
                Context(mode, approved: false),
                JsonSerializer.Serialize(new { operation }));
            Assert.False(result.Success);
            Assert.Contains("requires Full access or approval", result.Error, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("fetch")]
    [InlineData("pull")]
    [InlineData("push")]
    [InlineData("merge")]
    [InlineData("rebase")]
    [InlineData("cherry-pick")]
    [InlineData("revert")]
    [InlineData("remote")]
    [InlineData("tag")]
    public void GitIntegratingOperations_AskAndAutoPromptThenExactApprovalAllows(string operation)
    {
        using var paths = new TemporaryPaths();
        var safety = ToolSafetyKernel.Assess(
            new SandboxCommandService(paths.SandboxBase),
            Guid.NewGuid(),
            AgentToolNames.Git,
            JsonSerializer.Serialize(new { operation }));

        Assert.True(safety.RequiresExplicitApproval);
        Assert.True(safety.BypassImmune);
        Assert.True(Authorize(
            AgentPermissionModes.RequestApproval,
            safety,
            explicitlyApproved: false).RequiresApproval);
        Assert.True(Authorize(
            AgentPermissionModes.AutoApprove,
            safety,
            explicitlyApproved: false).RequiresApproval);
        Assert.False(Authorize(
            AgentPermissionModes.RequestApproval,
            safety,
            explicitlyApproved: true).RequiresApproval);
    }

    [Theory]
    [InlineData("fetch", AgentPermissionModes.BypassPermissions, false)]
    [InlineData("pull", AgentPermissionModes.BypassPermissions, false)]
    [InlineData("push", AgentPermissionModes.BypassPermissions, false)]
    [InlineData("merge", AgentPermissionModes.BypassPermissions, false)]
    [InlineData("rebase", AgentPermissionModes.BypassPermissions, false)]
    [InlineData("cherry-pick", AgentPermissionModes.BypassPermissions, false)]
    [InlineData("revert", AgentPermissionModes.BypassPermissions, false)]
    [InlineData("remote", AgentPermissionModes.BypassPermissions, false)]
    [InlineData("tag", AgentPermissionModes.BypassPermissions, false)]
    [InlineData("fetch", AgentPermissionModes.RequestApproval, true)]
    [InlineData("pull", AgentPermissionModes.RequestApproval, true)]
    [InlineData("push", AgentPermissionModes.RequestApproval, true)]
    [InlineData("merge", AgentPermissionModes.RequestApproval, true)]
    [InlineData("rebase", AgentPermissionModes.RequestApproval, true)]
    [InlineData("cherry-pick", AgentPermissionModes.RequestApproval, true)]
    [InlineData("revert", AgentPermissionModes.RequestApproval, true)]
    [InlineData("remote", AgentPermissionModes.RequestApproval, true)]
    [InlineData("tag", AgentPermissionModes.RequestApproval, true)]
    public async Task GitIntegratingOperations_FullOrExactApprovalReachGit(
        string operation,
        string mode,
        bool approved)
    {
        if (!GitIsAvailable())
            return;

        using var paths = new TemporaryPaths();
        var (repo, arguments) = PrepareGitOperation(paths, operation, approved);
        var tool = new GitAgentTool(new SandboxCommandService(paths.SandboxBase));

        var result = await tool.ExecuteAsync(
            Context(mode, approved),
            JsonSerializer.Serialize(new { operation, path = repo, arguments }));

        Assert.True(result.Success, result.Error);
        Assert.DoesNotContain("not allowed", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("requires Full access", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Absolute paths", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static ToolAuthorizationDecision Authorize(
        string mode,
        ToolSafetyAssessment safety,
        bool explicitlyApproved) =>
        ToolAuthorizationPolicy.Evaluate(
            mode,
            safety,
            policy: null,
            toolRequiresApproval: true,
            requiresUserInteraction: false,
            isDestructive: safety.IsWriteOperation,
            isPlanMode: false,
            autoApproveTools: mode == AgentPermissionModes.AutoApprove,
            explicitlyApproved: explicitlyApproved);

    private static void AddIfPresent(HashSet<string> targets, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
            targets.Add(path);
    }

    private static AgentToolExecutionContext Context(string mode, bool approved) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            TimeoutSeconds: 10,
            MaxOutputChars: 12000,
            PermissionMode: mode,
            HasInvocationAuthorization: approved);

    private static bool GitIsAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "git.exe",
                ArgumentList = { "--version" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            process?.WaitForExit(5000);
            return process?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static (string Repository, string[] Arguments) PrepareGitOperation(
        TemporaryPaths paths,
        string operation,
        bool approved)
    {
        var suffix = $"{operation}-{approved}";
        var repository = paths.CreateHostDirectory($"repo-{suffix}");
        RunGit(repository, "init", "-b", "main");
        RunGit(repository, "config", "user.name", "TLAH Tests");
        RunGit(repository, "config", "user.email", "tests@tlah.local");
        File.WriteAllText(Path.Combine(repository, "README.md"), "initial\n");
        RunGit(repository, "add", "README.md");
        RunGit(repository, "commit", "-m", "initial");

        if (operation is "merge" or "cherry-pick")
        {
            RunGit(repository, "switch", "-c", "feature");
            File.WriteAllText(Path.Combine(repository, "feature.txt"), "feature\n");
            RunGit(repository, "add", "feature.txt");
            RunGit(repository, "commit", "-m", "feature");
            RunGit(repository, "switch", "main");
            return (repository, ["feature"]);
        }

        if (operation == "rebase")
            return (repository, ["main"]);

        if (operation == "revert")
        {
            File.WriteAllText(Path.Combine(repository, "revert.txt"), "revert me\n");
            RunGit(repository, "add", "revert.txt");
            RunGit(repository, "commit", "-m", "revert target");
            return (repository, ["HEAD", "--no-edit"]);
        }

        if (operation == "remote")
            return (repository, ["-v"]);

        if (operation == "tag")
            return (repository, [$"v-test-{(approved ? "approved" : "full")}"]);

        var remote = paths.CreateHostDirectory($"remote-{suffix}.git");
        RunGit(remote, "init", "--bare");
        if (operation is "fetch" or "pull")
        {
            RunGit(repository, "push", remote, "HEAD:refs/heads/main");
            return (repository, [remote, "main"]);
        }

        return operation switch
        {
            "push" => (repository, [remote, "HEAD:refs/heads/main"]),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
        };
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git.exe",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {stdout}\n{stderr}");
    }

    private sealed class TemporaryPaths : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "TLAHStudio.TypedPermission.Tests",
            Guid.NewGuid().ToString("N"));

        public TemporaryPaths()
        {
            SandboxBase = Path.Combine(_root, "sandboxes");
            HostRoot = Path.Combine(_root, "host");
            Directory.CreateDirectory(SandboxBase);
            Directory.CreateDirectory(HostRoot);
        }

        public string SandboxBase { get; }
        private string HostRoot { get; }

        public string CreateHostDirectory(string name)
        {
            var path = Path.Combine(HostRoot, name);
            Directory.CreateDirectory(path);
            return path;
        }

        public string CreateHostFile(string name, string content)
        {
            var path = Path.Combine(HostRoot, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                    Directory.Delete(_root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup; Git may briefly retain a pack file on Windows.
            }
        }
    }
}
