using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;

namespace MeowFilm.Desktop;

public sealed class ShellNavigationEntry : INotifyPropertyChanged
{
    internal ShellNavigationEntry(ModuleSession owner, NavigationItem item)
    {
        Owner = owner;
        Item = item;
    }

    internal ModuleSession Owner { get; }
    public NavigationItem Item { get; private set; }
    public string Route => Item.Route;
    public string Title => Item.Title;
    public string Glyph => Item.Glyph;
    public string? IconData => Item.IconData;
    public string? Group => Item.Group;
    public DesktopNavigationGroupAction? GroupAction => Item.GroupAction;
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Apply(NavigationItem next)
    {
        var previous = Item; Item = next;
        if (previous.Title != next.Title) Changed(nameof(Title));
        if (previous.Glyph != next.Glyph) Changed(nameof(Glyph));
        if (previous.IconData != next.IconData) Changed(nameof(IconData));
        if (previous.Group != next.Group) Changed(nameof(Group));
        if (!ReferenceEquals(previous.GroupAction, next.GroupAction)) Changed(nameof(GroupAction));
    }

    private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class ShellNavigationGroup : INotifyPropertyChanged
{
    internal ShellNavigationGroup(ModuleSession owner, string name)
    {
        Owner = owner;
        Name = name;
    }

    internal ModuleSession Owner { get; }
    public string Name { get; }
    public ObservableCollection<ShellNavigationEntry> Items { get; } = [];
    public DesktopNavigationGroupAction? Action { get; private set; }
    public bool HasAction => Action is not null;
    public string ActionAutomationName => Action?.AutomationName ?? "";
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void RefreshAction()
    {
        var next = Items.FirstOrDefault()?.GroupAction;
        if (ReferenceEquals(Action, next)) return;
        Action = next;
        PropertyChanged?.Invoke(this, new(nameof(Action)));
        PropertyChanged?.Invoke(this, new(nameof(HasAction)));
        PropertyChanged?.Invoke(this, new(nameof(ActionAutomationName)));
    }
}

public sealed partial class ShellWindow
{
    private readonly List<ShellNavigationEntry> _sidebar = [];
    private ModuleSession? _settingsReturn;
    private ModuleSession? _sidebarOwner;
    private readonly CollectionViewSource _collectionNavigation = new() { IsSourceGrouped = true, ItemsPath = new PropertyPath(nameof(ShellNavigationGroup.Items)) };
    private readonly ObservableCollection<ShellNavigationEntry> _contentNavigation = [];
    private readonly ObservableCollection<ShellNavigationGroup> _navigationGroups = [];

    private void RefreshNavigation()
    {
        ReconcileNavigation(_contentNavigation, _sidebar.Where(item => IsCurrentNavigation(item, false)).ToArray());
        var groups = _sidebar.Where(item => IsCurrentNavigation(item, true)).GroupBy(item => item.Group!);
        var next = new List<ShellNavigationGroup>();
        foreach (var group in groups)
        {
            var retained = _navigationGroups.FirstOrDefault(item => ReferenceEquals(item.Owner, _sidebarOwner) && item.Name == group.Key)
                ?? new ShellNavigationGroup(_sidebarOwner!, group.Key);
            ReconcileNavigation(retained.Items, group.ToArray());
            retained.RefreshAction();
            next.Add(retained);
        }
        ReconcileNavigation(_navigationGroups, next);
    }

    private static void ReconcileNavigation<T>(ObservableCollection<T> target, IReadOnlyList<T> next) where T : class
    {
        var members = next.ToHashSet(ReferenceEqualityComparer.Instance);
        for (int index = target.Count - 1; index >= 0; index--)
            if (!members.Contains(target[index])) target.RemoveAt(index);
        for (int index = 0; index < next.Count; index++)
        {
            if (index < target.Count && ReferenceEquals(target[index], next[index])) continue;
            int old = target.IndexOf(next[index]);
            if (old >= 0) target.Move(old, index); else target.Insert(index, next[index]);
        }
    }

    private void InitializeUnifiedNavigation()
    {
        NavigationList.ItemsSource = _contentNavigation;
        _collectionNavigation.Source = _navigationGroups;
        CollectionNavigationList.ItemsSource = _collectionNavigation.View;
        _sidebarOwner = _sessions[0];
        foreach (var session in _sessions) BindNavigation(session);
        ModuleNavigationList.ItemsSource = _sessions;
    }

    private bool IsCurrentNavigation(object value, bool grouped) => value is ShellNavigationEntry entry &&
        ReferenceEquals(entry.Owner, _sidebarOwner) && (entry.Group is not null) == grouped &&
        entry.Route != entry.Owner.Module.Navigation.FirstOrDefault()?.Route;

    private void ApplyNavigationOwner(ModuleSession current)
    {
        if (ReferenceEquals(_sidebarOwner, current)) return;
        var suppressed = _suppressSelection;
        _suppressSelection = true;
        try
        {
            _sidebarOwner = current;
            RefreshNavigation();
        }
        finally { _suppressSelection = suppressed; }
    }

    private void OnModuleSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || _isClosing || !ReferenceEquals(sender, ModuleNavigationList) ||
            ModuleNavigationList.SelectedItem is not ModuleSession session) return;
        RunUiAction(() => session.Module.Navigation.FirstOrDefault() is { } home
            ? NavigateFromUiAsync(session, home.Route)
            : ActivateAsync(session));
    }

    private void OnNavigationContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        args.ItemContainer.ClearValue(AutomationProperties.NameProperty);
        if (args.InRecycleQueue) return;
        string? name = args.Item switch
        {
            ModuleSession session => session.ModuleTitle,
            ShellNavigationEntry entry => entry.Title,
            IDesktopPage page => page.Title,
            _ => null
        };
        if (!string.IsNullOrEmpty(name))
            AutomationProperties.SetName(args.ItemContainer, name);
    }

    private void SynchronizeNavigation(ModuleSession session)
    {
        var suppressed = _suppressSelection;
        _suppressSelection = true;
        try
        {
            var next = new List<ShellNavigationEntry>();
            foreach (var item in session.Navigation!.Where(item => item.ShowInSidebar))
            {
                var entry = _sidebar.FirstOrDefault(entry => ReferenceEquals(entry.Owner, session) && entry.Route == item.Route)
                    ?? new ShellNavigationEntry(session, item);
                entry.Apply(item);
                if (NavigationList.ContainerFromItem(entry) is DependencyObject navContainer)
                    AutomationProperties.SetName(navContainer, entry.Title);
                if (CollectionNavigationList.ContainerFromItem(entry) is DependencyObject colContainer)
                    AutomationProperties.SetName(colContainer, entry.Title);
                next.Add(entry);
            }
            _sidebar.RemoveAll(entry => ReferenceEquals(entry.Owner, session));
            _sidebar.AddRange(next);
            if (ReferenceEquals(_sidebarOwner, session)) RefreshNavigation();
        }
        finally { _suppressSelection = suppressed; }
        if (ReferenceEquals(_current, session)) RestoreNavigationSelection();
    }
}
