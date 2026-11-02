using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Windows.Tests;

public static class MusicSongIdentityTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("Song recording owner rejects non-transitive sets without mutating either input", Pairwise);
        yield return new("Song recording groups prefer exact source bindings over ambiguous duration matches", ExactSource);
        yield return new("Song recording updates replace same-key evidence and update the shared fingerprint index", Updates);
        yield return new("Song recording groups distinguish conflicting sets with the same representative", QualifiedSets);
        yield return new("Song recording groups retain local identity and only match known online evidence", LocalAndUnknown);
        yield return new("Song display updates without a primary recording preserve the bound primary", LocalDisplayUpdate);
        yield return new("Song recording owner canonical output is detached from inputs and other snapshots", Detached);
        yield return new("Song recording grouping handles many same-fingerprint variants across retained groups", Variants);
        yield return new("Song recording operations honor cancellation including empty local sets", Cancellation);
    }
    private static void Check(bool condition, string message = "Assertion failed")
    { if (!condition) throw new InvalidOperationException(message); }
    private static JsonObject Song(string platform, string id, double duration = 180, string title = "Song", string artist = "Artist") =>
        MusicSongFixture.Online(platform, id, title, artist, duration: duration);
    private static JsonObject Combine(JsonObject main, params JsonObject[] other)
    {
        var result = main.DeepClone().AsObject();
        foreach (var song in other) result["sources"]!.AsArray().Add(song["sources"]![0]!.DeepClone());
        return NativeMusicSong.Normalize(result);
    }
    private static void Canonical(JsonObject song)
    {
        Check(JsonNode.DeepEquals(song, NativeMusicSong.Normalize(song)));
        _ = SongRow.Read(JsonSerializer.SerializeToElement(song));
    }
    private static Task Pairwise()
    {
        var a = Combine(Song("wy", "one", 103), Song("tx", "one", 100));
        var b = Combine(Song("wy", "two", 103), Song("tx", "two", 106));
        var left = new NativeMusicSong.Recordings(a); var right = new NativeMusicSong.Recordings(b);
        Check(!left.CanMerge(right) && !right.CanMerge(left) && !left.TryMerge(right));
        Check(!left.TryAddSource(b["sources"]![1]!.AsObject()));
        Check(JsonNode.DeepEquals(a, left.ToSong()) && JsonNode.DeepEquals(b, right.ToSong()));
        return Task.CompletedTask;
    }
    private static Task ExactSource()
    {
        var groups = new NativeMusicSongGroups();
        var first = groups.Add(new(Song("wy", "short", 100)));
        var second = groups.Add(new(Song("tx", "long", 106)));
        var ambiguous = new NativeMusicSong.Recordings(Song("tx", "long", 103));
        Check(first.CanMerge(ambiguous) && second.CanMerge(ambiguous));
        Check(ReferenceEquals(groups.Find(ambiguous), second));
        Check(ReferenceEquals(groups.Add(ambiguous), second));
        Check(first.Id != second.Id);
        Canonical(second.ToSong());
        return Task.CompletedTask;
    }
    private static Task Updates()
    {
        var groups = new NativeMusicSongGroups();
        var original = groups.Add(new(Song("wy", "same", title: "Before")));
        string id = original.Id;
        var updated = groups.Add(new(Song("wy", "same", 181, "After")), preferIncoming: true);
        Check(ReferenceEquals(original, updated) && updated.Id == id);
        Check(updated.ToSong()["title"]!.GetValue<string>() == "After");
        Check(MusicSongFixture.Info(updated.ToSong())["duration"]!.GetValue<double>() == 181);
        var current = groups.Add(new(Song("tx", "current", 181, "After")), preferIncoming: true);
        Check(ReferenceEquals(updated, current) && current.Id == id);
        Check(MusicSongFixture.Info(current.ToSong())["source"]!.GetValue<string>() == "tx");
        Check(current.Sources.Count() == 2);
        var oldTitle = groups.Add(new(Song("kw", "old", title: "Before")));
        Check(!ReferenceEquals(oldTitle, current), "replaced metadata remained in the grouping index");
        Canonical(current.ToSong()); Canonical(oldTitle.ToSong());
        return Task.CompletedTask;
    }
    private static Task QualifiedSets()
    {
        // Complete snapshots can share a representative while disagreeing in their other recordings.
        var root = Song("wy", "shared", 103);
        JsonObject[] songs =
        [
            Combine(root, Song("tx", "low", 100)),
            Combine(root, Song("tx", "high", 106)),
            Combine(root, Song("kw", "middle-low", 100.5), Song("mg", "middle-high", 103.5))
        ];
        var groups = new NativeMusicSongGroups();
        var added = songs.Select(song => groups.Add(new(song))).ToArray();
        Check(added.Select(group => group.Id).Distinct().Count() == 3);
        for (int i = 0; i < songs.Length; i++)
        {
            Check(ReferenceEquals(added[i], groups.Add(new(songs[i]))));
            Canonical(added[i].ToSong());
        }
        var retained = new NativeMusicSongGroups();
        foreach (var group in added) retained.Import(group.ToSong());
        Check(songs.Select(song => retained.Add(new(song)).Id).SequenceEqual(added.Select(group => group.Id)));
        return Task.CompletedTask;
    }
    private static Task LocalAndUnknown()
    {
        var groups = new NativeMusicSongGroups();
        var local = MusicSongFixture.Local("local:first", "Song", "Artist", duration: 180);
        var first = groups.Add(new(local));
        var second = groups.Add(new(MusicSongFixture.Local("local:second", "Song", "Artist", duration: 180)));
        Check(!ReferenceEquals(first, second));
        var online = groups.Add(new(Song("wy", "unbound")));
        Check(!ReferenceEquals(first, online) && !ReferenceEquals(second, online), "local membership cannot be guessed from metadata");
        var corrected = groups.Add(new(MusicSongFixture.Local("local:first", "Corrected", duration: 400)), preferIncoming: true);
        Check(ReferenceEquals(first, corrected) && corrected.ToSong()["title"]!.GetValue<string>() == "Corrected");
        var binding = Song("tx", "explicit");
        binding["songId"] = second.Id;
        var bound = groups.Add(new(binding), preferIncoming: true);
        Check(ReferenceEquals(bound, second) && !bound.IsLocal && bound.Id == "local:second");
        Canonical(bound.ToSong());

        var unknown = new NativeMusicSongGroups();
        var zero = unknown.Add(new(Song("wy", "zero", 0)));
        Check(!ReferenceEquals(zero, unknown.Add(new(Song("tx", "zero", 0)))));
        Check(ReferenceEquals(zero, unknown.Add(new(Song("wy", "zero", 180)), preferIncoming: true)));
        Check(ReferenceEquals(zero, unknown.Add(new(Song("kw", "known", 180)))));
        var absent = unknown.Add(new(Song("wy", "absent", artist: "Unknown")));
        Check(!ReferenceEquals(absent, unknown.Add(new(Song("tx", "absent", artist: "Unknown")))));
        Canonical(zero.ToSong());
        return Task.CompletedTask;
    }
    private static Task Detached()
    {
        var original = Song("wy", "1"); var incoming = Song("tx", "2");
        string before = original.ToJsonString(), nextBefore = incoming.ToJsonString();
        var groups = new NativeMusicSongGroups();
        var group = groups.Import(original); groups.Add(new(incoming));
        var result = group.ToSong(); result["title"] = "modified output";
        MusicSongFixture.Info(result)["singer"] = "modified source";
        Check(original.ToJsonString() == before && incoming.ToJsonString() == nextBefore);
        Canonical(group.ToSong());
        return Task.CompletedTask;
    }
    private static Task LocalDisplayUpdate()
    {
        var song = Combine(Song("tx", "primary"), Song("kg", "other"));
        var recordings = new NativeMusicSong.Recordings(song);
        var local = MusicSongFixture.Local(recordings.Id, "Song", "Artist", "New album", 180);
        Check(recordings.TryMerge(new(local), preferIncoming: true));
        var result = recordings.ToSong();
        Check(result["album"]!.GetValue<string>() == "New album" &&
            MusicSongFixture.Info(result)["source"]!.GetValue<string>() == "tx");
        Canonical(result);
        var discovered = new NativeMusicSong.Recordings(local);
        Check(discovered.TryAddSource(Song("tx", "first")["sources"]![0]!.AsObject()));
        Check(discovered.TryAddSource(Song("kg", "second")["sources"]![0]!.AsObject()));
        Check(MusicSongFixture.Info(discovered.ToSong())["source"]!.GetValue<string>() == "tx");
        Canonical(discovered.ToSong());
        return Task.CompletedTask;
    }
    private static Task Variants()
    {
        var groups = new NativeMusicSongGroups();
        var distinct = new HashSet<NativeMusicSong.Recordings>();
        for (int i = 0; i < 160; i++) distinct.Add(groups.Add(new(Song("mg", "variant-" + i, i % 2 == 0 ? 100 : 200))));
        Check(distinct.Count == 2 && distinct.All(group => group.Sources.Count() == 80));
        var retained = new NativeMusicSongGroups();
        foreach (var group in distinct) { var song = group.ToSong(); Canonical(song); retained.Import(song); }
        for (int i = 0; i < 80; i++)
        {
            var result = retained.Add(new(Song("tx", "later-" + i, i % 2 == 0 ? 100 : 200)));
            Check(distinct.Any(group => group.Id == result.Id));
            if (i >= 78) { Check(result.Sources.Count() == 120); Canonical(result.ToSong()); }
        }
        return Task.CompletedTask;
    }
    private static Task Cancellation()
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var song = MusicSongFixture.Local("local:cancel");
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
