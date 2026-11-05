using System.IO;
using System.Net;
using System.Net.Http;
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
            yield return new($"Music sync {p} full playlist create bind members", () => Create(p));
            yield return new($"Music sync {p} unknown creation never duplicated after restart", () => UnknownCreate(p));
            yield return new($"Music sync {p} read-only binding never writes", () => ReadOnly(p));
            yield return new($"Music sync {p} full paged members retained", () => Pages(p));
            yield return new($"Music sync {p} local source-free song does not block other favorites", () => LocalUnmapped(p));
        }
        yield return new("Music sync per-platform favorite and playlist switches never over-select", IndependentKinds);
        yield return new("Music sync remote empty requires exact explicit confirmation", EmptyConfirmation);
        yield return new("Music sync stale confirmation cannot authorize changed remote", StaleConfirmation);
        yield return new("Music sync large local deletes require confirmation", LocalBulk);
        yield return new("Music sync bilateral local pending beats unacknowledged old remote", LocalPending);
        yield return new("Music sync remote deletion propagates only after native delta", RemoteDelta);
        yield return new("Music sync WY pure order uses update and verifies order", OrderWy);
        yield return new("Music sync QQ order unsupported without clear and re-add", OrderTx);
        yield return new("Music sync KG repeated recording preserves file occurrence and removal", OccurrencesKg);
        yield return new("Music sync creation timeout success is recovered without second create", CreationReadback);
        yield return new("Music sync ambiguous create preserves fence", AmbiguousCreate);
        yield return new("Music sync remote request failure is not empty success", FailureNotEmpty);
        yield return new("Music sync repeated page and partial count block every write", RepeatedPage);
        yield return new("Music sync local edit during pull supersedes the whole plan", ConcurrentEdit);
        yield return new("Music sync account replacement fences delayed read and write", AccountReplacement);
        yield return new("Music sync account reset is explicit local only", ResetAccount);
        yield return new("Music sync cancellation and reversible close retain singleton owner", Lifecycle);
        yield return new("Music sync startup defers network and five minute clock keeps one flight", Scheduling);
        yield return new("Music sync blocked scheduled round does not retry-loop", ScheduledFailure);
        yield return new("Music sync completes every selected collection without a round request quota", RequestBudget);
        yield return new("Music sync resolves every selected mapping without a lookup quota", MappingLookupBudget);
        yield return new("Music sync unknown before CAS prevents remote dispatch", () => UnknownCas(false));
        yield return new("Music sync unknown after CAS durable fence recovers without replay", () => UnknownCas(true));
        yield return new("Music sync pending write partial acknowledgement survives reopen", PartialAck);
        yield return new("Music sync independent queue changes survive sync envelope commit", QueuePreserved);
        foreach (var corruption in SyncCorruptions())
        {
            var current = corruption;
            yield return new("Music sync rejects current document " + current.Name + " without side effects",
                () => Corrupt(current.Apply));
        }
        yield return new("Music sync real DPAPI restart settings bindings pending without credentials", Dpapi);
        yield return new("Music sync viewmodel immediate switches confirmation and visibility", ViewModel);
        yield return new("Music platform settings XAML initializes on native STA without module state", View);
        yield return new("Music sync cloud capability missing blocks only KG delete and order", KgCloudMissing);
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
    private sealed class Store : IDocumentStore
    {
        internal byte[]? Data; internal int Writes; internal bool FailMarker, FailAfter;
        public Task<DocumentSnapshot> ReadAsync(string ns, CancellationToken cancellationToken = default) => Task.FromResult(new DocumentSnapshot(Data?.ToArray(), Data is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(Data)));
        public Task<DocumentFingerprint> WriteAsync(string ns, ReadOnlyMemory<byte> data, DocumentFingerprint expected, CancellationToken cancellationToken = default)
        {
            Check(ns == MusicLibraryOwner.DocumentNamespace, "SECOND_NAMESPACE"); cancellationToken.ThrowIfCancellationRequested();
            var current = Data is null ? DocumentFingerprint.Missing : DocumentFingerprint.FromBytes(Data);
            if (current != expected) throw new DocumentStoreException(DocumentStoreError.Conflict);
            var obj = JsonNode.Parse(data.Span)!.AsObject(); bool marker = obj["entries"]!.AsArray().Any(e => e!["key"]!.GetValue<string>().Contains("library-sync", StringComparison.Ordinal) && e["value"]?["pending"] is JsonObject);
            bool fail = FailMarker && marker; if (fail) FailMarker = false;
            if (fail && !FailAfter) throw new DocumentStoreException(DocumentStoreError.Storage);
            Data = data.ToArray(); Writes++;
            if (fail) throw new DocumentStoreException(DocumentStoreError.Storage);
            return Task.FromResult(DocumentFingerprint.FromBytes(Data));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly Store Store = new(); internal readonly Sessions Sessions = new(); internal readonly Server Server;
        internal readonly MusicPlatformHttp Http; internal MusicLibraryOwner Owner; internal MusicLibrarySyncService Sync;
        internal readonly MusicClientContextFixture Clients;
        internal Fixture(string platform = "tx", TimeProvider? clock = null)
        { Server = new(platform); Http = MusicPlatformHttp.CreateWithHandler(Server); Clients = new(Http, clock: clock); if (platform == "tx") Clients.SeedQqAsync(TimeProvider.System).GetAwaiter().GetResult(); Owner = new(Store); Sync = new(Owner, Sessions, Http, Clients.Owner, null, clock); }
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
        { await Sync.DisposeAsync(); await Owner.AbortAsync(); Owner = new(Store); Sync = new(Owner, Sessions, Http, Clients.Owner); }
        public async ValueTask DisposeAsync() { await Sync.DisposeAsync(); await Owner.AbortAsync(); await Clients.DisposeAsync(); Http.Dispose(); }
    }
    private sealed class Entry(string id, string name, bool favorite = false, bool writable = true)
    {
        internal string Id = id, Name = name; internal bool Favorite = favorite, Writable = writable;
        internal List<int> Songs = []; internal List<int> Files = [];
        internal void Add(int n) { Songs.Add(n); Files.Add(Files.Count == 0 ? 5000 : Files.Max() + 1); }
    }
    private sealed class Server(string platform) : HttpMessageHandler
    {
        internal string Platform = platform; internal List<Entry> Entries = [new(platform == "wy" ? "101" : platform == "tx" ? "favorites" : "0:101", platform == "kg" ? "我喜欢" : "Favorites", true)];
        internal Entry Favorite => Entries[0]; internal int Reads, Writes, Creates; internal bool FailReads, RepeatPage, DropWrites, ThrowAfterCreate, Ambiguous, Partial;
        internal string CreateName = "Local"; internal int FavoriteTarget = 1; internal bool FavoriteAdd = true;
        internal TaskCompletionSource? Entered, Release; internal Action? OnRead; internal Action<int>? ObserveRead;
        internal Action<JsonObject>? KgListInfo;
        internal readonly HashSet<int> WyMissingTracks = [];
        internal bool WyDetailsAvailable, WyDetailNetworkFailure, WyWrongDetail, WyConflictingDuplicate;
        internal int WyDetailCode = 200, WyDetailReads;
        internal Action? AfterWrite;
        internal List<string> Operations = [];
        internal JsonObject? LastWrite;
        private static JsonObject Obj(params (string Key, object? Value)[] fields)
        { var o = new JsonObject(); foreach (var (k, v) in fields) o[k] = v is JsonNode n ? n.DeepClone() : JsonSerializer.SerializeToNode(v); return o; }
        private JsonObject Raw(int n)
        {
            if (Platform == "wy") return Obj(("id", Native(Platform, n)), ("name", "Track " + n), ("dt", 200000), ("ar", new[] { new { name = "Synthetic Artist" } }), ("al", new { name = "Synthetic Album" }));
            if (Platform == "tx") return Obj(("mid", Native(Platform, n)), ("id", (1000 + n).ToString()), ("type", 0), ("title", "Track " + n), ("interval", 200), ("singer", new[] { new { name = "Synthetic Artist" } }), ("album", new { name = "Synthetic Album", mid = "ALBUM" }), ("file", new { }));
            return Obj(("hash", Native(Platform, n)), ("name", "Synthetic Artist - Track " + n), ("timelen", 200000), ("album_id", "5"), ("mixsongid", "6"), ("albuminfo", new { name = "Synthetic Album", id = "5" }), ("singerinfo", new[] { new { name = "Synthetic Artist" } }));
        }
        private static int Number(JsonNode? n) => int.Parse(n?.ToString() ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string path = request.RequestUri!.AbsolutePath; JsonObject body = request.Content is null ? new() : await Decode(request);
            string operation = Platform == "tx" ? body["req_0"]!["method"]!.GetValue<string>() : path;
            bool write = operation is "AddPlaylist" or "DelPlaylist" or "AddSonglist" or "DelSonglist" || path.Contains("/radio/like", StringComparison.Ordinal) ||
                path.Contains("/playlist/create", StringComparison.Ordinal) || path.Contains("/playlist/remove", StringComparison.Ordinal) || path.Contains("/playlist/manipulate/", StringComparison.Ordinal) ||
                path is "/cloudlist.service/v5/add_list" or "/cloudlist.service/v6/add_song" or "/v4/delete_songs";
            if (!write)
            {
                Reads++; ObserveRead?.Invoke(Reads); var action = OnRead; OnRead = null; action?.Invoke();
                if (Entered is not null) { var entered = Entered; var release = Release!; Entered = null; entered.TrySetResult(); await release.Task.WaitAsync(token); }
                if (FailReads) return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") };
            }
            else { Writes++; Operations.Add(operation); LastWrite = (JsonObject)body.DeepClone(); }
            JsonObject data = Platform switch { "wy" => Wy(path, body), "tx" => Tx(operation, body["req_0"]!["param"]!.AsObject()), _ => Kg(path, body, request.RequestUri.Query) };
            if (write) { var after = AfterWrite; AfterWrite = null; after?.Invoke(); }
            return new(HttpStatusCode.OK) { Content = new StringContent(data.ToJsonString(), Encoding.UTF8, "application/json") };
        }
        private async Task<JsonObject> Decode(HttpRequestMessage request)
        {
            string body = await request.Content!.ReadAsStringAsync(); if (Platform != "wy") return JsonNode.Parse(body)!.AsObject();
            if (request.RequestUri!.AbsolutePath.StartsWith("/weapi/", StringComparison.Ordinal)) return new();
            string value = Uri.UnescapeDataString(body.Split('=', 2)[1]);
            using var aes = Aes.Create(); aes.Key = Encoding.ASCII.GetBytes("e82ckenh8dichen8");
            string plain = Encoding.UTF8.GetString(aes.DecryptEcb(Convert.FromHexString(value), PaddingMode.PKCS7));
            return JsonNode.Parse(plain.Split("-36cd479b6b5-", StringSplitOptions.None)[1])!.AsObject();
        }
        private Entry New()
        {
            Creates++; string id = Platform == "kg" ? "0:" + (400 + Creates) : (400 + Creates).ToString();
            var entry = new Entry(id, CreateName); if (!DropWrites) Entries.Add(entry);
            if (Ambiguous && !DropWrites) Entries.Add(new(Platform == "kg" ? "0:999" : "999", CreateName));
            if (ThrowAfterCreate) throw new HttpRequestException("SYNTHETIC_TIMEOUT"); return entry;
        }
        private JsonObject Wy(string path, JsonObject p)
        {
            if (path.EndsWith("/user/playlist", StringComparison.Ordinal)) return Obj(("code", 200), ("more", false), ("playlist", Entries.Select(e => Obj(("id", e.Id), ("name", e.Name), ("creator", new { userId = e.Writable ? "42" : "77" }), ("specialType", e.Favorite ? 5 : 0))).ToArray()));
            if (path.EndsWith("/playlist/detail", StringComparison.Ordinal))
            {
                var e = Entries.Single(e => e.Id == p["id"]!.ToString());
                var tracks = e.Songs.Where(n => !WyMissingTracks.Contains(n)).Select(Raw).ToArray();
                if (WyConflictingDuplicate && tracks.Length > 1) tracks[^1]["name"] = "Synthetic conflict (live)";
                return Obj(("code", 200), ("playlist", Obj(("id", e.Id), ("name", e.Name), ("creator", new { userId = e.Writable ? "42" : "77" }), ("specialType", e.Favorite ? 5 : 0),
                    ("trackCount", e.Songs.Count), ("trackIds", e.Songs.Select(n => new { id = Native(Platform, n) }).ToArray()), ("tracks", tracks))));
            }
            if (path.EndsWith("/song/detail", StringComparison.Ordinal))
            {
                WyDetailReads++;
                if (WyDetailNetworkFailure) throw new HttpRequestException("SYNTHETIC_DETAIL_UNAVAILABLE");
                var details = WyWrongDetail ? new[] { Raw(999) } : WyDetailsAvailable ?
                    Entries.SelectMany(e => e.Songs).Distinct().Where(WyMissingTracks.Contains).Select(Raw).ToArray() : [];
                return Obj(("code", WyDetailCode), ("songs", details));
            }
            if (path.EndsWith("/radio/like", StringComparison.Ordinal)) { if (!DropWrites) Change(Favorite, [FavoriteTarget], FavoriteAdd); return Obj(("code", 200)); }
            if (path.EndsWith("/playlist/create", StringComparison.Ordinal)) { var e = New(); return Obj(("code", 200), ("id", e.Id)); }
            if (path.EndsWith("/playlist/manipulate/tracks", StringComparison.Ordinal))
            {
                var e = Entries.Single(e => e.Id == p["pid"]!.ToString()); var ids = JsonNode.Parse(p["trackIds"]!.GetValue<string>())!.AsArray().Select(n => Number(n) - 1000).ToArray();
                if (!DropWrites) { if (p["op"]!.ToString() == "update") { e.Songs = ids.ToList(); } else Change(e, ids, p["op"]!.ToString() == "add"); }
                return Obj(("code", 200));
            }
            if (path.EndsWith("/playlist/remove", StringComparison.Ordinal)) { if (!DropWrites) Entries.RemoveAll(e => !e.Favorite); return Obj(("code", 200)); }
            if (path.Contains("cloudsearch", StringComparison.Ordinal)) return Obj(("code", 200), ("result", new { songs = Array.Empty<object>(), songCount = 0 }));
            throw new InvalidOperationException("UNEXPECTED_SYNTHETIC_WY_ROUTE");
        }
        private JsonObject Tx(string method, JsonObject p)
        {
            JsonObject data;
            if (method == "GetPlaylistByUin") data = Obj(("bFinish", true), ("total", Entries.Count(e => !e.Favorite && e.Writable)), ("v_playlist", Entries.Where(e => !e.Favorite && e.Writable).Select(e => Card(e)).ToArray()));
            else if (method == "CgiGetPlaylistFavInfo") data = Obj(("hide", false), ("v_failTids", Array.Empty<string>()), ("total", Entries.Count(e => !e.Writable)), ("hasmore", 0), ("v_list", Entries.Where(e => !e.Writable).Select(e => Card(e)).ToArray()));
            else if (method == "CgiGetDiss")
            {
                var e = Number(p["dirid"]) == 201 ? Favorite : Entries.Single(e => e.Id == p["disstid"]!.ToString());
                int offset = RepeatPage ? 0 : Number(p["song_begin"]), limit = Number(p["song_num"]); var songs = e.Songs.Skip(offset).Take(limit).Select(Raw).ToArray();
                data = Obj(("total_song_num", e.Songs.Count), ("hasmore", Number(p["song_begin"]) + songs.Length < e.Songs.Count ? 1 : 0), ("songlist_size", songs.Length), ("songlist", songs),
                    ("dirinfo", new { id = e.Favorite ? "0" : e.Id, title = e.Name, dirid = e.Favorite ? "201" : e.Id, creator = new { musicid = e.Writable ? "42" : "77" } }));
            }
            else if (method == "AddPlaylist") { CreateName = p["dirName"]!.GetValue<string>(); var e = New(); data = Obj(("id", e.Id)); }
            else if (method == "DelPlaylist") { if (!DropWrites) Entries.RemoveAll(e => e.Id == p["dirId"]!.ToString()); data = Obj(("dirid", p["dirId"])); }
            else if (method is "AddSonglist" or "DelSonglist")
            {
                var e = Number(p["dirId"]) == 201 ? Favorite : Entries.Single(e => e.Id == p["tid"]!.ToString());
                var ids = p["v_songInfo"]!.AsArray().Select(s => Number(s!["songId"]) - 1000).ToArray();
                if (method == "DelSonglist") Check(p["v_songInfo"]!.AsArray().All(s => Number(s!["songType"]) == 0), "QQ_DELETE_TYPE");
                if (!DropWrites) Change(e, Partial ? ids.Take(1).ToArray() : ids, method == "AddSonglist"); data = Obj(("retCode", 0));
            }
            else if (method == "DoSearchForQQMusicMobile") data = Obj(("meta", new { sum = 0, nextpage = -1 }), ("body", new { item_song = Array.Empty<object>() }));
            else throw new InvalidOperationException("UNEXPECTED_SYNTHETIC_TX_ROUTE");
            return Obj(("code", 0), ("req_0", new { code = 0, data }));
        }
        private object Card(Entry e) => new { tid = e.Id, id = e.Id, dirid = e.Id, uin = e.Writable ? "42" : "77", title = e.Name, invalid = false };
        private JsonObject Kg(string path, JsonObject p, string query)
        {
            JsonObject data;
            if (path == "/v7/get_all_list")
            {
                var rows = Entries.Select(e => new { listid = e.Id.Split(':')[1], type = e.Writable ? 0 : 1, source = 1, name = e.Name, count = e.Songs.Count,
                    list_create_userid = e.Writable ? "42" : "77", is_def = e.Favorite ? 1 : 0, list_ver = 1, global_collection_id = "global" + e.Id.Split(':')[1] }).ToArray();
                data = Obj(("count", rows.Length), ("total_ver", 1), ("info", rows.Skip((Number(p["page"]) - 1) * 30).Take(30).ToArray()));
            }
            else if (path is "/v4/get_list_all_file_v3" or "/pubsongs/v2/get_other_list_file_nofilt")
            {
                string id; int offset;
                if (p["listid"] is not null) { id = "0:" + p["listid"]; offset = (Number(p["page"]) - 1) * 30; }
                else { var q = Query(query); id = "1:" + q["global_collection_id"].Replace("global", "", StringComparison.Ordinal); offset = int.Parse(q["begin_idx"], System.Globalization.CultureInfo.InvariantCulture); }
                var e = Entries.Single(e => e.Id == id); var rows = e.Songs.Select((n, i) => { var row = Raw(n); row["fileid"] = e.Files[i]; row["sort"] = i; return row; }).Skip(offset).Take(30).ToArray();
                var info = Obj(("count", e.Songs.Count), ("list_ver", 1)); KgListInfo?.Invoke(info);
                data = Obj(("count", e.Songs.Count), ("list_info", info), ("info", rows));
            }
            else if (path == "/cloudlist.service/v5/add_list") { CreateName = p["name"]!.GetValue<string>(); var e = New(); data = Obj(("listid", e.Id.Split(':')[1])); }
            else if (path == "/cloudlist.service/v6/add_song")
            { var e = Entries.Single(e => e.Id == "0:" + p["listid"]); var ids = p["data"]!.AsArray().Select(r => Convert.ToInt32(r!["hash"]!.ToString(), 16)).ToArray(); if (!DropWrites) Change(e, ids, true); data = new(); }
            else if (path == "/v4/delete_songs")
            { var e = Entries.Single(e => e.Id == "0:" + p["listid"]); var files = p["data"]!.AsArray().Select(r => Number(r!["fileid"])).ToHashSet(); if (!DropWrites) for (int i = e.Files.Count - 1; i >= 0; i--) if (files.Contains(e.Files[i])) { e.Files.RemoveAt(i); e.Songs.RemoveAt(i); } data = new(); }
            else if (path == "/v2/search/song") data = Obj(("total", 0), ("lists", Array.Empty<object>()));
            else throw new InvalidOperationException("UNEXPECTED_SYNTHETIC_KG_ROUTE");
            return Obj(("status", 1), ("error_code", 0), ("data", data));
        }
        private static Dictionary<string, string> Query(string text) => text.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));
        private static void Change(Entry e, int[] ids, bool add)
        {
            foreach (int id in ids) if (add) { if (!e.Songs.Contains(id)) e.Add(id); }
                else for (int i = e.Songs.Count - 1; i >= 0; i--) if (e.Songs[i] == id) { e.Songs.RemoveAt(i); e.Files.RemoveAt(i); }
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
        Check(f.Server.Reads == (platform == "tx" ? 2 : 4), "UNCHANGED_PUSH_REREAD");
    }
    private static async Task FavoriteWrite(string platform)
    {
        await using var f = new Fixture(platform); await f.AddFavorite(1); await f.Enable(); Ok(await f.Run()); Check(f.Server.Favorite.Songs.SequenceEqual(new[] { 1 })); Check(f.Server.Writes == 1);
        var snapshot = await f.Owner.ReadLibrarySyncAsync(); var intents = snapshot.Library.GetProperty("collections")[0].GetProperty("intents");
        Check(intents.EnumerateObject().All(i => !i.Value.GetProperty("pending").GetBoolean() && i.Value.GetProperty("acknowledged").GetArrayLength() == 1));
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
            detail.GetProperty("songs").EnumerateArray().Single(song => S(song, "songId") == "local:unmapped")
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
        Ok(await f.Owner.ExecuteAsync("music.library.edit", new { id = target.GetProperty("id").GetString(), ifRevision = target.GetProperty("revision").GetInt64(), songs = Array.Empty<object>() }));
        Ok(await f.Run()); Check(f.Server.Writes == 0 && remote.Songs.Count == 1);
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
    { await using var f = new Fixture("wy"); await Reorder(f); Ok(await f.Run()); Check(f.Server.Favorite.Songs.SequenceEqual(new[] { 2, 1 }) && f.Server.Operations.All(p => p.Contains("manipulate", StringComparison.Ordinal))); }
    private static async Task OrderTx()
    { await using var f = new Fixture(); await Reorder(f); var result = Ok(await f.Run()); Check(result.GetProperty("status").GetProperty("phase").GetString() == "partial" && f.Server.Writes == 0); }
    private static async Task OccurrencesKg()
    {
        await using var f = new Fixture("kg"); f.Server.Favorite.Add(1); f.Server.Favorite.Add(2); f.Server.Favorite.Add(1); await f.Enable(); Ok(await f.Run()); Check(f.Server.Writes == 0);
        var detail = Ok(await f.Owner.ExecuteAsync("music.library.detail", new { id = "favorites" })); Check(detail.GetProperty("songs").GetArrayLength() == 2);
        Ok(await f.Owner.ExecuteAsync("music.library.removeSong", new { id = "favorites", ifRevision = detail.GetProperty("collection").GetProperty("revision").GetInt64(), songId = detail.GetProperty("songs")[0].GetProperty("songId").GetString() }));
        Ok(await f.Run()); Check(f.Server.Favorite.Songs.SequenceEqual(new[] { 2 })); Check(f.Server.LastWrite!["data"]!.AsArray().Count == 2);
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
        f.Server.Release.SetResult(); await Wait(() => f.Server.Reads >= 2); await Task.Delay(30); int reads = f.Server.Reads;
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
        Check(f.Server.Reads > 192 && f.Server.Writes == 0, "INCOMPLETE_SYNC_READS");
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
        Check(f.Server.Reads == 2 + 32 &&
            f.Server.Writes == 0, "MAPPING_LOOKUP_REQUEST_COUNT");
    }
    private static async Task UnknownCas(bool after)
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
        var run = f.Run(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queue = Ok(await f.Owner.ExecuteAsync("music.queue.get"));
        Ok(await f.Owner.ReplaceQueueAsync([E(Song("tx", "MID1"))], 0, null, queue.GetProperty("version").GetInt64()));
        f.Server.Release.SetResult(); Ok(await run); var again = Ok(await f.Owner.ExecuteAsync("music.queue.get")); Check(again.GetProperty("entries").GetArrayLength() == 1);
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
        yield return new("with a missing root field", (_, sync, _) => sync.Remove("status"));
        yield return new("with an extra legacy field", (_, sync, _) => sync["legacy"] = true);
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
            var entries = root["entries"]!.AsArray();
            var libraryEntry = entries.Single(entry =>
                entry!["key"]!.GetValue<string>().EndsWith(",\"library\"]", StringComparison.Ordinal));
            entries.Remove(libraryEntry);
        });
    }
    private static async Task Corrupt(Action<JsonObject, JsonObject, JsonObject> apply)
    {
        await using var f = new Fixture();
        await f.Enable();
        await f.Sync.DisposeAsync();
        await f.Owner.AbortAsync();
        var root = JsonNode.Parse(f.Store.Data!)!.AsObject();
        var entries = root["entries"]!.AsArray();
        var sync = entries.Single(entry =>
            entry!["key"]!.GetValue<string>().Contains("\"library-sync\"", StringComparison.Ordinal))!["value"]!.AsObject();
        var library = entries.Single(entry =>
            entry!["key"]!.GetValue<string>().EndsWith(",\"library\"]", StringComparison.Ordinal))!["value"]!["library"]!.AsObject();
        apply(root, sync, library);
        f.Store.Data = LJ.Bytes(root);
        byte[] original = f.Store.Data.ToArray();
        f.Store.Writes = 0;
        f.Server.Reads = f.Server.Writes = f.Server.Creates = 0;
        f.Owner = new(f.Store);
        f.Sync = new(f.Owner, f.Sessions, f.Http, f.Clients.Owner);
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
    private static async Task Dpapi()
    {
        string path = Path.GetFullPath(TestWorkspace.DataPath("music-library-sync-20261023", "dpapi-" + Guid.NewGuid().ToString("N"))); Directory.CreateDirectory(path);
        var sessions = new Sessions(); var server = new Server("tx"); using var http = MusicPlatformHttp.CreateWithHandler(server);
        await using var store = new LocalDocumentStore(path, [MusicLibraryOwner.DocumentNamespace, MusicPlatformClientContextOwner.DocumentNamespace], protectedNamespaces: [MusicPlatformClientContextOwner.DocumentNamespace]);
        await using var clients = new MusicClientContextFixture(http, store);
        var owner = new MusicLibraryOwner(store); await using (var sync = new MusicLibrarySyncService(owner, sessions, http, clients.Owner))
        {
            Ok(await sync.ExecuteAsync("music.librarySync.set", new { platform = "tx", favorites = false, playlists = true, ifRevision = 0, confirm = true }));
            Ok(await owner.CreatePlaylistAsync("Local", [])); server.DropWrites = true; Code(await sync.ExecuteAsync("music.librarySync.run"), "CREATE_UNCONFIRMED");
        }
        await owner.AbortAsync(); owner = new(store); await using var next = new MusicLibrarySyncService(owner, sessions, http, clients.Owner);
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
        Check(result.GetProperty("status").GetProperty("issues").EnumerateArray().Any(e => S(e, "code") == "SYNC_CLOUD_TRANSPORT_REQUIRED") && f.Server.Writes == 0);
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
        int validationRead = f.Server.Reads + 5;
        bool changed = false;
        f.Server.ObserveRead = read =>
        {
            if (read != validationRead) return;
            f.Server.Favorite.Songs[0] = 2;
            changed = true;
        };
        var result = Ok(await f.Run());
        Check(changed && f.Server.Reads == validationRead + 3, "WRITE_VALIDATION_READ_NOT_OBSERVED");
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
