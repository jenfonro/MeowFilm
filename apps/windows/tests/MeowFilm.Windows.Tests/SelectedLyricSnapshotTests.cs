using System.IO;
using System.Security.Cryptography;
using System.Text;
using MeowFilm.Platform.Files;

namespace MeowFilm.Windows.Tests;

public static class SelectedLyricSnapshotTests
{
    public static IEnumerable<TestCase> Cases()
    {
        foreach (string extension in new[] { ".lrc", ".qrc", ".yrc", ".txt" })
            yield return new("Selected lyric snapshot native extension " + extension, async () =>
            {
                string path = Path.Combine(NewRoot(), "fixture" + extension);
                byte[] bytes = Encoding.UTF8.GetBytes("[00:01]合成🙂\r\n[00:12]第二行");
                await File.WriteAllBytesAsync(path, bytes);
                var result = await SelectedLyricSnapshot.ReadAsync(path);
                Equal(Encoding.UTF8.GetString(bytes), result.Text);
                Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), result.Sha256);
                Equal("fixture" + extension, result.Basename);
                Check((await File.ReadAllBytesAsync(path)).SequenceEqual(bytes));
                using var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                Check(exclusive.Length == bytes.Length); // Returned task has released its pins and handle.
            });
        foreach (string kind in new[] { "utf8-bom", "utf16-le", "utf16-be" })
            yield return new("Selected lyric snapshot explicit encoding " + kind, async () =>
            {
                Encoding encoding = kind switch { "utf8-bom" => new UTF8Encoding(true, true),
                    "utf16-le" => new UnicodeEncoding(false, true, true), _ => new UnicodeEncoding(true, true, true) };
                const string text = "[00:01]合成🙂\n[00:02]第二";
                byte[] bytes = [.. encoding.GetPreamble(), .. encoding.GetBytes(text)];
                string path = Path.Combine(NewRoot(), "encoded.lrc");
                await File.WriteAllBytesAsync(path, bytes);
                Equal(text, (await SelectedLyricSnapshot.ReadAsync(path)).Text);
            });
        yield return new("Selected lyric snapshot malformed encodings binary and empty payload fail without leaking path", async () =>
        {
            string path = Path.Combine(NewRoot(), "not-secret.lrc");
            foreach (byte[] bytes in new byte[][] { [], [0xc0, 0xaf], [0xff, 0xfe, 0x00],
                [0xfe, 0xff, 0xd8, 0x00], [0x00, 0x00], [0xef, 0xbb, 0xbf, 0x01] })
            {
                await File.WriteAllBytesAsync(path, bytes);
                await Reject(path, SelectedLyricSnapshotError.InvalidEncoding);
            }
        });
        yield return new("Selected lyric snapshot byte and decoded character budgets reject complete input not a prefix", async () =>
        {
            string path = Path.Combine(NewRoot(), "large.lrc");
            await File.WriteAllBytesAsync(path, new byte[SelectedLyricSnapshot.MaximumBytes + 1]);
            await Reject(path, SelectedLyricSnapshotError.TooLarge);
            await File.WriteAllTextAsync(path, new string('a', SelectedLyricSnapshot.MaximumCharacters + 1));
            await Reject(path, SelectedLyricSnapshotError.TooLarge);
        });
        yield return new("Selected lyric snapshot invalid path or extension never causes a directory search", async () =>
        {
            foreach (string path in new[] { "", "relative.lrc", @"\\host\share\file.lrc", @"\\?\C:\file.lrc",
                @"C:\fixture\..\file.lrc", @"C:\fixture\file.lrc:stream", @"C:\fixture\file.js", @"C:\fixture\NUL.lrc" })
                await Reject(path, SelectedLyricSnapshotError.InvalidPath);
        });
        yield return new("Selected lyric snapshot in-use file has fixed failure and can be read after owner release", async () =>
        {
            string path = Path.Combine(NewRoot(), "shared.lrc");
            await File.WriteAllTextAsync(path, "[00:01]synthetic");
            await using (var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                await Reject(path, SelectedLyricSnapshotError.InUse);
            Equal("[00:01]synthetic", (await SelectedLyricSnapshot.ReadAsync(path)).Text);
        });
        yield return new("Selected lyric snapshot cancelled read creates no write and releases all handles", async () =>
        {
            string path = Path.Combine(NewRoot(), "cancelled.lrc");
            await File.WriteAllTextAsync(path, "[00:01]unchanged");
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            try { await SelectedLyricSnapshot.ReadAsync(path, cancel.Token); throw new InvalidOperationException("Expected cancellation"); }
            catch (OperationCanceledException) { }
            Equal("[00:01]unchanged", await File.ReadAllTextAsync(path));
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            Check(exclusive.Length > 0);
        });
    }
    private static async Task Reject(string path, SelectedLyricSnapshotError code)
    {
        try { await SelectedLyricSnapshot.ReadAsync(path); throw new InvalidOperationException("Expected fixed snapshot rejection"); }
        catch (SelectedLyricSnapshotException error)
        { Equal(code, error.Error); Equal(code.ToString(), error.Message); Check(error.InnerException is null); }
    }
    private static string NewRoot()
    {
        string parent = Environment.GetEnvironmentVariable("MEOWFILM_TEST_DATA_ROOT") ?? throw new InvalidOperationException("Missing isolated test root");
        string root = Path.Combine(parent, "selected-lyric-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); return root;
    }
    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}"); }
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Selected lyric assertion failed"); }
}
