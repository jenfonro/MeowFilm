using System.Text;
using System.Text.Json;
using MeowFilm.Capabilities;
using MeowFilm.Platform.Storage;
using static MeowFilm.Windows.Tests.FilmPlaybackFixture;

namespace MeowFilm.Windows.Tests;

public static partial class FilmLibraryTests
{
    private static async Task TypedPersistence()
    {
        await using var store = new FilmMemoryStore();
        var target = new FilmPlaybackTarget("source:typed", "source", "原始", Source: FilmJson.Encode(new
            { key = "e\u0301-\u00e9", detailId = 0, lineId = "0" }));
        var item = new FilmCardItem(target.Id, target.Title, "", "") { PlaybackContext = new(target) };
        await using (var library = new FilmLibrary(store))
        {
            await library.SetFavoriteAsync(item, true);
            await library.SetFavoriteAsync(Card(number: 200), true);
            await library.SetFavoriteAsync(item with { Title = "明确修改标题" }, true);
        }
        await using var reopened = new FilmLibrary(store);
        var saved = (await reopened.ReadAsync()).Single(row => row.Id == target.Id);
        var source = saved.PlaybackContext!.Target.Source!.Value;
        Check(saved.Title == "明确修改标题" && source.GetProperty("key").GetString() == "e\u0301-\u00e9" &&
            source.GetProperty("detailId").GetInt32() == 0 && source.GetProperty("lineId").GetString() == "0",
            "typed relations preserve exact text and distinguish numeric and textual identity");
        Check(await store.ReadAsync(db => db.Scalar<long>("SELECT count(*) FROM windows_film_library_favorites WHERE scope_key='device'")) == 2,
            "each favorite occupies an independent row");
    }

    private static async Task CorruptRow()
    {
        await using var store = new FilmMemoryStore();
        await using var library = new FilmLibrary(store);
        await library.SetFavoriteAsync(Card(), true);
        await store.Inner.WriteAsync(db => db.Execute("UPDATE windows_film_library_favorites_playbackContext_target SET kind='invalid'"));
        await Error("CORRUPT_STORAGE", () => library.ReadAsync());
        await library.SetFavoriteAsync(Card(number: 200), true);
        Check(await store.ReadAsync(db => db.Scalar<string>("SELECT kind FROM windows_film_library_favorites_playbackContext_target WHERE favorites_key='tmdb:tv:100'")) == "invalid" &&
            await store.ReadAsync(db => db.Scalar<long>("SELECT count(*) FROM windows_film_library_favorites")) == 2,
            "an unrelated incremental write preserves the damaged row");
        await using var reopened = new FilmLibrary(store);
        await Error("CORRUPT_STORAGE", () => reopened.ReadAsync());
        await store.Inner.WriteAsync(db => db.Execute("DELETE FROM windows_film_library_favorites_playbackContext_target WHERE favorites_key='tmdb:tv:100'"));
        await library.SetFavoriteAsync(Card(), true);
        Check((await reopened.ReadAsync()).Count == 2, "a legal complete replacement does not decode the discarded payload");
        var revision = await store.RevisionAsync(FilmLibrary.DocumentNamespace);
        await library.SetFavoriteAsync(Card(), true);
        Check(await store.RevisionAsync(FilmLibrary.DocumentNamespace) == revision,
            "an identical complete favorite does not advance its owner revision");
        await store.Inner.WriteAsync(db => db.Execute("UPDATE windows_film_library_favorites_playbackContext_target SET year=2000,shape_mask=shape_mask | 32,null_mask=null_mask & ~32 WHERE favorites_key='tmdb:tv:100'"));
        await library.SetFavoriteAsync(Card(), true);
        Check((await reopened.ReadAsync()).Single(row => row.Id == Card().Id).PlaybackContext!.Target.Year is null,
            "a complete replacement clears the old optional year when the new target has no year");
        await store.Inner.WriteAsync(db => db.Execute("DELETE FROM windows_film_library_favorites_playbackContext_target WHERE favorites_key='tmdb:tv:100'"));
        await library.SetFavoriteAsync(Card(), false);
        Check((await reopened.ReadAsync()).Single().Id == Card(number: 200).Id,
            "deletion consumes the exact selected key without decoding its old business target");
    }

