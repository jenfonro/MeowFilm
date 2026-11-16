using System.Reflection;
using System.Security.Cryptography;
using MeowFilm.Media;
using static MeowFilm.Windows.Tests.FFmpegVideoFixtures;

namespace MeowFilm.Windows.Tests;

public static class NativeVideoInputTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return new("native-video-input: typed keys validate UUID bytes one-use and private diagnostics", () =>
        {
            using var keys = NativeClearKeySet.FromHex(new Dictionary<string, string>
            { ["00112233-4455-6677-8899-aabbccddeeff"] = VideoKey, [AudioKid] = AudioKey });
            Check(keys.ToString() == "NativeClearKeySet", "keys absent from normal diagnostic");
            using var input = new NativeVideoInput(NativeVideoFormat.Dash, keys);
            var take = typeof(NativeVideoInput).GetMethod("Take", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var material = ((byte[] Pairs, byte[]? Default))take.Invoke(input, null)!;
            try
            {
                Check(Convert.ToHexStringLower(material.Pairs.AsSpan(0, 16)) == VideoKid &&
                    Convert.ToHexStringLower(material.Pairs.AsSpan(16, 16)) == VideoKey &&
                    Convert.ToHexStringLower(material.Pairs.AsSpan(32, 16)) == AudioKid, "network byte order and pair order");
                try { take.Invoke(input, null); throw new InvalidOperationException("input was reusable"); }
                catch (TargetInvocationException e) when (e.InnerException is PlaybackException { Code: PlaybackError.InvalidRequest }) { }
            }
            finally { CryptographicOperations.ZeroMemory(material.Pairs); }
            foreach (string kid in new[] { "bad", "00112233-4455-6677-8899-aabbccddeef-", new string('z', 32) })
                Error(() => NativeClearKeySet.FromHex(new Dictionary<string, string> { [kid] = VideoKey }));
            Error(() => NativeClearKeySet.FromHex(new Dictionary<string, string>
            { [VideoKid] = VideoKey, ["00112233-4455-6677-8899-aabbccddeeff"] = VideoKey }));
            return Task.CompletedTask;
        });
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static void Error(Func<NativeClearKeySet> action)
    {
        try { using var keys = action(); }
        catch (PlaybackException error) when (error.Code == PlaybackError.InvalidRequest &&
            error.Message == error.Code && error.InnerException is null) { return; }
        throw new InvalidOperationException("Expected fixed key validation failure");
    }
}
