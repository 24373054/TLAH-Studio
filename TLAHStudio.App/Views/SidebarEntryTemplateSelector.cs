using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TLAHStudio.App.ViewModels;

namespace TLAHStudio.App.Views;

public sealed class SidebarEntryTemplateSelector : DataTemplateSelector
{
    public DataTemplate? HeaderTemplate { get; set; }
    public DataTemplate? CompactHeaderTemplate { get; set; }
    public DataTemplate? ChatTemplate { get; set; }
    public DataTemplate? CompactChatTemplate { get; set; }
    public bool IsCompact { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) =>
        item is SidebarEntry { IsHeader: true } ? (IsCompact ? CompactHeaderTemplate : HeaderTemplate) :
        IsCompact ? CompactChatTemplate : ChatTemplate;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
        SelectTemplateCore(item);
}
