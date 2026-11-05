using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static partial class DesktopTests
{
    private static IEnumerable<TestCase> HeaderPresentationCases()
    {
        yield return Case("shared header composes different slot structures without a shell", async () =>
        {
            var navigation = new TextBox { Width = 160, Height = 36, Text = "retained navigation" };
            var host = new DesktopPageHeader { Navigation = navigation };
            var root = HeaderRoot(host);
            var input = new TextBox { Text = "slot draft", Height = 24, MinHeight = 24, Padding = new Thickness(4, 2, 4, 2) };
            var slot = new Grid { Width = 120, Height = 48 };
            slot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            slot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            slot.Children.Add(new TextBlock { Text = "自定义结构" });
            Grid.SetRow(input, 1); slot.Children.Add(input);
            var executions = 0;
            var page = new HeaderContributionProbe("custom", new("自定义", Slot: slot,
                Actions: [new("custom", "执行", _ => { executions++; return Task.CompletedTask; }, isPrimary: true)]));
            host.Present(page, new("fallback"), headingFallback: "默认标题");
            using var source = HeaderSource(root);
            LayoutHeader(root, 620);
            await DrainAsync();
            var heading = (TextBlock)host.FindName("Heading");
            Rect Bounds(FrameworkElement element) => new(element.TranslatePoint(new Point(), root), element.RenderSize);
            Same(slot, ((ContentControl)host.FindName("LeadingSlot")).Content, "automatic slot follows the visible title");
            True(Bounds(slot).Left > Bounds(heading).Right && Bounds(heading).Left > Bounds(navigation).Right,
                "the host measures navigation, heading and contributed structure in order");
            Equal(new Size(120, 48), slot.RenderSize, "a multi-row slot retains its own structure");
            Equal(24d, input.ActualHeight, "compact contributed input keeps its intended height");
            True(Bounds(input).Top >= Bounds((FrameworkElement)slot.Children[0]).Bottom,
                "contributed rows do not overlap");
            FocusManager.SetIsFocusScope(root, true);
            FocusManager.SetFocusedElement(root, input);
            var originalParent = LogicalTreeHelper.GetParent(slot);
            page.Publish(page.Header with { Title = "更新标题" });
            await DrainAsync(); LayoutHeader(root, 620);
            Same(originalParent, LogicalTreeHelper.GetParent(slot), "same-slot refresh does not remount controls");
            Same(input, FocusManager.GetFocusedElement(root), "same-slot refresh retains native logical focus");
            Equal("slot draft", input.Text, "contributed editing state retained");
            var action = ChromeVisuals(host).OfType<Button>().Single();
            True(action.Focusable && action.FocusVisualStyle is not null, "generated action retains keyboard focus chrome");
            Equal("执行", AutomationProperties.GetName(action), "contributed accessible action name");
            Equal(TextFormattingMode.Display, TextOptions.GetTextFormattingMode(action), "header action display metrics");
            Equal(TextRenderingMode.Auto, TextOptions.GetTextRenderingMode(action), "header action automatic rendering");
            Click(action); await DrainAsync(); Equal(1, executions, "real generated button executes its contribution");

            var toggle = new CheckBox { Content = "选项", Width = 88, Height = 36 };
            page.Publish(page.Header with { Slot = toggle, SlotPlacement = DesktopHeaderSlotPlacement.Trailing });
            await DrainAsync(); LayoutHeader(root, 620);
            True(LogicalTreeHelper.GetParent(slot) is null, "replaced custom structure is released");
            Same(toggle, ((ContentControl)host.FindName("TrailingSlot")).Content, "page selects the trailing slot");
            True(Bounds(toggle).Left > Bounds(heading).Right, "trailing content follows the measured leading group");
            DesktopTheme.SetIsHighContrast(root, true); LayoutHeader(root, 620);
            action = ChromeVisuals(host).OfType<Button>().Single();
            Equal(SystemColors.HighlightTextColor, ((SolidColorBrush)action.Foreground).Color, "system action foreground");
            Equal(SystemColors.HighlightColor, ((SolidColorBrush)action.Background).Color, "system action background");
            page.Publish(page.Header with { SlotPlacement = DesktopHeaderSlotPlacement.Leading });
            await DrainAsync(); LayoutHeader(root, 420);
            Same(toggle, ((ContentControl)host.FindName("LeadingSlot")).Content, "same control can move between slots");
            True(toggle.TranslatePoint(new Point(), root).X < 420, "leading content responds to the host width");
            Equal("retained navigation", navigation.Text, "page layout changes retain the supplied navigation");
            host.Retire();
            Equal(0, page.Subscribers, "explicit retirement releases the source");
            True(LogicalTreeHelper.GetParent(toggle) is null, "explicit retirement releases the slot");
            source.RootVisual = null;
        });
        yield return Case("shell header preserves positions caption hit tests and settings search identity", async () =>
        {
            var page = new HeaderContributionProbe("layout", new("layout",
                Actions: [new("action", "执行", _ => Task.CompletedTask)]));
            await using var f = new Fixture([new ProbeModule("layout-domain")], pages: [page]);
            await f.LoadAsync();
            f.Element<ListBox>("PublicNavigationList").SelectedItem = page;
            await UntilAsync(() => f.Status == "" && f.Shell.Title.EndsWith("layout", StringComparison.Ordinal), "header page");
            var search = f.Search;
            var root = DetachChrome(f.Shell);
            using var source = HeaderSource(root);
            foreach (var width in new[] { 1110d, 860d, 1440d })
            {
                root.Width = width; root.Height = 800;
                root.Measure(new(width, 800)); root.Arrange(new Rect(0, 0, width, 800)); root.UpdateLayout();
                await DrainAsync();
                Rect Bounds(FrameworkElement element) => new(element.TranslatePoint(new Point(), root), element.RenderSize);
                void Near(double expected, double actual, string label) =>
                    True(Math.Abs(expected - actual) <= 1, label + " (within layout rounding)");
                var caption = f.Element<StackPanel>("CaptionBar");
                var button = ChromeVisuals(f.Element<DesktopPageHeader>("PageHeaderHost")).OfType<Button>()
                    .Single(item => AutomationProperties.GetName(item) == "执行");
                Near(243, Bounds(f.Back).Left, "back keeps sidebar plus existing content inset");
                Near(279, Bounds(search).Left, "retained search keeps its original left position");
                Near(14, Bounds(search).Top, "retained search keeps its original top position");
                Near(300, Bounds(search).Width, "retained search keeps its original width");
                Near(36, Bounds(search).Height, "retained search keeps its original height");
                Near(width - 180, Bounds(caption).Left, "four existing caption controls keep their position");
                Near(width - 12, Bounds(caption).Right, "caption keeps its existing right inset");
                True(Math.Abs(Bounds(button).Right - (Bounds(caption).Left - 20)) <= 1,
                    "page action retains the existing right inset before the caption");
                Equal(64d, Bounds(f.Element<Grid>("ContentRegion")).Top, "content retains the compact top boundary");
                foreach (var interactive in new FrameworkElement[] { search, f.Back, button, f.Element<Button>("MaximizeButton") })
                    True(WindowChrome.GetIsHitTestVisibleInChrome(interactive), "controls retain client hit tests");
                var hit = root.InputHitTest(new Point((Bounds(search).Right + Bounds(button).Left) / 2, 32));
                True(hit is not null && !WindowChrome.GetIsHitTestVisibleInChrome(hit), "blank header space remains draggable");
            }
            Click(f.Element<Button>("SettingsButton")); await DrainAsync(); root.UpdateLayout();
            Same(search, f.Search, "settings do not replace the search instance");
            Equal(Visibility.Collapsed, f.Element<Grid>("SearchInput").Visibility, "settings hide the retained search");
            var heading = f.HeaderElement<TextBlock>("Heading");
            Equal("设置", heading.Text, "the shared component presents the settings title");
            True(heading.TranslatePoint(new Point(), root).X > f.Back.TranslatePoint(new Point(f.Back.ActualWidth, 0), root).X,
                "settings heading remains beside back");
            Click(f.Back); await DrainAsync(); root.UpdateLayout();
            Same(search, f.Search, "leaving settings restores the same search");
            True(search.IsEnabled && f.Element<Grid>("SearchInput").Visibility == Visibility.Visible, "search is restored");
            source.RootVisual = null;
        });
        yield return Case("header generation fences queued events and unfinished actions across a return", async () =>
        {
            using var pending = new HeldOperation(false);
            var slot = new TextBox { Text = "retained slot" };
            var first = new HeaderContributionProbe("first", new("first", Slot: slot,
                Actions: [new("pending", "等待", pending.RunAsync)]));
            var second = new HeaderContributionProbe("second", new("second"));
            await using var f = new Fixture([new ProbeModule("domain")], pages: [first, second]);
            await f.LoadAsync();
            var navigation = f.Element<ListBox>("PublicNavigationList");
            navigation.SelectedItem = first; await DrainAsync();
            var stale = f.HeaderElement<ItemsControl>("HeaderActions").Items.Cast<DesktopHeaderActionItem>().Single();
            InvokeHeaderAction(f.Shell, stale); await pending.Started.Task;
            first.Publish(first.Header with { Title = "updated" });
            navigation.SelectedItem = second;
            True(LogicalTreeHelper.GetParent(slot) is null, "leaving the source detaches its contributed tree");
            Equal(0, first.Subscribers, "leaving unsubscribes before another presentation");
            navigation.SelectedItem = first;
            var reads = first.Reads;
            await DrainAsync();
            Equal(reads, first.Reads, "a queued event from the previous generation cannot refresh a returned source");
            True(pending.Token.IsCancellationRequested, "leaving cancels the in-flight header operation");
            Equal(1, first.Subscribers, "returning subscribes once");
            Same(slot, f.HeaderElement<ContentControl>("TrailingSlot").Content, "returning remounts the original slot");
            var current = f.HeaderElement<ItemsControl>("HeaderActions").Items.Cast<DesktopHeaderActionItem>().Single();
            InvokeHeaderAction(f.Shell, stale);
            pending.Fail(); await DrainAsync();
            Equal("", f.Status, "late failure cannot publish into the returned presentation");
            Same(current, f.HeaderElement<ItemsControl>("HeaderActions").Items.Cast<DesktopHeaderActionItem>().Single(),
                "late completion cannot replace the returned header actions");
            Equal("meowfilm · updated", f.Shell.Title, "current contribution keeps the window title");
        });
        yield return Case("retained module header retires while hidden and returns through opaque search restoration", async () =>
        {
            using var pending = new HeldOperation(false);
            var executions = 0;
            var slot = new TextBox { Text = "retained filter" };
            var contribution = new DesktopHeaderContribution("retained header", Slot: slot,
                Actions: [new("pending", "等待", token => { executions++; return pending.RunAsync(token); })]);
            var view = new HeaderContributionView(contribution) { Text = "retained results" };
            var owner = new ProbeModule("retained", view: view);
            var other = new ProbeModule("visible", supportsSearch: false);
            await using var f = new Fixture([owner, other]); await f.LoadAsync();
            const string location = "opaque/header-origin|retained?source=online";
            owner.NextSearchLocation = location;
            await f.SearchAsync("retained-query");
            view.Select(2, 5);
            var main = Host(view); var surface = Surface(view); var search = f.Search;
            var stale = f.HeaderElement<ItemsControl>("HeaderActions").Items.Cast<DesktopHeaderActionItem>().Single();
            InvokeHeaderAction(f.Shell, stale); await pending.Started.Task;

            await f.SwitchAsync(other);
            Same(main, Host(view), "hidden module keeps its original content host");
            Same(surface, Surface(view), "hidden module keeps its original surface");
            Equal(Visibility.Collapsed, surface.Visibility, "retained content is hidden");
            Equal(0, view.Subscribers, "hidden content does not retain header subscriptions");
            True(pending.Token.IsCancellationRequested, "hiding a retained module cancels its header action");
            True(LogicalTreeHelper.GetParent(slot) is null, "hiding releases its header slot without unmounting the content");
            view.Publish(contribution with { Title = "returned header" }); await DrainAsync();
            Equal("meowfilm · visible", f.Shell.Title, "hidden contributions cannot change the visible title");

            var before = owner.Calls.Count;
            Click(f.Back);
            await UntilAsync(() => owner.Calls.Count == before + 1 && f.Status == "", "opaque search restoration");
            Sequence(["route:one", "search:retained-query", "restore-" + location], owner.Calls,
                "header restoration does not replay the search or interpret the owner location");
            Equal(2, owner.CaptureCount, "restoration does not recapture the location");
            Same(main, Host(view), "returning keeps the content host");
            Same(surface, Surface(view), "returning keeps the content surface");
            Equal(Visibility.Visible, surface.Visibility, "returned content is visible");
            Equal("retained results", view.Text, "returning retains content");
            Equal(2, view.SelectionStart, "returning retains selection start");
            Equal(5, view.SelectionLength, "returning retains selection length");
            Same(search, f.Search, "returning keeps the global search instance");
            Equal("retained-query", search.Text, "search draft comes from the query rather than the opaque location");
            Equal(1, view.Subscribers, "returning subscribes to the header once");
            Same(slot, f.HeaderElement<ContentControl>("TrailingSlot").Content, "returning remounts the page-owned slot");
            Equal("retained filter", slot.Text, "returning retains slot editing state");
            Equal("meowfilm · returned header", f.Shell.Title, "returning reads the current contribution");
            var current = f.HeaderElement<ItemsControl>("HeaderActions").Items.Cast<DesktopHeaderActionItem>().Single();
            InvokeHeaderAction(f.Shell, stale);
            pending.Fail(); await DrainAsync();
            Equal(1, executions, "a stale action cannot execute after the same retained view returns");
            Equal("", f.Status, "late failure cannot overwrite the restored presentation");
            Same(current, f.HeaderElement<ItemsControl>("HeaderActions").Items.Cast<DesktopHeaderActionItem>().Single(),
                "late completion cannot replace the restored actions");
        });
        yield return Case("header unload cancels actions releases slots and reloads with a fresh generation", async () =>
        {
            using var pending = new HeldOperation(false);
            var slot = new TextBox { Text = "persisted" };
            var page = new HeaderContributionProbe("unload", new("unload", Slot: slot,
                Actions: [new("pending", "等待", pending.RunAsync)]));
            var host = new DesktopPageHeader();
            var root = HeaderRoot(host);
            var failures = 0; host.OperationFailed += (_, _) => failures++;
            host.Present(page, new("fallback"));
            using var source = HeaderSource(root);
            LayoutHeader(root, 620); await DrainAsync();
            Click(ChromeVisuals(host).OfType<Button>().Single()); await pending.Started.Task;
            root.Child = null; await DrainAsync();
            Equal(0, page.Subscribers, "unloaded header stops source notifications");
            True(pending.Token.IsCancellationRequested, "unloaded header cancels the action");
            True(LogicalTreeHelper.GetParent(slot) is null, "unload releases the contributed tree");
            page.Publish(page.Header with { Title = "reloaded" });
            root.Child = host; LayoutHeader(root, 620); await DrainAsync();
            Equal(1, page.Subscribers, "reload restores only one subscription");
            Equal("reloaded", host.Contribution!.Title, "reload reads the current contribution");
            Equal("persisted", slot.Text, "reload preserves page-owned slot state");
            pending.Fail(); await DrainAsync();
            Equal(0, failures, "unloaded operation cannot report a failure after reload");
            host.Retire(); source.RootVisual = null;
        });
        foreach (var boundary in new[] { "settings", "close", "abort" })
            yield return Case("header action cancellation at " + boundary, async () =>
            {
                using var pending = new HeldOperation(false);
                var slot = new TextBox();
                var page = new HeaderContributionProbe("pending", new("pending", Slot: slot,
                    Actions: [new("pending", "等待", pending.RunAsync)]));
                await using var f = new Fixture([new ProbeModule("domain")], pages: [page]);
                await f.LoadAsync();
                f.Element<ListBox>("PublicNavigationList").SelectedItem = page; await DrainAsync();
                InvokeHeaderAction(f.Shell, f.HeaderElement<ItemsControl>("HeaderActions").Items.Cast<DesktopHeaderActionItem>().Single());
                await pending.Started.Task;
                if (boundary == "settings") { Click(f.Element<Button>("SettingsButton")); await DrainAsync(); }
                else if (boundary == "close") { f.Shell.Close(); await f.WaitClosedAsync(); }
                else { await f.Shell.AbortAsync(); await f.WaitClosedAsync(); }
                True(pending.Token.IsCancellationRequested, boundary + " cancels header work");
                Equal(0, page.Subscribers, boundary + " removes header subscriptions");
                True(LogicalTreeHelper.GetParent(slot) is null, boundary + " releases the slot");
                var status = f.Status;
                pending.Fail(); await DrainAsync();
                Equal(status, f.Status, "late failure cannot overwrite " + boundary + " status");
            });
    }

    private static Border HeaderRoot(DesktopPageHeader header)
    {
        var root = new Border { Child = header, Background = Brushes.White, UseLayoutRounding = true, SnapsToDevicePixels = true };
        root.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("/MeowFilm.Desktop;component/Theme.xaml", UriKind.Relative) });
        TextOptions.SetTextFormattingMode(root, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(root, TextRenderingMode.Auto);
        return root;
    }

    private static HwndSource HeaderSource(FrameworkElement root)
    {
        var source = new HwndSource(new HwndSourceParameters("desktop-header-fixture")
        {
            WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08000080,
            PositionX = -32000, PositionY = -32000, Width = 1440, Height = 800
        });
        source.RootVisual = root;
        return source;
    }

    private static void LayoutHeader(FrameworkElement root, double width)
    {
        root.Width = width; root.Height = 64;
        root.Measure(new(width, 64)); root.Arrange(new Rect(0, 0, width, 64)); root.UpdateLayout();
    }

    private sealed class HeaderContributionView(DesktopHeaderContribution header) : TextBox, IDesktopHeaderSource
    {
        private EventHandler? changed;
        public int Subscribers { get; private set; }
        public DesktopHeaderContribution Header => header;
        public event EventHandler? HeaderChanged
        {
            add { changed += value; Subscribers++; }
            remove { changed -= value; Subscribers--; }
        }
        public void Publish(DesktopHeaderContribution value) { header = value; changed?.Invoke(this, EventArgs.Empty); }
    }

    private sealed class HeaderContributionProbe(string id, DesktopHeaderContribution header) : IDesktopPage, IDesktopHeaderSource
    {
        private EventHandler? changed;
        public string Id => id;
        public string Title => id;
        public string Glyph => "";
        public FrameworkElement View { get; } = new TextBox();
        public int Subscribers { get; private set; }
        public int Reads { get; private set; }
        public DesktopHeaderContribution Header { get { Reads++; return header; } }
        public event EventHandler? HeaderChanged
        {
            add { changed += value; Subscribers++; }
            remove { changed -= value; Subscribers--; }
        }
        public void Publish(DesktopHeaderContribution value) { header = value; changed?.Invoke(this, EventArgs.Empty); }
        public Task ActivateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
