using Microsoft.UI.Xaml;
using Windows.Foundation;
using Window = MeowFilm.Desktop.DesktopWindow;
using Color = Windows.UI.Color;
using Colors = Microsoft.UI.Colors;
using Windows.UI.Text;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.System;
using System.Text.Json;
using MeowFilm.Capabilities;
using MeowFilm.AppHost;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static class MusicSettingsSequenceTests
{
    public static IEnumerable<TestCase> Cases() => MusicUiTests.SettingsSequenceCases();
}

public static partial class MusicUiTests
{
    internal static IEnumerable<TestCase> SettingsSequenceCases()
    {
        yield return Case("settings keyboard sequence retains the focused quality row across saves and does not reclaim focus after leaving",
            SettingsKeyboardSequence);
        yield return Case("settings keyboard sequence moves the same row twice in all six shared lists with real busy disabling",
            SettingsSixListSequence);
        foreach (var transition in new[] { "focus-away-and-back", "hide-and-return", "unload-and-remount" })
            yield return Case($"settings keyboard sequence retires focus restoration on {transition}",
                () => SettingsRetiredFocusSequence(transition));
        yield return Case("settings keyboard sequence synchronous no-op retains the original handle without saving",
            SettingsNoOpSequence);


        foreach (bool quality in new[] { false, true })
        {
            bool selected = quality;
            yield return Case($"settings {(quality ? "quality" : "metadata")} admitted unknown settles after close and only a later same-scope save clears it",
                () => SettingsUnknownAfterClose(selected));
        }
        yield return Case("settings unknowns for independent policy fields survive unrelated and independent-store successes",
            SettingsReceiptIsolation);
        yield return Case("metadata unknown readback failing after retirement preserves its receipt without publishing the old error",
            MetadataRetiredReadback);
        foreach (bool failure in new[] { false, true })
        {
            bool fail = failure;
            yield return Case($"source management retired read {(fail ? "error" : "value")} cannot publish or reload its replacement",
                () => SourceRetiredRead(fail));
        }
        yield return Case("official source admitted unknown survives retirement and reopening until an actual same-setting success",
            SourceUnknownAfterRetirement);
        yield return Case("retired source checks release a reopened row without publishing the retired result",
            SourceRetiredCheck);
        foreach (string setting in new[] { "appearance", "catalogSource", "search" })
        {
            string selected = setting;
            yield return Case($"settings {setting} unknown settles without publishing to a retired page and clears only after a confirmed save",
                () => SettingsOtherUnknownAfterClose(selected));
        }
    }

    private static async Task SourceRetiredCheck()
    {
        await using var f = await Fixture.Create();
        await f.Module.RefreshSourceManagementAsync();
        var row = f.Module.Sources.Single(item => item.Platform == "wy");
        var held = f.Calls.HoldNext("music.official.check");
        held.Transform = _ => JsonSerializer.SerializeToElement(new { ok = true, state = "ok" });
        var checking = f.Module.CheckSourcesAsync([row]);
        try
        {
            await held.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.Module.RetireSourceManagement();
            await f.Module.RefreshSourceManagementAsync();
            var current = f.Module.Sources.Single(item => item.Platform == "wy");
            True(!ReferenceEquals(row, current) && current.Checking,
                "the replacement projection stays busy until the physical source check settles");
            string summary = current.Summary, state = current.State, status = f.Module.Status;
            var platforms = current.Platforms.ToArray();
            int retiredChanges = 0;
            row.PropertyChanged += (_, _) => retiredChanges++;
            held.Release(); await checking.WaitAsync(TimeSpan.FromSeconds(5));
            True(!current.Checking && current.Summary == summary && current.State == state &&
                current.Platforms.SequenceEqual(platforms) && row.Checking && retiredChanges == 0,
                "only the active projection releases the physical check; its retired result is not replayed");
            Equal(status, f.Module.Status);
            Equal("未检测", current.StatusText);
        }
        finally { held.Release(); await checking; }
    }

