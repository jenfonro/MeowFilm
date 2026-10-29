using System.Text.Json;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Library;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static class MusicSearchPreferenceTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("original-five-platform-defaults-are-passive", async () =>
        {
            var store = new Store(); await using var owner = new MusicLibraryOwner(store);
            var settings = (await Call(owner, "music.search.settings.get")).GetProperty("settings");
            Check(settings.GetProperty("schema").GetInt32() == 2 && settings.GetProperty("revision").GetInt64() == 0);
            foreach (var kind in new[] { settings.GetProperty("songs"), settings.GetProperty("playlists") })
                Check(kind.GetProperty("mode").GetString() == "aggregate" &&
                    kind.GetProperty("platform").GetString() == "wy" &&
                    kind.GetProperty("order").EnumerateArray().Select(x => x.GetString()).SequenceEqual(Platforms) &&
                    kind.GetProperty("enabled").EnumerateArray().Select(x => x.GetString()).SequenceEqual(Platforms));
            Check((await Call(owner, "music.search.history.get")).GetProperty("history").GetProperty("items").GetArrayLength() == 0);
            Check(store.Writes == 0);
        });
        yield return Case("all-disabled-is-durable-not-reset-to-default", async () =>
        {
            var store = new Store(); await using (var owner = new MusicLibraryOwner(store))
                await Call(owner, "music.search.settings.save", Policy(0,
                    Kind(enabled: []), Kind(enabled: [])));
            await using var reopened = new MusicLibraryOwner(store);
            var value = (await Call(reopened, "music.search.settings.get")).GetProperty("settings");
            Check(value.GetProperty("songs").GetProperty("enabled").GetArrayLength() == 0 &&
                value.GetProperty("playlists").GetProperty("enabled").GetArrayLength() == 0 &&
                value.GetProperty("revision").GetInt64() == 1);
        });
        yield return Case("settings-revision-conflict-keeps-newer-policy", async () =>
        {
            var store = new Store(); await using var owner = new MusicLibraryOwner(store);
            await Call(owner, "music.search.settings.save", Policy(0,
                Kind(enabled: ["wy"]), Kind("single", "kg", enabled: ["kg"])));
            await Code(owner, "music.search.settings.save", Policy(0,
                Kind(enabled: ["tx"]), Kind("single", "tx", enabled: ["tx"])), "REVISION_CONFLICT");
            Check(store.Writes == 1);
            Check((await Call(owner, "music.search.settings.get")).GetProperty("settings")
                .GetProperty("songs").GetProperty("enabled")[0].GetString() == "wy");
        });
        foreach (var invalid in new[] {
            Policy(0, Kind(mode: "unknown"), Kind()),
            Policy(0, Kind(platform: "unknown"), Kind()),
            Policy(0, Kind(order: ["wy", "tx", "kw", "kg", "kg"]), Kind()),
            Policy(0, Kind(enabled: ["tx", "wy"]), Kind())
        })
            yield return Case("invalid-policy-" + JsonSerializer.Serialize(invalid), async () =>
            {
                var store = new Store(); await using var owner = new MusicLibraryOwner(store);
                await Code(owner, "music.search.settings.save", invalid, "INVALID_REQUEST");
                Check(store.Writes == 0);
            });
        yield return Case("history-deduplicates-trims-and-retains-sixty-on-cold-reopen", async () =>
        {
            var store = new Store();
            await using (var owner = new MusicLibraryOwner(store))
            {
                for (int i = 0; i < 65; i++) await Call(owner, "music.search.history.record",
                    new { text = "词" + i, kind = "songs", platform = "aggregate" });
                await Call(owner, "music.search.history.record", new { text = " 词20 ", kind = "playlists", platform = "wy" });
            }
            await using var reopened = new MusicLibraryOwner(store);
            var history = (await Call(reopened, "music.search.history.get")).GetProperty("history");
            var items = history.GetProperty("items").EnumerateArray().ToArray();
            Check(items.Length == 60 && items[0].GetProperty("text").GetString() == "词20" &&
                items[0].GetProperty("kind").GetString() == "playlists");
            Check(items.All(i => i.GetProperty("text").GetString() != "词0") &&
                items.Count(i => i.GetProperty("text").GetString() == "词20") == 1);
        });
        yield return Case("history-clear-requires-current-revision-and-explicit-confirmation", async () =>
        {
            var store = new Store(); await using var owner = new MusicLibraryOwner(store);
            await Call(owner, "music.search.history.record", new { text = "保留", kind = "local", platform = "local" });
            await Code(owner, "music.search.history.clear", new { ifRevision = 1, confirm = false }, "CONFIRMATION_REQUIRED");
            await Code(owner, "music.search.history.clear", new { ifRevision = 0, confirm = true }, "REVISION_CONFLICT");
            await Call(owner, "music.search.history.clear", new { ifRevision = 1, confirm = true });
            Check(store.Writes == 2 && (await Call(owner, "music.search.history.get"))
                .GetProperty("history").GetProperty("items").GetArrayLength() == 0);
        });
        yield return Case("history-has-one-clear-operation-and-rejects-per-row-removal", async () =>
        {
            var store = new Store(); await using var owner = new MusicLibraryOwner(store);
            foreach (string text in new[] { "A", "B" })
                await Call(owner, "music.search.history.record", new { text, kind = "songs", platform = "kg" });
            await Code(owner, "music.search.history.remove", new { text = "A", ifRevision = 2 }, "CAPABILITY_UNAVAILABLE");
            var items = (await Call(owner, "music.search.history.get")).GetProperty("history").GetProperty("items");
            Check(items.GetArrayLength() == 2 && items[0].GetProperty("text").GetString() == "B");
        });
        foreach (string text in new[] { "", " ", "bad\nvalue", "bad\u0085value", new string('x', 201) })
            yield return Case("invalid-history-text-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(text)))[..8], async () =>
            {
                var store = new Store(); await using var owner = new MusicLibraryOwner(store);
                await Code(owner, "music.search.history.record", new { text, kind = "songs", platform = "wy" }, "INVALID_REQUEST");
                Check(store.Writes == 0);
            });
        yield return Case("lost-policy-ack-is-resolved-by-the-next-current-read-without-replaying-write", async () =>
        {
            var store = new Store { Lose = true }; await using var owner = new MusicLibraryOwner(store);
            await Code(owner, "music.search.settings.save", Policy(0,
                Kind(enabled: []), Kind(enabled: [])), "COMMIT_UNKNOWN");
            Check((await Call(owner, "music.search.settings.get")).GetProperty("settings")
                .GetProperty("songs").GetProperty("enabled").GetArrayLength() == 0);
            Check(store.Writes == 1);
        });
        yield return Case("DPAPI-policy-and-history-share-existing-library-owner", async () =>
        {
            string root = System.IO.Path.Combine(Environment.GetEnvironmentVariable("MEOWFILM_TEST_DATA_ROOT")
                ?? throw new InvalidOperationException("Isolated root required"), "search-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(root);
            await using (var store = new DpapiDocumentStore(root, [MusicLibraryOwner.DocumentNamespace]))
            await using (var owner = new MusicLibraryOwner(store))
            {
                await Call(owner, "music.library.create", new { name = "保留歌单" });
                await Call(owner, "music.search.settings.save", Policy(0,
                    Kind(enabled: ["mg"]), Kind(enabled: ["kw"])));
                await Call(owner, "music.search.history.record", new { text = "本机", kind = "local", platform = "local" });
            }
            await using var reopened = new DpapiDocumentStore(root, [MusicLibraryOwner.DocumentNamespace]);
            await using var library = new MusicLibraryOwner(reopened);
            Check((await Call(library, "music.library.list")).GetProperty("items").GetArrayLength() == 2);
            Check((await Call(library, "music.search.settings.get")).GetProperty("settings")
                .GetProperty("songs").GetProperty("enabled")[0].GetString() == "mg");
            Check((await Call(library, "music.search.history.get")).GetProperty("history")
                .GetProperty("items")[0].GetProperty("text").GetString() == "本机");
        });
        foreach (var test in MusicUiTests.SearchPreferenceIntegrationCases()) yield return test;
    }

    private static TestCase Case(string name, Func<Task> run) => new("music.search-preferences." + name, run);
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Search preferences assertion"); }
    private static readonly string[] Platforms = ["wy", "tx", "kw", "kg", "mg"];
    private static object Kind(string mode = "aggregate", string platform = "wy",
        string[]? order = null, string[]? enabled = null) =>
        new { mode, platform, order = order ?? Platforms, enabled = enabled ?? Platforms };
    private static object Policy(long revision, object songs, object playlists) =>
        new { ifRevision = revision, songs, playlists };
    private static async Task<JsonElement> Call(MusicLibraryOwner owner, string method, object? input = null)
    {
        var result = await owner.ExecuteAsync(method, input);
        Check(result.GetProperty("ok").GetBoolean()); return result;
    }
    private static async Task Code(MusicLibraryOwner owner, string method, object? input, string code)
    {
        var result = await owner.ExecuteAsync(method, input);
        Check(!result.GetProperty("ok").GetBoolean() && result.GetProperty("code").GetString() == code);
    }
    private sealed class Store : IProtectedDocumentStore
    {
        private byte[]? bytes;
        internal bool Lose;
        internal int Writes;
        public Task<ProtectedDocument> ReadAsync(string ns, CancellationToken cancellationToken = default)
        {
            Check(ns == MusicLibraryOwner.DocumentNamespace); cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ProtectedDocument(bytes?.ToArray(), bytes is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(bytes)));
        }
        public Task<DocumentFingerprint> WriteAsync(string ns, ReadOnlyMemory<byte> value, DocumentFingerprint expected, CancellationToken cancellationToken = default)
        {
            Check(ns == MusicLibraryOwner.DocumentNamespace);
            var actual = bytes is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(bytes);
            if (actual != expected) throw new ProtectedDocumentException(DocumentStoreError.Conflict);
            Writes++; bytes = value.ToArray();
            if (Lose) { Lose = false; throw new ProtectedDocumentException(DocumentStoreError.Storage); }
            return Task.FromResult(DocumentFingerprint.FromBytes(bytes));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public static partial class MusicUiTests
{
    internal static IEnumerable<TestCase> SearchPreferenceIntegrationCases()
    {
        yield return Case("search history is lazy and repeated settings activation shares one read", async () =>
        {
            await using var f = await Fixture.Create();
            Equal(0, f.Calls.Count("music.search.history.get"));
            var hold = f.Calls.HoldNext("music.search.history.get");
            var first = f.Module.ActivateSettingsAsync();
            await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = f.Module.ActivateSettingsAsync();
            await Drain();
            Equal(1, f.Calls.Count("music.search.history.get"));
            hold.Release();
            await Task.WhenAll(first, second);
            Equal(1, f.Calls.Count("music.search.history.get"));
            await f.Module.DeactivateSettingsAsync();
            await f.Module.ActivateSettingsAsync();
            Equal(1, f.Calls.Count("music.search.history.get"));
            await f.Module.DeactivateSettingsAsync();
        });
        yield return Case("retired search history read drains before a new visible generation", async () =>
        {
            await using var f = await Fixture.Create();
            var hold = f.Calls.HoldNext("music.search.history.get");
            var first = f.Module.ActivateSettingsAsync();
            await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await f.Rpc("music.search.history.record",
                new { text = "当前快照", kind = "songs", platform = "aggregate" });
            var leaving = f.Module.DeactivateSettingsAsync();
            await Drain();
            True(!leaving.IsCompleted, "retirement waits for the physical read");
            var reopened = f.Module.ActivateSettingsAsync();
            await Drain();
            Equal(1, f.Calls.Count("music.search.history.get"));
            hold.Release();
            await Task.WhenAll(first, leaving, reopened);
            Equal(2, f.Calls.Count("music.search.history.get"));
            True(f.Module.HasSearchHistory, "retired empty snapshot cannot replace the current history");
            await f.Module.DeactivateSettingsAsync();
        });
        yield return Case("rapid settings unload and reopen has one account observation and no stale activation", async () =>
        {
            await using var f = await Fixture.Create();
            var hold = f.Calls.HoldNext("music.accounts.list");
            var first = f.Module.ActivateSettingsAsync();
            await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var leaving = f.Module.DeactivateSettingsAsync();
            var reopened = f.Module.ActivateSettingsAsync();
            await Drain();
            Equal(1, f.Calls.Count("music.accounts.list"));
            hold.Release();
            await Task.WhenAll(first, leaving, reopened);
            Equal(1, f.Calls.Count("music.accounts.list"));
            True(f.Module.LibrarySync.IsActive && f.Module.LibrarySync.CanEdit,
                "only the reopened settings generation owns presentation state");
            await f.Module.DeactivateSettingsAsync();
            True(!f.Module.LibrarySync.IsActive && !f.Module.LibrarySync.CanEdit,
                "the final unload retires the sole settings generation");
        });
        yield return Case("search discovery first visibility loads history once without startup prefetch", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Rpc("music.search.history.record",
                new { text = "发现历史", kind = "songs", platform = "aggregate" });
            Equal(0, f.Calls.Count("music.search.history.get"));
            await f.Module.NavigateAsync("catalog-discovery", default);
            await Until(() => f.Module.HasSearchHistory);
            Equal(1, f.Calls.Count("music.search.history.get"));
            await f.Module.NavigateAsync("favorites", default);
            await f.Module.NavigateAsync("catalog-discovery", default);
            await Drain();
            Equal(1, f.Calls.Count("music.search.history.get"));
        });
        yield return Case("search preferences and native local history have real settings consumers", async () =>
        {
            await using var store = ServiceStore();
            await using var runtime = new NativeMusicScriptTestRuntime();
            var app = new MusicNativeApplication(store, runtime, new NoUpstream());
            await using var module = new MusicCapability(app, store, new PlaybackProbe(), new Dialogs());
            await module.InitializeAsync(default);
            await module.NavigateAsync("favorites", default);
            Equal(5, module.SongSearch.Platforms.Count); Equal(5, module.PlaylistSearch.Platforms.Count);
            True(module.CanEditSearchPreferences, "settings loaded");
            await module.SearchAsync(" 测试搜索 ", default);
            True(module.HasSearchHistory, "search response published the current history summary");
            await Until(() => module.CanEditSearchPreferences);
            var source = module.SongSearch.Platforms.Single(row => row.Platform == "mg");
            var target = module.SongSearch.Platforms.Single(row => row.Platform == "wy");
            await module.MoveSearchPlatformAsync("songs", source, target);
            await Until(() => module.CanEditSearchPreferences);
            Equal("mg", module.SongSearch.Platforms[0].Platform);
            foreach (var platform in module.SongSearch.Platforms.ToArray())
            {
                await module.SetSearchPlatformEnabledAsync("songs", platform, false);
                await Until(() => module.CanEditSearchPreferences);
            }
            await module.SetSearchModeAsync("playlists", "single");
            await Until(() => module.CanEditSearchPreferences);
            await module.SetSearchPlatformAsync("playlists", "tx");
            await Until(() => module.CanEditSearchPreferences);
            Equal("", module.SearchPreferenceStatus);
            True(module.SongSearch.Platforms.All(p => !p.Enabled), "each switch saved immediately");
            Equal("single", module.PlaylistSearch.Mode); Equal("tx", module.PlaylistSearch.Platform);
            await module.ClearSearchHistoryAsync();
            True(!module.HasSearchHistory, "history cleared"); Equal(0, runtime.Calls.Count);
            module.SettingsView.Measure(new System.Windows.Size(1000, 1800));
            module.SettingsView.Arrange(new System.Windows.Rect(0, 0, 1000, 1800)); module.SettingsView.UpdateLayout();
            var tabs = Visuals(module.SettingsView).OfType<System.Windows.Controls.TabControl>()
                .Single(tab => System.Windows.Automation.AutomationProperties.GetName(tab) == "设置分类");
            tabs.SelectedIndex = 1;
            module.SettingsView.Measure(new System.Windows.Size(1000, 1800));
            module.SettingsView.Arrange(new System.Windows.Rect(0, 0, 1000, 1800)); module.SettingsView.UpdateLayout();
            var buttons = Visuals(module.SettingsView).OfType<System.Windows.Controls.Button>()
                .Select(button => button.Content as string).ToArray();
            True(buttons.Contains("清空搜索历史"), "search history can be cleared from settings");
        });
    }
}
