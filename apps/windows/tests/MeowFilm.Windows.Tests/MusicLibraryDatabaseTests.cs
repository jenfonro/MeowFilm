using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;
using MeowFilm.Platform.Storage;
using Microsoft.Data.Sqlite;

namespace MeowFilm.Windows.Tests;

public static class MusicLibraryDatabaseTests
{
    private const string Scope = "database-tests";
    private const string DatabaseName = "music-library.sqlite3";
    private const string RecoveryCase = "Music SQLite crashed write recovers the pinned persistent journal before schema reads";
    private const string CrashRootEnvironment = "MEOWFILM_SQLITE_CRASH_FIXTURE";

    public static IEnumerable<TestCase> Cases()
    {
        yield return new("Music SQLite explicit memory store reads empty controls and indexed records", MemoryRecords);
        yield return new("Music SQLite lazy file construction and owned records reopen durably", Reopen);
        yield return new("Music SQLite missing root is created only on first read", MissingRoot);
        yield return new("Music SQLite exact generations and receipts reject stale commits", Generations);
        yield return new("Music SQLite source conflict atomically rolls back songs controls and receipt", AtomicSourceConflict);
        yield return new("Music SQLite registered source removal cannot enable later rebinding", SourceRemoval);
        yield return new("Music SQLite unchanged records and sources do not execute update statements", ActualChanges);
        yield return new("Music SQLite candidates use online known recording and fixed duration boundaries", Candidates);
        yield return new("Music SQLite cancellation before admission and after admission preserve real outcome", Cancellation);
        yield return new("Music SQLite dispose drains admitted owned writes and releases file pins", Drain);
        yield return new(RecoveryCase, Recovery);
        yield return new("Music SQLite corrupt existing and empty databases fail without replacement", Corrupt);
        yield return new("Music SQLite unknown schema and header formats fail without replacement", UnknownFormat);
        yield return new("Music SQLite database and persistent journal hardlinks fail closed", UnsafeFiles);
        yield return new("Music SQLite ten thousand five-source songs hot queries and one-record page changes", Scale);
        yield return new("Music library ten thousand songs use bounded page reads zero-write adoption and control-only sync", OwnerScale);
    }

