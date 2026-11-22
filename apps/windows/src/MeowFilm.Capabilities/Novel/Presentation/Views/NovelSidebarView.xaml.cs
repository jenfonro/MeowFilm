using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeowFilm.Capabilities;

public sealed class NovelSidebarSourceItem : INotifyPropertyChanged
{
    private string name;
    private bool canExplore;
    private bool selected;

    public string InstallationId { get; }
    public string Name => name;
    public bool CanExplore => canExplore;
    public Visibility SelectedVisibility => selected ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    public NovelSidebarSourceItem(string installationId, string name, bool canExplore, bool selected)
    {
        InstallationId = installationId;
        this.name = name;
        this.canExplore = canExplore;
        this.selected = selected;
    }

    internal void Update(string nextName, bool nextCanExplore, bool nextSelected)
    {
        if (name != nextName)
        {
            name = nextName;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        }
        if (canExplore != nextCanExplore)
        {
            canExplore = nextCanExplore;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanExplore)));
        }
        if (selected != nextSelected)
        {
            selected = nextSelected;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedVisibility)));
        }
    }
}

public sealed partial class NovelSidebarView : UserControl
{
    private readonly NovelCapability module;
    private readonly ObservableCollection<NovelSidebarSourceItem> items = [];
    private readonly Dictionary<string, NovelSidebarSourceItem> lookup = new(StringComparer.Ordinal);
    private IReadOnlyList<NovelSource>? displayedSources;
    private string? displayedSelection;

    internal NovelSidebarView(NovelCapability module)
    {
        this.module = module;
        InitializeComponent();
        SourceRows.ItemsSource = items;
    }

    private void Manage_Click(object sender, RoutedEventArgs e) => module.RequestSettings();
    private void Import_Click(object sender, RoutedEventArgs e) => module.RunUi(ct => module.ImportSourcesAsync(this, ct));

    private void Source_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: NovelSidebarSourceItem item }) return;
        var current = module.Sources.Sources.FirstOrDefault(s => s.InstallationId == item.InstallationId);
        if (current is null) return;
        module.RunUi(async ct =>
        {
            await module.LoadDiscoveryAsync(current, null, true, ct);
            if (module.Route is not (NovelCapability.HomeRoute or NovelCapability.CategoriesRoute))
                module.Request(NovelCapability.HomeRoute);
        });
    }

    internal void Present()
    {
        bool changedSources = !ReferenceEquals(displayedSources, module.Sources.Sources);
        string? selection = module.ExploreSource?.InstallationId;
        if (!changedSources && displayedSelection == selection) return;

        if (changedSources)
        {
            displayedSources = module.Sources.Sources;
            var desired = module.Sources.Sources.Where(s => s.Enabled).ToArray();
            var identities = desired.Select(s => s.InstallationId).ToHashSet(StringComparer.Ordinal);
            for (int i = items.Count - 1; i >= 0; i--)
            {
                if (!identities.Contains(items[i].InstallationId))
                {
                    lookup.Remove(items[i].InstallationId);
                    items.RemoveAt(i);
                }
            }
            for (int index = 0; index < desired.Length; index++)
            {
                var source = desired[index];
                bool isSelected = source.InstallationId == selection;
                bool canExplore = NovelCapability.CanExplore(source);
                if (lookup.TryGetValue(source.InstallationId, out var existing))
                {
                    existing.Update(source.Name, canExplore, isSelected);
                    int currentIdx = items.IndexOf(existing);
                    if (currentIdx != index && currentIdx >= 0) items.Move(currentIdx, index);
                }
                else
                {
                    var item = new NovelSidebarSourceItem(source.InstallationId, source.Name, canExplore, isSelected);
                    lookup.Add(source.InstallationId, item);
                    items.Insert(index, item);
                }
            }
        }
        else
        {
            if (displayedSelection is not null && lookup.TryGetValue(displayedSelection, out var previous))
                previous.Update(previous.Name, previous.CanExplore, false);
            if (selection is not null && lookup.TryGetValue(selection, out var current))
                current.Update(current.Name, current.CanExplore, true);
        }
        displayedSelection = selection;
    }
}
