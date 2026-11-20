using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static partial class MusicNativeLibraryTests
{
    public static IEnumerable<TestCase> OnlineSettingsCases()
    {
        yield return new("Native music online settings missing area stays absent on reads and reconciliation", OnlineSettingsMissingArea);
        yield return new("Native music online settings current reads preserve active library state across reopen", OnlineSettingsCurrentState);
        yield return new("Native music online settings missing identity values reject writes while lyrics can be overwritten", OnlineSettingsMissingRequired);
        yield return new("Native music online settings ignore unrelated damaged areas until selected", OnlineSettingsInvalidDocument);
        yield return new("Native music online settings current schema consumes extras and rejects unusable current values", OnlineSettingsInvalidCurrent);
        yield return new("Native music online settings save before commit failure settles without replay", () => OnlineSettingsUnknownCommit(false));
        yield return new("Native music online settings save after commit failure settles without replay", () => OnlineSettingsUnknownCommit(true));
        yield return new("Native music online settings save revision conflict preserves the competing records and pending attempt", () => OnlineSettingsConflict(true));
        yield return new("Native music online settings save unknown commit rejects a third state without rewriting it", () => OnlineSettingsConflict(false));
        yield return new("Native music online settings save cancelled during initial read remains retryable", OnlineSettingsSaveCancelRead);
        yield return new("Native music online settings save cancellation before and after admission preserves the real result", OnlineSettingsSaveCancellation);
    }

    private static JsonObject CurrentOnlineSettings(bool lyrics = false, long revision = 7) =>
        O(new { schema = 1, revision, lyrics });

    private static async Task<byte[]> OnlineSettingsDocument(JsonObject settings)
    {
        var current = await CurrentDocuments();
        return Document(("online-settings", settings),
            ("library", current.Library), ("queue", current.Queue), ("history", current.History), ("songs", current.Songs),
            ("native-official-sources", O(new { schema = 1, revision = 4, enabled = new[] { "kg" } })));
    }

    private static void CheckOnlineSettings(JsonElement settings, long revision, bool lyrics)
    {
        Check(settings.GetProperty("schema").GetDouble() == 1 &&
            settings.GetProperty("revision").GetInt64() == revision &&
            settings.GetProperty("lyrics").GetBoolean() == lyrics);
    }

    private static void CheckOtherOnlineSettingsAreas(byte[] before, Store store)
    {
        var original = JsonNode.Parse(before)!.AsObject();
        var actual = store.Root;
        var oldAreas = original["areas"]!.AsObject(); var newAreas = actual["areas"]!.AsObject();
        Check(oldAreas.Count == newAreas.Count);
        foreach (var pair in oldAreas)
            if (pair.Key != "online-settings") Check(JsonNode.DeepEquals(pair.Value, newAreas[pair.Key]), "online settings changed another area");
        Check(JsonNode.DeepEquals(original["songs"], actual["songs"]), "online settings changed song records");
    }

    private static async Task OnlineSettingsMissingArea()
    {
        await using var f = new Fixture();
        var current = await CurrentDocuments();
        f.Store.Bytes = Document(("library", current.Library), ("songs", current.Songs));
        byte[] before = f.Store.Bytes.ToArray();
        var owner = f.Owner();
        var fresh = (await Ok(owner, "music.settings.get")).GetProperty("settings");

        CheckOnlineSettings(fresh, 0, true);
        await owner.ReconcileAsync();
        CheckOnlineSettings((await Ok(owner, "music.settings.get")).GetProperty("settings"), 0, true);
        await owner.DisposeAsync();
        CheckOnlineSettings((await Ok(f.Owner(), "music.settings.get")).GetProperty("settings"), 0, true);
        Check(f.Store.WriteCalls == 0 && before.SequenceEqual(f.Store.Bytes!));
    }

    private static async Task OnlineSettingsCurrentState()
    {
        foreach (var (lyrics, revision) in new (bool, long)[]
        {
            (true, 0),
            (false, 7),
            (false, 9007199254740991)
        })
        {
            await using var f = new Fixture();
            f.Store.Bytes = await OnlineSettingsDocument(CurrentOnlineSettings(lyrics, revision));
            byte[] before = f.Store.Bytes.ToArray();
            var owner = f.Owner();

            await owner.ReconcileAsync();
            CheckOnlineSettings((await Ok(owner, "music.settings.get")).GetProperty("settings"), revision, lyrics);
            CheckOtherOnlineSettingsAreas(before, f.Store);
            CheckOnlineSettings(E(f.Store.State("online-settings")), revision, lyrics);
            Check((await Ok(owner, "music.library.detail", new { id = "favorites" }))
                .GetProperty("songs")[0].GetProperty("localFileId").GetString() == "library:local:a");
            Check((await Ok(owner, "music.queue.get")).GetProperty("currentId").GetString() == "entry-1");
            Check((await Ok(owner, "music.officialSources.settings.get")).GetProperty("settings")
                .GetProperty("enabled").EnumerateArray().Select(p => p.GetString()).SequenceEqual(new[] { "kg" }));
            Check(f.Store.WriteCalls == 0 && f.Store.Writes == 0 && before.SequenceEqual(f.Store.Bytes!));

            await owner.ReconcileAsync();
            await owner.DisposeAsync();
            var reopened = f.Owner();
            CheckOnlineSettings((await Ok(reopened, "music.settings.get")).GetProperty("settings"), revision, lyrics);
            Check(f.Store.WriteCalls == 0 && before.SequenceEqual(f.Store.Bytes!));
            if (revision < 9007199254740991)
            {
                CheckOnlineSettings((await Ok(reopened, "music.settings.save", new { ifRevision = revision, lyrics = !lyrics }))
                    .GetProperty("settings"), revision + 1, !lyrics);
                CheckOtherOnlineSettingsAreas(before, f.Store);
                Check(f.Store.WriteCalls == 1);
            }
        }
    }

    private static async Task RejectOnlineSettingsDocument(byte[] bytes, string code = "CORRUPT_STORAGE")
    {
        await using var f = new Fixture();
        f.Store.Bytes = bytes.ToArray();
        var durable = f.Store.Bytes!.ToArray();
        var owner = f.Owner();
        await Error(owner, "music.settings.get", null, code);
        var settings = JsonNode.Parse(bytes)!["areas"]!["online-settings"]!.AsObject();
        bool replaceable = settings["schema"] is JsonValue schema && schema.ToJsonString() == "1" &&
            settings["revision"] is JsonValue rev && rev.ToJsonString() == "7";
        if (replaceable)
        {
            CheckOnlineSettings((await Ok(owner, "music.settings.save", new { ifRevision = 7, lyrics = true })).GetProperty("settings"), 8, true);
            Check(f.Store.WriteCalls == 1);
        }
        else
        {
            await Error(owner, "music.settings.save", new { ifRevision = 7, lyrics = true }, code);
            await owner.ReconcileAsync();
            await Error(owner, "music.settings.get", null, code);
            Check(f.Store.WriteCalls == 0 && durable.SequenceEqual(f.Store.Bytes!));
        }
    }

    private static async Task OnlineSettingsMissingRequired()
    {
        foreach (string field in new[] { "schema", "revision", "lyrics" })
        {
            var settings = CurrentOnlineSettings(); settings.Remove(field);
            await RejectOnlineSettingsDocument(await OnlineSettingsDocument(settings));
        }
    }

    private static async Task OnlineSettingsInvalidDocument()
    {
        foreach (var (area, code) in new (string, string)[]
        {
            ("library", "CORRUPT_LIBRARY"),
            ("native-official-sources", "CORRUPT_STORAGE")
        })
        {
            var doc = JsonNode.Parse(await OnlineSettingsDocument(CurrentOnlineSettings()))!.AsObject();
            var invalid = doc["areas"]![area]!.DeepClone().AsObject();
            invalid["schema"] = 2;
            doc["areas"]![area] = invalid;
            await using var f = new Fixture();
            f.Store.Bytes = JsonSerializer.SerializeToUtf8Bytes(doc, PersistedJson);
            var owner = f.Owner();
            CheckOnlineSettings((await Ok(owner, "music.settings.get")).GetProperty("settings"), 7, false);
            await Ok(owner, "music.settings.save", new { ifRevision = 7, lyrics = true });
            Check(JsonNode.DeepEquals(f.Store.Root["areas"]![area], invalid));
            await Error(owner, area == "library" ? "music.library.list" : "music.officialSources.settings.get", null, code);
        }
    }

    private static async Task OnlineSettingsInvalidCurrent()
    {
        foreach (var (field, value) in new (string, JsonNode?)[]
        {
            ("schema", JsonValue.Create(0)), ("schema", JsonValue.Create(2)), ("schema", JsonValue.Create(3)),
            ("lyrics", null), ("revision", JsonValue.Create(-1))
        })
        {
            var settings = CurrentOnlineSettings();
            settings[field] = value;
            await RejectOnlineSettingsDocument(await OnlineSettingsDocument(settings));
        }
        foreach (string field in new[] { "schema", "revision", "lyrics" })
        {
            var settings = CurrentOnlineSettings();
            settings.Remove(field);
            await RejectOnlineSettingsDocument(await OnlineSettingsDocument(settings));
        }
        foreach (var (field, value) in new (string, JsonNode?)[]
        {
            ("schema", JsonValue.Create("1")), ("lyrics", JsonValue.Create("false")),
            ("revision", JsonValue.Create(1.5)), ("revision", JsonValue.Create("7"))
        })
        {
            await using var f = new Fixture();
            f.Store.Bytes = await OnlineSettingsDocument(CurrentOnlineSettings());
            var before = f.Store.Bytes!.ToArray();
            var invalid = CurrentOnlineSettings(); invalid[field] = value;
            try
            {
                f.Store.SetArea("online-settings", invalid);
                throw new InvalidOperationException("Invalid typed value was committed.");
            }
            catch (DocumentStoreException error) when (error.Error == DocumentStoreError.InvalidRequest) { }
            Check(before.SequenceEqual(f.Store.Bytes!) && f.Store.WriteCalls == 0,
                "typed storage rejects unrepresentable values without changing committed records");
            CheckOnlineSettings((await Ok(f.Owner(), "music.settings.get")).GetProperty("settings"), 7, false);
        }
        foreach (string schema in new[] { "1.0", "1e0" })
        {
            await using var f = new Fixture();
            var extended = CurrentOnlineSettings(); extended["target"] = null;
            extended["extra"] = new JsonObject { ["nested"] = true };
            f.Store.SetAreaJson("online-settings", extended.ToJsonString()
                .Replace("\"schema\":1", "\"schema\":" + schema, StringComparison.Ordinal));
            var owner = f.Owner();
            CheckOnlineSettings((await Ok(owner, "music.settings.get")).GetProperty("settings"), 7, false);
            Check(f.Store.WriteCalls == 0);
            await Ok(owner, "music.settings.save", new { ifRevision = 7, lyrics = true, unused = new { value = 1 } });
            var saved = f.Store.State("online-settings");
            CheckOnlineSettings(E(saved), 8, true);
            Check(!saved.ContainsKey("extra") && !saved.ContainsKey("target"),
                "unused API fields do not create a second persistent settings shape");
            Check(f.Store.WriteCalls == 1);
        }
    }

    private static async Task OnlineSettingsUnknownCommit(bool committed)
    {
        await using var f = new Fixture();
        f.Store.Bytes = await OnlineSettingsDocument(CurrentOnlineSettings());
        byte[] before = f.Store.Bytes.ToArray();
        var owner = f.Owner();
        f.Store.Failure = committed ? 2 : 1;
        await Error(owner, "music.settings.save", new { ifRevision = 7, lyrics = true }, "COMMIT_UNKNOWN");
        byte[] durable = f.Store.Bytes!.ToArray();
        Check(f.Store.WriteCalls == 1 && f.Store.Writes == 1);
        Check(committed || before.SequenceEqual(durable));
        await Error(owner, "music.settings.get", null, "PENDING_WRITE");
        await Error(owner, "music.settings.save", new { ifRevision = 7, lyrics = true }, "PENDING_WRITE");
        await Error(owner, "music.library.create", new { name = "Blocked" }, "PENDING_WRITE");
        await Throws("COMMIT_UNKNOWN", () => owner.PrepareShutdownAsync());
        f.Store.FailRead = true;
        bool readFailed = false;
        try { await owner.ReconcileAsync(); }
        catch (DocumentStoreException error) when (error.Error == DocumentStoreError.Storage) { readFailed = true; }
        Check(readFailed);
        f.Store.FailRead = false;
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel();
            bool cancelled = false;
            try { await owner.ReconcileAsync(cancel.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled);
        }
        await Error(owner, "music.settings.get", null, "PENDING_WRITE");
        Check(f.Store.WriteCalls == 1 && durable.SequenceEqual(f.Store.Bytes!));
        await owner.ReconcileAsync();
        await owner.ReconcileAsync();
        Check(f.Store.WriteCalls == 1 && durable.SequenceEqual(f.Store.Bytes!));
        Check(f.Store.State("online-settings")["schema"]!.GetValue<long>() == 1);
        Check(f.Store.State("online-settings")["revision"]!.GetValue<long>() == (committed ? 8 : 7));
        Check(f.Store.State("online-settings")["lyrics"]!.GetValue<bool>() == committed);

        long revision = committed ? 8 : 7;
        CheckOnlineSettings((await Ok(owner, "music.settings.get")).GetProperty("settings"), revision, committed);
        Check(f.Store.WriteCalls == 1);
        CheckOtherOnlineSettingsAreas(before, f.Store);
        CheckOnlineSettings((await Ok(owner, "music.settings.save", new { ifRevision = revision, lyrics = true }))
            .GetProperty("settings"), revision + 1, true);
        Check(f.Store.WriteCalls == 2);
        await owner.DisposeAsync();
        CheckOnlineSettings((await Ok(f.Owner(), "music.settings.get")).GetProperty("settings"), revision + 1, true);
        Check(f.Store.WriteCalls == 2);
    }

    private static async Task OnlineSettingsConflict(bool conflictAtCommit)
    {
        await using var f = new Fixture();
        f.Store.Bytes = await OnlineSettingsDocument(CurrentOnlineSettings());
        var owner = f.Owner(); await Ok(owner, "music.settings.get");
        if (!conflictAtCommit)
        {
            f.Store.Failure = 2;
            await Error(owner, "music.settings.save", new { ifRevision = 7, lyrics = true }, "COMMIT_UNKNOWN");
        }
        var competitor = f.Owner();
        await Ok(competitor, "music.officialSources.settings.save", new { ifRevision = 4, enabled = Array.Empty<string>() });
        byte[] durable = f.Store.Bytes!.ToArray();
        if (conflictAtCommit)
            await Error(owner, "music.settings.save", new { ifRevision = 7, lyrics = true }, "STORAGE_CONFLICT");
        int calls = f.Store.WriteCalls;
        await Throws("STORAGE_CONFLICT", () => owner.ReconcileAsync());
        await Throws("STORAGE_CONFLICT", () => owner.ReconcileAsync());
        string blocked = conflictAtCommit ? "STORAGE_CONFLICT" : "PENDING_WRITE";
        await Error(owner, "music.settings.get", null, blocked);
        await Error(owner, "music.settings.save", new { ifRevision = 7, lyrics = true }, blocked);
        await Throws(conflictAtCommit ? "STORAGE_CONFLICT" : "COMMIT_UNKNOWN", () => owner.PrepareShutdownAsync());
        Check(f.Store.WriteCalls == calls && durable.SequenceEqual(f.Store.Bytes!), "foreign receipt must remain fenced without replay");
        CheckOnlineSettings((await Ok(competitor, "music.settings.get")).GetProperty("settings"), conflictAtCommit ? 7 : 8, !conflictAtCommit);
    }

    private static async Task OnlineSettingsSaveCancelRead()
    {
        await using var f = new Fixture();
        f.Store.Bytes = await OnlineSettingsDocument(CurrentOnlineSettings());
        byte[] before = f.Store.Bytes.ToArray();
        using var cancel = new CancellationTokenSource();
        f.Store.AfterRead = () => { f.Store.AfterRead = null; cancel.Cancel(); };
        var owner = f.Owner();
        var result = await owner.ExecuteAsync("music.settings.save", new { ifRevision = 7, lyrics = true }, cancel.Token);
        Check(!result.GetProperty("ok").GetBoolean() && result.GetProperty("code").GetString() == "CANCELLED");
        Check(f.Store.WriteCalls == 0 && before.SequenceEqual(f.Store.Bytes!));
        CheckOnlineSettings((await Ok(owner, "music.settings.get")).GetProperty("settings"), 7, false);
        Check(f.Store.WriteCalls == 0 && before.SequenceEqual(f.Store.Bytes!));
        CheckOnlineSettings((await Ok(owner, "music.settings.save", new { ifRevision = 7, lyrics = true }))
            .GetProperty("settings"), 8, true);
        Check(f.Store.WriteCalls == 1);
        CheckOtherOnlineSettingsAreas(before, f.Store);
    }

    private static async Task OnlineSettingsSaveCancellation()
    {
        await using var f = new Fixture();
        var owner = f.Owner();
        using (var before = new CancellationTokenSource())
        {
            before.Cancel();
            var cancelled = await owner.ExecuteAsync("music.settings.save", new { ifRevision = 0, lyrics = false }, before.Token);
            Check(!cancelled.GetProperty("ok").GetBoolean() && cancelled.GetProperty("code").GetString() == "CANCELLED");
            Check(f.Store.WriteCalls == 0 && f.Store.Bytes is null);
        }
        f.Store.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Store.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();
        var saving = owner.ExecuteAsync("music.settings.save", new { ifRevision = 0, lyrics = false }, cancel.Token);
        await f.Store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancel.Cancel();
        f.Store.Release.TrySetResult();
        var result = await saving;
        Check(result.GetProperty("ok").GetBoolean());
        CheckOnlineSettings(result.GetProperty("settings"), 1, false);
        CheckOnlineSettings(E(f.Store.State("online-settings")), 1, false);
        Check(f.Store.WriteCalls == 1);
    }
}
