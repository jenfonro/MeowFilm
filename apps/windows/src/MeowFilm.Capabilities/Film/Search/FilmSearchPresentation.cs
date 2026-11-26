using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;

namespace MeowFilm.Capabilities;

/// <summary>Search presentation keeps the runtime's normalized card entry and lightweight search session reference.</summary>
public sealed class FilmSearchCardItem : IFilmCardDisplay, INotifyPropertyChanged
{
    private FilmSearchCardValue value;
    internal FilmSearchCardItem(FilmSearchSession session, FilmSearchCardValue value)
    {
        this.value = value;
        SearchContext = new(session, value.Key, value.Entry);
    }

    public string Id => value.Id;
    public string Title => value.Title;
    public string Poster => value.Poster;
    public string Score => value.Score;
    public string TextBadge => value.TextBadge;
    public string Subtitle => value.Subtitle;
    public bool HasScore => Score.Length > 0;
    public bool HasTextBadge => TextBadge.Length > 0;
    public bool HasSubtitle => Subtitle.Length > 0;
    public FilmSearchContext SearchContext { get; }
    public event PropertyChangedEventHandler? PropertyChanged;

    public FilmCardItem ToCard() => new(Id, Title, Poster, Score)
    {
        TextBadge = TextBadge,
        Subtitle = Subtitle,
        SearchContext = SearchContext,
        Entry = SearchContext.Entry
    };

    internal void Apply(FilmSearchCardValue next, FilmSearchCardFields fields)
    {
        bool hadScore = HasScore, hadBadge = HasTextBadge, hadSubtitle = HasSubtitle;
        value = next;
        SearchContext.Update(next.Key, next.Entry);
        if ((fields & FilmSearchCardFields.Title) != 0) Changed(nameof(Title));
        if ((fields & FilmSearchCardFields.Poster) != 0) Changed(nameof(Poster));
        if ((fields & FilmSearchCardFields.Score) != 0)
        {
            Changed(nameof(Score));
            if (hadScore != HasScore) Changed(nameof(HasScore));
        }
        if ((fields & FilmSearchCardFields.TextBadge) != 0)
        {
            Changed(nameof(TextBadge));
            if (hadBadge != HasTextBadge) Changed(nameof(HasTextBadge));
        }
        if ((fields & FilmSearchCardFields.Subtitle) != 0)
        {
            Changed(nameof(Subtitle));
            if (hadSubtitle != HasSubtitle) Changed(nameof(HasSubtitle));
        }
    }

    private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
}

[Flags]
internal enum FilmSearchCardFields { None = 0, Title = 1, Poster = 2, Score = 4, TextBadge = 8, Subtitle = 16 }

internal sealed record FilmSearchCardValue(string Key, string Id, string Title, string Poster,
    string Score, string TextBadge, string Subtitle, FilmEntry Entry)
{
    internal FilmSearchCardFields Changes(FilmSearchCardValue previous) =>
        (Title != previous.Title ? FilmSearchCardFields.Title : 0) |
        (Poster != previous.Poster ? FilmSearchCardFields.Poster : 0) |
        (Score != previous.Score ? FilmSearchCardFields.Score : 0) |
        (TextBadge != previous.TextBadge ? FilmSearchCardFields.TextBadge : 0) |
        (Subtitle != previous.Subtitle ? FilmSearchCardFields.Subtitle : 0);
}

internal enum FilmSearchCardEditKind { Insert, Remove, Move }
internal readonly record struct FilmSearchCardEdit(FilmSearchCardEditKind Kind, int Index, int From = -1);
internal readonly record struct FilmSearchCardUpdate(FilmSearchCardValue Value, FilmSearchCardFields Fields);

/// <summary>Immutable, UI-independent inputs and keyed edits, prepared away from the Dispatcher.</summary>
internal sealed class FilmSearchCardProjection
{
    private FilmSearchCardProjection(JsonElement json, IReadOnlyList<JsonElement> items,
        IReadOnlyList<FilmSearchCardValue> values, IReadOnlyList<FilmSearchCardEdit> edits)
    { Json = json; Items = items; Values = values; Edits = edits; }

    internal JsonElement Json { get; }
    internal IReadOnlyList<JsonElement> Items { get; }
    internal IReadOnlyList<FilmSearchCardValue> Values { get; }
    internal IReadOnlyList<FilmSearchCardEdit> Edits { get; }

