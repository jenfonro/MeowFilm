using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Windows.Tests;

public static class MusicSongIdentityTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("Song repository compares every new source to the fixed representative duration", FixedBaseline);
        yield return new("Song repository prefers registered sources and keeps ambiguous new sources independent", ExactSource);
        yield return new("Song repository corrections retain UUID and update searchable metadata", Updates);
        yield return new("Song repository replaces optional structured artists without changing queue references", ArtistMetadataUpdates);
        yield return new("Song repository observations of the same source never split its permanent identity", RegisteredSets);
        yield return new("Song repository keeps local files independent and fixes the first valid duration", LocalAndUnknown);
        yield return new("Song repository partial observations preserve known metadata and the bound primary", PartialObservation);
        yield return new("Song repository returned projections are detached from inputs and other snapshots", Detached);
        yield return new("Song repository retains many sources and current-format UUIDs after cold reload", Variants);
        yield return new("Song recording operations honor cancellation including empty local sets", Cancellation);
        yield return new("Song repository normalizes complete title and artist without stripping versions", Normalization);
        yield return new("Song repository structured artists ignore order and duplicates without guessing text", StructuredArtists);
        yield return new("Song repository does not discard incomplete structured artist items to match known artists", IncompleteStructuredArtists);
        yield return new("Song repository excludes exact unknown placeholders from cross-source matching", UnknownMetadata);
        yield return new("Song repository completing unknown metadata retains each registered source UUID", CompletedMetadata);
        yield return new("Song repository UUID belongs to its database rather than metadata or source keys", IndependentDatabases);
    }
    private static void Check(bool condition, string message = "Assertion failed")
    { if (!condition) throw new InvalidOperationException(message); }
    private static JsonObject Song(string platform, string id, double duration = 180, string title = "Song", string artist = "Artist") =>
        MusicSongFixture.Online(platform, id, title, artist, duration: duration);
    private static JsonObject Structured(string platform, string id, params string[] names) =>
        MusicSongFixture.Online(platform, id, "Duet", string.Join(" / ", names), duration: 180,
            native: new JsonObject { ["artistNames"] = LJ.Arr(names) });
    private static async Task<JsonObject> Adopt(MusicLibraryOwner owner, JsonObject song, bool authoritative = true)
    {
        var result = await owner.AdoptPlatformResultAsync(JsonSerializer.SerializeToElement(song), default, authoritative,
            shape: MusicLibraryOwner.PlatformSongShape.Song);
        var adopted = JsonNode.Parse(result.GetRawText())!.AsObject();
        Check(Guid.TryParseExact(LJ.S(adopted["songId"]), "D", out _));
        _ = SongRow.Read(result);
        return adopted;
    }
    private static string Id(JsonObject song) => LJ.S(song["songId"]);
    private static async Task FixedBaseline()
    {
        foreach (var pair in new[] { (240d, 245d, true), (240d, 245.1d, false), (239d, 240d, true), (181d, 239d, false) })
        {
            await using var store = new MusicOfficialAudioTests.MemoryStore();
            await using var owner = new MusicLibraryOwner(store);
            var first = await Adopt(owner, Song("wy", "base", pair.Item1));
            var next = await Adopt(owner, Song("tx", "next", pair.Item2));
            Check((Id(first) == Id(next)) == pair.Item3);
        }
        await using var retainedStore = new MusicOfficialAudioTests.MemoryStore();
        await using var retained = new MusicLibraryOwner(retainedStore);
        var baseline = await Adopt(retained, Song("wy", "base", 240));
        Check(Id(await Adopt(retained, Song("tx", "edge", 245))) == Id(baseline));
        Check(Id(await Adopt(retained, Song("kg", "chain", 250))) != Id(baseline), "later sources must not extend the fixed baseline");
        Check(Id(await Adopt(retained, Song("wy", "base", 300))) == Id(baseline));
        Check(Id(await Adopt(retained, Song("kw", "original-range", 241))) == Id(baseline), "correction must not move the baseline");
    }
    private static async Task ExactSource()
    {
        await using var store = new MusicOfficialAudioTests.MemoryStore();
        await using var owner = new MusicLibraryOwner(store);
        var first = await Adopt(owner, Song("wy", "short", 100));
        var second = await Adopt(owner, Song("tx", "long", 106));
        var ambiguous = await Adopt(owner, Song("kw", "middle", 103));
        Check(Id(first) != Id(second) && Id(ambiguous) != Id(first) && Id(ambiguous) != Id(second));
        Check(Id(await Adopt(owner, Song("tx", "long", 103))) == Id(second));
        Check(Id(await Adopt(owner, Song("kw", "middle", 500, "Corrected"))) == Id(ambiguous));
        var observation = first.DeepClone().AsObject();
        observation["sources"]!.AsArray().Add(second["sources"]![0]!.DeepClone());
        Check(Id(await Adopt(owner, observation)) == Id(first));
        Check(Id(await Adopt(owner, Song("tx", "long", 103))) == Id(second), "an observation cannot rebind another record's source");
    }
    private static async Task Updates()
    {
        await using var store = new MusicOfficialAudioTests.MemoryStore();
        await using var owner = new MusicLibraryOwner(store);
        var original = await Adopt(owner, Song("wy", "same", title: "Before"));
        var updated = await Adopt(owner, Song("wy", "same", 181, "After"));
        Check(Id(original) == Id(updated) && LJ.S(updated["title"]) == "After");
        Check(MusicSongFixture.Info(updated)["duration"]!.GetValue<double>() == 181);
        var current = await Adopt(owner, Song("tx", "current", 181, "After"));
        Check(Id(current) == Id(original) && current["sources"]!.AsArray().Count == 2);
        Check(MusicSongFixture.Info(current)["source"]!.GetValue<string>() == "wy", "a discovered source cannot replace the selected primary");
        Check(Id(await Adopt(owner, Song("kw", "old", title: "Before"))) != Id(original));
    }
    private static async Task ArtistMetadataUpdates()
    {
        await using var store = new MusicOfficialAudioTests.MemoryStore();
        await using var owner = new MusicLibraryOwner(store);
        var original = await Adopt(owner, Song("wy", "artists"));
        Check((await owner.ExecuteAsync("music.queue.replace", new { songs = new[] { original, original }, index = 1 }))
            .GetProperty("ok").GetBoolean());
        var before = await owner.ExecuteAsync("music.queue.get");
        Check(before.GetProperty("ok").GetBoolean());
        var text = await Adopt(owner, Song("wy", "artists", 181, "Corrected", "Complete artist"));
        Check(Id(text) == Id(original) && !text.ContainsKey("artistNames") &&
            LJ.S(text["title"]) == "Corrected" && LJ.S(text["artist"]) == "Complete artist");
        var structured = await Adopt(owner, Structured("wy", "artists", "B", "A"));
        Check(Id(structured) == Id(original) && structured["artistNames"]!.ToJsonString() == "[\"B\",\"A\"]");
        var withoutList = await Adopt(owner, Song("wy", "artists", 250, "Duet", "B / A"));
        Check(Id(withoutList) == Id(original) && !withoutList.ContainsKey("artistNames") &&
            LJ.N(withoutList["baselineDuration"]) == 180 && LJ.N(withoutList["duration"]) == 250);
        Check(structured["artistNames"]!.AsArray().Count == 2, "an update must not mutate an earlier projection");
        var after = await owner.ExecuteAsync("music.queue.get");
        Check(after.GetProperty("ok").GetBoolean() &&
            after.GetProperty("version").GetInt64() == before.GetProperty("version").GetInt64() &&
            after.GetProperty("current").GetProperty("id").GetString() == before.GetProperty("current").GetProperty("id").GetString() &&
            after.GetProperty("entries").EnumerateArray().Select(entry => entry.GetProperty("id").GetString())
                .SequenceEqual(before.GetProperty("entries").EnumerateArray().Select(entry => entry.GetProperty("id").GetString())) &&
            after.GetProperty("entries").EnumerateArray().All(entry =>
                entry.GetProperty("song").GetProperty("songId").GetString() == Id(original) &&
                !entry.GetProperty("song").TryGetProperty("artistNames", out _) &&
                entry.GetProperty("song").GetProperty("duration").GetDouble() == 250));
        await owner.DisposeAsync();
        await using var reopened = new MusicLibraryOwner(store);
        var restored = await reopened.ExecuteAsync("music.queue.get");
        Check(restored.GetProperty("ok").GetBoolean() &&
            restored.GetProperty("entries").GetRawText() == after.GetProperty("entries").GetRawText());
        var restoredList = await Adopt(reopened, Structured("wy", "artists", "A", "B"));
        Check(Id(restoredList) == Id(original) && LJ.N(restoredList["baselineDuration"]) == 180 &&
            restoredList["artistNames"]!.ToJsonString() == "[\"A\",\"B\"]");
    }
    private static async Task RegisteredSets()
    {
        await using var store = new MusicOfficialAudioTests.MemoryStore();
        await using var owner = new MusicLibraryOwner(store);
        var first = await Adopt(owner, Song("wy", "shared", 103));
        foreach (var extra in new[] { Song("tx", "low", 100), Song("tx", "high", 106), Song("kw", "middle", 100.5) })
        {
            var observation = Song("wy", "shared", 103);
            observation["sources"]!.AsArray().Add(extra["sources"]![0]!.DeepClone());
            Check(Id(await Adopt(owner, observation)) == Id(first));
        }
        Check(owner.SongSnapshot(Id(first))!.Value.GetProperty("sources").GetArrayLength() == 4);
    }
    private static async Task LocalAndUnknown()
    {
        await using var store = new MusicOfficialAudioTests.MemoryStore();
        await using var owner = new MusicLibraryOwner(store);
        var first = await Adopt(owner, MusicSongFixture.Local("file:first", "Song", "Artist", duration: 180));
        var second = await Adopt(owner, MusicSongFixture.Local("file:second", "Song", "Artist", duration: 180));
        Check(Id(first) != Id(second) && Id(first) != Id(await Adopt(owner, Song("wy", "unbound"))));
        Check(Id(await Adopt(owner, MusicSongFixture.Local("file:first", "Corrected", duration: 400))) == Id(first));
        const string recoveredTitle = "First valid duration";
        var zero = await Adopt(owner, Song("wy", "zero", 0, recoveredTitle));
        Check(zero["baselineDuration"] is null);
        Check(Id(zero) != Id(await Adopt(owner, Song("tx", "zero", 0, recoveredTitle))));
        var recovered = await Adopt(owner, Song("wy", "zero", 180, recoveredTitle));
        Check(Id(recovered) == Id(zero) && LJ.N(recovered["baselineDuration"]) == 180);
        Check(Id(await Adopt(owner, Song("kw", "known", 185, recoveredTitle))) == Id(zero));
        Check(Id(await Adopt(owner, Song("kg", "beyond", 185.1, recoveredTitle))) != Id(zero));
        var missing = await Adopt(owner, Song("wy", "missing", artist: ""));
        Check(Id(missing) != Id(await Adopt(owner, Song("tx", "missing", artist: ""))));
        Check(Id(await Adopt(owner, Song("wy", "missing", title: "Corrected", artist: "Known"))) == Id(missing));
    }
    private static async Task PartialObservation()
    {
        await using var store = new MusicOfficialAudioTests.MemoryStore();
        await using var owner = new MusicLibraryOwner(store);
        var first = await Adopt(owner, Song("wy", "primary"));
        var partial = Song("wy", "primary", 0, "", "");
        var result = await Adopt(owner, partial, authoritative: false);
        Check(Id(result) == Id(first) && LJ.S(result["title"]) == "Song" && LJ.S(result["artist"]) == "Artist");
        Check(result["duration"]!.GetValue<double>() == 180);
        var corrected = await Adopt(owner, partial);
        Check(Id(corrected) == Id(first) && LJ.S(corrected["title"]) == "" && corrected["duration"] is null);
        Check(corrected["baselineDuration"]!.GetValue<double>() == 180);
    }
    private static async Task Detached()
    {
        await using var store = new MusicOfficialAudioTests.MemoryStore();
        await using var owner = new MusicLibraryOwner(store);
        var input = Song("wy", "1"); string before = input.ToJsonString();
        var adopted = await Adopt(owner, input); string id = Id(adopted);
        adopted["title"] = "modified output"; MusicSongFixture.Info(adopted)["singer"] = "modified source";
        Check(input.ToJsonString() == before);
        var current = await Adopt(owner, input, authoritative: false);
        Check(Id(current) == id && LJ.S(current["title"]) == "Song" && LJ.S(MusicSongFixture.Info(current)["singer"]) == "Artist");
    }
    private static async Task Variants()
    {
        await using var store = new MusicOfficialAudioTests.MemoryStore();
        var owner = new MusicLibraryOwner(store); var ids = new HashSet<string>();
        for (int i = 0; i < 160; i++) ids.Add(Id(await Adopt(owner, Song("mg", "variant-" + i, i % 2 == 0 ? 100 : 200))));
        Check(ids.Count == 2 && ids.All(id => owner.SongSnapshot(id)!.Value.GetProperty("sources").GetArrayLength() == 80));
        await owner.DisposeAsync();
        await using var reopened = new MusicLibraryOwner(store);
        for (int i = 0; i < 80; i++)
        {
            var result = await Adopt(reopened, Song("tx", "later-" + i, i % 2 == 0 ? 100 : 200));
            Check(ids.Contains(Id(result)));
            if (i >= 78) Check(result["sources"]!.AsArray().Count == 120);
        }
    }
    private static async Task Normalization()
    {
        await using var store = new MusicOfficialAudioTests.MemoryStore();
        await using var owner = new MusicLibraryOwner(store);
        var original = await Adopt(owner, Song("wy", "one", 180, "  ＳＯＮＧ\u3000 (Live) ", " ＡＲＴＩＳＴ  "));
        var matched = await Adopt(owner, Song("tx", "two", 180, "song (live)", "artist"));
        Check(Id(original) == Id(matched) && LJ.S(matched["title"]) == "  ＳＯＮＧ\u3000 (Live) ");
        var nextLine = await Adopt(owner, Song("kg", "next-line", 180, "\u0085song\u0085\u0085(live)\u0085", "\u0085artist\u0085"));
        Check(Id(nextLine) == Id(original), "Unicode NEXT LINE is whitespace in both title and artist lookup");
        var titleBom = await Adopt(owner, Song("kw", "title-bom", 180, "\uFEFFsong (live)", "artist"));
        Check(Id(titleBom) != Id(original) && LJ.S(titleBom["title"]) == "\uFEFFsong (live)", "FEFF is title content, not whitespace");
        var artistBom = await Adopt(owner, Song("mg", "artist-bom", 180, "song (live)", "\uFEFFartist"));
        Check(Id(artistBom) != Id(original) && LJ.S(artistBom["artist"]) == "\uFEFFartist", "FEFF is artist content, not whitespace");
        foreach (string title in new[] { "Song", "Song (Remix)", "Song (伴奏)" })
            Check(Id(await Adopt(owner, Song("kg", title, title: title))) != Id(original));
        var boundary = await Adopt(owner, Song("wy", "boundary", title: "ab", artist: "c"));
        Check(Id(await Adopt(owner, Song("tx", "boundary", title: "a", artist: "bc"))) != Id(boundary));
    }
    private static async Task StructuredArtists()
    {
        await using var store = new MusicOfficialAudioTests.MemoryStore();
        await using var owner = new MusicLibraryOwner(store);
        var first = await Adopt(owner, Structured("wy", "one", "Ａ", " B "));
        var reordered = await Adopt(owner, Structured("tx", "two", "b", "a", "a"));
        Check(Id(first) == Id(reordered) && LJ.S(reordered["artist"]) == "Ａ /  B ");
        Check(reordered["artistNames"]![0]!.GetValue<string>() == "Ａ");
        Check(Id(await Adopt(owner, Song("kg", "text", title: "Duet", artist: "A / B"))) != Id(first));
        var single = await Adopt(owner, Structured("wy", "single", "A / B"));
        Check(Id(await Adopt(owner, Song("kw", "text-single", title: "Duet", artist: "a / b"))) == Id(single));
    }
    private static async Task IncompleteStructuredArtists()
    {
        await using var store = new MusicOfficialAudioTests.MemoryStore();
        await using var owner = new MusicLibraryOwner(store);
        var known = await Adopt(owner, Structured("wy", "known", "Artist"));
        string[] missing = ["", " \t\u3000", "未知歌手", "未知歌曲", "Unknown", " UNKNOWN\tARTIST ", "Ｕｎｋｎｏｗｎ　Ｔｉｔｌｅ"];
        for (int i = 0; i < missing.Length; i++)
        {
            string sourceId = "incomplete-" + i;
            var first = await Adopt(owner, Structured("wy", sourceId, "Artist", missing[i]));
            var second = await Adopt(owner, Structured("tx", sourceId, missing[i], "Artist"));
            Check(Id(first) != Id(known) && Id(second) != Id(known), "an incomplete list cannot be reduced to the known artist");
            Check(Id(first) != Id(second), "an unknown artist item is not a shared matching fact");
            Check(first["artistNames"]!.AsArray().Count == 2 && LJ.S(first["artistNames"]![1]) == missing[i]);
            Check(LJ.S(MusicSongFixture.Info(first)["songmid"]) == sourceId, "incomplete matching facts must not lose the usable locator");
        }
    }
    private static async Task UnknownMetadata()
    {
        await using var store = new MusicOfficialAudioTests.MemoryStore();
        await using var owner = new MusicLibraryOwner(store);
        string[] placeholders = ["未知歌手", "未知歌曲", "Unknown", " UNKNOWN\tARTIST ", "Ｕｎｋｎｏｗｎ　Ｔｉｔｌｅ"];
        for (int i = 0; i < placeholders.Length; i++)
        {
            var title = await Adopt(owner, Song("wy", "title-" + i, title: placeholders[i]));
            Check(Id(title) != Id(await Adopt(owner, Song("tx", "title-" + i, title: placeholders[i]))),
                "an unknown title is not a shared matching fact");
            var artist = await Adopt(owner, Song("wy", "artist-" + i, artist: placeholders[i]));
            Check(Id(artist) != Id(await Adopt(owner, Song("tx", "artist-" + i, artist: placeholders[i]))),
                "an unknown complete artist text is not a shared matching fact");
        }
        foreach (string known in new[] { "Unknown Soldiers", "未知之名" })
        {
            var first = await Adopt(owner, Song("wy", known, title: known, artist: known));
            Check(Id(first) == Id(await Adopt(owner, Song("tx", known, title: known, artist: known))),
                "only the exact placeholders are unknown, not every title or artist with that prefix");
        }
    }
    private static async Task CompletedMetadata()
    {
        await using var store = new MusicOfficialAudioTests.MemoryStore();
        await using var owner = new MusicLibraryOwner(store);
        var known = await Adopt(owner, Structured("tx", "known", "Artist"));
        var incomplete = await Adopt(owner, Structured("wy", "incomplete", "Artist", " "));
        var unknown = await Adopt(owner, Song("kg", "unknown", title: "Unknown", artist: "未知歌手"));
        Check(Id(incomplete) != Id(known) && Id(unknown) != Id(known) && Id(incomplete) != Id(unknown));
        Check(Id(await Adopt(owner, Structured("wy", "incomplete", "Artist", " "))) == Id(incomplete));
        Check(Id(await Adopt(owner, Song("kg", "unknown", title: "Unknown", artist: "未知歌手"))) == Id(unknown));
        var completedList = await Adopt(owner, Structured("wy", "incomplete", "Artist"));
        var completedText = await Adopt(owner, Song("kg", "unknown", title: "Duet", artist: "Artist"));
        Check(Id(completedList) == Id(incomplete) && Id(completedText) == Id(unknown),
            "completion reuses the source owner instead of rebinding to an existing metadata match");
        Check(LJ.S(completedList["artist"]) == "Artist" && completedList["artistNames"]!.AsArray().Count == 1);
        Check(LJ.S(completedText["title"]) == "Duet" && LJ.S(completedText["artist"]) == "Artist");
        Check(completedList["sources"]!.AsArray().Count == 1 && completedText["sources"]!.AsArray().Count == 1);
        Check(LJ.S(MusicSongFixture.Info(completedList)["songmid"]) == "incomplete" &&
            LJ.S(MusicSongFixture.Info(completedText)["songmid"]) == "unknown");
        Check(Id(await Adopt(owner, Structured("tx", "known", "Artist"))) == Id(known));
        Check(Id(await Adopt(owner, Structured("wy", "incomplete", "Artist"))) == Id(incomplete));
        Check(Id(await Adopt(owner, Song("kg", "unknown", title: "Duet", artist: "Artist"))) == Id(unknown));
    }
    private static async Task IndependentDatabases()
    {
        await using var firstStore = new MusicOfficialAudioTests.MemoryStore();
        await using var secondStore = new MusicOfficialAudioTests.MemoryStore();
        await using var first = new MusicLibraryOwner(firstStore);
        await using var second = new MusicLibraryOwner(secondStore);
        Check(Id(await Adopt(first, Song("wy", "same"))) != Id(await Adopt(second, Song("wy", "same"))));
    }
    private static Task Cancellation()
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var song = MusicSongFixture.Local("file:cancel");
        var group = new NativeMusicSong.Recordings(song);
        var groups = new NativeMusicSongGroups(cancelled.Token);
        Action[] actions =
        [
            () => _ = new NativeMusicSong.Recordings(song, cancelled.Token),
            () => group.CanMerge(new(song), cancelled.Token),
            () => group.TryMerge(new(song), token: cancelled.Token),
            () => group.ToSong(cancelled.Token),
            () => groups.Import(song),
            () => groups.Find(new(song)),
            () => groups.Add(new(song))
        ];
        foreach (var action in actions)
        {
            bool observed = false;
            try { action(); } catch (OperationCanceledException) { observed = true; }
            Check(observed);
        }
        return Task.CompletedTask;
    }
}
