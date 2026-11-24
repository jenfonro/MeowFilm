using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowFilm.Capabilities.Platforms;

namespace MeowFilm.Capabilities;

internal sealed record CatalogPageState(MusicCatalogSection[] Sections, MusicCatalogTag[] Tags,
    string Tag, string Title, string Artwork, bool More, JsonElement? Next, int Total, double Scroll = 0,
    CatalogTarget? Target = null, string AccountStamp = "", long? ExpiresAt = null, JsonElement? DailySelection = null)
{
    public CatalogOptionalReads? OptionalReads { get; set; }
    public bool ContinuationBlocked { get; init; }
}

/// <summary>
/// Standalone Catalog service owning personal playlists/collections routing &amp; targets,
/// platform/chart/FM/daily filter options, account-stamped session validation, and the daily style editor.
/// </summary>
internal sealed class MusicPersonalCatalogService(
    MusicRuntimeContext ctx,
    MusicAccountsController accounts,
    MusicQueueController queue,
    MusicSearchController search,
    MusicCatalogActionState catalogActions,
    MusicContentState contentState,
    Action<string> notify)
{
    private static readonly MusicCatalogPlatform NeteasePlatform = new("wy", "网易云音乐");
    private static readonly MusicCatalogPlatform KuwoPlatform = new("kw", "酷我音乐");
    private static readonly MusicCatalogPlatform MiguPlatform = new("mg", "咪咕音乐");
    private static readonly IReadOnlyList<MusicCatalogPlatform> StandardCatalogChoices = Array.AsReadOnly<MusicCatalogPlatform>(
        [NeteasePlatform, new("tx", "QQ 音乐"), new("kg", "酷狗音乐")]);
    private static readonly IReadOnlyList<MusicCatalogPlatform> KuwoPlatformChoices =
        Array.AsReadOnly<MusicCatalogPlatform>([.. StandardCatalogChoices, KuwoPlatform]);
    private static readonly IReadOnlyList<MusicCatalogPlatform> MiguPlatformChoices =
        Array.AsReadOnly<MusicCatalogPlatform>([.. StandardCatalogChoices, MiguPlatform]);

    private static readonly IReadOnlyList<MusicCatalogChoice> PersonalKinds = Array.AsReadOnly<MusicCatalogChoice>(
        [new("all", "全部歌单")]);
    private static readonly IReadOnlyList<MusicCatalogChoice> QqPersonalKinds = Array.AsReadOnly<MusicCatalogChoice>(
        [.. PersonalKinds, new("created", "创建的歌单"), new("subscribed", "收藏的歌单")]);

    private readonly Dictionary<string, CatalogTarget> catalogTargets = [];
    private CatalogTarget? catalogLoadedTarget;
    private string catalogAccountStamp = "";

    // Daily style editor state
    private MusicDailyStyleEditor? dailyStyleEditor;
    private string dailyStyleAccount = "", dailyStyleCategory = "", dailyStyleSelectedCategory = "";
    private MusicDailyStyleTag[] dailyStyleSelected = [];
    private readonly HashSet<string> dailyStyleDraft = new(StringComparer.Ordinal);
    private IReadOnlyList<MusicCatalogTagGroup> dailyStyleGroups = [];
    private long dailyStyleRevision;
    private bool dailyStyleBusy, dailyStyleNeedsRead;

    internal IReadOnlyList<MusicCatalogPlatform> CatalogPlatforms { get; } =
        Array.AsReadOnly<MusicCatalogPlatform>([.. StandardCatalogChoices, KuwoPlatform, MiguPlatform]);

    internal IReadOnlyList<MusicCatalogPlatform> PlatformChoicesFor(string loadedPlatform) => loadedPlatform switch
    {
        "kw" => KuwoPlatformChoices,
        "mg" => MiguPlatformChoices,
        _ => StandardCatalogChoices
    };

    internal MusicCatalogPlatform SelectedCatalogPlatform { get; set; } = NeteasePlatform;
    internal IReadOnlyList<MusicCatalogChoice> CatalogPersonalKinds => SelectedCatalogPlatform.Key == "tx" ? QqPersonalKinds : PersonalKinds;
    internal string CatalogPersonalKind { get; set; } = "all";
    internal string CatalogDailyBranch { get; set; } = "default";
    internal string CatalogFmMode { get; set; } = "default";
    internal string CatalogFmScene { get; set; } = "FOCUS";

    internal MusicCatalogChoice[] CatalogChartKinds { get; } =
        [new("albums", "数字专辑"), new("singles", "数字单曲"), new("song-sales", "歌曲畅销指数")];
    internal MusicCatalogChoice[] CatalogChartPeriods { get; } =
        [new("daily", "日榜"), new("weekly", "周榜"), new("yearly", "年榜"), new("overall", "总榜")];
    internal string CatalogChartKind { get; set; } = "albums";
    internal string CatalogChartPeriod { get; set; } = "daily";
    internal int CatalogChartYear { get; set; } = DateTime.UtcNow.Year;

    internal CatalogTarget? LoadedTarget { get => catalogLoadedTarget; set => catalogLoadedTarget = value; }
    internal string AccountStamp { get => catalogAccountStamp; set => catalogAccountStamp = value; }

    private bool IsCatalogPage => ctx.CurrentRoute.StartsWith("catalog-", StringComparison.Ordinal);
    private bool CanCatalogBrowse => !ctx.Stopping && ctx.Commands == 0;

    internal bool IsCatalogPersonal => catalogLoadedTarget?.Operation == "personalPlaylists" && IsCatalogPage;
    internal bool IsCatalogDaily => IsCatalogPage && (ctx.CurrentRoute == "catalog-daily" || ctx.CurrentRoute.StartsWith("catalog-daily/", StringComparison.Ordinal));
    internal bool IsCatalogFm => catalogLoadedTarget?.Operation == "fm" && IsCatalogPage;
    internal bool IsCatalogFmScene => IsCatalogFm && CatalogFmMode == "scene";
    internal bool IsCatalogBatch => IsCatalogPage && catalogLoadedTarget?.Operation is
        "daily" or "fm" or "similar" or "heartbeat" or "album" or "artist";
    internal bool IsCatalogPlaylist => ctx.CurrentRoute.StartsWith("catalog-playlist/", StringComparison.Ordinal);
    internal bool IsCatalogCollection => IsCatalogPlaylist || IsCatalogBatch;
    internal bool CanCatalogWy => CanCatalogBrowse && SelectedCatalogPlatform.Key == "wy";
    internal bool CanCatalogBatchRefresh => IsCatalogBatch && CanCatalogBrowse;
    internal bool IsCatalogSalesChart => IsCatalogPage && catalogLoadedTarget?.Operation == "chart" &&
        catalogLoadedTarget.Params.Kind != "user-recommendations";
    internal bool CatalogChartHasPeriods => IsCatalogSalesChart && CatalogChartKind is "albums" or "singles";
    internal bool CatalogChartIsYearly => CatalogChartHasPeriods && CatalogChartPeriod == "yearly";

    internal string DailyOtherBranch => IsDailyStyle ? "default" : "style";
    internal string DailyOtherBranchTitle => IsDailyStyle ? "每日推荐" : "风格推荐";
    internal bool IsDailyStyle => IsCatalogDaily && CatalogDailyBranch == "style";
    internal bool IsDailyStyleEditorOpen => dailyStyleEditor is not null;
    internal bool DailyStyleBusy => dailyStyleBusy;
    internal bool DailyStyleNeedsRead => dailyStyleNeedsRead;
    internal bool CanEditDailyStyle => IsDailyStyle && !dailyStyleBusy && ctx.LoadCancellation is null && CanCatalogBrowse;
    internal bool CanConfirmDailyStyle => CanEditDailyStyle && !dailyStyleNeedsRead && dailyStyleDraft.Count > 0 && IsDailyStyleEditorOpen;
    internal string DailyStyleSelectionText => dailyStyleSelected.Length == 0 ? "选择风格" : string.Join(" · ", dailyStyleSelected.Select(t => t.Name));
    internal IReadOnlyList<MusicCatalogTagGroup> DailyStyleGroups => dailyStyleGroups;

    internal string CatalogAccountText
    {
        get
        {
            var account = accounts.Accounts.SingleOrDefault(a => a.Platform == SelectedCatalogPlatform.Key);
            if (account is null) return "";
            var name = account.DisplayName.Length > 0 ? account.DisplayName : account.UserId;
            return name.Length == 0 ? account.StateText : name + " · " + account.StateText;
        }
    }

    internal string CatalogAccountKey(string platform)
    {
        if (platform is "kw" or "mg") return "public/" + platform;
        var account = accounts.Accounts.Single(a => a.Platform == platform);
        return $"{accounts.AccountEpoch}/{accounts.PlatformObservation(platform)}/{platform}/{account.Generation}/{account.UserId}";
    }

    internal void RequireCatalogContent(bool isCatalogHome, string loadedPlatform)
    {
        // Home is a last-successful display, not a grant from a prior account.
        // Its cards use their own identities and obtain current authority on use.
        if (isCatalogHome) return;
        if (search.IsSearchPage)
        {
            if (search.SearchAccountEpoch != accounts.AccountEpoch || !search.HasSearchSession)
                throw new MusicOperationException("STALE_SESSION");
            return;
        }
        if (loadedPlatform.Length == 0 || catalogAccountStamp != CatalogAccountKey(loadedPlatform))
            throw new MusicOperationException("STALE_SESSION");
    }

    internal bool RestoreCatalogPage(CatalogPageState state, string platform) =>
        state.AccountStamp == CatalogAccountKey(platform) &&
        (state.ExpiresAt is null || state.ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    internal static bool IsPrivateCatalogCard(MusicCatalogCard? card) =>
        card is not null && (card.RequiresAccount || card.Locator is not null);

    internal void RequireCatalogAccount(MusicCatalogCard? card)
    {
        if (!IsPrivateCatalogCard(card)) return;
        if (accounts.Accounts.Single(account => account.Platform == card!.Platform).State is not ("saved" or "verified"))
            throw new MusicOperationException("AUTH_REQUIRED");
    }

    internal void ClearForSessionRetire()
    {
        RetireDailyStyleEditor();
        dailyStyleSelected = [];
        dailyStyleSelectedCategory = "";
        dailyStyleNeedsRead = false;
        catalogTargets.Clear();
        catalogLoadedTarget = null;
        catalogAccountStamp = "";
    }

    internal void PersonalCatalogBindings()
    {
        foreach (string name in new[]
        {
            nameof(MusicCatalogController.IsCatalogPersonal), nameof(MusicCatalogController.IsCatalogDaily),
            nameof(MusicCatalogController.IsCatalogFm), nameof(MusicCatalogController.IsCatalogFmScene),
            nameof(MusicCatalogController.IsCatalogBatch), nameof(MusicCatalogController.IsCatalogCollection),
            nameof(MusicCatalogController.CanCatalogWy), nameof(MusicCatalogController.CanCatalogBatchRefresh),
            nameof(MusicCatalogController.CatalogAccountText), nameof(MusicCatalogController.CatalogPersonalKinds),
            nameof(MusicCatalogController.CatalogPersonalKind), nameof(MusicCatalogController.CatalogDailyBranch),
            nameof(MusicCatalogController.CatalogFmMode), nameof(MusicCatalogController.CatalogFmScene),
            nameof(MusicCatalogController.IsCatalogSalesChart), nameof(MusicCatalogController.CatalogChartHasPeriods),
            nameof(MusicCatalogController.CatalogChartIsYearly), nameof(MusicCatalogController.CatalogChartKind),
            nameof(MusicCatalogController.CatalogChartPeriod), nameof(MusicCatalogController.CatalogChartYear)
        })
            notify(name);
    }

    internal void DailyStyleBindings()
    {
        notify(nameof(MusicCatalogController.IsDailyStyle));
        notify(nameof(MusicCatalogController.CanEditDailyStyle));
        notify(nameof(MusicCatalogController.DailyOtherBranch));
        notify(nameof(MusicCatalogController.DailyOtherBranchTitle));
        notify(nameof(MusicCatalogController.CanConfirmDailyStyle));
        notify(nameof(MusicCatalogController.DailyStyleSelectionText));
    }

    internal void PublishPersonalCatalog(CatalogPageState state)
    {
        catalogLoadedTarget = state.Target; catalogAccountStamp = state.AccountStamp;
        if (state.Target is not { } target) return;
        if (target.Operation == "personalPlaylists") CatalogPersonalKind = target.Params.Kind ?? "";
        if (target.Operation == "chart")
        {
            CatalogChartKind = target.Params.Kind ?? "";
            CatalogChartPeriod = target.Params.Period ?? "";
            CatalogChartYear = target.Params.Year ?? DateTime.UtcNow.Year;
        }
        if (target.Operation == "daily") CatalogDailyBranch = target.Params.Branch ?? "";
        if (target.Operation == "fm")
        {
            CatalogFmMode = target.Params.Mode ?? "";
            if (target.Params.Submode is { Length: > 0 } scene) CatalogFmScene = scene;
        }
    }

    internal CatalogTarget? CatalogRouteTarget(string route, string platform)
    {
        if (route == "catalog-personal") return MusicCatalogDispatcher.Target("personalPlaylists", platform, "平台歌单", new(Kind: CatalogPersonalKind));
        if (route == "catalog-daily") return MusicCatalogDispatcher.Target("daily", platform, CatalogDailyBranch == "style" ? "风格推荐" : "每日推荐", new(Branch: CatalogDailyBranch));
        if (route == "catalog-fm") return MusicCatalogDispatcher.Target("fm", platform, "私人 FM", new(Mode: CatalogFmMode, Submode: CatalogFmMode == "scene" ? CatalogFmScene : ""));
        if (catalogTargets.TryGetValue(route, out var target)) return target;
        if (route == ctx.CurrentRoute && catalogLoadedTarget is not null) return catalogLoadedTarget;
        var parts = route.Split('/', 3);
        if (parts.Length == 3 && parts[0] == "catalog-chart")
            return MusicCatalogDispatcher.Target("chart", parts[1], "排行榜", new(Kind: Uri.UnescapeDataString(parts[2]), Period: "daily"));
        if (parts.Length == 3 && parts[0] == "catalog-album")
            return MusicCatalogDispatcher.Target("album", parts[1], "专辑", new(Id: Uri.UnescapeDataString(parts[2])));
        if (parts.Length == 3 && parts[0] == "catalog-artist")
            return MusicCatalogDispatcher.Target("artist", parts[1], "歌手", new(Id: Uri.UnescapeDataString(parts[2])));
        if (parts.Length == 3 && parts[0] == "catalog-daily")
            return MusicCatalogDispatcher.Target("daily", parts[1], parts[2] == "style" ? "风格推荐" : "每日推荐", new(Branch: parts[2]));
        return null;
    }

    internal bool IsUnsupportedCatalogRoute(string route)
    {
        if (!MusicCatalogDispatcher.IsCatalogRoute(route)) return false;
        var platform = route.Split('/', 3) is { Length: 3 } parts ? parts[1] : SelectedCatalogPlatform.Key;
        return CatalogRouteTarget(route, platform) is { Operation: not ("personalPlaylists" or "playlist"), Platform: not "wy" } target &&
            CatalogPlatforms.Any(item => item.Key == target.Platform);
    }

    internal string RegisterCatalogTarget(CatalogTarget target, Action<string> retireLocation, string? id = null)
    {
        var key = target.Params.CanonicalKey();
        id ??= Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        var route = "catalog-" + target.Operation + "/" + target.Platform + "/" + Uri.EscapeDataString(id);
        if (!catalogTargets.TryGetValue(route, out var previous) || previous.Params.CanonicalKey() != key)
            retireLocation(route + "/" + target.Platform);
        catalogTargets.Remove(route); catalogTargets[route] = target;
        while (catalogTargets.Count > 100) catalogTargets.Remove(catalogTargets.Keys.First());
        return route;
    }

    internal Task OpenCatalogTargetAsync(CatalogTarget target, Action rememberLocation, Action<string> retireLocation, string? id = null)
    {
        rememberLocation();
        return ctx.RequestNavigationAsync(RegisterCatalogTarget(target, retireLocation, id));
    }

    internal Task OpenPersonalCatalogCardAsync(MusicCatalogCard card, bool isCatalogHome, Action rememberLocation, Action<string> retireLocation)
    {
        var action = card.Action;
        if (card.Kind == "playlist" && card.Locator is { } locator)
            return OpenCatalogTargetAsync(MusicCatalogDispatcher.Target("playlist", card.Platform, card.Title,
                new(Id: card.ActionId, Personal: locator.Clone())) with { ManagedPlaylist = card.IsManagedPlaylist, Artwork = card.Cover, Subtitle = card.Subtitle, Count = card.Count }, rememberLocation, retireLocation, card.ActionId);
        if (card.Kind == "chart")
            return OpenCatalogTargetAsync(MusicCatalogDispatcher.Target("chart", card.Platform, card.Title,
                new(Kind: card.ActionId, Period: "daily")) with
            {
                Artwork = card.Cover.Length > 0 ? card.Cover : card.FirstPreviewCover,
                Subtitle = card.Subtitle,
                Count = card.Count
            }, rememberLocation, retireLocation, card.ActionId);
        if (card.Kind == "album")
            return OpenCatalogTargetAsync(MusicCatalogDispatcher.Target("album", card.Platform, card.Title, new(Id: card.ActionId)) with
            {
                Artwork = card.Cover,
                Subtitle = card.Subtitle,
                Count = card.Count
            }, rememberLocation, retireLocation, card.ActionId);
        if (card.Kind == "daily")
        {
            var branch = action is { } a ? MusicCatalogDto.Text(a, "branch") : "";
            if (branch.Length == 0) branch = "default";
            return OpenCatalogTargetAsync(MusicCatalogDispatcher.Target("daily", card.Platform, card.Title, new(Branch: branch))
                with { Artwork = card.Cover, Subtitle = card.Subtitle, Count = card.Count }, rememberLocation, retireLocation, branch);
        }
        if (card.Kind == "similar")
        {
            if (action is not { } simAction || !simAction.TryGetProperty("sourceIds", out var ids) || ids.ValueKind != JsonValueKind.Array)
            {
                ctx.SetStatus(MusicCatalogDto.ErrorText("INVALID_REQUEST"));
                return Task.CompletedTask;
            }
            var sourceIds = ids.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetRawText()).ToArray();
            return queue.StartRecommendation(MusicCatalogDispatcher.Target("similar", card.Platform, card.Title,
                new(SourceIds: sourceIds, SourceType: MusicCatalogDto.Text(simAction, "sourceType"))), card);
        }
        if (card.Kind == "heartbeat")
            return queue.StartRecommendation(MusicCatalogDispatcher.Target("heartbeat", card.Platform, card.Title,
                new(Entry: "favorites", SongId: action is { } hb ? MusicCatalogDto.Text(hb, "songId") : "")), card);
        if (card.Kind == "fm")
        {
            string ruleId = action is { } fa ? MusicCatalogDto.Text(fa, "ruleId") : "";
            string tabTagId = action is { } fb ? MusicCatalogDto.Text(fb, "tabTagId") : "";
            if (isCatalogHome && ruleId.Length == 0 && tabTagId.Length == 0)
                return queue.StartRecommendation(MusicCatalogDispatcher.Target("fm", card.Platform, card.Title), card);
            var mode = (action is { } fc ? MusicCatalogDto.Text(fc, "mode") : "").ToLowerInvariant();
            mode = mode switch { "" => "default", "scene_rcmd" => "scene", "puzzle_mode_rcmd" => "puzzle", _ => mode };
            var submode = action is { } fd ? MusicCatalogDto.Text(fd, "submode") : "";
            return queue.StartRecommendation(MusicCatalogDispatcher.Target("fm", card.Platform, card.Title,
                new(Mode: mode, Submode: submode, RuleId: ruleId.Length > 0 ? ruleId : null, TabTagId: tabTagId.Length > 0 ? tabTagId : null)), card);
        }
        return Task.CompletedTask;
    }

    internal Task ChangeCatalogChartKindAsync(string kind, Action rememberLocation, Action<string> retireLocation)
    {
        if (ctx.Publishing || !CanCatalogBrowse || !IsCatalogSalesChart || kind == CatalogChartKind ||
            !CatalogChartKinds.Any(choice => choice.Key == kind)) return Task.CompletedTask;
        CatalogChartKind = kind;
        return ChangeCatalogChartAsync(rememberLocation, retireLocation);
    }

    internal Task ChangeCatalogChartPeriodAsync(string period, Action rememberLocation, Action<string> retireLocation)
    {
        if (ctx.Publishing || !CanCatalogBrowse || !IsCatalogSalesChart || !CatalogChartHasPeriods ||
            period == CatalogChartPeriod || !CatalogChartPeriods.Any(choice => choice.Key == period)) return Task.CompletedTask;
        CatalogChartPeriod = period;
        return ChangeCatalogChartAsync(rememberLocation, retireLocation);
    }

    internal Task ChangeCatalogChartYearAsync(int year, Action rememberLocation, Action<string> retireLocation)
    {
        if (ctx.Publishing || !CanCatalogBrowse || !IsCatalogSalesChart || !CatalogChartIsYearly ||
            year == CatalogChartYear) return Task.CompletedTask;
        CatalogChartYear = year;
        return ChangeCatalogChartAsync(rememberLocation, retireLocation);
    }

    internal Task ChangeCatalogChartAsync(Action rememberLocation, Action<string> retireLocation)
    {
        if (ctx.Publishing || !CanCatalogBrowse || !IsCatalogSalesChart ||
            !CatalogChartKinds.Any(choice => choice.Key == CatalogChartKind) ||
            !CatalogChartPeriods.Any(choice => choice.Key == CatalogChartPeriod)) return Task.CompletedTask;
        var parameters = new CatalogTargetParams(
            Kind: CatalogChartKind,
            Period: CatalogChartKind == "song-sales" ? "daily" : CatalogChartPeriod,
            Year: CatalogChartKind != "song-sales" && CatalogChartPeriod == "yearly" ? CatalogChartYear : null);
        if (parameters.CanonicalKey() == catalogLoadedTarget!.Params.CanonicalKey())
            return Task.CompletedTask;
        PersonalCatalogBindings();
        return OpenCatalogTargetAsync(
            MusicCatalogDispatcher.Target("chart", "wy", "排行榜", parameters) with { Artwork = catalogLoadedTarget?.Artwork ?? "" },
            rememberLocation, retireLocation, CatalogChartKind);
    }

    internal Task CatalogOptionsChangedAsync(Func<string, Task> loadRouteAsync)
    {
        if (ctx.Publishing || !CanCatalogBrowse) return Task.CompletedTask;
        string? route = IsCatalogPersonal ? "catalog-personal" : IsCatalogDaily ? "catalog-daily" : IsCatalogFm ? "catalog-fm" : null;
        if (route is not null && IsUnsupportedCatalogRoute(route)) return Task.CompletedTask;
        PersonalCatalogBindings();
        if (IsCatalogFm && queue.HasPlaybackFm &&
            CatalogRouteTarget("catalog-fm", SelectedCatalogPlatform.Key) is { } activeFm)
            return queue.StartRecommendation(activeFm);
        return route is null ? Task.CompletedTask : loadRouteAsync(route);
    }

    internal Task ChangeCatalogPersonalKindAsync(MusicCatalogChoice choice, Func<string, Task> loadRouteAsync)
    {
        if (ctx.Publishing || !CanCatalogBrowse || !IsCatalogPersonal || choice.Key == CatalogPersonalKind ||
            !CatalogPersonalKinds.Contains(choice)) return Task.CompletedTask;
        CatalogPersonalKind = choice.Key;
        return CatalogOptionsChangedAsync(loadRouteAsync);
    }

    // Daily style editor methods
    private void RebuildDailyStyleGroups()
    {
        dailyStyleGroups = dailyStyleEditor?.Categories.Select(category =>
            new MusicCatalogTagGroup(category.Name, category.Tags.Select(tag => new MusicCatalogTag(tag.Id, tag.Name,
                category.Id, category.Id == dailyStyleCategory && dailyStyleDraft.Contains(tag.Id))).ToArray())).ToArray() ?? [];
        notify(nameof(MusicCatalogController.DailyStyleGroups));
    }

    internal void RetireDailyStyleEditor()
    {
        bool wasOpen = IsDailyStyleEditorOpen;
        bool hadGroups = dailyStyleGroups.Count > 0;
        ++dailyStyleRevision; dailyStyleEditor = null; dailyStyleDraft.Clear(); dailyStyleAccount = "";
        if (hadGroups) { dailyStyleGroups = []; notify(nameof(MusicCatalogController.DailyStyleGroups)); }
        if (wasOpen) notify(nameof(MusicCatalogController.IsDailyStyleEditorOpen));
        DailyStyleBindings();
    }

    internal void PublishDailyStyleSelection(JsonElement reply)
    {
        dailyStyleNeedsRead = false;
        dailyStyleSelectedCategory = MusicCatalogDto.Text(reply, "category");
        dailyStyleSelected = MusicCatalogDto.Rows(MusicCatalogDto.Field(reply, "tags"))
            .Select(tag => new MusicDailyStyleTag(MusicCatalogDto.Text(tag, "id"), MusicCatalogDto.Text(tag, "name"))).ToArray();
        DailyStyleBindings();
    }

    internal Task SwitchDailyBranchAsync(string branch, Action rememberLocation, Action<string> retireLocation)
    {
        if (!IsCatalogDaily || !CanCatalogBrowse || dailyStyleBusy || branch is not ("default" or "style")) return Task.CompletedTask;
        RetireDailyStyleEditor();
        return OpenCatalogTargetAsync(MusicCatalogDispatcher.Target("daily", "wy", branch == "style" ? "风格推荐" : "每日推荐", new(Branch: branch)),
            rememberLocation, retireLocation, branch);
    }

    internal async Task OpenDailyStyleEditorAsync()
    {
        if (!CanEditDailyStyle) return;
        long revision = ++dailyStyleRevision; string account = CatalogAccountKey("wy");
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.LoadDrains.Add(drained.Task);
        dailyStyleBusy = true; DailyStyleBindings();
        try
        {
            var editor = await ctx.Application.OpenDailyStyleEditorAsync(ctx.Lifetime.Token);
            if (revision != dailyStyleRevision || !IsDailyStyle || account != CatalogAccountKey("wy")) return;
            bool wasOpen = IsDailyStyleEditorOpen;
            dailyStyleEditor = editor; dailyStyleAccount = account; dailyStyleCategory = dailyStyleSelectedCategory;
            dailyStyleDraft.Clear(); foreach (var tag in dailyStyleSelected) dailyStyleDraft.Add(tag.Id);
            RebuildDailyStyleGroups();
            if (!wasOpen) notify(nameof(MusicCatalogController.IsDailyStyleEditorOpen));
        }
        catch (Exception error) { if (revision == dailyStyleRevision) ctx.SetStatus(DailyStyleError(error)); }
        finally { ctx.LoadDrains.Remove(drained.Task); drained.TrySetResult(); dailyStyleBusy = false; DailyStyleBindings(); }
    }

    internal void ToggleDailyStyleTag(MusicCatalogTag tag)
    {
        if (!CanEditDailyStyle || dailyStyleEditor?.Categories.Any(c => c.Id == tag.Group && c.Tags.Any(t => t.Id == tag.Id)) != true) return;
        if (dailyStyleCategory != tag.Group) { dailyStyleCategory = tag.Group; dailyStyleDraft.Clear(); }
        if (!dailyStyleDraft.Add(tag.Id)) dailyStyleDraft.Remove(tag.Id);
        RebuildDailyStyleGroups();
        DailyStyleBindings();
    }

    internal async Task ConfirmDailyStyleAsync(
        Func<CatalogPageState?> getCurrentPage,
        Action retireCatalogLoad,
        Action<string> retireSavedAccountSnapshots,
        Action clearCurrentDailyPage,
        Func<CatalogPageState, Func<bool>, Task<bool>> publishDailyPageAsync,
        Action catalogBindings)
    {
        if (!CanConfirmDailyStyle || dailyStyleEditor is not { } editor) return;
        if (dailyStyleAccount != CatalogAccountKey("wy")) { RetireDailyStyleEditor(); return; }
        string saveAccount = dailyStyleAccount;
        string receiptKey = "music.platform.dailyStyle:wy:" + editor.UserId;
        long submission = ctx.Receipts.NextSubmission();
        long generation = ctx.LoadGeneration;
        bool CanPublishSave() => !ctx.Stopping && generation == ctx.LoadGeneration && IsDailyStyle && saveAccount == CatalogAccountKey("wy");
        void SettleSavePage()
        {
            retireSavedAccountSnapshots(saveAccount);
            if (!CanPublishSave()) return;
            catalogAccountStamp = "";
            clearCurrentDailyPage();
            dailyStyleNeedsRead = true;
            contentState.ClearSelectionOnly();
            ctx.RefreshBindings();
        }
        var previousPage = getCurrentPage();
        var selection = new MusicDailyStyleSelection(dailyStyleCategory, dailyStyleDraft.ToArray());
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.LoadDrains.Add(drained.Task);
        dailyStyleBusy = true; dailyStyleNeedsRead = true; DailyStyleBindings();
        try
        {
            retireCatalogLoad();
            generation = ctx.LoadGeneration;
            catalogActions.Invalidate();
            retireSavedAccountSnapshots(saveAccount);
            catalogAccountStamp = "";
            clearCurrentDailyPage();
            contentState.ClearSelectionOnly();
            ctx.RefreshBindings();
            var result = await ctx.Application.SaveDailyStyleAsync(editor, selection, ctx.Lifetime.Token);
            if (result.Status is MusicDailyStyleWriteStatus.Confirmed or MusicDailyStyleWriteStatus.Unknown)
                ctx.Receipts.Record(receiptKey, "风格偏好", submission, result.Status == MusicDailyStyleWriteStatus.Confirmed);
            SettleSavePage();
            if (!CanPublishSave()) return;
            if (result.Readback is null && result.Status is (MusicDailyStyleWriteStatus.NotSent or MusicDailyStyleWriteStatus.Rejected) && previousPage is not null)
            {
                if (!await publishDailyPageAsync(previousPage, CanPublishSave)) return;
            }
            if (result.Readback is { } reply)
            {
                var sections = MusicCatalogDto.Read(reply, "wy", "daily", "风格推荐", "songs").ToArray();
                var state = new CatalogPageState(sections, [], "", "风格推荐", MusicCatalogDto.Text(reply, "cover"), false, null,
                    sections.Sum(s => s.Items.Count), Target: catalogLoadedTarget, AccountStamp: saveAccount,
                    ExpiresAt: MusicCatalogDispatcher.CatalogExpiry(reply),
                    DailySelection: MusicCatalogDispatcher.DailySelectionSnapshot(reply));
                if (!await publishDailyPageAsync(state, CanPublishSave)) return;
                if (result.Status == MusicDailyStyleWriteStatus.Confirmed && result.SelectionMatches == true) RetireDailyStyleEditor();
            }
            if (result.Status == MusicDailyStyleWriteStatus.Unknown || result.Status == MusicDailyStyleWriteStatus.Confirmed && result.Readback is null)
            {
                catalogAccountStamp = "";
                clearCurrentDailyPage();
                contentState.ClearSelectionOnly();
                ctx.RefreshBindings();
            }
            dailyStyleNeedsRead = result.Status == MusicDailyStyleWriteStatus.Unknown ||
                result.Status == MusicDailyStyleWriteStatus.Confirmed && result.Readback is null;
            if (result.Status != MusicDailyStyleWriteStatus.Confirmed || result.SelectionMatches != true || result.Readback is null)
                ctx.SetStatus(result.ErrorCode is { } code ? MusicCatalogDto.ErrorText(code) : "风格偏好尚未确认，请重新读取");
        }
        catch (Exception error)
        {
            if (error is MusicPlatformException { Code: "COMMIT_UNKNOWN" })
                ctx.Receipts.Record(receiptKey, "风格偏好", submission, confirmed: false);
            SettleSavePage();
            if (CanPublishSave()) { dailyStyleNeedsRead = true; ctx.SetStatus(DailyStyleError(error)); }
        }
        finally { ctx.LoadDrains.Remove(drained.Task); drained.TrySetResult(); dailyStyleBusy = false; catalogBindings(); }
    }

    private static string DailyStyleError(Exception error) => error is MusicPlatformException platform
        ? MusicCatalogDto.ErrorText(platform.Code) : MusicRuntimeContext.ErrorText(error);
}