    private static void Check(bool condition, string message = "Assertion failed")
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static MusicLibraryRevision Next(MusicLibraryRevision previous) => new(previous.Generation + 1, Guid.NewGuid().ToString("D"));
    private static MusicLibraryChanges Changes(IEnumerable<JsonObject>? songs = null, JsonObject? control = null) =>
        new(control is null ? new Dictionary<string, JsonObject>() : new Dictionary<string, JsonObject> { ["control"] = control },
            (songs ?? []).ToDictionary(song => song["songId"]!.GetValue<string>(), StringComparer.Ordinal));
    private static JsonObject Song(string platform, string native, double duration = 100, string title = "Recording", string? id = null)
    {
        var song = MusicSongFixture.Online(platform, native, title, "Artist", duration: duration);
        song["songId"] = id ?? Guid.NewGuid().ToString("D");
        return song;
    }
    private static string Id(JsonObject song) => song["songId"]!.GetValue<string>();
    private static string Key(JsonObject song, int position = 0) => song["sources"]![position]!["sourceKey"]!.GetValue<string>();
    private static async Task Reject(DocumentStoreError expected, Func<Task> operation)
    {
        try { await operation(); }
        catch (DocumentStoreException error) { Check(error.Error == expected, "Unexpected fixed error category"); return; }
        throw new InvalidOperationException("Expected fixed storage failure");
    }
    private sealed class Folder : IDisposable
    {
        internal string Root { get; } = TestWorkspace.DataPath("music-sqlite-" + Guid.NewGuid().ToString("N"));
        internal string Database => Path.Combine(Root, DatabaseName);
        internal Folder() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
    private static SqliteConnection Inspect(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        connection.Open();
        _ = Sql(connection, "PRAGMA journal_mode=PERSIST");
        return connection;
    }
    private static object? Sql(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
    private static byte[] Bytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static async Task MemoryRecords()
    {
        await Reject(DocumentStoreError.InvalidRequest, () =>
        {
            _ = new MusicLibraryDatabase("");
            return Task.CompletedTask;
        });
        await Reject(DocumentStoreError.InvalidRequest, () =>
        {
            _ = new MusicLibraryDatabase(null!);
            return Task.CompletedTask;
        });
        await using var database = MusicLibraryDatabase.CreateMemory();
        var before = await database.ReadAsync(Scope);
        Check(before.Revision == MusicLibraryRevision.Empty && before.Areas.Count == 0 && database.SongIds().Count == 0);
        var song = Song("wy", "123");
        var kg = Song("kg", "123");
        var variant = kg["sources"]![0]!.AsObject();
        variant["musicInfo"]!["hash"] = "ABC123";
        variant["sourceKey"] = NativeMusicSong.InfoKey(variant["musicInfo"]!.AsObject());
        song["sources"]!.AsArray().Add(variant.DeepClone());
        var next = Next(before.Revision);
        await database.CommitAsync(Scope, before.Revision, next, Changes([song]));
        Check(database.FindSource(Key(song)) == Id(song) && database.FindSource(Key(song, 1)) == Id(song));
        Check(database.FindSource("[\"kg\",\"123\",\"wrong\"]") is null);
        Check(JsonNode.DeepEquals(database.ReadSong(Id(song)), song));
        Check(database.ContainsSong(Id(song)) && !database.ContainsSong(Guid.NewGuid().ToString("D")));
        var copy = database.ReadSong(Id(song))!;
        copy["title"] = "Caller-owned";
        Check(database.ReadSong(Id(song))!["title"]!.GetValue<string>() == "Recording");
    }

    private static async Task Reopen()
    {
        using var folder = new Folder();
        var song = Song("wy", "reopen");
        var revision = Next(MusicLibraryRevision.Empty);
        var database = new MusicLibraryDatabase(folder.Root);
        Check(!File.Exists(folder.Database));
        await database.ReadAsync(Scope);
        await database.CommitAsync(Scope, MusicLibraryRevision.Empty, revision,
            Changes([song], new JsonObject { ["selected"] = Id(song) }));
        await database.DisposeAsync();
        using (var inspect = Inspect(folder.Database))
        {
            Check((long)Sql(inspect, "SELECT count(*) FROM songs")! == 1);
            Check((long)Sql(inspect, "SELECT count(*) FROM song_sources")! == 1);
            Check((long)Sql(inspect, "SELECT count(*) FROM songs WHERE json_type(data,'$.sources') IS NOT NULL")! == 0);
        }
        await using var reopened = new MusicLibraryDatabase(folder.Root);
        var snapshot = await reopened.ReadAsync(Scope);
        Check(snapshot.Revision == revision && JsonNode.DeepEquals(reopened.ReadSong(Id(song)), song));
        snapshot.Areas["control"]["selected"] = "caller";
        Check((await reopened.ReadAsync(Scope)).Areas["control"]["selected"]!.GetValue<string>() == Id(song));
        Check(new FileInfo(folder.Database + "-journal").Exists);
    }

    private static async Task Generations()
    {
        await using var database = MusicLibraryDatabase.CreateMemory();
        var initial = await database.ReadAsync(Scope);
        var revision = Next(initial.Revision);
        await database.CommitAsync(Scope, initial.Revision, revision, Changes([Song("wy", "generation")]));
        await Reject(DocumentStoreError.Conflict, () => database.CommitAsync(Scope, initial.Revision, Next(initial.Revision), Changes()));
        await Reject(DocumentStoreError.Conflict, () => database.CommitAsync(Scope, revision with { Receipt = "wrong" }, Next(revision), Changes()));
        await Reject(DocumentStoreError.InvalidRequest, () => database.CommitAsync(Scope, revision, revision with { Generation = revision.Generation + 1 }, Changes()));
        await Reject(DocumentStoreError.InvalidRequest, () => database.CommitAsync(Scope, revision, new(revision.Generation + 2, "different"), Changes()));
        await Reject(DocumentStoreError.Conflict, () => database.ReadAsync("wrong-scope"));
        Check((await database.ReadAsync(Scope)).Revision == revision);
    }

    private static async Task MissingRoot()
    {
        using var folder = new Folder();
        string root = Path.Combine(folder.Root, "new-root");
        await using var database = new MusicLibraryDatabase(root);
        Check(!Directory.Exists(root));
        var snapshot = await database.ReadAsync(Scope);
        Check(snapshot.Revision == MusicLibraryRevision.Empty && database.SongIds().Count == 0);
    }

    private static async Task AtomicSourceConflict()
    {
        using var folder = new Folder();
        await using var database = new MusicLibraryDatabase(folder.Root);
        await database.ReadAsync(Scope);
        var first = Song("wy", "bound");
        var revision = Next(MusicLibraryRevision.Empty);
        await database.CommitAsync(Scope, MusicLibraryRevision.Empty, revision,
            Changes([first], new JsonObject { ["selected"] = "before" }));
        var unrelated = Song("kw", "unrelated");
        var conflict = Song("wy", "bound");
        await Reject(DocumentStoreError.Conflict, () => database.CommitAsync(Scope, revision, Next(revision),
            Changes([unrelated, conflict], new JsonObject { ["selected"] = "after" })));
        var after = await database.ReadAsync(Scope);
        Check(after.Revision == revision && after.Areas["control"]["selected"]!.GetValue<string>() == "before");
        Check(database.FindSource(Key(first)) == Id(first) && database.FindSource(Key(unrelated)) is null);
        Check(database.ReadSong(Id(unrelated)) is null && database.ReadSong(Id(conflict)) is null && database.SongIds().Count == 1);
        await database.DisposeAsync();
        await using var reopened = new MusicLibraryDatabase(folder.Root);
        Check((await reopened.ReadAsync(Scope)).Revision == revision && reopened.SongIds().Count == 1 &&
            reopened.FindSource(Key(first)) == Id(first) && reopened.ReadSong(Id(unrelated)) is null);
    }

    private static async Task SourceRemoval()
    {
        await using var database = MusicLibraryDatabase.CreateMemory();
        await database.ReadAsync(Scope);
        var song = Song("wy", "permanent");
        var revision = Next(MusicLibraryRevision.Empty);
        await database.CommitAsync(Scope, MusicLibraryRevision.Empty, revision, Changes([song]));
        var changed = song.DeepClone().AsObject();
        changed["sources"] = new JsonArray();
        await Reject(DocumentStoreError.Conflict, () => database.CommitAsync(Scope, revision, Next(revision), Changes([changed])));
        Check(database.FindSource(Key(song)) == Id(song));
    }

    private static async Task Candidates()
    {
        await using var database = MusicLibraryDatabase.CreateMemory();
        await database.ReadAsync(Scope);
        var known = Song("wy", "known", 100);
        known["baselineDuration"] = 100;
        known["duration"] = 200;
        var unknown = Song("tx", "unknown", 100, "未知歌曲");
        var local = NativeMusicSong.CreateLocal("local-file", "Recording", "Artist", duration: 100);
        local["songId"] = Guid.NewGuid().ToString("D");
        var noDuration = Song("kw", "no-duration", 0);
        await database.CommitAsync(Scope, MusicLibraryRevision.Empty, Next(MusicLibraryRevision.Empty),
            Changes([known, unknown, local, noDuration]));
        string fingerprint = NativeMusicSong.Fingerprint(known);
        Check(database.FindCandidates(fingerprint, 95).SequenceEqual([Id(known)]));
        Check(database.FindCandidates(fingerprint, 105).SequenceEqual([Id(known)]));
        Check(database.FindCandidates(fingerprint, 105.01).Count == 0 && database.FindCandidates(fingerprint, 200).Count == 0);
        Check(database.FindCandidates("", 100).Count == 0 && database.FindCandidates(fingerprint, double.NaN).Count == 0);
        Check(database.FindLocalFile("local-file") == Id(local) && database.FindLocalFile("") is null);
        known["cover_url"] = "https://example.invalid/cover";
        var revision = (await database.ReadAsync(Scope)).Revision;
        await database.CommitAsync(Scope, revision, Next(revision), Changes([known]));
        Check(database.FindArtwork("https://example.invalid/cover").SequenceEqual([Id(known)]) && database.FindArtwork("").Count == 0);
    }

    private static async Task ActualChanges()
    {
        using var folder = new Folder();
        await using var database = new MusicLibraryDatabase(folder.Root);
        await database.ReadAsync(Scope);
        using (var inspect = Inspect(folder.Database))
            _ = Sql(inspect, """
                CREATE TABLE audit(kind TEXT PRIMARY KEY,writes INTEGER NOT NULL);
                INSERT INTO audit VALUES('songs',0),('sources',0);
                CREATE TRIGGER audit_song_insert AFTER INSERT ON songs BEGIN UPDATE audit SET writes=writes+1 WHERE kind='songs'; END;
                CREATE TRIGGER audit_song_update AFTER UPDATE ON songs BEGIN UPDATE audit SET writes=writes+1 WHERE kind='songs'; END;
                CREATE TRIGGER audit_source_insert AFTER INSERT ON song_sources BEGIN UPDATE audit SET writes=writes+1 WHERE kind='sources'; END;
                CREATE TRIGGER audit_source_update AFTER UPDATE ON song_sources BEGIN UPDATE audit SET writes=writes+1 WHERE kind='sources'; END;
                """);
        var song = Song("wy", "actual-changes");
        var revision = Next(MusicLibraryRevision.Empty);
        await database.CommitAsync(Scope, MusicLibraryRevision.Empty, revision, Changes([song]));
        var next = Next(revision);
        await database.CommitAsync(Scope, revision, next, Changes([song]));
        using (var inspect = Inspect(folder.Database))
            Check((long)Sql(inspect, "SELECT writes FROM audit WHERE kind='songs'")! == 1 &&
                (long)Sql(inspect, "SELECT writes FROM audit WHERE kind='sources'")! == 1);
        song["album"] = "New album";
        revision = next; next = Next(revision);
        await database.CommitAsync(Scope, revision, next, Changes([song]));
        using (var inspect = Inspect(folder.Database))
            Check((long)Sql(inspect, "SELECT writes FROM audit WHERE kind='songs'")! == 2 &&
                (long)Sql(inspect, "SELECT writes FROM audit WHERE kind='sources'")! == 1);
        song["sources"]![0]!["musicInfo"]!["albumName"] = "New source album";
        await database.CommitAsync(Scope, next, Next(next), Changes([song]));
        using (var inspect = Inspect(folder.Database))
            Check((long)Sql(inspect, "SELECT writes FROM audit WHERE kind='songs'")! == 2 &&
                (long)Sql(inspect, "SELECT writes FROM audit WHERE kind='sources'")! == 2);
    }

    private static async Task Cancellation()
    {
        await using var database = MusicLibraryDatabase.CreateMemory();
        await database.ReadAsync(Scope);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try
        {
            await database.CommitAsync(Scope, MusicLibraryRevision.Empty, Next(MusicLibraryRevision.Empty),
                Changes([Song("wy", "not-admitted")]), canceled.Token);
            throw new InvalidOperationException("Expected cancellation");
        }
        catch (OperationCanceledException) { }
        Check(database.SongIds().Count == 0);
        using var afterAdmission = new CancellationTokenSource();
        var song = Song("wy", "admitted");
        var next = Next(MusicLibraryRevision.Empty);
        var commit = database.CommitAsync(Scope, MusicLibraryRevision.Empty, next, Changes([song]), afterAdmission.Token);
        afterAdmission.Cancel();
        song["title"] = "Mutated after admission";
        Check(await commit == next && database.ReadSong(Id(song))!["title"]!.GetValue<string>() == "Recording");
    }

    private static async Task Drain()
    {
        using var folder = new Folder();
        var database = new MusicLibraryDatabase(folder.Root);
        await database.ReadAsync(Scope);
        try
        {
            File.Move(folder.Database, Path.Combine(folder.Root, "must-not-move.sqlite3"));
            throw new InvalidOperationException("Open database did not pin its file name");
        }
        catch (IOException) { }
        var song = Song("wy", "drain");
        var next = Next(MusicLibraryRevision.Empty);
        var commit = database.CommitAsync(Scope, MusicLibraryRevision.Empty, next, Changes([song]));
        var second = Song("tx", "drain-second");
        var last = Next(next);
        var queued = database.CommitAsync(Scope, next, last, Changes([second]));
        var dispose = database.DisposeAsync().AsTask();
        await Reject(DocumentStoreError.Closed, () => database.ReadAsync(Scope));
        Check(await commit == next);
        Check(await queued == last);
        await dispose;
        await database.DisposeAsync();
        // Deletion denied by the pins during use must now be possible.
        string moved = Path.Combine(folder.Root, "released.sqlite3");
        File.Move(folder.Database, moved);
        File.Move(moved, folder.Database);
        await using var reopen = new MusicLibraryDatabase(folder.Root);
        Check((await reopen.ReadAsync(Scope)).Revision == last && reopen.ContainsSong(Id(song)) && reopen.ContainsSong(Id(second)));
    }

    private static async Task Corrupt()
    {
        foreach (byte[] bytes in new[] { Array.Empty<byte>(), new byte[] { 1, 2, 3, 4 } })
        {
            using var folder = new Folder();
            File.WriteAllBytes(folder.Database, bytes);
            await using var database = new MusicLibraryDatabase(folder.Root);
            await Reject(DocumentStoreError.Corrupt, () => database.ReadAsync(Scope));
            Check(File.ReadAllBytes(folder.Database).SequenceEqual(bytes));
        }
    }

    private static async Task Recovery()
    {
        if (Environment.GetEnvironmentVariable(CrashRootEnvironment) is { } crashRoot)
        {
            string path = TestWorkspace.ValidatePath(TestWorkspace.DataRoot, Path.Combine(crashRoot, DatabaseName));
            // A child deliberately leaves a hot native SQLite journal. Never
            // call the library or dispose this connection before process exit.
            using var connection = Inspect(path);
            _ = Sql(connection, """
                PRAGMA synchronous=FULL;
                PRAGMA cache_size=1;
                BEGIN IMMEDIATE;
                UPDATE songs SET data=json_set(data,'$.album','uncommitted');
                """);
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO crash_scratch(data) VALUES($data)";
            insert.Parameters.AddWithValue("$data", new string('x', 4000));
            for (int i = 0; i < 1000; i++) _ = insert.ExecuteNonQuery();
            Environment.Exit(0);
            throw new InvalidOperationException("Crash fixture unexpectedly continued");
        }
        using var folder = new Folder();
        var song = Song("wy", "recovery");
        var revision = Next(MusicLibraryRevision.Empty);
        await using (var database = new MusicLibraryDatabase(folder.Root))
        {
            await database.ReadAsync(Scope);
            await database.CommitAsync(Scope, MusicLibraryRevision.Empty, revision, Changes([song]));
        }
        using (var inspect = Inspect(folder.Database))
            _ = Sql(inspect, "CREATE TABLE crash_scratch(data TEXT NOT NULL)");
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing child executable");
        var start = new ProcessStartInfo(executable)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(MusicLibraryDatabaseTests).Assembly.Location);
        start.ArgumentList.Add("--case"); start.ArgumentList.Add(RecoveryCase);
        start.Environment[CrashRootEnvironment] = folder.Root;
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Unable to start crash fixture");
        var output = child.StandardOutput.ReadToEndAsync();
        var errors = child.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try { await child.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync();
            await Task.WhenAll(output, errors);
            throw new InvalidOperationException("Crash fixture did not settle");
        }
        await Task.WhenAll(output, errors);
        Check(child.ExitCode == 0, "Crash fixture failed");
        var journal = Bytes(folder.Database + "-journal");
        Check(journal.Length > 512 && journal.AsSpan(0, 8).SequenceEqual(new byte[] { 0xd9, 0xd5, 0x05, 0xf9, 0x20, 0xa1, 0x63, 0xd7 }),
            "Crash fixture did not leave a hot SQLite journal");
        await using var recovered = new MusicLibraryDatabase(folder.Root);
        Check((await recovered.ReadAsync(Scope)).Revision == revision);
        Check(JsonNode.DeepEquals(recovered.ReadSong(Id(song)), song));
        using var reopened = Inspect(folder.Database);
        Check((long)Sql(reopened, "SELECT count(*) FROM crash_scratch")! == 0);
    }

