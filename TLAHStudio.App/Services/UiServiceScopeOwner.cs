using Microsoft.Extensions.DependencyInjection;

namespace TLAHStudio.App.Services;

/// <summary>
/// Owns the dedicated dependency-injection scopes used by long-lived UI
/// objects. Each created object receives an isolated scoped service graph, so
/// chat, sidebar, debug, and dialog work never share an EF DbContext merely
/// because the application window is a singleton.
/// </summary>
internal sealed class UiServiceScopeOwner : IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly object _gate = new();
    private readonly List<IServiceScope> _scopes = [];
    private bool _disposed;

    public UiServiceScopeOwner(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public T Create<T>() where T : notnull
    {
        IServiceScope scope;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            scope = _scopeFactory.CreateScope();
            _scopes.Add(scope);
        }

        try
        {
            return ActivatorUtilities.CreateInstance<T>(scope.ServiceProvider);
        }
        catch
        {
            lock (_gate)
                _scopes.Remove(scope);
            scope.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        IServiceScope[] scopes;
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            scopes = _scopes.ToArray();
            _scopes.Clear();
        }

        for (var i = scopes.Length - 1; i >= 0; i--)
            scopes[i].Dispose();
    }
}
