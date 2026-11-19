using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;

namespace MeowFilm.Desktop;

// The selected owner scopes navigation; business state remains capability-owned.
internal sealed record ShellNavigationEntry(ModuleSession Owner, NavigationItem Item)
{
    public string Route => Item.Route;
    public string Title => Item.Title;
    public string Glyph => Item.Glyph;
    public string? IconData => Item.IconData;
    public string? Group => Item.Group;
    public DesktopNavigationGroupAction? GroupAction => Item.GroupAction;
}

public sealed partial class ShellWindow
{
    private readonly ObservableCollection<ShellNavigationEntry> _sidebar = [];
    private readonly List<(ModuleSession Session, Destination Destination)> _history = [];
    private readonly List<(ModuleSession Session, Destination Destination)> _future = [];
    private ModuleSession? _settingsReturn;
    private ModuleSession? _sidebarOwner;
    private readonly CollectionViewSource _collectionNavigation = new() { IsSourceGrouped = true };
    private sealed class NavigationGroup(string name, IEnumerable<ShellNavigationEntry> entries) : List<ShellNavigationEntry>(entries)
    {
        public string Name => name;
        public DesktopNavigationGroupAction? Action => this.FirstOrDefault()?.GroupAction;
        public bool HasAction => Action is not null;
    }
    private void RefreshNavigation()
    {
        NavigationList.ItemsSource = _sidebar.Where(item => IsCurrentNavigation(item, false)).ToArray();
        _collectionNavigation.Source = _sidebar.Where(item => IsCurrentNavigation(item, true))
            .GroupBy(item => item.Group!).Select(group => new NavigationGroup(group.Key, group)).ToArray();
        CollectionNavigationList.ItemsSource = _collectionNavigation.View;
    }

    private void InitializeUnifiedNavigation()
    {
        foreach (var session in _sessions) BindNavigation(session);
        _sidebarOwner = _sessions[0];
        ModuleNavigationList.ItemsSource = _sessions;
        RebuildSidebar();
    }

    private bool IsCurrentNavigation(object value, bool grouped) => value is ShellNavigationEntry entry &&
        ReferenceEquals(entry.Owner, _sidebarOwner) && (entry.Group is not null) == grouped &&
        entry.Route != entry.Owner.Module.Navigation.FirstOrDefault()?.Route;

    private void ApplyNavigationOwner(ModuleSession current)
    {
        if (ReferenceEquals(_sidebarOwner, current)) return;
        _suppressSelection = true;
        try
        {
            _sidebarOwner = current;
            RefreshNavigation();
        }
        finally { _suppressSelection = false; }
    }

    private void OnModuleSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || _isClosing || !ReferenceEquals(e.OriginalSource, ModuleNavigationList) ||
            ModuleNavigationList.SelectedItem is not ModuleSession session) return;
        RunUiAction(() => session.Module.Navigation.FirstOrDefault() is { } home
            ? NavigateFromUiAsync(session, home.Route)
            : ActivateAsync(session));
    }

    private void OnNavigationContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        args.ItemContainer.ClearValue(AutomationProperties.NameProperty);
        if (args.InRecycleQueue) return;
        args.ItemContainer.SetBinding(AutomationProperties.NameProperty, new Binding
        {
            Source = args.Item,
            Path = new PropertyPath(args.Item is ModuleSession ? "Module.Title" : "Title")
        });
    }

    private void RebuildSidebar()
    {
        _suppressSelection = true;
        try
        {
            _sidebar.Clear();
            foreach (var session in _sessions)
                foreach (var item in session.Module.Navigation.Where(x => x.ShowInSidebar))
                    _sidebar.Add(new(session, item));
            RefreshNavigation();
        }
        finally { _suppressSelection = false; }
        RestoreNavigationSelection();
    }

}
