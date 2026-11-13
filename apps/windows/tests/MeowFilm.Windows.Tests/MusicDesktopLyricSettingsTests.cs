using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Library;
using MeowFilm.Desktop;
using static MeowFilm.Windows.Tests.QualityFixture;

namespace MeowFilm.Windows.Tests;

public static class MusicDesktopLyricSettingsTests
{
    private const string Get = "music.lyrics.desktop.settings.get", Save = "music.lyrics.desktop.settings.save";
    private const string Normal = "#FFB5C5", Highlight = "#FFF2A8", Area = "native-desktop-lyrics";

    public static IEnumerable<TestCase> Cases()
    {
        yield return Case("missing area defaults remain read only and legacy areas survive a cold reopen", async () =>
        {
            await using var f = new QualityFixture();
            var defaults = (await f.Ok(Get)).GetProperty("settings");
            Settings(defaults, 0, "single", Normal, Highlight);
            Check(!defaults.TryGetProperty("geometry", out _), "the original settings shape remains readable without geometry");
            await f.Ok(Save, Draft());
            Check(f.Store.Writes == 0 && f.Runtime.Calls.Count == 0);
            await f.Ok("music.appearance.save", new { ifRevision = 0, playlistView = "list",
                playerTheme = "starlight", colorMode = "fixed", colorPalette = "mint" });
            await f.Ok("music.library.create", new { name = "Keep" });
            var legacy = Document(f.Store);
            await using (var legacyOwner = new MusicLibraryOwner(f.Store))
                Settings((await legacyOwner.ExecuteAsync(Get)).GetProperty("settings"), 0, "single", Normal, Highlight);
            int writes = f.Store.Writes;
            Check(JsonNode.DeepEquals(legacy, Document(f.Store)), "reading a missing area never migrates legacy state");
            await f.Ok(Save, Draft(layout: "double", normal: "#12abef", highlight: "#000000"));
            Check(f.Store.Writes == writes + 1);
            var current = Document(f.Store);
            foreach (var entry in legacy["entries"]!.AsArray().OfType<JsonObject>())
                Check(current["entries"]!.AsArray().OfType<JsonObject>().Any(saved => JsonNode.DeepEquals(saved, entry)),
                    "saving desktop lyrics preserves each legacy area");
            Check(StoredSettings(current).Count == 5, "only schema, revision, layout and two colors are persisted");
            await using var reopened = new MusicLibraryOwner(f.Store);
            var reopenedSettings = (await reopened.ExecuteAsync(Get)).GetProperty("settings");
            Settings(reopenedSettings, 1, "double", "#12ABEF", "#000000");
            Check(!reopenedSettings.TryGetProperty("geometry", out _), "legacy settings do not acquire a placeholder geometry");
            Check((await reopened.ExecuteAsync("music.appearance.get")).GetProperty("settings")
                .GetProperty("colorPalette").GetString() == "mint" && f.Runtime.Calls.Count == 0);
        });
        yield return Case("native parameter validation and revision admission never write rejected drafts", async () =>
        {
            await using var f = new QualityFixture();
            foreach (var (field, value) in new (string, object?)[] {
                ("layout", ""), ("layout", "triple"), ("layout", true),
                ("normalColor", "pink"), ("normalColor", "#123"), ("normalColor", "#FF112233"),
                ("normalColor", "#11223Z"), ("normalColor", null), ("highlightColor", "#AABB"),
                ("highlightColor", " #112233"), ("highlightColor", 123),
                ("geometry", new { monitor = "", x = 0d, y = 0d, width = 760d, height = 144d }),
                ("geometry", new { monitor = @"\\.\DISPLAY1", x = 0d, y = 0d, width = 0d, height = 144d }),
                ("geometry", new { monitor = @"\\.\DISPLAY1", x = "12", y = 0d, width = 760d, height = 144d }),
                ("geometry", null),
                ("ifRevision", -1), ("ifRevision", "0"), ("ifRevision", .5),
                ("showTranslation", false), ("offset", .5), ("locked", true), ("left", 20) })
            {
                var draft = Draft(layout: "double"); draft[field] = JsonSerializer.SerializeToNode(value);
                await f.Error(Save, draft, "INVALID_REQUEST");
            }
            foreach (string field in new[] { "ifRevision", "layout", "normalColor", "highlightColor" })
            {
                var draft = Draft(layout: "double"); draft.Remove(field);
                await f.Error(Save, draft, "INVALID_REQUEST");
            }
            await f.Error(Get, new { layout = "single" }, "INVALID_REQUEST");
            await f.Error("music.lyrics.desktop.settings.reconcile", null, "CAPABILITY_UNAVAILABLE");
            Check(f.Store.Writes == 0);
            await f.Ok(Save, Draft(layout: "double"));
            await f.Error(Save, Draft(), "REVISION_CONFLICT");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Code(await f.App.ExecuteAsync(Save, Draft(1), cancelled.Token), "CANCELLED");
            Check(f.Store.Writes == 1);
            Settings((await f.Ok(Get)).GetProperty("settings"), 1, "double", Normal, Highlight);
        });
        yield return Case("window geometry uses the settings CAS and subsequent appearance changes preserve it", async () =>
        {
            await using var f = new QualityFixture();
            var geometry = O(new { monitor = @"\\.\DISPLAY2", x = 143.5, y = 84.25, width = 920d, height = 232d });
            await f.Ok(Save, Draft(geometry: geometry));
            var saved = (await f.Ok(Get)).GetProperty("settings");
            Settings(saved, 1, "single", Normal, Highlight);
            Check(JsonNode.DeepEquals(JsonNode.Parse(saved.GetProperty("geometry").GetRawText()), geometry));
            await f.Ok(Save, Draft(1, normal: "#000000"));
            saved = (await f.Ok(Get)).GetProperty("settings");
            Settings(saved, 2, "single", "#000000", Highlight);
            Check(JsonNode.DeepEquals(JsonNode.Parse(saved.GetProperty("geometry").GetRawText()), geometry),
                "a color write preserves the independently-owned window geometry");
            Check(f.Store.Writes == 2);
            await using var reopened = new MusicLibraryOwner(f.Store);
            var reopenedSettings = (await reopened.ExecuteAsync(Get)).GetProperty("settings");
            Check(JsonNode.DeepEquals(JsonNode.Parse(reopenedSettings.GetProperty("geometry").GetRawText()), geometry));
        });
        yield return Case("persisted desktop fields remain strict without weakening the old appearance schema", async () =>
        {
            await using var f = new QualityFixture();
            await f.Ok(Save, Draft(layout: "double"));
            await f.Ok("music.appearance.save", new { ifRevision = 0, playlistView = "grid",
                playerTheme = "starlight", colorMode = "song", colorPalette = "lilac" });
            var original = Document(f.Store);
            try
            {
                foreach (var (field, value) in new (string, object?)[] {
                    ("schema", 2), ("revision", -1), ("layout", "triple"),
                    ("normalColor", "#123"), ("highlightColor", "#FFAABBCC"),
                    ("geometry", new JsonObject { ["monitor"] = "DISPLAY1", ["x"] = "20",
                        ["y"] = 0, ["width"] = 760, ["height"] = 144 }),
                    ("geometry", null),
                    ("showTranslation", true), ("offset", 0), ("locked", false) })
                {
                    var doc = (JsonObject)original.DeepClone();
                    StoredSettings(doc)[field] = JsonSerializer.SerializeToNode(value);
                    f.Store.Data[MusicLibraryOwner.DocumentNamespace] = LJ.Bytes(doc);
                    await using var invalid = new MusicLibraryOwner(f.Store);
                    Code(await invalid.ExecuteAsync(Get), "CORRUPT_STORAGE");
                }
                var legacy = (JsonObject)original.DeepClone();
                Entry(legacy, "native-appearance")["value"]!["desktopLyrics"] = new JsonObject();
                f.Store.Data[MusicLibraryOwner.DocumentNamespace] = LJ.Bytes(legacy);
                await using var strictLegacy = new MusicLibraryOwner(f.Store);
                Code(await strictLegacy.ExecuteAsync(Get), "CORRUPT_STORAGE");
            }
            finally { f.Store.Data[MusicLibraryOwner.DocumentNamespace] = LJ.Bytes(original); }
        });
        yield return Case("native unknown receipts reconcile the exact attempt without replaying a write", async () =>
        {
            await using var f = new QualityFixture();
            f.Store.FaultNamespace = MusicLibraryOwner.DocumentNamespace;
            f.Store.Fault = true; f.Store.FaultAfter = true;
            await f.Error(Save, Draft(layout: "double"), "COMMIT_UNKNOWN");
            Settings((await f.Ok(Get)).GetProperty("settings"), 1, "double", Normal, Highlight);
            Check(f.Store.Writes == 1);
            f.Store.Fault = true; f.Store.FaultAfter = false;
            await f.Error(Save, Draft(1, normal: "#000000"), "COMMIT_UNKNOWN");
            Settings((await f.Ok(Get)).GetProperty("settings"), 1, "double", Normal, Highlight);
            Check(f.Store.Writes == 2, "authoritative reads settle both before and after receipt loss without saving again");
        });
        yield return UiCase("consumer selections persist independently and leave the shared translation switch in its session", ConsumerRoundTrip);
        yield return UiCase("failed reads are retryable and concurrent callers share the physical read", ReadRecovery);
        yield return UiCase("consumer unknown commits accept only the authority and report an uncommitted draft", UnknownConsumer);
        yield return UiCase("window geometry reconciles unknown commits without replaying an unconfirmed write", GeometryUnknownConsumer);
        yield return UiCase("consumer revision conflicts adopt newer authority without replaying the draft", RevisionConflict);
        yield return UiCase("an unavailable authority after an unknown write keeps edits disabled until a real retry", UnknownReadFailure);
        yield return UiCase("a real document CAS conflict never publishes the rejected settings", StorageConflict);
        yield return UiCase("global command busy gates setters and admitted saves physically drain before shutdown", BusyAndShutdown);
    }