    private static async Task SettingsOtherUnknownAfterClose(string setting)
    {
        await using var f = await Fixture.Create();
        string method;
        Func<Task> save, retire, reopen, confirm;
        Func<bool> original, committed;
        switch (setting)
        {
            case "appearance":
                await f.Module.EnsureAppearanceAsync();
                method = "music.appearance.save";
                save = () => f.Module.SetAppearanceAsync("playlistView", "list");
                retire = () => { f.Module.RetireAppearanceSettings(); return Task.CompletedTask; };
                reopen = () => f.Module.EnsureAppearanceAsync();
                confirm = () => f.Module.SetAppearanceAsync("playlistView", "grid");
                original = () => f.Module.AppearancePlaylistView == "grid";
                committed = () => f.Module.AppearancePlaylistView == "list";
                break;
            case "catalogSource":
                method = "music.catalogSource.settings.save";
                save = () => f.Module.SetCatalogSourceAsync("kg");
                retire = () => f.Module.DeactivateCatalogSourceSettingsAsync(retirePage: true);
                reopen = () => f.Module.ReadCatalogSourceSettingsAsync();
                confirm = () => f.Module.SetCatalogSourceAsync("wy");
                original = () => f.Module.CatalogSourcePlatform == "wy";
                committed = () => f.Module.CatalogSourcePlatform == "kg";
                break;
            default:
                await f.Module.ActivateSettingsAsync();
                int pages = f.Module.SongSearch.Pages, concurrency = f.Module.SongSearch.Concurrency,
                    cache = f.Module.SongSearch.CacheMinutes;
                int changedPages = pages == 1 ? 2 : 1;
                method = "music.search.settings.save";
                save = () => f.Module.SetSearchAdvancedAsync("songs", changedPages, concurrency, cache);
                retire = () => f.Module.DeactivateSettingsAsync();
                reopen = () => f.Module.ActivateSettingsAsync();
                confirm = () => f.Module.SetSearchAdvancedAsync("songs", pages, concurrency, cache);
                original = () => f.Module.SongSearch.Pages == pages;
                committed = () => f.Module.SongSearch.Pages == changedPages;
                break;
        }
        var held = f.Calls.HoldNext(method); held.Transform = UnknownSetting;
        var saving = save();
        Task retiring = Task.CompletedTask;
        try
        {
            await held.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            retiring = retire();
            held.Release();
            await Task.WhenAll(saving, retiring).WaitAsync(TimeSpan.FromSeconds(5));
            True(original() && f.Module.OwnedNotices.Count == 1,
                "a physically submitted save settles without changing the retired projection");
            await reopen();
            True(committed() && f.Module.OwnedNotices.Count == 1 && f.Calls.Count(method) == 1,
                "readback adopts the stored value without acknowledging or replaying the unknown save");
            f.Module.Volume = 37;
            await f.Module.SaveVolumeAsync();
            Equal(1, f.Module.OwnedNotices.Count);
            await confirm();
            True(original() && f.Module.OwnedNotices.Count == 0 && f.Calls.Count(method) == 2,
                "a confirmed same-setting save clears only its prior unknown");
        }
        finally { held.Release(); await Task.WhenAll(saving, retiring); }
    }

    private static async Task SourceRetiredRead(bool failure)
    {
        await using var f = await Fixture.Create();
        var settings = (await f.Rpc("music.settings.get")).GetProperty("settings");
        await f.Rpc("music.settings.save", new { ifRevision = settings.GetProperty("revision").GetInt64(), lyrics = false });
        bool before = f.Module.LyricsEnabled;
        var held = f.Calls.HoldNext("music.quality.settings.get");
        if (failure) held.Transform = _ => JsonSerializer.SerializeToElement(new { ok = false, code = "NETWORK_ERROR" });
        var reading = f.Module.RefreshSourceManagementAsync();
        try
        {
            await held.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.Module.RetireSourceManagement();
            string status = f.Module.Status;
            int reloads = f.Calls.Count("music.officialSources.settings.get");
            held.Release(); await reading.WaitAsync(TimeSpan.FromSeconds(5));
            Equal(before, f.Module.LyricsEnabled);
            Equal(status, f.Module.Status);
            Equal(reloads, f.Calls.Count("music.officialSources.settings.get"));
            await f.Module.RefreshSourceManagementAsync();
            True(!f.Module.LyricsEnabled && f.Module.CanEditOfficialSources,
                "a new page reads the current owner instead of reviving the retired reply");
        }
        finally { held.Release(); await reading; }
    }

