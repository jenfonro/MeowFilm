using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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
        yield return Case("general source first read cancelled by failed shutdown can recover on the current page without replaying writes",
            () => SettingsSourceShutdownRecovery(false));
        yield return Case("general source first read cancelled by failed shutdown exposes Retry after a fresh read failure without blocking shutdown cancellation",
            () => SettingsSourceShutdownRecovery(true));
    }

    private static async Task SettingsSourceShutdownRecovery(bool failRecoveryRead)
    {
        const string get = "music.catalogSource.settings.get";
        const string save = "music.catalogSource.settings.save";
        await using var f = new QualityFixture();
        await f.Ok(save, new { ifRevision = 0, platform = "kg" });
        var calls = new ObservedApplication(f.App);
        var module = new MusicCapability(calls, f.Store, new PlaybackProbe(), dialogs: new Dialogs());
        await using var appearance = new AppAppearance(f.Store);
        var held = calls.HoldNext(get);
        var failure = new SourceShutdownFailure();
        var lifecycle = new AppShutdown([module, failure], [], sharedParticipants: [appearance]);
        bool acceptedRetiredValue = false;
        module.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MusicCapability.CatalogSourcePlatform) &&
                module.CatalogSourcePlatform == "kg") acceptedRetiredValue = true;
        };
        Task? close = null;
        try
        {
            var general = new GeneralSettingsView(appearance, [module]);
            var sourceTab = ((Panel)general.FindName("Areas")).Children.OfType<RadioButton>()
                .Single(tab => Equals(tab.Tag, "sources"));
            sourceTab.IsChecked = true;
            using var mount = new ReplicaMount(general);
            await held.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(8));
            await Drain();
            var source = (MusicHomeSourceSettingsView)module.HomeSourceSettingsView;
            var picker = (ComboBox)source.FindName("CatalogSourcePicker");
            var retry = (Button)source.FindName("CatalogSourceRetry");
            True(source.IsLoaded && source.IsVisible && sourceTab.IsChecked == true,
                "source recovery prerequisite: the real General source page owns the first pending read");
            Equal(1, calls.Count(get));
            True(!module.CanEditCatalogSource && !picker.IsEnabled,
                "source recovery prerequisite: no successful initial source restore has occurred");
            // The held reply is kg; a fresh authoritative read must now return wy.
            await f.Ok(save, new { ifRevision = 1, platform = "wy" });
            int writes = f.Store.Writes;
            var firstRead = calls.Log.Single(call => call.Method == get);
            close = lifecycle.CloseAsync();
            await Until(() => firstRead.Token.IsCancellationRequested);
            True(!close.IsCompleted, "shutdown must drain the actual held read before reaching the failing participant");
            Equal(1, calls.Count(get));
            if (failRecoveryRead) calls.FailNext(get, "STORAGE_UNAVAILABLE");
            held.Release();
            try
            {
                await close.WaitAsync(TimeSpan.FromSeconds(8));
                throw new InvalidOperationException("source recovery prerequisite: shutdown unexpectedly succeeded");
            }
            catch (InvalidOperationException error) when (error.Message == "SHUTDOWN_FAILED") { }
            Equal(1, failure.Prepares);
            Equal(2, calls.Count(get));
            await lifecycle.CancelShutdownPreparationAsync().WaitAsync(TimeSpan.FromSeconds(8));
            await Drain();
            True(!acceptedRetiredValue, "a cancelled first-read reply must never publish its retired kg value");
            True(source.IsVisible && sourceTab.IsChecked == true,
                "recovery must retain the current page without a visibility or navigation workaround");
            Equal(2, calls.Count(get));
            if (failRecoveryRead)
            {
                True(!module.CanEditCatalogSource && !picker.IsEnabled && module.CanReadCatalogSource &&
                    module.HasCatalogSourceError && retry.IsVisible && retry.IsEnabled,
                    "a failed fresh read must leave shutdown cancellation successful and the current page retryable");
            }
            else
            {
                True(module.CanEditCatalogSource,
                    "shutdown cancellation must automatically restore the unloaded source with a fresh read");
            }
            await Until(() => module.CanEditCatalogSource || (retry.IsVisible && retry.IsEnabled));
            if (!module.CanEditCatalogSource)
            {
                retry.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await Until(() => module.CanEditCatalogSource);
            }
            True(calls.Count(get) >= 2 && picker.IsEnabled && Equals(picker.SelectedValue, "wy") &&
                module.CatalogSourcePlatform == "wy" && module.CatalogSourceStatus.Length == 0,
                "the current page must recover through a fresh authoritative read, automatically or via its real Retry button");
            True(!acceptedRetiredValue, "recovery must not revive the old successful-but-cancelled reply");
            Equal(0, calls.Count(save));
            Equal(writes, f.Store.Writes);
            Equal(0, f.Runtime.Calls.Count);
            Equal(failRecoveryRead ? 3 : 2, calls.Count(get));
            // A second cancelled close must retain the now-loaded source without another read.
            int reads = calls.Count(get);
            close = lifecycle.CloseAsync();
            try
            {
                await close.WaitAsync(TimeSpan.FromSeconds(8));
                throw new InvalidOperationException("source recovery prerequisite: second shutdown unexpectedly succeeded");
            }
            catch (InvalidOperationException error) when (error.Message == "SHUTDOWN_FAILED") { }
            Equal(2, failure.Prepares);
            await lifecycle.CancelShutdownPreparationAsync().WaitAsync(TimeSpan.FromSeconds(8));
            await Drain();
            Equal(reads, calls.Count(get));
            True(source.IsVisible && sourceTab.IsChecked == true && picker.IsEnabled &&
                Equals(picker.SelectedValue, "wy") && module.CatalogSourceStatus.Length == 0,
                "an already loaded source must remain usable on the current page without extra I/O");
            True(!acceptedRetiredValue, "later shutdown cancellation must not publish the retired kg value");
            Equal(0, calls.Count(save));
            Equal(writes, f.Store.Writes);
            Equal(0, f.Runtime.Calls.Count);
        }
        finally
        {
            calls.ReleaseAll();
            if (close is not null)
                try { await close; } catch { /* Preserve the original assertion while draining failed preparation. */ }
            await module.AbortAsync();
        }
    }

    private sealed class SourceShutdownFailure : IDesktopShutdownParticipant
    {
        internal int Prepares;
        public Task PrepareShutdownAsync()
        {
            Prepares++;
            return Task.FromException(new InvalidOperationException("SYNTHETIC_SHUTDOWN_FAILURE"));
        }
        public Task CancelShutdownPreparationAsync() => Task.CompletedTask;
        public Task AbortAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
            priority.BringIntoView(); await Drain(); view.UpdateLayout();
            var list = (ListBox)priority.FindName("PriorityItems");
            True(list.Items.Count >= 5, "E2 fixture needs a nonboundary row with two downward destinations");
            var item = (MusicQualityRow)list.Items[1];
            var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(item);
            var handle = Visuals(row).OfType<Thumb>().Single();
            handle.BringIntoView(); await Drain();
            mount.Window.Focus();
            handle.Focus();

            IInputElement CurrentTarget(RoutedCommand command)
            {
                var focused = Keyboard.FocusedElement;
                True(focused is Thumb thumb && ReferenceEquals(thumb.DataContext, item),
                    "E2 current keyboard focus must remain on the same quality row's handle");
                True(command.CanExecute(null, focused), "E2 command must be enabled at the actual current focus");
                return focused!;
            }
            True(ReferenceEquals(Keyboard.FocusedElement, handle),
                "E2 ENVIRONMENT PRECONDITION: the real handle must own Keyboard.FocusedElement; no simulated focus is allowed");
            True(handle.InputBindings.OfType<KeyBinding>().Any(binding =>
                binding.Key == Key.Down && binding.Modifiers == ModifierKeys.Alt &&
                ReferenceEquals(binding.Command, ComponentCommands.MoveDown)),
                "E2 downward command is the handle's Alt+Down binding");
            int saves = f.Calls.Count(method);
            for (int move = 1; move <= 2; move++)
            {
                var held = f.Calls.HoldNext(method);
                try
                {
                    ComponentCommands.MoveDown.Execute(null, CurrentTarget(ComponentCommands.MoveDown));
                    await held.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(8));
                    await Drain();
                    True(f.Module.Busy && !f.Module.CanEditQuality, "E2 held receipt keeps save admission closed");
                    True(!list.IsEnabled, "E2 busy must actually disable PriorityItems");
                    Equal(saves + move, f.Calls.Count(method));
                }
                finally { held.Release(); }
                await Until(() => f.Module.CanEditQuality);
                Equal(1 + move, list.Items.IndexOf(item));
                _ = CurrentTarget(ComponentCommands.MoveDown);
            }
            Equal(saves + 2, f.Calls.Count(method));

            var leaving = f.Calls.HoldNext(method);
            try
            {
                // Move upward so this phase stays away from the lower boundary.
                ComponentCommands.MoveUp.Execute(null, CurrentTarget(ComponentCommands.MoveUp));
                await leaving.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(8));
                await Drain();
                view.Visibility = Visibility.Collapsed;
                destination.Visibility = Visibility.Visible;
                destination.Focus(); await Drain();
                True(ReferenceEquals(Keyboard.FocusedElement, destination),
                    "E2 ENVIRONMENT PRECONDITION: the visible destination must own real keyboard focus");
                True(!priority.IsKeyboardFocusWithin && !priority.IsVisible, "E2 the original page has lost focus and visibility");
                True(!ComponentCommands.MoveDown.CanExecute(null, handle), "E2 a retired handle cannot admit another move");
                ComponentCommands.MoveDown.Execute(null, handle);
                Equal(saves + 3, f.Calls.Count(method));
            }
            finally { leaving.Release(); }
            await Until(() => !f.Module.Busy);
            await Drain();
            True(ReferenceEquals(Keyboard.FocusedElement, destination) && !priority.IsKeyboardFocusWithin,
                "E2 settling a retired page's save must not reclaim focus from the destination");
            Equal(saves + 3, f.Calls.Count(method));
        }
        finally
        {
            // A failed focus prerequisite must not strand a held owner during fixture cleanup.
            f.Calls.ReleaseAll();
        }
    }

    private static Thumb SequenceHandle(ListBox list, object item) =>
        Visuals((ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(item)).OfType<Thumb>().Single();

    private static void SequenceFocus(UIElement target, string stage)
    {
        target.Focus();
        True(ReferenceEquals(Keyboard.FocusedElement, target),
            $"E2 ENVIRONMENT PRECONDITION: {stage} must acquire real keyboard focus");
    }

    private static void SequenceMove(object item, RoutedCommand command)
    {
        var focused = Keyboard.FocusedElement;
        True(focused is Thumb thumb && ReferenceEquals(thumb.DataContext, item),
            "E2 move must originate from current keyboard focus on the same item object");
        True(command.CanExecute(null, focused), "E2 current focused handle must admit the move");
        command.Execute(null, focused);
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
                priority.BringIntoView(); await Drain(); view.UpdateLayout();
                await Until(() => priority.CanEdit);
                var list = (ListBox)priority.FindName("PriorityItems");
                True(list.Items.Count >= 3, "E2 each shared list supplies a nonboundary source");
                // Capture each consumer's current object, including the shared metadata collection.
                var original = list.Items.Cast<object>().ToArray();
                var item = original[1];
                var handle = SequenceHandle(list, item);
                handle.BringIntoView(); await Drain(); mount.Window.Focus();
                SequenceFocus(handle, name + "-initial");
                int saves = f.Calls.Count(method);
                // Down then up supports short lists while requiring both commands to follow the same row.
                for (int step = 0; step < 2; step++)
                {
                    var held = f.Calls.HoldNext(method);
                    try
                    {
                        SequenceMove(item, step == 0 ? ComponentCommands.MoveDown : ComponentCommands.MoveUp);
                        await held.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(8)); await Drain();
                        True(f.Module.Busy && !priority.CanEdit && !list.IsEnabled,
                            "E2 all shared lists retain actual disabling during accepted writes");
                        Equal(saves + step + 1, f.Calls.Count(method));
                    }
                    finally { held.Release(); }
                    await Until(() => !f.Module.Busy && priority.CanEdit);
                    var current = SequenceHandle(list, item);
                    True(ReferenceEquals(Keyboard.FocusedElement, current) && ReferenceEquals(current.DataContext, item),
                        $"E2 {name} step {step}: settlement restores the current handle without test refocusing;" +
                        $" focused={Keyboard.FocusedElement?.GetType().Name}; currentIsOriginal={ReferenceEquals(current, handle)};" +
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
            priority.BringIntoView(); await Drain(); view.UpdateLayout();
            var list = (ListBox)priority.FindName("PriorityItems");
            var item = list.Items[1];
            var handle = SequenceHandle(list, item);
            handle.BringIntoView(); await Drain(); mount.Window.Focus();
            SequenceFocus(handle, transition + "-initial");
            int saves = f.Calls.Count(method);
            var held = f.Calls.HoldNext(method);
            try
            {
                SequenceMove(item, ComponentCommands.MoveDown);
                await held.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(8)); await Drain();
                True(!list.IsEnabled && f.Module.Busy, "E2 retirement starts during a physically disabled pending save");
                if (transition == "focus-away-and-back")
                {
                    var anchor = ReplicaAnchors(view)[2];
                    True(anchor.IsVisible && anchor.IsEnabled, "E2 same-page destination is an editable visible settings control");
                    SequenceFocus(anchor, transition + "-away");
                }
                else if (transition == "hide-and-return")
                {
                    view.Visibility = Visibility.Collapsed; await Drain();
                    True(!view.IsVisible, "E2 original page really hides");
                    view.Visibility = Visibility.Visible; await Drain();
                    True(view.IsVisible, "E2 original page really returns");
                }
                else
                {
                    pages.Children.Remove(view); await Drain();
                    True(!view.IsLoaded, "E2 original view really unloads");
                    pages.Children.Add(view); await Drain();
                    True(view.IsLoaded && view.IsVisible, "E2 same original view really remounts");
                }
                // Returning to the parent must not revive the retired request.
                priority.BringIntoView(); await Drain();
                SequenceFocus(priority, transition + "-parent-return");
                Equal(saves + 1, f.Calls.Count(method));
            }
            finally { held.Release(); }
            await Until(() => !f.Module.Busy && priority.CanEdit);
            await Drain();
            True(ReferenceEquals(Keyboard.FocusedElement, priority),
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
        var list = (ListBox)priority.FindName("PriorityItems");
        var handle = SequenceHandle(list, rows[1]);
        mount.Window.Focus(); SequenceFocus(handle, "no-op-initial");
        int saves = f.Calls.Count("music.quality.settings.save");
        SequenceMove(rows[1], ComponentCommands.MoveDown);
        await Drain();
        True(ReferenceEquals(Keyboard.FocusedElement, handle), "E2 synchronous no-op must return focus to the original handle");
        True(list.IsEnabled && priority.CanEdit, "E2 no-op does not manufacture a busy transition");
        Equal(1, requests);
        True(list.Items.Cast<object>().SequenceEqual(rows), "E2 rejected move does not change row order");
        Equal(saves, f.Calls.Count("music.quality.settings.save"));
    }
}