    private static async Task ConsumerRoundTrip()
    {
        var store = new DocumentStore();
        await using (var scene = new Scene(store))
        {
            var module = scene.Module;
            Check(!module.CanEditDesktopLyricSettings);
            await module.EnsureDesktopLyricSettingsAsync();
            Check(module.CanEditDesktopLyricSettings && module.DesktopLyricLayout == "single" &&
                module.DesktopLyricNormalColor == Normal && module.DesktopLyricHighlightColor == Highlight);
            var notifications = new List<string?>();
            module.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
            await module.SetDesktopLyricColorAsync("normal", "#000000");
            Check(module.DesktopLyricNormalColor == "#000000" && module.DesktopLyricHighlightColor == Highlight &&
                !notifications.Contains(nameof(MusicCapability.DesktopLyricLayout)),
                "ordinary color changes do not publish a layout change");
            await module.SetDesktopLyricColorAsync("highlight", "#12abef");
            await module.SetDesktopLyricLayoutAsync("double");
            var geometry = new MusicDesktopLyricsGeometry(@"\\.\DISPLAY1", 21.25, 34.5, 980, 254);
            await module.SaveDesktopLyricGeometryAsync(geometry);
            Check(module.DesktopLyricGeometry == geometry);
            await module.ExecuteMediaShortcutAsync(MediaShortcutAction.ToggleTranslation);
            Check(!module.ShowLyricTranslation);
            int writes = store.Writes;
            await module.SetDesktopLyricLayoutAsync("triple");
            await module.SetDesktopLyricColorAsync("translation", "#112233");
            await module.SetDesktopLyricColorAsync("normal", "#123");
            Check(module.Status == "桌面歌词设置无效" && store.Writes == writes &&
                module.DesktopLyricLayout == "double" && module.DesktopLyricNormalColor == "#000000" &&
                module.DesktopLyricHighlightColor == "#12ABEF" && !module.ShowLyricTranslation);
            await module.SetDesktopLyricColorAsync("highlight", "#12ABEF");
            await module.SetDesktopLyricLayoutAsync("double");
            Check(store.Writes == writes && scene.Runtime.Calls.Count == 0, "unchanged values do not save");
            Settings((await scene.Native.ExecuteAsync(Get)).GetProperty("settings"),
                4, "double", "#000000", "#12ABEF");
            Check(StoredSettings(Document(store)).Count == 6);
        }
        await using var next = new Scene(store);
        await next.Module.EnsureDesktopLyricSettingsAsync();
        Check(next.Module.DesktopLyricLayout == "double" && next.Module.DesktopLyricNormalColor == "#000000" &&
            next.Module.DesktopLyricHighlightColor == "#12ABEF" &&
            next.Module.DesktopLyricGeometry == new MusicDesktopLyricsGeometry(@"\\.\DISPLAY1", 21.25, 34.5, 980, 254) &&
            next.Module.ShowLyricTranslation,
            "layout, colors and window geometry survive a new module session while translation remains session-scoped");
    }

