using System.Collections;
using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using TLAHStudio.App.Models;
using TLAHStudio.App.ViewModels;
using TLAHStudio.Core.Services;
using Windows.ApplicationModel.DataTransfer;

namespace TLAHStudio.App.Views;

public sealed partial class WorkspaceReviewPanelControl : UserControl
{
    private WorkspaceReviewViewModel? _viewModel;
    private IInteractionSoundService? _sound;
    private readonly VirtualizedDiffLineSource _diffLines = new();
    private string _diffText = string.Empty;

    public WorkspaceReviewPanelControl()
    {
        InitializeComponent();
        DiffLinesRepeater.ItemsSource = _diffLines;
        ActualThemeChanged += (_, _) => RefreshThemeSensitiveItems();
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
                SetDiffText(viewModel.DiffText);
            else if (args.PropertyName == nameof(WorkspaceReviewViewModel.SelectedChange))
                ChangesList.SelectedItem = viewModel.SelectedChange;
        });
        Apply(viewModel);
    }

    private void Apply(WorkspaceReviewViewModel viewModel)
    {
        WorkspaceNameText.Text = viewModel.WorkspaceName;
        SummaryText.Text = viewModel.Summary;
        SetDiffText(viewModel.DiffText);
        ChangesList.SelectedItem = viewModel.SelectedChange;
    }

    private void SetDiffText(string? text)
    {
        _diffText = text ?? string.Empty;
        _diffLines.Reset(_diffText);
    }

    private void RefreshThemeSensitiveItems()
    {
        _diffLines.Invalidate();
        if (_viewModel == null)
            return;

        var selected = ChangesList.SelectedItem;
        ChangesList.ItemsSource = null;
        ChangesList.ItemsSource = _viewModel.Changes;
        ChangesList.SelectedItem = selected;
    }

    private void CopyDiff_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_diffText))
            return;

        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(_diffText);
        Clipboard.SetContent(package);
        Clipboard.Flush();
        _sound?.Play(InteractionSound.Toggle);
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

public sealed record WorkspaceDiffLine(int Number, string Text)
{
    public string AccessibleText => $"Line {Number}: {Text}";
}

/// <summary>
/// Indexes newline offsets once, then materializes strings only for the lines
/// currently realized by ItemsRepeater. A multi-megabyte diff therefore no
/// longer creates a full editing surface or one string/object per hidden line.
/// </summary>
internal sealed class VirtualizedDiffLineSource : IList, INotifyCollectionChanged
{
    private string _text = string.Empty;
    private readonly List<int> _lineStarts = new();

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public int Count => _lineStarts.Count;
    public bool IsFixedSize => true;
    public bool IsReadOnly => true;
    public bool IsSynchronized => false;
    public object SyncRoot => this;

    public object? this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_lineStarts.Count)
                throw new ArgumentOutOfRangeException(nameof(index));

            var start = _lineStarts[index];
            var end = index + 1 < _lineStarts.Count ? _lineStarts[index + 1] : _text.Length;
            while (end > start && _text[end - 1] is '\r' or '\n')
                end--;
            return new WorkspaceDiffLine(index + 1, _text.Substring(start, end - start));
        }
        set => throw new NotSupportedException();
    }

    public void Reset(string text)
    {
        if (ReferenceEquals(_text, text))
            return;

        _text = text;
        _lineStarts.Clear();
        if (_text.Length > 0)
        {
            _lineStarts.Add(0);
            for (var index = 0; index < _text.Length - 1; index++)
            {
                if (_text[index] == '\n')
                    _lineStarts.Add(index + 1);
            }
        }

        CollectionChanged?.Invoke(
            this,
            new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public void Invalidate() => CollectionChanged?.Invoke(
        this,
        new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));

    public IEnumerator GetEnumerator()
    {
        for (var index = 0; index < Count; index++)
            yield return this[index]!;
    }

    public void CopyTo(Array array, int index)
    {
        ArgumentNullException.ThrowIfNull(array);
        if (index < 0 || array.Length - index < Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        for (var offset = 0; offset < Count; offset++)
            array.SetValue(this[offset], index + offset);
    }

    public bool Contains(object? value) => IndexOf(value) >= 0;

    public int IndexOf(object? value) =>
        value is WorkspaceDiffLine line && line.Number > 0 && line.Number <= Count
            ? line.Number - 1
            : -1;

    public int Add(object? value) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public void Insert(int index, object? value) => throw new NotSupportedException();
    public void Remove(object? value) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
}

public sealed class WorkspaceDiffLineBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var text = value as string ?? string.Empty;
        var resourceKey = text.StartsWith('+')
            ? "DiffAddedBrush"
            : text.StartsWith('-')
                ? "DiffRemovedBrush"
                : text.StartsWith("@@", StringComparison.Ordinal)
                    ? "AccentBrush"
                    : "TextPrimaryBrush";
        return WorkspaceReviewBrushes.Resolve(resourceKey);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
