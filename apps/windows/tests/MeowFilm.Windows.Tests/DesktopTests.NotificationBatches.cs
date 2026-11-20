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
        yield return Case("module business notifications leave shell unchanged and header events update live actions", async () =>
        {
            var enabled = true;
            var view = new BatchHeaderView(new("before",
                Actions: [new("action", "执行", _ => Task.CompletedTask, () => enabled)]));
            var module = new ProbeModule("music", view: view);
            await using var f = new Fixture([module]); await f.LoadAsync();
            var actions = f.HeaderElement<ItemsControl>("HeaderActions");
            var original = actions.Items.Cast<DesktopHeaderActionItem>().Single();
            var reads = view.Reads;
            var navigationReads = module.NavigationReads;
            for (int i = 0; i < 3; i++)
            { module.Notify("CurrentTitle"); module.Notify("PositionSeconds"); }
            await DrainAsync();
            Equal(reads, view.Reads, "unrelated business properties never reread the shell header");
            Equal(navigationReads, module.NavigationReads, "business notifications never redo shell navigation presentation");
            Same(original, actions.Items.Cast<DesktopHeaderActionItem>().Single(), "business notifications retain the live action");
            enabled = false;
            view.Contribution = view.Contribution with { Title = "after" };
            view.NotifyHeaderChanged();
            await DrainAsync();
            Equal(reads + 1, view.Reads, "the header owner event rereads its contribution");
            Same(original, actions.Items.Cast<DesktopHeaderActionItem>().Single(), "unchanged live action identity");
            False(original.IsEnabled, "the retained action publishes its latest enabled state");
            Equal("meowfilm · after", f.Shell.Title, "the batch reads the latest header state");

            reads = view.Reads;
            enabled = true;
            await Task.Run(view.NotifyHeaderChanged);
            await DrainAsync();
            Equal(reads + 1, view.Reads, "a background header event is applied on the dispatcher");
            Same(original, actions.Items.Cast<DesktopHeaderActionItem>().Single(), "background refresh retains the action");
            True(original.IsEnabled, "background notifications update enabled state on the dispatcher");

            reads = view.Reads; navigationReads = module.NavigationReads;
            var replacementPlayer = new Border { Height = 64 };
            module.ReplacePlayer(replacementPlayer); await DrainAsync();
            Same(replacementPlayer, f.Element<ContentControl>("PlayerHost").Content, "player replacement reaches its actual host");
            Equal(reads, view.Reads, "player replacement does not reread the unrelated page header");
            Equal(navigationReads, module.NavigationReads, "player replacement does not resynchronize navigation");
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
                module.Notify(nameof(IDesktopModule.Title));
            };
            module.Notify(nameof(IDesktopModule.Title));
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
        yield return Case("cancelled shutdown releases a queued module batch and resumes live presentation", async () =>
        {
            var view = new BatchHeaderView(new("before"));
            var module = new ProbeModule("music", view: view);
            var attempts = 0;
            await using var f = new Fixture([module],
                _ => ++attempts == 1 ? Task.FromException(new Exception("save")) : Task.CompletedTask,
                cancel: _ => Task.CompletedTask);
            await f.LoadAsync();
            module.Notify(nameof(IDesktopModule.Title));
            f.Shell.Close();
            await UntilAsync(() => f.Status == "关闭失败", "shutdown failed");
            await DrainAsync();
            Click(f.Element<Button>("CancelShutdownButton"));
            await UntilAsync(() => f.Status == "" && f.Search.IsEnabled, "shutdown cancellation resumed the UI");
            await DrainAsync();
            var reads = view.Reads;
            view.Contribution = new("after");
            module.Notify(nameof(IDesktopModule.Title));
            await DrainAsync();
            Equal(reads + 1, view.Reads, "discarding a closing batch cannot strand the next live batch");
            Equal("meowfilm · after", f.Shell.Title, "the resumed shell presents the latest contribution");
            f.Shell.Close(); await f.WaitClosedAsync();
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
        public event EventHandler? HeaderChanged;
        public void NotifyHeaderChanged() => HeaderChanged?.Invoke(this, EventArgs.Empty);
    }
}