    internal static FilmSearchCardProjection Read(JsonElement json, FilmSearchCardProjection? previous)
    {
        if (previous is not null && JsonElement.DeepEquals(previous.Json, json)) return previous;
        if (json.ValueKind != JsonValueKind.Array) throw new FilmPlaybackException("INVALID_RESPONSE");
        var items = json.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object &&
            (FilmSearchJson.Text(item, "displayKey").Length > 0 || FilmSearchJson.Text(item, "id").Length > 0)).ToArray();
        if (json.GetArrayLength() > 0 && items.Length == 0) throw new FilmPlaybackException("INVALID_RESPONSE");
        var values = new FilmSearchCardValue[items.Length];
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < items.Length; index++)
        {
            var item = items[index];
            string key = FilmSearchJson.Text(item, "displayKey");
            if (key.Length == 0) key = FilmSearchJson.Text(item, "id");
            if (key.Length == 0 || !keys.Add(key)) throw new FilmPlaybackException("INVALID_RESPONSE");
            var entry = FilmEntry.Read(item);
            string subtitle = entry.SiteLabel.Length > 0 ? entry.SiteLabel : entry.Subtitle;
            values[index] = new(key, key, entry.Title, entry.Poster, entry.Score, entry.TextBadge, subtitle, entry);
        }
        var order = previous?.Values.Select(value => value.Key).ToList() ?? [];
        var edits = new List<FilmSearchCardEdit>();
        for (int index = order.Count - 1; index >= 0; index--)
            if (!keys.Contains(order[index]))
            {
                order.RemoveAt(index);
                edits.Add(new(FilmSearchCardEditKind.Remove, index));
            }
        for (int index = 0; index < values.Length; index++)
        {
            string key = values[index].Key;
            if (index < order.Count && order[index] == key) continue;
            int from = order.IndexOf(key, index);
            if (from >= 0)
            {
                order.RemoveAt(from);
                order.Insert(index, key);
                edits.Add(new(FilmSearchCardEditKind.Move, index, from));
            }
            else
            {
                order.Insert(index, key);
                edits.Add(new(FilmSearchCardEditKind.Insert, index));
            }
        }
        return new(json, Array.AsReadOnly(items), Array.AsReadOnly(values), edits.AsReadOnly());
    }

    internal void Apply(ObservableCollection<FilmSearchCardItem> items,
        IReadOnlyDictionary<string, FilmSearchCardItem> models)
    {
        foreach (var edit in Edits)
            switch (edit.Kind)
            {
                case FilmSearchCardEditKind.Insert: items.Insert(edit.Index, models[Values[edit.Index].Key]); break;
                case FilmSearchCardEditKind.Remove: items.RemoveAt(edit.Index); break;
                case FilmSearchCardEditKind.Move: items.Move(edit.From, edit.Index); break;
            }
    }
}

internal sealed class FilmSearchDisplay
{
    private FilmSearchDisplay() { }
    internal FilmSearchCardProjection Cards { get; private init; } = null!;
    internal FilmSearchCardProjection RawCards { get; private init; } = null!;
    internal IReadOnlyDictionary<string, FilmSearchCardValue> Values { get; private init; } = null!;
    internal IReadOnlyList<FilmSearchCardUpdate> Updates { get; private init; } = [];
    internal IReadOnlyList<string> Removed { get; private init; } = [];

    internal static FilmSearchDisplay Read(JsonElement snapshot, FilmSearchDisplay? previous)
    {
        var cards = FilmSearchCardProjection.Read(snapshot.GetProperty("cards"), previous?.Cards);
        var rawCards = FilmSearchCardProjection.Read(snapshot.GetProperty("rawCards"), previous?.RawCards);
        if (ReferenceEquals(cards, previous?.Cards) && ReferenceEquals(rawCards, previous?.RawCards)) return previous!;
        var values = new Dictionary<string, FilmSearchCardValue>(StringComparer.Ordinal);
        foreach (var value in cards.Values) values.Add(value.Key, value);
        foreach (var value in rawCards.Values) values.TryAdd(value.Key, value);
        var updates = new List<FilmSearchCardUpdate>();
        foreach (var value in values.Values)
        {
            if (previous is null || !previous.Values.TryGetValue(value.Key, out var before))
                updates.Add(new(value, FilmSearchCardFields.None));
            else if (!ReferenceEquals(before, value)) updates.Add(new(value, value.Changes(before)));
        }
        return new()
        {
            Cards = cards, RawCards = rawCards, Values = values, Updates = updates.AsReadOnly(),
            Removed = previous?.Values.Keys.Where(key => !values.ContainsKey(key)).ToArray() ?? []
        };
    }
}

internal sealed class FilmSearchPreparedSnapshot
{
    internal FilmSearchPreparedSnapshot(JsonElement snapshot, FilmSearchDisplay? previous, bool readDisplay = true)
    {
        Snapshot = snapshot;
        (Id, Query) = ReadIdentity(snapshot);
        Revision = snapshot.GetProperty("revision").GetInt64();
        Invalidated = FilmJson.Bool(snapshot, "invalidated");
        if (Invalidated) return;
        SitesState = State(FilmJson.Text(snapshot, "siteState"));
        MetadataState = State(FilmJson.Text(snapshot, "metadataState"));
        Round = FilmJson.Integer(snapshot, "round") ?? 0;
        Loading = snapshot.GetProperty("loading").GetBoolean();
        HasMore = snapshot.GetProperty("hasMore").GetBoolean();
        NoSources = FilmJson.Bool(snapshot, "noSources");
        if (readDisplay) Display = FilmSearchDisplay.Read(snapshot, previous);
    }

    internal JsonElement Snapshot { get; }
    internal string Id { get; }
    internal string Query { get; }
    internal long Revision { get; }
    internal bool Invalidated { get; }
    internal int Round { get; }
    internal bool Loading { get; }
    internal bool HasMore { get; }
    internal bool NoSources { get; }
    internal FilmSearchChannelState SitesState { get; }
    internal FilmSearchChannelState MetadataState { get; }
    internal FilmSearchDisplay? Display { get; }

    internal static (string Id, string Query) ReadIdentity(JsonElement snapshot)
    {
        string id = FilmJson.RequiredText(snapshot, "sessionId");
        if (!Guid.TryParse(id, out _)) throw new FilmPlaybackException("INVALID_RESPONSE");
        return (id, FilmSearchJson.Text(snapshot, "query"));
    }

    private static FilmSearchChannelState State(string state) => state switch
    {
        "idle" => FilmSearchChannelState.Idle, "loading" => FilmSearchChannelState.Loading,
        "completed" => FilmSearchChannelState.Completed, "unavailable" => FilmSearchChannelState.Unavailable,
        "failed" => FilmSearchChannelState.Error, "cancelled" => FilmSearchChannelState.Cancelled,
        _ => FilmSearchChannelState.Unknown
    };
}
