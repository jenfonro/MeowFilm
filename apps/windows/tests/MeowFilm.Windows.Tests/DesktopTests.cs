using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

// Each case owns an STA dispatcher and test-only modules. No product Main, real runtime or native input.
public static partial class DesktopTests
{
    public static IEnumerable<TestCase> Cases()
    {
        foreach (var test in VisualChromeCases()) yield return test;
        foreach (var test in PresentationCases()) yield return test;
        yield return Case("unified sidebar keeps one current visual tree and back crosses content types", async () =>
        {
            var a = new ProbeModule("music"); var b = new ProbeModule("film", hasPlayer: false);
            await using var f = new Fixture([a,b]); await f.LoadAsync();
            Equal(10, f.Navigation.Items.Count, "all declared visible destinations share one sidebar");
            await f.NavigateAsync(a, "two"); await f.NavigateAsync(b, "three");
            Same(a.PlayerView, f.Element<ContentControl>("PlayerHost").Content,
                "the last real player remains in the global host");
            Same(f.Element<ContentControl>("PlayerHost"), Host(a.PlayerView!),
                "the retained player remains attached once");
            Equal(Visibility.Visible, f.Element<Border>("PlayerFrame").Visibility,
                "a playerless capability keeps the global player visible");
            Click(f.Back); await UntilAsync(() => f.SelectedRoute == "two" && f.Status == "", "cross-type back");
            Same(a, f.SelectedOwner, "restored owner");
            Same(a.PlayerView, f.Element<ContentControl>("PlayerHost").Content,
                "restored owner reuses the same global player");
            var hidden = new ObservableCollection<NavigationItem>(b.Navigation) { new("new", "new", "\uE8F1") };
            b.ReplaceNavigation(hidden); await DrainAsync();
            True(f.Navigation.Items.OfType<ShellNavigationEntry>().Any(x => x.Owner.Module == b && x.Route == "new"), "inactive owner dynamic data visible");
            Equal("\uE8F1", f.Navigation.Items.OfType<ShellNavigationEntry>().Single(x => x.Route == "new").Glyph,
                "current navigation glyph is preserved");
        });
        yield return Case("settings belongs to the current app and exposes no module selector", async () =>
        {
            var a = new ProbeModule("music"); var b = new ProbeModule("film", hasPlayer: false);
            await using var f = new Fixture([a,b]); await f.LoadAsync(); await f.NavigateAsync(a, "two");
            var before = a.Calls.Count;
            Click(f.Element<Button>("SettingsButton")); await DrainAsync();
            True(!LogicalDescendants(f.Element<Border>("WindowFrame")).OfType<ComboBox>()
                .Any(box => AutomationProperties.GetName(box) == "设置类别"), "settings has no app/module switcher");
            Equal(Visibility.Visible, Host(a.SettingsView).Visibility, "current app settings visible");
            True(LogicalTreeHelper.GetParent(a.View) is null, "main view is detached while settings is active");
            True(LogicalTreeHelper.GetParent(b.SettingsView) is null, "another owner cannot mount settings");
            Equal(0, b.InitializeCount + b.Calls.Count, "settings does not initialize another owner");
            Click(f.Back); await DrainAsync();
            Same(a, f.SelectedOwner, "original content restored"); Equal("two", f.SelectedRoute, "same route");
            Equal(before, a.Calls.Count, "no content replay");
        });
        yield return Case("global player owner can navigate from other pages but not settings", async () =>
        {
            var a = new NavigationProbe("music"); var b = new ProbeModule("reader", hasPlayer: false);
            var page = new ProbePage("public-page");
            await using var f = new Fixture([a,b], pages: [page]); await f.LoadAsync(); await f.NavigateAsync(b, "two");
            Same(a.PlayerView, f.Element<ContentControl>("PlayerHost").Content,
                "music remains the active player owner on another capability");
            a.Request("three", fromPlayer: true);
            await UntilAsync(() => f.SelectedOwner == a && f.SelectedRoute == "three" && f.Status == "",
                "global player route leaves another capability");

            await f.NavigateAsync(b, "two");
            f.Element<ListBox>("PublicNavigationList").SelectedItem = page;
            await UntilAsync(() => page.Activations == 1, "ordinary public page");
            a.Request("three", fromPlayer: true);
            await UntilAsync(() => f.SelectedOwner == a && f.SelectedRoute == "three" && f.Status == "",
                "global player route leaves an ordinary public page");
            True(LogicalTreeHelper.GetParent(page.View) is null, "player navigation retires the public page");

            await f.NavigateAsync(b, "two");
            Click(f.Element<Button>("SettingsButton")); await DrainAsync();
            var before = a.Calls.Count;
            a.Request("three", fromPlayer: true); await DrainAsync();
            Equal(before, a.Calls.Count, "settings reject player navigation");
            Equal(Visibility.Visible, Host(b.SettingsView).Visibility, "settings retain their current owner");
            Equal(Visibility.Collapsed, f.Element<Border>("PlayerFrame").Visibility,
                "settings are the only ordinary shell surface that hides the player");
            Click(f.Back); await DrainAsync();
            a.Request("undeclared", fromPlayer: true); await DrainAsync();
            False(a.Calls.Contains("route:undeclared"), "player cannot invent routes");
        });
        yield return Case("settings-only public page stays out of sidebar and remains reachable without domain restart", async () =>
        {
            var module = new ProbeModule("domain"); var hidden = new ProbePage("scripts") { ShowInSidebar = false };
            var downloads = new ProbePage("downloads");
            await using var f = new Fixture([module], pages: [downloads, hidden]); await f.LoadAsync();
            Equal(1, f.Element<ListBox>("PublicNavigationList").Items.Count, "only sidebar pages");
            Click(f.Element<Button>("SettingsButton")); await DrainAsync();
            Equal(Visibility.Collapsed, f.Element<Grid>("SearchInput").Visibility,
                "settings hide the retained global search");
            False(f.Search.IsEnabled, "settings disable search submission");
            var before = module.Calls.Count;
            f.Search.Text = "blocked"; Click(f.Button("搜索")); await DrainAsync();
            Equal(before, module.Calls.Count, "settings cannot submit global search");
            var actions = f.Element<ItemsControl>("HeaderActions");
            Equal(1, actions.Items.Count, "settings-only page is exposed by the one header");
            var item = actions.Items.Cast<ShellHeaderActionItem>().Single();
            Equal(hidden.Title, item.Label, "explicit settings entry");
            InvokeHeaderAction(f.Shell, item);
            await UntilAsync(() => hidden.Activations == 1, "settings public page activates");
            Equal(1, module.InitializeCount, "same domain instance");
            Equal(Visibility.Collapsed, f.Element<Border>("PlayerFrame").Visibility,
                "settings-owned public pages also hide the player");
            Equal(Visibility.Collapsed, f.Element<Grid>("SearchInput").Visibility,
                "settings-owned public pages also hide search");
            False(f.Search.IsEnabled, "settings-owned public pages cannot submit search");
            Click(f.Button("搜索")); await DrainAsync();
            Equal(before, module.Calls.Count, "settings-owned public page cannot target the search owner");
            Click(f.Back); await DrainAsync();
            Equal(module.Title, AutomationProperties.GetName(f.Element<Grid>("ContentDeck")),
                "returns to the same shell-owned accessible title");
            Equal(1, actions.Items.Count, "settings action restored");
            Equal(Visibility.Collapsed, f.Element<Border>("PlayerFrame").Visibility,
                "returning from a settings page remains in settings");
            Click(f.Back); await DrainAsync();
            Equal(Visibility.Visible, f.Element<Border>("PlayerFrame").Visibility,
                "leaving settings restores the retained player");
            Equal(Visibility.Visible, f.Element<Grid>("SearchInput").Visibility,
                "leaving settings restores the retained global search");
            True(f.Search.IsEnabled, "restored global search is interactive");
        });
        yield return Case("public page is not an App mode and returning retains capability state and player", async () =>
        {
            var module = new NavigationProbe("domain");
            var page = new ProbePage("public-downloads");
            await using var f = new Fixture([module], pages: [page]);
            f.Shell.PrepareForRender();
            Equal(0, page.Activations, "render is passive");
            await f.LoadAsync();
            var player = Host(module.PlayerView!); var view = Host(module.View);
            ((TextBox)module.View).Text = "retained-edit";
            f.Element<ListBox>("PublicNavigationList").SelectedItem = page;
            await UntilAsync(() => page.Activations == 1 && f.Status == "", "public activation");

            Equal(Visibility.Collapsed, ((FrameworkElement)view.Parent).Visibility, "capability surface hidden");
            Equal(Visibility.Visible, Host(page.View).Visibility, "public page visible");
            Equal(Visibility.Visible, player.Visibility, "same current player stays visible");
            Equal(Visibility.Visible, f.Element<Grid>("SearchInput").Visibility,
                "ordinary public page keeps the global search visible");
            True(f.Search.IsEnabled, "ordinary public page keeps the global search enabled");
            var before = module.Calls.Count;
            Click(f.Back); await DrainAsync();
            Equal(before, module.Calls.Count, "no capability route replay");
            Same(view, Host(module.View), "same view"); Same(player, Host(module.PlayerView!), "same player");
            Equal("retained-edit", ((TextBox)module.View).Text, "state retained");
            True(f.Search.IsEnabled, "domain search restored");
            True(LogicalTreeHelper.GetParent(page.View) is null, "ordinary public view is unmounted on exit");
            f.Element<ListBox>("PublicNavigationList").SelectedItem = page; await DrainAsync();
            Equal(2, page.Activations, "second explicit activation"); Equal(2, f.Element<Grid>("ContentDeck").Children.Count, "view mounted only once");
        });
        yield return Case("one shell header retires stale page contributions and actions", async () =>
        {
            var module = new ProbeModule("domain");
            var first = new HeaderProbePage("first"); var second = new HeaderProbePage("second");
            await using var f = new Fixture([module], pages: [first, second]); await f.LoadAsync();
            f.Element<ListBox>("PublicNavigationList").SelectedItem = first;
            await UntilAsync(() => AutomationProperties.GetName(f.Element<Grid>("ContentDeck")) == "first", "first header");
            Equal(1, LogicalDescendants(f.Element<Border>("WindowFrame")).OfType<StackPanel>().Count(x => x.Name == "PageHeader"),
                "one shell-owned header");
            var stale = f.Element<ItemsControl>("HeaderActions").Items.Cast<ShellHeaderActionItem>().Single();
            first.Publish("first-updated");
            await UntilAsync(() => AutomationProperties.GetName(f.Element<Grid>("ContentDeck")) == "first-updated",
                "same source header refresh");
            var refreshed = f.Element<ItemsControl>("HeaderActions").Items.Cast<ShellHeaderActionItem>().Single();
            InvokeHeaderAction(f.Shell, stale); await DrainAsync();
            Equal(0, first.Executions, "replaced action wrapper cannot execute");
            InvokeHeaderAction(f.Shell, refreshed);
            await UntilAsync(() => first.Executions == 1, "current action executes");
            f.Element<ListBox>("PublicNavigationList").SelectedItem = second;
            await UntilAsync(() => AutomationProperties.GetName(f.Element<Grid>("ContentDeck")) == "second", "second header");
            first.Publish("stale-title");
            InvokeHeaderAction(f.Shell, refreshed);
            await DrainAsync();
            Equal("second", AutomationProperties.GetName(f.Element<Grid>("ContentDeck")),
                "retired source cannot overwrite current title");
            Equal(1, first.Executions, "retired action cannot execute");
            Equal(0, first.HeaderSubscribers, "retired source unsubscribed");
            Equal(1, second.HeaderSubscribers, "current source subscribed once");
        });
        yield return Case("public page fences queued and live domain navigation events", async () =>
        {
            var module = new NavigationProbe("domain"); var page = new ProbePage("shared");
            await using var f = new Fixture([module], pages: [page]); await f.LoadAsync();
            module.Request("three");
            f.Element<ListBox>("PublicNavigationList").SelectedItem = page;
            module.Request("two"); await DrainAsync();
            Equal(1, module.Calls.Count, "late domain navigation ignored"); Same(page, f.Element<ListBox>("PublicNavigationList").SelectedItem, "public page retains ownership");
            await f.NavigateAsync(module, "two");
            True(f.Element<ListBox>("PublicNavigationList").SelectedItem is null, "explicit sidebar leaves public page");
            Equal("route:two", module.Calls[^1], "explicit route honored");
        });
        yield return Case("public activation cancellation fences late results and serializes reactivation", async () =>
        {
            var module = new ProbeModule("domain"); using var pending = new HeldOperation(false);
            var page = new ProbePage("shared") { Activate = pending.RunAsync };
            await using var f = new Fixture([module], pages: [page]); await f.LoadAsync();
            f.Element<ListBox>("PublicNavigationList").SelectedItem = page; await pending.Started.Task;
            Click(f.Back); await DrainAsync();
            True(pending.Token.IsCancellationRequested, "leaving cancels page");
            f.Element<ListBox>("PublicNavigationList").SelectedItem = page; await DrainAsync();
            Equal(1, page.Activations, "old real call holds page lease");
            pending.Release(); await UntilAsync(() => page.Activations == 2 && f.Status == "", "replacement activation");
            Equal(1, page.MaximumActive, "no overlapping activation");
            Equal(1, module.Calls.Count, "domain not replayed");
        });
        yield return Case("public activation failure has explicit retry and recovered page can use global search", async () =>
        {
            var module = new ProbeModule("domain"); var page = new ProbePage("shared") { Activate = _ => Task.FromException(new Exception("private-page-detail")) };
            await using var f = new Fixture([module], pages: [page]); await f.LoadAsync();
            f.Element<ListBox>("PublicNavigationList").SelectedItem = page; await UntilAsync(() => f.Status == "暂时无法完成操作", "failure");
            False(f.Status.Contains("private", StringComparison.Ordinal), "sanitized");
            page.Activate = _ => Task.CompletedTask; Click(f.Button("重试")); await UntilAsync(() => page.Activations == 2 && f.Status == "", "retry");
            f.Search.Text = "test"; Click(f.Button("搜索"));
            await UntilAsync(() => module.Calls.Contains("search:test") && f.Status == "", "global search");
            True(LogicalTreeHelper.GetParent(page.View) is null, "global search retires the public page");
            Click(f.Element<Button>("SettingsButton")); await DrainAsync();
            True(f.Element<ListBox>("PublicNavigationList").SelectedItem is null, "settings leaves public page");
            Equal(Visibility.Visible, Host(module.SettingsView).Visibility, "current domain settings");
        });
        yield return Case("forced cleanup failure retains shell and explicit retry waits for actual success", async () =>
        {
            var fail = true;
            await using var f = new Fixture([new ProbeModule("domain")], _ => Task.FromException(new Exception("save")),
                force: _ => fail ? Task.FromException(new Exception("abort")) : Task.CompletedTask);
            f.Shell.PrepareForRender(); f.Shell.Close(); await UntilAsync(() => f.Status == "关闭失败", "failed prepare");
            Click(f.Element<Button>("ForceExitButton")); await UntilAsync(() => f.Status == "清理尚未完成", "failed abort");
            False(f.Closed.Task.IsCompleted, "abort failure is not permission to close"); False(f.Shell.CleanupCompleted, "not drained");
            fail = false; Click(f.Element<Button>("ForceExitButton")); await f.WaitClosedAsync();
            True(f.Shell.CleanupCompleted, "real successful callback"); False(f.Shell.ShutdownSucceeded, "not reported saved");
            Equal(2, f.ForceCalls, "explicit retry only");
        });
        yield return Case("forced cleanup deadline does not close and late normal completion cannot overrule it", async () =>
        {
            using var normal = new HeldOperation(false); using var abort = new HeldOperation(false);
            await using var f = new Fixture([new ProbeModule("domain")], _ => normal.RunAsync(default), force: _ => abort.RunAsync(default));
            f.Shell.PrepareForRender(); f.Shell.Close(); await normal.Started.Task;
            f.Clock.Advance(TimeSpan.FromSeconds(5)); await UntilAsync(() => f.Status == "关闭超时", "normal timeout");
            Click(f.Element<Button>("ForceExitButton")); await abort.Started.Task;
            normal.Release(); f.Clock.Advance(TimeSpan.FromSeconds(5)); await UntilAsync(() => f.Status == "清理尚未完成", "force timeout");
            False(f.Closed.Task.IsCompleted, "deadline retains window"); False(f.Shell.CleanupCompleted, "actual cleanup still pending");
            abort.Release(); await DrainAsync(); False(f.Closed.Task.IsCompleted, "no unsolicited late exit");
            Click(f.Element<Button>("ForceExitButton")); await f.WaitClosedAsync(); True(f.Shell.CleanupCompleted, "drain confirmed on retry");
        });
        yield return Case("cancel failed close resumes UI and next close gets a fresh preparation", async () =>
        {
            var attempts = 0; var cancelled = 0;
            await using var f = new Fixture([new ProbeModule("domain")], _ => ++attempts == 1 ? Task.FromException(new Exception("save")) : Task.CompletedTask,
                cancel: _ => { cancelled++; return Task.CompletedTask; });
            await f.LoadAsync(); f.Shell.Close(); await UntilAsync(() => f.Status == "关闭失败", "first failure");
            Click(f.Element<Button>("CancelShutdownButton")); await UntilAsync(() => f.Status == "" && f.Search.IsEnabled, "resumed");
            Equal(1, cancelled, "one explicit rollback"); False(f.Closed.Task.IsCompleted, "resume keeps shell");
            f.Shell.Close(); await f.WaitClosedAsync(); Equal(2, attempts, "new close not cached failure"); True(f.Shell.ShutdownSucceeded, "new preparation saved");
        });
        yield return Case("failed cancellation keeps shell sealed and never implicitly forces", async () =>
        {
            await using var f = new Fixture([new ProbeModule("domain")], _ => Task.FromException(new Exception("save")),
                cancel: _ => Task.FromException(new Exception("cannot-resume")));
            f.Shell.PrepareForRender(); f.Shell.Close(); await UntilAsync(() => f.Status == "关闭失败", "failure");
            Click(f.Element<Button>("CancelShutdownButton")); await UntilAsync(() => f.Status == "无法取消关闭", "resume refused");
            False(f.Element<Button>("SettingsButton").IsEnabled, "sealed");
            Equal(0, f.ForceCalls, "no implicit abort");
            Click(f.Element<Button>("ForceExitButton")); await f.WaitClosedAsync();
        });
        yield return Case("pending close rollback has a bounded observation and explicit retry never resumes early", async () =>
        {
            using var rollback = new HeldOperation(false); var cancels = 0;
            await using var f = new Fixture([new ProbeModule("domain")], _ => Task.FromException(new Exception("save")),
                cancel: _ => { cancels++; return rollback.RunAsync(default); });
            f.Shell.PrepareForRender(); f.Shell.Close(); await UntilAsync(() => f.Status == "关闭失败", "prepare failed");
            Click(f.Element<Button>("CancelShutdownButton")); await rollback.Started.Task;
            f.Clock.Advance(TimeSpan.FromSeconds(5)); await UntilAsync(() => f.Status == "取消关闭尚未完成", "rollback deadline");
            False(f.Element<Button>("SettingsButton").IsEnabled, "timeout is not resumed");
            True(f.Element<Button>("ForceExitButton").IsEnabled, "force is not trapped by rollback");
            rollback.Release(); await DrainAsync();
            False(f.Element<Button>("SettingsButton").IsEnabled, "late rollback cannot reopen UI unrequested");
            Click(f.Element<Button>("CancelShutdownButton")); await UntilAsync(() => f.Status == "", "explicit resume");
            Equal(1, cancels, "same rollback task observed");
        });
        yield return Case("prepare mounts first supplied module without business calls", async () =>
        {
            var first = new ProbeModule("z-probe"); var second = new ProbeModule("a-probe");
            await using var f = new Fixture([first, second]);
            f.Shell.PrepareForRender();
            var mainHost = Host(first.View); var playerHost = Host(first.PlayerView!);
            f.Shell.PrepareForRender();
            Equal(0, first.InitializeCount + second.InitializeCount, "render preparation must not initialize");
            Equal(0, first.Calls.Count + second.Calls.Count, "render preparation must not navigate/search");
            Equal(Visibility.Visible, Surface(first.View).Visibility, "default module uses input order, not Id");
            Equal(1, f.Element<Grid>("ContentDeck").Children.Count, "only selected module mounted");
            Same(mainHost, Host(first.View), "idempotent main host");
            Same(playerHost, Host(first.PlayerView!), "idempotent player host");
            True(LogicalTreeHelper.GetParent(first.SettingsView) is null, "inactive settings view is not mounted");

            Equal(Visibility.Collapsed, f.Element<StackPanel>("PageHeader").Visibility,
                "header chrome collapses when the page contributes no slot or actions");
        });
        yield return Case("one player host swaps only the current player view", async () =>
        {
            var a = new ProbeModule("player-a"); var b = new ProbeModule("player-b");
            await using var f = new Fixture([a, b]); await f.LoadAsync();
            var host = f.Element<ContentControl>("PlayerHost");
            var aMainHost = Host(a.View);
            Same(a.PlayerView, host.Content, "default player in global host");
            Same(host, Host(a.PlayerView!), "default player parent");
            await f.SwitchAsync(b);
            Same(b.PlayerView, host.Content, "current player replaces host content");
            Same(host, Host(b.PlayerView!), "replacement uses same host");
            True(LogicalTreeHelper.GetParent(a.PlayerView!) is null, "old player is detached, not hidden in another host");
            True(LogicalTreeHelper.GetParent(a.View) is null && LogicalTreeHelper.GetParent(aMainHost) is null,
                "inactive capability visual tree is unmounted");
            Equal(1, f.Element<Grid>("ContentDeck").Children.Count, "only the current capability surface is mounted");
            Equal(1, LogicalDescendants(f.Element<Border>("WindowFrame")).OfType<ContentControl>().Count(x => x.Name == "PlayerHost"),
                "only one player host exists");
            await f.SwitchAsync(a);
            Same(a.PlayerView, host.Content, "original player instance restores");
            Same(aMainHost, Host(a.View), "capability owner retains the original view and host");
            True(LogicalTreeHelper.GetParent(b.PlayerView!) is null, "second player detached after switch");
        });
        yield return Case("global player owner can replace or withdraw its view while another capability is current", async () =>
        {
            var owner = new ProbeModule("player-owner");
            var viewing = new ProbeModule("playerless-view", hasPlayer: false);
            await using var f = new Fixture([owner, viewing]); await f.LoadAsync();
            var original = owner.PlayerView!;
            await f.SwitchAsync(viewing);
            var replacement = new Border
            {
                Height = 64,
                Background = Brushes.DarkOliveGreen,
                Child = new TextBlock { Text = "replacement-player" }
            };
            owner.ReplacePlayer(replacement);
            await DrainAsync();
            Same(replacement, f.Element<ContentControl>("PlayerHost").Content,
                "the retained owner refreshes its supplied player");
            True(LogicalTreeHelper.GetParent(original) is null, "the replaced player is detached");
            Equal(Visibility.Visible, f.Element<Border>("PlayerFrame").Visibility,
                "the replacement remains visible on the playerless capability");
            owner.ReplacePlayer(null);
            await DrainAsync();
            True(f.Element<ContentControl>("PlayerHost").Content is null,
                "withdrawing the owner clears the global host");
            Equal(Visibility.Collapsed, f.Element<Border>("PlayerFrame").Visibility,
                "no supplied player collapses the footer");
        });
        yield return Case("Loaded initializes selected module only and only once", async () =>
        {
            var a = new ProbeModule("probe-a"); var b = new ProbeModule("probe-b");
            await using var f = new Fixture([a, b]); await f.LoadAsync();
            f.Shell.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent)); await DrainAsync();
            Equal(1, a.InitializeCount, "duplicate Loaded must not initialize twice");
            Equal(0, b.InitializeCount, "unselected module dormant");
            Equal("route:one", a.Calls.Single(), "default route is supplied first item");
            await f.SwitchAsync(b); await f.SwitchAsync(a);
            Equal(1, a.InitializeCount, "returning module initialization retained");
            Equal(1, b.InitializeCount, "new module initialized on demand");
            Equal(2, a.Calls.Count, "explicit sidebar reentry invokes its route without reinitialization");
        });
        yield return Case("single capability accepts the ordinary sidebar contract", async () =>
        {
            var module = new ProbeModule("only-probe"); // Not IDesktopNavigationSource.
            await using var f = new Fixture([module]); f.Shell.PrepareForRender();

            await f.NavigateAsync(module, "two");
            Equal("two", f.SelectedRoute, "ordinary sidebar uses unchanged module contract");
            Equal(1, module.InitializeCount, "first real request initializes");
            Equal("route:two", module.Calls.Single(), "no implicit capability/id fallback");
        });
        yield return Case("settings hide but retain the global player for a playerless current capability", async () =>
        {
            var playing = new ProbeModule("player-capability"); var viewing = new ProbeModule("view-capability", hasPlayer: false);
            await using var f = new Fixture([playing, viewing]);
            await f.LoadAsync(); await f.SwitchAsync(viewing); await f.NavigateAsync(viewing, "two");
            ((TextBox)viewing.View).Text = "test-edit-state"; f.Search.Text = "test-query-draft";
            Same(playing.PlayerView, f.Element<ContentControl>("PlayerHost").Content,
                "the last player owner remains global");
            Same(f.Element<ContentControl>("PlayerHost"), Host(playing.PlayerView!),
                "the global player stays attached once");
            Equal(Visibility.Visible, f.Element<Border>("PlayerFrame").Visibility,
                "playerless content keeps the player visible");
            var before = viewing.Calls.Count;
            Click(f.Element<Button>("SettingsButton")); await DrainAsync();
            Equal(Visibility.Visible, Host(viewing.SettingsView).Visibility, "current capability settings shown");
            True(LogicalTreeHelper.GetParent(viewing.View) is null, "main view is detached, not retained as hidden chrome");
            True(LogicalTreeHelper.GetParent(playing.SettingsView) is null, "other player's settings not selected");
            Equal(Visibility.Collapsed, f.Element<Border>("PlayerFrame").Visibility,
                "settings hide the retained player");
            Same(playing.PlayerView, f.Element<ContentControl>("PlayerHost").Content,
                "hiding settings does not destroy the player instance");
            True(f.SelectedRoute is null, "settings clears selection so old route is clickable");
            Click(f.Back); await DrainAsync();
            Equal(before, viewing.Calls.Count, "settings back does not navigate/rebuild");
            Equal("test-edit-state", ((TextBox)viewing.View).Text, "editing state retained");
            Equal("test-query-draft", f.Search.Text, "query retained");
            Equal("two", f.SelectedRoute, "old sidebar selection restored");
            Equal(Visibility.Visible, Host(viewing.View).Visibility, "same main visible again");
            Equal(Visibility.Visible, f.Element<Border>("PlayerFrame").Visibility,
                "leaving settings restores the retained player");
        });
        yield return Case("sidebar history back follows successful routes without crossing modules", async () =>
        {
            var a = new ProbeModule("probe-a"); var b = new ProbeModule("probe-b");
            await using var f = new Fixture([a, b]); await f.LoadAsync();
            await f.NavigateAsync(a, "two"); await f.NavigateAsync(a, "three");
            True(f.Back.IsEnabled, "history enables back");
            Click(f.Back); await UntilAsync(() => f.SelectedRoute == "two", "back to second route");
            Click(f.Back); await UntilAsync(() => f.SelectedRoute == "one", "back to initial route");
            False(f.Back.IsEnabled, "root has no earlier history");
            Sequence(["route:one", "route:two", "route:three", "route:two", "route:one"], a.Calls, "history execution order");
            Equal(0, b.Calls.Count, "no fallback into hidden domain");
        });
        yield return Case("search trims input restores query history and ignores whitespace", async () =>
        {
            var a = new ProbeModule("search-probe"); await using var f = new Fixture([a]); await f.LoadAsync();
            f.Search.Text = "   "; Click(f.Button("搜索")); await DrainAsync();
            Equal(1, a.Calls.Count, "whitespace must not search");
            await f.SearchAsync(" first "); await f.SearchAsync("second");
            True(f.SelectedRoute is null, "search is not a fake sidebar route");
            Click(f.Back); await UntilAsync(() => f.Search.Text == "first" && f.Status == "", "restore first query");
            Equal("search:first", a.Calls[^1], "back replays search not route");
            Click(f.Back); await UntilAsync(() => f.SelectedRoute == "one", "back to source route");
            Sequence(["route:one", "search:first", "search:second", "search:first", "route:one"], a.Calls, "search history");
        });
        yield return Case("global search owner survives searchless capability and ordinary public page", async () =>
        {
            var music = new ProbeModule("music");
            var film = new ProbeModule("film", hasPlayer: false, supportsSearch: false);
            var page = new ProbePage("downloads");
            await using var f = new Fixture([music, film], pages: [page]); await f.LoadAsync();
            f.Search.Text = "music-route-draft";

            await f.SwitchAsync(film);
            Equal(Visibility.Visible, f.Element<Grid>("SearchInput").Visibility,
                "searchless capability keeps the global search visible");
            True(f.Search.IsEnabled, "searchless capability can submit through the retained owner");
            Equal("music-route-draft", f.Search.Text, "searchless capability presents the owner draft");
            await f.SearchAsync("from-film");
            Equal("search:from-film", music.Calls[^1], "film search enters the music search owner");
            False(film.Calls.Any(call => call.StartsWith("search:", StringComparison.Ordinal)),
                "searchless capability never receives the query");
            Equal(Visibility.Visible, Host(music.View).Visibility, "search owner becomes the current presentation");
            Click(f.Back); await UntilAsync(() => f.SelectedOwner == film && f.SelectedRoute == "one", "return to film");

            f.Element<ListBox>("PublicNavigationList").SelectedItem = page;
            await UntilAsync(() => page.Activations == 1 && f.Status == "", "ordinary public page");
            Equal(Visibility.Visible, f.Element<Grid>("SearchInput").Visibility,
                "ordinary public page keeps the same global search");
            await f.SearchAsync("from-public");
            Equal("search:from-public", music.Calls[^1], "public page search enters the retained owner");
            True(LogicalTreeHelper.GetParent(page.View) is null, "search retires the public page");
            Click(f.Back); await UntilAsync(() => f.SelectedOwner == film && f.SelectedRoute == "one",
                "return to the page behind public content");
        });
        yield return Case("search drafts are isolated by owner and route", async () =>
        {
            var a = new ProbeModule("draft-a"); var b = new ProbeModule("draft-b");
            await using var f = new Fixture([a, b]); await f.LoadAsync();
            f.Search.Text = "a-one";
            await f.NavigateAsync(a, "two");
            Equal("", f.Search.Text, "new route starts with its own draft");
            f.Search.Text = "a-two";
            await f.SwitchAsync(b);
            Equal("", f.Search.Text, "same route key in another owner cannot reuse a draft");
            f.Search.Text = "b-one";
            await f.SwitchAsync(a);
            Equal("a-one", f.Search.Text, "owner route draft restored");
            await f.NavigateAsync(a, "two");
            Equal("a-two", f.Search.Text, "second route draft restored");
            await f.SearchAsync("submitted");
            Equal("submitted", f.Search.Text, "submitted query remains visible during its search presentation");
        });
        yield return Case("dynamic navigation replacement preserves identity and removal does not invoke", async () =>
        {
            var a = new ProbeModule("dynamic-probe"); await using var f = new Fixture([a]); await f.LoadAsync();
            a.Navigation.Add(new("added", "test-added", "", "test-group"));
            await DrainAsync(); await f.NavigateAsync(a, "added"); var before = a.Calls.Count;
            a.ReplaceNavigation(new(a.Navigation.Select(item => item with { Title = item.Title + "-updated" })));
            await DrainAsync();
            Equal("added", f.SelectedRoute, "new item instance restores by exact route");
            Same(a.Navigation[^1], ((ShellNavigationEntry)f.Element<ListBox>("CollectionNavigationList").SelectedItem).Item, "selection refers to new collection item");
            Equal(before, a.Calls.Count, "replacement is not navigation intent");
            a.Navigation.RemoveAt(a.Navigation.Count - 1); await DrainAsync();
            True(f.SelectedRoute is null, "removed item no longer selected");
            Equal(before, a.Calls.Count, "removal must not choose another route");
        });
        yield return Case("internal navigation captures subscribed source and enters same history", async () =>
        {
            var a = new NavigationProbe("source-probe"); var b = new NavigationProbe("other-probe");
            await using var f = new Fixture([a, b]); await f.LoadAsync();
            await Task.Run(() => a.Request("two", sender: b));
            await UntilAsync(() => f.SelectedRoute == "two", "internal request delivered");
            Equal("route:two", a.Calls[^1], "subscribed source wins over sender");
            Equal(0, b.Calls.Count, "forged sender does not select other module");
            Click(f.Back); await UntilAsync(() => f.SelectedRoute == "one", "internal request contributed history");
            Sequence(["route:one", "route:two", "route:one"], a.Calls, "same history path as sidebar");
        });
        yield return Case("hidden module navigation events rejected without initialization", async () =>
        {
            var a = new NavigationProbe("current-probe"); var b = new NavigationProbe("hidden-probe");
            await using var f = new Fixture([a, b]); await f.LoadAsync();
            b.Request("two", sender: a); await Task.Run(() => b.Request("three", sender: null)); await DrainAsync();
            Equal(0, b.InitializeCount, "hidden event cannot activate module"); Equal(0, b.Calls.Count, "hidden requests rejected");
            Equal("one", f.SelectedRoute, "current selection unchanged"); Equal(1, a.Calls.Count, "no redirection to current module");
        });
        yield return Case("queued internal event fenced after switching away and back", async () =>
        {
            var a = new NavigationProbe("old-presentation"); var b = new ProbeModule("new-presentation");
            await using var f = new Fixture([a, b]); await f.LoadAsync();
            a.Request("three"); // Enqueued; switch synchronously before delivery.
            f.Select(b, "one"); f.Select(a, "one"); await DrainAsync();
            Equal("one", f.SelectedRoute, "old presentation cannot navigate after return");
            False(a.Calls.Contains("route:three"), "stale event not consumed");
            Equal(1, a.InitializeCount, "no reconstruction on domain return");
        });
        yield return Case("internal navigation exits settings and selects exact sidebar route", async () =>
        {
            var a = new NavigationProbe("settings-event-probe"); await using var f = new Fixture([a]); await f.LoadAsync();
            var player = Host(a.PlayerView!); Click(f.Element<Button>("SettingsButton")); await DrainAsync();
            a.Request("two"); await UntilAsync(() => f.SelectedRoute == "two", "request from settings");
            True(LogicalTreeHelper.GetParent(a.SettingsView) is null, "settings left and detached");
            Equal(Visibility.Visible, Host(a.View).Visibility, "main restored");
            Same(player, Host(a.PlayerView!), "no new player for internal navigation");
        });
        yield return Case("superseded request cancels serializes and cannot commit late success", async () =>
        {
            var a = new ProbeModule("serial-probe"); await using var f = new Fixture([a]);
            using var old = new HeldOperation(cooperative: false);
            a.NavigateHook = (route, token) => route == "slow" ? old.RunAsync(token) : Task.CompletedTask;
            await f.LoadAsync(); f.Select(a, "slow"); await old.Started.Task;
            f.Select(a, "two"); await DrainAsync();
            True(old.Token.IsCancellationRequested, "replacement cancels old request");
            False(a.Calls.Contains("route:two"), "replacement waits for actual task, not timeout wrapper");
            Equal(1, a.ActiveCalls, "one actual call holds the lease");
            old.Release(); await UntilAsync(() => f.SelectedRoute == "two", "replacement completes");
            Equal(1, a.MaximumConcurrentCalls, "same module never overlaps");
            Equal("", f.Status, "old completion cannot overwrite success status");
            Click(f.Back); await UntilAsync(() => f.SelectedRoute == "one", "back skips cancelled route");
            Sequence(["route:one", "route:slow", "route:two", "route:one"], a.Calls, "cancelled route not committed to history");
        });
        yield return Case("old failure after domain switch cannot alter current status or selection", async () =>
        {
            var a = new ProbeModule("late-failure-probe"); var b = new ProbeModule("current-probe");
            await using var f = new Fixture([a, b]); using var old = new HeldOperation(cooperative: false);
            a.NavigateHook = (route, token) => route == "slow" ? old.RunAsync(token) : Task.CompletedTask;
            await f.LoadAsync(); f.Select(a, "slow"); await old.Started.Task; await f.SwitchAsync(b);
            True(old.Token.IsCancellationRequested, "domain switch cancels old operation");
            old.Fail(); await UntilAsync(() => a.ActiveCalls == 0, "old task observed"); await DrainAsync();
            Same(b, f.SelectedOwner, "current module retained");
            Equal("one", f.SelectedRoute, "current selection retained"); Equal("", f.Status, "late exception not shown in other domain");
            await f.SwitchAsync(a); True(f.Back.IsEnabled, "unified back retains prior successful content from another module");
            False(a.Calls.TakeLast(1).Contains("route:slow"), "failed destination not restored");
        });
        yield return Case("cancel button owns status even when old task finishes later", async () =>
        {
            var a = new ProbeModule("cancel-probe"); await using var f = new Fixture([a]); using var old = new HeldOperation(false);
            a.NavigateHook = (route, token) => route == "slow" ? old.RunAsync(token) : Task.CompletedTask;
            await f.LoadAsync(); f.Search.Text = "root-draft"; f.Select(a, "slow"); await old.Started.Task;
            await DrainAsync();
            Equal(Visibility.Collapsed, f.Element<Border>("StatusPanel").Visibility, "loading does not occupy the content viewport");
            Click(f.Button("取消当前操作")); await UntilAsync(() => f.Status == "已取消", "cancel status");
            True(old.Token.IsCancellationRequested, "cancel propagates token");
            Equal("root-draft", f.Search.Text, "cancel restores the committed route draft");
            old.Release(); await UntilAsync(() => a.ActiveCalls == 0, "cancelled task released"); await DrainAsync();
            Equal("已取消", f.Status, "late success cannot clear cancellation");
            Equal("one", f.SelectedRoute, "cancel restores last committed selection"); False(f.Back.IsEnabled, "cancel does not insert history");
        });
        yield return Case("business failure sanitized and retry commits once", async () =>
        {
            var a = new ProbeModule("failure-probe"); var attempts = 0;
            a.NavigateHook = (route, _) => route == "broken" && ++attempts == 1
                ? Task.FromException(new InvalidOperationException("private-test-detail")) : Task.CompletedTask;
            await using var f = new Fixture([a]); await f.LoadAsync(); f.Select(a, "broken");
            await UntilAsync(() => f.Status == "暂时无法完成操作", "fixed failure status");
            Equal(Visibility.Visible, f.Element<Border>("StatusPanel").Visibility, "real errors and retry remain visible");
            False(f.Status.Contains("private-test-detail", StringComparison.Ordinal), "exception details never displayed");
            Equal("one", f.SelectedRoute, "failed intent not selected");
            Equal(Visibility.Visible, f.Button("重试").Visibility, "retry offered");
            Click(f.Button("重试")); await UntilAsync(() => f.SelectedRoute == "broken", "retry success");
            Equal(2, a.Calls.Count(call => call == "route:broken"), "exactly one retry");
            Click(f.Back); await UntilAsync(() => f.SelectedRoute == "one", "failed attempt not duplicated in history");
        });
        yield return Case("request deadline uses thirty second policy with virtual clock", async () =>
        {
            var a = new ProbeModule("deadline-probe"); var attempts = 0;
            await using var f = new Fixture([a]); using var old = new HeldOperation(false);
            a.NavigateHook = (route, token) => route == "slow" && ++attempts == 1 ? old.RunAsync(token) : Task.CompletedTask;
            await f.LoadAsync(); f.Select(a, "slow"); await old.Started.Task;
            f.Clock.Advance(TimeSpan.FromSeconds(30) - TimeSpan.FromTicks(1)); await DrainAsync();
            False(old.Token.IsCancellationRequested, "deadline must not fire early"); Equal("正在打开…", f.Status, "busy before deadline");
            f.Clock.Advance(TimeSpan.FromTicks(1)); await UntilAsync(() => f.Status == "操作超时", "deadline reached");
            True(old.Token.IsCancellationRequested, "timeout requests cancellation");
            Click(f.Button("重试")); await DrainAsync();
            Equal(1, a.Calls.Count(call => call == "route:slow"), "timed-out actual task retains serial lease");
            old.Release(); await UntilAsync(() => f.SelectedRoute == "slow", "queued retry completes");
            Equal(2, a.Calls.Count(call => call == "route:slow"), "one retry execution"); Equal(1, a.MaximumConcurrentCalls, "no early lease release");
        });
        yield return Case("cancelled initialization can retry without old default route", async () =>
        {
            var a = new ProbeModule("initialization-probe"); await using var f = new Fixture([a]); using var first = new HeldOperation(true);
            a.InitializeHook = token => a.InitializeCount == 1 ? first.RunAsync(token) : Task.CompletedTask;
            f.Shell.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent)); await first.Started.Task;
            Click(f.Button("取消当前操作")); await UntilAsync(() => a.ActiveCalls == 0, "initialization cancellation unwinds");
            await f.NavigateAsync(a, "two");
            Equal(2, a.InitializeCount, "failed initialization may retry");
            Sequence(["route:two"], a.Calls, "cancelled initialization cannot execute default navigation");
            Equal(1, a.MaximumConcurrentCalls, "initialization and navigation serialized");
        });
        yield return Case("navigation settings search and domain return retain view and player", async () =>
        {
            var a = new ProbeModule("retained-a"); var b = new ProbeModule("retained-b");
            await using var f = new Fixture([a, b]); await f.LoadAsync();
            var main = Host(a.View); var player = Host(a.PlayerView!);
            await f.NavigateAsync(a, "two"); Click(f.Element<Button>("SettingsButton")); await DrainAsync();
            var settings = Host(a.SettingsView);
            Same(settings, ((Grid)main.Parent).Children.OfType<ContentControl>().Single(x => !ReferenceEquals(x, main)),
                "one retained settings host belongs to the same surface");
            Click(f.Back); await DrainAsync(); await f.SearchAsync("retention-query"); await f.SwitchAsync(b); await f.SwitchAsync(a);
            Same(main, Host(a.View), "main host identity");
            True(LogicalTreeHelper.GetParent(a.SettingsView) is null, "inactive settings visual is detached");
            Same(player, Host(a.PlayerView!), "player host identity"); Same(a.View, main.Content, "main instance");
            Click(f.Element<Button>("SettingsButton")); await DrainAsync();
            Same(settings, Host(a.SettingsView), "settings host identity");
            Same(a.SettingsView, settings.Content, "settings instance");
            Click(f.Back); await DrainAsync();
            Same(a.PlayerView!, player.Content, "player instance");
            Equal(1, f.Element<Grid>("ContentDeck").Children.Count, "only the current capability surface remains mounted");
            Equal(1, a.InitializeCount, "no reinitialize");
        });
        yield return Case("Ctrl F routed keyboard event selects search without native input", async () =>
        {
            await using var f = new Fixture([new ProbeModule("keyboard-find")]);
            f.Shell.PrepareForRender(); f.Search.Text = "test-search-text"; f.Search.Select(2, 0);
            var key = RaiseKey(f.Shell, Key.F, ModifierKeys.Control, true);
            True(key.Handled, "Ctrl+F must be handled"); Equal(f.Search.Text.Length, f.Search.SelectionLength, "Ctrl+F selects all");
            Equal(0, f.Probes[0].InitializeCount, "focus command is not a business request");
            False(RaiseKey(f.Shell, Key.F, ModifierKeys.Control | ModifierKeys.Shift, true).Handled, "unassigned modifiers left alone");
        });
        yield return Case("Alt Left routes back while system chords remain unhandled", async () =>
        {
            var a = new ProbeModule("keyboard-back"); await using var f = new Fixture([a]); await f.LoadAsync(); await f.NavigateAsync(a, "two");
            True(RaiseKey(f.Shell, Key.Left, ModifierKeys.Alt, true).Handled, "Alt+Left handled");
            await UntilAsync(() => f.SelectedRoute == "one", "Alt+Left used real history");
            False(RaiseKey(f.Shell, Key.F4, ModifierKeys.Alt, true).Handled, "Alt+F4 left to system");
            False(RaiseKey(f.Shell, Key.Space, ModifierKeys.Alt, true).Handled, "Alt+Space left to system menu");
        });
        yield return Case("search Enter honors originating device modifiers and IME", async () =>
        {
            var a = new ProbeModule("keyboard-enter"); await using var f = new Fixture([a]); await f.LoadAsync(); f.Search.Text = "key-query";
            False(RaiseKey(f.Search, Key.Enter, ModifierKeys.Control, false).Handled, "Ctrl+Enter is not plain Enter");
            False(RaiseKey(f.Search, Key.ImeProcessed, ModifierKeys.None, false).Handled, "IME must not submit");
            await DrainAsync(); Equal(1, a.Calls.Count, "modified/composing keys did not search");
            True(RaiseKey(f.Search, Key.Enter, ModifierKeys.None, false).Handled, "plain Enter submitted");
            await UntilAsync(() => a.Calls.Contains("search:key-query"), "Enter routed");
            Equal(1, a.Calls.Count(call => call.StartsWith("search:", StringComparison.Ordinal)), "one search only");
        });
        yield return Case("synchronous shutdown closes once unsubscribes and does not double dispose", async () =>
        {
            var a = new NavigationProbe("close-sync"); await using var f = new Fixture([a]); await f.LoadAsync();
            Equal(1, a.NavigationSubscribers, "optional navigation subscribed"); Equal(1, a.PropertySubscribers, "property subscribed");
            f.Shell.Close(); f.Shell.Close(); await f.WaitClosedAsync();
            Equal(1, f.ShutdownCalls, "shutdown exactly once"); Equal(1, f.ClosedCount, "Closed exactly once");
            True(f.Shell.ShutdownSucceeded, "synchronous callback successful"); Equal(1, a.DisposeCount, "caller owns single disposal");
            Equal(0, a.NavigationSubscribers + a.PropertySubscribers, "subscriptions removed");
            var calls = a.Calls.Count; a.Request("two"); a.NotifyAll(); await DrainAsync();
            Equal(calls, a.Calls.Count, "closed shell rejects later events");
        });
        yield return Case("close cancels work before owner shutdown and waits for completion", async () =>
        {
            var a = new ProbeModule("close-pending"); using var work = new HeldOperation(true);
            using var shutdown = new HeldOperation(false); var observedCancellation = false;
            await using var f = new Fixture([a], async owner =>
            {
                observedCancellation = work.Token.IsCancellationRequested;
                await shutdown.RunAsync(default); await owner.DisposeProbesAsync();
            });
            a.NavigateHook = (route, token) => route == "slow" ? work.RunAsync(token) : Task.CompletedTask;
            await f.LoadAsync(); f.Select(a, "slow"); await work.Started.Task;
            f.Shell.Close(); f.Shell.Close(); await shutdown.Started.Task; await DrainAsync();
            True(observedCancellation, "cancel occurs before owner cleanup"); False(f.Closed.Task.IsCompleted, "wait within budget");
            False(f.Element<Button>("CloseButton").IsEnabled, "repeat close button disabled"); Equal(1, f.ShutdownCalls, "one callback only");
            shutdown.Release(); await f.WaitClosedAsync(); True(f.Shell.ShutdownSucceeded, "cooperative cleanup succeeds");
            Equal(1, a.DisposeCount, "owner disposal once");
        });
        yield return Case("shutdown deadline retains window until explicit force exit and fences late fault", async () =>
        {
            var a = new ProbeModule("close-deadline"); using var shutdown = new HeldOperation(false);
            await using var f = new Fixture([a], _ => shutdown.RunAsync(default));
            f.Shell.PrepareForRender(); f.Shell.Close(); await shutdown.Started.Task;
            f.Clock.Advance(TimeSpan.FromSeconds(5) - TimeSpan.FromTicks(1)); await DrainAsync();
            False(f.Shell.ShutdownCompletion.IsCompleted, "deadline must not fire early"); False(f.Closed.Task.IsCompleted, "still waiting");
            f.Shell.Close(); f.Clock.Advance(TimeSpan.FromTicks(1));
            await UntilAsync(() => f.Status == "关闭超时", "shutdown deadline exposes risk");
            False(f.Shell.ShutdownSucceeded, "timeout is not successful cleanup");
            True(f.Shell.ShutdownCompletion.IsCompleted, "initial wait is bounded");
            False(f.Closed.Task.IsCompleted, "timeout must not silently exit");
            Equal(Visibility.Visible, f.Element<StackPanel>("ShutdownConfirmation").Visibility, "visible confirmation");
            True(f.Element<Button>("WaitForShutdownButton").IsEnabled, "explicit further wait offered");
            True(f.Element<Button>("ForceExitButton").IsEnabled, "explicit destructive choice offered");
            f.Shell.Close(); await DrainAsync();
            Equal(1, f.ShutdownCalls, "repeat Close does not retry cleanup"); Equal(0, f.ClosedCount, "repeat Close is not confirmation");
            Click(f.Element<Button>("ForceExitButton")); Click(f.Element<Button>("ForceExitButton")); await f.WaitClosedAsync();
            Equal(1, f.ClosedCount, "explicit force closes once"); False(f.Shell.ShutdownSucceeded, "force is not successful cleanup");
            shutdown.Fail(); await DrainAsync(); False(f.Shell.ShutdownSucceeded, "late failure cannot change result");
            Equal(1, f.ClosedCount, "late failure cannot close again"); Equal(0, a.DisposeCount, "shell does not fake disposal");
        });
        yield return Case("shutdown synchronous failure requires confirmation before releasing subscriptions", async () =>
        {
            var a = new NavigationProbe("close-throw");
            await using var f = new Fixture([a], _ => throw new InvalidOperationException("private-test-detail"));
            f.Shell.PrepareForRender(); f.Shell.Close(); await UntilAsync(() => f.Status == "关闭失败", "failure confirmation");
            False(f.Shell.ShutdownSucceeded, "failure reported accurately"); Equal(1, f.ShutdownCalls, "no retry on failure");
            False(f.Closed.Task.IsCompleted, "failure cannot silently discard pending changes");
            Equal(2, a.NavigationSubscribers + a.PropertySubscribers, "ownership retained until actual closure");
            False(f.Element<Button>("WaitForShutdownButton").IsEnabled, "failed callback cannot be retried");
            Click(f.Element<Button>("WaitForShutdownButton")); await DrainAsync(); Equal(1, f.ShutdownCalls, "disabled action guarded");
            False(f.Status.Contains("private-test-detail", StringComparison.Ordinal), "raw detail never in status");
            Click(f.Element<Button>("ForceExitButton")); await f.WaitClosedAsync();
            Equal(0, a.NavigationSubscribers + a.PropertySubscribers, "subscriptions released only on explicit exit");
        });
        yield return Case("late shutdown success stays open until user explicitly waits on same task", async () =>
        {
            using var pending = new HeldOperation(false);
            await using var f = new Fixture([new ProbeModule("late-cleanup-success")], _ => pending.RunAsync(default));
            f.Shell.PrepareForRender(); f.Shell.Close(); await pending.Started.Task;
            f.Clock.Advance(TimeSpan.FromSeconds(5)); await UntilAsync(() => f.Status == "关闭超时", "timeout decision");
            var status = f.Status; pending.Release(); await DrainAsync();
            False(f.Closed.Task.IsCompleted, "late result alone cannot silently close");
            False(f.Shell.ShutdownSucceeded, "deadline not rewritten by unrequested continuation");
            Equal(status, f.Status, "late completion does not duplicate notifications");
            Click(f.Element<Button>("WaitForShutdownButton")); await f.WaitClosedAsync();
            True(f.Shell.ShutdownSucceeded, "explicit wait observes actual successful cleanup");
            Equal(1, f.ShutdownCalls, "completed callback reused, not reinvoked"); Equal(1, f.ClosedCount, "one exit");
        });
        yield return Case("continue waiting is bounded again and never repeats shutdown callback", async () =>
        {
            using var pending = new HeldOperation(false);
            await using var f = new Fixture([new ProbeModule("wait-again")], _ => pending.RunAsync(default));
            f.Shell.PrepareForRender(); f.Shell.Close(); await pending.Started.Task;
            f.Clock.Advance(TimeSpan.FromSeconds(5)); await UntilAsync(() => f.Status == "关闭超时", "first deadline");
            Click(f.Element<Button>("WaitForShutdownButton")); Click(f.Element<Button>("WaitForShutdownButton")); await DrainAsync();
            Equal("正在等待关闭…", f.Status, "waiting visible"); False(f.Element<Button>("WaitForShutdownButton").IsEnabled, "no parallel waits");
            True(f.Element<Button>("ForceExitButton").IsEnabled, "wait does not trap user");
            f.Clock.Advance(TimeSpan.FromSeconds(5) - TimeSpan.FromTicks(1)); await DrainAsync();
            Equal("正在等待关闭…", f.Status, "second wait not cut short");
            f.Clock.Advance(TimeSpan.FromTicks(1)); await UntilAsync(() => f.Status == "关闭超时", "second bounded wait");
            Equal(1, f.ShutdownCalls, "wait retry is not cleanup retry"); Equal(0, f.ClosedCount, "second timeout cannot exit");
            Click(f.Element<Button>("WaitForShutdownButton")); pending.Release(); await f.WaitClosedAsync();
            True(f.Shell.ShutdownSucceeded, "real completion during explicit wait succeeds"); Equal(1, f.ClosedCount, "one close");
        });
        yield return Case("force during explicit wait fences late success and all further close actions", async () =>
        {
            using var pending = new HeldOperation(false);
            await using var f = new Fixture([new ProbeModule("force-during-wait")], _ => pending.RunAsync(default));
            f.Shell.PrepareForRender(); f.Shell.Close(); await pending.Started.Task;
            f.Clock.Advance(TimeSpan.FromSeconds(5)); await UntilAsync(() => f.Status == "关闭超时", "confirmation offered");
            Click(f.Element<Button>("WaitForShutdownButton")); Click(f.Element<Button>("ForceExitButton")); await f.WaitClosedAsync();
            var status = f.Status; pending.Release(); await DrainAsync();
            Click(f.Element<Button>("ForceExitButton")); Click(f.Element<Button>("WaitForShutdownButton")); await DrainAsync();
            Equal(1, f.ClosedCount, "late completion cannot close twice"); Equal(1, f.ShutdownCalls, "no extra cleanup");
            False(f.Shell.ShutdownSucceeded, "forced exit remains distinguishable from successful cleanup");
            Equal(status, f.Status, "late results cannot mutate closed presentation");
        });
        yield return Case("cleanup fault during explicit wait reopens decision without automatic exit", async () =>
        {
            using var pending = new HeldOperation(false);
            await using var f = new Fixture([new ProbeModule("wait-fault")], _ => pending.RunAsync(default));
            f.Shell.PrepareForRender(); f.Shell.Close(); await pending.Started.Task;
            f.Clock.Advance(TimeSpan.FromSeconds(5)); await UntilAsync(() => f.Status == "关闭超时", "confirmation offered");
            Click(f.Element<Button>("WaitForShutdownButton")); pending.Fail(); await UntilAsync(() => f.Status == "关闭失败", "late fault disclosed");
            False(f.Shell.ShutdownSucceeded, "fault never reported as cleanup"); False(f.Closed.Task.IsCompleted, "fault retains window");
            False(f.Element<Button>("WaitForShutdownButton").IsEnabled, "terminal failure cannot be retried");
            False(f.Status.Contains("private-test-detail", StringComparison.Ordinal), "fixed status only");
            Click(f.Element<Button>("ForceExitButton")); await f.WaitClosedAsync(); Equal(1, f.ShutdownCalls, "one cleanup");
        });
        yield return Case("cancelled shutdown is a failed cleanup requiring force confirmation", async () =>
        {
            await using var f = new Fixture([new ProbeModule("cancelled-cleanup")], _ => Task.FromCanceled(new CancellationToken(true)));
            f.Shell.PrepareForRender(); f.Shell.Close(); await UntilAsync(() => f.Status == "关闭失败", "cancelled callback disclosed");
            False(f.Closed.Task.IsCompleted, "cleanup cancellation does not authorize exit");
            False(f.Shell.ShutdownSucceeded, "cancellation is not success");
            Click(f.Element<Button>("ForceExitButton")); await f.WaitClosedAsync(); Equal(1, f.ShutdownCalls, "one callback");
        });
        yield return Case("force exit has no effect before confirmation and frozen shell rejects other actions", async () =>
        {
            var a = new NavigationProbe("freeze-probe"); using var pending = new HeldOperation(false);
            await using var f = new Fixture([a], _ => pending.RunAsync(default)); await f.LoadAsync();
            Click(f.Element<Button>("ForceExitButton")); await DrainAsync();
            Equal(0, f.ShutdownCalls, "hidden confirmation action not armed");
            f.Shell.Close(); await pending.Started.Task; Click(f.Element<Button>("ForceExitButton")); await DrainAsync();
            False(f.Closed.Task.IsCompleted, "initial wait not bypassed by hidden button");
            f.Clock.Advance(TimeSpan.FromSeconds(5)); await UntilAsync(() => f.Status == "关闭超时", "confirmation");
            var calls = a.Calls.Count; a.Request("two"); f.Select(a, "three"); Click(f.Element<Button>("SettingsButton"));
            Click(f.Button("取消当前操作")); Click(f.Button("关闭状态")); await DrainAsync();
            Equal(calls, a.Calls.Count, "frozen shell cannot resume partially disposed domain");
            Equal("关闭超时", f.Status, "unrelated actions cannot erase shutdown risk");
            False(f.Element<Button>("ForceExitButton").IsDefault, "force is not an implicit Enter default");
            False(RaiseKey(f.Shell, Key.F, ModifierKeys.Control, true).Handled, "search shortcut blocked during shutdown");
            Click(f.Element<Button>("ForceExitButton")); await f.WaitClosedAsync();
        });
        yield return Case("Closing veto does not begin shutdown until close allowed", async () =>
        {
            await using var f = new Fixture([new ProbeModule("close-veto")]); f.Shell.PrepareForRender();
            CancelEventHandler veto = (_, e) => e.Cancel = true;
            f.Shell.Closing += veto;
            try
            {
                f.Shell.Close(); await DrainAsync();
                Equal(0, f.ShutdownCalls, "veto preserves resource ownership"); False(f.Closed.Task.IsCompleted, "veto keeps shell open");
            }
            finally { f.Shell.Closing -= veto; }
            f.Shell.Close(); await f.WaitClosedAsync(); Equal(1, f.ShutdownCalls, "allowed close starts one callback");
        });
        yield return Case("throwing cancellation registration cannot escape UI action", async () =>
        {
            var a = new ProbeModule("cancel-throw"); using var pending = new HeldOperation(true);
            a.NavigateHook = async (route, token) =>
            {
                if (route != "slow") return;
                using var registration = token.Register(() => throw new InvalidOperationException("private-test-detail"));
                await pending.RunAsync(token);
            };
            await using var f = new Fixture([a]); await f.LoadAsync(); f.Select(a, "slow"); await pending.Started.Task;
            Click(f.Button("取消当前操作")); await UntilAsync(() => a.ActiveCalls == 0, "cancellation unwinds despite registration failure");
            Equal("已取消", f.Status, "finite cancellation status"); await f.NavigateAsync(a, "two");
            Equal("two", f.SelectedRoute, "subsequent request works"); Equal(1, a.MaximumConcurrentCalls, "lease not released early");
        });
        foreach (var size in new[] { new Size(1180, 800), new Size(860, 600) })
            yield return Case($"offscreen content layout and nonempty pixels {size.Width}x{size.Height}", () => RenderContentAsync(size));
        yield return Case("module accent scoped and removed on domain switch", async () =>
        {
            var a = new ProbeModule("accent-a"); var b = new ProbeModule("accent-b"); var accent = new SolidColorBrush(Color.FromRgb(20, 90, 65));
            a.View.Resources["MeowFilm.Brush.Accent"] = accent;
            await using var f = new Fixture([a, b]); await f.LoadAsync(); var root = f.Element<Grid>("RootLayout");
            Same(accent, root.Resources["MeowFilm.Brush.Accent"], "selected-domain override"); await f.SwitchAsync(b);
            False(root.Resources.Contains("MeowFilm.Brush.Accent"), "old accent does not leak");
            foreach (var key in new[] { "MeowFilm.Button", "MeowFilm.TextBox", "MeowFilm.Heading", "MeowFilm.Surface" })
                True(f.Shell.TryFindResource(key) is Style, "reusable style: " + key);
        });
    }

    private static TestCase Case(string name, Func<Task> body) => new("desktop: " + name, () => OnStaAsync(body));
    private static async Task OnStaAsync(Func<Task> body)
    {
        var result = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher? owner = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher; Volatile.Write(ref owner, dispatcher);
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var unhandled = new List<Exception>();
            dispatcher.UnhandledException += (_, e) => { unhandled.Add(e.Exception); e.Handled = true; };
            async Task RunAsync()
            {
                Exception? failure = null;
                try
                {
                    await body(); await DrainAsync();
                    if (unhandled.Count > 0) throw new AggregateException("Unhandled dispatcher exception", unhandled);
                }
                catch (Exception error) { failure = error; }
                finally { result.TrySetResult(failure); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }
            dispatcher.BeginInvoke(new Action(() => _ = RunAsync()));
            try { Dispatcher.Run(); }
            catch (Exception error) { result.TrySetResult(error); }
            finally { stopped.TrySetResult(); }
        }) { IsBackground = true, Name = "meowfilm-desktop-code-test" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Exception? failure;
        try { failure = await result.Task.WaitAsync(TimeSpan.FromSeconds(8)); }
        catch { Volatile.Read(ref owner)?.BeginInvokeShutdown(DispatcherPriority.Send); throw; }
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static async Task DrainAsync()
    {
        for (var i = 0; i < 3; i++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }
    private static async Task UntilAsync(Func<bool> predicate, string label)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(2)) throw new InvalidOperationException("Timed out: " + label);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (!predicate()) await Task.Delay(1); // Continuation safety wait, not policy time advancement.
        }
        await DrainAsync();
    }
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
    private static void InvokeHeaderAction(ShellWindow shell, ShellHeaderActionItem item)
    {
        var button = new Button { Tag = item };
        typeof(ShellWindow).GetMethod("OnHeaderActionClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(shell, [button, new RoutedEventArgs(Button.ClickEvent)]);
    }
    private static ContentControl Host(FrameworkElement view) => LogicalTreeHelper.GetParent(view) as ContentControl
        ?? throw new InvalidOperationException("Expected retained ContentControl host");
    private static FrameworkElement Surface(FrameworkElement view) => (FrameworkElement)Host(view).Parent;
    private static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in LogicalDescendants(child)) yield return descendant;
    }
    private static KeyEventArgs RaiseKey(UIElement target, Key key, ModifierKeys modifiers, bool preview)
    {
        var device = new ProbeKeyboard(modifiers); Equal(modifiers, device.Modifiers, "explicit test keyboard modifiers");
        var args = new KeyEventArgs(device, new ProbePresentationSource(), 0, key)
        { RoutedEvent = preview ? Keyboard.PreviewKeyDownEvent : Keyboard.KeyDownEvent };
        target.RaiseEvent(args); return args;
    }
    private sealed class ProbeKeyboard(ModifierKeys modifiers) : KeyboardDevice(InputManager.Current)
    {
        protected override KeyStates GetKeyStatesFromSystem(Key key) => key switch
        {
            Key.LeftCtrl when modifiers.HasFlag(ModifierKeys.Control) => KeyStates.Down,
            Key.LeftAlt when modifiers.HasFlag(ModifierKeys.Alt) => KeyStates.Down,
            Key.LeftShift when modifiers.HasFlag(ModifierKeys.Shift) => KeyStates.Down,
            Key.LWin when modifiers.HasFlag(ModifierKeys.Windows) => KeyStates.Down,
            _ => KeyStates.None
        };
    }
    private sealed class ProbePresentationSource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = new DrawingVisual();
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }
    private static async Task RenderContentAsync(Size size)
    {
        var a = new ProbeModule("layout-probe"); await using var f = new Fixture([a]); f.Shell.PrepareForRender();
        var root = (Border)f.Shell.Content; var background = f.Shell.Background;
        // Actual shell content; no Show/HWND/product startup. Explicitly retain inherited values when detaching.
        f.Shell.Content = null; root.Resources.MergedDictionaries.Add(f.Shell.Resources); root.DataContext = f.Shell.DataContext;
        root.Background = background; TextElement.SetForeground(root, f.Shell.Foreground);
        TextElement.SetFontFamily(root, f.Shell.FontFamily); TextElement.SetFontSize(root, f.Shell.FontSize);
        DesktopTheme.SetIsHighContrast(root, DesktopTheme.GetIsHighContrast(f.Shell));
        root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout();
        var content = f.Element<Grid>("ContentDeck"); var player = f.Element<Border>("PlayerFrame");
        var contentTop = content.TranslatePoint(new Point(), root); var playerTop = player.TranslatePoint(new Point(), root);
        True(content.ActualWidth > 400 && content.ActualHeight > 200, "usable main viewport");
        True(player.ActualHeight >= 64, "player measured into own row");
        True(contentTop.Y + content.ActualHeight <= playerTop.Y + 0.5, "player must not cover content");
        Equal(203d, f.Element<Grid>("RootLayout").ColumnDefinitions[0].ActualWidth, "sidebar width");
        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var pixels = new byte[(int)size.Width * (int)size.Height * 4]; bitmap.CopyPixels(pixels, (int)size.Width * 4, 0);
        var opaque = 0; for (var index = 3; index < pixels.Length; index += 4) if (pixels[index] > 240) opaque++;
        True(opaque > size.Width * size.Height * 0.8, "nonempty rendered pixels, not just file existence");
        Equal(0, a.InitializeCount, "render did not initialize"); Equal(0, a.Calls.Count, "render did not invoke business");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public ProbeModule[] Probes { get; }
        public ManualClock Clock { get; } = new();
        public ShellWindow Shell { get; }
        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ClosedCount { get; private set; }
        public int ShutdownCalls { get; private set; }
        public int ForceCalls { get; private set; }
        public ListBox Navigation => Element<ListBox>("NavigationList");
        public TextBox Search => Element<TextBox>("SearchBox");
        public Button Back => Element<Button>("BackButton");
        public string Status => Element<TextBlock>("StatusText").Text;
        public string? SelectedRoute => (Navigation.SelectedItem as ShellNavigationEntry)?.Route ?? (Element<ListBox>("CollectionNavigationList").SelectedItem as ShellNavigationEntry)?.Route;
        public ProbeModule? SelectedOwner => ((Navigation.SelectedItem as ShellNavigationEntry) ??
            (Element<ListBox>("CollectionNavigationList").SelectedItem as ShellNavigationEntry))?.Owner.Module as ProbeModule;
        public Fixture(ProbeModule[] modules, Func<Fixture, Task>? shutdown = null, IReadOnlyList<IDesktopPage>? pages = null,
            Func<Fixture, Task>? force = null, Func<Fixture, Task>? cancel = null)
        {
            Probes = modules;
            Shell = new ShellWindow(modules, pages ?? [], async () =>
            {
                ShutdownCalls++;
                if (shutdown is not null) await shutdown(this);
                else await DisposeProbesAsync();
            }, () => { ForceCalls++; return force?.Invoke(this) ?? Task.CompletedTask; },
                cancel is null ? null : () => cancel(this), Clock);
            Shell.Closed += (_, _) => { ClosedCount++; Closed.TrySetResult(); };
        }
        public T Element<T>(string name) where T : FrameworkElement => Shell.FindName(name) as T
            ?? throw new InvalidOperationException("Missing shell element: " + name);
        public Button Button(string name) => LogicalDescendants(Shell.Content as DependencyObject ?? Element<Border>("WindowFrame"))
            .OfType<Button>().Single(button => AutomationProperties.GetName(button) == name);
        public async Task LoadAsync()
        {
            Shell.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            await UntilAsync(() => SelectedOwner == Probes[0] && SelectedRoute == "one" && Status == "", "default route loaded");
        }
        public void Select(ProbeModule module, string route)
        {
            var item = module.Navigation.Single(item => item.Route == route);
            var list = item.Group is null ? Navigation : Element<ListBox>("CollectionNavigationList");
            list.SelectedItem = list.Items.OfType<ShellNavigationEntry>().Single(entry => ReferenceEquals(entry.Owner.Module, module) && entry.Route == route);
        }
        public async Task NavigateAsync(ProbeModule module, string route)
        {
            Select(module, route);
            await UntilAsync(() => SelectedOwner == module && SelectedRoute == route && Status == "", "navigate " + route);
        }
        public async Task SwitchAsync(ProbeModule module)
        {
            Select(module, "one");
            await UntilAsync(() => SelectedOwner == module && SelectedRoute == "one" &&
                module.InitializeCount > 0 && Status == "", "switch " + module.Id);
        }
        public async Task SearchAsync(string query)
        {
            var before = Probes.Sum(probe => probe.Calls.Count(call => call.StartsWith("search:", StringComparison.Ordinal)));
            Search.Text = query; Click(Button("搜索"));
            await UntilAsync(() => Status == "" && Search.Text == query.Trim() &&
                Probes.Sum(probe => probe.Calls.Count(call => call.StartsWith("search:", StringComparison.Ordinal))) == before + 1,
                "search completion");
        }
        public async Task DisposeProbesAsync()
        {
            foreach (var probe in Probes) await probe.DisposeAsync();
        }
        public async Task WaitClosedAsync()
        {
            await Closed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Shell.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(2)); await DrainAsync();
        }
        public async ValueTask DisposeAsync()
        {
            if (!Closed.Task.IsCompleted)
            {
                Shell.Close(); Clock.Advance(TimeSpan.FromSeconds(6)); await DrainAsync();
                if (!Closed.Task.IsCompleted && Element<Button>("ForceExitButton").IsEnabled)
                    Click(Element<Button>("ForceExitButton")); // Cleanup guard, not a passing assertion.
            }
            await WaitClosedAsync();
            foreach (var probe in Probes)
            {
                Equal(0, probe.PropertySubscribers, "fixture released property subscription");
                if (probe is NavigationProbe source) Equal(0, source.NavigationSubscribers, "fixture released navigation subscription");
            }
        }
    }

    // Intentionally small contract fixtures, never supplied to a product or shipped as sample business data.
    private sealed class ProbePage(string id) : IDesktopPage
    {
        public bool ShowInSidebar { get; init; } = true;
        public string Id => id;
        public string Title => id;
        public string Glyph => "";
        public FrameworkElement View { get; } = new TextBox { Text = "synthetic-public-page" };
        public Func<CancellationToken, Task> Activate { get; set; } = _ => Task.CompletedTask;
        public int Activations { get; private set; }
        public int Active { get; private set; }
        public int MaximumActive { get; private set; }
        public async Task ActivateAsync(CancellationToken cancellationToken = default)
        {
            Activations++; Active++; MaximumActive = Math.Max(MaximumActive, Active);
            try { await Activate(cancellationToken); } finally { Active--; }
        }
    }
    private sealed class HeaderProbePage : IDesktopPage, IDesktopHeaderSource
    {
        private readonly string id;
        private string title;
        private EventHandler? headerChanged;
        public HeaderProbePage(string id)
        {
            this.id = id;
            title = id;
        }
        public string Id => id;
        public string Title => id;
        public string Glyph => "";
        public FrameworkElement View { get; } = new TextBox { Text = "synthetic-header-page" };
        public int Executions { get; private set; }
        public int HeaderSubscribers { get; private set; }
        public DesktopHeaderContribution Header => new(title, SearchVisible: false, SearchEnabled: false,
            Actions: [new("execute/" + id, "执行", token =>
            {
                token.ThrowIfCancellationRequested();
                Executions++;
                return Task.CompletedTask;
            })]);
        public event EventHandler? HeaderChanged
        {
            add { headerChanged += value; HeaderSubscribers++; }
            remove { headerChanged -= value; HeaderSubscribers--; }
        }
        public Task ActivateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
        public void Publish(string value)
        {
            title = value;
            headerChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    private sealed class SearchlessProbeView : TextBox, IDesktopHeaderSource
    {
        public SearchlessProbeView()
        {
            Text = "test-view";
            VerticalAlignment = VerticalAlignment.Top;
            MinHeight = 80;
        }
        public DesktopHeaderContribution Header =>
            new("", SearchVisible: false, SearchEnabled: false);
        public event EventHandler? HeaderChanged { add { } remove { } }
    }
    private class ProbeModule : IDesktopModule
    {
        private PropertyChangedEventHandler? _propertyChanged;
        public int PropertySubscribers { get; private set; }
        public event PropertyChangedEventHandler? PropertyChanged
        {
            add { _propertyChanged += value; PropertySubscribers++; }
            remove { _propertyChanged -= value; PropertySubscribers--; }
        }
        public string Id { get; }
        public string Title => Id;
        public string Version => "test-only";
        public ObservableCollection<NavigationItem> Navigation { get; private set; }
        public FrameworkElement View { get; }
        public FrameworkElement SettingsView { get; }
        private FrameworkElement? playerView;
        public FrameworkElement? PlayerView => playerView;
        public int InitializeCount { get; private set; }
        public int DisposeCount { get; private set; }
        public int ActiveCalls { get; private set; }
        public int MaximumConcurrentCalls { get; private set; }
        public List<string> Calls { get; } = [];
        public Func<CancellationToken, Task>? InitializeHook { get; set; }
        public Func<string, CancellationToken, Task>? NavigateHook { get; set; }
        public ProbeModule(string id, bool hasPlayer = true, bool supportsSearch = true)
        {
            Id = id;
            Navigation = new(new[] { "one", "two", "three", "slow", "broken" }.Select(route => new NavigationItem(route, "test-" + route, "")));
            View = supportsSearch
                ? new TextBox { Text = "test-view", VerticalAlignment = VerticalAlignment.Top, MinHeight = 80 }
                : new SearchlessProbeView();
            SettingsView = new TextBox { Text = "test-settings", VerticalAlignment = VerticalAlignment.Top, MinHeight = 80 };
            playerView = hasPlayer ? new Border { Height = 64, Background = Brushes.DarkSlateGray, Child = new TextBlock { Text = "test-player" } } : null;
        }
        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            InitializeCount++;
            return ExecuteAsync(() => InitializeHook?.Invoke(cancellationToken) ?? Task.CompletedTask);
        }
        public Task NavigateAsync(string route, CancellationToken cancellationToken)
        {
            Calls.Add("route:" + route);
            return ExecuteAsync(() => NavigateHook?.Invoke(route, cancellationToken) ?? Task.CompletedTask);
        }
        public Task SearchAsync(string query, CancellationToken cancellationToken)
        {
            View.Dispatcher.VerifyAccess(); cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("search:" + query); return Task.CompletedTask;
        }
        private async Task ExecuteAsync(Func<Task> action)
        {
            View.Dispatcher.VerifyAccess(); ActiveCalls++; MaximumConcurrentCalls = Math.Max(ActiveCalls, MaximumConcurrentCalls);
            try { await action(); }
            finally { ActiveCalls--; }
        }
        public void ReplaceNavigation(ObservableCollection<NavigationItem> items)
        {
            Navigation = items; _propertyChanged?.Invoke(this, new(nameof(Navigation)));
        }
        public void ReplacePlayer(FrameworkElement? player)
        {
            playerView = player;
            _propertyChanged?.Invoke(this, new(nameof(PlayerView)));
        }
        public void NotifyAll() => _propertyChanged?.Invoke(this, new(null));
        public Task PrepareShutdownAsync() => Task.CompletedTask;
        public Task CancelShutdownPreparationAsync() => Task.CompletedTask;
        public Task AbortAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() { DisposeCount++; return ValueTask.CompletedTask; }
    }
    private sealed class NavigationProbe(string id) : ProbeModule(id), IDesktopNavigationSource
    {
        private EventHandler<DesktopNavigationRequestEventArgs>? _requested;
        public int NavigationSubscribers { get; private set; }
        public event EventHandler<DesktopNavigationRequestEventArgs>? NavigationRequested
        {
            add { _requested += value; NavigationSubscribers++; }
            remove { _requested -= value; NavigationSubscribers--; }
        }
        public void Request(string route, object? sender = null, bool fromPlayer = false) => _requested?.Invoke(sender, new(route, fromPlayer));
    }
    private sealed class HeldOperation(bool cooperative) : IDisposable
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }
        public async Task RunAsync(CancellationToken token)
        {
            Token = token; Started.TrySetResult();
            if (cooperative) await _release.Task.WaitAsync(token);
            else await _release.Task;
        }
        public void Release() => _release.TrySetResult();
        public void Fail() => _release.TrySetException(new InvalidOperationException("private-test-detail"));
        public void Dispose() => Release();
    }

    // Drives the real production deadlines without sleeping or weakening their duration.
    private sealed class ManualClock : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ClockTimer> _timers = [];
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (_gate) return _ticks; }
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ArgumentNullException.ThrowIfNull(callback);
            lock (_gate)
            {
                var timer = new ClockTimer(this, callback, state); _timers.Add(timer); timer.Change(dueTime, period); return timer;
            }
        }
        public void Advance(TimeSpan amount)
        {
            if (amount < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(amount));
            long target; lock (_gate) target = checked(_ticks + amount.Ticks);
            for (var safety = 0; safety < 1000; safety++)
            {
                ClockTimer? next;
                lock (_gate)
                {
                    next = _timers.Where(timer => !timer.Disposed && timer.Due <= target).MinBy(timer => timer.Due);
                    if (next is null) { _ticks = target; return; }
                    _ticks = next.Due;
                    next.Due = next.Period == Timeout.InfiniteTimeSpan ? long.MaxValue : checked(_ticks + next.Period.Ticks);
                }
                next.Callback(next.State);
            }
            throw new InvalidOperationException("Unexpected repeating test timer");
        }
        private sealed class ClockTimer(ManualClock owner, TimerCallback callback, object? state) : ITimer
        {
            public TimerCallback Callback { get; } = callback;
            public object? State { get; } = state;
            public long Due { get; set; } = long.MaxValue;
            public TimeSpan Period { get; private set; } = Timeout.InfiniteTimeSpan;
            public bool Disposed { get; private set; }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._gate)
                {
                    if (Disposed) return false;
                    Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : checked(owner._ticks + dueTime.Ticks);
                    Period = period <= TimeSpan.Zero ? Timeout.InfiniteTimeSpan : period;
                    return true;
                }
            }
            public void Dispose() { lock (owner._gate) { Disposed = true; owner._timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void False(bool condition, string message) => True(!condition, message);
    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}: expected '{expected}', actual '{actual}'");
    }
    private static void Same(object? expected, object? actual, string message) => True(ReferenceEquals(expected, actual), message);
    private static void Sequence(IEnumerable<string> expected, IEnumerable<string> actual, string message)
    {
        if (!expected.SequenceEqual(actual)) throw new InvalidOperationException(message + ": " + string.Join(", ", actual));
    }
}
