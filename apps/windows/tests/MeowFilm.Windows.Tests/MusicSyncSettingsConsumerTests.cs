using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using MeowFilm.Capabilities;

namespace MeowFilm.Windows.Tests;

public static class MusicSyncSettingsConsumerTests
{
    public static IEnumerable<TestCase> Cases() => MusicUiTests.SyncSettingsConsumerCases();
}

public static partial class MusicUiTests
{
    internal static IEnumerable<TestCase> SyncSettingsConsumerCases()
    {
        yield return Case("platform capsules consume the sole sync owner without a draft page", async () =>
        {
            await using var f = await SearchScene.Create();
            await f.Module.ActivateSettingsAsync();
            await SearchUntil(() => f.Module.LibrarySync.CanEdit);
            var view = f.Module.SettingsView;
            view.Measure(new Size(1000, 1000));
            view.Arrange(new Rect(0, 0, 1000, 1000)); view.UpdateLayout();
            var tabs = Visuals(view).OfType<TabControl>()
                .Single(control => AutomationProperties.GetName(control) == "设置分类");
            tabs.SelectedIndex = 0; view.UpdateLayout();
            var accounts = Visuals(view).OfType<MusicAccountsView>().Single();
            True(ReferenceEquals(accounts.DataContext, f.Module),
                "platform capsules bind directly to the capability");
            Equal(3, f.Module.PlatformSettings.Count);
            foreach (var row in f.Module.PlatformSettings)
                True(ReferenceEquals(row.Sync,
                    f.Module.LibrarySync.Platforms.Single(item => item.Platform == row.Platform)),
                    "each capsule must project the sole native sync owner");
            Equal(0, f.Runtime.Calls.Count);
        });

        yield return Case("platform sync switch saves immediately and authoritative denial restores the switch", async () =>
        {
            await using var f = await SearchScene.Create();
            await f.Module.ActivateSettingsAsync();
            await SearchUntil(() => f.Module.LibrarySync.CanEdit);
            var row = f.Module.PlatformSettings.Single(item => item.Platform == "wy");
            int http = f.Http.Requests.Count;
            await f.Module.SetPlatformSyncAsync(row, "favorites", true);
            await SearchUntil(() => f.Module.LibrarySync.CanEdit);
            True(!row.FavoritesSyncEnabled && !row.PlaylistsSyncEnabled,
                "AUTH_REQUIRED must restore the sole durable switch state");
            Equal("未登录", f.Module.LibrarySync.Status);
            var stored = await f.Call("music.librarySync.get");
            True(!stored.GetProperty("platforms").GetProperty("wy").GetProperty("favorites").GetBoolean(),
                "denied favorite sync was not persisted");
            Equal(http, f.Http.Requests.Count);
            Equal(0, f.Runtime.Calls.Count);
        });

        yield return Case("platform settings retirement cancels its read lane without retaining local edits", async () =>
        {
            await using var f = await SearchScene.Create();
            await f.Module.ActivateSettingsAsync();
            await SearchUntil(() => f.Module.LibrarySync.CanEdit);
            await f.Module.DeactivateSettingsAsync();
            True(!f.Module.LibrarySync.CanEdit && !f.Module.LibrarySync.IsActive,
                "hidden settings own no active presentation request");
            await f.Module.ActivateSettingsAsync();
            await SearchUntil(() => f.Module.LibrarySync.CanEdit);
            True(f.Module.PlatformSettings.All(row =>
                !row.FavoritesSyncEnabled && !row.PlaylistsSyncEnabled),
                "reactivated settings use only durable sync state");
            Equal(0, f.Runtime.Calls.Count);
        });
    }
}