    private static async Task ReadRecovery()
    {
        await using var scene = new Scene();
        scene.App.ReadError = "STORAGE_UNAVAILABLE";
        await scene.Module.EnsureDesktopLyricSettingsAsync();
        Check(!scene.Module.CanEditDesktopLyricSettings && scene.Module.Status == "桌面歌词设置读取失败");
        await scene.Module.SetDesktopLyricLayoutAsync("double");
        Check(scene.App.Saves == 0 && scene.Store.Writes == 0);
        var hold = new Hold(); scene.App.NextRead = hold;
        var first = scene.Module.EnsureDesktopLyricSettingsAsync();
        await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = scene.Module.EnsureDesktopLyricSettingsAsync();
        Check(ReferenceEquals(first, second) && !second.IsCompleted && scene.App.Reads == 2);
        hold.Release.TrySetResult();
        await Task.WhenAll(first, second);
        Check(scene.Module.CanEditDesktopLyricSettings && scene.Store.Writes == 0);
        await scene.Module.EnsureDesktopLyricSettingsAsync();
        Check(scene.App.Reads == 2, "a successful load is reused, but a failed read was not installed as success");
    }

    private static async Task UnknownConsumer()
    {
        await using var scene = new Scene();
        var module = scene.Module;
        await module.EnsureDesktopLyricSettingsAsync();
        scene.Store.FaultNamespace = MusicLibraryOwner.DocumentNamespace;
        scene.Store.Fault = true; scene.Store.FaultAfter = true;
        await module.SetDesktopLyricLayoutAsync("double");
        Check(module.CanEditDesktopLyricSettings && module.DesktopLyricLayout == "double" &&
            module.Status.Length == 0 && scene.Store.Writes == 1 && scene.App.Saves == 1);
        scene.Store.Fault = true; scene.Store.FaultAfter = false;
        await module.SetDesktopLyricColorAsync("normal", "#000000");
        Check(module.CanEditDesktopLyricSettings && module.DesktopLyricNormalColor == Normal &&
            module.DesktopLyricLayout == "double" && module.Status == "桌面歌词设置保存失败" &&
            scene.Store.Writes == 2 && scene.App.Saves == 2);
    }

