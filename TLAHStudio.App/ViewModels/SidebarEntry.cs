using TLAHStudio.Core.Services;

namespace TLAHStudio.App.ViewModels;

public sealed record SidebarEntry(string? Header, ChatSummaryDto? Chat)
{
    public bool IsHeader => Header != null;
}
