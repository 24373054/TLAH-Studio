using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using TLAHStudio.Core.Models;
using TLAHStudio.Core.Services;

namespace TLAHStudio.App.ViewModels;

public partial class ToolPlatformViewModel : ObservableObject
{
    private readonly IToolPlatformService _platform;
    private readonly IExecutionBackendRouter _backends;
    private readonly IMcpClientService _mcp;
    private readonly IAppStateService _appState;
    private readonly IChatService _chatService;

    public ToolPlatformViewModel(
        IToolPlatformService platform,
        IExecutionBackendRouter backends,
        IMcpClientService mcp,
        IAppStateService appState,
        IChatService chatService)
    {
        _platform = platform;
        _backends = backends;
        _mcp = mcp;
        _appState = appState;
        _chatService = chatService;
    }

    public IReadOnlyList<string> BackendOptions { get; } =
    [
        ToolExecutionBackends.RestrictedLocal,
        ToolExecutionBackends.Wsl,
        ToolExecutionBackends.Docker,
        ToolExecutionBackends.Remote
    ];

    public IReadOnlyList<string> TransportOptions { get; } =
    [
        McpTransportTypes.Stdio,
        McpTransportTypes.StreamableHttp
    ];

    public IReadOnlyList<string> PolicySubjectOptions { get; } =
    [
        ToolPolicySubjects.Tool,
        ToolPolicySubjects.Path,
        ToolPolicySubjects.Domain
    ];

    public IReadOnlyList<string> PolicyScopeOptions { get; } =
    [
        ToolPolicyScopes.Global,
        ToolPolicyScopes.Project,
        ToolPolicyScopes.Chat
    ];

    public IReadOnlyList<string> PolicyDecisionOptions { get; } =
    [
        ToolPolicyDecisions.Allow,
        ToolPolicyDecisions.Deny
    ];

    public IReadOnlyList<McpExampleOption> McpExamples { get; } =
    [
        new("Local Python script", "python"),
        new("Filesystem via npx", "filesystem"),
        new("Streamable HTTP", "http")
    ];

    public ObservableCollection<McpServerEditor> McpServers { get; } = [];
    public ObservableCollection<CredentialEntryDto> Credentials { get; } = [];
    public ObservableCollection<ToolPolicyRule> Policies { get; } = [];

    [ObservableProperty] private string selectedBackend = ToolExecutionBackends.RestrictedLocal;
    [ObservableProperty] private string networkAllowlist = string.Empty;
    [ObservableProperty] private double maxRuntimeSeconds = 30;
    [ObservableProperty] private double maxOutputChars = 20000;
    [ObservableProperty] private double maxFileMegabytes = 10;
    [ObservableProperty] private double maxMemoryMb = 512;
    [ObservableProperty] private double maxProcesses = 8;
    [ObservableProperty] private string wslDistribution = string.Empty;
    [ObservableProperty] private string dockerImage = string.Empty;
    [ObservableProperty] private string remoteEndpoint = string.Empty;
    [ObservableProperty] private string remoteCredentialName = string.Empty;
    [ObservableProperty] private string backendAvailability = string.Empty;
    [ObservableProperty] private McpServerEditor? selectedMcpServer;
    [ObservableProperty] private McpExampleOption? selectedMcpExample;
    [ObservableProperty] private CredentialEntryDto? selectedCredential;
    [ObservableProperty] private string credentialName = string.Empty;
    [ObservableProperty] private string credentialSecret = string.Empty;
    [ObservableProperty] private string credentialDomains = string.Empty;
    [ObservableProperty] private string credentialTools = string.Empty;
    [ObservableProperty] private string newPolicySubjectKind = ToolPolicySubjects.Tool;
    [ObservableProperty] private string newPolicyPattern = "tool(*)";
    [ObservableProperty] private string newPolicyScope = ToolPolicyScopes.Global;
    [ObservableProperty] private string newPolicyDecision = ToolPolicyDecisions.Allow;
    [ObservableProperty] private string newPolicyDescription = string.Empty;
    [ObservableProperty] private string statusMessage = string.Empty;

