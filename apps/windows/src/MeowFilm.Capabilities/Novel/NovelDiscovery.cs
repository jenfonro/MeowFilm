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
    private bool discoveryInvalidated = true, retryDiscoveryReset;
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
        if (current is not null && CanExplore(current) && current.Revision == selected.Revision)
        { ExploreSource = current; return; }
        discovery?.Cancel();
        // Keep the selected identity, including a removed source, until the user chooses another.
        ExploreSource = current ?? selected;
        Kinds = []; ExploreKind = null; DiscoverBooks.Clear(); MoreDiscovery = false; explorePage = 0;
        discoveryNeedsReload = true; DiscoveryError = "";
        DiscoveryState = current is not null && CanExplore(current) ? NovelDiscoveryState.Idle : NovelDiscoveryState.Unavailable;
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
            bool reloadKinds = reset || changedSource || discoveryNeedsReload;
            bool replacePage = reloadKinds || changedKind;
            retryDiscoveryReset = replacePage;
            var requestedKind = kind ?? (changedSource ? null : ExploreKind);
            try
            {
                if (changedSource)
                { ExploreSource = source; ExploreKind = null; Kinds = []; }
                if (changedSource || changedKind)
                {
                    DiscoverBooks.Clear(); MoreDiscovery = false; explorePage = 0; discoveryNeedsReload = true;
                    if (!changedSource && kind is not null) ExploreKind = kind;
                }
                DiscoveryState = NovelDiscoveryState.Loading; DiscoveryError = ""; Publish();
                await Sources.EnsureReadyAsync(cancellation);
                cancellation.ThrowIfCancellationRequested();
                if (generation != discoveryGeneration) return false;
                source ??= Sources.Sources.FirstOrDefault(CanExplore);
                var current = Sources.Sources.FirstOrDefault(s => s.InstallationId == source?.InstallationId);
                bool firstSelection = ExploreSource is null && current is not null;
                ExploreSource = current ?? source;
                if (firstSelection) Publish();
                if (current is null || !CanExplore(current))
                {
                    Kinds = []; ExploreKind = null; DiscoverBooks.Clear(); MoreDiscovery = false; explorePage = 0;
                    DiscoveryState = NovelDiscoveryState.Unavailable; discoveryNeedsReload = false; return true;
                }
                source = current;
                var nextKinds = Kinds;
                if (reloadKinds)
                {
                    var result = await Sources.InvokeAsync(source, "exploreKinds", new { }, cancellation);
                    cancellation.ThrowIfCancellationRequested();
                    if (generation != discoveryGeneration) return false;
                    nextKinds = ReadDiscoveryKinds(result.GetProperty("kinds"));
                }
                var nextKind = reloadKinds ? NovelDiscoveryKinds.Select(nextKinds, requestedKind, Route == HomeRoute) :
                    changedKind ? NovelDiscoveryKinds.Groups(nextKinds, Route == HomeRoute).SelectMany(group => group.Kinds)
                        .FirstOrDefault(candidate => candidate.Url == kind!.Url) : ExploreKind;
                if (nextKind is null)
                {
                    Kinds = nextKinds; ExploreKind = null; DiscoverBooks.Clear(); MoreDiscovery = false; explorePage = 0;
                    DiscoveryState = NovelDiscoveryState.Empty; discoveryNeedsReload = false; return true;
                }

                int nextPage = replacePage ? 1 : explorePage + 1;
                var page = await Sources.InvokeAsync(source, "explore", new { url = nextKind.Url, page = nextPage }, cancellation);
                cancellation.ThrowIfCancellationRequested();
                if (generation != discoveryGeneration) return false;
                var books = ReadDiscoveryBooks(page.GetProperty("books"), source);
                cancellation.ThrowIfCancellationRequested();
                // A same-target refresh commits the first page and its kinds together.
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
                    Notice = "发现加载失败，请重试";
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
        var result = new List<NovelKind>();
        foreach (var value in values.EnumerateArray())
            if (value.ValueKind == JsonValueKind.Object &&
                value.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(title.GetString()) &&
                value.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
                result.Add(new(title.GetString()!, url.GetString()!));
        if (values.GetArrayLength() > 0 && result.Count == 0) throw new JsonException("INVALID_DISCOVERY_KINDS");
        return result.ToArray();
    }
    private static NovelBook[] ReadDiscoveryBooks(JsonElement values, NovelSource source)
    {
        var result = new List<NovelBook>();
        foreach (var value in values.EnumerateArray())
            if (value.ValueKind == JsonValueKind.Object &&
                !string.IsNullOrWhiteSpace(NovelJson.Text(value, "bookUrl")) &&
                !string.IsNullOrWhiteSpace(NovelJson.Text(value, "name")))
                result.Add(NovelBook.From(value, source));
        if (values.GetArrayLength() > 0 && result.Count == 0) throw new JsonException("INVALID_DISCOVERY_BOOKS");
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
