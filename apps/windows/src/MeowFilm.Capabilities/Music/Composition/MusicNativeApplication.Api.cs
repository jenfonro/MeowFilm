using System.Text.Json;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Capabilities.Library;
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Capabilities.Recommendations;
using MeowFilm.Capabilities.Scripting;

namespace MeowFilm.Capabilities;

/// <summary>Strongly-typed C# business facade on <see cref="MusicNativeApplication"/> (zero in-process JSON-RPC).</summary>
public sealed partial class MusicNativeApplication
{
    // ---- Accounts ----

    private Task<T> RunAccountAsync<T>(Func<CancellationToken, Task<T>> action, bool clearsReceipt, CancellationToken cancellationToken) =>
        RunAdmittedAsync(async token =>
        {
            long receiptEpoch;
            lock (gate) receiptEpoch = accountReceiptEpoch;
            try
            {
                var result = await action(token).ConfigureAwait(false);
                if (clearsReceipt)
                {
                    lock (gate)
                    {
                        if (receiptEpoch == accountReceiptEpoch) accountReceiptUnknown = false;
                    }
                }
                return result;
            }
            catch (MusicAccountException error) when (error.Code is "COMMIT_UNKNOWN" or "HANDOFF_UNCONFIRMED")
            {
                lock (gate) { accountReceiptUnknown = true; accountReceiptEpoch++; }
                throw;
            }
        }, cancellationToken);

    public Task<MusicAccountListResult> ListAccountsAsync(CancellationToken ct = default) =>
        RunAccountAsync(token => accounts.ListAccountsAsync(token), clearsReceipt: true, ct);
    public Task<MusicAccountStatusRecord> LogoutAccountAsync(string platform, CancellationToken ct = default) =>
        RunAccountAsync(token => accounts.LogoutAccountAsync(platform, token), clearsReceipt: false, ct);
    public Task CancelAccountAsync(string platform, CancellationToken ct = default) =>
        RunAccountAsync(async token => { await accounts.CancelAccountAsync(platform, token).ConfigureAwait(false); return true; }, clearsReceipt: false, ct);
    public Task<MusicAccountQrChallenge> BeginAccountQrAsync(string platform, CancellationToken ct = default) =>
        RunAccountAsync(token => accounts.BeginQrAsync(platform, token), clearsReceipt: false, ct);
    public Task<MusicAccountQrPollResult> PollAccountQrAsync(string platform, string challengeId, CancellationToken ct = default) =>
        RunAccountAsync(token => accounts.PollQrAsync(platform, challengeId, token), clearsReceipt: false, ct);
    public Task<MusicAccountSmsSendResult> SendAccountSmsAsync(string platform, string phone, string countryCode = "86", CancellationToken ct = default) =>
        RunAccountAsync(token => accounts.SendSmsAsync(platform, phone, countryCode, token), clearsReceipt: false, ct);
    public Task<MusicAccountStatusRecord> LoginAccountSmsAsync(string platform, string phone, string countryCode, string captcha, CancellationToken ct = default) =>
        RunAccountAsync(token => accounts.LoginSmsAsync(platform, phone, countryCode, captcha, token), clearsReceipt: false, ct);

    // ---- Catalog ----

