namespace TLAHStudio.Core.Services.Lsp;

public sealed record DiagnosticItem(string FilePath, int Line, int Column, string Severity, string Message, string? Code, string? Source);

public sealed record LspLanguageConfig(string LanguageId, string[] Extensions, string Command, string[] Args);

public interface ILspManager
{
    Task StartAsync(string languageId, string rootPath, CancellationToken ct = default);
    Task StopAsync(string languageId, CancellationToken ct = default);
    Task<IReadOnlyList<DiagnosticItem>> GetDiagnosticsAsync(string languageId, string filePath, CancellationToken ct = default);
    bool IsAvailable(string languageId);
    Task<IReadOnlyList<LspLanguageConfig>> GetConfiguredLanguagesAsync(CancellationToken ct = default);
}

public class LspManager : ILspManager
{
    private static readonly List<LspLanguageConfig> DefaultConfigs =
    [
        new("csharp", [".cs"], "dotnet", ["roslyn-server"]),
        new("typescript", [".ts", ".tsx"], "typescript-language-server", ["--stdio"]),
        new("python", [".py"], "pyright-langserver", ["--stdio"]),
        new("rust", [".rs"], "rust-analyzer", []),
    ];

    private readonly Dictionary<string, bool> _availability = new();

    public Task StartAsync(string languageId, string rootPath, CancellationToken ct = default)
    {
        _availability[languageId] = File.Exists(rootPath); // Basic check
        return Task.CompletedTask;
    }

    public Task StopAsync(string languageId, CancellationToken ct = default)
    {
        _availability.Remove(languageId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DiagnosticItem>> GetDiagnosticsAsync(string languageId, string filePath, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<DiagnosticItem>>(Array.Empty<DiagnosticItem>());

    public bool IsAvailable(string languageId) => _availability.GetValueOrDefault(languageId, false);

    public Task<IReadOnlyList<LspLanguageConfig>> GetConfiguredLanguagesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<LspLanguageConfig>>(DefaultConfigs);
}