    private static async Task GeometryUnknownConsumer()
    {
        await using var scene = new Scene();
        await scene.Module.EnsureDesktopLyricSettingsAsync();
        var accepted = new MusicDesktopLyricsGeometry(@"\\.\DISPLAY1", 23.5, 48.25, 880, 144);
        scene.Store.FaultNamespace = MusicLibraryOwner.DocumentNamespace;
        scene.Store.Fault = true; scene.Store.FaultAfter = true;
        await scene.Module.SaveDesktopLyricGeometryAsync(accepted);
        Check(scene.Module.CanEditDesktopLyricSettings && scene.Module.DesktopLyricGeometry == accepted &&
            scene.Module.Status.Length == 0 && scene.Store.Writes == 1 && scene.App.Saves == 1,
            "an accepted-but-unknown geometry commit is adopted from the authority without replay");
        var rejected = accepted with { X = 123.5, Y = 148.25 };
        scene.Store.Fault = true; scene.Store.FaultAfter = false;
        await scene.Module.SaveDesktopLyricGeometryAsync(rejected);
        Check(scene.Module.CanEditDesktopLyricSettings && scene.Module.DesktopLyricGeometry == accepted &&
            scene.Module.Status == "桌面歌词位置和大小保存失败" &&
            scene.Store.Writes == 2 && scene.App.Saves == 2,
            "an uncommitted geometry draft is not published when the authority retains the prior value");
    }

