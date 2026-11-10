using System.Globalization;
using System.Text.Json;

namespace MeowFilm.Capabilities;

/// <summary>Lossless handoff from the shared search projection, never from display title inference.</summary>
internal static class FilmSearchPlayback
{
    internal static FilmPlaybackRequest Capture(FilmSearchContext context)
    {
        var carrier = context.CarrierItem;
        string title = FilmSearchJson.Text(carrier, "title");
        var members = context.Sources;
        var cursors = context.Session.Cursors.Where(cursor => !context.IsRawSelection ||
            members.Any(member => Matches(cursor.Source, member))).ToArray();
        FilmPlaybackTarget target;
        if (context.IsRawSelection)
        {
            var member = members.Single();
            target = new(context.DisplayKey, "source", title, Source: FilmJson.Encode(new
            {
                key = FilmSearchJson.Text(member, "siteKey"), detailId = FilmJson.Member(member, "detailId")
            }));
        }
        else if (FilmSearchJson.Text(carrier, "sourceKind") == "tmdb")
        {
            string kind = FilmSearchJson.Text(carrier, "tmdbType");
            string id = FilmSearchJson.Number(carrier, "tmdbId").ToString(CultureInfo.InvariantCulture);
            target = new($"tmdb:{kind}:{id}", kind, title, "tmdb", id);
        }
        else target = new(context.DisplayKey, "group", title,
            Members: FilmJson.Encode(members.Select(member => new
            {
                sourceKey = FilmSearchJson.Text(member, "siteKey"), detailId = FilmJson.Member(member, "detailId")
            }).DistinctBy(member => (member.sourceKey, member.detailId.GetRawText())).ToArray()));
        target.Validate();
        var results = cursors.Select(cursor => new
        {
            sourceKey = cursor.Source.Key,
            items = cursor.Pages.Values.SelectMany(page => FilmJson.Array(page.Value, "items"))
                .Where(row => !context.IsRawSelection || SameDetail(row, target.Source!.Value.GetProperty("detailId"))).ToArray()
        }).Where(row => row.items.Length > 0).ToArray();
        var entries = cursors.SelectMany(cursor => cursor.Pages.Values)
            .Concat(context.IsRawSelection ? [] : context.Session.MetadataData.Values)
            .Where(response => response.CacheEntry is not null).Select(response => response.CacheEntry!.Value).ToArray();
        return new(target, context.Session.SourcesLoaded ? cursors.Select(cursor => cursor.Source.Wire).ToArray() : null,
            context.Query, FilmJson.Encode(results),
            FilmJson.Encode(cursors.Select(cursor => new { sourceKey = cursor.Source.Key,
                page = cursor.ConfirmedPage, completed = cursor.Ended }).ToArray()),
            context.Metadata, FilmJson.Encode(entries))
        { DataOwner = context.Session.DataOwner };
    }

    internal static bool Matches(FilmSearchSource source, JsonElement member) =>
        source.Key == FilmSearchJson.Text(member, "siteKey") &&
        source.Cat.Generation == FilmSearchJson.Number(member, "generation");

    private static bool SameDetail(JsonElement row, JsonElement identity)
    {
        var detail = FilmJson.Member(row, "vod_id");
        if (detail.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) detail = FilmJson.Member(row, "id");
        return FilmJson.IsIdentity(detail) && JsonElement.DeepEquals(detail, identity);
    }
}
