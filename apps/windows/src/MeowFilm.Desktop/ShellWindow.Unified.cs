using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

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
    private ListCollectionView _mainNavigation = null!, _collectionNavigation = null!;

    private void InitializeUnifiedNavigation()
    {
        foreach (var session in _sessions) BindNavigation(session);
        _sidebarOwner = _sessions[0];
        ModuleNavigationList.ItemsSource = _sessions;
        _mainNavigation = new ListCollectionView(_sidebar) { Filter = x => IsCurrentNavigation(x, grouped: false) };
        _collectionNavigation = new ListCollectionView(_sidebar) { Filter = x => IsCurrentNavigation(x, grouped: true) };
        _collectionNavigation.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ShellNavigationEntry.Group)));
        NavigationList.ItemsSource = _mainNavigation;
        CollectionNavigationList.ItemsSource = _collectionNavigation;
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
            _mainNavigation.Refresh();
            _collectionNavigation.Refresh();
        }
        finally { _suppressSelection = false; }
    }

    private void OnModuleSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || _isClosing || !ReferenceEquals(e.Source, ModuleNavigationList) ||
            ModuleNavigationList.SelectedItem is not ModuleSession session) return;
        RunUiAction(() => session.Module.Navigation.FirstOrDefault() is { } home
            ? NavigateFromUiAsync(session, home.Route)
            : ActivateAsync(session));
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
        }
        finally { _suppressSelection = false; }
        RestoreNavigationSelection();
    }

}