    private static async Task RevisionConflict()
    {
        await using var scene = new Scene();
        await scene.Module.EnsureDesktopLyricSettingsAsync();
        var updated = await scene.Native.ExecuteAsync(Save, Draft(normal: "#12ABEF"));
        Check(updated.GetProperty("ok").GetBoolean());
        int writes = scene.Store.Writes;
        await scene.Module.SetDesktopLyricLayoutAsync("double");
        Check(scene.Module.CanEditDesktopLyricSettings && scene.Module.DesktopLyricLayout == "single" &&
            scene.Module.DesktopLyricNormalColor == "#12ABEF" && scene.Module.Status == "桌面歌词设置已更新" &&
            scene.Store.Writes == writes && scene.App.Saves == 1);
        await scene.Module.SetDesktopLyricLayoutAsync("double");
        Check(scene.Module.DesktopLyricLayout == "double" && scene.Store.Writes == writes + 1 &&
            scene.Module.DesktopLyricNormalColor == "#12ABEF",
            "a fresh explicit choice uses the authority revision without overwriting its other fields");
    }

    private static async Task UnknownReadFailure()
    {
        await using var scene = new Scene();
        await scene.Module.EnsureDesktopLyricSettingsAsync();
        scene.Store.FaultNamespace = MusicLibraryOwner.DocumentNamespace;
        scene.Store.Fault = true; scene.Store.FaultAfter = true;
        scene.App.ReadError = "STORAGE_UNAVAILABLE";
        await scene.Module.SetDesktopLyricLayoutAsync("double");
        Check(!scene.Module.CanEditDesktopLyricSettings && scene.Module.DesktopLyricLayout == "single" &&
            scene.Module.Status == "桌面歌词设置保存失败" && scene.App.Saves == 1 && scene.Store.Writes == 1,
            "lost save and read receipts cannot install an optimistic draft");
        await scene.Module.SetDesktopLyricColorAsync("highlight", "#000000");
        Check(scene.App.Saves == 1);
        await scene.Module.EnsureDesktopLyricSettingsAsync();
        Check(scene.Module.CanEditDesktopLyricSettings && scene.Module.DesktopLyricLayout == "double" &&
            scene.Module.DesktopLyricHighlightColor == Highlight && scene.App.Saves == 1 && scene.Store.Writes == 1);
    }

    private static async Task StorageConflict()
    {
        await using var scene = new Scene();
        await scene.Module.EnsureDesktopLyricSettingsAsync();
        await using var competing = new MusicLibraryOwner(scene.Store);
        var committed = await competing.ExecuteAsync(Save, Draft(normal: "#12ABEF"));
        Check(committed.GetProperty("ok").GetBoolean());
        await scene.Module.SetDesktopLyricLayoutAsync("double");
        Check(!scene.Module.CanEditDesktopLyricSettings && scene.Module.DesktopLyricLayout == "single" &&
            scene.Module.DesktopLyricNormalColor == Normal && scene.Module.Status == "桌面歌词设置保存失败");
        Settings((await competing.ExecuteAsync(Get)).GetProperty("settings"), 1, "single", "#12ABEF", Highlight);
        int writes = scene.Store.Writes;
        await scene.Module.EnsureDesktopLyricSettingsAsync();
        Check(!scene.Module.CanEditDesktopLyricSettings && scene.Store.Writes == writes,
            "an unresolved document conflict stays fail closed and never replays the rejected save");
    }

