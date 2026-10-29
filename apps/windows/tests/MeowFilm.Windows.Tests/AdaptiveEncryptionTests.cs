using System.Buffers.Binary;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Threading;
using MeowFilm.Media;
using A = MeowFilm.Windows.Tests.AdaptiveMediaTests;

namespace MeowFilm.Windows.Tests;

public static class AdaptiveEncryptionTests
{
    public static IEnumerable<TestCase> Cases()
    {
        foreach (string profile in new[] { "ts-sequence", "ts-explicit", "master", "mp4", "ranges" })
            yield return Case("native decode seek rotation and release " + profile, () => Online(profile));
        yield return Case("plaintext capability Range has exact decrypted bytes", LocalRange);
        yield return Case("key cross-origin redirect strips all caller headers", Redirect);
        foreach (bool body in new[] { false, true })
            yield return Case("native blocked " + (body ? "ciphertext" : "key") + " cancellation releases old generation",
                () => Cancellation(body));
        foreach (string scenario in new[] { "short-key", "long-key", "padding", "cipher-size", "dref", "payload-playlist",
            "key-file", "key-lan", "sample-aes", "keyformat", "iv", "map-no-iv", "live", "key-budget" })
            yield return Case("native rejects unsafe encrypted input " + scenario, () => Rejected(scenario));
    }
    private static TestCase Case(string name, Func<Task> body) => new("adaptive-encryption: " + name, () => A.Sta(body));
    internal static void Check(bool value, string message = "encryption assertion")
    { if (!value) throw new InvalidOperationException(message); }
    private static async Task Until(Func<bool> predicate, string phase, int ms = 12000)
    {
        var clock = Stopwatch.StartNew();
        while (!predicate()) { if (clock.ElapsedMilliseconds > ms) throw new TimeoutException(phase); await Task.Delay(15); }
    }
    internal sealed class Vod(string profile = "ts-sequence", int count = 12)
    {
        internal string? Invalid;
        internal bool Hold;
        internal string HeldPath = "/s4";
        internal byte[] Key(int index) => Enumerable.Range(0, 16).Select(i => (byte)(113 + i + index * 31)).ToArray();
        internal byte[] Plain(int index) => profile == "mp4" || profile == "ranges" ? A.Fragment(index) : A.Ts(index);
        private static byte[] IV(ulong n) { var b = new byte[16]; BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(8), n); return b; }
        private static byte[] Encrypt(byte[] bytes, byte[] key, byte[] iv)
        { using var aes = Aes.Create(); aes.Key = key; return aes.EncryptCbc(bytes, iv, PaddingMode.PKCS7); }
        private static int KeyIndex(int index) => index < 3 ? 0 : 1;
        private bool Explicit => profile is "ts-explicit" or "mp4" or "ranges";
        private const ulong Sequence = 0x100000002; // Exercises full 64-bit implicit IV, not truncated uint.
        private byte[] Segment(int i) => i == 6 ? Plain(i) : Encrypt(Plain(i), Key(KeyIndex(i)), IV(Explicit ? 0x123456789 : Sequence + (ulong)i));
        internal string Manifest()
        {
            if (profile == "master") return "#EXTM3U\n#EXT-X-SESSION-KEY:METHOD=AES-128,URI=\"key0\",KEYFORMAT=\"identity\"\n" +
                "#EXT-X-STREAM-INF:BANDWIDTH=128000,CODECS=\"mp4a.6B\"\nchild.m3u8\n";
            return MediaManifest();
        }
        private string MediaManifest()
        {
            var b = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:2\n#EXT-X-PLAYLIST-TYPE:VOD\n" +
                "#EXT-X-MEDIA-SEQUENCE:" + Sequence + "\n");
            string KeyLine(int key) => "#EXT-X-KEY:METHOD=AES-128,URI=\"key" + key +
                "\",KEYFORMAT=\"identity\",KEYFORMATVERSIONS=\"1\"" + (Explicit ? ",IV=0x123456789" : "") + "\n";
            b.Append(KeyLine(0));
            if (profile is "mp4" or "ranges") b.AppendLine("#EXT-X-MAP:URI=\"init\"");
            long offset = 0;
            for (int i = 0; i < count; i++)
            {
                if (i == 3 || i == 7) b.Append(KeyLine(1));
                if (i == 6) b.AppendLine("#EXT-X-KEY:METHOD=NONE");
                b.AppendLine("#EXTINF:" + (profile is "mp4" or "ranges" ? "1" : "1.008") + ",");
                if (profile == "ranges")
                {
                    int size = Segment(i).Length;
                    b.AppendLine($"#EXT-X-BYTERANGE:{size}@{offset}\nblob"); offset += size;
                }
                else b.AppendLine("s" + i);
            }
            b.AppendLine("#EXT-X-ENDLIST");
            string text = b.ToString();
            return Invalid switch
            {
                "key-file" => text.Replace("URI=\"key0\"", "URI=\"file:///C:/not-authorized\""),
                "key-lan" => text.Replace("URI=\"key0\"", "URI=\"http://192.168.1.2/key\""),
                "sample-aes" => text.Replace("METHOD=AES-128", "METHOD=SAMPLE-AES"),
                "keyformat" => text.Replace("KEYFORMAT=\"identity\"", "KEYFORMAT=\"com.example.drm\""),
                "iv" => text.Replace("KEYFORMAT=\"identity\"", "IV=0xnothex,KEYFORMAT=\"identity\""),
                "live" => text.Replace("#EXT-X-ENDLIST", ""),
                "map-no-iv" => text.Replace(",IV=0x123456789", ""),
                "key-budget" => text.Replace("#EXT-X-ENDLIST", string.Concat(Enumerable.Range(2, 257)
                    .Select(i => $"#EXT-X-KEY:METHOD=AES-128,URI=\"key{i}\"\n")) + "#EXT-X-ENDLIST"),
                _ => text
            };
        }
        internal A.Reply Route(string path)
        {
            if (path == "/root.m3u8") return new(Encoding.UTF8.GetBytes(Manifest()));
            if (path == "/child.m3u8") return new(Encoding.UTF8.GetBytes(MediaManifest()));
            if (path.StartsWith("/key", StringComparison.Ordinal))
            {
                byte[] key = Key(path == "/key0" ? 0 : 1);
                if (Invalid == "short-key") key = key[..15];
                if (Invalid == "long-key") key = [.. key, 1];
                return new(key, "application/octet-stream") { BlockBody = Hold && path == HeldPath };
            }
            byte[] data;
            if (path == "/init")
            {
                byte[] init = A.Init(count);
                if (Invalid == "dref") init[init.AsSpan().IndexOf("url "u8) + 7] = 0;
                data = Encrypt(init, Key(0), IV(0x123456789));
            }
            else if (path == "/blob") data = Enumerable.Range(0, count).SelectMany(Segment).ToArray();
            else if (path.StartsWith("/s", StringComparison.Ordinal) && int.TryParse(path[2..], out int i) && i < count)
            {
                data = Segment(i);
                if (Invalid == "cipher-size") data = data[..^1];
                if (Invalid == "payload-playlist") data = Encrypt(Encoding.ASCII.GetBytes("#EXTM3U\nfile:///C:/blocked\n"), Key(KeyIndex(i)), IV(Explicit ? 0x123456789 : Sequence + (ulong)i));
                if (Invalid == "padding")
                {
                    using var aes = Aes.Create(); aes.Key = Key(KeyIndex(i));
                    data = aes.EncryptCbc(new byte[32], IV(Explicit ? 0x123456789 : Sequence + (ulong)i), PaddingMode.None);
                }
            }
            else return new([]) { Status = 404 };
            return new(data, "application/octet-stream") { BlockBody = Hold && path == HeldPath,
                Tag = Invalid == "weak-etag" ? "W/\"fixture\"" : "\"cipher-v1\"",
                IgnoreRange = Invalid == "no-range" };
        }
    }
    private static NativePlayback New(params A.Origin[] origins) => new(Dispatcher.CurrentDispatcher, null, NativeAudioOutput.Dummy,
        NativeHttpMediaOptions.ForLoopbackTests(origins.Select(o => o.Base), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3)));
    private static Task Open(NativePlayback player, A.Origin origin, CancellationToken token = default) =>
        player.OpenAsync("film", "synthetic-encrypted", origin.Uri("root.m3u8"), "合成加密媒体", origin.Headers, token);
    private static List<byte[]> Keys(object adaptive)
    {
        var graph = A.Field(adaptive, "_manifest")!;
        return ((IDictionary)A.Field(graph, "_keys")!).Values.Cast<object>()
            .Select(k => A.Field(k, "bytes") as byte[]).OfType<byte[]>().ToList();
    }
    private static async Task Online(string profile)
    {
        await using var origin = new A.Origin(); var vod = new Vod(profile); origin.Route = vod.Route;
        await using var player = New(origin);
        await Open(player, origin); await A.Decoded(player);
        object session = A.Session(player)!; int port = A.Port(player);
        await player.SeekAsync(8); await Until(() => player.PositionSeconds >= 7, "native encrypted seek");
        Check(ReferenceEquals(session, A.Session(player)) && origin.Requests.All(r => r.Authorized && !r.LocalCapability), "same native owner and scoped headers");
        Check(origin.Requests.Any(r => r.Path.StartsWith("/key", StringComparison.Ordinal)), "native consumed key via approved transport");
        var keys = Keys(A.Adaptive(player)!); Check(keys.Count > 0);
        await player.StopAsync(); await A.Released(session, port);
        Check(keys.All(k => k.All(b => b == 0)), "owned key buffers actually zeroed");
    }
    private static async Task LocalRange()
    {
        await using var origin = new A.Origin(); var vod = new Vod(); origin.Route = vod.Route;
        await using var player = New(origin); await Open(player, origin); await A.Decoded(player); await player.PauseAsync();
        object adaptive = A.Adaptive(player)!, graph = A.Field(adaptive, "_manifest")!;
        var resources = (IDictionary)A.Field(graph, "_resources")!;
        string path = resources.Keys.Cast<string>().First(k =>
            ((Uri)A.Field(resources[k]!, "OriginalUri")!).AbsolutePath == "/s0");
        string capability = (string)adaptive.GetType().GetProperty("Capability", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(adaptive)!;
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });
        client.DefaultRequestHeaders.UserAgent.ParseAdd(capability);
        using (var first = await client.GetAsync($"http://127.0.0.1:{A.Port(player)}{path}")) Check(first.IsSuccessStatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{A.Port(player)}{path}");
        request.Headers.Range = new RangeHeaderValue(7, 511);
        using var response = await client.SendAsync(request);
        Check(response.StatusCode == HttpStatusCode.PartialContent && (await response.Content.ReadAsByteArrayAsync()).SequenceEqual(vod.Plain(0)[7..512]),
            "native capability range uses plaintext not ciphertext offsets");
        await player.StopAsync();
    }
    private static async Task Redirect()
    {
        await using var a = new A.Origin(); await using var b = new A.Origin { RequireAuthorization = false };
        var vod = new Vod(); a.Route = path => path.StartsWith("/key", StringComparison.Ordinal) ? A.Reply.Redirect(b.Uri(path).AbsoluteUri) : vod.Route(path);
        b.Route = vod.Route;
        await using var player = New(a, b); await Open(player, a); await A.Decoded(player);
        await player.SeekAsync(8); await Until(() => player.PositionSeconds >= 7, "redirect key seek");
        Check(b.Requests.Count > 0 && b.Requests.All(r => !r.AnyCallerHeader && !r.LocalCapability), "cross-origin keys receive no caller credentials");
        Check(a.Requests.All(r => r.Authorized), "same-origin payloads retain only their scoped credentials");
        await player.StopAsync();
    }
    private static async Task Cancellation(bool body)
    {
        await using var origin = new A.Origin(); var vod = new Vod { Hold = !body, HeldPath = "/key0" }; int calls = 0;
        origin.Route = path =>
        {
            var reply = vod.Route(path);
            return body && path == "/s0" && Interlocked.Increment(ref calls) == 3 ? reply with { BlockBody = true, PrefixBytes = 16 } : reply;
        };
        using var cancelled = new CancellationTokenSource();
        await using var player = New(origin); await Open(player, origin, cancelled.Token);
        await Until(() => origin.Blocked > 0, "native encrypted IO blocked");
        object session = A.Session(player)!; int port = A.Port(player); var keys = Keys(A.Adaptive(player)!);
        cancelled.Cancel(); await player.StopAsync().WaitAsync(TimeSpan.FromSeconds(2)); await A.Released(session, port);
        Check(keys.All(k => k.All(b => b == 0)), "cancel zeroed keys");
        vod.Hold = false; origin.Route = vod.Route;
        await Open(player, origin); await A.Decoded(player);
        cancelled.Cancel(); Check(player.HasMedia && player.LastErrorCode is null, "old key cancellation cannot retire new owner");
        await player.StopAsync();
    }
    private static async Task Rejected(string scenario)
    {
        await using var origin = new A.Origin(); var vod = new Vod(scenario is "map-no-iv" or "dref" ? "mp4" : "ts-sequence") { Invalid = scenario };
        origin.Route = vod.Route;
        await using var player = New(origin);
        var rejectedSynchronously = false;
        try { await Open(player, origin); }
        catch (PlaybackException) { rejectedSynchronously = true; }
        if (!rejectedSynchronously)
            await Until(() => player.LastErrorCode is not null, "native encrypted rejection");
        Check(!player.HasMedia && (rejectedSynchronously || player.LastErrorCode is not null),
            "unsafe encrypted input cannot become native media");
        Check(origin.Requests.All(r => !r.LocalCapability), "no native capability upstream");
        if (scenario is "key-file" or "key-lan" or "sample-aes" or "keyformat" or "live" or "iv" or "map-no-iv" or "key-budget")
            Check(origin.Requests.All(r => r.Path == "/root.m3u8"), "invalid graph cannot fetch a key or payload");
        await player.StopAsync();
    }
}
