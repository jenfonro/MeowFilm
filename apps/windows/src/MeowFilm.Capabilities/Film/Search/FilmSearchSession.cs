using System.Collections.ObjectModel;
using System.Text.Json;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Capabilities;

/// <summary>One Node search's display snapshot and borrowed runtime epoch.</summary>
public sealed class FilmSearchSession
{
    internal FilmSearchSession(FilmSearchService service, string query) { Service = service; Query = query; }
    public string Id { get; private set; } = "";
    public string Query { get; }
    public long Revision { get; private set; } = -1;
    public int Round { get; private set; }
    public string MetadataQuery { get; private set; } = "";
    public JsonElement? MetadataSearchPayload { get; private set; }
    public FilmSearchChannelState SitesState { get; private set; } = FilmSearchChannelState.Loading;
    public FilmSearchChannelState MetadataState { get; private set; } = FilmSearchChannelState.Loading;
    public bool Loading { get; private set; } = true;
    public bool HasMore { get; private set; }
    public bool CanLoadMore => !Loading && !Stopped && !Commanding && Error.Length == 0 && HasMore;
    public string Error { get; private set; } = "";
    public IReadOnlyList<JsonElement> Items { get; private set; } = [];
    public IReadOnlyList<JsonElement> RawItems { get; private set; } = [];
    public JsonElement Snapshot { get; private set; }
    public event EventHandler? Changed;
    internal event EventHandler<long>? ReferenceChanged;
    internal long ReferenceRevision { get; private set; } = -1;
    internal FilmSearchService Service { get; }
    internal FilmSearchDisplay? Display { get; private set; }
    internal ObservableCollection<FilmSearchCardItem> CardItems { get; } = [];
    internal ObservableCollection<FilmSearchCardItem> RawCardItems { get; } = [];
    private readonly Dictionary<string, FilmSearchCardItem> cardModels = new(StringComparer.Ordinal);
    internal RuntimeConnection? Connection { get; set; }
    internal FilmPlaybackClient? Client { get; set; }
    internal IReadOnlyList<JsonElement> SourceSnapshot { get; set; } = [];
    internal CancellationTokenSource? Observation { get; set; }
    internal CancellationTokenSource Execution { get; private set; } = new();
    internal Task Observing { get; set; } = Task.CompletedTask;
    internal Task Work { get; set; } = Task.CompletedTask;
    internal SemaphoreSlim Control { get; } = new(1, 1);
    internal bool Stopped { get; set; }
    internal bool Commanding { get; set; }
    internal bool Resuming { get; set; }
    internal bool Rebinding { get; private set; }
    internal long Publication { get; set; }
    internal bool Retired { get; set; }
    internal Task? Releasing { get; set; }
    internal Task? Stopping { get; set; }
    internal HashSet<Task> Handoffs { get; } = [];

    internal void Bind(FilmSearchPreparedSnapshot snapshot) => Bind(snapshot.Id, snapshot.Query);
    internal void Bind(string id, string query)
    {
        if (Id.Length > 0 && Id != id || query != Query)
            throw new FilmPlaybackException("INVALID_RESPONSE");
        Id = id;
    }
    internal bool Apply(FilmSearchPreparedSnapshot snapshot, bool refresh = false)
    {
        Bind(snapshot);
        long revision = snapshot.Revision;
        if (revision < Revision || revision == Revision && !refresh) return false;
        bool unavailable = snapshot.SitesState is FilmSearchChannelState.Error or FilmSearchChannelState.Unavailable &&
            snapshot.MetadataState is FilmSearchChannelState.Error or FilmSearchChannelState.Unavailable;
        var next = Rebinding && (snapshot.Loading || unavailable) && Display is not null ? Display : snapshot.Display!;
        if (!ReferenceEquals(Display, next))
        {
            foreach (var update in next.Updates)
            {
                if (cardModels.TryGetValue(update.Value.Key, out var model)) model.Apply(update.Value, update.Fields);
                else cardModels.Add(update.Value.Key, new(this, update.Value));
            }
            if (!ReferenceEquals(Display?.Cards, next.Cards)) next.Cards.Apply(CardItems, cardModels);
            if (!ReferenceEquals(Display?.RawCards, next.RawCards)) next.RawCards.Apply(RawCardItems, cardModels);
            foreach (string key in next.Removed) cardModels.Remove(key);
            Display = next;
        }
        Revision = revision; Round = snapshot.Round;
        if (!snapshot.Loading && !unavailable) Rebinding = false;
        SitesState = snapshot.NoSources && snapshot.SitesState == FilmSearchChannelState.Completed
            ? FilmSearchChannelState.NoSources : snapshot.SitesState;
        MetadataState = snapshot.MetadataState; Loading = snapshot.Loading; HasMore = snapshot.HasMore;
        MetadataQuery = snapshot.MetadataQuery; MetadataSearchPayload = snapshot.MetadataSearch;
        Items = next.Cards.Items; RawItems = next.RawCards.Items; Snapshot = snapshot.Snapshot; Error = "";
        SourceSnapshot = snapshot.Snapshot.GetProperty("sources").EnumerateArray().Select(source => source.Clone()).ToArray();
        Notify();
        return true;
    }
    internal void ApplyStopped(FilmSearchPreparedSnapshot snapshot)
    {
        Bind(snapshot);
        long revision = snapshot.Revision;
        if (revision < Revision) return;
        Revision = revision;
        Loading = snapshot.Loading; HasMore = snapshot.HasMore;
        SitesState = snapshot.SitesState; MetadataState = snapshot.MetadataState;
        Notify();
    }
    internal void PublishReference(FilmSearchPreparedSnapshot snapshot)
    {
        Bind(snapshot);
        long revision = snapshot.Revision;
        if (revision <= ReferenceRevision) return;
        ReferenceRevision = revision;
        ReferenceChanged?.Invoke(this, revision);
    }
    internal void CancelUnopened()
    {
        Loading = false; HasMore = false; Commanding = false;
        if (SitesState == FilmSearchChannelState.Loading) SitesState = FilmSearchChannelState.Cancelled;
        if (MetadataState == FilmSearchChannelState.Loading) MetadataState = FilmSearchChannelState.Cancelled;
        Notify();
    }
    internal void ResetBinding(bool reopen = false)
    {
        Id = ""; Revision = ReferenceRevision = -1;
        Connection = null; Client = null;
        Observation?.Dispose(); Observation = null; Observing = Task.CompletedTask;
        Execution.Dispose(); Execution = new();
        Rebinding = true;
        if (reopen)
        {
            Error = "";
            Stopped = false;
            Loading = true;
            HasMore = false;
            SitesState = FilmSearchChannelState.Loading;
            MetadataState = FilmSearchChannelState.Loading;
            Notify();
        }
    }
    internal void ResetForRetry()
    {
        Error = "";
        Stopped = false;
        Loading = true;
        HasMore = false;
        SitesState = FilmSearchChannelState.Loading;
        MetadataState = FilmSearchChannelState.Loading;
        Execution.Dispose();
        Execution = new();
        Notify();
    }
    internal void Fail(Exception error)
    {
        Error = error is FilmPlaybackException film ? film.Code : error is BridgeException bridge ? bridge.Code : error.Message;
        Loading = false; Stopped = true;
        SitesState = FilmSearchChannelState.Error;
        if (MetadataState == FilmSearchChannelState.Loading) MetadataState = FilmSearchChannelState.Error;
        Notify();
    }
    internal void Notify() => Changed?.Invoke(this, EventArgs.Empty);
}
