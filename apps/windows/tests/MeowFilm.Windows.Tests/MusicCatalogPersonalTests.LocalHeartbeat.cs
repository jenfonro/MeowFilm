using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Windows.Tests;

public static partial class MusicCatalogPersonalTests
{
    private static IEnumerable<TestCase> LocalHeartbeatCases()
    {
        yield return Case("local favorites use current Netease identity but never invent official playlist context", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-playlist/wy/500", default);
            f.Module.SelectedSong = f.Module.Songs[0];
            await f.Module.FavoriteAsync();
            await f.Module.NavigateAsync("favorites", default);
            await f.Module.PlayCollectionAsync();
            Check(f.Module.CanHeartbeatCurrent, "local favorites with an exact WY identity can request official qualification");
            var before = await f.Queue();
            f.App.FailNext("music.recommendations.start", "UNSUPPORTED");
            await f.Module.HeartbeatCurrentAsync(f.Module.PlaybackPresentationKey);
            var input = f.App.Last("music.recommendations.start");
            Equal("current", S(input, "entry"));
            Equal(MusicSongFixture.NativeId(before.GetProperty("current").GetProperty("song")), S(input, "songId"));
            Check(!input.TryGetProperty("playlistId", out _), "local favorites is not a remote playlist ID");
            var after = await f.Queue();
            Equal(S(before, "currentId"), S(after, "currentId")); Equal(N(before, "total"), N(after, "total"));
            Check(!f.Module.HasPlaybackHeartbeat, "an unqualified entry cannot become an active heartbeat");
            await f.Module.HeartbeatCurrentAsync(f.Module.PlaybackPresentationKey);
            await Until(() => f.Module.HasPlaybackHeartbeat && f.Module.CanReturnRecommendation && !f.Module.RecommendationLoading);
            var request = f.Platform.Last<MusicHeartbeatRequest>();
            Equal(MusicSongFixture.NativeId(before.GetProperty("current").GetProperty("song")), request.SongId);
            Check(request.PlaylistId is null, "the platform owner resolves the real favorites playlist");
            await f.Module.ReturnRecommendationAsync();
            await Until(() => !f.Module.HasActiveRecommendation);
            Equal(before.GetProperty("entries").GetRawText(), (await f.Queue()).GetProperty("entries").GetRawText());
            Equal(S(before, "currentId"), S(await f.Queue(), "currentId"));
        });
        yield return Case("local favorites without Netease identity cannot issue a heartbeat request", async () =>
        {
            await using var f = await Fixture.Create();
            await f.Module.NavigateAsync("catalog-playlist/tx/500", default);
            f.Module.SelectedSong = f.Module.Songs[0];
            await f.Module.FavoriteAsync();
            await f.Module.NavigateAsync("favorites", default);
            await f.Module.PlayCollectionAsync();
            Check(!f.Module.CanHeartbeatCurrent);
            await f.Module.HeartbeatCurrentAsync(f.Module.PlaybackPresentationKey);
            Check(!f.Platform.Requests.OfType<MusicHeartbeatRequest>().Any());
        });
    }
}
