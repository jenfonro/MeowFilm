// SPDX-License-Identifier: GPL-3.0-only
using MeowFilm.Capabilities.Platforms;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Accounts;

// One-time, read-only import of the published account-v1 device records.
internal static class AccountClientContextImport
{
    internal static async Task<MusicClientContextSeeds> ReadAsync(IProtectedDocumentStore store, CancellationToken token)
    {
        try { return await ReadCoreAsync(store, token).ConfigureAwait(false); }
        catch (Exception error) when (error is MusicAccountException or ProtectedDocumentException)
        { throw new MusicPlatformException(AccountRules.Error(error)); }
    }
    private static async Task<MusicClientContextSeeds> ReadCoreAsync(IProtectedDocumentStore store, CancellationToken token)
    {
        var document = AccountDocument.Decode(await store.ReadAsync(MusicAccountOwner.DocumentNamespace, token).ConfigureAwait(false));
        token.ThrowIfCancellationRequested();
        MusicWyClientContext? wy = null;
        if (document.Get("wy").Cookie is { } cookie)
        {
            var values = AccountRules.Cookies(cookie);
            var fresh = MusicWyClientContext.Create();
            string Value(string key, string fallback) =>
                values.TryGetValue(key, out var value) && value.Length > 0 ? value : fallback;
            wy = fresh with
            {
                DeviceId = Value("deviceId", fresh.DeviceId),
                Nuid = Value("_ntes_nuid", fresh.Nuid),
                Nnid = Value("_ntes_nnid", ""),
                Wnmcid = Value("WNMCID", fresh.Wnmcid),
                Nmtid = values.TryGetValue("NMTID", out var nmtid) && nmtid.Length > 0 ? nmtid : null
            };
        }
        return new(wy, document.GetKgDevice(), document.GetQqFeedbackDevice());
    }
}
