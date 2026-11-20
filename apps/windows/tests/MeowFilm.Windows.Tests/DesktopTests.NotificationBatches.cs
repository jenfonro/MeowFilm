using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Windows.System;
using Microsoft.UI.Xaml.Controls;
using MeowFilm.Threading;
using Microsoft.UI.Dispatching;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static partial class DesktopTests
{
    private static IEnumerable<TestCase> NotificationBatchCases()
    {
        yield return Case("module notification bursts preserve one header presentation and live actions", async () =>
        {
            var enabled = true;
            var view = new BatchHeaderView(new("before",
                Actions: [new("action", "执行", _ => Task.CompletedTask, () => enabled)]));
            var module = new ProbeModule("music", view: view);
            await using var f = new Fixture([module]); await f.LoadAsync();
            var actions = f.HeaderElement<ItemsControl>("HeaderActions");
            var original = actions.Items.Cast<DesktopHeaderActionItem>().Single();
            var reads = view.Reads;
            enabled = false;
            view.Contribution = view.Contribution with { Title = "after" };
            for (int i = 0; i < 1000; i++) module.Notify("CurrentTitle");
            await DrainAsync();
            Equal(reads + 1, view.Reads, "one source read for the entire UI notification burst");
            Same(original, actions.Items.Cast<DesktopHeaderActionItem>().Single(), "unchanged live action identity");
            False(original.IsEnabled, "the retained action publishes its latest enabled state");
            Equal("meowfilm · after", f.Shell.Title, "the batch reads the latest header state");

            reads = view.Reads;
            enabled = true;
                            Task.Run(() =>
                {
                    for (int i = 0; i < 1000; i++) module.Notify("PositionSeconds");
                }).WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            await DrainAsync();
            Equal(reads + 1, view.Reads, "a background burst also has one source read");
            Same(original, actions.Items.Cast<DesktopHeaderActionItem>().Single(), "background refresh retains the action");
            True(original.IsEnabled, "background notifications update enabled state on the dispatcher");
        });
        yield return Case("combined module invalidations retain navigation views settings and player replacements", async () =>
        {
            var current = new ProbeModule("current"); var hidden = new ProbeModule("hidden", hasPlayer: false);
            await using var f = new Fixture([current, hidden]); await f.LoadAsync();
            var oldView = current.View;
            var view = new TextBox { Text = "new view" };
            var settings = new TextBox { Text = "new settings" };
            var player = new Border { Height = 64 };
            var hiddenView = new TextBox { Text = "hidden view" };
            current.ReplaceViews(view, settings);
            current.ReplaceNavigation(new ObservableCollection<NavigationItem>(current.Navigation)
                { new("new", "new", "") });
            current.ReplacePlayer(player);
            hidden.ReplaceViews(hiddenView, new TextBox());
            hidden.ReplaceNavigation(new ObservableCollection<NavigationItem>(hidden.Navigation)
                { new("hidden-new", "hidden new", "") });
            current.NotifyAll(); hidden.NotifyAll();
            await DrainAsync();
            True(ParentOf(oldView) is null, "the replaced main view is detached");
            Same(view, Host(view).Content, "current main view is refreshed");
            Same(player, f.Element<ContentControl>("PlayerHost").Content, "current player replacement is retained");
            True(f.Navigation.Items.OfType<ShellNavigationEntry>().Any(item => item.Route == "new"),
                "navigation invalidation survives the combined batch");
            Equal(0, hidden.InitializeCount, "invalidating an unvisited owner does not initialize it");
            True(ParentOf(hiddenView) is null, "an unvisited owner's replacement stays unmounted");
            Click(f.Element<Button>("SettingsButton")); await DrainAsync();
            Same(settings, Host(settings).Content, "latest settings view is mounted on demand");
            Click(f.Back); await DrainAsync();
            await f.SwitchAsync(hidden);
            Same(hiddenView, Host(hiddenView).Content, "later activation mounts the hidden owner's latest view");
            True(f.Navigation.Items.OfType<ShellNavigationEntry>().Any(item => item.Route == "hidden-new"),
                "later activation uses the hidden owner's latest navigation");
        });
        yield return Case("module invalidation during presentation forms a new batch and shutdown rejects late work", async () =>
        {
            var view = new BatchHeaderView(new("before"));
            var module = new ProbeModule("music", view: view);
            await using var f = new Fixture([module]); await f.LoadAsync();
            var reads = view.Reads;
            var player = new Border { Height = 64 };
            view.OnRead = () =>
            {
                view.OnRead = null;
                view.Contribution = new("after");
                module.ReplacePlayer(player);
            };
            module.Notify("CurrentTitle");
            await DrainAsync();
            Equal(reads + 2, view.Reads, "a new invalidation is not lost while a batch is being presented");
            Equal("meowfilm · after", f.Shell.Title, "the later batch publishes the later contribution");
            Same(player, f.Element<ContentControl>("PlayerHost").Content, "the reentrant player replacement is accepted");

            reads = view.Reads;
            module.NotifyAll();
            f.Shell.Close(); await f.WaitClosedAsync();
            module.NotifyAll(); await DrainAsync();
            Equal(reads, view.Reads, "queued and late notifications cannot present after shutdown");
            Equal(0, module.PropertySubscribers, "shutdown retires the module subscription");
        });
        yield return Case("failed owner release still retires queued and late module notifications", async () =>
        {
            var view = new BatchHeaderView(new("before"));
            var module = new ProbeModule("music", view: view);
            await using var f = new Fixture([module], _ => Task.FromException(new Exception("release")));
            await f.LoadAsync(); module.Notify("CurrentTitle");
            f.Shell.Close(); await f.WaitClosedAsync();
            var reads = view.Reads;
            view.Contribution = new("after");
            for (var index = 0; index < 1000; index++) module.Notify("CurrentTitle");
            await DrainAsync();
            Equal(reads, view.Reads, "closed presentation cannot be revived by late notifications");
            Equal(0, module.PropertySubscribers, "failed release does not retain the closed window");
        });
    }

    private sealed class BatchHeaderView(DesktopHeaderContribution contribution) : TextBox, IDesktopHeaderSource
    {
        public DesktopHeaderContribution Contribution { get; set; } = contribution;
        public int Reads { get; private set; }
        public Action? OnRead { get; set; }
        public new DesktopHeaderContribution Header
        {
            get { Reads++; var current = Contribution; OnRead?.Invoke(); return current; }
        }
        public event EventHandler? HeaderChanged { add { } remove { } }
    }
}
