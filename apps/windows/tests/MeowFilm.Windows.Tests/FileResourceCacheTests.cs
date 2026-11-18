using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using MeowFilm.Platform.Storage;
using Microsoft.Win32.SafeHandles;

namespace MeowFilm.Windows.Tests;

public static class FileResourceCacheTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static IEnumerable<TestCase> Cases()
    {
        yield return new("Resource file cache cold and hot reads isolate one raw full-key file", ColdHot);
        yield return new("Resource file cache null directory owns bounded per-group bytes", Memory);
        yield return new("Resource file cache validates admission without touching a lazy directory", Lazy);
        yield return new("Resource file cache shares capacity and reserves live stage bytes", StageBudget);
        yield return new("Resource file cache TTL reads do not renew and playback touch is monotonic", Ttl);
        yield return new("Resource file cache default hits persist mtime and LRU across reopen", DefaultHit);
        yield return new("Resource file cache truncated and oversized entries miss independently", Corruption);
        yield return new("Resource file cache failed removals stay charged and never serve", () => FailedDeletion(false));
        yield return new("Resource file cache abandoned stages stay charged until actual removal", () => FailedDeletion(true));
        yield return new("Resource file cache failed clear keeps its epoch and group isolation", FailedClear);
        yield return new("Resource file cache failed initial open cannot restore a cleared group", InitialFailure);
        yield return new("Resource file cache clear rejects late staged write remove and touch", Epoch);
        yield return new("Resource file cache canceled clear fences already staged writes", CanceledClear);
        yield return new("Resource file cache canceled stage preserves old bytes and drains cleanup", CanceledWrite);
        yield return new("Resource file cache failed replacement preserves old bytes and cleans its stage", FailedReplacement);
        yield return new("Resource file cache late hot read and playback touch cannot renew after clear", LateTouch);
        yield return new("Resource file cache close rejects admission and drains staged and queued writes", CloseDrain);
        yield return new("Resource file cache rejects hardlinks without touching outside bytes", Hardlinks);
        yield return new("Resource file cache pins roots and rejects reparse ancestors", RootSafety);
    }

    private static async Task ColdHot()
    {
        using var folder = new Folder();
        var clock = new Clock { Now = Start.AddSeconds(1) };
        string key = "https://images.example.test/cover?variant=original";
        byte[] first = "aaaa"u8.ToArray(), second = "bbbb"u8.ToArray();
        folder.Seed("images", key, first);
        folder.Seed("lyrics", key, second);
        folder.Seed("images", key + "&other=1", second);
        await using var cache = new FileResourceCache(folder.Root, 64, 16, time: clock);
        using (var unrelated = new FileStream(folder.Entry("lyrics", key), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (var unrelatedImage = new FileStream(folder.Entry("images", key + "&other=1"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Has(cache, "images", key, first);
            File.WriteAllBytes(folder.Entry("images", key), second);
            await Has(cache, "images", key, second);
            folder.Seed("images", key + "&added-after-inventory=1", first);
            await Miss(cache, "images", key + "&added-after-inventory=1");
        }
        await Has(cache, "images", key + "&other=1", second);
        await Has(cache, "lyrics", key, second);
        Check(File.ReadAllBytes(folder.Entry("images", key)).SequenceEqual(second), "disk content must remain raw bytes");
        var returned = (await cache.ReadAsync("images", key).WaitAsync(Deadline))!;
        returned.Content.AsSpan().Clear();
        await Has(cache, "images", key, second);
        await cache.WriteAsync("images", key + "&new=1", first, cache.Epoch("images")).WaitAsync(Deadline);
        await Has(cache, "images", key + "&new=1", first);
        Check(Directory.GetFiles(folder.Root).All(path => Path.GetFileName(path) == ".resources.lock" ||
            path.EndsWith(".resource", StringComparison.Ordinal)), "no shared index or extra payload file");
    }

    private static async Task Memory()
    {
        await using var cache = new FileResourceCache(null, 16, 8);
        byte[] first = "aaaaaaaa"u8.ToArray(), second = "bbbbbbbb"u8.ToArray(), third = "cccccccc"u8.ToArray();
        await cache.WriteAsync("images", "same-key", first, cache.Epoch("images")).WaitAsync(Deadline);
        first.AsSpan().Clear(); // Ownership returns only after the real write task.
        await Has(cache, "images", "same-key", "aaaaaaaa"u8.ToArray());
        await cache.WriteAsync("lyrics", "same-key", second, cache.Epoch("lyrics")).WaitAsync(Deadline);
        var returned = (await cache.ReadAsync("images", "same-key").WaitAsync(Deadline))!;
        returned.Content.AsSpan().Clear();
        await cache.WriteAsync("images", "third", third, cache.Epoch("images")).WaitAsync(Deadline);
        await Miss(cache, "lyrics", "same-key");
        await Has(cache, "images", "same-key", "aaaaaaaa"u8.ToArray());
        await Has(cache, "images", "third", third);
        await cache.WriteAsync("images", "oversized", new byte[9], cache.Epoch("images")).WaitAsync(Deadline);
        await cache.WriteAsync("images", "empty", ReadOnlyMemory<byte>.Empty, cache.Epoch("images")).WaitAsync(Deadline);
        await Miss(cache, "images", "oversized");
        await Miss(cache, "images", "empty");
        long before = cache.Epoch("images");
        var clearing = cache.ClearAsync("images");
        Check(cache.Epoch("images") != before, "clear must publish its epoch synchronously");
        await clearing.WaitAsync(Deadline);
        await Miss(cache, "images", "third");
        await cache.WriteAsync("images", "third", third, before).WaitAsync(Deadline);
        await Miss(cache, "images", "third");
    }

    private static async Task Lazy()
    {
        using var folder = new Folder(createRoot: false);
        var cache = new FileResourceCache(folder.Root, 16, 8);
        try
        {
            Check(cache.Epoch("images") == 0 && !Directory.Exists(folder.Root), "constructor and epoch are lazy");
            foreach (string group in new[] { "", "Images", "../escape", "a/b", "a..b" })
                await Error<ArgumentException>(() => cache.ReadAsync(group, "key"));
            await Error<ArgumentException>(() => cache.WriteAsync("images", "", new byte[1], 0));
            var canceled = new CancellationToken(canceled: true);
            await Error<OperationCanceledException>(() => cache.WriteAsync("images", "key", new byte[1], 0, canceled));
            await Error<OperationCanceledException>(() => cache.ClearAsync("images", canceled));
            Check(cache.Epoch("images") == 0 && !Directory.Exists(folder.Root), "rejected admission has no epoch or I/O side effect");
            await cache.DrainAsync().WaitAsync(Deadline);
        }
        finally { await cache.DisposeAsync().AsTask().WaitAsync(Deadline); }
        Check(!Directory.Exists(folder.Root), "closing an unused disk cache must not create it");
    }

    private static async Task StageBudget()
    {
        using var folder = new Folder();
        var clock = new Clock { Now = Start.AddSeconds(1) };
        byte[] first = "aaaa"u8.ToArray(), second = "bbbb"u8.ToArray(), next = "cccccccc"u8.ToArray();
        folder.Seed("images", "first", first);
        folder.Seed("lyrics", "second", second);
        await using var cache = new FileResourceCache(folder.Root, 12, 8, time: clock);
        await Has(cache, "images", "first", first);
        using var pause = clock.PauseNext();
        try
        {
            var writing = cache.WriteAsync("images", "next", next, cache.Epoch("images"));
            await pause.Entered.Task.WaitAsync(Deadline);
            CheckStage(folder, next);
            Check(!writing.IsCompleted && !cache.DrainAsync().IsCompleted, "stage I/O must still belong to the write task");
            Check(!File.Exists(folder.Entry("lyrics", "second")), "LRU eviction must cross groups for the shared budget");
            Check(OwnedBytes(folder) == 12, "live stage and retained resource must both be charged");
            pause.Dispose();
            await writing.WaitAsync(Deadline);
            await Has(cache, "images", "first", first);
            await Has(cache, "images", "next", next);
            Check(OwnedBytes(folder) == 12 && Directory.GetFiles(folder.Root, "*.stage").Length == 0);
        }
        finally { pause.Dispose(); await cache.DisposeAsync().AsTask().WaitAsync(Deadline); }
    }

    private static async Task Ttl()
    {
        foreach (bool disk in new[] { false, true })
        {
            using var folder = new Folder();
            var clock = new Clock();
            TimeSpan ttl = TimeSpan.FromSeconds(10);
            byte[] content = "aaaa"u8.ToArray();
            await using var cache = new FileResourceCache(disk ? folder.Root : null, 32, 8, ttl, clock);
            long epoch = cache.Epoch("images");
            await cache.WriteAsync("images", "key", content, epoch).WaitAsync(Deadline);
            clock.Now = Start.AddSeconds(5);
            await Has(cache, "images", "key", content, Start + ttl);
            if (disk) Check(File.GetLastWriteTimeUtc(folder.Entry("images", "key")) == Start.UtcDateTime);
            await cache.TouchAsync("images", "key", epoch).WaitAsync(Deadline);
            await Has(cache, "images", "key", content, Start.AddSeconds(15));
            clock.Now = Start.AddSeconds(4);
            await cache.TouchAsync("images", "key", epoch).WaitAsync(Deadline);
            await Has(cache, "images", "key", content, Start.AddSeconds(15));
            if (disk) Check(File.GetLastWriteTimeUtc(folder.Entry("images", "key")) == Start.AddSeconds(5).UtcDateTime);
            clock.Now = Start.AddSeconds(14);
            await Has(cache, "images", "key", content, Start.AddSeconds(15));
            clock.Now = Start.AddSeconds(15);
            await cache.TouchAsync("images", "key", epoch).WaitAsync(Deadline);
            await Miss(cache, "images", "key");
        }
    }

    private static async Task DefaultHit()
    {
        using var folder = new Folder();
        var clock = new Clock { Now = Start.AddSeconds(1) };
        byte[] first = "aaaa"u8.ToArray(), next = "cccccccc"u8.ToArray();
        folder.Seed("artwork", "first", first);
        folder.Seed("artwork", "second", "bbbb"u8.ToArray());
        await using (var cache = new FileResourceCache(folder.Root, 12, 8, time: clock))
            await Has(cache, "artwork", "first", first);
        Check(File.GetLastWriteTimeUtc(folder.Entry("artwork", "first")) == clock.Now.UtcDateTime,
            "default hit must persist the newer mtime");
        clock.Now = Start.AddSeconds(2);
        await using var reopened = new FileResourceCache(folder.Root, 12, 8, time: clock);
        await reopened.WriteAsync("artwork", "next", next, reopened.Epoch("artwork")).WaitAsync(Deadline);
        await Miss(reopened, "artwork", "second");
        await Has(reopened, "artwork", "first", first);
        await Has(reopened, "artwork", "next", next);
    }

    private static async Task Corruption()
    {
        using var folder = new Folder();
        byte[] first = "aaaa"u8.ToArray(), other = "bbbb"u8.ToArray();
        folder.Seed("images", "first", first);
        folder.Seed("lyrics", "other", other);
        folder.Seed("images", "oversized", new byte[9]);
        folder.Seed("images", "empty", []);
        await using var cache = new FileResourceCache(folder.Root, 64, 8);
        await Has(cache, "lyrics", "other", other);
        await Miss(cache, "images", "oversized");
        await Miss(cache, "images", "empty");
        File.WriteAllBytes(folder.Entry("images", "first"), new byte[2]);
        await Miss(cache, "images", "first");
        await Has(cache, "lyrics", "other", other);
        await cache.WriteAsync("images", "first", first, cache.Epoch("images")).WaitAsync(Deadline);
        await Has(cache, "images", "first", first);
        File.Delete(folder.Entry("images", "first"));
        await Miss(cache, "images", "first");
        folder.Seed("images", "first", first);
        await Miss(cache, "images", "first"); // A missing/deleted entry is not rediscovered.
        await Has(cache, "lyrics", "other", other);
    }

    private static async Task FailedDeletion(bool stage)
    {
        using var folder = new Folder();
        string blocked = folder.Entry("images", "blocked");
        if (stage) blocked += "." + new string('a', 32) + ".stage";
        File.WriteAllBytes(blocked, new byte[8]);
        File.SetLastWriteTimeUtc(blocked, Start.UtcDateTime);
        byte[] other = "bb"u8.ToArray(), next = "ccc"u8.ToArray();
        folder.Seed("lyrics", "other", other);
        await using var cache = new FileResourceCache(folder.Root, 12, 16);
        using (var held = new FileStream(blocked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Has(cache, "lyrics", "other", other);
            held.SetLength(10);
            held.Flush(flushToDisk: true);
            if (!stage) await cache.RemoveAsync("images", "blocked", cache.Epoch("images")).WaitAsync(Deadline);
            await Miss(cache, "images", "blocked");
            await cache.WriteAsync("images", "next", next, cache.Epoch("images")).WaitAsync(Deadline);
            Check(!File.Exists(folder.Entry("images", "next")), "failed deletion must retain the grown file's charge");
            Check(File.Exists(blocked) && new FileInfo(blocked).Length == 10);
        }
        await Miss(cache, "images", "blocked");
        await cache.WriteAsync("images", "next", next, cache.Epoch("images")).WaitAsync(Deadline);
        await Has(cache, "images", "next", next);
        Check(!File.Exists(blocked) && Directory.GetFiles(folder.Root, "*.stage").Length == 0);
    }

    private static async Task FailedClear()
    {
        using var folder = new Folder();
        byte[] image = "aaaa"u8.ToArray(), lyric = "bbbb"u8.ToArray();
        folder.Seed("images", "key", image);
        folder.Seed("lyrics", "key", lyric);
        await using var cache = new FileResourceCache(folder.Root, 64, 16);
        await Has(cache, "images", "key", image);
        long oldEpoch = cache.Epoch("images"), lyricEpoch = cache.Epoch("lyrics");
        using (var held = new FileStream(folder.Entry("images", "key"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var clearing = cache.ClearAsync("images");
            Check(cache.Epoch("images") != oldEpoch && cache.Epoch("lyrics") == lyricEpoch);
            await clearing.WaitAsync(Deadline);
            Check(File.Exists(folder.Entry("images", "key")), "fixture must actually deny deletion");
            await Has(cache, "lyrics", "key", lyric);
        }
        await Miss(cache, "images", "key");
        await cache.TouchAsync("images", "key", oldEpoch).WaitAsync(Deadline);
        await cache.WriteAsync("images", "key", "cccc"u8.ToArray(), cache.Epoch("images")).WaitAsync(Deadline);
        await cache.RemoveAsync("images", "key", oldEpoch).WaitAsync(Deadline);
        await Has(cache, "images", "key", "cccc"u8.ToArray());
        await Has(cache, "lyrics", "key", lyric);
    }

    private static async Task InitialFailure()
    {
        using var folder = new Folder();
        byte[] other = "bbbb"u8.ToArray();
        folder.Seed("images", "key", "aaaa"u8.ToArray());
        folder.Seed("lyrics", "key", other);
        string ownership = Path.Combine(folder.Root, ".resources.lock");
        File.WriteAllBytes(ownership, []);
        await using var cache = new FileResourceCache(folder.Root, 64, 16);
        long before = cache.Epoch("images");
        using (var held = new FileStream(ownership, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Miss(cache, "images", "key");
            await cache.ClearAsync("images").WaitAsync(Deadline);
            Check(cache.Epoch("images") != before && cache.Epoch("lyrics") == 0);
        }
        await Has(cache, "lyrics", "key", other);
        await Miss(cache, "images", "key");
        folder.Seed("lyrics", "added-after-inventory", other);
        await Miss(cache, "lyrics", "added-after-inventory");
        await cache.WriteAsync("images", "key", other, cache.Epoch("images")).WaitAsync(Deadline);
        await Has(cache, "images", "key", other);
    }

    private static async Task Epoch()
    {
        using var folder = new Folder();
        var clock = new Clock();
        byte[] other = "bbbb"u8.ToArray(), latest = "cccc"u8.ToArray();
        await using var cache = new FileResourceCache(folder.Root, 64, 16, time: clock);
        await cache.WriteAsync("lyrics", "key", other, cache.Epoch("lyrics")).WaitAsync(Deadline);
        using var pause = clock.PauseNext();
        try
        {
            long before = cache.Epoch("images");
            var writing = cache.WriteAsync("images", "key", "aaaaaaaa"u8.ToArray(), before);
            await pause.Entered.Task.WaitAsync(Deadline);
            CheckStage(folder, "aaaaaaaa"u8.ToArray());
            var clearing = cache.ClearAsync("images");
            long after = cache.Epoch("images");
            Check(after != before && !writing.IsCompleted && !clearing.IsCompleted);
            var successor = cache.WriteAsync("images", "key", latest, after);
            pause.Dispose();
            await Task.WhenAll(writing, clearing, successor).WaitAsync(Deadline);
            clock.Now = Start.AddSeconds(5);
            await cache.WriteAsync("images", "key", other, before).WaitAsync(Deadline);
            await cache.RemoveAsync("images", "key", before).WaitAsync(Deadline);
            await cache.TouchAsync("images", "key", before).WaitAsync(Deadline);
            await Has(cache, "images", "key", latest);
            await Has(cache, "lyrics", "key", other);
            var all = cache.ClearAsync(null);
            Check(cache.Epoch("images") != after && cache.Epoch("lyrics") != 0);
            await all.WaitAsync(Deadline);
            await Miss(cache, "images", "key");
            await Miss(cache, "lyrics", "key");
            Check(Directory.GetFiles(folder.Root, "*.stage").Length == 0);
        }
        finally { pause.Dispose(); await cache.DisposeAsync().AsTask().WaitAsync(Deadline); }
    }

    private static async Task CanceledClear()
    {
        using var folder = new Folder();
        var clock = new Clock();
        byte[] other = "bbbb"u8.ToArray();
        await using var cache = new FileResourceCache(folder.Root, 64, 16, time: clock);
        await cache.WriteAsync("images", "key", "aaaa"u8.ToArray(), cache.Epoch("images")).WaitAsync(Deadline);
        await cache.WriteAsync("lyrics", "key", other, cache.Epoch("lyrics")).WaitAsync(Deadline);
        using var pause = clock.PauseNext();
        using var cancel = new CancellationTokenSource();
        try
        {
            var writing = cache.WriteAsync("images", "key", "cccccccc"u8.ToArray(), cache.Epoch("images"));
            await pause.Entered.Task.WaitAsync(Deadline);
            var clearing = cache.ClearAsync("images", cancel.Token);
            long after = cache.Epoch("images");
            cancel.Cancel();
            await Error<OperationCanceledException>(() => clearing);
            Check(!writing.IsCompleted && !cache.DrainAsync().IsCompleted);
            pause.Dispose();
            await writing.WaitAsync(Deadline);
            await Miss(cache, "images", "key");
            Check(cache.Epoch("images") == after && File.Exists(folder.Entry("images", "key")));
            await Has(cache, "lyrics", "key", other);
            await cache.WriteAsync("images", "next", other, after).WaitAsync(Deadline);
            await Has(cache, "images", "next", other);
            Check(!File.Exists(folder.Entry("images", "key")), "retired bytes must be reclaimed before unrelated LRU victims");
        }
        finally { pause.Dispose(); await cache.DisposeAsync().AsTask().WaitAsync(Deadline); }
    }

    private static async Task CanceledWrite()
    {
        using var folder = new Folder();
        var clock = new Clock();
        byte[] old = "aaaa"u8.ToArray(), next = "bbbbbbbb"u8.ToArray();
        await using var cache = new FileResourceCache(folder.Root, 64, 16, time: clock);
        await cache.WriteAsync("images", "key", old, cache.Epoch("images")).WaitAsync(Deadline);
        using var pause = clock.PauseNext();
        using var cancel = new CancellationTokenSource();
        try
        {
            var writing = cache.WriteAsync("images", "key", next, cache.Epoch("images"), cancel.Token);
            await pause.Entered.Task.WaitAsync(Deadline);
            CheckStage(folder, next);
            cancel.Cancel();
            Check(!writing.IsCompleted && !cache.DrainAsync().IsCompleted);
            pause.Dispose();
            await Error<OperationCanceledException>(() => writing);
            await cache.DrainAsync().WaitAsync(Deadline);
            Check(Directory.GetFiles(folder.Root, "*.stage").Length == 0, "canceled stage cleanup must actually finish");
            Check(File.ReadAllBytes(folder.Entry("images", "key")).SequenceEqual(old));
            await Has(cache, "images", "key", old);
            await cache.WriteAsync("images", "key", next, cache.Epoch("images")).WaitAsync(Deadline);
            await Has(cache, "images", "key", next);
        }
        finally { pause.Dispose(); await cache.DisposeAsync().AsTask().WaitAsync(Deadline); }
    }

    private static async Task LateTouch()
    {
        foreach (bool touch in new[] { false, true })
        {
            using var folder = new Folder();
            var clock = new Clock { Now = Start.AddSeconds(10) };
            folder.Seed("images", "key", "aaaa"u8.ToArray());
            folder.Seed("lyrics", "key", "bbbb"u8.ToArray());
            var cache = new FileResourceCache(folder.Root, 64, 16, time: clock);
            using var pause = clock.PauseNext();
            using var cancel = new CancellationTokenSource();
            try
            {
                var reading = touch ? null : cache.ReadAsync("images", "key");
                Task operation = reading is not null ? reading : cache.TouchAsync("images", "key", cache.Epoch("images"));
                await pause.Entered.Task.WaitAsync(Deadline);
                var clearing = cache.ClearAsync("images", cancel.Token);
                cancel.Cancel();
                await Error<OperationCanceledException>(() => clearing);
                pause.Dispose();
                await operation.WaitAsync(Deadline);
                if (reading is not null) Check(await reading is null, "late bytes must not be returned after clear");
                Check(File.GetLastWriteTimeUtc(folder.Entry("images", "key")) == Start.UtcDateTime,
                    "neither implicit hit nor playback touch may mutate a retired file");
                await Miss(cache, "images", "key");
                await Has(cache, "lyrics", "key", "bbbb"u8.ToArray());
            }
            finally { pause.Dispose(); await cache.DisposeAsync().AsTask().WaitAsync(Deadline); }
        }
    }

    private static async Task FailedReplacement()
    {
        using var folder = new Folder();
        byte[] old = "aaaa"u8.ToArray(), next = "bbbbbbbb"u8.ToArray();
        folder.Seed("images", "key", old);
        await using var cache = new FileResourceCache(folder.Root, 64, 16);
        await Has(cache, "images", "key", old);
        using (var held = new FileStream(folder.Entry("images", "key"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await cache.WriteAsync("images", "key", next, cache.Epoch("images")).WaitAsync(Deadline);
            await cache.DrainAsync().WaitAsync(Deadline);
            Check(File.ReadAllBytes(folder.Entry("images", "key")).SequenceEqual(old), "denied atomic replacement must retain old bytes");
            Check(Directory.GetFiles(folder.Root, "*.stage").Length == 0, "failed replacement must finish its own stage cleanup");
            await Has(cache, "images", "key", old);
        }
        await cache.WriteAsync("images", "key", next, cache.Epoch("images")).WaitAsync(Deadline);
        await Has(cache, "images", "key", next);
    }

    private static async Task CloseDrain()
    {
        using var folder = new Folder();
        var clock = new Clock();
        var cache = new FileResourceCache(folder.Root, 64, 16, time: clock);
        using var pause = clock.PauseNext();
        byte[] first = "aaaa"u8.ToArray(), second = "bbbb"u8.ToArray();
        try
        {
            var writing = cache.WriteAsync("images", "first", first, cache.Epoch("images"));
            await pause.Entered.Task.WaitAsync(Deadline);
            CheckStage(folder, first);
            var queued = cache.WriteAsync("lyrics", "second", second, cache.Epoch("lyrics"));
            var drain = cache.DrainAsync();
            var closing = cache.DisposeAsync().AsTask();
            Check(!writing.IsCompleted && !queued.IsCompleted && !drain.IsCompleted && !closing.IsCompleted);
            Check(ReferenceEquals(closing, cache.DisposeAsync().AsTask()), "close must have one settlement task");
            await Error<ObjectDisposedException>(() => cache.ReadAsync("images", "new"));
            await Error<ObjectDisposedException>(() => cache.WriteAsync("images", "new", first, 0));
            await Error<ObjectDisposedException>(() => cache.RemoveAsync("images", "new", 0));
            await Error<ObjectDisposedException>(() => cache.TouchAsync("images", "new", 0));
            await Error<ObjectDisposedException>(() => cache.ClearAsync(null));
            await Error<ObjectDisposedException>(() => { cache.Epoch("images"); return Task.CompletedTask; });
            await Error<IOException>(() =>
            {
                using var held = new FileStream(Path.Combine(folder.Root, ".resources.lock"),
                    FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return Task.CompletedTask;
            });
            pause.Dispose();
            await Task.WhenAll(writing, queued, drain, closing).WaitAsync(Deadline);
            using (var released = new FileStream(Path.Combine(folder.Root, ".resources.lock"),
                FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            string moved = folder.Root + "-moved";
            Directory.Move(folder.Root, moved);
            Directory.Move(moved, folder.Root);
            await using var reopened = new FileResourceCache(folder.Root, 64, 16);
            await Has(reopened, "images", "first", first);
            await Has(reopened, "lyrics", "second", second);
            Check(Directory.GetFiles(folder.Root, "*.stage").Length == 0);
        }
        finally { pause.Dispose(); await cache.DisposeAsync().AsTask().WaitAsync(Deadline); }
    }

    private static async Task Hardlinks()
    {
        using var folder = new Folder();
        byte[] outside = "outside!"u8.ToArray(), other = "bbbb"u8.ToArray();
        string target = Path.Combine(folder.Base, "outside");
        File.WriteAllBytes(target, outside);
        File.SetLastWriteTimeUtc(target, Start.UtcDateTime);
        Check(CreateHardLinkW(folder.Entry("images", "unsafe"), target, IntPtr.Zero), "hardlink fixture creation failed");
        folder.Seed("lyrics", "safe", other);
        await using (var cache = new FileResourceCache(folder.Root, 64, 16))
        {
            await Miss(cache, "images", "unsafe");
            await Has(cache, "lyrics", "safe", other);
            await cache.WriteAsync("images", "unsafe", other, cache.Epoch("images")).WaitAsync(Deadline);
            await cache.RemoveAsync("images", "unsafe", cache.Epoch("images")).WaitAsync(Deadline);
            await cache.TouchAsync("images", "unsafe", cache.Epoch("images")).WaitAsync(Deadline);
            await cache.ClearAsync("images").WaitAsync(Deadline);
            await Miss(cache, "images", "unsafe");
            await Has(cache, "lyrics", "safe", other);
            Check(File.ReadAllBytes(target).SequenceEqual(outside) && File.GetLastWriteTimeUtc(target) == Start.UtcDateTime);
        }
        using var lockFolder = new Folder();
        string lockTarget = Path.Combine(lockFolder.Base, "outside-empty");
        File.WriteAllBytes(lockTarget, []);
        Check(CreateHardLinkW(Path.Combine(lockFolder.Root, ".resources.lock"), lockTarget, IntPtr.Zero));
        await using var locked = new FileResourceCache(lockFolder.Root, 64, 16);
        await Miss(locked, "images", "key");
        await locked.WriteAsync("images", "key", outside, locked.Epoch("images")).WaitAsync(Deadline);
        Check(!File.Exists(lockFolder.Entry("images", "key")) && new FileInfo(lockTarget).Length == 0,
            "an unsafe ownership file must fail closed without memory fallback");
    }

    private static async Task RootSafety()
    {
        using var folder = new Folder();
        var cache = new FileResourceCache(folder.Root, 64, 16);
        try
        {
            await Miss(cache, "images", "key");
            await Error<IOException>(() => { Directory.Move(folder.Root, folder.Root + "-moved"); return Task.CompletedTask; });
            Check(!Directory.Exists(folder.Root + "-moved"), "an initialized root must remain pinned");
        }
        finally { await cache.DisposeAsync().AsTask().WaitAsync(Deadline); }
        string link = Path.Combine(folder.Base, "junction");
        CreateJunction(link, folder.Root);
        await using var unsafeCache = new FileResourceCache(Path.Combine(link, "child"), 64, 16);
        await Miss(unsafeCache, "images", "key");
        await unsafeCache.WriteAsync("images", "key", "aaaa"u8.ToArray(), unsafeCache.Epoch("images")).WaitAsync(Deadline);
        Check(!Directory.Exists(Path.Combine(folder.Root, "child")), "root traversal must not create a child through a reparse ancestor");
    }

    private static async Task Has(FileResourceCache cache, string group, string resource, byte[] content,
        DateTimeOffset? expiresAt = null)
    {
        var entry = await cache.ReadAsync(group, resource).WaitAsync(Deadline);
        Check(entry is not null && entry.Content.SequenceEqual(content) && entry.ExpiresAt == expiresAt,
            "resource content or expiry differs");
    }

    private static async Task Miss(FileResourceCache cache, string group, string resource) =>
        Check(await cache.ReadAsync(group, resource).WaitAsync(Deadline) is null, "resource must be a miss");

    private static async Task Error<T>(Func<Task> operation) where T : Exception
    {
        try { await operation().WaitAsync(Deadline); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private static void Check(bool value, string message = "Resource cache assertion failed")
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static string Name(string group, string resource) =>
        "r-" + group + "-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(resource))) + ".resource";

    private static long OwnedBytes(Folder folder) =>
        Directory.GetFiles(folder.Root).Where(path => path.EndsWith(".resource", StringComparison.Ordinal) ||
            path.EndsWith(".stage", StringComparison.Ordinal)).Sum(path => new FileInfo(path).Length);

    private static void CheckStage(Folder folder, byte[] content)
    {
        string path = Directory.GetFiles(folder.Root, "*.stage").Single();
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[content.Length];
        file.ReadExactly(bytes);
        Check(file.Length == content.Length && bytes.SequenceEqual(content), "real stage bytes must already be flushed");
    }

    private sealed class Clock : TimeProvider
    {
        private long ticks = Start.UtcTicks;
        private Pause? next;
        internal DateTimeOffset Now
        {
            get => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
            set => Interlocked.Exchange(ref ticks, value.UtcTicks);
        }
        internal Pause PauseNext()
        {
            var pause = new Pause();
            Check(Interlocked.CompareExchange(ref next, pause, null) is null);
            return pause;
        }
        public override DateTimeOffset GetUtcNow()
        {
            Interlocked.Exchange(ref next, null)?.Wait();
            return Now;
        }
    }

    private sealed class Pause : IDisposable
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Wait()
        {
            Entered.TrySetResult();
            released.Task.WaitAsync(Deadline).GetAwaiter().GetResult();
        }
        public void Dispose() => released.TrySetResult();
    }

    private sealed class Folder : IDisposable
    {
        internal string Base { get; } = TestWorkspace.DataPath("file-resource-cache-" + Guid.NewGuid().ToString("N"));
        internal string Root { get; }
        internal Folder(bool createRoot = true)
        {
            Root = Path.Combine(Base, "resources");
            Directory.CreateDirectory(createRoot ? Root : Base);
        }
        internal string Entry(string group, string resource) => Path.Combine(Root, Name(group, resource));
        internal void Seed(string group, string resource, byte[] content)
        {
            File.WriteAllBytes(Entry(group, resource), content);
            File.SetLastWriteTimeUtc(Entry(group, resource), Start.UtcDateTime);
        }
        public void Dispose()
        {
            TestWorkspace.ValidatePath(TestWorkspace.DataRoot, Base);
            Remove(Base);
        }
        private static void Remove(string path)
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) == 0) { File.Delete(path); return; }
            if ((attributes & FileAttributes.ReparsePoint) == 0)
                foreach (string child in Directory.EnumerateFileSystemEntries(path)) Remove(child);
            Directory.Delete(path);
        }
    }

    private static void CreateJunction(string link, string target)
    {
        Directory.CreateDirectory(link);
        byte[] substitute = Encoding.Unicode.GetBytes(@"\??\" + Path.GetFullPath(target));
        byte[] print = Encoding.Unicode.GetBytes(Path.GetFullPath(target));
        byte[] buffer = new byte[16 + substitute.Length + 2 + print.Length + 2];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0xA0000003);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), checked((ushort)(buffer.Length - 8)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), checked((ushort)substitute.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), checked((ushort)(substitute.Length + 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14), checked((ushort)print.Length));
        substitute.CopyTo(buffer, 16); print.CopyTo(buffer, 16 + substitute.Length + 2);
        using var handle = CreateFileW(@"\\?\" + Path.GetFullPath(link), 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        Check(!handle.IsInvalid && DeviceIoControl(handle, 0x000900A4, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero),
            "junction fixture creation failed");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string fileName, string existingFileName, IntPtr security);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputLength,
        IntPtr output, int outputLength, out int returned, IntPtr overlapped);
}
