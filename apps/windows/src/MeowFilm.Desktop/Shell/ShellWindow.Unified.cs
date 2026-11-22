using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;

namespace MeowFilm.Desktop;

// The selected owner scopes navigation; business state remains capability-owned.
internal sealed class ShellNavigationEntry(ModuleSession owner, NavigationItem item) : INotifyPropertyChanged
{
    public ModuleSession Owner { get; } = owner;
    public NavigationItem Item { get; private set; } = item;
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

public sealed partial class ShellWindow
{
    private readonly List<ShellNavigationEntry> _sidebar = [];
    private readonly List<(ModuleSession Session, Destination Destination)> _history = [];
    private readonly List<(ModuleSession Session, Destination Destination)> _future = [];
    private ModuleSession? _settingsReturn;
    private ModuleSession? _sidebarOwner;
    private readonly CollectionViewSource _collectionNavigation = new() { IsSourceGrouped = true };
    private readonly ObservableCollection<ShellNavigationEntry> _contentNavigation = [];
    private readonly ObservableCollection<NavigationGroup> _navigationGroups = [];
    private sealed class NavigationGroup(ModuleSession owner, string name) : ObservableCollection<ShellNavigationEntry>
    {
        internal ModuleSession Owner { get; } = owner;
        public string Name { get; } = name;
        public DesktopNavigationGroupAction? Action { get; private set; }
        public bool HasAction => Action is not null;
        internal void RefreshAction()
        {
            var next = this.FirstOrDefault()?.GroupAction;
            if (ReferenceEquals(Action, next)) return;
            Action = next;
            OnPropertyChanged(new(nameof(Action))); OnPropertyChanged(new(nameof(HasAction)));
        }
    }
    private void RefreshNavigation()
    {
        ReconcileNavigation(_contentNavigation, _sidebar.Where(item => IsCurrentNavigation(item, false)).ToArray());
        var groups = _sidebar.Where(item => IsCurrentNavigation(item, true)).GroupBy(item => item.Group!);
        var next = new List<NavigationGroup>();
        foreach (var group in groups)
        {
            var retained = _navigationGroups.FirstOrDefault(item => ReferenceEquals(item.Owner, _sidebarOwner) && item.Name == group.Key)
                ?? new NavigationGroup(_sidebarOwner!, group.Key);
            ReconcileNavigation(retained, group.ToArray()); retained.RefreshAction(); next.Add(retained);
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
        if (args.InRecycleQueue)
        {
            if (args.ItemContainer.ContentTemplateRoot is FrameworkElement template) template.DataContext = null;
            args.ItemContainer.Content = null;
            return;
        }
        if (args.ItemContainer.ContentTemplateRoot is FrameworkElement content) content.ClearValue(FrameworkElement.DataContextProperty);
        args.ItemContainer.SetBinding(AutomationProperties.NameProperty, new Binding
        {
            Source = args.Item,
            Path = new PropertyPath(args.Item is ModuleSession ? "Module.Title" : "Title")
        });
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
                entry.Apply(item); next.Add(entry);
            }
            _sidebar.RemoveAll(entry => ReferenceEquals(entry.Owner, session));
            _sidebar.AddRange(next);
            if (ReferenceEquals(_sidebarOwner, session)) RefreshNavigation();
        }
        finally { _suppressSelection = suppressed; }
        if (ReferenceEquals(_current, session)) RestoreNavigationSelection();
    }

}
