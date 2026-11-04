// SPDX-License-Identifier: GPL-3.0-only
// Native adaptations of iWebPlus b900c259 (Apache-2.0). Protocol notices: LICENSES/NOTICE.md.
using System.Net.Http;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities.Sync;

// Membership can be fully verified even when a detail endpoint omits its recording metadata.
// This flag is round-local evidence, not a persisted song field or a fabricated metadata revision.
internal sealed record SyncMember(string Id, string Key, JsonObject Song, string? Sort = null, bool HasMetadata = true);
internal sealed record SyncRemote(string Id, string Name, string Kind, bool Writable, string Owner,
    IReadOnlyList<SyncMember> Members, JsonObject Locator)
{
    internal string[] Keys => Members.Select(m => m.Key).ToArray();
    internal string[] Ids => Members.Select(m => m.Id).ToArray();
    internal string Signature => LJ.Digest(LJ.Obj(("id", Id), ("name", Name), ("kind", Kind), ("writable", Writable),
        ("owner", Owner), ("members", Members.Select(m => new { m.Id, m.Key, m.Sort })), ("locator", Locator)));
}
internal sealed record SyncDirectory(string AccountId, IReadOnlyList<SyncRemote> Collections);

internal abstract class MusicSyncAdapter(MusicPlatformSession session, MusicPlatformHttp http,
    MusicPlatformClientContextOwner clients, MusicPlatformClientContext? client,
    Func<Task> guard, Action accountGuard, CancellationToken token)
{
    protected readonly MusicPlatformSession Session = session;
    protected readonly CancellationToken Token = token;
    private readonly Func<Task> checkpointGuard = guard;
    protected readonly MusicPlatformHttp Http = http;
    protected readonly MusicPlatformClientContextOwner Clients = clients;
    private MusicPlatformClientContext? clientContext = client;
    protected MusicWyClientContext WyClient => (MusicWyClientContext)clientContext!;
    protected MusicKgClientContext KgClient => (MusicKgClientContext)clientContext!;
    private void GuardClient()
    {
        accountGuard();
        Token.ThrowIfCancellationRequested();
        if (clientContext is not null)
            Require(Clients.IsCurrent(clientContext), Clients.NeedsReconciliation ? "COMMIT_UNKNOWN" : "STORAGE_CONFLICT");
    }
    protected async Task Guard()
    {
        await checkpointGuard().ConfigureAwait(false);
        GuardClient();
    }
    protected async Task AcceptCatalogReadAsync()
    {
        accountGuard(); Token.ThrowIfCancellationRequested();
        if (clientContext is not null)
            clientContext = await Clients.AcquireAsync(Platform, Token).ConfigureAwait(false);
        await Guard().ConfigureAwait(false);
    }
    internal string Platform => Session.Platform;
    internal string AccountId => Session.UserId;
    protected async Task<JsonObject> Send(HttpRequestMessage request, string? path = null)
    {
        try { await Guard().ConfigureAwait(false); }
        catch { request.Dispose(); throw; }
        var response = await Http.SendAsync(Platform, request, Token).ConfigureAwait(false);
        await Guard().ConfigureAwait(false);
        var body = Platform switch { "wy" => PlatformProtocols.WySuccess(response.Body), "tx" => PlatformProtocols.TxSuccess(response.Body),
            "kg" => PlatformProtocols.KgSuccess(response.Body, path ?? ""), _ => throw new MusicLibraryException("INVALID_PLATFORM") };
        if (Platform == "wy") clientContext = await Clients.AcceptWyAsync(WyClient, Session.Cookie,
            response.SetCookieHeaders, GuardClient, Token).ConfigureAwait(false);
        await Guard().ConfigureAwait(false); return body;
    }
    protected Task<JsonObject> Wy(string path, JsonObject body, bool weapi = true) =>
        Send(MusicPlatformRequests.WyWithCookie(path, body, Session.Cookie, WyClient, weapi));
    protected Task<JsonObject> Tx(string module, string method, JsonObject body) =>
        Send(MusicPlatformRequests.TxWithCookie(module, method, body, Session.Cookie));
    protected Dictionary<string, string> Cookies => PJ.CookieHeader(Session.Cookie);
    protected Task<JsonObject> Kg(string path, JsonObject query, string? router = "cloudlist.service.kugou.com", JsonObject? body = null) =>
        Send(MusicPlatformRequests.KgWithCookie(path, query, Session.Cookie, KgClient, router, body), path);
    protected static JsonObject O(params (string Key, object? Value)[] v) => LJ.Obj(v);
    protected static JsonArray A(IEnumerable<string> v) => LJ.Arr(v);
    protected static string Id(JsonNode? v) => PJ.Id(v);
    protected static long Count(JsonNode? v) => PJ.Count(v);
    protected static List<JsonObject> Rows(JsonNode? v) => PJ.RowsAll(v);
    protected static List<JsonObject> Rows(JsonNode? v, int max) => PJ.Rows(v, max);
    protected static void Require(bool condition, string code = "INCOMPLETE_PAGE") => LJ.Require(condition, code);
    protected static string Name(JsonNode? n) { string value = PJ.Metadata(n); Require(LJ.Id(value, 200), "INVALID_RESPONSE"); return value; }
    protected static void Same(IEnumerable<string> a, IEnumerable<string> b) => Require(a.SequenceEqual(b), "SNAPSHOT_CHANGED");
    internal abstract Task<SyncDirectory> ReadAsync(string[] kinds);
    internal abstract Task<SyncRemote?> ReadCollectionAsync(string id);
    internal virtual void CheckWriteCapability(string operation) { }
    internal abstract Task CreateAsync(string name);
    internal abstract Task DeleteAsync(SyncRemote remote);
    internal abstract Task ChangeAsync(SyncRemote remote, bool add, IReadOnlyList<string> ids, IReadOnlyDictionary<string, JsonObject> songs);
    internal abstract Task OrderAsync(SyncRemote remote, IReadOnlyList<string> ids);
    internal virtual string? NativeId(JsonObject song)
    {
        var sd = NativeMusicSong.SourceData(song);
        if (LJ.S(sd["source"]) != Platform) return null;
        string id = Platform == "kg" ? LJ.S(sd["hash"]).ToUpperInvariant() : NativeMusicSong.InfoId(sd);
        return LJ.Id(id, 64) ? id : null;
    }
    internal virtual async Task<JsonObject?> ResolveAsync(JsonObject song)
    {
        if (LJ.S(song["title"]).Length == 0 || LJ.S(song["artist"]).Length == 0 || LJ.N(song["duration"]) <= 0) return null;
        // Read-only catalog owned by this round, pinned to this exact lease. No second data writer/cache authority.
        using var catalog = new MusicPlatformCatalog(new Pinned(Session, checkpointGuard), Http, Clients);
        string query = (LJ.S(song["title"]) + " " + LJ.S(song["artist"])); query = query[..Math.Min(200, query.Length)];
        var matches = new Dictionary<string, JsonObject>(); long? total = null; int received = 0;
        var recordings = new NativeMusicSong.Recordings(song, Token);
        for (int page = 1; ; page = checked(page + 1))
        {
            await Guard().ConfigureAwait(false);
            var result = await catalog.SearchAsync(new(Platform, query, Page: page, Limit: 30, Refresh: true), Token).ConfigureAwait(false);
            await AcceptCatalogReadAsync().ConfigureAwait(false);
            var value = JsonNode.Parse(result.GetRawText())!.AsObject();
            if (Platform == "kg")
            {
                long n = Count(value["total"]);
                if (total is not null) Require(total == n, "SNAPSHOT_CHANGED");
                total = n;
                if (n > 300) return null;
            }
            foreach (var candidate in Rows(value["items"], 30))
            {
                received++;
                if (recordings.CanMerge(new(candidate, Token), Token) && NativeId(candidate) is { } id)
                    matches[id] = candidate;
            }
            if (Platform != "kg" || !LJ.B(value["more"])) break;
        }
        if (Platform == "kg") Require(received == total, "INCOMPLETE_PAGE");
        if (matches.Count > 1 && LJ.S(song["album"]).Length > 0)
            matches = matches.Where(p => NativeMusicSong.NormalText(LJ.S(p.Value["album"])) == NativeMusicSong.NormalText(LJ.S(song["album"]))).ToDictionary();
        return matches.Count == 1 ? matches.Values.Single() : null;
    }
    private sealed class Pinned(MusicPlatformSession session, Func<Task> guard) : IMusicPlatformSessionSource
    {
        public async ValueTask<MusicPlatformSession?> AcquireAsync(string platform, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); await guard().ConfigureAwait(false); return platform == session.Platform ? session : null; }
        public bool IsCurrent(MusicPlatformSession other) => ReferenceEquals(session, other);
    }
}
