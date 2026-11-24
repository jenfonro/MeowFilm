using System.Collections.ObjectModel;
using System.Text.Json;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Capabilities;

public enum NovelDiscoveryState { Idle, Loading, Cancelling, Ready, Empty, Failed, Cancelled, Unavailable }

public sealed partial class NovelCapability
{
    private CancellationTokenSource? discovery;
    private long discoveryGeneration;
    private int explorePage;
    private (string InstallationId, long Revision)? loadedKindsSource;
    private bool discoveryInvalidated = true, retryDiscoveryReset;
    private bool HasLoadedKindsFor(NovelSource? source) =>
        loadedKindsSource is { } loaded && source is not null &&
        loaded.InstallationId == source.InstallationId && loaded.Revision == source.Revision;
    private bool discoveryNeedsReload
    {
        get => discoveryInvalidated ||
            NovelDiscoveryKinds.Select(Kinds, ExploreKind, Route == HomeRoute)?.Url != ExploreKind?.Url;
        set => discoveryInvalidated = value;
    }
    public NovelSource? ExploreSource { get; private set; }
    public NovelKind? ExploreKind { get; private set; }
    public IReadOnlyList<NovelKind> Kinds { get; private set; } = [];
    public ObservableCollection<NovelBook> DiscoverBooks { get; } = [];
    public bool MoreDiscovery { get; private set; }
    public bool LoadingDiscovery => discovery is not null;
    public NovelDiscoveryState DiscoveryState { get; private set; }
    public string DiscoveryError { get; private set; } = "";
    internal long DiscoveryGeneration => discoveryGeneration;
    internal static bool CanExplore(NovelSource source) => source.Enabled && source.EnabledExplore &&
        source.Actions.Contains("exploreKinds") && source.Actions.Contains("explore");

    private void InvalidateDiscoverySource()
    {
        if (ExploreSource is not { } selected) { discoveryNeedsReload = true; return; }
        var current = Sources.Sources.FirstOrDefault(s => s.InstallationId == selected.InstallationId);
        if (current is not null && current.Revision == selected.Revision &&
            (CanExplore(current) || !CanExplore(selected)))
        { ExploreSource = current; return; }
        discovery?.Cancel();
        // Keep the selected identity, including a removed source, until the user chooses another.
        ExploreSource = current ?? selected;
        loadedKindsSource = null;
        Kinds = []; ExploreKind = null; DiscoverBooks.Clear(); MoreDiscovery = false; explorePage = 0;
        discoveryNeedsReload = true; DiscoveryError = "";
        DiscoveryState = current is not null && CanExplore(current) ? NovelDiscoveryState.Idle : NovelDiscoveryState.Unavailable;
    }

    internal void SelectExploreSource(NovelSource source)
    {
        if (ExploreSource?.InstallationId == source.InstallationId && ExploreSource?.Revision == source.Revision) return;
        discovery?.Cancel();
        ExploreSource = source;
        loadedKindsSource = null;
        Kinds = []; ExploreKind = null; DiscoverBooks.Clear(); MoreDiscovery = false; explorePage = 0;
        discoveryNeedsReload = true; DiscoveryError = "";
        DiscoveryState = CanExplore(source) ? NovelDiscoveryState.Idle : NovelDiscoveryState.Unavailable;
    }

