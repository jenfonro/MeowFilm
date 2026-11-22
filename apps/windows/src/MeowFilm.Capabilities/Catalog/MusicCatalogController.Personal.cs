using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MeowFilm.Capabilities;

// These are in-process route arguments, never credentials or a generic RPC/URL.
internal sealed record CatalogTarget(string Operation, string Platform, string Title, JsonElement Parameters,
    bool ManagedPlaylist = false, string Artwork = "", string Subtitle = "", int? Count = null)
{
    public bool Private => Operation is "personalPlaylists" or "daily" or "fm" or "similar" or
        "heartbeat" || Parameters.TryGetProperty("personal", out _);
    public bool Cacheable => Operation is not ("fm" or "similar" or "heartbeat");
    public int Limit => Operation == "personalPlaylists" ? 30 :
        Parameters.TryGetProperty("personal", out var p) && MusicCatalogDto.Text(p, "kind") == "kugou-owned" ? 30 : 100;
}

public sealed partial class MusicCatalogController
{
    private readonly Dictionary<string, CatalogTarget> catalogTargets = [];
    private CatalogTarget? catalogLoadedTarget;
    private string catalogAccountStamp = "";
    private long? catalogExpiresAt;

    private static readonly IReadOnlyList<MusicCatalogChoice> PersonalKinds = Array.AsReadOnly<MusicCatalogChoice>(
        [new("all", "全部歌单")]);
    private static readonly IReadOnlyList<MusicCatalogChoice> QqPersonalKinds = Array.AsReadOnly<MusicCatalogChoice>(
        [.. PersonalKinds, new("created", "创建的歌单"), new("subscribed", "收藏的歌单")]);
    public IReadOnlyList<MusicCatalogChoice> CatalogPersonalKinds => SelectedCatalogPlatform.Key == "tx" ? QqPersonalKinds : PersonalKinds;
    public MusicCatalogChoice[] CatalogFmModes { get; } =
        [new("default", "私人 FM"), new("familiar", "熟悉"), new("explore", "探索"), new("puzzle", "拼图"), new("scene", "场景")];
    public MusicCatalogChoice[] CatalogFmScenes { get; } =
        [new("NIGHT_EMO", "伤感"), new("EXERCISE", "运动"), new("SLEEP_HELP", "助眠"), new("RELAX", "放松"),
         new("CHEERFUL", "欢快"), new("LYRICAL", "抒情"), new("CURE", "治愈"), new("FOCUS", "专注"),
         new("SWEET", "情歌"), new("RHYTHM_BLUES", "R&B"), new("RAINY", "下雨天"), new("GAMES", "打游戏"),
         new("RAP", "说唱"), new("K_POP", "K-Pop"), new("ORIGINAL_MUSICIAL", "宝藏原创"), new("ELECTRONIC", "电音"),
         new("COMMUTE", "出行"), new("TAKE_SHOWER", "洗澡"), new("COFFEE_SHOP", "咖啡馆"), new("ROCK", "摇滚"),
         new("INSPIRATIONAL", "励志"), new("CHINESE", "华语"), new("ENGLISH", "欧美"), new("YUEYU", "粤语"),
         new("MANYAO", "慢摇DJ"), new("JINGDIAN", "经典"), new("LIGHT", "轻音乐"), new("GUOFENG", "国风"),
         new("FOLK", "民谣"), new("ACG", "二次元"), new("GUDIAN", "古典"), new("JAZZ", "爵士"),
         new("JAPANESE", "日语"), new("GLOBAL", "全球"), new("FRANCH", "法语"), new("BLUE", "蓝调"),
         new("DANCE", "舞蹈"), new("LATIN", "拉丁"), new("PUNK", "放克"), new("COUNTRY", "乡村乐"),
         new("MUSICAL", "音乐剧"), new("YINGSHI", "影视"), new("HOT_SONG", "热歌")];
    public string CatalogPersonalKind { get; private set; } = "all";
    public string CatalogDailyBranch { get; set; } = "default";
    public string CatalogFmMode { get; set; } = "default";
    public string CatalogFmScene { get; set; } = "FOCUS";
    public bool IsCatalogPersonal => catalogLoadedTarget?.Operation == "personalPlaylists" && IsCatalogPage;
    public bool IsCatalogDaily => IsCatalogPage && (host.CurrentRoute == "catalog-daily" || host.CurrentRoute.StartsWith("catalog-daily/", StringComparison.Ordinal));
    public bool IsCatalogFm => catalogLoadedTarget?.Operation == "fm" && IsCatalogPage;
    public bool IsCatalogFmScene => IsCatalogFm && CatalogFmMode == "scene";
    public bool IsCatalogBatch => IsCatalogPage && catalogLoadedTarget?.Operation is
        "daily" or "fm" or "similar" or "heartbeat" or "album" or "artist";
    public bool IsCatalogCollection => IsCatalogPlaylist || IsCatalogBatch;
    public bool CanCatalogWy => CanCatalogBrowse && SelectedCatalogPlatform.Key == "wy";
    public bool CanCatalogBatchRefresh => IsCatalogBatch && CanCatalogBrowse;
    public string CatalogAccountText
    {
        get
        {
            var account = host.Accounts.Accounts.SingleOrDefault(a => a.Platform == SelectedCatalogPlatform.Key);
            if (account is null) return "";
            var name = account.DisplayName.Length > 0 ? account.DisplayName : account.UserId;
            return name.Length == 0 ? account.StateText : name + " · " + account.StateText;
        }
    }

