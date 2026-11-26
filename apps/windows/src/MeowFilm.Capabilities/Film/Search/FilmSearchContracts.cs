using System.Text.Json;

namespace MeowFilm.Capabilities;

public enum FilmSearchChannelState { Idle, Loading, Completed, NoSources, Unavailable, Error, Cancelled, Unknown }

public sealed record FilmSearchSource(FilmCatSource Cat, FilmVideoSource Site)
{
    // A tuple encoding keeps installation/site boundaries unambiguous without a URL identity.
    public string Key => JsonSerializer.Serialize(new[] { Cat.InstallationId, Site.Key.SiteKey });
}

public sealed class FilmSearchContext
{
    internal FilmSearchContext(FilmSearchSession session, string cardId, FilmEntry entry)
    {
        Session = session;
        CardId = cardId;
        Entry = entry;
    }

    public FilmSearchSession Session { get; }
    public string Query => Session.Query;
    public string SessionId => Session.Id;
    public string CardId { get; private set; }
    public FilmEntry Entry { get; private set; }

    internal void Update(string cardId, FilmEntry entry)
    {
        if (cardId.Length > 0) CardId = cardId;
        Entry = entry;
    }

    internal object? Reference => Session.Retired || SessionId.Length == 0
        ? null
        : new { sessionId = SessionId, cardId = CardId };
}

internal static class FilmSearchJson
{
    internal static string Text(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(key, out var field) ? DisplayText(field) : "";

    internal static string DisplayText(JsonElement field)
    {
        if (field.ValueKind == JsonValueKind.String) return field.GetString()!;
        if (field.ValueKind != JsonValueKind.Array) return "";
        var units = new char[field.GetArrayLength()];
        int index = 0;
        foreach (var unit in field.EnumerateArray())
        {
            if (unit.ValueKind != JsonValueKind.Number || !unit.TryGetUInt16(out ushort value)) return "";
            units[index++] = (char)value;
        }
        // WinUI text is a projection only; the original JSON remains the transport identity.
        return System.Text.Encoding.Unicode.GetString(System.Text.Encoding.Unicode.GetBytes(units));
    }
}
