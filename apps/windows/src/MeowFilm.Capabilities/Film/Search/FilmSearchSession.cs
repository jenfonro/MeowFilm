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
    internal RuntimeConnection? Connection { get; set; }
    internal FilmPlaybackClient? Client { get; set; }
    internal IReadOnlyList<JsonElement> SourceSnapshot { get; set; } = [];
    internal CancellationTokenSource? Observation { get; set; }
    internal Task Observing { get; set; } = Task.CompletedTask;
    internal Task Work { get; set; } = Task.CompletedTask;
    internal SemaphoreSlim Control { get; } = new(1, 1);
    internal bool Stopped { get; set; }
    internal bool Commanding { get; set; }
    internal bool Resuming { get; set; }
    internal long Publication { get; set; }
    internal bool Retired { get; set; }
    internal int Subscribers => (Changed?.GetInvocationList().Length ?? 0) +
        (ReferenceChanged?.GetInvocationList().Length ?? 0);
    internal int Retainers { get; set; }
    internal Task? Releasing { get; set; }
    internal Task? Stopping { get; set; }
    internal HashSet<Task> Handoffs { get; } = [];

    internal void Bind(JsonElement snapshot)
    {
        string id = FilmJson.RequiredText(snapshot, "sessionId");
        if (!Guid.TryParse(id, out _) || Id.Length > 0 && Id != id ||
            FilmJson.RequiredText(snapshot, "query") != Query) throw new FilmPlaybackException("INVALID_RESPONSE");
        Id = id;
    }
    internal bool Apply(JsonElement snapshot, bool refresh = false)
    {
        Bind(snapshot);
        long revision = snapshot.GetProperty("revision").GetInt64();
        if (revision < Revision || revision == Revision && !refresh) return false;
        var siteState = State(FilmJson.RequiredText(snapshot, "siteState"));
        var metadataState = State(FilmJson.RequiredText(snapshot, "metadataState"));
        var cards = ReadCards(snapshot, "cards");
        var rawCards = ReadCards(snapshot, "rawCards");
        int round = snapshot.GetProperty("round").GetInt32();
        bool loading = snapshot.GetProperty("loading").GetBoolean();
        bool hasMore = snapshot.GetProperty("hasMore").GetBoolean();
        bool noSources = snapshot.GetProperty("noSources").GetBoolean();
        Revision = revision; Round = round;
        SitesState = noSources && siteState == FilmSearchChannelState.Completed ? FilmSearchChannelState.NoSources : siteState;
        MetadataState = metadataState; Loading = loading; HasMore = hasMore;
        MetadataQuery = FilmJson.Text(snapshot, "metadataQuery");
        MetadataSearchPayload = FilmJson.Member(snapshot, "metadataSearch") is { ValueKind: JsonValueKind.Object } search
            ? search.Clone() : null;
        Items = cards; RawItems = rawCards; Snapshot = snapshot.Clone(); Error = "";
        Notify();
        return true;
    }
    internal void ApplyStopped(JsonElement snapshot)
    {
        Bind(snapshot);
        long revision = snapshot.GetProperty("revision").GetInt64();
        if (revision < Revision) return;
        Revision = revision;
        Loading = snapshot.GetProperty("loading").GetBoolean();
        HasMore = snapshot.GetProperty("hasMore").GetBoolean();
        SitesState = State(FilmJson.RequiredText(snapshot, "siteState"));
        MetadataState = State(FilmJson.RequiredText(snapshot, "metadataState"));
        Notify();
    }
    internal void PublishReference(JsonElement snapshot)
    {
        Bind(snapshot);
        long revision = snapshot.GetProperty("revision").GetInt64();
        if (revision <= ReferenceRevision) return;
        ReferenceRevision = revision;
        ReferenceChanged?.Invoke(this, revision);
    }
    internal void CancelUnopened()
    {
        Loading = false; HasMore = false;
        if (SitesState == FilmSearchChannelState.Loading) SitesState = FilmSearchChannelState.Cancelled;
        if (MetadataState == FilmSearchChannelState.Loading) MetadataState = FilmSearchChannelState.Cancelled;
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
    private static FilmSearchChannelState State(string state) => state switch
    {
        "idle" => FilmSearchChannelState.Idle, "loading" => FilmSearchChannelState.Loading,
        "completed" => FilmSearchChannelState.Completed, "unavailable" => FilmSearchChannelState.Unavailable,
        "failed" => FilmSearchChannelState.Error, "cancelled" => FilmSearchChannelState.Cancelled,
        _ => throw new FilmPlaybackException("INVALID_RESPONSE")
    };
    private static IReadOnlyList<JsonElement> ReadCards(JsonElement snapshot, string field) =>
        Array.AsReadOnly(snapshot.GetProperty(field).EnumerateArray().Select(item => item.Clone()).ToArray());
}
