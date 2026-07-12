using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TLAHStudio.App.Models;
using TLAHStudio.App.ViewModels;
using TLAHStudio.Core.Services;

namespace TLAHStudio.App.Views;

public sealed partial class WorkspaceReviewPanelControl : UserControl
{
    private WorkspaceReviewViewModel? _viewModel;
    private IInteractionSoundService? _sound;

    public WorkspaceReviewPanelControl()
    {
        InitializeComponent();
    }

    public void Bind(WorkspaceReviewViewModel viewModel, IInteractionSoundService sound)
    {
        _viewModel = viewModel;
        _sound = sound;
        ChangesList.ItemsSource = viewModel.Changes;
        viewModel.PropertyChanged += (_, args) => DispatcherQueue.TryEnqueue(() =>
        {
            if (args.PropertyName == nameof(WorkspaceReviewViewModel.WorkspaceName))
                WorkspaceNameText.Text = viewModel.WorkspaceName;
            else if (args.PropertyName is nameof(WorkspaceReviewViewModel.Summary) or nameof(WorkspaceReviewViewModel.IsLoading))
                SummaryText.Text = viewModel.IsLoading ? "Refreshing changes…" : viewModel.Summary;
            else if (args.PropertyName == nameof(WorkspaceReviewViewModel.DiffText))
                DiffTextBox.Text = viewModel.DiffText;
            else if (args.PropertyName == nameof(WorkspaceReviewViewModel.SelectedChange))
                ChangesList.SelectedItem = viewModel.SelectedChange;
        });
        Apply(viewModel);
    }

    private void Apply(WorkspaceReviewViewModel viewModel)
    {
        WorkspaceNameText.Text = viewModel.WorkspaceName;
        SummaryText.Text = viewModel.Summary;
        DiffTextBox.Text = viewModel.DiffText;
        ChangesList.SelectedItem = viewModel.SelectedChange;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel == null)
            return;

        _sound?.Play(InteractionSound.Toggle);
        await _viewModel.RefreshAsync();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _sound?.Play(InteractionSound.Toggle);
        if (App.MainWindow is MainWindow window)
            window.ToggleWorkspaceReviewPanel(false);
    }

    private void ChangesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel != null && ChangesList.SelectedItem is WorkspaceChange change)
            _viewModel.SelectedChange = change;
    }
}