    private static async Task UnknownFormat()
    {
        foreach (string change in new[] { "PRAGMA user_version=99", "UPDATE header SET format=99" })
        {
            using var folder = new Folder();
            await using (var database = new MusicLibraryDatabase(folder.Root)) await database.ReadAsync(Scope);
            using (var inspect = Inspect(folder.Database)) _ = Sql(inspect, change);
            var original = SHA256.HashData(Bytes(folder.Database));
            await using var reopened = new MusicLibraryDatabase(folder.Root);
            await Reject(DocumentStoreError.UnsupportedVersion, () => reopened.ReadAsync(Scope));
            Check(SHA256.HashData(Bytes(folder.Database)).SequenceEqual(original));
        }
    }

    private static async Task UnsafeFiles()
    {
        foreach (string suffix in new[] { "", "-journal" })
        {
            using var folder = new Folder();
            string target = Path.Combine(folder.Root, "ordinary-target");
            File.WriteAllBytes(target, [1, 2, 3, 4]);
            Check(CreateHardLinkW(folder.Database + suffix, target, IntPtr.Zero), "Unable to prepare hardlink fixture");
            await using var database = new MusicLibraryDatabase(folder.Root);
            await Reject(DocumentStoreError.UnsafePath, () => database.ReadAsync(Scope));
            Check(File.ReadAllBytes(target).SequenceEqual(new byte[] { 1, 2, 3, 4 }));
        }
    }

