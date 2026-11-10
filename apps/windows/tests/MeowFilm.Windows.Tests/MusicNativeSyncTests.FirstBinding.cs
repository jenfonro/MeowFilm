using System.Net.Http;
using System.Text.Json;

namespace MeowFilm.Windows.Tests;

public static partial class MusicNativeSyncTests
{
    private static IEnumerable<TestCase> FirstBindingPrecedenceCases()
    {
        yield return new("Music sync first binding restores only imported cancellations and later cancellation still wins", FirstBindingCancellationPrecedence);
        yield return new("Music sync first binding restores an acknowledged cancellation to the older account", FirstBindingRestoresAcknowledgedCancellation);
        yield return new("Music sync an existing empty binding cannot restore a pending cancellation", ExistingEmptyBindingCancellation);
        foreach (string platform in new[] { "wy", "tx" })
        {
            string p = platform;
            yield return new($"Music sync first {p} binding wins over the other platform remote deletion", () => FirstBindingRemoteDeletion(p));
        }
    }

    // Independent platform stores share only the production library/sync owner.
    private sealed class FirstBindingTransport : HttpMessageHandler
    {
        private readonly Dictionary<string, HttpMessageInvoker> servers;
        internal FirstBindingTransport(Server first, Server second) =>
            servers = new[] { first, second }.ToDictionary(server => server.Platform, server => new HttpMessageInvoker(server));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string platform = request.RequestUri!.Host switch
            {
                "music.163.com" or "interfacepc.music.163.com" => "wy",
                "u.y.qq.com" => "tx",
                _ => throw new InvalidOperationException("UNEXPECTED_FIRST_BINDING_HOST")
            };
            return servers[platform].SendAsync(request, token);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) foreach (var server in servers.Values) server.Dispose();
            base.Dispose(disposing);
        }
    }

    private static async Task SetFavoritesEnabled(Fixture f, string platform, bool enabled)
    {
        var state = Ok(await f.Sync.ExecuteAsync("music.librarySync.get"));
        Ok(await f.Sync.ExecuteAsync("music.librarySync.set", new
        {
            platform, favorites = enabled, playlists = false,
            ifRevision = state.GetProperty("revision").GetInt64(), confirm = true
        }));
    }

    private static async Task RemoveFavorites(Fixture f, params int[] numbers)
    {
        foreach (int number in numbers)
        {
            var detail = Ok(await f.Owner.ExecuteAsync("music.library.detail", new { id = "favorites" }));
            var song = detail.GetProperty("songs").EnumerateArray().Single(song => S(song, "title") == "Track " + number);
            Ok(await f.Owner.ExecuteAsync("music.library.removeSong", new
            {
                id = "favorites", ifRevision = detail.GetProperty("collection").GetProperty("revision").GetInt64(),
                songId = S(song, "songId")
            }));
        }
    }

    private static async Task ExpectFavorites(Fixture f, params int[] numbers)
    {
        JsonElement songs = Ok(await f.Owner.ExecuteAsync("music.library.detail", new { id = "favorites" })).GetProperty("songs");
        Check(songs.EnumerateArray().Select(song => S(song, "title")).Order()
            .SequenceEqual(numbers.Select(number => "Track " + number).Order()), "FAVORITES_MEMBERSHIP");
    }

    private static async Task FirstBindingCancellationPrecedence()
    {
        var newer = new Server("wy");
        await using var f = new Fixture(second: newer);
        f.Server.Favorite.Add(1); f.Server.Favorite.Add(2); f.Server.Favorite.Add(3);
        await f.Enable(); Ok(await f.Run());
        await f.Enable(false);
        await RemoveFavorites(f, 1, 3);
        await f.Restart();
        newer.Favorite.Add(1); newer.Favorite.Add(2);
        await SetFavoritesEnabled(f, "wy", true); Ok(await f.Run());
        await ExpectFavorites(f, 1, 2);
        Check(newer.Favorite.Songs.SequenceEqual([1, 2]) && newer.Writes == 0 && f.Server.Writes == 0,
            "NEW_BINDING_MUST_NOT_RECEIVE_OLDER_CANCELLATIONS");

        await f.Enable(); Ok(await f.Run());
        Check(f.Server.Favorite.Songs.SequenceEqual([1, 2]) &&
            f.Server.Operations.SequenceEqual(["DelSonglist"]) && newer.Writes == 0,
            "OLDER_ACCOUNT_RETAINS_RESTORED_SONG_WITHOUT_READDING_IT");
        await ExpectFavorites(f, 1, 2);

        await RemoveFavorites(f, 1);
        newer.FavoriteTarget = 1; newer.FavoriteAdd = false;
        Ok(await f.Run());
        await ExpectFavorites(f, 2);
        Check(f.Server.Favorite.Songs.SequenceEqual([2]) && newer.Favorite.Songs.SequenceEqual([2]),
            "NORMAL_SYNC_MUST_HONOR_A_LATER_CANCELLATION");
        int writes = f.Server.Writes + newer.Writes;
        await f.Restart(); Ok(await f.Run());
        Check(f.Server.Writes + newer.Writes == writes, "CONFIRMED_CHANGES_MUST_NOT_REPLAY");
    }

    private static async Task FirstBindingRestoresAcknowledgedCancellation()
    {
        var newer = new Server("wy");
        await using var f = new Fixture(second: newer);
        f.Server.Favorite.Add(1); f.Server.Favorite.Add(2);
        await f.Enable(); Ok(await f.Run());
        await RemoveFavorites(f, 1); Ok(await f.Run());
        Check(f.Server.Favorite.Songs.SequenceEqual([2]) && f.Server.Writes == 1);
        await f.Enable(false);
        newer.Favorite.Add(1); newer.Favorite.Add(2);
        await SetFavoritesEnabled(f, "wy", true); Ok(await f.Run());
        await ExpectFavorites(f, 1, 2);
        Check(newer.Writes == 0 && f.Server.Writes == 1);
        await f.Enable(); Ok(await f.Run());
        Check(f.Server.Favorite.Songs.Order().SequenceEqual([1, 2]) &&
            f.Server.Operations.SequenceEqual(["DelSonglist", "AddSonglist"]) && newer.Writes == 0,
            "OLDER_ACCOUNT_MUST_RECOLLECT_THE_NEW_UNION");
    }

    private static async Task ExistingEmptyBindingCancellation()
    {
        var newer = new Server("wy");
        await using var f = new Fixture(second: newer);
        f.Server.Favorite.Add(1); f.Server.Favorite.Add(2);
        await f.Enable(); Ok(await f.Run());
        await f.Enable(false);
        await SetFavoritesEnabled(f, "wy", true); Ok(await f.Run());
        Check(newer.Favorite.Songs.Count == 0 && newer.Writes == 0);
        await RemoveFavorites(f, 1);
        await f.Restart();
        newer.Favorite.Add(1); newer.Favorite.Add(2);
        newer.FavoriteTarget = 1; newer.FavoriteAdd = false;
        await f.Enable(); Ok(await f.Run());
        await ExpectFavorites(f, 2);
        Check(f.Server.Favorite.Songs.SequenceEqual([2]) && newer.Favorite.Songs.SequenceEqual([2]),
            "EMPTY_EXISTING_BINDING_IS_NOT_A_FIRST_UNION");
    }

    private static async Task FirstBindingRemoteDeletion(string newPlatform)
    {
        string oldPlatform = newPlatform == "wy" ? "tx" : "wy";
        var newer = new Server(newPlatform);
        await using var f = new Fixture(oldPlatform, second: newer);
        f.Server.Favorite.Add(1); f.Server.Favorite.Add(2);
        await f.Enable(); Ok(await f.Run());
        f.Server.Favorite.Songs.RemoveAt(0); f.Server.Favorite.Files.RemoveAt(0);
        newer.Favorite.Add(1); newer.Favorite.Add(2);
        await SetFavoritesEnabled(f, newPlatform, true); Ok(await f.Run());
        await ExpectFavorites(f, 1, 2);
        Check(f.Server.Favorite.Songs.Order().SequenceEqual([1, 2]) &&
            newer.Favorite.Songs.SequenceEqual([1, 2]) && newer.Writes == 0,
            "FIRST_BINDING_UNION_MUST_WIN_OVER_AN_EXISTING_REMOTE_DELETION");
    }
}
