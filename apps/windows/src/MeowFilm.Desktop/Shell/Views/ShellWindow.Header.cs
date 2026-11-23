using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;

namespace MeowFilm.Desktop;

public sealed partial class ShellWindow
{
    private void InitializeHeaderPresentation()
    {
        _search.Initialize(_pages);
        PageHeaderHost.ContributionChanged += (_, _) =>
        {
            if (!_isClosing && CurrentSession is { } current && PageHeaderHost.Contribution is { } contribution)
                ApplyHeaderContext(current, contribution);
        };
        PageHeaderHost.ActionCancelled += (_, _) => { if (!_isClosing) SetStatus("已取消"); };
        PageHeaderHost.OperationFailed += (_, _) => { if (!_isClosing) SetStatus("暂时无法完成操作", error: true); };
    }

    private void ApplyAccessibilityPresentation()
    {
        bool highContrast = SystemAppearance.IsHighContrast;
        DesktopTheme.SetIsHighContrast(Root, highContrast);
        var background = (Brush)DesktopVisual.Resource(Root, highContrast ? "MeowFilm.Brush.Window" : "MeowFilm.Brush.AppBackground");
        WindowFrame.Background = ApplicationBackground.Background = background;
        SidebarMaterial.Visibility = SidebarTint.Visibility = highContrast ? Visibility.Collapsed : Visibility.Visible;
        SidebarEdge.Visibility = highContrast || SidebarBackground.Visibility == Visibility.Collapsed ? Visibility.Collapsed : Visibility.Visible;
        UpdateCaption();
        UpdateStatusPresentation();
    }

    internal void UpdateCaption()
    {
        bool maximized = WindowState == DesktopWindowState.Maximized;
        MaximizeCaptionButton.Content = maximized ? "\uE923" : "\uE922";
        AutomationProperties.SetName(MaximizeCaptionButton, maximized ? "向下还原" : "最大化");
        WindowCaptionButtons.RequestedTheme = _playerDetailOpen ? ElementTheme.Dark : ElementTheme.Light;
        WindowCaptionButtons.Opacity = IsActive ? 1.0 : 0.68;
        _nativeCaption?.Refresh();
    }

    private void UpdateStatusPresentation()
    {
        StatusPanel.Visibility = _state.HasStatus && !_state.IsBusy ? Visibility.Visible : Visibility.Collapsed;
        StatusPanel.Background = (Brush)DesktopVisual.Resource(Root, _state.IsError ? "MeowFilm.Brush.ErrorSurface" : "MeowFilm.Brush.FloatingSurface");
        StatusPanel.BorderBrush = (Brush)DesktopVisual.Resource(Root, _state.IsError ? "MeowFilm.Brush.Error" : "MeowFilm.Brush.Border");
    }

    private void OnMinimizeCaptionClick(object sender, RoutedEventArgs e) { if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p) p.Minimize(); }
    private void OnMaximizeCaptionClick(object sender, RoutedEventArgs e)
    {
        if (AppWindow.Presenter is not Microsoft.UI.Windowing.OverlappedPresenter p) return;
        if (p.State == Microsoft.UI.Windowing.OverlappedPresenterState.Maximized) p.Restore(); else p.Maximize();
    }
    private void OnCloseCaptionClick(object sender, RoutedEventArgs e) => Close();

    private void ApplyHeaderPresentation(ModuleSession current)
    {
        if (_currentPage is null && current.IsSettingsOpen)
        {
            SelectSettingsTab(current);
            var additionalActions = _pages
                .Where(item => !item.Page.ShowInSidebar && item.Page is not IDesktopSearchPage)
                .Select(target => new DesktopHeaderAction(
                    "page/" + target.Page.Id, target.Page.Title,
                    token =>
                    {
                        token.ThrowIfCancellationRequested();
                        return ActivatePageAsync(target);
                    },
                    () => !_isClosing, automationName: target.Page.Title))
                .ToArray();
            PageHeaderHost.Present(null,
                new("设置", SearchVisible: false, SearchEnabled: false, Slot: _settingsTabs),
                "设置", additionalActions);
            return;
        }
        var source = ResolveHeaderSource(current);
        var fallback = _currentPage is { } page
            ? new DesktopHeaderContribution(page.Page.Title, SearchVisible: false, SearchEnabled: false)
            : new DesktopHeaderContribution(current.Module.Title);
        PageHeaderHost.Present(source, fallback);
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