    private static async Task Scale()
    {
        using var folder = new Folder();
        await using var database = new MusicLibraryDatabase(folder.Root);
        await database.ReadAsync(Scope);
        var songs = Enumerable.Range(0, 10000).Select(index =>
        {
            var song = Song("wy", "scale-" + index, title: "Recording " + index);
            foreach (string platform in new[] { "tx", "kw", "kg", "mg" })
                song["sources"]!.AsArray().Add(Song(platform, "scale-" + index, title: "Recording " + index)["sources"]![0]!.DeepClone());
            return song;
        }).ToArray();
        var revision = Next(MusicLibraryRevision.Empty);
        await database.CommitAsync(Scope, MusicLibraryRevision.Empty, revision, Changes(songs));
        using (var inspect = Inspect(folder.Database))
            Check((long)Sql(inspect, "SELECT count(*) FROM song_sources")! == 50000);
        var before = Bytes(folder.Database);
        string soughtKey = Key(songs[7654], 4), soughtId = Id(songs[7654]);
        // Warm provider/type initialization before measuring repeated indexed reads.
        Check(database.FindSource(soughtKey) == soughtId);
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++) Check(database.FindSource(soughtKey) == soughtId);
        for (int i = 0; i < 100; i++) Check(database.ReadSong(soughtId)!["sources"]!.AsArray().Count == 5);
        watch.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Check(allocated < 64L * 1024 * 1024, "Indexed hot reads allocated a library-scale snapshot");
        Check((await database.ReadAsync(Scope)).Revision == revision && Bytes(folder.Database).SequenceEqual(before));
        var changed = songs[7654].DeepClone().AsObject();
        changed["album"] = "Updated album";
        await database.CommitAsync(Scope, revision, Next(revision), Changes([changed]));
        var after = Bytes(folder.Database);
        int pageSize;
        using (var inspect = Inspect(folder.Database)) pageSize = Convert.ToInt32(Sql(inspect, "PRAGMA page_size"));
        int pages = 0;
        for (int offset = 0; offset < Math.Max(before.Length, after.Length); offset += pageSize)
        {
            var left = before.AsSpan(Math.Min(offset, before.Length), Math.Min(pageSize, Math.Max(0, before.Length - offset)));
            var right = after.AsSpan(Math.Min(offset, after.Length), Math.Min(pageSize, Math.Max(0, after.Length - offset)));
            if (!left.SequenceEqual(right)) pages++;
        }
        Check(pages < 64, "Single-record update rewrote library-scale pages");
        Check(database.ReadSong(soughtId)!["album"]!.GetValue<string>() == "Updated album" && database.SongIds().Count == 10000);
        Console.WriteLine($"MUSIC_SQLITE_SCALE songs=10000 sources=50000 hotAllocatedBytes={allocated} hotElapsedMs={watch.ElapsedMilliseconds} changedPages={pages} pageSize={pageSize}");
    }

