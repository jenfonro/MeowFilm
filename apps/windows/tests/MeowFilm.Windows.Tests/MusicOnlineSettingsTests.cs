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
        yield return new("Native music online settings initial reconciliation observes legacy bytes without conversion", OnlineSettingsInitialReconcile);
        yield return new("Native music online settings schema two conversion preserves all unrelated state and content", OnlineSettingsConversion);
        yield return new("Native music online settings conversion validates the complete legacy shape and reference", OnlineSettingsInvalidLegacy);
        yield return new("Native music online settings conversion rejects damaged neighboring areas and installation schema three", OnlineSettingsInvalidDocument);
        yield return new("Native music online settings current schema rejects extra fields and unknown versions", OnlineSettingsInvalidCurrent);
        yield return new("Native music online settings conversion before commit failure settles without replay", () => OnlineSettingsUnknownCommit(false, true));
        yield return new("Native music online settings conversion after commit failure settles without replay", () => OnlineSettingsUnknownCommit(true, true));
        yield return new("Native music online settings save before commit failure settles without replay", () => OnlineSettingsUnknownCommit(false, false));
        yield return new("Native music online settings save after commit failure settles without replay", () => OnlineSettingsUnknownCommit(true, false));
        yield return new("Native music online settings conversion CAS conflict preserves the competing document and pending attempt", () => OnlineSettingsConflict(true));
        yield return new("Native music online settings conversion unknown commit rejects a third state without rewriting it", () => OnlineSettingsConflict(false));
        yield return new("Native music online settings conversion cancelled before admission remains retryable", OnlineSettingsConversionCancelAdmission);
        yield return new("Native music online settings conversion admitted before cancellation remains durable", OnlineSettingsConversionCancelCommit);
        yield return new("Native music online settings save cancellation before and after admission preserves the real result", OnlineSettingsSaveCancellation);
    }

    private static JsonObject LegacyOnlineSettings(JsonNode? target = null, bool lyrics = false, long revision = 7) =>
        O(new { schema = 2, revision, lyrics, target });

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
                    "Conversion changed another area: " + key);
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

    private static async Task OnlineSettingsInitialReconcile()
    {
        await using var f = new Fixture();
        f.Store.Bytes = OnlineSettingsDocument(LegacyOnlineSettings());
        byte[] before = f.Store.Bytes.ToArray();
        var owner = f.Owner();
        await owner.ReconcileAsync();
        await owner.ReconcileAsync();
        Check(f.Store.WriteCalls == 0 && before.SequenceEqual(f.Store.Bytes!));
        CheckOnlineSettings((await Ok(owner, "music.settings.get")).GetProperty("settings"), 7, false);
        Check(f.Store.WriteCalls == 1);
        CheckOtherOnlineSettingsAreas(before, f.Store);
    }

    private static async Task OnlineSettingsConversion()
    {
        foreach (var (targetId, platform, lyrics, revision) in new (string?, string, bool, long)[]
        {
            (null, "", true, 0),
            ("test-install-a", "wy", false, 7),
            ("test-install-b", "local", false, 9007199254740991)
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
            f.Store.Seed(ContentKey(current.Sha256, 0), currentBytes);
            f.Store.Seed(ContentKey(previous.Sha256, 0), previousBytes);
            var target = targetId is null ? null : O(new { installationId = targetId, platform });
            var original = JsonNode.Parse(OnlineSettingsDocument(LegacyOnlineSettings(target, lyrics, revision)))!.AsObject();
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

            // A first read through a different owner port must also complete the conversion.
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
            var currentContent = await owner.ReadScriptContentAsync(current.Sha256, current.Length);
            var previousContent = await owner.ReadScriptContentAsync(previous.Sha256, previous.Length);
            Check(currentBytes.SequenceEqual(currentContent));
            Check(previousBytes.SequenceEqual(previousContent));
            Check(currentBytes.SequenceEqual(f.Store.Document(ContentKey(current.Sha256, 0))));
            Check(previousBytes.SequenceEqual(f.Store.Document(ContentKey(previous.Sha256, 0))));
            Check(f.Store.WriteCalls == 1 && f.Store.Writes == 1);
            byte[] converted = f.Store.Bytes!.ToArray();

            await owner.ReconcileAsync();
            await owner.DisposeAsync();
            var reopened = f.Owner();
            CheckOnlineSettings((await Ok(reopened, "music.settings.get")).GetProperty("settings"), revision, lyrics);
            Check((await reopened.ReadSourceMetadataAsync()).Sha256 == metadata.Sha256);
            Check(f.Store.WriteCalls == 1 && converted.SequenceEqual(f.Store.Bytes!));
            if (revision < 9007199254740991)
            {
                CheckOnlineSettings((await Ok(reopened, "music.settings.save", new { ifRevision = revision, lyrics = !lyrics }))
                    .GetProperty("settings"), revision + 1, !lyrics);
                CheckOtherOnlineSettingsAreas(before, f.Store);
                Check(f.Store.WriteCalls == 2);
            }
        }
        await using var empty = new Fixture();
        empty.Store.Bytes = Document(("online-settings", LegacyOnlineSettings(lyrics: true, revision: 0)));
        var convertedSettings = (await Ok(empty.Owner(), "music.settings.get")).GetProperty("settings");
        CheckOnlineSettings(convertedSettings, 0, true);
        Check(empty.Store.Root["entries"]!.AsArray().Count == 1 &&
            empty.Store.State("online-settings").ToJsonString(PersistedJson) == convertedSettings.GetRawText());
        Check(empty.Store.WriteCalls == 1);
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

    private static async Task OnlineSettingsInvalidLegacy()
    {
        var original = LegacyOnlineSettings(O(new { installationId = "test-install-a", platform = "wy" }));
        foreach (string field in new[] { "schema", "revision", "lyrics", "target" })
        {
            var settings = (JsonObject)original.DeepClone();
            settings.Remove(field);
            await RejectOnlineSettingsDocument(OnlineSettingsDocument(settings));
        }
        foreach (var (field, value) in new (string, JsonNode?)[]
        {
            ("schema", JsonValue.Create(1)), ("schema", JsonValue.Create(4)), ("schema", JsonValue.Create("2")),
            ("revision", JsonValue.Create(-1)), ("revision", JsonValue.Create(1.5)), ("revision", JsonValue.Create("7")),
            ("lyrics", null), ("lyrics", JsonValue.Create("false")), ("extra", JsonValue.Create(true)),
            ("target", JsonValue.Create(false)), ("target", new JsonArray()), ("target", new JsonObject()),
            ("target", O(new { installationId = "test-install-a" })),
            ("target", O(new { platform = "wy" })),
            ("target", O(new { installationId = "../invalid", platform = "wy" })),
            ("target", O(new { installationId = "missing", platform = "wy" })),
            ("target", O(new { installationId = "test-install-a", platform = "unknown" })),
            ("target", O(new { installationId = "test-install-a", platform = (string?)null })),
            ("target", O(new { installationId = 1, platform = "wy" })),
            ("target", O(new { installationId = "test-install-a", platform = "wy", extra = true }))
        })
        {
            var settings = (JsonObject)original.DeepClone();
            settings[field] = value?.DeepClone();
            await RejectOnlineSettingsDocument(OnlineSettingsDocument(settings));
        }
        byte[] valid = OnlineSettingsDocument(original);
        await RejectOnlineSettingsDocument(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(valid)
            .Replace("\"lyrics\":false", "\"lyrics\":false,\"lyrics\":false", StringComparison.Ordinal)));
        await RejectOnlineSettingsDocument(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(valid)
            .Replace("\"schema\":2", "\"schema\":2.0", StringComparison.Ordinal)));
        await RejectOnlineSettingsDocument(Document(("online-settings", original)));
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
            var doc = JsonNode.Parse(OnlineSettingsDocument(LegacyOnlineSettings()))!.AsObject();
            doc["entries"]!.AsArray().Single(e => e!["key"]!.GetValue<string>() == AreaKey(area))!["value"] = invalid;
            await RejectOnlineSettingsDocument(JsonSerializer.SerializeToUtf8Bytes(doc, PersistedJson), code);
        }
    }

    private static async Task OnlineSettingsInvalidCurrent()
    {
        foreach (var (field, value) in new (string, JsonNode?)[]
        {
            ("schema", JsonValue.Create(0)), ("schema", JsonValue.Create(4)), ("schema", JsonValue.Create("3")),
            ("target", null), ("target", O(new { installationId = "test-install-a", platform = "wy" })),
            ("extra", JsonValue.Create(true)), ("lyrics", null), ("revision", JsonValue.Create(-1))
        })
        {
            var settings = O(new { schema = 3, revision = 7, lyrics = false });
            settings[field] = value;
            await RejectOnlineSettingsDocument(OnlineSettingsDocument(settings));
        }
        foreach (string field in new[] { "schema", "revision", "lyrics" })
        {
            var settings = O(new { schema = 3, revision = 7, lyrics = false });
            settings.Remove(field);
            await RejectOnlineSettingsDocument(OnlineSettingsDocument(settings));
        }
    }

    private static async Task OnlineSettingsUnknownCommit(bool committed, bool converting)
    {
        await using var f = new Fixture();
        f.Store.Bytes = OnlineSettingsDocument(converting ? LegacyOnlineSettings() : O(new { schema = 3, revision = 7, lyrics = false }));
        byte[] before = f.Store.Bytes.ToArray();
        var owner = f.Owner();
        f.Store.Failure = committed ? 2 : 1;
        await Error(owner, converting ? "music.settings.get" : "music.settings.save",
            converting ? null : new { ifRevision = 7, lyrics = true }, "COMMIT_UNKNOWN");
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
        catch (ProtectedDocumentException error) when (error.Error == DocumentStoreError.Storage) { readFailed = true; }
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
        Check(f.Store.State("online-settings")["schema"]!.GetValue<int>() == (converting && !committed ? 2 : 3));
        Check(f.Store.State("online-settings")["revision"]!.GetValue<long>() == (!converting && committed ? 8 : 7));
        Check(f.Store.State("online-settings")["lyrics"]!.GetValue<bool>() == (!converting && committed));

        // Only this new call may retry a conversion observed at its before fingerprint.
        long revision = !converting && committed ? 8 : 7;
        CheckOnlineSettings((await Ok(owner, "music.settings.get")).GetProperty("settings"), revision, !converting && committed);
        int calls = converting && !committed ? 2 : 1;
        Check(f.Store.WriteCalls == calls);
        CheckOtherOnlineSettingsAreas(before, f.Store);
        CheckOnlineSettings((await Ok(owner, "music.settings.save", new { ifRevision = revision, lyrics = true }))
            .GetProperty("settings"), revision + 1, true);
        Check(f.Store.WriteCalls == calls + 1);
        await owner.DisposeAsync();
        CheckOnlineSettings((await Ok(f.Owner(), "music.settings.get")).GetProperty("settings"), revision + 1, true);
        Check(f.Store.WriteCalls == calls + 1);
    }

    private static async Task OnlineSettingsConflict(bool conflictAtCommit)
    {
        await using var f = new Fixture();
        f.Store.Bytes = OnlineSettingsDocument(LegacyOnlineSettings());
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
            var converting = owner.ExecuteAsync("music.settings.get");
            await f.Store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            f.Store.Bytes = competing.ToArray();
            f.Store.Release.TrySetResult();
            var failed = await converting;
            Check(!failed.GetProperty("ok").GetBoolean() && failed.GetProperty("code").GetString() == "STORAGE_CONFLICT");
            observed = before;
        }
        else
        {
            f.Store.Failure = 2;
            await Error(owner, "music.settings.get", null, "COMMIT_UNKNOWN");
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
        CheckOnlineSettings((await Ok(owner, "music.settings.get")).GetProperty("settings"), 7, false);
        Check(f.Store.WriteCalls == (conflictAtCommit ? 2 : 1));
        CheckOtherOnlineSettingsAreas(before, f.Store);
    }

    private sealed class OnlineSettingsReadCancellation(Store store, CancellationTokenSource cancel) : IProtectedDocumentStore
    {
        public async Task<ProtectedDocument> ReadAsync(string documentNamespace, CancellationToken cancellationToken = default)
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

    private static async Task OnlineSettingsConversionCancelAdmission()
    {
        await using var f = new Fixture();
        f.Store.Bytes = OnlineSettingsDocument(LegacyOnlineSettings());
        byte[] before = f.Store.Bytes.ToArray();
        using var cancel = new CancellationTokenSource();
        var owner = f.Owner(new OnlineSettingsReadCancellation(f.Store, cancel));
        var result = await owner.ExecuteAsync("music.settings.get", cancellationToken: cancel.Token);
        Check(!result.GetProperty("ok").GetBoolean() && result.GetProperty("code").GetString() == "CANCELLED");
        Check(f.Store.WriteCalls == 0 && before.SequenceEqual(f.Store.Bytes!));
        CheckOnlineSettings((await Ok(owner, "music.settings.get")).GetProperty("settings"), 7, false);
        Check(f.Store.WriteCalls == 1);
        CheckOtherOnlineSettingsAreas(before, f.Store);
    }

    private static async Task OnlineSettingsConversionCancelCommit()
    {
        await using var f = new Fixture();
        f.Store.Bytes = OnlineSettingsDocument(LegacyOnlineSettings());
        byte[] before = f.Store.Bytes.ToArray();
        var owner = f.Owner();
        f.Store.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Store.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();
        var converting = owner.ExecuteAsync("music.settings.get", cancellationToken: cancel.Token);
        await f.Store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancel.Cancel();
        f.Store.Release.TrySetResult();
        var result = await converting;
        Check(!result.GetProperty("ok").GetBoolean() && result.GetProperty("code").GetString() == "CANCELLED");
        CheckOnlineSettings(E(f.Store.State("online-settings")), 7, false);
        CheckOtherOnlineSettingsAreas(before, f.Store);
        byte[] converted = f.Store.Bytes!.ToArray();
        await owner.ReconcileAsync();
        CheckOnlineSettings((await Ok(owner, "music.settings.get")).GetProperty("settings"), 7, false);
        Check(f.Store.WriteCalls == 1 && converted.SequenceEqual(f.Store.Bytes!));
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