    private static async Task SourceUnknownAfterRetirement()
    {
        await using var f = await Fixture.Create();
        await f.Module.RefreshSourceManagementAsync();
        var row = f.Module.Sources.Single(item => item.Platform == "wy");
        var held = f.Calls.HoldNext("music.officialSources.settings.save"); held.Transform = UnknownSetting;
        var saving = f.Module.SetSourceEnabledAsync(row, false);
        try
        {
            await held.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.Module.RetireSourceManagement();
            int reads = f.Calls.Count("music.officialSources.settings.get");
            held.Release(); await saving.WaitAsync(TimeSpan.FromSeconds(5));
            True(row.Enabled && f.Module.OwnedNotices.Count == 1,
                "retired pages keep their projection while the submitted receipt still owns its unknown");
            Equal(reads, f.Calls.Count("music.officialSources.settings.get"));
            await f.Module.RefreshSourceManagementAsync();
            True(!f.Module.Sources.Single(item => item.Platform == "wy").Enabled,
                "the reopened page reads the committed official source setting");
            Equal(1, f.Module.OwnedNotices.Count);
            await f.Module.SetSourceEnabledAsync(f.Module.Sources.Single(item => item.Platform == "wy"), true);
            Equal(0, f.Module.OwnedNotices.Count);
            Equal(2, f.Calls.Count("music.officialSources.settings.save"));
        }
        finally { held.Release(); await saving; }
    }

    private static JsonElement UnknownSetting(JsonElement _) =>
        JsonSerializer.SerializeToElement(new { ok = false, code = "COMMIT_UNKNOWN" });

    private static async Task MetadataRetiredReadback()
    {
        await using var f = await Fixture.Create();
        await f.Module.ActivateSettingsAsync();
        var row = f.Module.MetadataPlatforms.Single(item => item.Platform == "mg");
        f.Calls.TransformNext("music.metadata.settings.save", UnknownSetting);
        var held = f.Calls.HoldNext("music.metadata.settings.get");
        held.Transform = _ => JsonSerializer.SerializeToElement(new { ok = false, code = "NETWORK_ERROR" });
        var saving = f.Module.SetMetadataEnabledAsync(row, false);
        Task retiring = Task.CompletedTask;
        try
        {
            await held.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            retiring = f.Module.DeactivateSettingsAsync();
            string status = f.Module.Status;
            held.Release();
            await Task.WhenAll(saving, retiring).WaitAsync(TimeSpan.FromSeconds(5));
            True(row.Enabled && f.Module.Status == status && f.Module.OwnedNotices.Count == 1,
                "retired readback cannot publish either stored values or its late failure");
            await f.Module.ActivateSettingsAsync();
            True(!f.Module.MetadataPlatforms.Single(item => item.Platform == "mg").Enabled &&
                f.Module.OwnedNotices.Count == 1 && f.Calls.Count("music.metadata.settings.save") == 1,
                "reopening reads the actual commit without replaying or acknowledging the unknown save");
        }
        finally { held.Release(); await Task.WhenAll(saving, retiring); }
    }