    private static async Task OwnerScale()
    {
        using var folder = new Folder();
        await using var database = new MusicLibraryDatabase(folder.Root);
        await database.ReadAsync(Scope);
        var songs = Enumerable.Range(0, 10000).Select(index =>
        {
            var song = Song("wy", "owner-scale-" + index, title: "Recording " + index);
            foreach (string platform in new[] { "tx", "kw", "kg", "mg" })
                song["sources"]!.AsArray().Add(Song(platform, "owner-scale-" + index,
                    title: "Recording " + index)["sources"]![0]!.DeepClone());
            return song;
        }).ToArray();
        await database.CommitAsync(Scope, MusicLibraryRevision.Empty, Next(MusicLibraryRevision.Empty), Changes(songs));
        await using (var setup = new MusicLibraryOwner(database, Scope))
            _ = await setup.CreatePlaylistAsync("Scale", []);
        var saved = await database.ReadAsync(Scope);
        var control = saved.Areas["library"].DeepClone().AsObject();
        var collection = control["library"]!["collections"]!.AsArray().OfType<JsonObject>()
            .Single(value => value["id"]!.GetValue<string>() == "playlist:1");
        collection["order"] = new JsonArray(songs.Select(song => (JsonNode?)JsonValue.Create(Id(song))).ToArray());
        await database.CommitAsync(Scope, saved.Revision, Next(saved.Revision),
            new(new Dictionary<string, JsonObject> { ["library"] = control }, new Dictionary<string, JsonObject>()));
        var measured = new MeasuredStore(database);
        await using var owner = new MusicLibraryOwner(measured, Scope);
        _ = await owner.ReadLibrarySyncAsync();
        var incoming = JsonSerializer.SerializeToElement(new { items = songs.Take(10).ToArray() });
        _ = await owner.AdoptPlatformResultAsync(incoming, default);
        measured.Reset();
        var before = (await database.ReadAsync(Scope)).Revision;
        _ = await owner.AdoptPlatformResultAsync(incoming, default);
        Check(measured.Commits == 0 && measured.Candidates == 0 && measured.Enumerations == 0 &&
            (await database.ReadAsync(Scope)).Revision == before, "Repeated source adoption must not write or enumerate");
        measured.Reset();
        var page = await owner.ExecuteAsync("music.library.detail", new { id = "playlist:1", offset = 100, limit = 25 });
        Check(page.GetProperty("ok").GetBoolean() && page.GetProperty("songs").GetArrayLength() == 25 &&
            measured.SongReads == 25 && measured.Enumerations == 0 && measured.Candidates == 0 && measured.Commits == 0,
            "Pagination must read only displayed songs");
        measured.Reset();
        await owner.ArtworkLoadCompletedAsync("https://images.example.test/success.png", true);
        Check(measured.SongReads == 0 && measured.SourceReads == 0 && measured.Commits == 0,
            "Successful image loading must not maintain the song repository");
        var token = await owner.ReadLibrarySyncAsync();
        Check(!token.Library.TryGetProperty("songs", out _), "Sync checkpoint must contain controls only");
        measured.Reset();
        _ = await owner.CommitLibrarySyncAsync(token, token.Library, token.State);
        Check(measured.SongReads == 0 && measured.ReferenceChecks == 0 && measured.Enumerations == 0 && measured.LastChangedSongs == 0,
            "Control-only sync must not materialize metadata or recheck already validated song relationships");
        measured.Reset();
        var updated = songs.Take(3).Select(song =>
        {
            var value = song.DeepClone().AsObject(); value["album"] = "Changed album"; return value;
        }).ToArray();
        _ = await owner.AdoptPlatformResultAsync(JsonSerializer.SerializeToElement(new { items = updated }), default);
        Check(measured.Commits == 1 && measured.LastChangedSongs == 3 && measured.SongReads < 30 &&
            measured.Enumerations == 0 && measured.Candidates == 0, "Small metadata updates must stay record-sized");
        Console.WriteLine($"MUSIC_LIBRARY_SCALE songs=10000 sources=50000 pageSongReads=25 repeatedWrites=0 updateSongs={measured.LastChangedSongs} updateSongReads={measured.SongReads} enumerations={measured.Enumerations} candidates={measured.Candidates}");
    }

