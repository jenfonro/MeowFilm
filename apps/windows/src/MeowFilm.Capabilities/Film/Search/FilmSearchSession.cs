using System.Text.Json;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Capabilities;

public sealed class FilmSearchSession
{
    internal FilmSearchSession(string query) => Query = query;
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string Query { get; }
    public string MetadataQuery { get; internal set; } = "";
    public JsonElement? MetadataSearchPayload { get; internal set; }
    public long Revision { get; private set; }
    public FilmSearchChannelState SitesState { get; internal set; }
    public FilmSearchChannelState MetadataState { get; internal set; }
    public bool Loading { get; internal set; }
    public bool CanLoadMore => !Loading && !Resuming && !Cancellation.IsCancellationRequested && Cursors.Any(cursor => !cursor.Ended) &&
        SitesState is FilmSearchChannelState.Completed or FilmSearchChannelState.Error;
    public IReadOnlyList<JsonElement> Items { get; internal set; } = [];
    public IReadOnlyList<JsonElement> RawItems { get; internal set; } = [];
    public event EventHandler? Changed;
    internal int Subscribers => Changed?.GetInvocationList().Length ?? 0;
    internal List<Cursor> Cursors { get; } = [];
    internal List<JsonElement> SiteItems { get; } = [];
    internal List<JsonElement> TmdbItems { get; } = [];
    internal Dictionary<string, JsonElement> Details { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, FilmSearchData> MetadataData { get; } = new(StringComparer.Ordinal);
    internal RuntimeConnection? DataOwner
    {
        get
        {
            var responses = Cursors.SelectMany(cursor => cursor.Pages.Values).ToArray();
            if (responses.Length == 0) responses = MetadataData.Values.ToArray();
            var owner = responses.FirstOrDefault()?.Owner;
            return owner is not null && responses.All(value => FilmPlaybackOpen.SameOwner(value.Owner, owner)) ? owner : null;
        }
    }
    internal HashSet<string> Identities { get; } = new(StringComparer.Ordinal);
    internal string[] AggregateOrder { get; set; } = [];
    internal string[] RawOrder { get; set; } = [];
    internal string[] Rules { get; set; } = [];
    internal string CoverSite { get; set; } = "";
    internal Task Work { get; set; } = Task.CompletedTask;
    internal CancellationTokenSource Cancellation { get; set; } = new();
    internal bool Resuming { get; set; }
    internal bool SourcesLoaded { get; set; }
    internal bool MetadataSearchCompleted { get; set; }
    internal bool ReadingSources { get; set; }
    internal void Publish() { Revision++; Changed?.Invoke(this, EventArgs.Empty); }
    internal JsonElement CurrentItem(JsonElement selected)
    {
        if (FilmSearchJson.Text(selected, "sourceKind") == "tmdb")
            return Members(selected).All(HasCurrentGeneration) ? Items.FirstOrDefault(item => FilmSearchJson.Text(item, "sourceKind") == "tmdb" &&
                FilmSearchJson.MetadataIdentity(item) == FilmSearchJson.MetadataIdentity(selected), selected) : selected;
        // A raw selection remains the exact source member, never a substituted TMDB identity.
        if (FilmSearchJson.Text(selected, "displayKey").StartsWith("site:", StringComparison.Ordinal))
            return RawItems.FirstOrDefault(item => SameMember(item, selected), selected);
        return Carrier(selected);
    }
    internal JsonElement Carrier(JsonElement selected)
    {
        if (FilmSearchJson.Text(selected, "sourceKind") == "tmdb") return CurrentItem(selected);
        var members = Members(selected).ToArray();
        if (!members.All(HasCurrentGeneration)) return selected;
        // Follow the algorithm's actual membership, including installation and generation.
        // No title matching or new movie/tv decision is made in this consumer.
        return Items.FirstOrDefault(card => Members(card).Any(member =>
            members.Any(original => SameMember(member, original))), selected);
    }
    private static IEnumerable<JsonElement> Members(JsonElement item) =>
        item.TryGetProperty("aggregateItems", out var members) && members.ValueKind == JsonValueKind.Array
            ? members.EnumerateArray() : FilmSearchJson.Text(item, "sourceKind") == "site" ? [item] : [];
    private static bool SameMember(JsonElement left, JsonElement right) =>
        FilmSearchJson.Text(left, "sourceKind") == "site" && FilmSearchJson.Text(right, "sourceKind") == "site" &&
        FilmSearchJson.SiteIdentity(left) == FilmSearchJson.SiteIdentity(right) &&
        FilmSearchJson.Number(left, "generation") == FilmSearchJson.Number(right, "generation");
    private bool HasCurrentGeneration(JsonElement member) => Cursors.Any(cursor =>
        cursor.Source.Key == FilmSearchJson.Text(member, "siteKey") &&
        cursor.Source.Cat.Generation == FilmSearchJson.Number(member, "generation"));
    internal JsonElement? Detail(JsonElement item)
    {
        if (Details.TryGetValue(FilmSearchJson.MetadataIdentity(item), out var value)) return value;
        if (MetadataSearchPayload is not { } payload || FilmSearchJson.Text(item, "tmdbType").Length == 0) return null;
        foreach (var row in payload.GetProperty("results").EnumerateArray())
            if (FilmSearchJson.Text(row, "media_type") == FilmSearchJson.Text(item, "tmdbType") &&
                FilmSearchJson.Number(row, "id") == FilmSearchJson.Number(item, "tmdbId")) return row;
        return null;
    }

    internal sealed class Cursor(FilmSearchSource source)
    {
        internal FilmSearchSource Source { get; } = source;
        internal int Page { get; set; } = 1;
        internal int ConfirmedPage => Pages.Count == 0 ? 0 : Pages.Keys.Max();
        internal SortedDictionary<int, FilmSearchData> Pages { get; } = [];
        internal bool Ended { get; set; }
        internal bool NeedsRecovery { get; set; } = true;
    }
}
