using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.UI.Xaml;
using TLAHStudio.Core.Llm;
using TLAHStudio.Core.Services;
using TLAHStudio.Core.Services.AgentRuntime;
using TLAHStudio.Core.Services.Background;
using TLAHStudio.Core.Services.Context;
using TLAHStudio.Core.Services.Lsp;
using TLAHStudio.Core.Services.Mcp;
using TLAHStudio.Core.Services.Memory;
using TLAHStudio.Core.Services.Observability;
using TLAHStudio.Core.Services.Plugins;
using TLAHStudio.Core.Services.Sandbox;
using TLAHStudio.Core.Services.Sdk;
using TLAHStudio.Core.Services.Tools;
using TLAHStudio.Core.Services.Tools.PerTool;
using TLAHStudio.Core.Services.SessionMemory;
using TLAHStudio.Core.Services.Workspace;
using TLAHStudio.Data;
using TLAHStudio.App.ViewModels;
using TLAHStudio.App.Views;

namespace TLAHStudio.App;

public partial class App : Application
{
    private static readonly IntPtr DpiAwarenessContextPerMonitorAwareV2 = new(-4);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

    public static Window? MainWindow { get; private set; }
    public static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TLAH Studio", "logs");
    private readonly IHost _host;

    public App()
    {
        TryEnableHighDpi();
        Directory.CreateDirectory(LogDir);
        UnhandledException += (_, e) => Log($"UNHANDLED XAML: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log($"UNHANDLED CLR: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) => Log($"UNOBSERVED TASK: {e.Exception}");

        Log("App starting...");
        InitializeComponent();
        Log("XAML resources initialized.");

        _host = Host.CreateDefaultBuilder().ConfigureServices((ctx, services) =>
        {
            // Database
            services.AddDbContext<TlahDbContext>(o =>
            {
                var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TLAH Studio", "data");
                Directory.CreateDirectory(d);
                o.UseSqlite($"Data Source={Path.Combine(d, "tlah.db")}");
            });
            services.AddScoped<DbContext>(sp => sp.GetRequiredService<TlahDbContext>());

            // HTTP
            services.AddHttpClient("LLM", c => c.Timeout = TimeSpan.FromSeconds(120));
            services.AddHttpClient("Update", c => { c.Timeout = TimeSpan.FromMinutes(10); c.DefaultRequestHeaders.Add("User-Agent", "TLAHStudio-Updater/1.0"); });
            services.AddHttpClient("Tools", c =>
            {
                c.Timeout = TimeSpan.FromMinutes(10);
                c.DefaultRequestHeaders.Add("User-Agent", "TLAHStudio-Tools/1.4");
            }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false
            });

            // Services
            services.AddSingleton<IUpdateService, UpdateService>();
            services.AddSingleton<IAppStateService, AppStateService>();
            services.AddSingleton<IThemeService, ThemeService>();
            services.AddSingleton<IBackgroundService, BackgroundImageService>();
            services.AddSingleton<IUiDensityService, UiDensityService>();
            services.AddSingleton<IInteractionSoundService, InteractionSoundService>();
            services.AddSingleton<IAppReleaseService, AppReleaseService>();
            services.AddScoped<IChatService, ChatService>();
            services.AddScoped<ISettingsService, SettingsService>();
            services.AddSingleton<ISandboxCommandService, SandboxCommandService>();
            services.AddScoped<IToolPlatformService, ToolPlatformService>();
            services.AddSingleton<INetworkSecurityService, NetworkSecurityService>();
            services.AddScoped<IExecutionBackendRouter, ExecutionBackendRouter>();
            services.AddScoped<IMcpClientService, McpClientService>();
            services.AddScoped<IAgentContextManager, AgentContextManager>();
            services.AddScoped<IProjectMemoryService, ProjectMemoryService>();
            services.AddScoped<IToolResultPersistenceService, ToolResultPersistenceService>();
            services.AddScoped<IAgentTaskService, AgentTaskService>();
            services.AddScoped<IAgentTool, ToolSearchAgentTool>();
            services.AddScoped<IAgentTool, TodoWriteAgentTool>();
            services.AddScoped<IAgentTool, TaskCreateAgentTool>();
            services.AddScoped<IAgentTool, TaskUpdateAgentTool>();
            services.AddScoped<IAgentTool, TaskListAgentTool>();
            services.AddScoped<IAgentTool, TaskOutputAgentTool>();
            services.AddScoped<IAgentTool, TaskStopAgentTool>();
            services.AddScoped<IAgentTool, TaskSendMessageAgentTool>();
            services.AddScoped<IAgentTool, ReadPersistedOutputAgentTool>();
            services.AddScoped<IAgentTool, SandboxExecAgentTool>();
            services.AddScoped<IAgentTool, TerminalExecAgentTool>();
            services.AddScoped<IAgentTool, FileListAgentTool>();
            services.AddScoped<IAgentTool, FileReadAgentTool>();
            services.AddScoped<IAgentTool, FileWriteAgentTool>();
            services.AddScoped<IAgentTool, FileSendAgentTool>();
            services.AddScoped<IAgentTool, FileSearchAgentTool>();
            services.AddScoped<IAgentTool, FileInfoAgentTool>();
            services.AddScoped<IAgentTool, FileMkdirAgentTool>();
            services.AddScoped<IAgentTool, FileMoveAgentTool>();
            services.AddScoped<IAgentTool, FileDeleteAgentTool>();
            services.AddScoped<IAgentTool, GitAgentTool>();
            services.AddScoped<IAgentTool, HttpRequestAgentTool>();
            services.AddScoped<IAgentTool, WebSearchAgentTool>();
            services.AddScoped<IAgentTool, BrowserReadAgentTool>();
            services.AddScoped<IAgentTool, McpListToolsAgentTool>();
            services.AddScoped<IAgentTool, McpListResourcesAgentTool>();
            services.AddScoped<IAgentTool, McpReadResourceAgentTool>();
            services.AddScoped<IAgentTool, McpCallAgentTool>();
            services.AddScoped<IAgentTool, MemoryReadAgentTool>();
            services.AddScoped<IAgentTool, MemoryWriteAgentTool>();
            services.AddScoped<IAgentTool, CodeReadAgentTool>();
            services.AddScoped<IAgentTool, CodeGrepAgentTool>();
            services.AddScoped<IAgentTool, CodeGlobAgentTool>();
            services.AddScoped<IAgentTool, CodeEditAgentTool>();
            services.AddScoped<IAgentTool, CodeMultiEditAgentTool>();
            services.AddScoped<IAgentTool, CodeDiffAgentTool>();
            services.AddScoped<IAgentTool, CodeApplyPatchAgentTool>();
            services.AddScoped<IAgentTool, CodeRollbackAgentTool>();
            services.AddScoped<IAgentTool, CodeDiagnosticsAgentTool>();
            services.AddScoped<IAgentTool, CodeSymbolsAgentTool>();
            services.AddScoped<IAgentToolRegistry, AgentToolRegistry>();
            services.AddScoped<IToolHookRegistry>(_ =>
            {
                var registry = new ToolHookRegistry();
                registry.Register(new SecretRedactionHook());
                return registry;
            });
            services.AddScoped<IToolLifecycleRunner, DefaultToolLifecycleRunner>();
            services.AddScoped<IAgentEventStream, AgentEventStream>();
            services.AddScoped<ICheckpointStore, CheckpointStore>();
            services.AddScoped<IProviderStreamAdapter, ProviderStreamAdapter>();
            services.AddScoped<IToolExecutionScheduler, ToolExecutionScheduler>();
            services.AddScoped<IAgentRunEngine, AgentRunEngine>();
            services.AddScoped<IAgentRunEngineV2, AgentRunEngineV2>();
            services.AddScoped<IAgentEventSubscriptionService, AgentEventSubscriptionService>();
            services.AddScoped<ILlmService, LlmService>();
            services.AddScoped<IDebugService, DebugService>();
            services.AddScoped<IPrivacyService, PrivacyService>();
            services.AddScoped<IWorkspaceService, WorkspaceService>();

            // M2.10.0: Context & Memory
            services.AddScoped<ITokenBudgetService, TokenBudgetService>();
            services.AddScoped<IReactiveCompactor, ReactiveCompactor>();
            services.AddScoped<IMemoryDirectoryService, MemoryDirectoryService>();

            // M2.11.0: Workspace & LSP
            services.AddScoped<IWorkspaceRootService, WorkspaceRootService>();
            services.AddScoped<ILspManager, LspManager>();
            services.AddScoped<IFileChangeDetector, FileChangeDetector>();

            // M2.12.0: MCP & Plugins & Skills
            services.AddScoped<IMcpConnectionManager, McpConnectionManager>();
            services.AddScoped<IMcpReconnectPolicy, McpReconnectPolicy>();
            services.AddScoped<IMcpAuthService, McpAuthService>();
            services.AddScoped<IPluginManifestService, PluginManifestService>();
            services.AddScoped<ISkillLoader, SkillLoader>();

            // M2.13.0: Sandbox & Background Tasks
            services.AddScoped<ISandboxBackendRegistry, SandboxBackendRegistry>();
            services.AddScoped<IFileSyncService, FileSyncService>();
            services.AddScoped<IBackgroundTaskService, BackgroundTaskService>();

            // M2.14.0: Observability & SDK
            services.AddSingleton<IRuntimeMetricsCollector, RuntimeMetricsCollector>();
            services.AddSingleton<IDiagnosticPackageExporter, DiagnosticPackageExporter>();
            services.AddSingleton<ILocalSdkHost, LocalSdkHost>();

            // M4.5.0: Recovery & Session Memory & ReadFileTracker
            services.AddScoped<IRecoveryService, RecoveryService>();
            services.AddSingleton<ISessionMemoryService, SessionMemoryService>();
            services.AddScoped<IReadFileTracker, ReadFileTracker>();
            services.AddSingleton<IFlagLevelValidationService, FlagLevelValidationService>();

            // ViewModels
            services.AddSingleton<MainViewModel>();
            services.AddSingleton<SidebarViewModel>();
            services.AddSingleton<ChatPageViewModel>();
            services.AddSingleton<DebugPanelViewModel>();
            services.AddTransient<SettingsDialogViewModel>();
            services.AddTransient<BackgroundSettingsDialogViewModel>();
            services.AddTransient<AgentFileDialogViewModel>();
            services.AddTransient<PrivacyDataViewModel>();
            services.AddTransient<TeamWorkspaceViewModel>();
            services.AddTransient<ToolPlatformViewModel>();
            services.AddSingleton<UpdateNotificationViewModel>();

            // Window
            services.AddSingleton<MainWindow>();
            Log("DI configured.");
        }).Build();
        Log("Host built.");
    }

    private static void TryEnableHighDpi()
    {
        try
        {
            _ = SetProcessDpiAwarenessContext(DpiAwarenessContextPerMonitorAwareV2);
        }
        catch
        {
            // The manifest carries the same setting; this is a best-effort fallback.
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            Log("OnLaunched entered.");
            using var scope = _host.Services.CreateScope();
            Log("Initializing DB...");
            scope.ServiceProvider.GetRequiredService<TlahDbContext>().Initialize();
            Log("DB initialized.");

            Log("Resolving MainWindow...");
            MainWindow = _host.Services.GetRequiredService<MainWindow>();
            Log("MainWindow resolved.");
            MainWindow.Activate();
            Log("Window activated.");

            _host.Services.GetRequiredService<IThemeService>().Initialize();

            // Background update check
            var updateVM = _host.Services.GetRequiredService<UpdateNotificationViewModel>();
            _ = Task.Run(async () =>
            {
                await Task.Delay(500);
                var r = await _host.Services.GetRequiredService<IUpdateService>().CheckForUpdateAsync();
                if (r != null) MainWindow.DispatcherQueue.TryEnqueue(() => { updateVM.ShowUpdate(r); _ = ShowUpdateAsync(updateVM); });
            });
        }
        catch (Exception ex) { Log($"FATAL: {ex}"); throw; }
    }

    private static async Task ShowUpdateAsync(UpdateNotificationViewModel vm)
    {
        var d = new UpdateNotificationDialog();
        if (MainWindow?.Content.XamlRoot is { } xamlRoot) d.XamlRoot = xamlRoot;
        if (MainWindow?.Content is FrameworkElement root) d.RequestedTheme = root.ActualTheme;
        d.SetData(vm);
        if (MainWindow is TLAHStudio.App.MainWindow window)
            await window.TryShowDialogAsync(d, waitForTurn: true);
    }

    internal static void Log(string msg)
    {
        try { File.AppendAllText(Path.Combine(LogDir, "startup.log"), $"{DateTime.UtcNow:O} [{Environment.ProcessId}] {msg}\n"); } catch { }
    }
}