    partial void OnSelectedCredentialChanged(CredentialEntryDto? value)
    {
        CredentialName = value?.Name ?? string.Empty;
        CredentialSecret = string.Empty;
        CredentialDomains = value?.AllowedDomains ?? string.Empty;
        CredentialTools = value?.AllowedTools ?? string.Empty;
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var settings = await _platform.GetSettingsAsync(ct);
        SelectedBackend = settings.DefaultBackend;
        NetworkAllowlist = settings.NetworkAllowlist;
        MaxRuntimeSeconds = settings.MaxRuntimeSeconds;
        MaxOutputChars = settings.MaxOutputChars;
        MaxFileMegabytes = settings.MaxFileBytes / 1024d / 1024d;
        MaxMemoryMb = settings.MaxMemoryMb;
        MaxProcesses = settings.MaxProcesses;
        WslDistribution = settings.WslDistribution;
        DockerImage = settings.DockerImage;
        RemoteEndpoint = settings.RemoteEndpoint;
        RemoteCredentialName = settings.RemoteCredentialName;

        var availability = await _backends.GetAvailabilityAsync(ct);
        BackendAvailability = string.Join("  |  ", availability.Select(p =>
            $"{p.Key}: {(p.Value ? "ready" : "unavailable")}"));
        await ReloadCollectionsAsync(ct);
        StatusMessage = string.Empty;
    }

    public async Task SaveSettingsAsync(CancellationToken ct = default)
    {
        await _platform.UpdateSettingsAsync(new ToolPlatformSettingsUpdate(
            SelectedBackend,
            NetworkAllowlist,
            (int)MaxRuntimeSeconds,
            (int)MaxOutputChars,
            (int)(MaxFileMegabytes * 1024 * 1024),
            (int)MaxMemoryMb,
            (int)MaxProcesses,
            WslDistribution,
            DockerImage,
            RemoteEndpoint,
            RemoteCredentialName), ct);
        StatusMessage = "Tool security settings saved.";
    }

    public async Task SaveAllAsync(CancellationToken ct = default)
    {
        await SaveSettingsAsync(ct);
        if (SelectedMcpServer != null)
            await SaveMcpServerAsync(ct);
        if (SelectedCredential != null || !string.IsNullOrWhiteSpace(CredentialName))
            await SaveCredentialAsync(ct);
        StatusMessage = "All current tool platform changes were saved.";
    }

    public void NewMcpServer()
    {
        var server = new McpServerEditor
        {
            Name = "New MCP Server",
            Transport = McpTransportTypes.Stdio,
            ArgumentsJson = "[]",
            HeadersJson = "{}",
            EnvironmentJson = "{}",
            Enabled = true
        };
        McpServers.Add(server);
        SelectedMcpServer = server;
    }

    public async Task SaveMcpServerAsync(CancellationToken ct = default)
    {
        var editor = SelectedMcpServer
            ?? throw new InvalidOperationException("Select or create an MCP server first.");
        var saved = await _platform.SaveMcpServerAsync(new McpServerConfigDto(
            editor.Id,
            editor.ProjectSpaceId,
            editor.Name,
            editor.Transport,
            editor.Command,
            editor.ArgumentsJson,
            editor.Endpoint,
            editor.HeadersJson,
            editor.EnvironmentJson,
            editor.Enabled), ct);
        editor.CopyFrom(saved);
        var persisted = (await _platform.ListMcpServersAsync(ct: ct))
            .FirstOrDefault(server => server.Id == saved.Id)
            ?? throw new InvalidOperationException("The MCP server could not be reloaded after saving.");
        editor.CopyFrom(persisted);
        StatusMessage = $"MCP server \"{editor.Name}\" saved and verified in local storage.";
    }