    private sealed class MeasuredStore(MusicLibraryDatabase database) : IMusicLibraryStore
    {
        internal int SongReads, ReferenceChecks, SourceReads, Candidates, Enumerations, Commits, LastChangedSongs;
        internal void Reset() => SongReads = ReferenceChecks = SourceReads = Candidates = Enumerations = Commits = LastChangedSongs = 0;
        public Task<MusicLibrarySnapshot> ReadAsync(string scope, CancellationToken token = default) => database.ReadAsync(scope, token);
        public JsonObject? ReadSong(string id) { SongReads++; return database.ReadSong(id); }
        public bool ContainsSong(string id) { ReferenceChecks++; return database.ContainsSong(id); }
        public string? FindSource(string key) { SourceReads++; return database.FindSource(key); }
        public string? FindLocalFile(string id) => database.FindLocalFile(id);
        public IReadOnlyList<string> FindCandidates(string key, double duration) { Candidates++; return database.FindCandidates(key, duration); }
        public IReadOnlyList<string> FindArtwork(string address) => database.FindArtwork(address);
        public IReadOnlyList<string> FindQuery(string query) => database.FindQuery(query);
        public IReadOnlyList<string> SongIds() { Enumerations++; return database.SongIds(); }
        public Task<MusicLibraryRevision> CommitAsync(string scope, MusicLibraryRevision expected,
            MusicLibraryRevision next, MusicLibraryChanges changes, CancellationToken token = default)
        { Commits++; LastChangedSongs = changes.Songs.Count; return database.CommitAsync(scope, expected, next, changes, token); }
        public ValueTask DisposeAsync() => database.DisposeAsync();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string fileName, string existingFileName, IntPtr security);
}
