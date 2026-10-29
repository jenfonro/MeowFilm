using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowFilm.Media;
using MeowFilm.Capabilities.PlaybackCache;
using static MeowFilm.Windows.Tests.MusicOfficialAudioTests;

namespace MeowFilm.Windows.Tests;

public static class MusicPlaybackCacheTests
{
    private static readonly string QualityPolicySignature = new('a', 64);
    private static readonly string CacheSignature = new('b', 64);
    private static readonly string OtherCacheSignature = new('c', 64);

    public static IEnumerable<TestCase> Cases()
    {
        yield return new("Music complete file quality reads actual FLAC precision MP3 bitrate and never infers master", () =>
        {
            foreach (var (rate, depth, quality) in new[] { (44100, 16, "flac"), (44100, 24, "flac24bit"), (96000, 16, "flac24bit") })
            {
                byte[] bytes = new byte[52]; "fLaC"u8.CopyTo(bytes);
                bytes[4] = 128; bytes[7] = 34;
                ulong packed = ((ulong)rate << 44) | (1UL << 41) | ((ulong)(depth - 1) << 36) | 44100;
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(18, 8), packed);
                bytes[42] = 255; bytes[43] = 248;
                var facts = AudioCacheStructure.Inspect(new MemoryStream(bytes), default);
                Check(facts?.Quality == quality && facts.SampleRate == rate && facts.BitDepth == depth && facts.Channels == 2);
            }
            byte[] mp3 = new byte[417 * 3];
            for (int p = 0; p < mp3.Length; p += 417) { mp3[p] = 255; mp3[p + 1] = 251; mp3[p + 2] = 144; }
            var lossy = AudioCacheStructure.Inspect(new MemoryStream(mp3), default);
            Check(lossy?.Quality == "128k" && lossy.SampleRate == 44100 && lossy.BitDepth == 0);
            return Task.CompletedTask;
        });
        yield return new("Music cache publishes observed encoding only after native validation not requested master", async () =>
        {
            await using var f = new Fixture();
            var raw = System.Text.Json.Nodes.JsonNode.Parse(f.Info().GetRawText())!.AsObject();
            raw["quality"] = "master"; raw["qualityVerified"] = false;
            await f.Cache.StorePlayedAsync("scope", "song", CacheSignature, JsonSerializer.SerializeToElement(raw), () => true, default);
            var info = await f.Cache.ReadInfoAsync("scope", "song", CacheSignature, default);
            Check(info?["actualQuality"]?.GetValue<string>() == "flac" && info["qualityVerified"]!.GetValue<bool>());
            Check(info!["quality"]!.GetValue<string>() == "master" && info["bitDepth"]!.GetValue<int>() == 16);
            Check(info["url"] is null && info["headers"] is null);
        });
        yield return new("Music online-only complete cache retains bytes without manufacturing offline permission", async () =>
        {
            await using var f = new Fixture();
            var raw = System.Text.Json.Nodes.JsonNode.Parse(f.Info(scope: "public:wy").GetRawText())!.AsObject();
            raw["offlineAllowed"] = false;
            await f.Cache.StorePlayedAsync("public:wy", "song", CacheSignature, JsonSerializer.SerializeToElement(raw), () => true, default);
            var restarted = f.NewCache();
            var info = await restarted.ReadInfoAsync("public:wy", "song", CacheSignature, default);
            Check(info is not null && info["offlineAllowed"]!.GetValue<bool>() == false &&
                info["licenseExpiresAt"] is null, "byte cache must not grant offline entitlement");
            await using var lease = await restarted.OpenAsync("public:wy", "song", CacheSignature, default);
            Check(lease is not null && lease.Length == f.Bytes.Length);
            Check(f.Store.Data.Keys.SequenceEqual(new[] { MusicAudioFileCache.Namespace }));
        });
        yield return new("Music file prefetch serves validated bytes but publishes only at real started and cancels unplayed files", async () =>
        {
            await using var f = new Fixture();
            using (var pending = f.Cache.Prepare("scope", "song", CacheSignature, f.Info(), () => true, default))
            {
                Check(await pending.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5)) is not null);
                using (var competing = f.NewCache().Prepare("scope", "other-process", CacheSignature,
                    f.Info(song: "other-process"), () => true, default))
                {
                    await competing.Work.WaitAsync(TimeSpan.FromSeconds(5));
                    Check(await competing.Ready.Task is null, "another owner cannot prune a live producer");
                }
                Check(await f.Cache.OpenAsync("scope", "song", CacheSignature, default) is null, "unplayed bytes are not a cache hit");
                await using (var lease = await pending.OpenAsync(default))
                    Check(lease is not null && lease.Length == f.Bytes.Length, "next player reads prepared bytes");
                f.Clock.Now += 1000;
                pending.Played.TrySetResult(f.Clock.Now);
                await pending.Work.WaitAsync(TimeSpan.FromSeconds(5));
                await using var hit = await f.Cache.OpenAsync("scope", "song", CacheSignature, default);
                Check(hit is not null);
            }
            using (var abandoned = f.Cache.Prepare("scope", "other", CacheSignature,
                f.Info(song: "other"), () => true, default))
            {
                Check(await abandoned.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5)) is not null);
                abandoned.Dispose(); await abandoned.Work.WaitAsync(TimeSpan.FromSeconds(5));
                Check(await f.Cache.OpenAsync("scope", "other", CacheSignature, default) is null);
            }
            Check(Directory.EnumerateFiles(f.Root, "*.audio").Count() == 1);
        });
        yield return new("Music cache clear fences pending publication and leaves active read and unrelated documents untouched", async () =>
        {
            await using var f = new Fixture();
            f.Store.Data["music.library.v1"] = "library"u8.ToArray();
            f.Store.Data["unrelated.settings.v1"] = "settings"u8.ToArray();
            await f.Cache.StorePlayedAsync("scope", "song", CacheSignature, f.Info(), () => true, default);
            await using (var lease = await f.Cache.OpenAsync("scope", "song", CacheSignature, default))
            {
                using var pending = f.Cache.Prepare("scope", "next", CacheSignature,
                    f.Info(song: "next"), () => true, default);
                Check(await pending.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5)) is not null);
                await f.Cache.ClearAsync(default);
                pending.Played.TrySetResult(f.Clock.Now);
                await pending.Work.WaitAsync(TimeSpan.FromSeconds(5));
                Check(await f.Cache.OpenAsync("scope", "song", CacheSignature, default) is null &&
                    await f.Cache.OpenAsync("scope", "next", CacheSignature, default) is null);
                Check(lease is not null && lease.Content.ReadByte() == 'R', "clearing cannot interrupt current playback");
                Check(Encoding.UTF8.GetString(f.Store.Data["music.library.v1"]) == "library" &&
                    Encoding.UTF8.GetString(f.Store.Data["unrelated.settings.v1"]) == "settings");
            }
            await f.Cache.ClearAsync(default);
            Check(!Directory.EnumerateFiles(f.Root, "*.audio").Any(), "reclaim released lease");
        });
        yield return new("Music cache persisted receipt has no signed URL or headers and reading does not touch TTL", async () =>
        {
            await using var f = new Fixture();
            await f.Cache.StorePlayedAsync("scope", "song", CacheSignature, f.Info(), () => true, default);
            int writes = f.Store.Writes;
            var info = await f.Cache.ReadInfoAsync("scope", "song", CacheSignature, default);
            Check(info is not null && info["url"] is null && info["headers"] is null && info["sourceKey"]!.GetValue<string>() == "wy:1");
            Check(f.Store.Writes == writes);
            f.Clock.Now += MusicAudioFileCache.Ttl;
            Check(await f.Cache.ReadInfoAsync("scope", "song", CacheSignature, default) is null);
        });
        yield return new("Music file cache rejects every non-current index path before filesystem network or writes", async () =>
        {
            var malformedEntry = new System.Text.Json.Nodes.JsonObject
            {
                ["file"] = Guid.Empty.ToString("N") + ".audio",
                ["size"] = 1,
                ["sha256"] = new string('b', 64),
                ["signature"] = CacheSignature,
                ["confirmedAt"] = 1,
                ["licenseExpiresAt"] = null,
                ["duration"] = 1,
                ["info"] = new System.Text.Json.Nodes.JsonObject()
            };
            foreach (var (bytes, expected) in new[]
            {
                (Encoding.UTF8.GetBytes("""{"schema":2,"entries":{}}"""), MeowFilm.Platform.Storage.DocumentStoreError.UnsupportedVersion),
                (Encoding.UTF8.GetBytes("""{"schema":1.0,"entries":{}}"""), MeowFilm.Platform.Storage.DocumentStoreError.UnsupportedVersion),
                (JsonSerializer.SerializeToUtf8Bytes(new System.Text.Json.Nodes.JsonObject
                {
                    ["schema"] = 1,
                    ["entries"] = new System.Text.Json.Nodes.JsonObject { [new string('a', 64)] = malformedEntry.DeepClone() }
                }), MeowFilm.Platform.Storage.DocumentStoreError.Corrupt)
            })
            {
                await using var f = new Fixture();
                f.Store.Data[MusicAudioFileCache.Namespace] = bytes.ToArray();
                byte[] original = f.Store.Data[MusicAudioFileCache.Namespace].ToArray();
                int requests = f.Requests, writes = f.Store.Writes;
                foreach (Func<Task> operation in new Func<Task>[]
                {
                    async () => _ = await f.Cache.ReadInfoAsync("scope", "song", CacheSignature, default),
                    async () => { await using var lease = await f.Cache.OpenAsync("scope", "song", CacheSignature, default); },
                    () => f.Cache.TouchAsync("scope", "song", CacheSignature, default),
                    () => f.Cache.EraseAsync("scope", "song", default),
                    () => f.Cache.StorePlayedAsync("scope", "song", CacheSignature, f.Info(), () => true, default)
                })
                    Check(await CacheDataError(operation) == expected);
                using (var prepared = f.Cache.Prepare("scope", "song", CacheSignature, f.Info(), () => true, default))
                {
                    Check(await CacheDataError(() => prepared.Work) == expected);
                    Check(await prepared.Ready.Task is null);
                }
                Check(f.Requests == requests && f.Store.Writes == writes &&
                    f.Store.Data[MusicAudioFileCache.Namespace].SequenceEqual(original) &&
                    !Directory.EnumerateFiles(f.Root).Any(),
                    "incompatible index must remain untouched and cannot start a producer");
            }
        });
        yield return new("Music file cache rejects non-canonical numbers and wrong fingerprints at every entry point", async () =>
        {
            foreach (string failure in new[] { "number", "fingerprint" })
            {
                await using var f = new Fixture();
                await f.Cache.StorePlayedAsync("scope", "song", CacheSignature, f.Info(), () => true, default);
                byte[] valid = f.Store.Data[MusicAudioFileCache.Namespace];
                byte[] invalid;
                if (failure == "number")
                {
                    string current = Encoding.UTF8.GetString(valid);
                    string changed = current.Replace("\"duration\":1,", "\"duration\":1.0,", StringComparison.Ordinal);
                    Check(changed != current, "duration fixture must change");
                    invalid = Encoding.UTF8.GetBytes(changed);
                    f.Store.Data[MusicAudioFileCache.Namespace] = invalid.ToArray();
                }
                else
                {
                    invalid = valid.ToArray();
                    f.Store.ReadFingerprint = new MeowFilm.Platform.Storage.DocumentFingerprint(
                        true, invalid.Length, new string('0', 64));
                }
                int requests = f.Requests, writes = f.Store.Writes;
                string[] files = Directory.EnumerateFiles(f.Root, "*.audio")
                    .Select(path => Path.GetFileName(path) + ":" + new FileInfo(path).Length)
                    .OrderBy(value => value, StringComparer.Ordinal).ToArray();
                foreach (Func<Task> operation in new Func<Task>[]
                {
                    async () => _ = await f.Cache.ReadInfoAsync("scope", "song", CacheSignature, default),
                    async () => { await using var lease = await f.Cache.OpenAsync("scope", "song", CacheSignature, default); },
                    () => f.Cache.TouchAsync("scope", "song", CacheSignature, default),
                    () => f.Cache.EraseAsync("scope", "song", default),
                    () => f.Cache.StorePlayedAsync("scope", "song", CacheSignature, f.Info(), () => true, default)
                })
                    Check(await CacheDataError(operation) == MeowFilm.Platform.Storage.DocumentStoreError.Corrupt);
                using (var prepared = f.Cache.Prepare("scope", "song", CacheSignature, f.Info(), () => true, default))
                {
                    Check(await CacheDataError(() => prepared.Work) ==
                        MeowFilm.Platform.Storage.DocumentStoreError.Corrupt);
                    Check(await prepared.Ready.Task is null);
                }
                Check(f.Requests == requests && f.Store.Writes == writes &&
                    f.Store.Data[MusicAudioFileCache.Namespace].SequenceEqual(invalid) &&
                    Directory.EnumerateFiles(f.Root, "*.audio")
                        .Select(path => Path.GetFileName(path) + ":" + new FileInfo(path).Length)
                        .OrderBy(value => value, StringComparer.Ordinal).SequenceEqual(files),
                    "invalid index must remain untouched");
            }
        });
        yield return new("Music file cache binds every persisted entry to its key receipt and outer facts", async () =>
        {
            var mutations = new (string Name, Action<JsonObject> Change)[]
            {
                ("entry-key", root =>
                {
                    var entries = root["entries"]!.AsObject();
                    var pair = entries.Single();
                    entries.Remove(pair.Key);
                    entries[new string('d', 64)] = pair.Value;
                }),
                ("scope", root => Entry(root)["info"]!["scope"] = "other-scope"),
                ("song", root => Entry(root)["info"]!["songId"] = "other-song"),
                ("outer-signature", root => Entry(root)["signature"] = OtherCacheSignature),
                ("inner-signature", root => Entry(root)["info"]!["qualityCacheSignature"] = OtherCacheSignature),
                ("outer-size", root => Entry(root)["size"] = 1),
                ("inner-size", root => Entry(root)["info"]!["size"] = 1),
                ("outer-duration", root => Entry(root)["duration"] = 2),
                ("inner-duration", root => Entry(root)["info"]!["duration"] = 2),
                ("license", root =>
                {
                    var info = Entry(root)["info"]!.AsObject();
                    info["licenseExpiresAt"] = 2000000000000;
                    info["authorizationExpiresAt"] = 2000000000000;
                    info["offlineAllowed"] = true;
                }),
                ("missing-info-field", root => Entry(root)["info"]!.AsObject().Remove("scope")),
                ("extra-info-field", root => Entry(root)["info"]!["legacy"] = true)
            };
            foreach (var mutation in mutations)
            {
                await using var f = new Fixture();
                await f.Cache.StorePlayedAsync("scope", "song", CacheSignature, f.Info(), () => true, default);
                var root = f.Store.Root(MusicAudioFileCache.Namespace);
                mutation.Change(root);
                byte[] invalid = JsonSerializer.SerializeToUtf8Bytes(root);
                f.Store.Data[MusicAudioFileCache.Namespace] = invalid.ToArray();
                int requests = f.Requests, writes = f.Store.Writes;
                string[] files = Directory.EnumerateFiles(f.Root)
                    .Select(path => Path.GetFileName(path) + ":" + new FileInfo(path).Length)
                    .OrderBy(value => value, StringComparer.Ordinal).ToArray();
                foreach (Func<Task> operation in new Func<Task>[]
                {
                    async () => _ = await f.Cache.ReadInfoAsync("scope", "song", CacheSignature, default),
                    async () => { await using var lease = await f.Cache.OpenAsync("scope", "song", CacheSignature, default); },
                    () => f.Cache.TouchAsync("scope", "song", CacheSignature, default),
                    () => f.Cache.EraseAsync("scope", "song", default),
                    () => f.Cache.StorePlayedAsync("scope", "song", CacheSignature, f.Info(), () => true, default)
                })
                    Check(await CacheDataError(operation) ==
                        MeowFilm.Platform.Storage.DocumentStoreError.Corrupt, mutation.Name);
                using (var prepared = f.Cache.Prepare("scope", "song", CacheSignature,
                    f.Info(), () => true, default))
                {
                    Check(await CacheDataError(() => prepared.Work) ==
                        MeowFilm.Platform.Storage.DocumentStoreError.Corrupt, mutation.Name);
                    Check(await prepared.Ready.Task is null, mutation.Name);
                }
                Check(f.Requests == requests && f.Store.Writes == writes &&
                    f.Store.Data[MusicAudioFileCache.Namespace].SequenceEqual(invalid) &&
                    Directory.EnumerateFiles(f.Root)
                        .Select(path => Path.GetFileName(path) + ":" + new FileInfo(path).Length)
                        .OrderBy(value => value, StringComparer.Ordinal).SequenceEqual(files),
                    mutation.Name + " changed cache state");
            }
            static JsonObject Entry(JsonObject root) =>
                root["entries"]!.AsObject().Single().Value!.AsObject();
        });
        yield return new("Music file cache rejects non-current producer receipts before index filesystem or network access", async () =>
        {
            var mutations = new (string Name, Action<JsonObject> Change)[]
            {
                ("scope", receipt => receipt["scope"] = "other-scope"),
                ("song", receipt => receipt["songId"] = "other-song"),
                ("signature", receipt => receipt["qualityCacheSignature"] = OtherCacheSignature),
                ("missing-field", receipt => receipt.Remove("delivery")),
                ("extra-field", receipt => receipt["legacy"] = true),
                ("wrong-type", receipt => receipt["size"] = "1")
            };
            foreach (var mutation in mutations)
            {
                await using var f = new Fixture();
                var receipt = JsonNode.Parse(f.Info().GetRawText())!.AsObject();
                mutation.Change(receipt);
                var invalid = JsonSerializer.SerializeToElement(receipt);
                Check(await CacheDataError(() => f.Cache.StorePlayedAsync(
                    "scope", "song", CacheSignature, invalid, () => true, default)) ==
                    MeowFilm.Platform.Storage.DocumentStoreError.Corrupt, mutation.Name);
                using (var prepared = f.Cache.Prepare(
                    "scope", "song", CacheSignature, invalid, () => true, default))
                {
                    Check(await CacheDataError(() => prepared.Work) ==
                        MeowFilm.Platform.Storage.DocumentStoreError.Corrupt, mutation.Name);
                    Check(await prepared.Ready.Task is null, mutation.Name);
                }
                Check(f.Requests == 0 && f.Store.Writes == 0 && f.Store.Data.Count == 0 &&
                    !Directory.EnumerateFiles(f.Root).Any(), mutation.Name + " produced side effects");
            }
        });
        yield return new("Music file cache keeps operational storage optional but reports protected data failure", async () =>
        {
            await using var f = new Fixture();
            f.Store.Fail = true;
            Check(await f.Cache.ReadInfoAsync("scope", "song", CacheSignature, default) is null);
            await using (var lease = await f.Cache.OpenAsync("scope", "song", CacheSignature, default))
                Check(lease is null);
            await f.Cache.TouchAsync("scope", "song", CacheSignature, default);
            await f.Cache.EraseAsync("scope", "song", default);
            await f.Cache.StorePlayedAsync("scope", "song", CacheSignature, f.Info(), () => true, default);
            Check(f.Requests == 0 && f.Store.Writes == 0 && !Directory.EnumerateFiles(f.Root).Any());
            f.Store.Fail = false;
            f.Store.Failure = MeowFilm.Platform.Storage.DocumentStoreError.Protection;
            Check(await CacheDataError(async () =>
                _ = await f.Cache.ReadInfoAsync("scope", "song", CacheSignature, default)) ==
                MeowFilm.Platform.Storage.DocumentStoreError.Protection);
        });
        yield return new("Music file cache explicit clear replaces an incompatible index and removes cache bytes", async () =>
        {
            await using var f = new Fixture();
            f.Store.Data[MusicAudioFileCache.Namespace] = Encoding.UTF8.GetBytes("""{"schema":2,"entries":{}}""");
            string orphan = Path.Combine(f.Root, Guid.NewGuid().ToString("N") + ".audio");
            await File.WriteAllBytesAsync(orphan, f.Bytes);
            await f.Cache.ClearAsync(default);
            var root = f.Store.Root(MusicAudioFileCache.Namespace);
            Check(root.Count == 2 && root["schema"]!.GetValue<int>() == 1 &&
                root["entries"]!.AsObject().Count == 0 && !Directory.EnumerateFiles(f.Root, "*.audio").Any());
        });
        yield return new("Music file cache rejects an unknown write receipt without retrying or changing the prior index", async () =>
        {
            await using var f = new Fixture();
            await f.Cache.StorePlayedAsync("scope", "song", CacheSignature, f.Info(), () => true, default);
            byte[] before = f.Store.Data[MusicAudioFileCache.Namespace].ToArray();
            int writes = f.Store.Writes;
            f.Store.SkipWrite = true;
            f.Store.WriteReceipt = new MeowFilm.Platform.Storage.DocumentFingerprint(
                true, 1, new string('0', 64));
            var error = await CacheDataError(() =>
                f.Cache.TouchAsync("scope", "song", CacheSignature, default));
            Check(error == MeowFilm.Platform.Storage.DocumentStoreError.Storage &&
                f.Store.Writes == writes + 1 &&
                f.Store.Data[MusicAudioFileCache.Namespace].SequenceEqual(before));
        });
        yield return new("Music cache accepts bounded APE metadata and extensible PCM while rejecting damaged tags", () =>
        {
            byte[] mp3 = new byte[417 * 3];
            for (int p = 0; p < mp3.Length; p += 417) { mp3[p] = 255; mp3[p + 1] = 251; mp3[p + 2] = 144; }
            using var tagged = new MemoryStream(); tagged.Write(mp3);
            using (var w = new BinaryWriter(tagged, Encoding.UTF8, true))
            {
                w.Write(1u); w.Write(0u); w.Write("Title\0x"u8);
                w.Write("APETAGEX"u8); w.Write(2000u); w.Write(47u); w.Write(1u); w.Write(0u); w.Write(0L);
            }
            AudioCacheStructure.Validate(tagged, default);
            byte[] invalid = tagged.ToArray(); invalid[^16] = 2;
            bool rejected = false;
            try { AudioCacheStructure.Validate(new MemoryStream(invalid), default); } catch (InvalidDataException) { rejected = true; }
            Check(rejected);
            byte[] wav = Wav(); using var extended = new MemoryStream();
            extended.Write(wav.AsSpan(0, 36));
            using (var w = new BinaryWriter(extended, Encoding.UTF8, true))
            {
                w.Write((ushort)22); w.Write((ushort)16); w.Write(4u);
                w.Write(new Guid("00000001-0000-0010-8000-00aa00389b71").ToByteArray());
                w.Write(wav.AsSpan(36)); extended.Position = 4; w.Write((int)extended.Length - 8);
                extended.Position = 16; w.Write(40); w.Write((ushort)0xfffe);
            }
            AudioCacheStructure.Validate(extended, default);
            return Task.CompletedTask;
        });
        yield return new("Music file cache streams complete media independently of download queue and survives restart", async () =>
        {
            await using var f = new Fixture();
            var info = f.Info(scope: "account");
            await f.Cache.StorePlayedAsync("account", "song", CacheSignature, info, () => true, default);
            Check(f.Store.Data.Keys.SequenceEqual(new[] { MusicAudioFileCache.Namespace }));
            var restarted = f.NewCache();
            await using var lease = await restarted.OpenAsync("account", "song", CacheSignature, default);
            Check(lease is not null && lease.Length == f.Bytes.Length && !lease.Content.CanWrite);
            Check(await restarted.OpenAsync("other-account", "song", CacheSignature, default) is null);
            Check(await restarted.OpenAsync("account", "song", OtherCacheSignature, default) is null);
            // Signed URL expiration is not file expiration.
            f.Clock.Now += 120000;
            await using var afterUrl = await restarted.OpenAsync("account", "song", CacheSignature, default);
            Check(afterUrl is not null);
        });
        yield return new("Music file cache real-play touch extends TTL but never license lifetime", async () =>
        {
            await using var f = new Fixture();
            await f.Cache.StorePlayedAsync("account", "song", CacheSignature,
                f.Info(scope: "account", license: f.Clock.Now + 2 * 86400000), () => true, default);
            f.Clock.Now += 86400000;
            await f.Cache.TouchAsync("account", "song", CacheSignature, default);
            f.Clock.Now += 86400001;
            Check(await f.Cache.OpenAsync("account", "song", CacheSignature, default) is null);
        });
        yield return new("Music file cache refuses incomplete body wrong duration and stale publication", async () =>
        {
            foreach (string failure in new[] { "incomplete", "size", "duration", "stale" })
            {
                await using var f = new Fixture();
                var cache = failure == "duration" ? f.NewCache((_, _, _) => Task.FromResult(10d)) : f.Cache;
                await cache.StorePlayedAsync("account", "song", CacheSignature,
                    f.Info(scope: "account", full: failure != "incomplete", size: failure == "size" ? 50 : null),
                    () => failure != "stale", default);
                Check(await cache.OpenAsync("account", "song", CacheSignature, default) is null, failure);
                Check(!Directory.EnumerateFiles(f.Root, "*.audio").Any(), failure + " orphan");
            }
        });
        yield return new("Music file cache rejects tampering and revoked read leases", async () =>
        {
            await using var f = new Fixture();
            await f.Cache.StorePlayedAsync("account", "song", CacheSignature,
                f.Info(scope: "account"), () => true, default);
            using (var cancellation = new CancellationTokenSource())
            {
                await using var lease = await f.Cache.OpenAsync("account", "song", CacheSignature, cancellation.Token);
                Check(lease is not null); cancellation.Cancel(); Check(lease!.Revoked.IsCancellationRequested);
            }
            string file = Directory.EnumerateFiles(f.Root, "*.audio").Single();
            var changed = f.Bytes.ToArray(); changed[^1] ^= 1; await File.WriteAllBytesAsync(file, changed);
            Check(await f.Cache.OpenAsync("account", "song", CacheSignature, default) is null);
        });
        yield return new("Music cache native silent decoder validates real synthetic PCM file", async () =>
        {
            await using var f = new Fixture();
            string path = Path.Combine(f.Root, "probe.wav");
            await File.WriteAllBytesAsync(path, f.Bytes);
            double duration = await NativeAudioCacheProbe.ValidateAsync(path, 1, default);
            Check(Math.Abs(duration - 1) < .1);
        });
    }
    private static async Task<MeowFilm.Platform.Storage.DocumentStoreError> CacheDataError(Func<Task> operation)
    {
        try { await operation(); }
        catch (MusicPlaybackCacheDataException error) { return error.Error; }
        catch (MusicPlaybackCacheCommitException) { return MeowFilm.Platform.Storage.DocumentStoreError.Storage; }
        throw new InvalidOperationException("Cache index was accepted");
    }
    internal sealed class Fixture : IAsyncDisposable
    {
        internal readonly string Root = TestWorkspace.DataPath("meowfilm-audio-cache-test-" + Guid.NewGuid().ToString("N"));
        internal readonly MemoryStore Store = new();
        internal readonly Clock Clock = new();
        internal readonly byte[] Bytes = Wav();
        internal int Requests;
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource lifetime = new();
        private readonly Task server;
        internal readonly Uri Origin;
        internal readonly MusicAudioFileCache Cache;
        internal Fixture()
        {
            Directory.CreateDirectory(Root); listener.Start();
            Origin = new Uri("http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/");
            server = Serve(); Cache = NewCache();
        }
        internal MusicAudioFileCache NewCache(Func<string, double, CancellationToken, Task<double>>? validate = null) =>
            new(Root, Store, validate ?? ((_, expected, _) => Task.FromResult(expected)), Clock,
                NativeHttpMediaOptions.ForLoopbackTests([Origin]));
        internal JsonElement Info(string scope = "scope", string song = "song",
            string? signature = null, bool full = true, long? size = null, long? license = null) =>
            JsonSerializer.SerializeToElement(new
            {
                ok = true,
                kind = "official",
                platform = "wy",
                songId = song,
                sourceKey = "wy:1",
                scope,
                generation = 1,
                quality = "flac",
                requestedQuality = "auto",
                actualQuality = "",
                qualityVerified = false,
                playbackVerified = false,
                qualityPolicySignature = QualityPolicySignature,
                qualityCacheSignature = signature ?? CacheSignature,
                url = new Uri(Origin, "track.wav").AbsoluteUri,
                headers = new { },
                expiresAt = Clock.Now + 60000,
                duration = 1d,
                size = size ?? Bytes.Length,
                full,
                licenseExpiresAt = license,
                delivery = "wy-official",
                offlineAllowed = full && license is not null,
                authorizationExpiresAt = license
            });
        private async Task Serve()
        {
            try
            {
                while (true)
                {
                    using var client = await listener.AcceptTcpClientAsync(lifetime.Token);
                    using var stream = client.GetStream();
                    var request = new List<byte>(); var one = new byte[1];
                    while (request.Count < 16384 && await stream.ReadAsync(one, lifetime.Token) != 0)
                    {
                        request.Add(one[0]);
                        if (request.Count >= 4 && request.TakeLast(4).SequenceEqual("\r\n\r\n"u8.ToArray())) break;
                    }
                    Interlocked.Increment(ref Requests);
                    byte[] header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {Bytes.Length}\r\nContent-Type: audio/wav\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header, lifetime.Token);
                    await stream.WriteAsync(Bytes, lifetime.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) when (lifetime.IsCancellationRequested) { }
            catch (SocketException) when (lifetime.IsCancellationRequested) { }
        }
        public async ValueTask DisposeAsync()
        {
            lifetime.Cancel(); listener.Stop(); await server; lifetime.Dispose();
            // Only the explicitly created UUID test root, no computed external directory.
            TestWorkspace.ValidatePath(TestWorkspace.DataRoot, Root);
            if (!Path.GetFileName(Root).StartsWith("meowfilm-audio-cache-test-", StringComparison.Ordinal)) throw new InvalidOperationException();
            Directory.Delete(Root, true); await Store.DisposeAsync();
        }
    }
}