    public async Task TestMcpServerAsync(CancellationToken ct = default)
    {
        await SaveMcpServerAsync(ct);
        var editor = SelectedMcpServer!;
        StatusMessage = $"Connecting to MCP server \"{editor.Name}\"...";
        var tools = await _mcp.TestServerAsync(ToDto(editor), ct);
        StatusMessage = tools.Count == 0
            ? $"Connected to \"{editor.Name}\", but it exposed no tools."
            : $"Connected to \"{editor.Name}\". {tools.Count} tool(s): {string.Join(", ", tools.Select(tool => tool.Name))}";
    }

    public void ApplySelectedMcpExample()
    {
        if (SelectedMcpExample == null)
            return;
        if (SelectedMcpServer == null)
            NewMcpServer();

        var editor = SelectedMcpServer!;
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        switch (SelectedMcpExample.Key)
        {
            case "filesystem":
                editor.Name = "Local Filesystem";
                editor.Transport = McpTransportTypes.Stdio;
                editor.Command = "cmd.exe";
                editor.ArgumentsJson = JsonSerializer.Serialize(new[]
                {
                    "/d",
                    "/s",
                    "/c",
                    "npx.cmd",
                    "-y",
                    "@modelcontextprotocol/server-filesystem",
                    documents
                });
                editor.Endpoint = string.Empty;
                editor.HeadersJson = "{}";
                editor.EnvironmentJson = "{}";
                break;
            case "http":
                editor.Name = "Remote MCP";
                editor.Transport = McpTransportTypes.StreamableHttp;
                editor.Command = string.Empty;
                editor.ArgumentsJson = "[]";
                editor.Endpoint = "https://mcp.example.com/mcp";
                editor.HeadersJson =
                    """{"Authorization":"Bearer ${credential:mcp-token}"}""";
                editor.EnvironmentJson = "{}";
                break;
            default:
                editor.Name = "Local Python MCP";
                editor.Transport = McpTransportTypes.Stdio;
                editor.Command = "python";
                editor.ArgumentsJson =
                    """["C:\\path\\to\\server.py"]""";
                editor.Endpoint = string.Empty;
                editor.HeadersJson = "{}";
                editor.EnvironmentJson =
                    """{"PYTHONUTF8":"1","PYTHONIOENCODING":"utf-8"}""";
                break;
        }

        editor.Enabled = true;
        StatusMessage = $"Loaded the {SelectedMcpExample.Name} example. Replace placeholder paths or endpoints, then test the connection.";
    }

    public async Task DeleteMcpServerAsync(CancellationToken ct = default)
    {
        if (SelectedMcpServer == null)
            return;
        if (SelectedMcpServer.Id != Guid.Empty)
            await _platform.DeleteMcpServerAsync(SelectedMcpServer.Id, ct);
        McpServers.Remove(SelectedMcpServer);
        SelectedMcpServer = McpServers.FirstOrDefault();
        StatusMessage = "MCP server removed.";
    }

    public void NewCredential()
    {
        SelectedCredential = null;
        CredentialName = string.Empty;
        CredentialSecret = string.Empty;
        CredentialDomains = string.Empty;
        CredentialTools = string.Empty;
    }

    public async Task SaveCredentialAsync(CancellationToken ct = default)
    {
        var saved = await _platform.SaveCredentialAsync(
            SelectedCredential?.Id,
            CredentialName,
            CredentialSecret,
            CredentialDomains,
            CredentialTools,
            ct);
        await ReloadCredentialsAsync(ct);
        SelectedCredential = Credentials.FirstOrDefault(c => c.Id == saved.Id);
        StatusMessage = $"Credential \"{saved.Name}\" saved securely.";
    }

    public async Task DeleteCredentialAsync(CancellationToken ct = default)
    {
        if (SelectedCredential == null)
            return;
        await _platform.DeleteCredentialAsync(SelectedCredential.Id, ct);
        await ReloadCredentialsAsync(ct);
        NewCredential();
        StatusMessage = "Credential removed.";
    }