    private Task<T> RunCatalogAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken) =>
        RunAdmittedAsync(async token =>
        {
            using var frame = sessions.Begin();
            try { return await action(token).ConfigureAwait(false); }
            catch (MusicPlatformException error) when (error.Code is "AUTH_EXPIRED" or "AUTH_REQUIRED")
            {
                if (frame.Session is { } failed) accounts.Revoke(failed);
                throw new MusicOperationException("AUTH_REQUIRED");
            }
        }, cancellationToken);

    public Task<JsonElement> GetCatalogChorusAsync(string platform, string id, CancellationToken ct = default) =>
        RunCatalogAsync(token => nativeCatalog.ChorusAsync(platform, id, token), ct);
    public Task<JsonElement> GetCatalogPlaylistsBatchAsync(string platform, string[] ids, CancellationToken ct = default) =>
        RunCatalogAsync(token => nativeCatalog.PlaylistsAsync(platform, ids, token), ct);
    public Task<MusicSongRecord> GetCatalogSongAsync(string platform, string id, bool refresh = false, CancellationToken ct = default) =>
        RunCatalogAsync(token => nativeCatalog.SongAsync(platform, id, refresh, token), ct);
    public Task<JsonElement> GetCatalogPlaylistAsync(string platform, string id, int page = 1, int limit = 100,
        bool refresh = false, JsonElement? personal = null, JsonElement? continuation = null, CancellationToken ct = default) =>
        RunCatalogAsync(token => nativeCatalog.PlaylistAsync(platform, id, page, limit, refresh, personal, continuation, token), ct);
    public Task<JsonElement> GetCatalogHomeAsync(string platform, JsonElement? continuation = null, bool refresh = false, CancellationToken ct = default) =>
        RunCatalogAsync(token => nativeCatalog.HomeAsync(platform, continuation, refresh, token), ct);
    public Task<JsonElement> GetCatalogChannelsAsync(string platform, bool refresh = false, CancellationToken ct = default) =>
        RunCatalogAsync(token => nativeCatalog.ChannelsAsync(platform, refresh, token), ct);
    public Task<JsonElement> GetCatalogChannelAsync(string platform, string id, string? pageCode = null,
        JsonElement? continuation = null, string? tag = null, bool refresh = false, CancellationToken ct = default) =>
        RunCatalogAsync(token => nativeCatalog.ChannelAsync(platform, id, pageCode, continuation, tag, refresh, token), ct);
    public Task<JsonElement> GetCatalogPersonalPlaylistsAsync(string platform, string kind = "all", int page = 1, int limit = 30,
        JsonElement? continuation = null, bool refresh = false, CancellationToken ct = default) =>
        RunCatalogAsync(token => nativeCatalog.PersonalPlaylistsAsync(platform, kind, page, limit, continuation, refresh, token), ct);
    public Task<JsonElement> GetCatalogDailyAsync(string platform, string branch = "default", bool refresh = false, CancellationToken ct = default) =>
        RunCatalogAsync(token => nativeCatalog.DailyAsync(platform, branch, refresh, token), ct);
    public Task<JsonElement> GetCatalogFmAsync(string platform, string mode = "default", string submode = "", bool preview = false,
        string? ruleId = null, string? tabTagId = null, CancellationToken ct = default) =>
        RunCatalogAsync(token => nativeCatalog.FmAsync(platform, mode, submode, preview, ruleId, tabTagId, token), ct);
    public Task<JsonElement> GetCatalogSimilarAsync(string platform, string[] sourceIds, string sourceType, CancellationToken ct = default) =>
        RunCatalogAsync(token => nativeCatalog.SimilarAsync(platform, sourceIds, sourceType, token), ct);
    public Task<JsonElement> GetCatalogHeartbeatAsync(string platform, string entry, string? songId = null, string? playlistId = null, CancellationToken ct = default) =>
        RunCatalogAsync(token => nativeCatalog.HeartbeatAsync(platform, entry, songId, playlistId, token), ct);
    public Task<JsonElement> GetCatalogAlbumAsync(string platform, string id, bool refresh = false, CancellationToken ct = default) =>
        RunCatalogAsync(token => nativeCatalog.AlbumAsync(platform, id, refresh, token), ct);
    public Task<JsonElement> GetCatalogChartAsync(string platform, string kind, string period = "daily", int? year = null, bool refresh = false, CancellationToken ct = default) =>
        RunCatalogAsync(token => nativeCatalog.ChartAsync(platform, kind, period, year, refresh, token), ct);
    public Task<JsonElement> GetCatalogArtistAsync(string platform, string id, bool refresh = false, CancellationToken ct = default) =>
        RunCatalogAsync(token => nativeCatalog.ArtistAsync(platform, id, refresh, token), ct);
    public Task<PreparedCatalogPlaylist> PrepareCatalogPlaylistAsync(string platform, string id, bool refresh = false, JsonElement? personal = null, CancellationToken ct = default) =>
        RunCatalogAsync(token => nativeCatalog.PreparePlaylistAsync(platform, id, refresh, personal, token), ct);
    public Task<MusicNetworkPlaylistMutationResult> SaveCatalogPlaylistAsync(string platform, string id, string name, string? cover = null, long? count = null, CancellationToken ct = default) =>
        RunCatalogAsync(token => nativeCatalog.SavePlaylistAsync(platform, id, name, cover, count, token), ct);
    public Task<bool> DiscardCatalogPlaylistAsync(string token, CancellationToken ct = default) =>
        RunCatalogAsync(inner => nativeCatalog.DiscardPlaylistAsync(token, inner), ct);
    public Task<MusicQueuePositionResult> CommitCatalogPlaylistAsync(string token, string action, long expectedVersion, int index = 0,
        string? memberId = null, string? songId = null, int memberOccurrence = 0, CancellationToken ct = default) =>
        RunCatalogAsync(async inner =>
        {
            var result = await CommitCatalogPlaylistInRecommendationLaneAsync(
                token, action, expectedVersion, index, memberId, songId, memberOccurrence, inner).ConfigureAwait(false);
            await SaveSongMatchesAsync(CancellationToken.None).ConfigureAwait(false);
            return result;
        }, ct);

    // ---- Scripts & Online Playback ----

    private Task<T> RunSourceMutationAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct) =>
        RunAdmittedAsync(async token =>
        {
            lyrics.Invalidate();
            try { return await action(token).ConfigureAwait(false); }
            finally { lyrics.Invalidate(); }
        }, ct);

    public Task<MusicScriptSourceSnapshot> ListSourcesAsync(CancellationToken ct = default) =>
        RunAdmittedAsync(token => scripts.ListSourcesAsync(token), ct);
    public Task<MusicScriptSourceDetail> GetSourceDetailAsync(string installationId, CancellationToken ct = default) =>
        RunAdmittedAsync(token => scripts.GetSourceDetailAsync(installationId, token), ct);
    public Task<MusicScriptSourceDetail> DownloadSourceAsync(string url, CancellationToken ct = default) =>
        RunSourceMutationAsync(token => scripts.DownloadSourceAsync(url, token), ct);
    public Task<MusicScriptSourceDetail> EditSourceAsync(string installationId, string? name = null, string? url = null,
        bool includeUrl = false, CancellationToken ct = default) =>
        RunAdmittedAsync(token => scripts.EditSourceAsync(installationId, name, url, includeUrl, token), ct);
    public Task<MusicScriptSourceDetail> UpdateSourceAsync(string installationId, CancellationToken ct = default) =>
        RunSourceMutationAsync(token => scripts.UpdateSourceAsync(installationId, token), ct);
    public Task<MusicScriptSourceDetail> RollbackSourceAsync(string installationId, CancellationToken ct = default) =>
        RunSourceMutationAsync(token => scripts.RollbackSourceAsync(installationId, token), ct);
    public Task<MusicScriptSourceDetail> SetSourceEnabledAsync(string installationId, bool enabled, CancellationToken ct = default) =>
        RunSourceMutationAsync(token => scripts.SetSourceEnabledAsync(installationId, enabled, token), ct);
    public Task<bool> RemoveSourceAsync(string installationId, CancellationToken ct = default) =>
        RunSourceMutationAsync(token => scripts.RemoveSourceAsync(installationId, token), ct);
    public Task<MusicScriptCheckResult> CheckSourceAsync(string installationId, CancellationToken ct = default) =>
        RunAdmittedAsync(token => scripts.CheckSourceAsync(installationId, token), ct);
    public Task<MusicResolvedScriptAudio> ResolveOnlineAudioAsync(string installationId, string platform, MusicSongRecord song, string quality,
        string? sourceKey = null, bool manualSource = false, string? qualityPolicySignature = null,
        string? qualityCacheSignature = null, CancellationToken ct = default) =>
        RunAdmittedAsync(token => scripts.ResolveOnlineAsync(
            installationId, platform, song, quality, sourceKey, manualSource, qualityPolicySignature, qualityCacheSignature, token), ct);

    // ---- Lyrics ----

    public Task<MusicLyricLookupResult> LookupLyricsAsync(MusicSongRecord song, string? sourceKey = null, CancellationToken ct = default) =>
        RunAdmittedAsync(async token =>
        {
            using var frame = sessions.Begin();
            try { return await lyrics.LookupAsync(song, sourceKey, token).ConfigureAwait(false); }
            catch (MusicPlatformException error) when (error.Code is "AUTH_EXPIRED" or "AUTH_REQUIRED")
            {
                if (frame.Session is { } failed) accounts.Revoke(failed);
                throw;
            }
        }, ct);

    public Task<MusicLyricLookupResult> GetLocalLyricsAsync(MusicSongRecord song, CancellationToken ct = default) =>
        RunAdmittedAsync(token => recommendationQueue.InLaneAsync((owner, inner) => owner.GetLocalLyricsAsync(song, inner), token), ct);

    public Task<bool> SaveLocalLyricsAsync(MusicSongRecord song, string text, CancellationToken ct = default) =>
        RunAdmittedAsync(async token =>
        {
            lyrics.Invalidate();
            try { return await recommendationQueue.InLaneAsync((owner, inner) => owner.SaveLocalLyricsAsync(song, text, inner), token).ConfigureAwait(false); }
            finally { lyrics.Invalidate(); }
        }, ct);

    public Task<bool> RemoveLocalLyricsAsync(MusicSongRecord song, CancellationToken ct = default) =>
        RunAdmittedAsync(async token =>
        {
            lyrics.Invalidate();
            try { return await recommendationQueue.InLaneAsync((owner, inner) => owner.RemoveLocalLyricsAsync(song, inner), token).ConfigureAwait(false); }
            finally { lyrics.Invalidate(); }
        }, ct);

    public Task<MusicLyricLookupResult> GetDisplayLyricsAsync(MusicSongRecord song, string? sourceKey = null, CancellationToken ct = default) =>
        RunAdmittedAsync(token => library.GetDisplayLyricsAsync(song, sourceKey, token), ct);

    public Task<bool> SaveDisplayLyricsAsync(MusicSongRecord song, string sourceKey, IReadOnlyList<MusicLyricLineRecord> lines, CancellationToken ct = default) =>
        RunAdmittedAsync(token => library.SaveDisplayLyricsAsync(song, sourceKey, lines, token), ct);

    // ---- Settings & Source Exclusions ----

    public Task<MusicOnlineSettingsRecord> GetOnlineSettingsAsync(CancellationToken ct = default) =>
        RunAdmittedAsync(token => recommendationQueue.InLaneAsync((owner, inner) => owner.GetOnlineSettingsAsync(inner), token), ct);

    public Task<MusicMetadataPolicyRecord> GetMetadataPolicyAsync(CancellationToken ct = default) =>
        RunAdmittedAsync(token => recommendationQueue.InLaneAsync((owner, inner) => owner.GetMetadataPolicyAsync(inner), token), ct);

    public Task<MusicMetadataPolicyRecord> SaveMetadataPolicyAsync(long ifRevision, string[] platforms, string[] enabled, CancellationToken ct = default) =>
        RunAdmittedAsync(async token =>
        {
            lyrics.Invalidate();
            try { return await recommendationQueue.InLaneAsync((owner, inner) => owner.SaveMetadataPolicyAsync(ifRevision, platforms, enabled, inner), token).ConfigureAwait(false); }
            finally { lyrics.Invalidate(); }
        }, ct);

    public Task<MusicAppearanceRecord> GetAppearanceAsync(CancellationToken ct = default) =>
        RunAdmittedAsync(token => recommendationQueue.InLaneAsync((owner, inner) => owner.GetAppearanceAsync(inner), token), ct);

    public Task<MusicAppearanceRecord> SaveAppearanceAsync(long ifRevision, string playlistView, string playerTheme,
        string colorMode, string colorPalette, CancellationToken ct = default) =>
        RunAdmittedAsync(token => recommendationQueue.InLaneAsync((owner, inner) =>
            owner.SaveAppearanceAsync(ifRevision, playlistView, playerTheme, colorMode, colorPalette, inner), token), ct);

    public Task<MusicCatalogSourceRecord> GetCatalogSourceAsync(CancellationToken ct = default) =>
        RunAdmittedAsync(token => recommendationQueue.InLaneAsync((owner, inner) => owner.GetCatalogSourceAsync(inner), token), ct);

    public Task<MusicCatalogSourceRecord> SaveCatalogSourceAsync(long ifRevision, string platform, CancellationToken ct = default) =>
        RunAdmittedAsync(token => recommendationQueue.InLaneAsync((owner, inner) => owner.SaveCatalogSourceAsync(ifRevision, platform, inner), token), ct);

    public Task<MusicDeviceSettingsRecord> GetDeviceSettingsAsync(CancellationToken ct = default) =>
        RunAdmittedAsync(token => recommendationQueue.InLaneAsync((owner, inner) => owner.GetDeviceSettingsAsync(inner), token), ct);

    public Task<MusicDeviceSettingsRecord> SaveDeviceSettingsAsync(long ifRevision, int volume, CancellationToken ct = default) =>
        RunAdmittedAsync(token => recommendationQueue.InLaneAsync((owner, inner) => owner.SaveDeviceSettingsAsync(ifRevision, volume, inner), token), ct);

    public Task<MusicDesktopLyricsRecord> GetDesktopLyricsAsync(CancellationToken ct = default) =>
        RunAdmittedAsync(token => recommendationQueue.InLaneAsync((owner, inner) => owner.GetDesktopLyricsAsync(inner), token), ct);

    public Task<MusicDesktopLyricsRecord> SaveDesktopLyricsAsync(long ifRevision, string layout, string normalColor,
        string highlightColor, MusicDesktopLyricsGeometryRecord? geometry = null, bool includeGeometry = false, CancellationToken ct = default) =>
        RunAdmittedAsync(token => recommendationQueue.InLaneAsync((owner, inner) =>
            owner.SaveDesktopLyricsAsync(ifRevision, layout, normalColor, highlightColor, geometry, includeGeometry, inner), token), ct);

    public Task<MusicOfficialSourcesRecord> GetOfficialSourcesAsync(CancellationToken ct = default) =>
        RunAdmittedAsync(token => recommendationQueue.InLaneAsync((owner, inner) => owner.GetOfficialSourcesAsync(inner), token), ct);

    public Task<MusicOfficialSourcesRecord> SaveOfficialSourcesAsync(long ifRevision, string[] enabled, CancellationToken ct = default) =>
        RunAdmittedAsync(token => recommendationQueue.InLaneAsync((owner, inner) => owner.SaveOfficialSourcesAsync(ifRevision, enabled, inner), token), ct);

    public Task<MusicQualitySettingsResult> GetQualityPolicyAsync(CancellationToken ct = default) =>
        RunAdmittedAsync(token => recommendationQueue.InLaneAsync((owner, inner) => owner.GetQualityPolicyAsync(inner), token), ct);

    public Task<MusicQualitySettingsResult> SaveQualityPolicyAsync(long ifRevision, string request, string[] qualityOrder,
        string[] qualityEnabled, string[] spatialOrder, string[] spatialEnabled, CancellationToken ct = default) =>
        RunAdmittedAsync(token => recommendationQueue.InLaneAsync((owner, inner) =>
            owner.SaveQualityPolicyAsync(ifRevision, request, qualityOrder, qualityEnabled, spatialOrder, spatialEnabled, inner), token), ct);

    public Task<MusicSourceExclusionListResult> ListSourceExclusionsAsync(string? songId = null,
        MusicSourceExclusionTier? tier = null, CancellationToken ct = default) =>
        RunAdmittedAsync(token => recommendationQueue.InLaneAsync((owner, inner) => owner.ListSourceExclusionsAsync(songId, tier, inner), token), ct);

    public Task<MusicSourceExclusionChangeResult> AddSourceExclusionAsync(string songId, string provider, string platform,
        string providerName, string title, string artist, CancellationToken ct = default) =>
        RunAdmittedAsync(token => recommendationQueue.InLaneAsync((owner, inner) =>
            owner.AddSourceExclusionAsync(songId, provider, platform, providerName, title, artist, inner), token), ct);

    public Task<MusicSourceExclusionChangeResult> RemoveSourceExclusionAsync(string songId, string provider, string platform,
        CancellationToken ct = default) =>
        RunAdmittedAsync(token => recommendationQueue.InLaneAsync((owner, inner) =>
            owner.RemoveSourceExclusionAsync(songId, provider, platform, inner), token), ct);

    public Task<MusicSourceExclusionChangeResult> AddRecordingExclusionAsync(string songId, string sourceKey, string platform,
        string providerName, string title, string artist, CancellationToken ct = default) =>
        RunAdmittedAsync(token => recommendationQueue.InLaneAsync((owner, inner) =>
            owner.AddRecordingExclusionAsync(songId, sourceKey, platform, providerName, title, artist, inner), token), ct);

    public Task<MusicSourceExclusionChangeResult> RemoveRecordingExclusionAsync(string songId, string sourceKey, string platform,
        CancellationToken ct = default) =>
        RunAdmittedAsync(token => recommendationQueue.InLaneAsync((owner, inner) =>
            owner.RemoveRecordingExclusionAsync(songId, sourceKey, platform, inner), token), ct);

    // ---- Library Collections & Network Playlists ----

    public Task<MusicCollectionsPage> ListCollectionsAsync(long offset = 0, int limit = 50, CancellationToken ct = default) =>
        RunAdmittedAsync(token => recommendationQueue.InLaneAsync((owner, inner) => owner.ListCollectionsAsync(offset, limit, inner), token), ct);

    public Task<MusicCollectionDetail> GetCollectionDetailAsync(string id, long offset = 0, int limit = 50, CancellationToken ct = default) =>
        RunAdmittedAsync(token => recommendationQueue.InLaneAsync((owner, inner) => owner.GetCollectionDetailAsync(id, offset, limit, inner), token), ct);

    public Task<MusicCollectionMembership> GetCollectionMembershipAsync(string id, MusicSongRecord song, CancellationToken ct = default) =>
        RunAdmittedAsync(token => recommendationQueue.InLaneAsync((owner, inner) => owner.GetCollectionMembershipAsync(id, song, inner), token), ct);

    public Task<MusicLibrarySearchPage> SearchLibraryAsync(string query, long offset = 0, int limit = 50, CancellationToken ct = default) =>
        RunAdmittedAsync(token => recommendationQueue.InLaneAsync((owner, inner) => owner.SearchLibraryAsync(query, offset, limit, inner), token), ct);

    public Task<MusicCollectionSummaryRecord> CreatePlaylistAsync(string name, IReadOnlyList<MusicSongRecord>? songs = null, CancellationToken ct = default) =>
        RunAdmittedAsync(async token =>
        {
            var reply = await recommendationQueue.InLaneAsync((owner, inner) => owner.CreatePlaylistAsync(name, songs, inner), token).ConfigureAwait(false);
            await SaveSongMatchesAsync(CancellationToken.None).ConfigureAwait(false);
            return reply;
        }, ct);

    public Task<bool> RemoveCollectionAsync(string id, long ifRevision, CancellationToken ct = default) =>
        RunAdmittedAsync(async token =>
        {
            var reply = await recommendationQueue.InLaneAsync((owner, inner) => owner.RemoveCollectionAsync(id, ifRevision, inner), token).ConfigureAwait(false);
            await SaveSongMatchesAsync(CancellationToken.None).ConfigureAwait(false);
            return reply;
        }, ct);

    public Task<MusicCollectionSummaryRecord> EditCollectionAsync(string id, long ifRevision, string? name = null,
        IReadOnlyList<MusicSongRecord>? songs = null, string[]? order = null, CancellationToken ct = default) =>
        RunAdmittedAsync(async token =>
        {
            var reply = await recommendationQueue.InLaneAsync((owner, inner) => owner.EditCollectionAsync(id, ifRevision, name, songs, order, inner), token).ConfigureAwait(false);
            await SaveSongMatchesAsync(CancellationToken.None).ConfigureAwait(false);
            return reply;
        }, ct);

    public Task<MusicCollectionSummaryRecord> AddSongsToCollectionAsync(string id, long ifRevision, IReadOnlyList<MusicSongRecord> songs, CancellationToken ct = default) =>
        RunAdmittedAsync(async token =>
        {
            var reply = await recommendationQueue.InLaneAsync((owner, inner) => owner.AddSongsToCollectionAsync(id, ifRevision, songs, inner), token).ConfigureAwait(false);
            await SaveSongMatchesAsync(CancellationToken.None).ConfigureAwait(false);
            return reply;
        }, ct);

    public Task<MusicCollectionSummaryRecord> RemoveSongFromCollectionAsync(string id, long ifRevision, string songId, CancellationToken ct = default) =>
        RunAdmittedAsync(async token =>
        {
            var reply = await recommendationQueue.InLaneAsync((owner, inner) => owner.RemoveSongFromCollectionAsync(id, ifRevision, songId, inner), token).ConfigureAwait(false);
            await SaveSongMatchesAsync(CancellationToken.None).ConfigureAwait(false);
            return reply;
        }, ct);

    public Task<MusicCollectionSummaryRecord> ReorderSongInCollectionAsync(string id, long ifRevision, string songId, string targetId, bool after, CancellationToken ct = default) =>
        RunAdmittedAsync(token => recommendationQueue.InLaneAsync((owner, inner) =>
            owner.ReorderSongInCollectionAsync(id, ifRevision, songId, targetId, after, inner), token), ct);

    public Task<MusicNetworkPlaylistMutationResult> RemoveNetworkPlaylistAsync(string id, long ifRevision, CancellationToken ct = default) =>
        RunAdmittedAsync(token => recommendationQueue.InLaneAsync((owner, inner) => owner.RemoveNetworkPlaylistAsync(id, ifRevision, inner), token), ct);

    // ---- Queue & History ----

    public Task<MusicQueuePageResult> GetQueueAsync(long offset = 0, int limit = 50, CancellationToken ct = default) =>
        RunAdmittedAsync(token => recommendationQueue.InLaneAsync((owner, inner) => owner.GetQueueAsync(offset, limit, inner), token), ct);

    public Task<MusicQueuePositionResult> SetPlaybackModeAsync(int mode, CancellationToken ct = default) =>
        RunAdmittedAsync(token => recommendationQueue.ExecuteExternalAsync(
            MusicRecommendationQueueChange.Mode, inner => library.SetPlaybackModeAsync(mode, inner), token), ct);

    public Task<MusicQueuePeekResult> PeekNextQueueAsync(CancellationToken ct = default) =>
        RunAdmittedAsync(token => recommendationQueue.InLaneAsync((owner, inner) => owner.PeekNextQueueAsync(inner), token), ct);

    public Task<MusicQueuePositionResult> PlayCollectionAsync(string id, long ifRevision, int index, CancellationToken ct = default) =>
        RunAdmittedAsync(token => recommendationQueue.ExecuteExternalAsync(
            MusicRecommendationQueueChange.Replace, inner => library.PlayCollectionAsync(id, ifRevision, index, inner), token), ct);

    public Task<MusicQueuePositionResult> ReplaceQueueAsync(IReadOnlyList<MusicSongRecord> songs, int index,
        MusicPlayContextRecord? context = null, MusicQueueSourceRecord? queueSource = null,
        long? expectedVersion = null, CancellationToken ct = default) =>
        RunAdmittedAsync(token => recommendationQueue.ExecuteExternalAsync(
            MusicRecommendationQueueChange.Replace, inner => library.ReplaceQueueAsync(songs, index, context, queueSource, expectedVersion, inner), token), ct);

    public Task<MusicQueueAppendResult> AppendQueueAsync(IReadOnlyList<MusicSongRecord> songs,
        MusicPlayContextRecord? context = null, bool next = false, long? expectedVersion = null, CancellationToken ct = default) =>
        RunAdmittedAsync(token => recommendationQueue.ExecuteExternalAsync(
            next ? MusicRecommendationQueueChange.PlayNext : MusicRecommendationQueueChange.Append,
            inner => library.AppendQueueAsync(songs, context, next, expectedVersion, inner), token), ct);

    public Task<MusicQueuePositionResult> SelectQueueEntryAsync(string id, bool remember = true, CancellationToken ct = default) =>
        RunAdmittedAsync(token => recommendationQueue.ExecuteExternalAsync(
            MusicRecommendationQueueChange.Selection, inner => library.SelectQueueEntryAsync(id, remember, inner), token), ct);

    public Task<MusicQueueRemoveResult> RemoveQueueEntryAsync(string id, CancellationToken ct = default) =>
        RunAdmittedAsync(token => recommendationQueue.ExecuteExternalAsync(
            MusicRecommendationQueueChange.Remove, inner => library.RemoveQueueEntryAsync(id, inner), token), ct);

    public Task<MusicQueuePositionResult> ClearQueueAsync(CancellationToken ct = default) =>
        RunAdmittedAsync(token => recommendationQueue.ExecuteExternalAsync(
            MusicRecommendationQueueChange.Clear, inner => library.ClearQueueAsync(inner), token), ct);

    public Task<MusicQueueStepResult> StepQueueAsync(int direction, int? modeOverride = null, string reason = "manual", CancellationToken ct = default) =>
        RunAdmittedAsync(token => recommendationQueue.ExecuteExternalAsync(
            MusicRecommendationQueueChange.Selection, inner => library.StepQueueAsync(direction, modeOverride, reason, inner), token), ct);

    public Task<MusicHistoryPlaylistsPage> ListHistoryPlaylistsAsync(long offset = 0, int limit = 50, CancellationToken ct = default) =>
        RunAdmittedAsync(token => recommendationQueue.InLaneAsync((owner, inner) => owner.ListHistoryPlaylistsAsync(offset, limit, inner), token), ct);

    public Task<MusicHistoryDaysPage> ListHistoryDaysAsync(long offset = 0, int limit = 50, CancellationToken ct = default) =>
        RunAdmittedAsync(token => recommendationQueue.InLaneAsync((owner, inner) => owner.ListHistoryDaysAsync(offset, limit, inner), token), ct);

    public Task<MusicHistoryDayDetail> GetHistoryDayDetailAsync(string id, long offset = 0, int limit = 50, CancellationToken ct = default) =>
        RunAdmittedAsync(token => recommendationQueue.InLaneAsync((owner, inner) => owner.GetHistoryDayDetailAsync(id, offset, limit, inner), token), ct);

    public Task RemoveHistoryPlaylistAsync(string id, CancellationToken ct = default) =>
        RunAdmittedAsync(async token =>
        {
            await recommendationQueue.InLaneAsync(async (owner, inner) =>
            {
                await owner.RemoveHistoryPlaylistAsync(id, inner).ConfigureAwait(false);
                return true;
            }, token).ConfigureAwait(false);
            return true;
        }, ct);

    public Task<MusicQueuePositionResult> RestoreHistoryPlaylistAsync(string id, CancellationToken ct = default) =>
        RunAdmittedAsync(token => recommendationQueue.ExecuteExternalAsync(
            MusicRecommendationQueueChange.Restore, inner => library.RestoreHistoryPlaylistAsync(id, inner), token), ct);

    public Task RecordPlayedHistoryAsync(string entryId, long atMs, CancellationToken ct = default) =>
        RunAdmittedAsync(async token =>
        {
            await recommendationQueue.InLaneAsync(async (owner, inner) =>
            {
                await owner.RecordPlayedHistoryAsync(entryId, atMs, inner).ConfigureAwait(false);
                return true;
            }, token).ConfigureAwait(false);
            return true;
        }, ct);
}
