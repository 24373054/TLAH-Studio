using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TLAHStudio.App.Models;
using TLAHStudio.App.Services;

namespace TLAHStudio.App.ViewModels;

public partial class WorkspaceReviewViewModel : ObservableObject
{
    private readonly IWorkspaceReviewService _reviewService;
    private readonly ChatPageViewModel _chat;
    private CancellationTokenSource? _refreshCancellation;
    private IReadOnlyDictionary<string, string> _diffByPath = new Dictionary<string, string>();

    public ObservableCollection<WorkspaceChange> Changes { get; } = new();

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _workspaceName = "Workspace";

    [ObservableProperty]
    private string _summary = "Choose a workspace to review local changes.";

    [ObservableProperty]
    private string _diffText = "Select a changed file to preview its diff.";

    [ObservableProperty]
    private WorkspaceChange? _selectedChange;

    public WorkspaceReviewViewModel(IWorkspaceReviewService reviewService, ChatPageViewModel chat)
    {
        _reviewService = reviewService;
        _chat = chat;
        _chat.PropertyChanged += (_, args) =>
        {
            if (IsOpen && args.PropertyName is nameof(ChatPageViewModel.WorkspacePath) or nameof(ChatPageViewModel.CurrentChat))
                _ = RefreshAsync();
        };
    }

    public async Task RefreshAsync()
    {
        _refreshCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _refreshCancellation = cancellation;
        IsLoading = true;
        try
        {
            var snapshot = await _reviewService.LoadAsync(_chat.WorkspacePath, cancellation.Token);
            if (cancellation.IsCancellationRequested || !ReferenceEquals(_refreshCancellation, cancellation))
                return;

            WorkspaceName = snapshot.WorkspaceName;
            Summary = snapshot.Summary;
            _diffByPath = snapshot.DiffByPath;
            Changes.Clear();
            foreach (var change in snapshot.Changes)
                Changes.Add(change);

            SelectedChange = Changes.FirstOrDefault();
            if (SelectedChange == null)
                DiffText = snapshot.Error ?? "No uncommitted changes to preview.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_refreshCancellation, cancellation))
            {
                _refreshCancellation = null;
                IsLoading = false;
            }
            cancellation.Dispose();
        }
    }

    partial void OnSelectedChangeChanged(WorkspaceChange? value)
    {
        DiffText = value != null && _diffByPath.TryGetValue(value.Path, out var diff)
            ? diff
            : value == null ? "Select a changed file to preview its diff." : "No textual diff is available for this change.";
    }
}
