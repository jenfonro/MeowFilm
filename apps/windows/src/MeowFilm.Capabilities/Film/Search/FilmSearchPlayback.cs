using System.Text.Json;
using MeowFilm.Core.Runtime;

namespace MeowFilm.Capabilities;

/// <summary>Decodes the Node-owned handoff without deriving identities, members, cursors or cache seeds.</summary>
internal static class FilmSearchPlayback
{
    internal static FilmPlaybackRequest Read(JsonElement handoff, RuntimeConnection owner)
    {
        var reference = handoff.GetProperty("searchReference");
        if (!Guid.TryParse(FilmJson.RequiredText(reference, "sessionId"), out _) ||
            FilmJson.RequiredText(reference, "selectionId").Length == 0)
            throw new FilmPlaybackException("INVALID_RESPONSE");
        var target = FilmJson.Target(handoff.GetProperty("target"));
        target.Validate();
        JsonElement? Optional(string name) => FilmJson.Member(handoff, name) is
            { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } value ? value.Clone() : null;
        return new(target, FilmJson.Array(handoff, "sources"), FilmJson.RequiredText(handoff, "query"),
            Optional("results"), Optional("search"), Optional("metadata"), Optional("cacheEntries"),
            Optional("resultHints"), reference.Clone(), EntrySurface: "search") { DataOwner = owner };
    }
}
