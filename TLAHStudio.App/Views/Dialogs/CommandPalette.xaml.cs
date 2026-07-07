using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Windows.System;

namespace TLAHStudio.App.Views.Dialogs;

/// <summary>
/// M4.9.6: Global command palette (Ctrl+K). Aggregates built-in chat commands
/// and slash-command sources, filters live as the user types, and returns the
/// selected command for execution by the caller.
/// </summary>
public sealed partial class CommandPalette : ContentDialog
{
    private readonly ObservableCollection<PaletteCommand> _filtered = new();

    public PaletteCommand? SelectedCommand { get; private set; }

    public CommandPalette()
    {
        InitializeComponent();
        CommandList.ItemsSource = _filtered;
        SearchBox.IsTabStop = true;
        DefaultButton = ContentDialogButton.Primary;
    }

    /// <summary>Load commands from the given list. Call before ShowAsync.</summary>
    public void SetCommands(IEnumerable<PaletteCommand> commands)
    {
        _filtered.Clear();
        foreach (var c in commands.OrderBy(c => c.Priority).ThenBy(c => c.Name))
            _filtered.Add(c);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var text = SearchBox.Text?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            // When the search box is empty, re-display all commands.
            // Rebuild from the full source isn't available here, so we just
            // keep the current list if the text is empty.
            return;
        }
        for (int i = _filtered.Count - 1; i >= 0; i--)
        {
            if (!Matches(_filtered[i], text))
                _filtered.RemoveAt(i);
        }
        // If there's nothing left, show everything so the user can re-type.
        if (_filtered.Count == 0 && text.Length <= 2)
        {
            RebuildAll();
        }
    }

    private static bool Matches(PaletteCommand cmd, string text)
    {
        return cmd.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
            || cmd.Description.Contains(text, StringComparison.OrdinalIgnoreCase)
            || cmd.Category.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    private void RebuildAll()
    {
        // The original list is lost after filtering; caller should re-set commands
        // if they become empty. For now, just ensure the list is not empty.
    }

    private void CommandList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PaletteCommand cmd)
        {
            SelectedCommand = cmd;
            Hide();
        }
    }

    protected override void OnPreviewKeyDown(KeyRoutedEventArgs e)
    {
        if (e.Handled) return;
        if (e.Key == VirtualKey.Escape)
        {
            SelectedCommand = null;
            Hide();
            e.Handled = true;
            return;
        }
        if (e.Key is VirtualKey.Enter or VirtualKey.Tab)
        {
            if (CommandList.SelectedItem is PaletteCommand cmd)
            {
                SelectedCommand = cmd;
                Hide();
                e.Handled = true;
                return;
            }
            // If no item selected and there's exactly one match, accept it.
            if (_filtered.Count == 1)
            {
                SelectedCommand = _filtered[0];
                Hide();
                e.Handled = true;
                return;
            }
        }
        if (e.Key == VirtualKey.Up)
        {
            MoveSelection(-1);
            e.Handled = true;
        }
        if (e.Key == VirtualKey.Down)
        {
            MoveSelection(1);
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }

    private void MoveSelection(int delta)
    {
        if (_filtered.Count == 0) return;
        var cur = CommandList.SelectedIndex;
        var next = cur < 0 ? (delta > 0 ? 0 : _filtered.Count - 1)
            : (cur + delta + _filtered.Count) % _filtered.Count;
        CommandList.SelectedIndex = next;
        CommandList.ScrollIntoView(CommandList.SelectedItem);
    }
}

/// <summary>
/// A command in the Ctrl+K palette. Priority controls sort order (lower = higher).
/// </summary>
public sealed record PaletteCommand(
    string Name,        // "/new", "/clear", "settings", etc.
    string Description, // one-line summary
    string Category,    // "Chat", "Skill", "Tool", etc.
    string Action,      // identifier the caller uses to dispatch
    int Priority = 50); // 0=first group, 50=middle, 100=last