    private static async Task BusyAndShutdown()
    {
        await using var scene = new Scene();
        await scene.Module.EnsureDesktopLyricSettingsAsync();
        var hold = new Hold();
        var command = scene.Module.RunAsync(async () =>
        {
            hold.Entered.TrySetResult(); await hold.Release.Task;
        });
        await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!scene.Module.CanEditDesktopLyricSettings);
        await scene.Module.SetDesktopLyricLayoutAsync("double");
        await scene.Module.SetDesktopLyricColorAsync("normal", "#000000");
        Check(scene.App.Saves == 0 && scene.Store.Writes == 0);
        hold.Release.TrySetResult(); await command;
        Check(scene.Module.CanEditDesktopLyricSettings);
        scene.Store.HoldNamespace = MusicLibraryOwner.DocumentNamespace;
        var save = scene.Module.SetDesktopLyricLayoutAsync("double");
        await scene.Store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var shutdown = scene.Module.PrepareShutdownAsync();
        Check(!save.IsCompleted && !shutdown.IsCompleted && !scene.Module.CanEditDesktopLyricSettings);
        scene.Store.Release.TrySetResult();
        await Task.WhenAll(save, shutdown);
        Check(scene.Module.DesktopLyricLayout == "double" && scene.Module.Status.Length == 0 &&
            scene.App.Saves == 1, "admitted physical success is retained while shutdown drains the command lane");
    }

    private static TestCase Case(string name, Func<Task> run) => new("desktop lyric settings " + name, run);
    private static TestCase UiCase(string name, Func<Task> run) => Case(name, () => MusicQualityConsumerTests.Sta(run));
    private static JsonObject Draft(long revision = 0, string layout = "single",
        string normal = Normal, string highlight = Highlight, JsonObject? geometry = null)
    {
        var draft = O(new { ifRevision = revision, layout, normalColor = normal, highlightColor = highlight });
        if (geometry is not null) draft["geometry"] = geometry.DeepClone();
        return draft;
    }
    private static void Settings(JsonElement value, long revision, string layout, string normal, string highlight) =>
        Check(value.GetProperty("schema").GetInt32() == 1 && value.GetProperty("revision").GetInt64() == revision &&
            value.GetProperty("layout").GetString() == layout && value.GetProperty("normalColor").GetString() == normal &&
            value.GetProperty("highlightColor").GetString() == highlight);
    private static JsonObject Document(DocumentStore store) => JsonNode.Parse(store.Data[MusicLibraryOwner.DocumentNamespace])!.AsObject();
    private static JsonObject Entry(JsonObject doc, string area) => doc["entries"]!.AsArray().OfType<JsonObject>()
        .Single(entry => LJ.S(entry["key"]) == LJ.Stringify(new JsonArray("meowfilm", "music", 1, "local", area)));
    private static JsonObject StoredSettings(JsonObject doc) => Entry(doc, Area)["value"]!.AsObject();

    private sealed class Hold
    {
        internal readonly TaskCompletionSource Entered = Gate(), Release = Gate();
    }

    private sealed class Application(IMusicApplication inner) : MusicApplicationForwarder(inner)
    {
        internal int Reads, Saves;
        internal string? ReadError;
        internal Hold? NextRead;
        internal readonly List<Hold> PendingReads = [];
        public override async Task<JsonElement> ExecuteAsync(string method, object? parameters = null,
            CancellationToken cancellationToken = default)
        {
            if (method == Get)
            {
                Reads++;
                if (NextRead is { } hold)
                {
                    NextRead = null;
                    PendingReads.Add(hold);
                    hold.Entered.TrySetResult();
                    await hold.Release.Task;
                    cancellationToken.ThrowIfCancellationRequested();
                }
                if (ReadError is { } error)
                {
                    ReadError = null;
                    return JsonSerializer.SerializeToElement(new { ok = false, code = error });
                }
            }
            if (method == Save) Saves++;
            return await base.ExecuteAsync(method, parameters, cancellationToken);
        }
    }

    private sealed class Scene : IAsyncDisposable
    {
        internal readonly DocumentStore Store;
        internal readonly ScriptRuntime Runtime = new();
        internal readonly MusicNativeApplication Native;
        internal readonly Application App;
        internal readonly MusicCapability Module;
        internal Scene(DocumentStore? store = null)
        {
            Store = store ?? new();
            Native = new(Store, Runtime, new RejectHttp());
            App = new(Native);
            Module = new(App, Store, new Output());
        }
        public async ValueTask DisposeAsync()
        {
            Store.Release.TrySetResult(); Runtime.Release.TrySetResult();
            App.NextRead?.Release.TrySetResult();
            foreach (var hold in App.PendingReads) hold.Release.TrySetResult();
            await Module.AbortAsync();
        }
    }

    private sealed class Output : IMusicPlayback
    {
        public string? OwnerId => null;
        public string? Identity => null;
        public bool HasMedia => false;
        public bool IsPlaying => false;
        public double PositionSeconds => 0;
        public double DurationSeconds => 0;
        public int Volume { get; set; } = 50;
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public event EventHandler? Started { add { } remove { } }
        public event EventHandler? Ended { add { } remove { } }
        public event EventHandler? Failed { add { } remove { } }
        public Task OpenAsync(string owner, string identity, Uri uri, string title, CancellationToken token) => Task.CompletedTask;
        public Task PauseAsync() => Task.CompletedTask;
        public Task ResumeAsync() => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public Task SeekAsync(double seconds) => Task.CompletedTask;
    }
}
