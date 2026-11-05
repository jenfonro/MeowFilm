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
        yield return new("Native music online settings current reads preserve all state and content across reopen", OnlineSettingsCurrentState);
        yield return new("Native music online settings non-current schema fails every owner entry without writes", OnlineSettingsNonCurrent);
        yield return new("Native music online settings reject damaged neighboring areas and installation schema three", OnlineSettingsInvalidDocument);
        yield return new("Native music online settings current schema rejects extra fields and unknown versions", OnlineSettingsInvalidCurrent);
        yield return new("Native music online settings save before commit failure settles without replay", () => OnlineSettingsUnknownCommit(false));
        yield return new("Native music online settings save after commit failure settles without replay", () => OnlineSettingsUnknownCommit(true));
        yield return new("Native music online settings save CAS conflict preserves the competing document and pending attempt", () => OnlineSettingsConflict(true));
        yield return new("Native music online settings save unknown commit rejects a third state without rewriting it", () => OnlineSettingsConflict(false));
        yield return new("Native music online settings save cancelled during initial read remains retryable", OnlineSettingsSaveCancelRead);
        yield return new("Native music online settings save cancellation before and after admission preserves the real result", OnlineSettingsSaveCancellation);
    }

    private static JsonObject CurrentOnlineSettings(bool lyrics = false, long revision = 7) =>
        O(new { schema = 3, revision, lyrics });

    private static byte[] OnlineSettingsDocument(JsonObject settings) =>
        Document(("online-settings", settings), ("lx-installations", CurrentSources()),
            ("library", CurrentLibraryState()), ("queue", CurrentQueue()), ("history", CurrentHistory()),
            ("native-official-sources", O(new { schema = 1, revision = 4, enabled = new[] { "kg" } })),
            ("[\"lx-data\",\"test-install-a\",\"wy\"]", O(new
            {
                schema = 1, values = new[] { new { key = "keep", value = "private" } }
            })));

    private static void CheckOnlineSettings(JsonElement settings, long revision, bool lyrics)
    {
        Check(settings.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)
            .SequenceEqual(new[] { "lyrics", "revision", "schema" }));
        Check(settings.GetProperty("schema").GetInt32() == 3 &&
            settings.GetProperty("revision").GetInt64() == revision &&
            settings.GetProperty("lyrics").GetBoolean() == lyrics);
    }

    private static void CheckOtherOnlineSettingsAreas(byte[] before, Store store)
    {
        var original = JsonNode.Parse(before)!.AsObject();
        var actual = store.Root;
        Check(original["version"]!.GetValue<int>() == actual["version"]!.GetValue<int>());
        var oldEntries = original["entries"]!.AsArray();
        var newEntries = actual["entries"]!.AsArray();
        Check(oldEntries.Count == newEntries.Count);
        for (int index = 0; index < oldEntries.Count; index++)
        {
            string key = oldEntries[index]!["key"]!.GetValue<string>();
            Check(key == newEntries[index]!["key"]!.GetValue<string>());
            if (key != AreaKey("online-settings"))
                Check(oldEntries[index]!.ToJsonString(PersistedJson) == newEntries[index]!.ToJsonString(PersistedJson),
                    "Settings operation changed another area: " + key);
        }
    }

    private static async Task OnlineSettingsMissingArea()
    {
        await using var f = new Fixture();
        f.Store.Bytes = Document(("library", CurrentLibraryState()), ("lx-installations", CurrentSources()));
        byte[] before = f.Store.Bytes.ToArray();
        var owner = f.Owner();
        CheckOnlineSettings((await Ok(owner, "music.settings.get")).GetProperty("settings"), 0, true);
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
            byte[] currentBytes = Encoding.UTF8.GetBytes("// current content");
            byte[] previousBytes = Encoding.UTF8.GetBytes("// previous content");
            var current = Content(Encoding.UTF8.GetString(currentBytes));
            var previous = Content(Encoding.UTF8.GetString(previousBytes));
            var installed = CurrentSources();
            installed["revision"] = 9;
            installed["installations"] = new JsonArray(Installation(content: current), Installation("test-install-b", false));
            installed["installations"]![0]!["previous"] = Installation(content: previous)["current"]!.DeepClone();
            installed["installations"]![0]!["previous"]!["permissions"]!["storage"] = new JsonArray("read");
            f.Store.Seed(ContentKey(current.Sha256), currentBytes);
            f.Store.Seed(ContentKey(previous.Sha256), previousBytes);
            var original = JsonNode.Parse(OnlineSettingsDocument(CurrentOnlineSettings(lyrics, revision)))!.AsObject();
            var entries = original["entries"]!.AsArray();
            entries.Single(e => e!["key"]!.GetValue<string>() == AreaKey("lx-installations"))!["value"] = installed.DeepClone();
            entries.Add(O(new
            {
                key = AreaKey("[\"lx-data\",\"test-install-b\",\"tx\"]"),
                value = new { schema = 1, values = new[] { new { key = "keep", value = "disabled-private" } } }
            }));
            f.Store.Bytes = JsonSerializer.SerializeToUtf8Bytes(original, PersistedJson);
            byte[] before = f.Store.Bytes.ToArray();
            var owner = f.Owner();

            await owner.ReconcileAsync();
            var metadata = await owner.ReadSourceMetadataAsync();
            Check(JsonNode.DeepEquals(installed, Parsed(metadata.State)));
            CheckOnlineSettings((await Ok(owner, "music.settings.get")).GetProperty("settings"), revision, lyrics);
            CheckOtherOnlineSettingsAreas(before, f.Store);
            CheckOnlineSettings(E(f.Store.State("online-settings")), revision, lyrics);
            Check((await Ok(owner, "music.library.detail", new { id = "favorites" }))
                .GetProperty("songs")[0].GetProperty("songId").GetString() == "library:local:a");
            Check((await Ok(owner, "music.queue.get")).GetProperty("currentId").GetString() == "entry-1");
            Check((await Ok(owner, "music.officialSources.settings.get")).GetProperty("settings")
                .GetProperty("enabled").EnumerateArray().Select(p => p.GetString()).SequenceEqual(new[] { "kg" }));
            Check((await owner.ReadScriptValueAsync("test-install-a", "wy", "keep", 9)).GetString() == "private");
            Check((await owner.ReadSourceScriptDataAsync("test-install-b", 9)).Sites.GetProperty("tx")
                .GetProperty("values")[0].GetProperty("value").GetString() == "disabled-private");
            await Throws("PERMISSION_DENIED", async () => await owner.ReadScriptValueAsync("test-install-b", "tx", "keep", 9));
            var currentContent = await owner.ReadScriptContentAsync(current.Sha256);
            var previousContent = await owner.ReadScriptContentAsync(previous.Sha256);
            Check(currentBytes.SequenceEqual(currentContent));
            Check(previousBytes.SequenceEqual(previousContent));
            byte[] storedCurrent = (await f.Store.ReadAsync(ContentKey(current.Sha256))).Data!;
            byte[] storedPrevious = (await f.Store.ReadAsync(ContentKey(previous.Sha256))).Data!;
            Check(currentBytes.SequenceEqual(storedCurrent));
            Check(previousBytes.SequenceEqual(storedPrevious));
            Check(f.Store.WriteCalls == 0 && f.Store.Writes == 0 && before.SequenceEqual(f.Store.Bytes!));

            await owner.ReconcileAsync();
            await owner.DisposeAsync();
            var reopened = f.Owner();
            CheckOnlineSettings((await Ok(reopened, "music.settings.get")).GetProperty("settings"), revision, lyrics);
            Check((await reopened.ReadSourceMetadataAsync()).Sha256 == metadata.Sha256);
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
        var owner = f.Owner();
        await Error(owner, "music.settings.get", null, code);
        await Error(owner, "music.settings.save", new { ifRevision = 7, lyrics = true }, code);
        await Throws(code, async () => await owner.ReadSourceMetadataAsync());
        await Throws(code, () => owner.ReconcileAsync());
        Check(f.Store.WriteCalls == 0 && bytes.SequenceEqual(f.Store.Bytes!));
    }

    private static async Task OnlineSettingsNonCurrent()
    {
        foreach (JsonNode? target in new JsonNode?[] { null, O(new { installationId = "test-install-a", platform = "wy" }) })
            await RejectOnlineSettingsDocument(OnlineSettingsDocument(O(new { schema = 2, revision = 7, lyrics = false, target })));
    }

    private static async Task OnlineSettingsInvalidDocument()
    {
        foreach (var (area, invalid, code) in new (string, JsonObject, string)[]
        {
            ("lx-installations", O(new { schema = 3, revision = 1, installations = new[] { Installation() } }), "CORRUPT_STORAGE"),
            ("library", O(new { schema = 1, scope = "local", revision = 1, library = CurrentLibraryState()["library"] }), "CORRUPT_LIBRARY"),
            ("native-official-sources", O(new { schema = 2, revision = 4, enabled = new[] { "kg" } }), "CORRUPT_STORAGE"),
            ("[\"lx-data\",\"test-install-a\",\"wy\"]", O(new { schema = 2, values = Array.Empty<object>() }), "CORRUPT_STORAGE")
        })
        {
            var doc = JsonNode.Parse(OnlineSettingsDocument(CurrentOnlineSettings()))!.AsObject();
            doc["entries"]!.AsArray().Single(e => e!["key"]!.GetValue<string>() == AreaKey(area))!["value"] = invalid;
            await RejectOnlineSettingsDocument(JsonSerializer.SerializeToUtf8Bytes(doc, PersistedJson), code);
        }
    }

    private static async Task OnlineSettingsInvalidCurrent()
    {
        foreach (var (field, value) in new (string, JsonNode?)[]
        {
            ("schema", JsonValue.Create(0)), ("schema", JsonValue.Create(2)), ("schema", JsonValue.Create(4)), ("schema", JsonValue.Create("3")),
            ("target", null), ("target", O(new { installationId = "test-install-a", platform = "wy" })),
            ("extra", JsonValue.Create(true)), ("lyrics", null), ("lyrics", JsonValue.Create("false")),
            ("revision", JsonValue.Create(-1)), ("revision", JsonValue.Create(1.5)), ("revision", JsonValue.Create("7"))
        })
        {
            var settings = CurrentOnlineSettings();
            settings[field] = value;
            await RejectOnlineSettingsDocument(OnlineSettingsDocument(settings));
        }
        foreach (string field in new[] { "schema", "revision", "lyrics" })
        {
            var settings = CurrentOnlineSettings();
            settings.Remove(field);
            await RejectOnlineSettingsDocument(OnlineSettingsDocument(settings));
        }
        byte[] valid = OnlineSettingsDocument(CurrentOnlineSettings());
        await RejectOnlineSettingsDocument(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(valid)
            .Replace("\"lyrics\":false", "\"lyrics\":false,\"lyrics\":false", StringComparison.Ordinal)));
        await RejectOnlineSettingsDocument(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(valid)
            .Replace("\"schema\":3", "\"schema\":3.0", StringComparison.Ordinal)));
    }

    private static async Task OnlineSettingsUnknownCommit(bool committed)
    {
        await using var f = new Fixture();
        f.Store.Bytes = OnlineSettingsDocument(CurrentOnlineSettings());
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
        await Throws("COMMIT_UNKNOWN", async () => await owner.ReadSourceMetadataAsync());
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
        Check(f.Store.State("online-settings")["schema"]!.GetValue<int>() == 3);
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
        f.Store.Bytes = OnlineSettingsDocument(CurrentOnlineSettings());
        byte[] before = f.Store.Bytes.ToArray();
        var competingDocument = JsonNode.Parse(before)!.AsObject();
        competingDocument["entries"]!.AsArray()
            .Single(e => e!["key"]!.GetValue<string>() == AreaKey("native-official-sources"))!["value"] =
            O(new { schema = 1, revision = 5, enabled = Array.Empty<string>() });
        byte[] competing = JsonSerializer.SerializeToUtf8Bytes(competingDocument, PersistedJson);
        var owner = f.Owner();
        byte[] observed;
        if (conflictAtCommit)
        {
            f.Store.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Store.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var saving = owner.ExecuteAsync("music.settings.save", new { ifRevision = 7, lyrics = true });
            await f.Store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            f.Store.Bytes = competing.ToArray();
            f.Store.Release.TrySetResult();
            var failed = await saving;
            Check(!failed.GetProperty("ok").GetBoolean() && failed.GetProperty("code").GetString() == "STORAGE_CONFLICT");
            observed = before;
        }
        else
        {
            f.Store.Failure = 2;
            await Error(owner, "music.settings.save", new { ifRevision = 7, lyrics = true }, "COMMIT_UNKNOWN");
            observed = f.Store.Bytes!.ToArray();
            f.Store.Bytes = competing.ToArray();
        }
        string blocked = conflictAtCommit ? "STORAGE_CONFLICT" : "PENDING_WRITE";
        await Throws("STORAGE_CONFLICT", () => owner.ReconcileAsync());
        await Throws("STORAGE_CONFLICT", () => owner.ReconcileAsync());
        await Error(owner, "music.settings.get", null, blocked);
        await Error(owner, "music.settings.save", new { ifRevision = 7, lyrics = true }, blocked);
        await Throws(conflictAtCommit ? "STORAGE_CONFLICT" : "COMMIT_UNKNOWN", () => owner.PrepareShutdownAsync());
        Check(f.Store.WriteCalls == 1 && competing.SequenceEqual(f.Store.Bytes!));

        // Restoring an original CAS side must still settle the original pending attempt.
        f.Store.Bytes = observed.ToArray();
        await owner.ReconcileAsync();
        Check(f.Store.WriteCalls == 1 && observed.SequenceEqual(f.Store.Bytes!));
        CheckOnlineSettings((await Ok(owner, "music.settings.get")).GetProperty("settings"), conflictAtCommit ? 7 : 8, !conflictAtCommit);
        Check(f.Store.WriteCalls == 1);
        CheckOtherOnlineSettingsAreas(before, f.Store);
    }

    private sealed class OnlineSettingsReadCancellation(Store store, CancellationTokenSource cancel) : IDocumentStore
    {
        public async Task<DocumentSnapshot> ReadAsync(string documentNamespace, CancellationToken cancellationToken = default)
        {
            var document = await store.ReadAsync(documentNamespace, cancellationToken);
            cancel.Cancel();
            return document;
        }
        public Task<DocumentFingerprint> WriteAsync(string documentNamespace, ReadOnlyMemory<byte> bytes,
            DocumentFingerprint expected, CancellationToken cancellationToken = default) =>
            store.WriteAsync(documentNamespace, bytes, expected, cancellationToken);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task OnlineSettingsSaveCancelRead()
    {
        await using var f = new Fixture();
        f.Store.Bytes = OnlineSettingsDocument(CurrentOnlineSettings());
        byte[] before = f.Store.Bytes.ToArray();
        using var cancel = new CancellationTokenSource();
        var owner = f.Owner(new OnlineSettingsReadCancellation(f.Store, cancel));
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
