using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace MeowFilm.Capabilities;

// Immutable catalog receipts remain the business/cache snapshots. A stable display
// slot changes its receipt without replacing the mounted section and its cards.
internal sealed class MusicCatalogSectionDisplay(MusicCatalogSection section, MusicCatalogContext? context) : INotifyPropertyChanged
{
    public MusicCatalogSection Section { get; private set; } = section;
    internal MusicCatalogContext? Context { get; private set; } = context;
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Update(MusicCatalogSection value)
    {
        if (ReferenceEquals(Section, value)) return;
        Section = value;
        PropertyChanged?.Invoke(this, new(nameof(Section)));
    }

    internal void UpdateContext(MusicCatalogContext? value)
    {
        if (ReferenceEquals(Context, value)) return;
        Context = value;
        PropertyChanged?.Invoke(this, new(nameof(Context)));
    }
}

internal sealed class MusicCatalogSectionDisplayCollection : IDisposable
{
    internal ObservableCollection<MusicCatalogSectionDisplay> Items { get; } = [];
    private ObservableCollection<MusicCatalogSection>? source;
    private MusicCatalogContext? context;

    internal void SetContext(MusicCatalogContext? value)
    {
        if (ReferenceEquals(context, value)) return;
        context = value;
        foreach (var item in Items) item.UpdateContext(value);
    }

    internal void Observe(ObservableCollection<MusicCatalogSection>? value)
    {
        if (ReferenceEquals(source, value)) return;
        if (source is not null) source.CollectionChanged -= Changed;
        source = value;
        if (source is not null) source.CollectionChanged += Changed;
        Reconcile();
    }

    private void Changed(object? sender, NotifyCollectionChangedEventArgs e) => Reconcile();

    private static bool SameSection(MusicCatalogSection left, MusicCatalogSection right) =>
        ReferenceEquals(left, right) || (left.Id.Length > 0 || right.Id.Length > 0
            ? left.Id == right.Id : left.Title == right.Title && left.Type == right.Type && left.Layout == right.Layout);

    private void Reconcile()
    {
        int count = source?.Count ?? 0;
        for (int index = Items.Count - 1; index >= 0; index--)
            if (source?.Any(section => SameSection(Items[index].Section, section)) != true) Items.RemoveAt(index);
        for (int index = 0; index < count; index++)
        {
            var section = source![index];
            int existing = index;
            while (existing < Items.Count && !SameSection(Items[existing].Section, section)) existing++;
            if (existing == Items.Count) Items.Insert(index, new(section, context));
            else
            {
                if (existing != index) Items.Move(existing, index);
                Items[index].Update(section);
                Items[index].UpdateContext(context);
            }
        }
        while (Items.Count > count) Items.RemoveAt(Items.Count - 1);
    }

    public void Dispose() { if (source is not null) source.CollectionChanged -= Changed; source = null; }
}
