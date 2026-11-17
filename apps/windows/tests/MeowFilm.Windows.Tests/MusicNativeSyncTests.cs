using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Capabilities.Sync;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Windows.Tests;

public static partial class MusicNativeSyncTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("Music sync defaults six off and read does not write", Defaults);
        yield return new("Music sync explicit consent required and independent flags", Consent);
        foreach (string platform in new[] { "wy", "tx", "kg" })
        {
            string p = platform;
            yield return new($"Music sync {p} pull and exact native identity", () => Pull(p));
            yield return new($"Music sync {p} local favorite write then verified ack", () => FavoriteWrite(p));
            yield return new($"Music sync {p} first binding unions local and platform favorites", () => FirstBindingUnion(p));
            yield return new($"Music sync {p} unbound remove cannot cancel future platform favorites", () => UnboundCancellation(p, false));
            yield return new($"Music sync {p} unbound replacement cannot cancel future platform favorites", () => UnboundCancellation(p, true));
            yield return new($"Music sync {p} full playlist create bind members", () => Create(p));
            yield return new($"Music sync {p} unknown creation never duplicated after restart", () => UnknownCreate(p));
            yield return new($"Music sync {p} subscription is metadata-only and rejects member editing", () => ReadOnly(p));
            yield return new($"Music sync {p} full paged members retained", () => Pages(p));
            yield return new($"Music sync {p} local source-free song does not block other favorites", () => LocalUnmapped(p));
        }
        yield return new("Music sync per-platform favorite and playlist switches never over-select", IndependentKinds);
        yield return new("Music sync remote empty requires exact explicit confirmation", EmptyConfirmation);
        yield return new("Music sync stale confirmation cannot authorize changed remote", StaleConfirmation);
        yield return new("Music sync large local deletes require confirmation", LocalBulk);
        yield return new("Music sync bilateral local pending beats unacknowledged old remote", LocalPending);
        yield return new("Music sync second platform first binding unions only real native identities", SecondPlatformFirstBinding);
        yield return new("Music sync bound cancellation while disabled survives reopen and wins on re-enable", BoundDisabledCancellation);
        foreach (var test in FirstBindingPrecedenceCases()) yield return test;
        yield return new("Music sync remote deletion propagates only after native delta", RemoteDelta);
        yield return new("Music sync WY pure order uses update and verifies order", OrderWy);
        yield return new("Music sync QQ order unsupported without clear and re-add", OrderTx);
        yield return new("Music sync KG repeated recording preserves file occurrence and removal", OccurrencesKg);
        yield return new("Music sync creation timeout success is recovered without second create", CreationReadback);
        yield return new("Music sync ambiguous create preserves fence", AmbiguousCreate);
        yield return new("Music sync remote request failure is not empty success", FailureNotEmpty);
        yield return new("Music sync canonical pagination failure blocks every write", RepeatedPage);
        yield return new("Music sync local edit during pull supersedes the whole plan", ConcurrentEdit);
        yield return new("Music sync account replacement fences delayed read and write", AccountReplacement);
        yield return new("Music sync account reset is explicit local only", ResetAccount);
        yield return new("Music sync cancellation and reversible close retain singleton owner", Lifecycle);
        yield return new("Music sync startup defers network and five minute clock keeps one flight", Scheduling);
        yield return new("Music sync blocked scheduled round does not retry-loop", ScheduledFailure);
        yield return new("Music sync completes every selected collection without a round request quota", RequestBudget);
        yield return new("Music sync resolves every selected mapping without a lookup quota", MappingLookupBudget);
        yield return new("Music sync unknown before commit prevents remote dispatch", () => UnknownTransaction(false));
        yield return new("Music sync unknown after commit durable fence recovers without replay", () => UnknownTransaction(true));
        yield return new("Music sync pending write partial acknowledgement survives reopen", PartialAck);
        yield return new("Music sync independent queue changes survive sync record commit", QueuePreserved);
        foreach (var corruption in SyncCorruptions())
        {
            var current = corruption;
            yield return new("Music sync rejects current document " + current.Name + " without side effects",
                () => Corrupt(current.Apply));
        }
        yield return new("Music sync real SQLite restart settings bindings pending without credentials", SQLiteReopen);
        yield return new("Music sync viewmodel immediate switches confirmation and visibility", ViewModel);
        yield return new("Music platform settings XAML initializes on native STA without module state", View);
        yield return new("Music sync KG order uses the shared client without a native cloud hook", KgCloudMissing);
        foreach (var test in NetworkPlaylistCases()) yield return test;
        foreach (var test in NeteaseTokenCases()) yield return test;
    }
    public static IEnumerable<TestCase> RecoveryCases()
    {
        yield return new("Music sync confirmed playlist delete is not repeatedly confirmed", DeletePlaylist);
        yield return new("Music sync KG fileid hash replacement before write supersedes binding", KgIdentityRace);
        yield return new("Music sync large remote IDs stay lossless strings", LosslessId);
        yield return new("Music sync disabling retains pending but stops all network", DisabledPending);
        yield return new("Music sync viewmodel cancellation works while page busy", BusyCancel);
        foreach (var test in MetadataCases()) yield return test;
    }
    private static void Check(bool value, string code = "ASSERTION_FAILED") { if (!value) throw new InvalidOperationException(code); }
    private static JsonObject O(object value) => JsonSerializer.SerializeToNode(value)!.AsObject();
    private static JsonElement E(object value) => JsonSerializer.SerializeToElement(value);
    private static string S(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
    private static JsonElement Ok(JsonElement e) { Check(e.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True, "EXPECTED_OK_" + S(e, "code")); return e; }
    private static void Code(JsonElement e, string code) => Check(S(e, "code") == code, "EXPECTED_" + code + "_GOT_" + S(e, "code"));
    private static JsonObject Song(string platform, string id, int number = 1)
    {
        var native = O(new {
            hash = platform == "kg" ? id : null, qqSongId = "1001", songType = 0,
            name = "Track " + number, singer = "Synthetic Artist",
            albumId = "5", albumAudioId = "6", duration = 200
        });
        return MusicSongFixture.Online(platform, id, "Track " + number,
            "Synthetic Artist", "Synthetic Album", 200, native: native);
    }
    private static string Native(string platform, int n) => platform == "wy" ? (1000 + n).ToString() : platform == "tx" ? "MID" + n : n.ToString("X32");
    private sealed class Sessions : IMusicPlatformSessionSource
    {
        internal readonly Dictionary<string, MusicPlatformSession> Values = new();
        internal Sessions()
        {
            foreach (string p in new[] { "wy", "tx", "kg" }) Values[p] = Make(p, 1, "42");
        }
        internal static MusicPlatformSession Make(string p, long generation, string user) => new(p, "synthetic-" + p, generation, user,
            p == "wy" ? "MUSIC_U=synthetic-only; __csrf=synthetic" : p == "tx" ? $"musicid={user}; musickey=synthetic-only; encrypt_uin=synthetic-euin" :
            $"userid={user}; token=synthetic-only; KUGOU_API_MID=123456789; dfid=synthetic-device");
        public ValueTask<MusicPlatformSession?> AcquireAsync(string platform, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(Values.GetValueOrDefault(platform)); }
        public bool IsCurrent(MusicPlatformSession session) => Values.GetValueOrDefault(session.Platform) == session;
    }
    private sealed class Store : MusicRecordTestStore
    {
        internal bool FailMarker;
        internal new bool FailAfter;
        internal Store()
        {
            BeforeCommit = (changes, _) =>
            {
                if (FailMarker && changes.Areas.Any(pair => pair.Key.Contains("library-sync", StringComparison.Ordinal)
                    && pair.Value["pending"] is JsonObject))
                { FailMarker = false; Failure = FailAfter ? 2 : 1; }
                return Task.CompletedTask;
            };
        }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly Store Store = new(); internal readonly Sessions Sessions = new(); internal readonly Server Server;
        internal readonly MusicPlatformRuntimeFixture Runtime = new();
        internal MusicLibraryOwner Owner; internal MusicLibrarySyncService Sync;
        private readonly TimeProvider? clock;
        internal Fixture(string platform = "tx", TimeProvider? clock = null, Server? second = null)
        {
            this.clock = clock; Server = new(platform); Runtime.Client.BindSessions(Sessions);
            Runtime.OnRequest = (call, token) => (call.Platform == Server.Platform ? Server
                : second is not null && call.Platform == second.Platform ? second
                : throw new InvalidOperationException("Unexpected sync platform")).Respond(call, token);
            Owner = new(MusicTestStores.For(Store)); Sync = new(Owner, Sessions, Runtime.Client, clock);
        }
        internal async Task Enable(bool favorites = true, bool playlists = false)
        {
            var before = Ok(await Sync.ExecuteAsync("music.librarySync.get"));
            Ok(await Sync.ExecuteAsync("music.librarySync.set", new { platform = Server.Platform, favorites, playlists, ifRevision = before.GetProperty("revision").GetInt64(), confirm = true }));
        }
        internal Task<JsonElement> Run() => Sync.ExecuteAsync("music.librarySync.run");
        internal async Task<JsonElement> AddFavorite(int n)
        {
            var c = Ok(await Owner.ExecuteAsync("music.library.detail", new { id = "favorites" })).GetProperty("collection");
            return Ok(await Owner.ExecuteAsync("music.library.addSongs", new { id = "favorites", ifRevision = c.GetProperty("revision").GetInt64(), songs = new[] { Song(Server.Platform, Native(Server.Platform, n), n) } }));
        }
        internal async Task Restart()
        { await Sync.DisposeAsync(); await Owner.AbortAsync(); Owner = new(MusicTestStores.For(Store)); Sync = new(Owner, Sessions, Runtime.Client, clock); }
        public async ValueTask DisposeAsync() { Server.Release?.TrySetResult(); await Sync.DisposeAsync(); await Owner.AbortAsync(); await Runtime.DisposeAsync(); }
    }
    private sealed class Entry(string id, string name, bool favorite = false, bool writable = true)
    {
        internal string Id = id, Name = name; internal bool Favorite = favorite, Writable = writable;
        internal string GlobalId = "", CreatorListId = "", Cover = "";
        internal List<int> Songs = []; internal List<int> Files = [];
        internal void Add(int n) { Songs.Add(n); Files.Add(Files.Count == 0 ? 5000 : Files.Max() + 1); }
    }
    // Canonical business peer: no platform HTTP, crypto, pagination or token implementation.
    private sealed class Server(string platform)
    {
        internal string Platform = platform;
        internal string AccountId = "42";
        internal List<Entry> Entries = [new(platform == "wy" ? "101" : platform == "tx" ? "favorites" : "0:101", "Favorites", true)];
        internal Entry Favorite => Entries[0];
        internal int Reads, Writes, Creates, MemberReads, SubscriptionMemberReads, SubscriptionWrites, MetadataReads, SearchReads, Preparations;
        internal bool FailReads, RepeatPage, DropWrites, ThrowAfterCreate, Ambiguous, Partial;
        internal bool ThrowAfterSubscription, FailSubscriptionReadback, SubscriptionRejected;
        internal string? WriteStatus, WriteError;
        internal string CreateName = "Local";
        internal int FavoriteTarget = 1; internal bool FavoriteAdd = true;
        internal readonly Dictionary<string, Entry> SubscriptionTargets = [];
        internal TaskCompletionSource? Entered, Release;
        internal Action? OnRead, AfterWrite; internal Action<int>? ObserveRead;
        internal Func<MusicPlatformRuntimeFixture.Call, CancellationToken, Task<JsonElement>>? OnWrite;
        internal Func<CancellationToken, Task<JsonElement>>? Prepare;
        internal Func<SyncRemote, SyncRemote>? ChangeRemote;
        // Canonical metadata variants used by the separately owned MetadataCases partial.
        // WyDetailReads counts supplied missing-metadata observations, not provider HTTP.
        internal readonly HashSet<int> WyMissingTracks = [];
        internal bool WyDetailsAvailable, WyDetailNetworkFailure;
        internal int WyDetailCode = 200, WyDetailReads;
        internal readonly List<string> Operations = [];
        internal JsonObject? LastWrite;
        internal async Task<JsonElement> Respond(MusicPlatformRuntimeFixture.Call call, CancellationToken token)
        {
            if (call.Operation == "sync.prepare") {
                Preparations++; return Prepare is null ? MusicPlatformRuntimeFixture.Json(new { preparationId = "synthetic-preparation-" + Preparations }) : await Prepare(token);
            }
            if (call.Operation == "sync.write") {
                Writes++; string action = call.Parameters.GetProperty("action").GetString()!;
                Operations.Add(action); LastWrite = JsonNode.Parse(call.Parameters.GetRawText())!.AsObject();
                if (OnWrite is not null) return await OnWrite(call, token);
                string status = WriteStatus ?? (SubscriptionRejected && (action is "subscribe" or "unsubscribe") ? "rejected" : "confirmed");
                if (status is "notSent" or "rejected") {
                    if (action is "subscribe" or "unsubscribe") SubscriptionWrites++;
                    return MusicPlatformRuntimeFixture.Json(new { status, errorCode = WriteError ?? "UPSTREAM_ERROR" });
                }
                Apply(call.Parameters, action);
                var after = AfterWrite; AfterWrite = null; after?.Invoke();
                if (ThrowAfterCreate && action == "create" || ThrowAfterSubscription && (action is "subscribe" or "unsubscribe")) {
                    ThrowAfterSubscription = false; return MusicPlatformRuntimeFixture.Json(new { status = "unknown", errorCode = "NETWORK_ERROR" });
                }
                return MusicPlatformRuntimeFixture.Json(new { status, errorCode = WriteError });
            }
            token.ThrowIfCancellationRequested(); Reads++; ObserveRead?.Invoke(Reads); var read = OnRead; OnRead = null; read?.Invoke();
            if (Entered is { } entered) { Entered = null; entered.TrySetResult(); await Release!.Task.WaitAsync(token); }
            if (FailReads) throw new MusicPlatformException("UPSTREAM_ERROR");
            if (RepeatPage) throw new MusicPlatformException("PAGINATION_STALLED");
            if (call.Operation == "search") { SearchReads++; return MusicPlatformRuntimeFixture.Json(new { items = Array.Empty<object>(), more = false, continuation = (object?)null }); }
            if (call.Operation == "sync.target") {
                MetadataReads++; string id = call.Parameters.GetProperty("id").GetString()!;
                var target = SubscriptionTargets.GetValueOrDefault(id) ?? Entries.Single(entry => entry.Id == id);
                return MusicPlatformRuntimeFixture.Json(Subscription(target));
            }
            Check(call.Operation == "sync.read", "UNEXPECTED_CANONICAL_OPERATION");
            var p = call.Parameters; bool subscriptionsOnly = p.TryGetProperty("subscriptionsOnly", out var only) && only.GetBoolean();
            string? collectionId = p.TryGetProperty("collectionId", out var cid) ? cid.GetString() : null;
            // Counts actual collection-specific requests, not upstream member HTTP.
            if (collectionId is not null && Entries.Concat(SubscriptionTargets.Values).Any(entry => !entry.Writable &&
                (entry.Id == collectionId || Platform == "kg" && Global(entry) == collectionId))) SubscriptionMemberReads++;
            string[] kinds = p.TryGetProperty("kinds", out var kind) ? kind.EnumerateArray().Select(value => value.GetString()!).ToArray() : ["favorites", "playlist"];
            Entry[] selected = subscriptionsOnly ? [] : Entries.Where(entry => entry.Writable && (collectionId is not null ? entry.Id == collectionId : kinds.Contains(entry.Favorite ? "favorites" : "playlist"))).ToArray();
            var collections = selected.Select(Remote).ToArray();
            SyncSubscription[] subscriptions = collectionId is not null || !subscriptionsOnly && !kinds.Contains("playlist") ? [] : Entries.Where(entry => !entry.Writable).Select(Subscription).ToArray();
            return MusicPlatformRuntimeFixture.Json(new {
                accountId = AccountId, collections, subscriptions,
                directory = Entries.Select(entry => new {
                    id = entry.Id, name = entry.Name, kind = entry.Favorite ? "favorites" : "playlist",
                    owned = entry.Writable, writable = entry.Writable, ownerId = entry.Writable ? AccountId : "77",
                    count = entry.Songs.Count, locator = Locator(entry)
                }).ToArray()
            });
        }
        private SyncRemote Remote(Entry entry)
        {
            MemberReads++; var occurrences = new Dictionary<string, int>(); var members = new List<SyncMember>();
            for (int index = 0; index < entry.Songs.Count; index++) {
                int number = entry.Songs[index]; string id = Native(Platform, number); int occurrence = occurrences.GetValueOrDefault(id); occurrences[id] = occurrence + 1;
                bool missing = Platform == "wy" && WyMissingTracks.Contains(number);
                if (missing) WyDetailReads++;
                // Metadata failures are already classified by the canonical provider; never decode a wire response here.
                if (missing && WyDetailCode == 301) throw new MusicPlatformException("AUTH_REQUIRED");
                bool hasMetadata = !missing || WyDetailsAvailable && !WyDetailNetworkFailure;
                var song = hasMetadata ? Song(Platform, id, number) : MusicSongFixture.Online(Platform, id, "", "", "", 0);
                string key = Platform == "kg" ? entry.Files[index].ToString() : JsonSerializer.Serialize(new object[] { id, occurrence });
                members.Add(new(id, key, song, Platform == "kg" ? index.ToString() : null, hasMetadata));
            }
            var remote = new SyncRemote(entry.Id, entry.Name, entry.Favorite ? "favorites" : "playlist", entry.Writable, AccountId, members, Locator(entry));
            return ChangeRemote?.Invoke(remote) ?? remote;
        }
        private SyncSubscription Subscription(Entry entry) => new(Platform == "kg" ? Global(entry) : entry.Id, entry.Name, entry.Cover, entry.Songs.Count, Locator(entry));
        private string Global(Entry entry) => entry.GlobalId.Length > 0 ? entry.GlobalId : "global" + entry.Id.Split(':').Last();
        private JsonObject Locator(Entry entry) => Platform switch {
            "kg" => new() { ["listId"] = entry.Id.Split(':').Last(), ["globalId"] = Global(entry), ["creatorId"] = entry.Writable ? AccountId : "77",
                ["creatorListId"] = entry.CreatorListId, ["version"] = "1", ["totalVersion"] = "1" },
            "tx" => new() { ["dirId"] = entry.Favorite ? "201" : entry.Id, ["playlistId"] = entry.Id }, _ => new() };
        private int Number(string id) => Platform switch { "wy" => int.Parse(id) - 1000, "tx" => int.Parse(id[3..]), _ => Convert.ToInt32(id, 16) };
        private void Apply(JsonElement value, string action)
        {
            if (action == "create") {
                Creates++; CreateName = value.GetProperty("name").GetString()!;
                if (!DropWrites) { Entries.Add(new(Platform == "kg" ? "0:" + (400 + Creates) : (400 + Creates).ToString(), CreateName)); if (Ambiguous) Entries.Add(new(Platform == "kg" ? "0:999" : "999", CreateName)); }
                return;
            }
            if (action is "subscribe" or "unsubscribe") {
                SubscriptionWrites++; string id = value.GetProperty("id").GetString()!;
                if (!DropWrites) {
                    if (action == "subscribe") {
                        var target = SubscriptionTargets.GetValueOrDefault(id) ??
                            Entries.Single(entry => (Platform == "kg" ? Global(entry) : entry.Id) == id);
                        if (!Entries.Contains(target)) Entries.Add(target);
                    }
                    else Entries.RemoveAll(entry => !entry.Writable && (Platform == "kg" ? Global(entry) : entry.Id) == id);
                }
                if (FailSubscriptionReadback) FailReads = true; return;
            }
            string remoteId = value.GetProperty("remote").GetProperty("id").GetString()!; var remote = Entries.Single(entry => entry.Id == remoteId);
            if (DropWrites) return;
            if (action == "delete") { Entries.Remove(remote); return; }
            var ids = value.GetProperty("ids").EnumerateArray().Select(item => item.GetString()!).ToArray();
            if (action == "order") {
                var files = remote.Songs.Select((number, index) => (number, file: remote.Files[index])).GroupBy(item => item.number).ToDictionary(group => group.Key, group => new Queue<int>(group.Select(item => item.file)));
                remote.Songs = ids.Select(Number).ToList(); remote.Files = remote.Songs.Select(number => files[number].Dequeue()).ToList(); return;
            }
            int[] values = ids.Select(Number).ToArray(); if (Partial) values = values.Take(1).ToArray();
            foreach (int number in values) {
                if (action == "add") { if (!remote.Songs.Contains(number)) remote.Add(number); }
                else for (int index = remote.Songs.Count - 1; index >= 0; index--) if (remote.Songs[index] == number) { remote.Songs.RemoveAt(index); remote.Files.RemoveAt(index); }
            }
        }
    }
    private static async Task Defaults()
    {
        await using var f = new Fixture(); var result = Ok(await f.Sync.ExecuteAsync("music.librarySync.get"));
        foreach (var p in result.GetProperty("platforms").EnumerateObject()) Check(!p.Value.GetProperty("favorites").GetBoolean() && !p.Value.GetProperty("playlists").GetBoolean());
        Ok(await f.Run()); Check(f.Server.Reads == 0 && f.Server.Writes == 0 && f.Store.Writes == 0);
        Check(MusicLibrarySyncService.Interval == TimeSpan.FromMinutes(5));
    }
    private static async Task Consent()
    {
        await using var f = new Fixture();
        Code(await f.Sync.ExecuteAsync("music.librarySync.set", new { platform = "tx", favorites = true, playlists = false, ifRevision = 0, confirm = false }), "CONFIRMATION_REQUIRED");
        await f.Enable(); var result = Ok(await f.Sync.ExecuteAsync("music.librarySync.get"));
        Check(result.GetProperty("platforms").GetProperty("tx").GetProperty("favorites").GetBoolean()); Check(!result.GetProperty("platforms").GetProperty("wy").GetProperty("favorites").GetBoolean());
        Check(f.Server.Reads == 0 && f.Server.Writes == 0);
    }
    private static async Task Pull(string platform)
    {
        await using var f = new Fixture(platform); f.Server.Favorite.Add(1); await f.Enable(); Ok(await f.Run());
        var detail = Ok(await f.Owner.ExecuteAsync("music.library.detail", new { id = "favorites" })); Check(detail.GetProperty("songs").GetArrayLength() == 1); Check(f.Server.Writes == 0);
        var snapshot = await f.Owner.ReadLibrarySyncAsync(); Check(snapshot.Library.GetProperty("collections")[0].GetProperty("bindings").GetArrayLength() == 1);
        Check(f.Server.Reads == 1, "UNCHANGED_PULL_MUST_NOT_REREAD");
    }
    private static async Task FavoriteWrite(string platform)
    {
        await using var f = new Fixture(platform); await f.AddFavorite(1); await f.Enable(); Ok(await f.Run()); Check(f.Server.Favorite.Songs.SequenceEqual(new[] { 1 })); Check(f.Server.Writes == 1);
        var snapshot = await f.Owner.ReadLibrarySyncAsync(); var intents = snapshot.Library.GetProperty("collections")[0].GetProperty("intents");
        Check(intents.EnumerateObject().All(i => !i.Value.GetProperty("pending").GetBoolean() && i.Value.GetProperty("acknowledged").GetArrayLength() == 1));
        Ok(await f.Run()); Check(f.Server.Writes == 1);
    }
    private static async Task FirstBindingUnion(string platform)
    {
        await using var f = new Fixture(platform);
        await f.AddFavorite(1);
        string localId = S(Ok(await f.Owner.ExecuteAsync("music.library.detail", new { id = "favorites" })).GetProperty("songs")[0], "songId");
        f.Server.Favorite.Add(2);
        await f.Enable(); Ok(await f.Run());
        var songs = Ok(await f.Owner.ExecuteAsync("music.library.detail", new { id = "favorites" })).GetProperty("songs");
        Check(songs.GetArrayLength() == 2 && songs.EnumerateArray().Any(song => S(song, "songId") == localId),
            "FIRST_BINDING_MUST_RETAIN_LOCAL_FAVORITE");
        Check(f.Server.Favorite.Songs.Order().SequenceEqual(new[] { 1, 2 }),
            "FIRST_BINDING_MUST_UNION_PLATFORM_FAVORITES");
    }
    private static async Task UnboundCancellation(string platform, bool replace)
    {
        await using var f = new Fixture(platform);
        await f.AddFavorite(1);
        var before = Ok(await f.Owner.ExecuteAsync("music.library.detail", new { id = "favorites" }));
        long revision = before.GetProperty("collection").GetProperty("revision").GetInt64();
        if (replace) Ok(await f.Owner.ExecuteAsync("music.library.edit", new { id = "favorites", ifRevision = revision, songs = Array.Empty<object>() }));
        else Ok(await f.Owner.ExecuteAsync("music.library.removeSong", new
        {
            id = "favorites", ifRevision = revision, songId = S(before.GetProperty("songs")[0], "songId")
        }));
        await f.Restart();
        Check(Ok(await f.Owner.ExecuteAsync("music.library.detail", new { id = "favorites" })).GetProperty("songs").GetArrayLength() == 0);
        f.Server.Favorite.Add(1); f.Server.Favorite.Add(2);
        await f.Enable(); Ok(await f.Run());
        Check(Ok(await f.Owner.ExecuteAsync("music.library.detail", new { id = "favorites" })).GetProperty("songs").GetArrayLength() == 2);
        Check(f.Server.Favorite.Songs.SequenceEqual(new[] { 1, 2 }) && f.Server.Writes == 0,
            "UNBOUND_CANCELLATION_MUST_NOT_REACH_FIRST_ACCOUNT");
    }
    private static async Task SecondPlatformFirstBinding()
    {
        await using var f = new Fixture();
        await f.AddFavorite(1);
        var favorite = Ok(await f.Owner.ExecuteAsync("music.library.detail", new { id = "favorites" })).GetProperty("collection");
        Ok(await f.Owner.ExecuteAsync("music.library.addSongs", new
        {
            id = "favorites", ifRevision = favorite.GetProperty("revision").GetInt64(), songs = new[] { Song("wy", Native("wy", 1)) }
        }));
        var firstPlatform = f.Server.Favorite;
        firstPlatform.Add(2);
        await f.Enable(); Ok(await f.Run());
        Check(firstPlatform.Songs.Order().SequenceEqual(new[] { 1, 2 }));
        await f.Enable(false);
        f.Server.Platform = "wy";
        f.Server.Entries = [new("101", "Favorites", true)];
        f.Server.Favorite.Add(3);
        await f.Enable(); Ok(await f.Run());
        var detail = Ok(await f.Owner.ExecuteAsync("music.library.detail", new { id = "favorites" }));
        Check(detail.GetProperty("songs").GetArrayLength() == 3, "SECOND_PLATFORM_MUST_RETAIN_LOCAL_UNION");
        Check(f.Server.Favorite.Songs.Order().SequenceEqual(new[] { 1, 3 }) &&
            firstPlatform.Songs.Order().SequenceEqual(new[] { 1, 2 }),
            "SECOND_PLATFORM_MUST_ONLY_WRITE_CONFIRMED_NATIVE_IDENTITIES");
        var txOnly = detail.GetProperty("songs").EnumerateArray().Single(song => S(song, "title") == "Track 2");
        Check(txOnly.GetProperty("sources").EnumerateArray().All(source => S(source, "source") == "tx"),
            "UNMAPPED_SONG_MUST_NOT_GAIN_A_FABRICATED_PLATFORM");
        var snapshot = await f.Owner.ReadLibrarySyncAsync();
        Check(snapshot.Library.GetProperty("collections")[0].GetProperty("bindings").GetArrayLength() == 2 &&
            S(snapshot.Library.GetProperty("accounts"), "tx") == "42" && S(snapshot.Library.GetProperty("accounts"), "wy") == "42");
    }
    private static async Task BoundDisabledCancellation()
    {
        await using var f = new Fixture();
        f.Server.Favorite.Add(1); f.Server.Favorite.Add(2);
        await f.Enable(); Ok(await f.Run());
        await f.Enable(false);
        var detail = Ok(await f.Owner.ExecuteAsync("music.library.detail", new { id = "favorites" }));
        Ok(await f.Owner.ExecuteAsync("music.library.removeSong", new
        {
            id = "favorites", ifRevision = detail.GetProperty("collection").GetProperty("revision").GetInt64(),
            songId = S(detail.GetProperty("songs")[0], "songId")
        }));
        await f.Restart();
        int reads = f.Server.Reads;
        Ok(await f.Run());
        Check(f.Server.Reads == reads && f.Server.Writes == 0 &&
            f.Server.Favorite.Songs.SequenceEqual(new[] { 1, 2 }));
        await f.Enable(); Ok(await f.Run());
        Check(f.Server.Favorite.Songs.SequenceEqual(new[] { 2 }) &&
            f.Server.Operations.SequenceEqual(new[] { "remove" }), "BOUND_DISABLED_CANCELLATION_MUST_STILL_WIN");
        Check(Ok(await f.Owner.ExecuteAsync("music.library.detail", new { id = "favorites" })).GetProperty("songs").GetArrayLength() == 1);
        Ok(await f.Run()); Check(f.Server.Writes == 1);
    }
    private static async Task Create(string platform)
    {
        await using var f = new Fixture(platform); await f.Enable(false, true);
        Ok(await f.Owner.CreatePlaylistAsync("Local", [E(Song(platform, Native(platform, 1)))])); Ok(await f.Run());
        Check(f.Server.Creates == 1 && f.Server.Entries.Single(e => !e.Favorite).Songs.SequenceEqual(new[] { 1 }));
        await f.Restart(); Ok(await f.Run()); Check(f.Server.Creates == 1);
    }
    private static async Task LocalUnmapped(string platform)
    {
        await using var f = new Fixture(platform);
        await f.AddFavorite(1);
        var collection = Ok(await f.Owner.ExecuteAsync("music.library.detail", new { id = "favorites" })).GetProperty("collection");
        var local = MusicSongFixture.Local("local:unmapped", "Synthetic local file");
        Ok(await f.Owner.ExecuteAsync("music.library.addSongs", new
        {
            id = "favorites", ifRevision = collection.GetProperty("revision").GetInt64(), songs = new[] { local }
        }));
        await f.Enable();
        var result = Ok(await f.Run());
        Check(S(result.GetProperty("status"), "phase") == "partial");
        Check(result.GetProperty("status").GetProperty("issues").EnumerateArray()
            .All(issue => S(issue, "code") == "UNMAPPED_SONG"));
        Check(f.Server.Favorite.Songs.SequenceEqual([1]) && f.Server.Writes == 1);
        var detail = Ok(await f.Owner.ExecuteAsync("music.library.detail", new { id = "favorites" }));
        Check(detail.GetProperty("songs").GetArrayLength() == 2 &&
            detail.GetProperty("songs").EnumerateArray().Single(song => S(song, "title") == "Synthetic local file")
                .GetProperty("sources").GetArrayLength() == 0);
        Ok(await f.Run());
        Check(f.Server.Writes == 1, "An unmapped local song does not replay successful writes");
    }
    private static async Task UnknownCreate(string platform)
    {
        await using var f = new Fixture(platform); f.Server.DropWrites = true;
        await f.Enable(false, true); Ok(await f.Owner.CreatePlaylistAsync("Local", [])); Code(await f.Run(), "CREATE_UNCONFIRMED");
        await f.Restart(); Code(await f.Run(), "CREATE_UNCONFIRMED"); Check(f.Server.Creates == 1);
    }
    private static async Task ReadOnly(string platform)
    {
        await using var f = new Fixture(platform); var remote = new Entry(platform == "kg" ? "1:301" : "301", "Subscription", writable: false); remote.Add(1); f.Server.Entries.Add(remote);
        await f.Enable(false, true); Ok(await f.Run());
        var list = Ok(await f.Owner.ExecuteAsync("music.library.list")).GetProperty("items"); var target = list.EnumerateArray().Single(c => c.GetProperty("name").GetString() == "Subscription");
        Check(S(target, "kind") == "network" && !target.GetProperty("editable").GetBoolean());
        Code(await f.Owner.ExecuteAsync("music.library.edit", new { id = target.GetProperty("id").GetString(), ifRevision = target.GetProperty("revision").GetInt64(), songs = Array.Empty<object>() }), "READ_ONLY");
        Code(await f.Owner.ExecuteAsync("music.library.detail", new { id = target.GetProperty("id").GetString() }), "READ_ONLY");
        Ok(await f.Run()); Check(f.Server.Writes == 0 && remote.Songs.Count == 1 && f.Server.SubscriptionMemberReads == 0);
        var snapshot = await f.Owner.ReadLibrarySyncAsync();
        Check(snapshot.Library.GetProperty("networkPlaylists").GetArrayLength() == 1 &&
            MusicTestStores.For(f.Store).SongIds().Count == 0 &&
            snapshot.Library.GetProperty("collections").GetArrayLength() == 1);
    }
    private static async Task Pages(string platform)
    {
        await using var f = new Fixture(platform); for (int i = 1; i <= 131; i++) f.Server.Favorite.Add(i);
        await f.Enable(); Ok(await f.Run()); var detail = Ok(await f.Owner.ExecuteAsync("music.library.detail", new { id = "favorites", offset = 100, limit = 100 }));
        Check(detail.GetProperty("total").GetInt64() == 131 && detail.GetProperty("songs").GetArrayLength() == 31 && f.Server.Writes == 0);
    }
    private static async Task IndependentKinds()
    {
        await using var f = new Fixture(); f.Server.Entries.Add(new("301", "Not selected")); await f.Enable(); Ok(await f.Run());
        Check(Ok(await f.Owner.ExecuteAsync("music.library.list")).GetProperty("total").GetInt64() == 1);
    }
    private static async Task<JsonElement> Confirm(Fixture f)
    {
        var status = Ok(await f.Sync.ExecuteAsync("music.librarySync.get")); var confirm = status.GetProperty("confirmation");
        return Ok(await f.Sync.ExecuteAsync("music.librarySync.confirm", new { token = confirm.GetProperty("token").GetString(), ifRevision = status.GetProperty("revision").GetInt64(), confirm = true }));
    }
    private static async Task EmptyConfirmation()
    {
        await using var f = new Fixture(); f.Server.Favorite.Add(1); await f.Enable(); Ok(await f.Run()); f.Server.Favorite.Songs.Clear(); f.Server.Favorite.Files.Clear();
        Code(await f.Run(), "CONFIRMATION_REQUIRED"); Check(Ok(await f.Owner.ExecuteAsync("music.library.detail", new { id = "favorites" })).GetProperty("songs").GetArrayLength() == 1);
        await Confirm(f); Ok(await f.Run()); Check(Ok(await f.Owner.ExecuteAsync("music.library.detail", new { id = "favorites" })).GetProperty("songs").GetArrayLength() == 0 && f.Server.Writes == 0);
    }
    private static async Task StaleConfirmation()
    {
        await using var f = new Fixture(); for (int i = 1; i <= 30; i++) f.Server.Favorite.Add(i); await f.Enable(); Ok(await f.Run());
        f.Server.Favorite.Songs.Clear(); f.Server.Favorite.Files.Clear(); Code(await f.Run(), "CONFIRMATION_REQUIRED"); await Confirm(f);
        f.Server.Favorite.Add(100); Code(await f.Run(), "CONFIRMATION_REQUIRED"); Check(f.Server.Writes == 0);
    }
    private static async Task LocalBulk()
    {
        await using var f = new Fixture(); for (int i = 1; i <= 25; i++) f.Server.Favorite.Add(i); await f.Enable(); Ok(await f.Run());
        var c = Ok(await f.Owner.ExecuteAsync("music.library.detail", new { id = "favorites" })).GetProperty("collection");
        Ok(await f.Owner.ExecuteAsync("music.library.edit", new { id = "favorites", ifRevision = c.GetProperty("revision").GetInt64(), songs = Array.Empty<object>() }));
        Code(await f.Run(), "CONFIRMATION_REQUIRED"); Check(f.Server.Writes == 0); await Confirm(f); Ok(await f.Run()); Check(f.Server.Favorite.Songs.Count == 0 && f.Server.Writes == 1);
    }
    private static async Task LocalPending()
    {
        await using var f = new Fixture(); f.Server.Favorite.Add(1); f.Server.Favorite.Add(2); await f.Enable(); Ok(await f.Run());
        var detail = Ok(await f.Owner.ExecuteAsync("music.library.detail", new { id = "favorites" }));
        Ok(await f.Owner.ExecuteAsync("music.library.removeSong", new { id = "favorites", ifRevision = detail.GetProperty("collection").GetProperty("revision").GetInt64(), songId = detail.GetProperty("songs")[0].GetProperty("songId").GetString() }));
        Ok(await f.Run()); Check(f.Server.Favorite.Songs.SequenceEqual(new[] { 2 }));
    }
    private static async Task RemoteDelta()
    {
        await using var f = new Fixture(); f.Server.Favorite.Add(1); f.Server.Favorite.Add(2); await f.Enable(); Ok(await f.Run());
        f.Server.Favorite.Songs.RemoveAt(0); f.Server.Favorite.Files.RemoveAt(0); Ok(await f.Run());
        var detail = Ok(await f.Owner.ExecuteAsync("music.library.detail", new { id = "favorites" })); Check(detail.GetProperty("songs").GetArrayLength() == 1 && f.Server.Writes == 0);
    }
    private static async Task Reorder(Fixture f)
    {
        f.Server.Favorite.Add(1); f.Server.Favorite.Add(2); await f.Enable(); Ok(await f.Run());
        var detail = Ok(await f.Owner.ExecuteAsync("music.library.detail", new { id = "favorites" })); var order = detail.GetProperty("songs").EnumerateArray().Select(s => s.GetProperty("songId").GetString()!).Reverse().ToArray();
        Ok(await f.Owner.ExecuteAsync("music.library.edit", new { id = "favorites", ifRevision = detail.GetProperty("collection").GetProperty("revision").GetInt64(), order }));
    }
    private static async Task OrderWy()
    { await using var f = new Fixture("wy"); await Reorder(f); Ok(await f.Run()); Check(f.Server.Favorite.Songs.SequenceEqual(new[] { 2, 1 }) && f.Server.Operations.SequenceEqual(["order"])); }
    private static async Task OrderTx()
    { await using var f = new Fixture(); await Reorder(f); var result = Ok(await f.Run()); Check(result.GetProperty("status").GetProperty("phase").GetString() == "partial" && f.Server.Writes == 0); }
    private static async Task OccurrencesKg()
    {
        await using var f = new Fixture("kg"); f.Server.Favorite.Add(1); f.Server.Favorite.Add(2); f.Server.Favorite.Add(1); await f.Enable(); Ok(await f.Run()); Check(f.Server.Writes == 0);
        var detail = Ok(await f.Owner.ExecuteAsync("music.library.detail", new { id = "favorites" })); Check(detail.GetProperty("songs").GetArrayLength() == 2);
        Ok(await f.Owner.ExecuteAsync("music.library.removeSong", new { id = "favorites", ifRevision = detail.GetProperty("collection").GetProperty("revision").GetInt64(), songId = detail.GetProperty("songs")[0].GetProperty("songId").GetString() }));
        Ok(await f.Run()); Check(f.Server.Favorite.Songs.SequenceEqual(new[] { 2 })); Check(f.Server.LastWrite!["members"]!.AsArray().Count == 2);
    }
    private static async Task CreationReadback()
    { await using var f = new Fixture(); await f.Enable(false, true); Ok(await f.Owner.CreatePlaylistAsync("Local", [])); f.Server.ThrowAfterCreate = true; Ok(await f.Run()); Check(f.Server.Creates == 1); }
    private static async Task AmbiguousCreate()
    { await using var f = new Fixture(); await f.Enable(false, true); Ok(await f.Owner.CreatePlaylistAsync("Local", [])); f.Server.Ambiguous = true; Code(await f.Run(), "CREATE_UNCONFIRMED"); Code(await f.Run(), "CREATE_UNCONFIRMED"); Check(f.Server.Creates == 1); }
    private static async Task FailureNotEmpty()
    { await using var f = new Fixture(); await f.Enable(); f.Server.FailReads = true; Code(await f.Run(), "UPSTREAM_ERROR"); Check(f.Server.Writes == 0); }
    private static async Task RepeatedPage()
    { await using var f = new Fixture(); for (int i = 1; i <= 110; i++) f.Server.Favorite.Add(i); await f.Enable(); f.Server.RepeatPage = true; Code(await f.Run(), "PAGINATION_STALLED"); Check(f.Server.Writes == 0); }
    private static async Task ConcurrentEdit()
    {
        await using var f = new Fixture(); await f.Enable(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); f.Server.Entered = entered; f.Server.Release = new();
        var run = f.Run(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); await f.AddFavorite(1); f.Server.Release.SetResult(); Code(await run, "SYNC_SUPERSEDED"); Check(f.Server.Writes == 0);
    }
    private static async Task AccountReplacement()
    {
        await using var f = new Fixture(); await f.AddFavorite(1); await f.Enable(); f.Server.OnRead = () => f.Sessions.Values["tx"] = Sessions.Make("tx", 2, "43");
        Code(await f.Run(), "STALE_SESSION"); Check(f.Server.Writes == 0); Code(await f.Run(), "ACCOUNT_REPLACED");
    }
    private static async Task ResetAccount()
    {
        await using var f = new Fixture(); f.Server.Favorite.Add(1); await f.Enable(); Ok(await f.Run()); var status = Ok(await f.Sync.ExecuteAsync("music.librarySync.get"));
        Ok(await f.Sync.ExecuteAsync("music.librarySync.resetAccount", new { platform = "tx", ifRevision = status.GetProperty("revision").GetInt64(), confirm = true }));
        var snap = await f.Owner.ReadLibrarySyncAsync(); Check(snap.Library.GetProperty("collections")[0].GetProperty("order").GetArrayLength() == 1 && snap.Library.GetProperty("collections")[0].GetProperty("bindings").GetArrayLength() == 0 && f.Server.Writes == 0);
    }
    private static async Task Lifecycle()
    {
        var clock = new Clock();
        await using var f = new Fixture(clock: clock);
        await f.Sync.StartAsync();
        await f.Sync.CancelShutdownPreparationAsync().WaitAsync(TimeSpan.FromSeconds(1));
        Check(clock.Timers.Count == 1, "UNPREPARED_CANCEL_CHANGED_SCHEDULER");
        await f.Enable(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); f.Server.Entered = entered; f.Server.Release = new();
        var run = f.Run(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Ok(await f.Sync.ExecuteAsync("music.librarySync.cancel")); Code(await run, "CANCELLED");
        await f.Sync.PrepareShutdownAsync(); Code(await f.Run(), "CLOSED"); await f.Sync.CancelShutdownPreparationAsync(); Ok(await f.Run());
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UnixEpoch; internal readonly List<Timer> Timers = [];
        public override DateTimeOffset GetUtcNow() => now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { var timer = new Timer(this, callback, state, dueTime, period); lock (Timers) Timers.Add(timer); return timer; }
        internal void Advance(TimeSpan by)
        { now += by; Timer[] copy; lock (Timers) copy = Timers.ToArray(); foreach (var timer in copy) timer.Advance(by); }
        internal sealed class Timer(Clock owner, TimerCallback callback, object? state, TimeSpan due, TimeSpan period) : ITimer
        {
            private bool closed;
            public bool Change(TimeSpan dueTime, TimeSpan newPeriod) { due = dueTime; period = newPeriod; return !closed; }
            internal void Advance(TimeSpan by) { if (closed || due == Timeout.InfiniteTimeSpan) return; due -= by; if (due <= TimeSpan.Zero) { due = period; callback(state); } }
            public void Dispose() { closed = true; lock (owner.Timers) owner.Timers.Remove(this); }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
    private static async Task Wait(Func<bool> condition)
    { for (int n = 0; n < 200 && !condition(); n++) await Task.Delay(10); Check(condition(), "BOUNDED_WAIT_TIMEOUT"); }
    private static async Task Scheduling()
    {
        var clock = new Clock(); await using var f = new Fixture(clock: clock); await f.Enable(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); f.Server.Entered = entered; f.Server.Release = new();
        await f.Sync.StartAsync(); await f.Sync.StartAsync(); Check(clock.Timers.Count == 1 && f.Server.Reads == 0);
        clock.Advance(TimeSpan.FromMinutes(4)); await Task.Delay(30); Check(f.Server.Reads == 0);
        clock.Advance(TimeSpan.FromMinutes(1)); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Code(await f.Run(), "SYNC_BUSY");
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Sync.StateChanged += () => completed.TrySetResult();
        f.Server.Release.SetResult(); await completed.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Task.Delay(30); int reads = f.Server.Reads;
        clock.Advance(TimeSpan.FromMinutes(5)); await Wait(() => f.Server.Reads > reads);
        await f.Sync.PrepareShutdownAsync(); Check(clock.Timers.Count == 0);
    }
    private static async Task ScheduledFailure()
    {
        var clock = new Clock(); await using var f = new Fixture(clock: clock); await f.Enable(); f.Server.FailReads = true;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Sync.StateChanged += () => finished.TrySetResult();
        await f.Sync.StartAsync(); clock.Advance(TimeSpan.FromMinutes(5));
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        int reads = f.Server.Reads; Check(reads > 0);
        clock.Advance(TimeSpan.FromMinutes(5)); await Task.Delay(30); Check(f.Server.Reads == reads);
        f.Server.FailReads = false; Ok(await f.Run()); Check(f.Server.Reads > reads);
    }
    private static async Task RequestBudget()
    {
        await using var f = new Fixture();
        for (int n = 0; n < 50; n++)
        {
            var entry = new Entry((300 + n).ToString(), "Remote " + n);
            for (int song = 1; song <= 101; song++) entry.Add(song);
            f.Server.Entries.Add(entry);
        }
        await f.Enable(false, true);
        Ok(await f.Run());
        var snapshot = await f.Owner.ReadLibrarySyncAsync();
        Check(snapshot.Library.GetProperty("collections").EnumerateArray().Count(collection => S(collection, "kind") == "playlist" && collection.GetProperty("order").GetArrayLength() == 101) == 50 && f.Server.MemberReads == 50 && f.Server.Writes == 0, "INCOMPLETE_SYNC_READS");
    }
    private static async Task MappingLookupBudget()
    {
        await using var f = new Fixture();
        var favorites = Ok(await f.Owner.ExecuteAsync("music.library.detail",
            new { id = "favorites" })).GetProperty("collection");
        var songs = Enumerable.Range(1, 32)
            .Select(n => Song("wy", Native("wy", n), n)).ToArray();
        Ok(await f.Owner.ExecuteAsync("music.library.addSongs", new
        {
            id = "favorites",
            ifRevision = favorites.GetProperty("revision").GetInt64(),
            songs
        }));
        await f.Enable();
        var result = Ok(await f.Run());
        var issues = result.GetProperty("status").GetProperty("issues").EnumerateArray().ToArray();
        Check(result.GetProperty("status").GetProperty("phase").GetString() == "partial" &&
            issues.Count(issue => S(issue, "code") == "UNMAPPED_SONG") == 1 &&
            issues.All(issue => S(issue, "code") == "UNMAPPED_SONG"),
            "MAPPING_LOOKUP_BOUNDARY");
        Check(f.Server.SearchReads == 32 && f.Server.Reads == 1 + 32 &&
            f.Server.Writes == 0, "MAPPING_LOOKUP_REQUEST_COUNT");
    }
    private static async Task UnknownTransaction(bool after)
    {
        await using var f = new Fixture(); await f.Enable(false, true); Ok(await f.Owner.CreatePlaylistAsync("Local", [])); f.Store.FailMarker = true; f.Store.FailAfter = after;
        Code(await f.Run(), "COMMIT_UNKNOWN"); Check(f.Server.Creates == 0);
        await f.Owner.ReconcileAsync(); if (after) { Code(await f.Run(), "CREATE_UNCONFIRMED"); Check(f.Server.Creates == 0); }
        else { Ok(await f.Run()); Check(f.Server.Creates == 1); }
    }
    private static async Task PartialAck()
    {
        await using var f = new Fixture(); await f.AddFavorite(1); await f.AddFavorite(2); await f.Enable(); f.Server.Partial = true; Code(await f.Run(), "WRITE_UNCONFIRMED");
        var snapshot = await f.Owner.ReadLibrarySyncAsync(); Check(snapshot.State.GetProperty("pending").ValueKind == JsonValueKind.Object);
        Check(snapshot.Library.GetProperty("collections")[0].GetProperty("intents").EnumerateObject().Any(p => p.Value.TryGetProperty("acknowledged", out var a) && a.GetArrayLength() == 1));
        await f.Restart(); Code(await f.Run(), "WRITE_UNCONFIRMED"); Check(f.Server.Writes == 1);
        f.Server.Favorite.Add(2); Ok(await f.Run()); Check(f.Server.Writes == 1);
    }
    private static async Task QueuePreserved()
    {
        await using var f = new Fixture(); await f.Enable(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); f.Server.Entered = entered; f.Server.Release = new();
        var run = f.Run();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var queue = Ok(await f.Owner.ExecuteAsync("music.queue.get"));
            Ok(await f.Owner.ReplaceQueueAsync([E(Song("tx", "MID1"))], 0, null, queue.GetProperty("version").GetInt64()));
            var current = Ok(await f.Owner.ExecuteAsync("music.queue.get"));
            string songId = S(current.GetProperty("current").GetProperty("song"), "songId");
            Check(Guid.TryParseExact(songId, "D", out _));
            f.Server.Release.TrySetResult(); Ok(await run);
            var again = Ok(await f.Owner.ExecuteAsync("music.queue.get"));
            Check(again.GetProperty("entries").GetArrayLength() == 1);
            Check(JsonElement.DeepEquals(again, current) &&
                MusicTestStores.For(f.Store).ContainsSong(songId) &&
                f.Server.Writes == 0, "SYNC_COMMIT_LOST_THE_NEW_QUEUE_REFERENCE_OR_CHANGED_ITS_FACTS");
            await f.Restart();
            var cold = Ok(await f.Owner.ExecuteAsync("music.queue.get"));
            Check(S(cold, "currentId") == S(current, "currentId") &&
                JsonElement.DeepEquals(cold.GetProperty("entries"), current.GetProperty("entries")) &&
                S(cold.GetProperty("current").GetProperty("song"), "songId") == songId,
                "CURRENT_FORMAT_COLD_READ_LOST_THE_CONCURRENT_QUEUE_RECORDING");
        }
        finally { f.Server.Release.TrySetResult(); await run; }
    }
    private sealed record SyncCorruption(string Name, Action<JsonObject, JsonObject, JsonObject> Apply);
    private static JsonObject Confirmation(string code = "EMPTY_REMOTE") => O(new
    {
        token = new string('a', 64),
        code,
        count = 1
    });
    private static JsonObject Pending(string collectionId, string remoteId, string operation,
        string[]? before = null, string[]? desired = null, string[]? ids = null, string[]? directory = null) => O(new
        {
            platform = "tx",
            accountId = "42",
            collectionId,
            remoteId,
            operation,
            before = before ?? [],
            desired = desired ?? [],
            ids = ids ?? [],
            name = collectionId == "favorites" ? "我的收藏" : "Local",
            directory = directory ?? []
        });
    private static JsonObject AddPlaylist(JsonObject library, bool bound = false)
    {
        library["nextId"] = 2;
        var playlist = O(new
        {
            id = "playlist:1",
            name = "Local",
            kind = "playlist",
            order = Array.Empty<string>(),
            revision = 0,
            manualOrder = false,
            bindings = Array.Empty<object>(),
            intents = new { },
            locallyCreated = true
        });
        library["collections"]!.AsArray().Add(playlist);
        if (bound) AddBinding(library, playlist, "701");
        return playlist;
    }
    private static void AddBinding(JsonObject library, JsonObject collection, string remoteId)
    {
        library["accounts"]!["tx"] = "42";
        collection["bindings"]!.AsArray().Add(O(new
        {
            platform = "tx",
            accountId = "42",
            id = remoteId,
            writable = true,
            baseline = Array.Empty<string>(),
            map = new { },
            name = collection["name"]!.GetValue<string>()
        }));
    }
    private static JsonObject Status(string phase, string code = "", params JsonObject[] issues) => O(new
    {
        phase,
        code,
        at = 1,
        issues
    });
    private static IEnumerable<SyncCorruption> SyncCorruptions()
    {
        yield return new("with an unsupported schema", (_, sync, _) => sync["schema"] = -1);
        yield return new("with a missing root field", (_, sync, _) => sync.Remove("status"));
        yield return new("with an extra document field", (_, sync, _) => sync["unexpected"] = true);
        yield return new("with an incomplete platform row", (_, sync, _) =>
            sync["platforms"]!["tx"]!.AsObject().Remove("accountId"));
        yield return new("with a non-boolean platform switch", (_, sync, _) =>
            sync["platforms"]!["tx"]!["favorites"] = "true");
        yield return new("with an incomplete pending write", (_, sync, library) =>
        {
            AddBinding(library, library["collections"]![0]!.AsObject(), "favorites");
            var pending = Pending("favorites", "favorites", "add", desired: ["MID1"], ids: ["MID1"]);
            pending.Remove("directory");
            sync["pending"] = pending;
        });
        yield return new("with an empty add operation", (_, sync, library) =>
        {
            AddBinding(library, library["collections"]![0]!.AsObject(), "favorites");
            sync["pending"] = Pending("favorites", "favorites", "add");
        });
        yield return new("with an inconsistent add journal", (_, sync, library) =>
        {
            AddBinding(library, library["collections"]![0]!.AsObject(), "favorites");
            sync["pending"] = Pending("favorites", "favorites", "add",
                before: ["MID1"], desired: ["MID1"], ids: ["MID2"]);
        });
        yield return new("with an inconsistent remove journal", (_, sync, library) =>
        {
            AddBinding(library, library["collections"]![0]!.AsObject(), "favorites");
            sync["pending"] = Pending("favorites", "favorites", "remove",
                before: ["MID1", "MID2"], desired: ["MID2"], ids: ["MID2"]);
        });
        yield return new("with a non-permutation order journal", (_, sync, library) =>
        {
            AddBinding(library, library["collections"]![0]!.AsObject(), "favorites");
            sync["pending"] = Pending("favorites", "favorites", "order",
                before: ["MID1", "MID2"], desired: ["MID2", "MID3"]);
        });
        yield return new("with a dangling pending collection", (_, sync, library) =>
        {
            library["accounts"]!["tx"] = "42";
            sync["pending"] = Pending("playlist:404", "", "create");
        });
        yield return new("with a dangling pending binding", (_, sync, library) =>
        {
            AddBinding(library, library["collections"]![0]!.AsObject(), "favorites");
            sync["pending"] = Pending("favorites", "missing", "add", desired: ["MID1"], ids: ["MID1"]);
        });
        yield return new("with a create journal beside an existing platform binding", (_, sync, library) =>
        {
            _ = AddPlaylist(library, bound: true);
            sync["pending"] = Pending("playlist:1", "", "create");
        });
        yield return new("with a pending account outside the library account", (_, sync, library) =>
        {
            AddBinding(library, library["collections"]![0]!.AsObject(), "favorites");
            sync["platforms"]!["tx"]!["accountId"] = "43";
            sync["pending"] = Pending("favorites", "favorites", "add", desired: ["MID1"], ids: ["MID1"]);
        });
        yield return new("with confirmation and approval together", (_, sync, _) =>
        {
            sync["confirmation"] = Confirmation();
            sync["approved"] = Confirmation();
            sync["status"] = Status("blocked", "CONFIRMATION_REQUIRED");
        });
        yield return new("with confirmation and pending together", (_, sync, library) =>
        {
            _ = AddPlaylist(library);
            library["accounts"]!["tx"] = "42";
            sync["pending"] = Pending("playlist:1", "", "create");
            sync["confirmation"] = Confirmation();
            sync["status"] = Status("blocked", "CONFIRMATION_REQUIRED");
        });
        yield return new("with an unknown confirmation code", (_, sync, _) =>
        {
            sync["confirmation"] = Confirmation("LEGACY_CONFIRMATION");
            sync["status"] = Status("blocked", "CONFIRMATION_REQUIRED");
        });
        yield return new("with confirmation status but no confirmation", (_, sync, _) =>
            sync["status"] = Status("blocked", "CONFIRMATION_REQUIRED"));
        yield return new("with an idle error code", (_, sync, _) =>
            sync["status"] = Status("idle", "SYNC_FAILED"));
        yield return new("with issues in a synced status", (_, sync, _) =>
            sync["status"] = Status("synced", "", O(new { platform = "tx", collectionId = "favorites", code = "UNMAPPED_SONG" })));
        yield return new("with an empty partial status", (_, sync, _) =>
            sync["status"] = Status("partial"));
        yield return new("with an issue for a missing collection", (_, sync, _) =>
            sync["status"] = Status("partial", "", O(new { platform = "tx", collectionId = "missing", code = "UNMAPPED_SONG" })));
        yield return new("with sync state but no library state", (root, _, _) =>
        {
            root["areas"]!.AsObject().Remove("library");
        });
    }
    private static async Task Corrupt(Action<JsonObject, JsonObject, JsonObject> apply)
    {
        await using var f = new Fixture();
        await f.Enable();
        await f.Sync.DisposeAsync();
        await f.Owner.AbortAsync();
        var root = JsonNode.Parse(f.Store.Data!)!.AsObject();
        var sync = root["areas"]!["library-sync"]!.AsObject();
        var library = root["areas"]!["library"]!["library"]!.AsObject();
        apply(root, sync, library);
        f.Store.Data = LJ.Bytes(root);
        byte[] original = f.Store.Data.ToArray();
        f.Store.Writes = 0;
        f.Server.Reads = f.Server.Writes = f.Server.Creates = 0;
        f.Owner = new(MusicTestStores.For(f.Store));
        f.Sync = new(f.Owner, f.Sessions, f.Runtime.Client);
        Code(await f.Owner.ExecuteAsync("music.library.list"), "CORRUPT_SYNC");
        Code(await f.Sync.ExecuteAsync("music.librarySync.get"), "CORRUPT_SYNC");
        Code(await f.Sync.ExecuteAsync("music.librarySync.run"), "CORRUPT_SYNC");
        Code(await f.Sync.ExecuteAsync("music.librarySync.cancel"), "CORRUPT_SYNC");
        Code(await f.Sync.ExecuteAsync("music.librarySync.set",
            new { platform = "tx", favorites = false, playlists = false, ifRevision = 0, confirm = true }), "CORRUPT_SYNC");
        Code(await f.Sync.ExecuteAsync("music.librarySync.confirm",
            new { token = new string('a', 64), ifRevision = 0, confirm = true }), "CORRUPT_SYNC");
        Code(await f.Sync.ExecuteAsync("music.librarySync.resetAccount",
            new { platform = "tx", ifRevision = 0, confirm = true }), "CORRUPT_SYNC");
        Check(f.Server.Reads == 0 && f.Server.Writes == 0 && f.Server.Creates == 0 &&
            f.Store.Writes == 0 && f.Store.Data!.SequenceEqual(original));
    }
    private static async Task SQLiteReopen()
    {
        string path = Path.GetFullPath(TestWorkspace.DataPath("music-library-sync-20261023", "dpapi-" + Guid.NewGuid().ToString("N"))); Directory.CreateDirectory(path);
        var sessions = new Sessions(); var server = new Server("tx");
        await using var runtime = new MusicPlatformRuntimeFixture(); runtime.Client.BindSessions(sessions); runtime.OnRequest = server.Respond;
        await using var store = new MusicRecordTestStore(path);
        var owner = new MusicLibraryOwner(MusicTestStores.For(store));
        await using (var sync = new MusicLibrarySyncService(owner, sessions, runtime.Client))
        {
            Ok(await sync.ExecuteAsync("music.librarySync.set", new { platform = "tx", favorites = false, playlists = true, ifRevision = 0, confirm = true }));
            Ok(await owner.CreatePlaylistAsync("Local", [])); server.DropWrites = true; Code(await sync.ExecuteAsync("music.librarySync.run"), "CREATE_UNCONFIRMED");
        }
        await owner.AbortAsync(); owner = new(MusicTestStores.For(store)); await using var next = new MusicLibrarySyncService(owner, sessions, runtime.Client);
        Code(await next.ExecuteAsync("music.librarySync.run"), "CREATE_UNCONFIRMED"); Check(server.Creates == 1);
        var snapshot = await owner.ReadLibrarySyncAsync(); Check(!snapshot.State.GetRawText().Contains("synthetic-only", StringComparison.Ordinal));
        await next.AbortAsync(); await owner.AbortAsync();
    }
    private static async Task ViewModel()
    {
        await using var f = new Fixture(); await using var vm = new MusicLibrarySyncViewModel(f.Sync.ExecuteAsync); await vm.ActivateAsync(); Check(vm.CanEdit);
        var row = vm.Platforms.Single(p => p.Platform == "tx");
        await vm.SetAsync(row, "favorites", true, confirmed: false); Check(f.Server.Reads == 0);
        await vm.SetAsync(row, "favorites", true, confirmed: true); Check(f.Server.Reads > 0 && row.Favorites);
        await vm.DeactivateAsync(); int requests = f.Server.Reads;
        await vm.SetAsync(row, "playlists", true, confirmed: true); Check(f.Server.Reads == requests);
        await vm.ActivateAsync(); Check(vm.Platforms.Single(p => p.Platform == "tx").Favorites);
    }
    private static Task View()
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { var view = new MusicAccountsView(); view.Measure(new System.Windows.Size(900, 700)); Check(view.DesiredSize.Width > 0); result.SetResult(); } catch (Exception e) { result.SetException(e); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return result.Task;
    }
    private static async Task KgCloudMissing()
    {
        await using var f = new Fixture("kg"); await Reorder(f); var result = Ok(await f.Run());
        Check(S(result.GetProperty("status"), "phase") == "synced" && f.Server.Operations.SequenceEqual(["order"]) &&
            f.Server.Favorite.Songs.SequenceEqual([2, 1]));
    }
    private static async Task DeletePlaylist()
    {
        await using var f = new Fixture(); f.Server.Entries.Add(new("701", "Local")); await f.Enable(false, true); Ok(await f.Run());
        var c = Ok(await f.Owner.ExecuteAsync("music.library.list")).GetProperty("items").EnumerateArray().Single(c => c.GetProperty("kind").GetString() == "playlist");
        Ok(await f.Owner.ExecuteAsync("music.library.remove", new { id = c.GetProperty("id").GetString(), ifRevision = c.GetProperty("revision").GetInt64() }));
        Code(await f.Run(), "CONFIRMATION_REQUIRED"); await Confirm(f); Ok(await f.Run()); Check(f.Server.Writes == 1 && f.Server.Entries.Count == 1);
        Ok(await f.Run()); Check(f.Server.Writes == 1);
    }
    private static async Task KgIdentityRace()
    {
        await using var f = new Fixture("kg"); f.Server.Favorite.Add(1); await f.Enable();
        Ok(await f.Run());
        await f.AddFavorite(2);
        int validationRead = f.Server.Reads + 2;
        bool changed = false;
        f.Server.ObserveRead = read =>
        {
            if (read != validationRead) return;
            f.Server.Favorite.Songs[0] = 2;
            changed = true;
        };
        var result = Ok(await f.Run());
        Check(changed && f.Server.Reads == validationRead, "WRITE_VALIDATION_READ_NOT_OBSERVED");
        Check(result.GetProperty("status").GetProperty("issues").EnumerateArray().Any(e => S(e, "code") == "REMOTE_CHANGED"));
        Check(f.Server.Writes == 0);
    }
    private static async Task LosslessId()
    {
        await using var f = new Fixture("wy"); const string id = "9007199254740993"; f.Server.Entries.Add(new(id, "Exact")); await f.Enable(false, true); Ok(await f.Run());
        var snapshot = await f.Owner.ReadLibrarySyncAsync(); Check(snapshot.Library.GetProperty("collections").EnumerateArray().SelectMany(c => c.GetProperty("bindings").EnumerateArray()).Any(b => b.GetProperty("id").GetString() == id));
    }
    private static async Task DisabledPending()
    {
        await using var f = new Fixture(); await f.Enable(false, true); Ok(await f.Owner.CreatePlaylistAsync("Local", [])); f.Server.DropWrites = true; Code(await f.Run(), "CREATE_UNCONFIRMED");
        var before = Ok(await f.Sync.ExecuteAsync("music.librarySync.get")); Ok(await f.Sync.ExecuteAsync("music.librarySync.set", new { platform = "tx", favorites = false, playlists = false, ifRevision = before.GetProperty("revision").GetInt64(), confirm = true }));
        int reads = f.Server.Reads; Ok(await f.Run()); Check(f.Server.Reads == reads && f.Server.Creates == 1);
        await f.Restart(); Ok(await f.Run()); Check(f.Server.Reads == reads && (await f.Owner.ReadLibrarySyncAsync()).State.GetProperty("pending").ValueKind == JsonValueKind.Object);
    }
    private static async Task BusyCancel()
    {
        await using var f = new Fixture(); await f.Enable(); await using var vm = new MusicLibrarySyncViewModel(f.Sync.ExecuteAsync); await vm.ActivateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); f.Server.Entered = entered; f.Server.Release = new();
        var row = vm.Platforms.Single(p => p.Platform == "tx");
        var run = vm.SetAsync(row, "playlists", true, confirmed: true);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Check(!vm.CanEdit);
        Ok(await f.Sync.ExecuteAsync("music.librarySync.cancel"));
        await run;
        Check(vm.CanEdit && row.Favorites && row.Playlists && f.Server.Writes == 0);
    }
}
