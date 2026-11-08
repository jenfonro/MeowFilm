using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MeowFilm.Capabilities;

public sealed partial class MusicCapability
{
    // These are in-process route arguments, never credentials or a generic RPC/URL.
    private sealed record CatalogTarget(string Operation, string Platform, string Title, JsonElement Parameters)
    {
        public bool Private => Operation is "personalPlaylists" or "daily" or "fm" or "similar" or
            "heartbeat" or "queueRecommendations" || Parameters.TryGetProperty("personal", out _);
        public bool Cacheable => Operation is not ("fm" or "similar" or "heartbeat" or "queueRecommendations");
        public int Limit => Operation == "personalPlaylists" ? 30 :
            Parameters.TryGetProperty("personal", out var p) && MusicCatalogDto.Text(p, "kind") == "kugou-owned" ? 30 : 100;
    }
    private readonly Dictionary<string, CatalogTarget> catalogTargets = [];
    private CatalogTarget? catalogLoadedTarget;
    private string catalogAccountStamp = "";
    private long? catalogExpiresAt;

    public MusicCatalogChoice[] CatalogPersonalKinds => SelectedCatalogPlatform.Key == "tx"
        ? [new("all", "全部歌单"), new("created", "创建的歌单"), new("subscribed", "收藏的歌单")]
        : [new("all", "全部歌单")];
    public MusicCatalogChoice[] CatalogDailyBranches { get; } = [new("default", "每日推荐"), new("style", "风格推荐")];
    public MusicCatalogChoice[] CatalogFmModes { get; } =
        [new("default", "私人 FM"), new("familiar", "熟悉"), new("explore", "探索"), new("puzzle", "拼图"), new("scene", "场景")];
    public MusicCatalogChoice[] CatalogFmScenes { get; } =
        [new("FOCUS", "专注"), new("EXERCISE", "运动"), new("SLEEP_HELP", "助眠"), new("RELAX", "放松"),
         new("COMMUTE", "通勤"), new("NIGHT_EMO", "深夜"), new("CHEERFUL", "愉快"), new("RAINY", "雨天"),
         new("LYRICAL", "抒情"), new("CURE", "治愈"), new("SWEET", "甜蜜"), new("RHYTHM_BLUES", "节奏布鲁斯"),
         new("GAMES", "游戏"), new("RAP", "说唱"), new("K_POP", "韩流"), new("ORIGINAL_MUSICIAL", "原声"),
         new("ELECTRONIC", "电子"), new("TAKE_SHOWER", "沐浴"), new("COFFEE_SHOP", "咖啡馆"),
         new("ROCK", "摇滚"), new("INSPIRATIONAL", "励志"), new("CHINESE", "华语"), new("ENGLISH", "英语"),
         new("YUEYU", "粤语"), new("MANYAO", "慢摇"), new("JINGDIAN", "经典"), new("LIGHT", "轻音乐"),
         new("GUOFENG", "国风"), new("FOLK", "民谣"), new("ACG", "动漫"), new("GUDIAN", "古典"),
         new("JAZZ", "爵士"), new("JAPANESE", "日语"), new("GLOBAL", "世界音乐"), new("FRANCH", "法语"),
         new("BLUE", "蓝调"), new("DANCE", "舞曲"), new("LATIN", "拉丁"), new("PUNK", "朋克"),
         new("COUNTRY", "乡村"), new("MUSICAL", "音乐剧"), new("YINGSHI", "影视")];
    public string CatalogPersonalKind { get; set; } = "all";
    public string CatalogDailyBranch { get; set; } = "default";
    public string CatalogFmMode { get; set; } = "default";
    public string CatalogFmScene { get; set; } = "FOCUS";
    public bool IsCatalogPersonal => catalogLoadedTarget?.Operation == "personalPlaylists" && IsCatalogPage;
    public bool IsCatalogDaily => catalogLoadedTarget?.Operation == "daily" && IsCatalogPage;
    public bool IsCatalogFm => catalogLoadedTarget?.Operation == "fm" && IsCatalogPage;
    public bool IsCatalogFmScene => IsCatalogFm && CatalogFmMode == "scene";
    public bool IsCatalogBatch => IsCatalogPage && catalogLoadedTarget?.Operation is
        "daily" or "fm" or "similar" or "heartbeat" or "queueRecommendations" or "album" or "artist";
    public bool IsCatalogCollection => IsCatalogPlaylist || IsCatalogBatch;
    public bool CanCatalogWy => CanCatalogBrowse && SelectedCatalogPlatform.Key == "wy";
    public bool CanCatalogBatchRefresh => IsCatalogBatch && CanCatalogBrowse;
    public string CatalogAccountText
    {
        get
        {
            var account = Accounts.SingleOrDefault(a => a.Platform == SelectedCatalogPlatform.Key);
            if (account is null) return "";
            var name = account.DisplayName.Length > 0 ? account.DisplayName : account.UserId;
            return name.Length == 0 ? account.StateText : name + " · " + account.StateText;
        }
    }
    private string CatalogAccountKey(string platform)
    {
        if (platform is "kw" or "mg") return "public/" + platform;
        var account = Accounts.Single(a => a.Platform == platform);
        return $"{accountEpoch}/{CatalogAccountObservation(platform)}/{platform}/{account.Generation}/{account.UserId}";
    }
    private void RequireCatalogContent()
    {
        // Home is a last-successful display, not a grant from a prior account.
        // Its cards use their own identities and obtain current authority on use.
        if (IsCatalogHome) return;
        if (IsSearchPage)
        {
            if (searchAccountEpoch != accountEpoch || !searchSnapshots.ContainsKey(searchKind))
                throw new MusicOperationException("STALE_SESSION");
            return;
        }
        if (catalogLoadedPlatform.Length == 0 || catalogAccountStamp != CatalogAccountKey(catalogLoadedPlatform))
            throw new MusicOperationException("STALE_SESSION");
        if (catalogExpiresAt is { } expires && expires <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            throw new MusicOperationException("CATALOG_CONTENT_EXPIRED");
    }
    private bool RestoreCatalogPage(CatalogPageState state, string platform) =>
        state.AccountStamp == CatalogAccountKey(platform) &&
        (state.ExpiresAt is null || state.ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private void PersonalCatalogBindings()
    {
        foreach (string name in new[] { nameof(IsCatalogPersonal), nameof(IsCatalogDaily), nameof(IsCatalogFm),
            nameof(IsCatalogFmScene), nameof(IsCatalogBatch), nameof(IsCatalogCollection), nameof(CanCatalogWy),
            nameof(CanCatalogBatchRefresh), nameof(CatalogAccountText), nameof(CatalogPersonalKind), nameof(CatalogPersonalKinds),
            nameof(CatalogDailyBranch), nameof(CatalogFmMode), nameof(CatalogFmScene),
            nameof(IsCatalogSalesChart), nameof(CatalogChartHasPeriods), nameof(CatalogChartIsYearly),
            nameof(CatalogChartKind), nameof(CatalogChartPeriod), nameof(CatalogChartYear) }) Changed(name);
    }
    public MusicCatalogChoice[] CatalogChartKinds { get; } =
        [new("albums", "数字专辑"), new("singles", "数字单曲"), new("song-sales", "歌曲畅销指数")];
    public MusicCatalogChoice[] CatalogChartPeriods { get; } =
        [new("daily", "日榜"), new("weekly", "周榜"), new("yearly", "年榜"), new("overall", "总榜")];
    public string CatalogChartKind { get; set; } = "albums";
    public string CatalogChartPeriod { get; set; } = "daily";
    public int CatalogChartYear { get; set; } = DateTime.UtcNow.Year;
    public bool IsCatalogSalesChart => IsCatalogPage && catalogLoadedTarget?.Operation == "chart" &&
        MusicCatalogDto.Text(catalogLoadedTarget.Parameters, "kind") != "user-recommendations";
    public bool CatalogChartHasPeriods => IsCatalogSalesChart && CatalogChartKind is "albums" or "singles";
    public bool CatalogChartIsYearly => CatalogChartHasPeriods && CatalogChartPeriod == "yearly";
    public Task ChangeCatalogChartAsync()
    {
        if (publishing || !CanCatalogBrowse || !IsCatalogSalesChart ||
            !CatalogChartKinds.Any(choice => choice.Key == CatalogChartKind) ||
            !CatalogChartPeriods.Any(choice => choice.Key == CatalogChartPeriod)) return Task.CompletedTask;
        var parameters = new Dictionary<string, object?> { ["kind"] = CatalogChartKind,
            ["period"] = CatalogChartKind == "song-sales" ? "daily" : CatalogChartPeriod };
        if (CatalogChartKind != "song-sales" && CatalogChartPeriod == "yearly") parameters["year"] = CatalogChartYear;
        if (JsonSerializer.SerializeToElement(parameters).GetRawText() == catalogLoadedTarget!.Parameters.GetRawText())
            return Task.CompletedTask;
        PersonalCatalogBindings();
        return OpenCatalogTargetAsync(Target("chart", "wy", "排行榜", parameters), CatalogChartKind);
    }
    public async Task ReadCatalogAccountAsync()
    {
        if (!CanCatalogBrowse) return;
        await ReadAccountsAsync();
        if (catalogLoadedPlatform.Length > 0 && catalogAccountStamp.Length > 0 &&
            catalogAccountStamp != CatalogAccountKey(catalogLoadedPlatform)) RetireCatalogSession();
        CatalogBindings();
    }
    public Task OpenCatalogFeatureAsync(string feature)
    {
        if (!CanCatalogBrowse || feature is not ("personal" or "daily" or "fm") ||
            IsUnsupportedCatalogRoute("catalog-" + feature)) return Task.CompletedTask;
        return RequestNavigationAsync("catalog-" + feature);
    }
    public Task CatalogOptionsChangedAsync()
    {
        if (publishing || !CanCatalogBrowse) return Task.CompletedTask;
        string? route = IsCatalogPersonal ? "catalog-personal" : IsCatalogDaily ? "catalog-daily" : IsCatalogFm ? "catalog-fm" : null;
        if (route is not null && IsUnsupportedCatalogRoute(route)) return Task.CompletedTask;
        PersonalCatalogBindings();
        if (IsCatalogFm && HasActiveRecommendation &&
            RecText(recommendationSnapshot!.Value, "kind") == "fm" &&
            CatalogRouteTarget("catalog-fm", SelectedCatalogPlatform.Key) is { } activeFm)
            return StartRecommendation(activeFm);
        return route is null ? Task.CompletedTask : LoadCatalogAsync(route, "", 1, null, lifetime.Token);
    }
    private static bool IsCatalogRoute(string route) =>
        route is "catalog-home" or "catalog-channels" or "catalog-personal" or "catalog-daily" or "catalog-fm" or "catalog-search" ||
        new[] { "playlist", "channel", "chart", "album", "artist", "daily", "fm", "similar", "heartbeat", "queueRecommendations" }
            .Any(kind => route.StartsWith("catalog-" + kind + "/", StringComparison.Ordinal));
    private static CatalogTarget Target(string operation, string platform, string title, object parameters) =>
        new(operation, platform, title, JsonSerializer.SerializeToElement(parameters));
    private CatalogTarget? CatalogRouteTarget(string route, string platform)
    {
        if (route == "catalog-personal") return Target("personalPlaylists", platform, "平台歌单", new { kind = CatalogPersonalKind });
        if (route == "catalog-daily") return Target("daily", platform, CatalogDailyBranch == "style" ? "风格推荐" : "每日推荐", new { branch = CatalogDailyBranch });
        if (route == "catalog-fm") return Target("fm", platform, "私人 FM", new { mode = CatalogFmMode, submode = CatalogFmMode == "scene" ? CatalogFmScene : "" });
        if (catalogTargets.TryGetValue(route, out var target)) return target;
        if (route == location.Route && catalogLoadedTarget is not null) return catalogLoadedTarget;
        var parts = route.Split('/', 3);
        if (parts.Length == 3 && parts[0] == "catalog-chart")
            return Target("chart", parts[1], "排行榜", new { kind = Uri.UnescapeDataString(parts[2]), period = "daily" });
        if (parts.Length == 3 && parts[0] == "catalog-album")
            return Target("album", parts[1], "专辑", new { id = Uri.UnescapeDataString(parts[2]) });
        if (parts.Length == 3 && parts[0] == "catalog-artist")
            return Target("artist", parts[1], "歌手", new { id = Uri.UnescapeDataString(parts[2]) });
        if (parts.Length == 3 && parts[0] == "catalog-daily")
            return Target("daily", parts[1], parts[2] == "style" ? "风格推荐" : "每日推荐", new { branch = parts[2] });
        return null;
    }
    private bool IsUnsupportedCatalogRoute(string route)
    {
        if (!IsCatalogRoute(route)) return false;
        var platform = route.Split('/', 3) is { Length: 3 } parts ? parts[1] : SelectedCatalogPlatform.Key;
        return CatalogRouteTarget(route, platform) is { Operation: not ("personalPlaylists" or "playlist"), Platform: not "wy" } target &&
            CatalogPlatforms.Any(item => item.Key == target.Platform);
    }
    private string RegisterCatalogTarget(CatalogTarget target, string? id = null)
    {
        id ??= Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(target.Parameters.GetRawText())));
        var route = "catalog-" + target.Operation + "/" + target.Platform + "/" + Uri.EscapeDataString(id);
        if (!catalogTargets.TryGetValue(route, out var previous) || previous.Parameters.GetRawText() != target.Parameters.GetRawText())
            catalogLocations.Remove(route + "/" + target.Platform);
        catalogTargets.Remove(route); catalogTargets[route] = target;
        while (catalogTargets.Count > 100) catalogTargets.Remove(catalogTargets.Keys.First());
        return route;
    }
    private Task OpenCatalogTargetAsync(CatalogTarget target, string? id = null)
    {
        RememberCatalogLocation();
        return RequestNavigationAsync(RegisterCatalogTarget(target, id));
    }
    private Task OpenPersonalCatalogCardAsync(MusicCatalogCard card)
    {
        var action = card.Action ?? JsonSerializer.SerializeToElement(new { type = card.Kind });
        if (card.Kind == "playlist" && card.Locator is { } locator)
            return OpenCatalogTargetAsync(Target("playlist", card.Platform, card.Title, new { id = card.ActionId, personal = locator }), card.ActionId);
        if (card.Kind == "chart")
            return OpenCatalogTargetAsync(Target("chart", card.Platform, card.Title,
                new { kind = card.ActionId, period = "daily" }), card.ActionId);
        if (card.Kind == "album")
            return OpenCatalogTargetAsync(Target("album", card.Platform, card.Title, new { id = card.ActionId }), card.ActionId);
        if (card.Kind == "daily")
        {
            var branch = MusicCatalogDto.Text(action, "branch"); if (branch.Length == 0) branch = "default";
            return OpenCatalogTargetAsync(Target("daily", card.Platform, card.Title, new { branch }), branch);
        }
        if (card.Kind == "similar")
        {
            if (!action.TryGetProperty("sourceIds", out var ids) || ids.ValueKind != JsonValueKind.Array)
                return CatalogBusinessError("INVALID_REQUEST");
            return StartRecommendation(Target("similar", card.Platform, card.Title, new { sourceIds = ids.Clone() }));
        }
        if (card.Kind == "fm")
        {
            var mode = MusicCatalogDto.Text(action, "mode").ToLowerInvariant();
            mode = mode switch { "" => "default", "scene_rcmd" => "scene", "puzzle_mode_rcmd" => "puzzle", _ => mode };
            var p = new Dictionary<string, object?> { ["mode"] = mode, ["submode"] = MusicCatalogDto.Text(action, "submode") };
            foreach (var name in new[] { "ruleId", "tabTagId" })
                if (MusicCatalogDto.Text(action, name) is { Length: > 0 } value) p[name] = value;
            return StartRecommendation(Target("fm", card.Platform, card.Title, p));
        }
        return Task.CompletedTask;
    }
    private Task CatalogBusinessError(string code)
    { status = MusicCatalogDto.ErrorText(code); Changed(nameof(Status)); return Task.CompletedTask; }
    public Task SimilarCatalogCardAsync(MusicCatalogCard card)
    {
        if (!CanCatalogBrowse || !ContainsCatalogCard(card) || !card.CanSimilar) return Task.CompletedTask;
        try { RequireCatalogContent(); } catch (MusicOperationException e) { return CatalogBusinessError(e.Code); }
        return StartRecommendation(Target("similar", card.Platform, "相似歌曲", new { sourceIds = new[] { card.ActionId } }));
    }
    public Task AlbumCatalogCardAsync(MusicCatalogCard card)
    {
        if (!CanCatalogBrowse || !ContainsCatalogCard(card) || !card.CanAlbum) return Task.CompletedTask;
        try { RequireCatalogContent(); } catch (MusicOperationException e) { return CatalogBusinessError(e.Code); }
        return OpenCatalogTargetAsync(Target("album", card.Platform, card.Song!.Album.Length > 0 ? card.Song.Album : "专辑",
            new { id = card.AlbumId }), card.AlbumId);
    }
    public Task QueueCatalogCardAsync(MusicCatalogCard card)
    {
        if (!CanEdit || !ContainsCatalogCard(card)) return Task.CompletedTask;
        if (card.Kind == "playlist") return CatalogPlaylistActionAsync("next", 0, entry: card);
        if (!card.CanFavorite) return Task.CompletedTask;
        try { RequireCatalogContent(); } catch (MusicOperationException e) { return CatalogBusinessError(e.Code); }
        return AppendSongNextAsync(card.Song);
    }
    public Task HeartbeatCatalogCardAsync(MusicCatalogCard card)
    {
        if (!CanCatalogBrowse || !IsCatalogPlaylist || !ContainsCatalogCard(card) || !card.CanSimilar) return Task.CompletedTask;
        try { RequireCatalogContent(); } catch (MusicOperationException e) { return CatalogBusinessError(e.Code); }
        var parts = location.Route.Split('/', 3);
        return StartRecommendation(Target("heartbeat", card.Platform, "心动推荐", new {
            playlistId = Uri.UnescapeDataString(parts[2]), startMusicId = card.ActionId, songId = card.ActionId, fromHome = false }));
    }
    public Task CatalogQueueRecommendationsAsync()
    {
        if (!CanCatalogWy) return Task.CompletedTask;
        return TrackCatalogAction(async (signal, currentAction) =>
        {
            var ids = new List<string>(); long? version = null; string currentId = ""; int count;
            do
            {
                var reply = await Call("music.queue.get", new { offset = ids.Count, limit = 100 }, signal);
                long nextVersion = MusicDto.Integer(reply, "version"); count = MusicDto.Count(reply, "total");
                if (count is < 1 or > 1000) throw new MusicOperationException(count == 0 ? "NOT_FOUND" : "LIMIT");
                if (version is not null && version != nextVersion) throw new MusicOperationException("REVISION_CONFLICT");
                version = nextVersion;
                if (reply.GetProperty("current").ValueKind == JsonValueKind.Object)
                    currentId = SongJson.NativeId(reply.GetProperty("current").GetProperty("song"));
                foreach (var entry in MusicDto.Page(reply, "entries", ids.Count))
                {
                    var song = entry.GetProperty("song");
                    if (SongJson.Platform(song) != "wy")
                        throw new MusicOperationException("UNSUPPORTED");
                    var id = SongJson.NativeId(song);
                    MusicDto.Require(id.Length > 0);
                    ids.Add(id);
                }
            } while (ids.Count < count);
            if (!currentAction()) return;
            if (currentId.Length == 0 || !ids.Contains(currentId)) throw new MusicOperationException("NOT_FOUND");
            await OpenCatalogTargetAsync(Target("queueRecommendations", "wy", "队列推荐",
                new { songIds = ids.ToArray(), currentPlaySongId = currentId }));
        }, "正在读取队列");
    }
    private async Task ReadCatalogAccountForRequestAsync(string platform, long generation, CancellationToken token)
    {
        long epoch = accountEpoch;
        await EnsureAccountsForRequestAsync(token);
        token.ThrowIfCancellationRequested();
        if (stopping || generation != loadGeneration || epoch != accountEpoch) throw new OperationCanceledException(token);
        var currentAccount = Accounts.Single(a => a.Platform == platform);
        if (currentAccount.State is not ("saved" or "verified")) throw new MusicOperationException("AUTH_REQUIRED");
    }
    private static void ApplyCatalogTarget(CatalogTarget target, Dictionary<string, object?> parameters, int number,
        JsonElement? continuation, bool refresh)
    {
        foreach (var p in target.Parameters.EnumerateObject()) parameters[p.Name] = p.Value.Clone();
        parameters["platform"] = target.Platform;
        if (target.Operation is "fm" or "similar" or "heartbeat" or "queueRecommendations") parameters.Remove("refresh");
        else parameters["refresh"] = refresh;
        if (target.Operation == "personalPlaylists")
        {
            parameters["page"] = number; parameters["limit"] = 30;
            if (continuation is not null) parameters["continuation"] = continuation.Value;
        }
    }
    private void PublishPersonalCatalog(CatalogPageState state)
    {
        catalogLoadedTarget = state.Target; catalogAccountStamp = state.AccountStamp;
        catalogExpiresAt = state.ExpiresAt;
        if (state.Target is not { } target) return;
        if (target.Operation == "personalPlaylists") CatalogPersonalKind = MusicCatalogDto.Text(target.Parameters, "kind");
        if (target.Operation == "chart")
        {
            CatalogChartKind = MusicCatalogDto.Text(target.Parameters, "kind");
            CatalogChartPeriod = MusicCatalogDto.Text(target.Parameters, "period");
            CatalogChartYear = target.Parameters.TryGetProperty("year", out var year) ? year.GetInt32() : DateTime.UtcNow.Year;
        }
        if (target.Operation == "daily") CatalogDailyBranch = MusicCatalogDto.Text(target.Parameters, "branch");
        if (target.Operation == "fm")
        {
            CatalogFmMode = MusicCatalogDto.Text(target.Parameters, "mode");
            if (MusicCatalogDto.Text(target.Parameters, "submode") is { Length: > 0 } scene) CatalogFmScene = scene;
        }
    }
    private static long? CatalogExpiry(JsonElement reply)
    {
        if (!reply.TryGetProperty("expiresAt", out var expires) || expires.ValueKind == JsonValueKind.Null) return null;
        MusicDto.Require(expires.ValueKind == JsonValueKind.Number && expires.TryGetInt64(out var value) && value >= 0);
        return expires.GetInt64();
    }
    private void ValidatePersonalCatalog(JsonElement reply, CatalogTarget? target, int count)
    {
        if (target is null) return;
        if (target.Operation == "personalPlaylists")
        {
            var account = Accounts.Single(a => a.Platform == target.Platform);
            if (account.UserId.Length > 0 && MusicCatalogDto.Text(reply, "accountId") != account.UserId)
                throw new MusicOperationException("STALE_SESSION");
            MusicDto.Require(MusicCatalogDto.Text(reply, "collection") == MusicCatalogDto.Text(target.Parameters, "kind"));
        }
        else if (target.Operation is not "playlist")
        {
            MusicDto.Require(!reply.TryGetProperty("more", out var more) || more.ValueKind == JsonValueKind.False);
            MusicDto.Require(!reply.TryGetProperty("count", out _) || MusicDto.Count(reply, "count") == count);
            if (target.Operation == "album")
                MusicDto.Require(MusicCatalogDto.Text(reply, "id") == MusicCatalogDto.Text(target.Parameters, "id") &&
                    MusicDto.Count(reply, "total") == count);
            if (target.Operation == "artist")
                MusicDto.Require(MusicCatalogDto.Text(reply, "id") == MusicCatalogDto.Text(target.Parameters, "id") &&
                    MusicDto.Count(reply, "total") == count);
        }
    }
}
