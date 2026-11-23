using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation.Collections;
using System.ComponentModel;
using System.Collections.Specialized;

namespace MeowFilm.Desktop.Artwork;

public sealed record ArtworkItemResource(string Slot, string Address, string DisplayIdentity = "");

// Native containers borrow presentations. Accepted items, rather than recycled visuals,
// own their successful underlying lease until the list or item actually retires.
public sealed class ArtworkItemsScope
{
    private static readonly DependencyProperty ScopeProperty = DependencyProperty.RegisterAttached(
        "Scope", typeof(ArtworkItemsScope), typeof(ArtworkItemsScope), new PropertyMetadata(null));
    public static readonly DependencyProperty ItemKeySelectorProperty = DependencyProperty.RegisterAttached(
        "ItemKeySelector", typeof(Func<object, object>), typeof(ArtworkItemsScope), new PropertyMetadata(null,
            (view, _) => { if (view.GetValue(ScopeProperty) is ArtworkItemsScope scope) scope.Clear(); }));
    public static void SetItemKeySelector(DependencyObject view, Func<object, object>? value) => view.SetValue(ItemKeySelectorProperty, value);
    public static Func<object, object>? GetItemKeySelector(DependencyObject view) => view.GetValue(ItemKeySelectorProperty) as Func<object, object>;
    public static readonly DependencyProperty ItemResourcesSelectorProperty = DependencyProperty.RegisterAttached(
        "ItemResourcesSelector", typeof(Func<object, IEnumerable<ArtworkItemResource>>), typeof(ArtworkItemsScope), new PropertyMetadata(null,
            (view, _) => { if (view.GetValue(ScopeProperty) is ArtworkItemsScope scope) scope.ReconcileItems(); }));
    public static void SetItemResourcesSelector(DependencyObject view, Func<object, IEnumerable<ArtworkItemResource>>? value) =>
        view.SetValue(ItemResourcesSelectorProperty, value);
    public static Func<object, IEnumerable<ArtworkItemResource>>? GetItemResourcesSelector(DependencyObject view) =>
        view.GetValue(ItemResourcesSelectorProperty) as Func<object, IEnumerable<ArtworkItemResource>>;
    private readonly ListViewBase list;
    private readonly Dictionary<ItemIdentity, Dictionary<string, Entry>> items = [];
    private readonly Dictionary<ItemIdentity, (INotifyPropertyChanged Item, PropertyChangedEventHandler Handler)> observed = [];
    private readonly IDisposable visibility;
    private INotifyCollectionChanged? sourceNotifications;
    private readonly long sourceCallback;
    private readonly Dictionary<DependencyObject, Dictionary<ArtworkImage, Binding>> bindings = [];
    private readonly struct ItemIdentity(object value, bool stable) : IEquatable<ItemIdentity>
    {
        private readonly object value = value;
        private readonly bool stable = stable;
        public bool Equals(ItemIdentity other) => stable == other.stable && (stable ? Equals(value, other.value) : ReferenceEquals(value, other.value));
        public override bool Equals(object? other) => other is ItemIdentity key && Equals(key);
        public override int GetHashCode() => stable ? value.GetHashCode() : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
    }
    private ItemIdentity Key(object item) => GetItemKeySelector(list) is { } selector ? new(selector(item), true) : new(item, false);
    private bool retired;
    internal readonly struct Resource(ArtworkService loader, string address, string identity) : IEquatable<Resource>
    {
        internal readonly ArtworkService Loader = loader;
        private readonly string address = address, identity = identity;
        public bool Equals(Resource other) => ReferenceEquals(Loader, other.Loader) && address == other.address && identity == other.identity;
        internal bool Matches(ArtworkItemResource other) => address == other.Address && identity == other.DisplayIdentity;
        internal bool Retains(ArtworkItemResource other) => identity.Length > 0 && identity == other.DisplayIdentity && other.Address.Length > 0;
        internal Entry Successor(bool inheritedLoader, Entry previous) => new(Loader, address, identity, inheritedLoader, previous);
        public override bool Equals(object? value) => value is Resource other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Loader), address, identity);
    }

    private ArtworkItemsScope(ListViewBase list)
    {
        this.list = list;
        list.Items.VectorChanged += ItemsChanged;
        sourceCallback = list.RegisterPropertyChangedCallback(ItemsControl.ItemsSourceProperty, (_, _) => ObserveSource());
        ObserveSource();
        list.ContainerContentChanging += ContainerChanged;
        list.Unloaded += Unloaded;
        visibility = DesktopVisual.ObserveVisibility(list, visible =>
        {
            if (!visible) CancelPending();
        });
    }
    internal static Binding? Find(ArtworkImage image)
    {
        DependencyObject? container = null;
        bool inheritedLoader = true;
        for (DependencyObject? node = image; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ListViewItem or GridViewItem) container = node;
            if (node is not ListViewBase list)
            {
                if (node.ReadLocalValue(ArtworkScope.LoaderProperty) != DependencyProperty.UnsetValue) inheritedLoader = false;
                continue;
            }
            if (container is null) return null;
            var scope = list.GetValue(ScopeProperty) as ArtworkItemsScope;
            if (scope is null || scope.retired) { scope = new(list); list.SetValue(ScopeProperty, scope); }
            if (scope.bindings.TryGetValue(container, out var priorImages) && priorImages.TryGetValue(image, out var prior)) prior.Detach();
            if (!scope.bindings.TryGetValue(container, out var images)) scope.bindings.Add(container, images = []);
            var binding = new Binding(scope, container, list.ItemFromContainer(container), inheritedLoader, image);
            images[image] = binding;
            return binding;
        }
        return null;
    }
    private void ContainerChanged(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!bindings.TryGetValue(args.ItemContainer, out var images)) return;
        // The source already owns the accepted replacement before WinUI retires
        // its old container. Hand off that slot while it still has a borrower.
        if (list.ItemsSource is System.Collections.IList source && args.ItemIndex >= 0 && args.ItemIndex < source.Count)
            foreach (var binding in images.Values.ToArray()) binding.RefreshAcceptedItem(source[args.ItemIndex]);
        if (!args.InRecycleQueue && args.Item is { } item && items.ContainsKey(Key(item)))
        { ObserveItem(item); RefreshResources(item); }
        foreach (var image in images.Keys.ToArray()) image.NativeItemChanged(args.InRecycleQueue);
    }
    internal static void LoaderChanged(DependencyObject view)
    {
        if (view is not ListViewBase || view.GetValue(ScopeProperty) is not ArtworkItemsScope scope) return;
        foreach (var (key, entries) in scope.items.ToArray())
        {
            foreach (var slot in entries.Where(pair => pair.Value.InheritedLoader).Select(pair => pair.Key).ToArray())
            { var entry = entries[slot]; entries.Remove(slot); entry.Retire(); }
            scope.RemoveEmptyItem(key);
        }
    }
    private void ObserveSource()
    {
        if (sourceNotifications is not null) sourceNotifications.CollectionChanged -= SourceChanged;
        sourceNotifications = list.ItemsSource as INotifyCollectionChanged;
        if (sourceNotifications is not null) sourceNotifications.CollectionChanged += SourceChanged;
        ReconcileItems();
    }
    private void ItemsChanged(IObservableVector<object> sender, IVectorChangedEventArgs args)
    {
        if (sourceNotifications is null) ReconcileItems();
    }
    private void SourceChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (args.Action == NotifyCollectionChangedAction.Reset) { ReconcileItems(); return; }
        if (args.Action == NotifyCollectionChangedAction.Move) return;
        object[] incoming = args.NewItems?.Cast<object>().ToArray() ?? [];
        var incomingKeys = incoming.Select(Key).ToHashSet();
        foreach (var item in args.OldItems?.Cast<object>() ?? [])
        {
            var key = Key(item);
            if (incomingKeys.Contains(key) || !items.Remove(key, out var entries)) continue;
            StopObserving(key);
            foreach (var entry in entries.Values) entry.Retire();
        }
        foreach (var item in incoming)
        {
            if (!items.ContainsKey(Key(item))) continue;
            ObserveItem(item); RefreshResources(item);
        }
    }
    private void ReconcileItems()
    {
        // The source has already completed a Move even if the native vector reports
        // its removal and insertion separately.
        var currentItems = list.ItemsSource is System.Collections.IEnumerable source ? source.Cast<object>().ToArray() : list.Items.ToArray();
        var members = currentItems.Select(Key).ToHashSet();
        foreach (var item in items.Keys.Where(item => !members.Contains(item)).ToArray())
        {
            var entries = items[item].Values.ToArray(); items.Remove(item); StopObserving(item);
            foreach (var entry in entries) entry.Retire();
        }
        foreach (var item in currentItems)
        {
            if (!items.ContainsKey(Key(item))) continue;
            ObserveItem(item); RefreshResources(item);
        }
    }
    private void ObserveItem(object item)
    {
        var key = Key(item);
        if (observed.TryGetValue(key, out var prior) && ReferenceEquals(prior.Item, item)) return;
        StopObserving(key);
        if (item is not INotifyPropertyChanged notification) return;
        PropertyChangedEventHandler changed = (sender, _) =>
        {
            if (observed.TryGetValue(key, out var current) && ReferenceEquals(current.Item, sender)) RefreshResources(current.Item);
        };
        notification.PropertyChanged += changed; observed.Add(key, (notification, changed));
    }
    private void StopObserving(ItemIdentity key)
    {
        if (observed.Remove(key, out var prior)) prior.Item.PropertyChanged -= prior.Handler;
    }
    private void RefreshResources(object item)
    {
        if (GetItemResourcesSelector(list) is not { } selector || !items.TryGetValue(Key(item), out var entries)) return;
        var resources = selector(item).ToDictionary(resource => resource.Slot, StringComparer.Ordinal);
        foreach (var slot in entries.Keys.ToArray())
        {
            var entry = entries[slot];
            if (resources.TryGetValue(slot, out var resource) && entry.Resource.Matches(resource)) continue;
            if (entry.HasBorrowers && resources.TryGetValue(slot, out resource) &&
                entry.Resource.Retains(resource) && entry.SuccessfulEntry is not null)
                entries[slot] = new(entry.Resource.Loader, resource.Address, resource.DisplayIdentity, entry.InheritedLoader, entry);
            else entries.Remove(slot);
            entry.Retire();
        }
        RemoveEmptyItem(Key(item));
    }
    private void RemoveEmptyItem(ItemIdentity key)
    {
        if (items.TryGetValue(key, out var entries) && entries.Count == 0)
        { items.Remove(key); StopObserving(key); }
    }
    private void CancelPending()
    {
        foreach (var (key, entries) in items.ToArray())
        {
            foreach (var slot in entries.Where(pair => !pair.Value.Work.IsCompleted).Select(pair => pair.Key).ToArray())
            {
                var entry = entries[slot];
                if (entry.SuccessfulEntry is not null) entries[slot] = entry.Resource.Successor(entry.InheritedLoader, entry);
                else entries.Remove(slot);
                entry.Retire();
            }
            RemoveEmptyItem(key);
        }
    }
    private void Clear()
    {
        var entries = items.Values.SelectMany(value => value.Values).ToArray(); items.Clear();
        foreach (var item in observed.Keys.ToArray()) StopObserving(item);
        foreach (var entry in entries) entry.Retire();
    }
    private void Unloaded(object sender, RoutedEventArgs args)
    {
        if (list.IsLoaded) return;
        retired = true; list.Items.VectorChanged -= ItemsChanged; list.ContainerContentChanging -= ContainerChanged; list.Unloaded -= Unloaded;
        list.UnregisterPropertyChangedCallback(ItemsControl.ItemsSourceProperty, sourceCallback);
        if (sourceNotifications is not null) sourceNotifications.CollectionChanged -= SourceChanged;
        bindings.Clear();
        visibility.Dispose(); Clear(); list.ClearValue(ScopeProperty);
    }
    internal sealed class Binding(ArtworkItemsScope scope, DependencyObject container, object? item, bool inheritedLoader, ArtworkImage image)
    {
        private bool recycled;
        private readonly ArtworkItemsScope owner = scope;
        private readonly ItemIdentity identity = item is null ? default : scope.Key(item);
        internal bool Current => !recycled && !owner.retired && item is not null && ReferenceEquals(owner.list.ItemFromContainer(container), item);
        internal bool SameItem(Binding other) => ReferenceEquals(owner, other.owner) && identity.Equals(other.identity);
        internal bool MatchesResource(string address, string displayIdentity, string slot)
        {
            if (!Current) return false;
            return GetItemResourcesSelector(owner.list) is not { } selector || selector(item!).Any(resource =>
                resource.Slot == slot && resource.Address == address && resource.DisplayIdentity == displayIdentity);
        }
        internal bool RetainsIdentity(string displayIdentity, string slot)
        {
            if (!Current || displayIdentity.Length == 0) return false;
            return GetItemResourcesSelector(owner.list) is { } selector && selector(item!).Any(resource =>
                resource.Slot == slot && resource.Address.Length > 0 && resource.DisplayIdentity == displayIdentity);
        }
        internal IArtworkLease? BorrowPrevious(string address, string displayIdentity, string slot)
        {
            if (!MatchesResource(address, displayIdentity, slot) || !owner.items.TryGetValue(identity, out var entries) ||
                !entries.TryGetValue(slot, out var entry)) return null;
            return entry.BorrowPrevious();
        }
        internal void Recycle() => recycled = true;
        internal void RefreshAcceptedItem(object? candidate)
        {
            if (item is null || candidate is null || !identity.Equals(owner.Key(candidate))) return;
            owner.ObserveItem(candidate); owner.RefreshResources(candidate);
        }
        internal void Detach()
        {
            if (owner.bindings.TryGetValue(container, out var images) && images.TryGetValue(image, out var current) && ReferenceEquals(current, this))
            {
                images.Remove(image);
                if (images.Count == 0) owner.bindings.Remove(container);
            }
        }
        internal void Retry(string slot)
        {
            if (!Current || !owner.items.TryGetValue(owner.Key(item!), out var entries) ||
                !entries.TryGetValue(slot, out var entry) || !entry.Work.IsFaulted) return;
            if (entry.SuccessfulEntry is not null) entries[slot] = entry.Resource.Successor(entry.InheritedLoader, entry);
            else entries.Remove(slot);
            entry.Retire(); owner.RemoveEmptyItem(owner.Key(item!));
        }
        internal void ChangeResource(ArtworkService? loader, string address, string identity, string slot)
        {
            if (!Current || !owner.items.TryGetValue(owner.Key(item!), out var entries) || !entries.TryGetValue(slot, out var entry)) return;
            if (loader is not null && entry.Resource.Equals(new Resource(loader, address, identity))) return;
            if (loader is not null && ReferenceEquals(loader, entry.Resource.Loader) &&
                entry.Resource.Retains(new(slot, address, identity)) && entry.SuccessfulEntry is not null)
                entries[slot] = new(loader, address, identity, entry.InheritedLoader, entry);
            else entries.Remove(slot);
            entry.Retire(); owner.RemoveEmptyItem(owner.Key(item!));
        }
        internal async Task<IArtworkLease> AcquireAsync(ArtworkService loader, string address, string identity, string slot, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!MatchesResource(address, identity, slot)) throw new OperationCanceledException();
            ChangeResource(loader, address, identity, slot);
            if (!owner.items.TryGetValue(owner.Key(item!), out var entries)) owner.items.Add(owner.Key(item!), entries = []);
            owner.ObserveItem(item!);
            if (!entries.TryGetValue(slot, out var entry))
            {
                entry = new(loader, address, identity, inheritedLoader); entries.Add(slot, entry);
            }
            entry.Start();
            entry.InheritedLoader |= inheritedLoader;
            entry.RetainPending = false; entry.Waiters++;
            try
            {
                await entry.Work.WaitAsync(token);
                token.ThrowIfCancellationRequested();
                if (!Current || entry.Retired) throw new OperationCanceledException();
                return entry.Borrow();
            }
            finally
            {
                if (recycled && DesktopVisual.IsVisible(owner.list)) entry.RetainPending = true;
                if (--entry.Waiters == 0 && !entry.Work.IsCompleted && !entry.RetainPending)
                {
                    if (entries.TryGetValue(slot, out var current) && ReferenceEquals(current, entry)) entries.Remove(slot);
                    entry.Retire(); owner.RemoveEmptyItem(owner.Key(item!));
                }
            }
        }
    }
    internal sealed class Presentation(ArtworkPixels pixels)
    {
        internal readonly Brush Raw = new SolidColorBrush(pixels.Palettes.Raw);
        internal readonly Brush Palette = new SolidColorBrush(pixels.Palettes.Palette);
        internal readonly Brush Top = new SolidColorBrush(pixels.Palettes.Top);
    }
    internal sealed class BorrowedLease(Entry entry) : IArtworkLease
    {
        private Entry? owner = entry;
        public ArtworkPixels Image => owner?.Lease!.Image ?? throw new ObjectDisposedException(nameof(BorrowedLease));
        internal Presentation Presentation => owner!.Presentation!;
        public void Dispose()
        {
            if (owner is not { } prior) return;
            owner = null; prior.Return();
        }
    }
    internal sealed class Entry(ArtworkService loader, string address, string identity, bool inheritedLoader, Entry? predecessor = null)
    {
        private Entry? previous = predecessor?.SuccessfulEntry;
        private IArtworkLease? previousLease = predecessor?.SuccessfulEntry?.Borrow();
        private readonly CancellationTokenSource cancellation = new();
        internal Task Work { get; private set; } = Task.CompletedTask;
        internal IArtworkLease? Lease { get; private set; }
        internal Presentation? Presentation { get; private set; }
        internal readonly Resource Resource = new(loader, address, identity);
        internal bool InheritedLoader = inheritedLoader;
        internal int Waiters;
        internal bool RetainPending;
        private int borrowers;
        internal bool HasBorrowers => borrowers > 0;
        private bool completed;
        private bool started;
        internal bool Retired { get; private set; }
        internal bool Succeeded => Lease is not null && !Retired;
        internal Entry? SuccessfulEntry => Lease is not null ? this : previousLease is not null ? previous : null;
        internal IArtworkLease? BorrowPrevious() => previousLease is not null ? previous?.Borrow() : null;
        internal void Start()
        {
            if (started) return;
            started = true;
            Work = Read();
            _ = Work.ContinueWith(work => _ = work.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
        private async Task Read()
        {
            try
            {
                var acquired = await loader.AcquireAsync(address, cancellation.Token);
                if (Retired) { acquired.Dispose(); return; }
                Lease = acquired; Presentation = new(acquired.Image);
                previousLease?.Dispose(); previousLease = null; previous = null;
            }
            finally { completed = true; cancellation.Dispose(); }
        }
        internal IArtworkLease Borrow() { borrowers++; return new BorrowedLease(this); }
        internal void Return() { borrowers--; Release(); }
        internal void Retire()
        {
            if (Retired) return;
            Retired = true;
            if (!completed) cancellation.Cancel();
            if (!started) { completed = true; cancellation.Dispose(); }
            previousLease?.Dispose(); previousLease = null; previous = null;
            Release();
        }
        private void Release()
        {
            if (!Retired || borrowers != 0) return;
            Lease?.Dispose(); Lease = null; Presentation = null;
        }
    }
}