    private static async Task SettingsUnknownAfterClose(bool quality)
    {
        await using var f = await Fixture.Create();
        await f.Module.ActivateSettingsAsync();
        string method = quality ? "music.quality.settings.save" : "music.metadata.settings.save";
        string before = f.Module.PlaybackQualityRequest;
        var metadata = f.Module.MetadataPlatforms.Single(row => row.Platform == "mg");
        var held = f.Calls.HoldNext(method); held.Transform = UnknownSetting;
        Task saving = quality ? f.Module.SetPlaybackQualityRequestAsync("auto")
            : f.Module.SetMetadataEnabledAsync(metadata, false);
        await held.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task leaving = f.Module.DeactivateSettingsAsync();
        try
        {
            await Drain();
            True(!saving.IsCompleted && !leaving.IsCompleted, "closing must retain the actual submitted owner task");
            True(!f.Calls.Log.Last(call => call.Method == method).Token.IsCancellationRequested,
                "closing a settings page must not cancel an admitted save");
            held.Release();
            await Task.WhenAll(saving, leaving).WaitAsync(TimeSpan.FromSeconds(5));
            Equal(before, f.Module.PlaybackQualityRequest);
            True(metadata.Enabled, "a retired metadata save cannot publish its late reply to the closed page");
            Equal(1, f.Module.OwnedNotices.Count);
            object notice = f.Module.OwnedNotices.Single().Owner;
            Equal(1, f.Calls.Count(method));

            await f.Module.ActivateSettingsAsync();
            True(quality ? f.Module.PlaybackQualityRequest == "auto"
                : !f.Module.MetadataPlatforms.Single(row => row.Platform == "mg").Enabled,
                "reopening reads the owner's committed value, not a retained optimistic draft");
            True(ReferenceEquals(notice, f.Module.OwnedNotices.Single().Owner),
                "readback and reopening cannot acknowledge an unknown write");
            Equal(1, f.Calls.Count(method));

            f.Module.Volume = 37;
            await f.Module.SaveVolumeAsync();
            Equal(1, f.Module.OwnedNotices.Count);
            if (quality) await f.Module.SetPlaybackQualityRequestAsync(before);
            else await f.Module.SetMetadataEnabledAsync(f.Module.MetadataPlatforms.Single(row => row.Platform == "mg"), true);
            Equal(0, f.Module.OwnedNotices.Count);
            Equal(2, f.Calls.Count(method));
        }
        finally { held.Release(); await Task.WhenAll(saving, leaving); }
    }

    private static async Task SettingsReceiptIsolation()
    {
        await using var f = await Fixture.Create();
        await f.Module.ActivateSettingsAsync();
        f.Calls.TransformNext("music.metadata.settings.save", UnknownSetting);
        await f.Module.SetMetadataEnabledAsync(f.Module.MetadataPlatforms.Single(row => row.Platform == "mg"), false);
        f.Calls.TransformNext("music.quality.settings.save", UnknownSetting);
        await f.Module.SetPlaybackQualityRequestAsync("auto");
        Equal(2, f.Module.OwnedNotices.Count);
        await using (var independent = await Fixture.Create())
        {
            await independent.Module.ActivateSettingsAsync();
            await independent.Module.SetPlaybackQualityRequestAsync("auto");
            Equal(2, f.Module.OwnedNotices.Count);
        }
        await f.Module.SetPlaybackQualityRequestAsync("128k");
        True(f.Module.OwnedNotices.Count == 1 &&
            f.Module.OwnedNotices.Single().Message.Contains("元数据"),
            "a successful full quality policy write cannot acknowledge metadata fields");
        await f.Module.SetMetadataEnabledAsync(f.Module.MetadataPlatforms.Single(row => row.Platform == "mg"), true);
        Equal(0, f.Module.OwnedNotices.Count);
    }

