using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.UI.Xaml;
using TLAHStudio.Core.Llm;
using TLAHStudio.Core.Services;
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
            services.AddSingleton<IAppReleaseService, AppReleaseService>();
            services.AddScoped<IChatService, ChatService>();
            services.AddScoped<ISettingsService, SettingsService>();
            services.AddSingleton<ISandboxCommandService, SandboxCommandService>();
            services.AddScoped<IToolPlatformService, ToolPlatformService>();
            services.AddSingleton<INetworkSecurityService, NetworkSecurityService>();
            services.AddScoped<IExecutionBackendRouter, ExecutionBackendRouter>();
            services.AddScoped<IMcpClientService, McpClientService>();
            services.AddScoped<IAgentTool, SandboxExecAgentTool>();
            services.AddScoped<IAgentTool, TerminalExecAgentTool>();
            services.AddScoped<IAgentTool, FileListAgentTool>();
            services.AddScoped<IAgentTool, FileReadAgentTool>();
            services.AddScoped<IAgentTool, FileWriteAgentTool>();
            services.AddScoped<IAgentTool, FileSearchAgentTool>();
            services.AddScoped<IAgentTool, GitAgentTool>();
            services.AddScoped<IAgentTool, HttpRequestAgentTool>();
            services.AddScoped<IAgentTool, WebSearchAgentTool>();
            services.AddScoped<IAgentTool, BrowserReadAgentTool>();
            services.AddScoped<IAgentTool, McpListToolsAgentTool>();
            services.AddScoped<IAgentTool, McpCallAgentTool>();
            services.AddScoped<IAgentToolRegistry, AgentToolRegistry>();
            services.AddScoped<ILlmService, LlmService>();
            services.AddScoped<IDebugService, DebugService>();
            services.AddScoped<IPrivacyService, PrivacyService>();
            services.AddScoped<IWorkspaceService, WorkspaceService>();

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
            using var scope = _host.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<TlahDbContext>().Initialize();
            Log("DB initialized.");

            MainWindow = _host.Services.GetRequiredService<MainWindow>();
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