    public async Task DeletePolicyAsync(Guid id, CancellationToken ct = default)
    {
        await _platform.DeletePolicyAsync(id, ct);
        await ReloadPoliciesAsync(ct);
        StatusMessage = "Permission rule removed.";
    }

    public async Task SavePolicyRuleAsync(CancellationToken ct = default)
    {
        Guid? chatId = null;
        Guid? projectId = null;
        if (NewPolicyScope == ToolPolicyScopes.Chat)
        {
            chatId = _appState.CurrentChatId
                ?? throw new InvalidOperationException("Select a chat before creating a chat-scoped permission rule.");
        }
        if (NewPolicyScope == ToolPolicyScopes.Project)
        {
            var currentChatId = _appState.CurrentChatId
                ?? throw new InvalidOperationException("Select a project chat before creating a project-scoped permission rule.");
            var chat = await _chatService.GetChatAsync(currentChatId, ct);
            projectId = chat?.ProjectSpaceId;
            if (projectId == null)
                throw new InvalidOperationException("The selected chat is not assigned to a project workspace.");
        }

        var saved = await _platform.SavePolicyRuleAsync(new ToolPolicyRuleUpdate(
            null,
            NewPolicySubjectKind,
            NewPolicyPattern,
            NewPolicyScope,
            NewPolicyDecision,
            NewPolicyDescription,
            chatId,
            projectId), ct);
        await ReloadPoliciesAsync(ct);
        StatusMessage = $"Permission rule saved: {saved.SubjectKind} {saved.Pattern} {saved.Decision}.";
    }

    private async Task ReloadCollectionsAsync(CancellationToken ct)
    {
        McpServers.Clear();
        foreach (var server in await _platform.ListMcpServersAsync(ct: ct))
            McpServers.Add(McpServerEditor.From(server));
        SelectedMcpServer = McpServers.FirstOrDefault();
        await ReloadCredentialsAsync(ct);
        await ReloadPoliciesAsync(ct);
    }

    private async Task ReloadCredentialsAsync(CancellationToken ct)
    {
        Credentials.Clear();
        foreach (var credential in await _platform.ListCredentialsAsync(ct))
            Credentials.Add(credential);
    }

    private async Task ReloadPoliciesAsync(CancellationToken ct)
    {
        Policies.Clear();
        foreach (var policy in await _platform.ListPoliciesAsync(ct))
            Policies.Add(policy);
    }

    private static McpServerConfigDto ToDto(McpServerEditor editor) =>
        new(
            editor.Id,
            editor.ProjectSpaceId,
            editor.Name,
            editor.Transport,
            editor.Command,
            editor.ArgumentsJson,
            editor.Endpoint,
            editor.HeadersJson,
            editor.EnvironmentJson,
            editor.Enabled);
}

public sealed record McpExampleOption(string Name, string Key);

public partial class McpServerEditor : ObservableObject
{
    [ObservableProperty] private Guid id;
    [ObservableProperty] private Guid? projectSpaceId;
    [ObservableProperty] private string name = string.Empty;
    [ObservableProperty] private string transport = McpTransportTypes.Stdio;
    [ObservableProperty] private string command = string.Empty;
    [ObservableProperty] private string argumentsJson = "[]";
    [ObservableProperty] private string endpoint = string.Empty;
    [ObservableProperty] private string headersJson = "{}";
    [ObservableProperty] private string environmentJson = "{}";
    [ObservableProperty] private bool enabled = true;

    public static McpServerEditor From(McpServerConfigDto value)
    {
        var editor = new McpServerEditor();
        editor.CopyFrom(value);
        return editor;
    }

    public void CopyFrom(McpServerConfigDto value)
    {
        Id = value.Id;
        ProjectSpaceId = value.ProjectSpaceId;
        Name = value.Name;
        Transport = value.Transport;
        Command = value.Command;
        ArgumentsJson = value.ArgumentsJson;
        Endpoint = value.Endpoint;
        HeadersJson = value.HeadersJson;
        EnvironmentJson = value.EnvironmentJson;
        Enabled = value.Enabled;
    }
}
