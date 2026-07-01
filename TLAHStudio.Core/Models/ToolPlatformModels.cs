using System.ComponentModel.DataAnnotations;

namespace TLAHStudio.Core.Models;

public static class ToolExecutionBackends
{
    public const string RestrictedLocal = "restricted_local";
    public const string UnrestrictedLocal = "unrestricted_local";
    public const string Wsl = "wsl";
    public const string Docker = "docker";
    public const string Remote = "remote";
}

public static class ToolPolicyScopes
{
    public const string Once = "once";
    public const string Chat = "chat";
    public const string Project = "project";
    public const string Global = "global";
}

public static class ToolPolicyDecisions
{
    public const string Allow = "allow";
    public const string Deny = "deny";
}

public static class ToolPolicySubjects
{
    public const string Tool = "tool";
    public const string Path = "path";
    public const string Domain = "domain";
}

public static class McpTransportTypes
{
    public const string Stdio = "stdio";
    public const string StreamableHttp = "streamable_http";
}

public class ToolPlatformSettings
{
    [Key]
    public int Id { get; set; } = 1;

    [MaxLength(40)]
    public string DefaultBackend { get; set; } = ToolExecutionBackends.RestrictedLocal;

    public string NetworkAllowlist { get; set; } =
        "api.github.com\ngithub.com\nraw.githubusercontent.com\nhtml.duckduckgo.com\nlite.duckduckgo.com";

    public int MaxRuntimeSeconds { get; set; } = 30;
    public int MaxOutputChars { get; set; } = 20000;
    public int MaxFileBytes { get; set; } = 10 * 1024 * 1024;
    public int MaxMemoryMb { get; set; } = 512;
    public int MaxProcesses { get; set; } = 8;

    [MaxLength(120)]
    public string WslDistribution { get; set; } = string.Empty;

    [MaxLength(300)]
    public string DockerImage { get; set; } = "mcr.microsoft.com/powershell:latest";

    [MaxLength(2048)]
    public string RemoteEndpoint { get; set; } = string.Empty;

    [MaxLength(160)]
    public string RemoteCredentialName { get; set; } = string.Empty;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class ToolPolicyRule
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? ChatId { get; set; }
    public Guid? ProjectSpaceId { get; set; }

    [MaxLength(100)]
    public string ToolName { get; set; } = string.Empty;

    [MaxLength(40)]
    public string SubjectKind { get; set; } = ToolPolicySubjects.Tool;

    [MaxLength(500)]
    public string Pattern { get; set; } = string.Empty;

    [MaxLength(40)]
    public string Scope { get; set; } = ToolPolicyScopes.Chat;

    [MaxLength(40)]
    public string Decision { get; set; } = ToolPolicyDecisions.Allow;

    [MaxLength(500)]
    public string Description { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class McpServerConfig
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? ProjectSpaceId { get; set; }

    [MaxLength(160)]
    public string Name { get; set; } = "MCP Server";

    [MaxLength(40)]
    public string Transport { get; set; } = McpTransportTypes.Stdio;

    [MaxLength(2048)]
    public string Command { get; set; } = string.Empty;

    public string ArgumentsJson { get; set; } = "[]";

    [MaxLength(2048)]
    public string Endpoint { get; set; } = string.Empty;

    public string HeadersJson { get; set; } = "{}";
    public string EnvironmentJson { get; set; } = "{}";
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class CredentialEntry
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(160)]
    public string Name { get; set; } = string.Empty;

    public string ProtectedValue { get; set; } = string.Empty;
    public string AllowedDomains { get; set; } = string.Empty;
    public string AllowedTools { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