    public Task LoadDiscoveryAsync(NovelSource? source, NovelKind? kind, bool reset, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (kind is not null && string.IsNullOrWhiteSpace(kind.Url)) return Task.CompletedTask;
        source ??= ExploreSource;
        bool changedSource = source?.InstallationId != ExploreSource?.InstallationId || source?.Revision != ExploreSource?.Revision;
        bool changedKind = kind is not null && kind.Url != ExploreKind?.Url;
        if (!reset && !changedSource && !changedKind && explorePage > 0 && !MoreDiscovery && !discoveryNeedsReload)
            return Task.CompletedTask;

        return work.Run(async operation =>
        {
            discovery?.Cancel();
            using var owner = CancellationTokenSource.CreateLinkedTokenSource(operation, presentation.Token);
            var cancellation = owner.Token;
            discovery = owner;
            long generation = ++discoveryGeneration;
            bool hasKinds = !changedSource && HasLoadedKindsFor(source);
            bool reloadKinds = reset || changedSource || !hasKinds;
            bool routeReselect = !reloadKinds && discoveryNeedsReload;
            bool replacePage = reloadKinds || changedKind || routeReselect;
            retryDiscoveryReset = reloadKinds;
            var requestedKind = kind ?? (changedSource ? null : ExploreKind);
            try
            {
                if (changedSource)
                {
                    // Commit the selected source before EnsureExecutionReadyAsync so
                    // failure retry always targets the newly chosen source.
                    ExploreSource = source;
                    loadedKindsSource = null;
                    Kinds = []; ExploreKind = null;
                    DiscoverBooks.Clear(); MoreDiscovery = false; explorePage = 0; discoveryNeedsReload = true;
                }
                else if (changedKind)
                {
                    DiscoverBooks.Clear(); MoreDiscovery = false; explorePage = 0; discoveryNeedsReload = true;
                    if (kind is not null) ExploreKind = kind;
                }
                DiscoveryState = NovelDiscoveryState.Loading; DiscoveryError = ""; Publish();
                await Sources.EnsureExecutionReadyAsync(cancellation);
                cancellation.ThrowIfCancellationRequested();
                if (generation != discoveryGeneration) return false;
                source ??= Sources.Sources.FirstOrDefault(CanExplore);
                var current = Sources.Sources.FirstOrDefault(s => s.InstallationId == source?.InstallationId);
                bool firstSelection = ExploreSource is null && current is not null;
                ExploreSource = current ?? source;
                if (firstSelection) Publish();
                if (current is null || !CanExplore(current))
                {
                    loadedKindsSource = null;
                    Kinds = []; ExploreKind = null; DiscoverBooks.Clear(); MoreDiscovery = false; explorePage = 0;
                    DiscoveryState = NovelDiscoveryState.Unavailable; discoveryNeedsReload = false; return true;
                }
                source = current;
                if (!HasLoadedKindsFor(source)) reloadKinds = true;
                var nextKinds = Kinds;
                if (reloadKinds)
                {
                    var result = await Sources.InvokeAsync(source, "exploreKinds", new { }, cancellation);
                    cancellation.ThrowIfCancellationRequested();
                    if (generation != discoveryGeneration) return false;
                    nextKinds = ReadDiscoveryKinds(result.GetProperty("kinds"));
                }
                var nextKind = reloadKinds || routeReselect ? NovelDiscoveryKinds.Select(nextKinds, requestedKind, Route == HomeRoute) :
                    changedKind ? NovelDiscoveryKinds.Groups(nextKinds, Route == HomeRoute).SelectMany(group => group.Kinds)
                        .FirstOrDefault(candidate => candidate.Url == kind!.Url) : ExploreKind;
                if (nextKind is null)
                {
                    loadedKindsSource = (source.InstallationId, source.Revision);
                    Kinds = nextKinds; ExploreKind = null; DiscoverBooks.Clear(); MoreDiscovery = false; explorePage = 0;
                    DiscoveryState = NovelDiscoveryState.Empty; discoveryNeedsReload = false; return true;
                }

                int nextPage = replacePage ? 1 : explorePage + 1;
                var page = await Sources.InvokeAsync(source, "explore", new { url = nextKind.Url, page = nextPage }, cancellation);
                cancellation.ThrowIfCancellationRequested();
                if (generation != discoveryGeneration) return false;
                var books = NovelBook.ReadList(page.GetProperty("books"), source, "INVALID_DISCOVERY_BOOKS");
                cancellation.ThrowIfCancellationRequested();
                // A same-target refresh commits the first page and its kinds together.
                loadedKindsSource = (source.InstallationId, source.Revision);
                Kinds = nextKinds; ExploreKind = nextKind;
                if (replacePage) DiscoverBooks.Clear();
                var identities = DiscoverBooks.Select(b => b.SourceIdentity).ToHashSet(StringComparer.Ordinal);
                int before = DiscoverBooks.Count;
                foreach (var book in books)
                    if (identities.Add(book.SourceIdentity)) DiscoverBooks.Add(book);
                explorePage = nextPage; MoreDiscovery = DiscoverBooks.Count > before; discoveryNeedsReload = false;
                DiscoveryState = DiscoverBooks.Count == 0 ? NovelDiscoveryState.Empty : NovelDiscoveryState.Ready;
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                cancellation.ThrowIfCancellationRequested();
                if (generation == discoveryGeneration)
                {
                    DiscoveryState = NovelDiscoveryState.Failed;
                    DiscoveryError = error is BridgeException { Code: "WEB_INTERACTION_REQUIRED" }
                        ? "书源需要网页验证" : "发现加载失败";
                    Notify(DiscoveryError == "书源需要网页验证" ? "书源需要网页验证" : "发现加载失败，请重试");
                }
                return false;
            }
            finally
            {
                if (ReferenceEquals(discovery, owner))
                {
                    discovery = null;
                    if (cancellation.IsCancellationRequested && DiscoveryState is NovelDiscoveryState.Loading or NovelDiscoveryState.Cancelling)
                        DiscoveryState = NovelDiscoveryState.Cancelled;
                    Publish();
                }
            }
        }, token);
    }

    private static NovelKind[] ReadDiscoveryKinds(JsonElement values)
    {
        if (values.ValueKind != JsonValueKind.Array) throw new JsonException("INVALID_DISCOVERY_KINDS");
        var result = new List<NovelKind>();
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Object) continue;
            string title = NovelJson.Text(value, "title").Trim();
            string url = NovelJson.Text(value, "url").Trim();
            if (title.Length == 0 && url.Length == 0) continue;
            result.Add(new(title.Length == 0 ? "未命名" : title, url));
        }
        if (values.GetArrayLength() > 0 && result.Count == 0) throw new JsonException("INVALID_DISCOVERY_KINDS");
        return result.ToArray();
    }

    internal Task RetryDiscoveryAsync(CancellationToken token) =>
        LoadDiscoveryAsync(ExploreSource, ExploreKind, retryDiscoveryReset, token);

    public void CancelDiscovery()
    {
        if (discovery is null) return;
        if (DiscoveryState == NovelDiscoveryState.Loading) DiscoveryState = NovelDiscoveryState.Cancelling;
        discovery.Cancel(); Publish();
    }
}
