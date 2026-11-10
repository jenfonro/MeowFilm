using System.Text.Json;
using MeowFilm.Capabilities;
using MeowFilm.Media;
using static MeowFilm.Windows.Tests.FilmPlaybackFixture;

namespace MeowFilm.Windows.Tests;

public static class FilmVideoInputTests
{
    public static IEnumerable<TestCase> Cases()
    {
        yield return Test("provider hints select typed DASH HLS progressive or normal probing", () =>
        {
            foreach (var (hint, expected) in new (string? Hint, NativeVideoFormat Format)[]
            {
                (null, NativeVideoFormat.Auto), ("", NativeVideoFormat.Auto), ("mkv", NativeVideoFormat.Auto),
                ("dash", NativeVideoFormat.Dash), ("MPD", NativeVideoFormat.Dash), ("application/dash+xml", NativeVideoFormat.Dash),
                ("hls", NativeVideoFormat.Hls), ("m3u8", NativeVideoFormat.Hls), ("application/vnd.apple.mpegurl", NativeVideoFormat.Hls),
                ("mp4", NativeVideoFormat.Progressive), ("progressive", NativeVideoFormat.Progressive)
            })
            {
                using var input = FilmVideoInput.Read(JsonSerializer.SerializeToElement(hint), default);
                Check(input.Format == expected, "typed format retains the actual provider hint");
            }
            Invalid(() => FilmVideoInput.Read(FilmJson.Encode(0), default), PlaybackError.InvalidRequest);
        });
        yield return Test("legal multi KID hex and UUID keys are accepted privately", () =>
        {
            var drm = FilmJson.Encode(new
            {
                type = "clearkey", clearKeys = new Dictionary<string, string>
                {
                    ["00112233-4455-6677-8899-aabbccddeeff"] = "ffeeddccbbaa99887766554433221100",
                    ["102132435465768798a9bacbdcedfe0f"] = "0123456789abcdef0123456789abcdef"
                }
            });
            using var input = FilmVideoInput.Read(FilmJson.Encode("dash"), drm);
            Check(input.Format == NativeVideoFormat.Dash && input.ToString() == "NativeVideoInput(Dash)",
                "normal input diagnostics reveal neither KID nor key");
        });
        yield return Test("JWK KIDs keep network byte order and each key remains paired with its KID", () =>
        {
            const string first = "00112233445566778899AABBCCDDEEFF", second = "FFEEDDCCBBAA99887766554433221100";
            var drm = FilmJson.Encode(new { keySystem = "org.w3.clearkey", keys = new[]
            {
                new { kty = "oct", kid = "ABEiM0RVZneImaq7zN3u_w", k = "_-7dzLuqmYh3ZlVEMyIRAA" },
                new { kty = "oct", kid = "ECEyQ1RldoeYqbrL3O3-Dw", k = "ASNFZ4mrze8BI0VniavN7w" }
            } });
            var pairs = FilmVideoInput.ReadClearKeys(drm);
            Check(pairs.Count == 2 && pairs[first] == second &&
                pairs["102132435465768798A9BACBDCEDFE0F"] == "0123456789ABCDEF0123456789ABCDEF",
                "RFC 7517 bytes are not transformed through Guid.ToByteArray");
            using var input = FilmVideoInput.Read(FilmJson.Encode("dash"), drm);
            Check(input.Format == NativeVideoFormat.Dash, "JWK sets reach the same typed native path");
        });
        yield return Test("unknown DRM malformed keys and contradictory descriptors are not dropped", () =>
        {
            foreach (var drm in new[]
            {
                FilmJson.Encode(new { keySystem = "com.widevine.alpha", keyId = "synthetic-id" }),
                FilmJson.Encode(new { type = "clearkey" }), FilmJson.Encode("clearkey"),
                FilmJson.Encode(new { keySystem = "org.w3.clearkey", clearKeys = new { }, keys = Array.Empty<object>() })
            }) Invalid(() => FilmVideoInput.Read(default, drm), PlaybackError.AdaptiveDrmUnsupported);
            foreach (var drm in new[]
            {
                FilmJson.Encode(new { type = "clearkey", clearKeys = new Dictionary<string, string>() }),
                FilmJson.Encode(new { type = "clearkey", clearKeys = new Dictionary<string, string> { ["bad"] = "bad" } }),
                FilmJson.Encode(new { keySystem = "org.w3.clearkey", keys = new[] { new { kty = "RSA", kid = "ABEiM0RVZneImaq7zN3u_w", k = "_-7dzLuqmYh3ZlVEMyIRAA" } } }),
                FilmJson.Encode(new { keySystem = "org.w3.clearkey", keys = new[] { new { kty = "oct", kid = "ABEiM0RVZneImaq7zN3u_x", k = "_-7dzLuqmYh3ZlVEMyIRAA" } } }),
                FilmJson.Encode(new { keySystem = "org.w3.clearkey", keys = new[] { new { kty = "oct", kid = "ABEiM0RVZneImaq7zN3u_w==", k = "_-7dzLuqmYh3ZlVEMyIRAA" } } })
            }) Invalid(() => FilmVideoInput.Read(default, drm), PlaybackError.InvalidRequest);
        });
    }

    private static TestCase Test(string name, Action action) => new("film-video-input: " + name, () =>
    { action(); return Task.CompletedTask; });
    private static void Invalid(Func<NativeVideoInput> action, string code)
    {
        try { using var input = action(); }
        catch (FilmPlaybackException error) when (error.Code == code) { return; }
        catch (PlaybackException error) when (error.Code == code) { return; }
        throw new InvalidOperationException("Expected fixed failure " + code);
    }
}
