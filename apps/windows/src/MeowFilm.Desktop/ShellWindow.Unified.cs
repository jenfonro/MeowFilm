using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace MeowFilm.Desktop;

// Explicit view targets, not an App mode selector. Business owners remain isolated.
internal sealed record ShellNavigationEntry(ModuleSession Owner, NavigationItem Item)
{
    public string Route => Item.Route;
    public string Title => Item.Title;
    public string Glyph => Item.Glyph;
    public string? Group => Item.Group;
    public DesktopNavigationGroupAction? GroupAction => Item.GroupAction;
}

public sealed partial class ShellWindow
{
    private readonly ObservableCollection<ShellNavigationEntry> _sidebar = [];
    private readonly List<(ModuleSession Session, Destination Destination)> _history = [];
    private ModuleSession? _settingsReturn;

    private void InitializeUnifiedNavigation()
    {
        foreach (var session in _sessions) BindNavigation(session);
        var main = new ListCollectionView(_sidebar) { Filter = x => x is ShellNavigationEntry { Group: null } };
        var collections = new ListCollectionView(_sidebar) { Filter = x => x is ShellNavigationEntry { Group: not null } };
        collections.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ShellNavigationEntry.Group)));
        NavigationList.ItemsSource = main;
        CollectionNavigationList.ItemsSource = collections;
        RebuildSidebar();
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