    private static async Task SettingsKeyboardSequence()
    {
        const string method = "music.quality.settings.save";
        await using var f = await Fixture.Create();
        await f.Module.ActivateSettingsAsync();
        var view = (MusicSettingsView)f.Module.SettingsView;
        var destination = new TextBox { Text = "合成目标页", Visibility = Visibility.Collapsed };
        var pages = new Grid();
        pages.Children.Add(view);
        pages.Children.Add(destination);
        using var mount = new ReplicaMount(pages);
        try
        {
            await Drain(); await Until(() => f.Module.CanEditQuality);
            ReplicaAnchors(view)[2].IsChecked = true;
            var priority = (SettingsPriorityList)view.FindName("QualityPriorities");
            priority.StartBringIntoView(); await Drain(); view.UpdateLayout();
            var list = (ListView)priority.FindName("PriorityItems");
            True(list.Items.Count >= 5, "E2 fixture needs a nonboundary row with two downward destinations");
            var item = (MusicQualityRow)list.Items[1];
            var row = (ListViewItem)list.ContainerFromItem(item);
            var handle = Visuals(row).OfType<Thumb>().Single();
            handle.StartBringIntoView(); await Drain();
            mount.Window.Activate();
            handle.Focus(FocusState.Programmatic);

            Thumb CurrentTarget(int direction)
            {
                var focused = DesktopVisual.Focused(view);
                True(focused is Thumb thumb && ReferenceEquals(thumb.DataContext, item),
                    "E2 current keyboard focus must remain on the same quality row's handle");
                True(priority.CanMove((Thumb)focused!, direction), "E2 command must be enabled at the actual current focus");
                return (Thumb)focused!;
            }
            True(ReferenceEquals(DesktopVisual.Focused(view), handle),
                "E2 ENVIRONMENT PRECONDITION: the real handle must own DesktopVisual.Focused(view); no simulated focus is allowed");
            True(priority.CanMove(handle, 1), "E2 current handle admits the downward operation");
            int saves = f.Calls.Count(method);
            for (int move = 1; move <= 2; move++)
            {
                var held = f.Calls.HoldNext(method);
                try
                {
                    priority.ProcessKey(CurrentTarget(1), VirtualKey.Down, VirtualKeyModifiers.Menu);
                    await held.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(8));
                    await Drain();
                    True(f.Module.Busy && !f.Module.CanEditQuality, "E2 held receipt keeps save admission closed");
                    True(!list.IsEnabled, "E2 busy must actually disable PriorityItems");
                    Equal(saves + move, f.Calls.Count(method));
                }
                finally { held.Release(); }
                await Until(() => f.Module.CanEditQuality);
                Equal(1 + move, list.Items.IndexOf(item));
                _ = CurrentTarget(1);
            }
            Equal(saves + 2, f.Calls.Count(method));

            var leaving = f.Calls.HoldNext(method);
            try
            {
                // Move upward so this phase stays away from the lower boundary.
                priority.ProcessKey(CurrentTarget(-1), VirtualKey.Up, VirtualKeyModifiers.Menu);
                await leaving.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(8));
                await Drain();
                view.Visibility = Visibility.Collapsed;
                destination.Visibility = Visibility.Visible;
                destination.Focus(FocusState.Programmatic); await Drain();
                True(ReferenceEquals(DesktopVisual.Focused(view), destination),
                    "E2 ENVIRONMENT PRECONDITION: the visible destination must own real keyboard focus");
                True(!DesktopVisual.ContainsFocus(priority) && !DesktopVisual.IsVisible(priority), "E2 the original page has lost focus and visibility");
                True(!priority.CanMove(handle, 1), "E2 a retired handle cannot admit another move");
                priority.RequestMove(handle, 1);
                Equal(saves + 3, f.Calls.Count(method));
            }
            finally { leaving.Release(); }
            await Until(() => !f.Module.Busy);
            await Drain();
            True(ReferenceEquals(DesktopVisual.Focused(view), destination) && !DesktopVisual.ContainsFocus(priority),
                "E2 settling a retired page's save must not reclaim focus from the destination");
            Equal(saves + 3, f.Calls.Count(method));
        }
        finally
        {
            // A failed focus prerequisite must not strand a held owner during fixture cleanup.
            f.Calls.ReleaseAll();
        }
    }

    private static Thumb SequenceHandle(ListView list, object item) =>
        Visuals((ListViewItem)list.ContainerFromItem(item)).OfType<Thumb>().Single();

    private static void SequenceFocus(FrameworkElement target, string stage)
    {
        target.Focus(FocusState.Programmatic);
        True(ReferenceEquals(DesktopVisual.Focused(target), target),
            $"E2 ENVIRONMENT PRECONDITION: {stage} must acquire real native focus");
    }

    private static void SequenceMove(SettingsPriorityList priority, object item, int direction)
    {
        var focused = DesktopVisual.Focused(priority);
        True(focused is Thumb thumb && ReferenceEquals(thumb.DataContext, item),
            "E2 move must originate from current native focus on the same item object");
        True(priority.CanMove((Thumb)focused!, direction), "E2 current focused handle must admit the move");
        True(priority.ProcessKey((Thumb)focused!, direction > 0 ? VirtualKey.Down : VirtualKey.Up, VirtualKeyModifiers.Menu),
            "E2 production key adapter handles the admitted semantic action");
    }

    private static async Task SettingsSixListSequence()
    {
        await using var f = await Fixture.Create();
        await f.Module.ActivateSettingsAsync();
        var view = (MusicSettingsView)f.Module.SettingsView;
        using var mount = new ReplicaMount(view);
        try
        {
            await Drain(); await Until(() => !f.Module.Busy);
            foreach (var (name, method, anchor) in new[]
            {
                ("SongPriorities", "music.search.settings.save", 1),
                ("PlaylistPriorities", "music.search.settings.save", 1),
                ("QualityPriorities", "music.quality.settings.save", 2),
                ("SpatialPriorities", "music.quality.settings.save", 2),
                ("MetadataPriorities", "music.metadata.settings.save", 3),
                ("ArtworkPriorities", "music.metadata.settings.save", 3)
            })
            {
                ReplicaAnchors(view)[anchor].IsChecked = true;
                var priority = (SettingsPriorityList)view.FindName(name);
                priority.StartBringIntoView(); await Drain(); view.UpdateLayout();
                await Until(() => priority.CanEdit);
                var list = (ListView)priority.FindName("PriorityItems");
                True(list.Items.Count >= 3, "E2 each shared list supplies a nonboundary source");
                // Capture each consumer's current object, including the shared metadata collection.
                var original = list.Items.Cast<object>().ToArray();
                var item = original[1];
                var handle = SequenceHandle(list, item);
                handle.StartBringIntoView(); await Drain(); mount.Window.Activate();
                SequenceFocus(handle, name + "-initial");
                int saves = f.Calls.Count(method);
                // Down then up supports short lists while requiring both commands to follow the same row.
                for (int step = 0; step < 2; step++)
                {
                    var held = f.Calls.HoldNext(method);
                    try
                    {
                        SequenceMove(priority, item, step == 0 ? 1 : -1);
                        await held.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(8)); await Drain();
                        True(f.Module.Busy && !priority.CanEdit && !list.IsEnabled,
                            "E2 all shared lists retain actual disabling during accepted writes");
                        Equal(saves + step + 1, f.Calls.Count(method));
                    }
                    finally { held.Release(); }
                    await Until(() => !f.Module.Busy && priority.CanEdit);
                    var current = SequenceHandle(list, item);
                    True(ReferenceEquals(DesktopVisual.Focused(view), current) && ReferenceEquals(current.DataContext, item),
                        $"E2 {name} step {step}: settlement restores the current handle without test refocusing;" +
                        $" focused={DesktopVisual.Focused(view)?.GetType().Name}; currentIsOriginal={ReferenceEquals(current, handle)};" +
                        $" originalLoaded={handle.IsLoaded}; currentLoaded={current.IsLoaded}; enabled={list.IsEnabled}");
                    Equal(step == 0 ? 2 : 1, list.Items.IndexOf(item));
                }
                Equal(saves + 2, f.Calls.Count(method));
                True(list.Items.Cast<object>().SequenceEqual(original), "E2 down and up preserve every original row object and order");
            }
        }
        finally { f.Calls.ReleaseAll(); }
    }

    private static async Task SettingsRetiredFocusSequence(string transition)
    {
        const string method = "music.quality.settings.save";
        await using var f = await Fixture.Create();
        await f.Module.ActivateSettingsAsync();
        var view = (MusicSettingsView)f.Module.SettingsView;
        var pages = new Grid();
        pages.Children.Add(view);
        using var mount = new ReplicaMount(pages);
        try
        {
            await Drain(); await Until(() => f.Module.CanEditQuality);
            ReplicaAnchors(view)[2].IsChecked = true;
            var priority = (SettingsPriorityList)view.FindName("QualityPriorities");
            priority.StartBringIntoView(); await Drain(); view.UpdateLayout();
            var list = (ListView)priority.FindName("PriorityItems");
            var item = list.Items[1];
            var handle = SequenceHandle(list, item);
            handle.StartBringIntoView(); await Drain(); mount.Window.Activate();
            SequenceFocus(handle, transition + "-initial");
            int saves = f.Calls.Count(method);
            var held = f.Calls.HoldNext(method);
            try
            {
                SequenceMove(priority, item, 1);
                await held.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(8)); await Drain();
                True(!list.IsEnabled && f.Module.Busy, "E2 retirement starts during a physically disabled pending save");
                if (transition == "focus-away-and-back")
                {
                    var anchor = ReplicaAnchors(view)[2];
                    True(DesktopVisual.IsVisible(anchor) && anchor.IsEnabled, "E2 same-page destination is an editable visible settings control");
                    SequenceFocus(anchor, transition + "-away");
                }
                else if (transition == "hide-and-return")
                {
                    view.Visibility = Visibility.Collapsed; await Drain();
                    True(!DesktopVisual.IsVisible(view), "E2 original page really hides");
                    view.Visibility = Visibility.Visible; await Drain();
                    True(DesktopVisual.IsVisible(view), "E2 original page really returns");
                }
                else
                {
                    pages.Children.Remove(view); await Drain();
                    True(!view.IsLoaded, "E2 original view really unloads");
                    pages.Children.Add(view); await Drain();
                    True(view.IsLoaded && DesktopVisual.IsVisible(view), "E2 same original view really remounts");
                }
                // Returning to the parent must not revive the retired request.
                priority.StartBringIntoView(); await Drain();
                SequenceFocus(priority, transition + "-parent-return");
                Equal(saves + 1, f.Calls.Count(method));
            }
            finally { held.Release(); }
            await Until(() => !f.Module.Busy && priority.CanEdit);
            await Drain();
            True(ReferenceEquals(DesktopVisual.Focused(view), priority),
                "E2 a retired request must not restore the handle even after focus returns to its parent");
            Equal(saves + 1, f.Calls.Count(method));
        }
        finally { f.Calls.ReleaseAll(); }
    }

    private static async Task SettingsNoOpSequence()
    {
        await using var f = await Fixture.Create();
        var rows = new[] { new MusicQualityRow("standard", true), new MusicQualityRow("lossless", true),
            new MusicQualityRow("spatial", true) };
        var priority = new SettingsPriorityList { ItemsSource = rows, CanEdit = true, Title = "合成同步拒绝" };
        int requests = 0;
        priority.MoveRequested += (_, _) => requests++; // Synchronous rejection: no owner work is admitted.
        using var mount = new ReplicaMount(priority);
        await Drain(); priority.UpdateLayout();
        var list = (ListView)priority.FindName("PriorityItems");
        var handle = SequenceHandle(list, rows[1]);
        mount.Window.Activate(); SequenceFocus(handle, "no-op-initial");
        int saves = f.Calls.Count("music.quality.settings.save");
        SequenceMove(priority, rows[1], 1);
        await Drain();
        True(ReferenceEquals(DesktopVisual.Focused(priority), handle), "E2 synchronous no-op must return focus to the original handle");
        True(list.IsEnabled && priority.CanEdit, "E2 no-op does not manufacture a busy transition");
        Equal(1, requests);
        True(list.Items.Cast<object>().SequenceEqual(rows), "E2 rejected move does not change row order");
        Equal(saves, f.Calls.Count("music.quality.settings.save"));
    }
}
