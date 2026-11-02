// SPDX-License-Identifier: GPL-3.0-only
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Capabilities.Sync;

namespace MeowFilm.Windows.Tests;

public static class MusicSyncClientContextTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("Music sync delegated catalog hands confirmed client state back to its adapter", async () =>
        {
            var handler = new Handler();
            using var http = MusicPlatformHttp.CreateWithHandler(handler);
            await using var clients = new MusicClientContextFixture(http);
            handler.Clients = clients.Owner;
            var before = await clients.Owner.AcquireWyAsync();
            var account = new MusicPlatformSession("wy", "synthetic-sync-client", 1, "42", "MUSIC_U=SYNTHETIC_SYNC_ACCOUNT");
            var adapter = new Adapter(account, http, clients.Owner, before);
            var song = MusicSongFixture.Online("tx", "SYNTHETIC_ORIGINAL", "Synthetic track",
                "Synthetic artist", "Synthetic album", 200);
            var mapped = await adapter.ResolveAsync(song);
            Check(mapped is not null && NativeMusicSong.NativeId(mapped) == "42");
            var current = await clients.Owner.AcquireWyAsync();
            Check(current.Generation == before.Generation + 1 && current.Nmtid == "SYNTHETIC_SYNC_CLIENT");
            await adapter.ReadProbeAsync();
            Check(handler.Calls == 2 && ReferenceEquals(current, await clients.Owner.AcquireWyAsync()));
        });
        yield return new("Music sync KG search over 300 is not a unique mapping and stops paging", async () =>
        {
            var handler = new KgHandler();
            using var http = MusicPlatformHttp.CreateWithHandler(handler);
            await using var clients = new MusicClientContextFixture(http);
            var kg = await clients.Owner.AcquireKgAsync();
            var account = new MusicPlatformSession("kg", "synthetic-sync-kg", 1, "42",
                "userid=42; token=SYNTHETIC_KG_TOKEN; KUGOU_API_MID=" + kg.Mid);
            var adapter = new Adapter(account, http, clients.Owner, kg);
            var song = MusicSongFixture.Online("tx", "SYNTHETIC_ORIGINAL", "Synthetic track",
                "Synthetic artist", "Synthetic album", 200);
            Check(await adapter.ResolveAsync(song) is null);
            Check(handler.Calls == 1);
        });
    }
    private static void Check(bool condition)
    { if (!condition) throw new InvalidOperationException("Sync client handoff invariant"); }
    private sealed class Handler : HttpMessageHandler
    {
        internal MusicPlatformClientContextOwner? Clients;
        internal int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            var client = await Clients!.AcquireWyAsync(token);
            var path = request.RequestUri!.AbsolutePath;
            string body;
            if (path == "/eapi/cloudsearch/pc")
            {
                Check(Calls == 1 && client.Nmtid is null);
                body = """{"code":200,"result":{"songCount":1,"songs":[{"id":42,"name":"Synthetic track","ar":[{"name":"Synthetic artist"}],"al":{"name":"Synthetic album"},"dt":200000}]}}""";
            }
            else
            {
                Check(path == "/eapi/toplist" && Calls == 2 && client.Nmtid == "SYNTHETIC_SYNC_CLIENT" &&
                    request.Headers.GetValues("Cookie").Single().Contains("NMTID=" + client.Nmtid, StringComparison.Ordinal));
                body = """{"code":200}""";
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (Calls == 1) response.Headers.TryAddWithoutValidation("Set-Cookie", "NMTID=SYNTHETIC_SYNC_CLIENT; Domain=.music.163.com");
            return response;
        }
    }
    private sealed class KgHandler : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            Check(request.RequestUri is { Host: "gateway.kugou.com", AbsolutePath: "/v2/search/song" });
            const string row = """{"FileHash":"ABCDEF0123456789ABCDEF0123456789","FileName":"Synthetic artist - Synthetic track","Duration":200,"SingerName":"Synthetic artist","AlbumName":"Synthetic album","FileSize":1000}""";
            var body = "{\"status\":1,\"error_code\":0,\"data\":{\"total\":301,\"lists\":[" +
                string.Join(",", Enumerable.Repeat(row, 30)) + "]}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
    private sealed class Adapter(MusicPlatformSession account, MusicPlatformHttp http,
        MusicPlatformClientContextOwner clients, MusicPlatformClientContext client)
        : MusicSyncAdapter(account, http, clients, client, () => Task.CompletedTask, () => { }, default)
    {
        internal Task<JsonObject> ReadProbeAsync() => Wy("/api/toplist", new(), weapi: false);
        internal override Task<SyncDirectory> ReadAsync(string[] kinds) => throw Unused();
        internal override Task<SyncRemote?> ReadCollectionAsync(string id) => throw Unused();
        internal override Task CreateAsync(string name) => throw Unused();
        internal override Task DeleteAsync(SyncRemote remote) => throw Unused();
        internal override Task ChangeAsync(SyncRemote remote, bool add, IReadOnlyList<string> ids, IReadOnlyDictionary<string, JsonObject> songs) => throw Unused();
        internal override Task OrderAsync(SyncRemote remote, IReadOnlyList<string> ids) => throw Unused();
        private static InvalidOperationException Unused() => new("Unexpected sync operation in delegated-read fixture");
    }
}