    private string CatalogAccountKey(string platform)
    {
        if (platform is "kw" or "mg") return "public/" + platform;
        var account = host.Accounts.Accounts.Single(a => a.Platform == platform);
        return $"{host.Accounts.AccountEpoch}/{CatalogAccountObservation(platform)}/{platform}/{account.Generation}/{account.UserId}";
    }

    internal void RequireCatalogContent()
    {
        // Home is a last-successful display, not a grant from a prior account.
        // Its cards use their own identities and obtain current authority on use.
        if (IsCatalogHome) return;
        if (host.Search.IsSearchPage)
        {
            if (host.Search.SearchAccountEpoch != host.Accounts.AccountEpoch || !host.Search.HasSearchSession)
                throw new MusicOperationException("STALE_SESSION");
            return;
        }
        if (catalogLoadedPlatform.Length == 0 || catalogAccountStamp != CatalogAccountKey(catalogLoadedPlatform))
            throw new MusicOperationException("STALE_SESSION");
    }

    private bool RestoreCatalogPage(CatalogPageState state, string platform) =>
        state.AccountStamp == CatalogAccountKey(platform) &&
        (state.ExpiresAt is null || state.ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private void PersonalCatalogBindings()
    {
        foreach (string name in new[]
        {
            nameof(IsCatalogPersonal), nameof(IsCatalogDaily), nameof(IsCatalogFm),
            nameof(IsCatalogFmScene), nameof(IsCatalogBatch), nameof(IsCatalogCollection), nameof(CanCatalogWy),
            nameof(CanCatalogBatchRefresh), nameof(CatalogAccountText), nameof(CatalogPersonalKinds), nameof(CatalogPersonalKind),
            nameof(CatalogDailyBranch), nameof(CatalogFmMode), nameof(CatalogFmScene),
            nameof(IsCatalogSalesChart), nameof(CatalogChartHasPeriods), nameof(CatalogChartIsYearly),
            nameof(CatalogChartKind), nameof(CatalogChartPeriod), nameof(CatalogChartYear)
        })
            Changed(name);
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
        if (host.Publishing || !CanCatalogBrowse || !IsCatalogSalesChart ||
            !CatalogChartKinds.Any(choice => choice.Key == CatalogChartKind) ||
            !CatalogChartPeriods.Any(choice => choice.Key == CatalogChartPeriod)) return Task.CompletedTask;
        var parameters = new Dictionary<string, object?> { ["kind"] = CatalogChartKind,
            ["period"] = CatalogChartKind == "song-sales" ? "daily" : CatalogChartPeriod };
        if (CatalogChartKind != "song-sales" && CatalogChartPeriod == "yearly") parameters["year"] = CatalogChartYear;
        if (JsonSerializer.SerializeToElement(parameters).GetRawText() == catalogLoadedTarget!.Parameters.GetRawText())
            return Task.CompletedTask;
        PersonalCatalogBindings();
        return OpenCatalogTargetAsync(
            Target("chart", "wy", "排行榜", parameters) with { Artwork = catalogLoadedTarget?.Artwork ?? "" },
            CatalogChartKind);
    }

    public async Task ReadCatalogAccountAsync()
    {
        if (!CanCatalogBrowse) return;
        await host.Accounts.ReadAccountsAsync();
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
        if (host.Publishing || !CanCatalogBrowse) return Task.CompletedTask;
        string? route = IsCatalogPersonal ? "catalog-personal" : IsCatalogDaily ? "catalog-daily" : IsCatalogFm ? "catalog-fm" : null;
        if (route is not null && IsUnsupportedCatalogRoute(route)) return Task.CompletedTask;
        PersonalCatalogBindings();
        if (IsCatalogFm && host.Queue.HasPlaybackFm &&
            CatalogRouteTarget("catalog-fm", SelectedCatalogPlatform.Key) is { } activeFm)
            return host.Queue.StartRecommendation(activeFm);
        return route is null ? Task.CompletedTask : LoadCatalogAsync(route, "", 1, null, lifetime.Token);
    }

    internal Task ChangeCatalogPersonalKindAsync(MusicCatalogChoice choice)
    {
        if (host.Publishing || !CanCatalogBrowse || !IsCatalogPersonal || choice.Key == CatalogPersonalKind ||
            !CatalogPersonalKinds.Contains(choice)) return Task.CompletedTask;
        CatalogPersonalKind = choice.Key;
        return CatalogOptionsChangedAsync();
    }

    internal static bool IsCatalogRoute(string route) =>
        route is "catalog-home" or "catalog-channels" or "catalog-personal" or "catalog-daily" or "catalog-fm" or "catalog-search" ||
        new[] { "playlist", "channel", "chart", "album", "artist", "daily", "fm", "similar", "heartbeat" }
            .Any(kind => route.StartsWith("catalog-" + kind + "/", StringComparison.Ordinal));

    internal static CatalogTarget Target(string operation, string platform, string title, object parameters) =>
        new(operation, platform, title, JsonSerializer.SerializeToElement(parameters));

    private CatalogTarget? CatalogRouteTarget(string route, string platform)
    {
        if (route == "catalog-personal") return Target("personalPlaylists", platform, "平台歌单", new { kind = CatalogPersonalKind });
        if (route == "catalog-daily") return Target("daily", platform, CatalogDailyBranch == "style" ? "风格推荐" : "每日推荐", new { branch = CatalogDailyBranch });
        if (route == "catalog-fm") return Target("fm", platform, "私人 FM", new { mode = CatalogFmMode, submode = CatalogFmMode == "scene" ? CatalogFmScene : "" });
        if (catalogTargets.TryGetValue(route, out var target)) return target;
        if (route == host.CurrentRoute && catalogLoadedTarget is not null) return catalogLoadedTarget;
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

    internal bool IsUnsupportedCatalogRoute(string route)
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
            RetireCatalogLocation(route + "/" + target.Platform);
        catalogTargets.Remove(route); catalogTargets[route] = target;
        while (catalogTargets.Count > 100) catalogTargets.Remove(catalogTargets.Keys.First());
        return route;
    }

    internal Task OpenCatalogTargetAsync(CatalogTarget target, string? id = null)
    {
        RememberCatalogLocation();
        return RequestNavigationAsync(RegisterCatalogTarget(target, id));
    }

    private Task OpenPersonalCatalogCardAsync(MusicCatalogCard card)
    {
        var action = card.Action ?? JsonSerializer.SerializeToElement(new { type = card.Kind });
        if (card.Kind == "playlist" && card.Locator is { } locator)
            return OpenCatalogTargetAsync(Target("playlist", card.Platform, card.Title,
                new { id = card.ActionId, personal = locator }) with { ManagedPlaylist = card.IsManagedPlaylist, Artwork = card.Cover, Subtitle = card.Subtitle, Count = card.Count }, card.ActionId);
        if (card.Kind == "chart")
            return OpenCatalogTargetAsync(Target("chart", card.Platform, card.Title,
                new { kind = card.ActionId, period = "daily" }) with
            {
                Artwork = card.Cover.Length > 0 ? card.Cover : card.FirstPreviewCover,
                Subtitle = card.Subtitle,
                Count = card.Count
            }, card.ActionId);
        if (card.Kind == "album")
            return OpenCatalogTargetAsync(Target("album", card.Platform, card.Title, new { id = card.ActionId }) with
            {
                Artwork = card.Cover,
                Subtitle = card.Subtitle,
                Count = card.Count
            }, card.ActionId);
        if (card.Kind == "daily")
        {
            var branch = MusicCatalogDto.Text(action, "branch"); if (branch.Length == 0) branch = "default";
            return OpenCatalogTargetAsync(Target("daily", card.Platform, card.Title, new { branch })
                with { Artwork = card.Cover, Subtitle = card.Subtitle, Count = card.Count }, branch);
        }
        if (card.Kind == "similar")
        {
            if (!action.TryGetProperty("sourceIds", out var ids) || ids.ValueKind != JsonValueKind.Array)
                return CatalogBusinessError("INVALID_REQUEST");
            return host.Queue.StartRecommendation(Target("similar", card.Platform, card.Title,
                new { sourceIds = ids.Clone(), sourceType = MusicCatalogDto.Text(action, "sourceType") }), card);
        }
        if (card.Kind == "heartbeat")
            return host.Queue.StartRecommendation(Target("heartbeat", card.Platform, card.Title,
                new { entry = "favorites", songId = MusicCatalogDto.Text(action, "songId") }), card);
        if (card.Kind == "fm")
        {
            if (IsCatalogHome && MusicCatalogDto.Text(action, "ruleId").Length == 0 &&
                MusicCatalogDto.Text(action, "tabTagId").Length == 0)
                return host.Queue.StartRecommendation(Target("fm", card.Platform, card.Title, new { }), card);
            var mode = MusicCatalogDto.Text(action, "mode").ToLowerInvariant();
            mode = mode switch { "" => "default", "scene_rcmd" => "scene", "puzzle_mode_rcmd" => "puzzle", _ => mode };
            var p = new Dictionary<string, object?> { ["mode"] = mode, ["submode"] = MusicCatalogDto.Text(action, "submode") };
            foreach (var name in new[] { "ruleId", "tabTagId" })
                if (MusicCatalogDto.Text(action, name) is { Length: > 0 } value) p[name] = value;
            return host.Queue.StartRecommendation(Target("fm", card.Platform, card.Title, p), card);
        }
        return Task.CompletedTask;
    }

    internal Task CatalogBusinessError(string code)
    { host.SetStatus(MusicCatalogDto.ErrorText(code)); return Task.CompletedTask; }

    public Task SimilarCatalogCardAsync(MusicCatalogCard card)
    {
        if (!CanCatalogBrowse || !ContainsCatalogCard(card) || !card.CanSimilar) return Task.CompletedTask;
        try { RequireCatalogContent(); } catch (MusicOperationException e) { return CatalogBusinessError(e.Code); }
        return host.Queue.StartRecommendation(Target("similar", "wy", "相似歌曲",
            new { sourceIds = new[] { Recommendations.RecommendationJson.NativeId(card.Song!.Raw) }, sourceType = "song" }));
    }

    public Task AlbumCatalogCardAsync(MusicCatalogCard card)
    {
        if (!CanCatalogBrowse || !ContainsCatalogCard(card) || !card.CanAlbum) return Task.CompletedTask;
        try { RequireCatalogContent(); } catch (MusicOperationException e) { return CatalogBusinessError(e.Code); }
        return OpenCatalogTargetAsync(Target("album", card.Platform, card.Song!.Album.Length > 0 ? card.Song.Album : "专辑",
            new { id = card.AlbumId }) with
        {
            Artwork = card.Song.Cover.Length > 0 ? card.Song.Cover : card.Cover,
            Subtitle = card.Song.Artist
        }, card.AlbumId);
    }

    public Task QueueCatalogCardAsync(MusicCatalogCard card)
    {
        if (!CanEdit || !ContainsCatalogCard(card)) return Task.CompletedTask;
        if (card.Kind == "playlist") return CatalogPlaylistActionAsync("next", 0, entry: card);
        if (!card.CanFavorite) return Task.CompletedTask;
        try { RequireCatalogContent(); } catch (MusicOperationException e) { return CatalogBusinessError(e.Code); }
        return host.Library.AppendSongNextAsync(card.Song);
    }

    public Task HeartbeatFavoritesCatalogCardAsync(MusicCatalogCard card)
    {
        if (!CanCatalogBrowse || !ContainsCatalogCard(card) || !card.CanHeartbeatFavorites) return Task.CompletedTask;
        try { RequireCatalogContent(); } catch (MusicOperationException e) { return CatalogBusinessError(e.Code); }
        return host.Queue.StartRecommendation(Target("heartbeat", "wy", "心动模式", new { entry = "favorites" }));
    }

    public Task CatalogQueueRecommendationsAsync()
    {
        if (!CanCatalogWy) return Task.CompletedTask;
        return OpenQueueRecommendations();
        async Task OpenQueueRecommendations()
        {
            if (host.Queue.HasQueueRecommendations) await host.Queue.RetryRecommendationAsync();
            else await host.Queue.RecommendCurrentAsync(host.Playback.PlaybackPresentationKey);
            if (host.Queue.HasQueueRecommendations) await host.OpenPlayQueueAsync();
        }
    }

    private async Task ReadCatalogAccountForRequestAsync(string platform, long generation, CancellationToken token)
    {
        long epoch = host.Accounts.AccountEpoch;
        await host.Accounts.EnsureAccountsForRequestAsync(token);
        token.ThrowIfCancellationRequested();
        if (stopping || generation != host.LoadGeneration || epoch != host.Accounts.AccountEpoch) throw new OperationCanceledException(token);
    }

    private static void ApplyCatalogTarget(CatalogTarget target, Dictionary<string, object?> parameters, int number,
        JsonElement? continuation, bool refresh)
    {
        foreach (var p in target.Parameters.EnumerateObject()) parameters[p.Name] = p.Value.Clone();
        parameters["platform"] = target.Platform;
        if (target.Operation is "fm" or "similar" or "heartbeat") parameters.Remove("refresh");
        else parameters["refresh"] = refresh;
        if (target.Operation is "personalPlaylists" or "playlist")
        {
            parameters["page"] = number;
            parameters["limit"] = target.Operation == "personalPlaylists" ? 30 : 100;
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
        var expires = MusicCatalogDto.Field(reply, "expiresAt");
        return expires.ValueKind == JsonValueKind.Number && expires.TryGetInt64(out var value) ? value : null;
    }
}