    private static async Task IdentityConstraints()
    {
        await using var store = new FilmMemoryStore();
        await using var library = new FilmLibrary(store);
        await library.SetFavoriteAsync(Card(), true);
        byte[] original = store.Data!;
        foreach (string sql in new[]
        {
            "INSERT INTO windows_film_library_favorites SELECT * FROM windows_film_library_favorites",
            "UPDATE windows_film_library_favorites SET scope_key='missing-parent'",
            "UPDATE windows_film_library_favorites SET ordinal=-1",
            "UPDATE windows_film_library_favorites SET title=x'00'"
        })
        {
            try
            {
                await store.Inner.WriteAsync(db =>
                {
                    db.Execute("UPDATE windows_film_library_favorites SET title='must-roll-back'");
                    return db.Execute(sql);
                });
                throw new InvalidOperationException("Expected a relational constraint failure");
            }
            catch (DocumentStoreException error) when (error.Error == DocumentStoreError.Conflict) { }
            Check(store.Data!.SequenceEqual(original), "constraint failure rolls back every preceding change");
        }
    }

    private static async Task ConcurrentWriters()
    {
        await using var first = new FilmMemoryStore();
        await using var second = new NativeDatabase(first.ProductRoot!);
        await using var a = new FilmLibrary(first);
        await using var b = new FilmLibrary(second);
        await a.SetFavoriteAsync(Card(), true);
        await b.ReadAsync();
        int publications = 0; a.Changed += (_, _) => publications++;
        await Task.WhenAll(a.SetFavoriteAsync(Card(number: 200), true), b.SetFavoriteAsync(Card(number: 300), true));
        Check((await a.ReadAsync()).Count == 3 && (await b.ReadAsync()).Count == 3 && publications == 1,
            "independent owners serialize incremental transactions without replacing another favorite");
        await a.SetFavoriteAsync(Card(number: 200), false);
        Check((await b.ReadAsync()).Select(row => row.PlaybackContext!.Target.Id).ToHashSet().SetEquals([Card().Id, Card(number: 300).Id]),
            "removal never overwrites another writer's distinct row");
        Check((await first.RevisionAsync(FilmLibrary.DocumentNamespace)).Revision == 4,
            "every committed favorite mutation advances the owner revision once");
    }

    private static async Task GroupPrivacy()
    {
        var store = new FilmMemoryStore();
        await using var library = new FilmLibrary(store);
        var target = new FilmPlaybackTarget("group:private", "group", "聚合",
            Members: JsonSerializer.Deserialize<JsonElement>("""
                [{"sourceKey":"source:raw","detailId":0,"credentials":"private-member-secret","playId":"private-play-reference"}]
                """));
        var context = new FilmSearchContext(new(null!, "private-search-query"),
            FilmJson.Encode(new { displayKey = "group:private", credential = "private-search-secret" }));
        await library.SetFavoriteAsync(new(target.Id, target.Title, "", "") { PlaybackContext = new(target), SearchContext = context }, true);
        var members = (await library.ReadAsync()).Single().PlaybackContext!.Target.Members!.Value;
        var metadata = Card(number: 200);
        await library.SetFavoriteAsync(metadata with { PlaybackContext = new(metadata.PlaybackContext!.Target with
        {
            Source = FilmJson.Encode(new { credential = "private-unused-source" }),
            Members = FilmJson.Encode(new { credential = "private-unused-members" })
        }) }, true);
        Check(members[0].GetProperty("detailId").ValueKind == JsonValueKind.Number &&
            members[0].EnumerateObject().Count() == 2 &&
            !Encoding.UTF8.GetString(store.Data!).Contains("private-", StringComparison.Ordinal),
            "new favorites project only stable member identities and never serialize search or transient credentials");
    }
}
