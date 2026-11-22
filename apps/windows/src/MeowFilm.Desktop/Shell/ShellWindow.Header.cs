using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;

namespace MeowFilm.Desktop;

public sealed partial class ShellWindow
{
    private void InitializeHeaderPresentation()
    {
        _search.Initialize(_pages);
        PageHeaderHost.ContributionChanged += (_, _) =>
        {
            if (!_isClosing && _current is { } current && PageHeaderHost.Contribution is { } contribution)
                ApplyHeaderContext(current, contribution);
        };
        PageHeaderHost.ActionCancelled += (_, _) => { if (!_isClosing) SetStatus("已取消"); };
        PageHeaderHost.OperationFailed += (_, _) => { if (!_isClosing) SetStatus("暂时无法完成操作", error: true); };
    }

    private void ApplyHeaderPresentation(ModuleSession current)
    {
        var source = ResolveHeaderSource(current);
        var fallback = _currentPage is { } page
            ? new DesktopHeaderContribution(page.Page.Title, SearchVisible: false, SearchEnabled: false)
            : current.IsSettingsOpen
                ? new DesktopHeaderContribution("", SearchVisible: false, SearchEnabled: false)
                : new DesktopHeaderContribution(current.Module.Title);
        bool settings = _currentPage is null && current.IsSettingsOpen;
        var additionalActions = settings
            ? _pages.Where(item => !item.Page.ShowInSidebar && item.Page is not IDesktopSearchPage).Select(target => new DesktopHeaderAction(
                "page/" + target.Page.Id, target.Page.Title,
                token =>
                {
                    token.ThrowIfCancellationRequested();
                    return ActivatePageAsync(target);
                },
                () => !_isClosing, automationName: target.Page.Title)).ToArray()
            : [];
        if (settings)
        {
            SelectSettingsTab(current);
            PageHeaderHost.Present(null,
                new("设置", SearchVisible: false, SearchEnabled: false, Slot: _settingsTabs),
                "设置", additionalActions);
        }
        else PageHeaderHost.Present(source, fallback, additionalActions: additionalActions);
    }

    private void ApplyHeaderContext(ModuleSession current, DesktopHeaderContribution contribution)
    {
        _search.ApplyHeaderContext(current, contribution);
        var title = string.IsNullOrWhiteSpace(contribution.Title) ? current.Module.Title : contribution.Title;
        AutomationProperties.SetName(ContentDeck, title);
        Title = string.IsNullOrWhiteSpace(title) ? "meowfilm" : $"meowfilm · {title}";
    }

    private IDesktopHeaderSource? ResolveHeaderSource(ModuleSession current)
    {
        if (_currentPage is { } page)
            return page.Page as IDesktopHeaderSource ?? page.Host.Content as IDesktopHeaderSource;
        return (current.IsSettingsOpen ? current.SettingsHost.Content : current.MainHost.Content) as IDesktopHeaderSource;
    }

    internal void RetireHeaderPresentation() => PageHeaderHost.Retire();
}
