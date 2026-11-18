using Colors = Microsoft.UI.Colors;
using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Microsoft.UI.Text;
using Windows.System;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static partial class DesktopTests
{
    private static IEnumerable<TestCase> HeaderPresentationCases()
    {
        yield return Case("search category is one accessible entry with an equal-width dropdown and no submission", async () =>
        {
            var module = new ProbeModule("search-entry"); var page = new SearchEntryProbe(module);
            await using var f = new Fixture([module], pages: [page]); await f.LoadAsync();
            var picker = f.Element<ComboBox>("SearchTargetPicker"); var input = f.Search;
            input.Text = "  草稿  ";
            True(picker.IsTabStop && input.IsTabStop, "both controls retain keyboard focus");
            Equal("搜索目标", AutomationProperties.GetName(picker), "category accessible name");
            var entry = f.Element<Grid>("SearchInput");
            True(!ChromeVisuals(entry).OfType<Button>().Any(button => AutomationProperties.GetName(button) == "搜索"),
                "magnifier is not a second search action");
            True(ChromeVisuals(entry).OfType<FontIcon>().Single().IsHitTestVisible == false,
                "magnifier defers hit testing to the complete combo entry");
            var expansion = DesktopTestUi.Expansion(picker);
            foreach (double width in new[] { 92d, 118d })
            {
                picker.Width = width; f.Shell.Root.UpdateLayout();
                expansion.Expand(); await UntilAsync(() => picker.IsDropDownOpen, "native category opens");
                var menu = VisualTreeHelper.GetOpenPopupsForXamlRoot(picker.XamlRoot).Single().Child as FrameworkElement;
                True(menu is not null, "native popup is realized"); menu!.UpdateLayout();
                True(Math.Abs(menu.ActualWidth - picker.ActualWidth) <= 1, "dropdown follows whole entry width");
                True(Math.Abs(NativeDesktopCapture.ScreenPoint(menu, new Point()).X -
                    NativeDesktopCapture.ScreenPoint(picker, new Point()).X) <= 1,
                    "dropdown left edge aligns with the whole category entry");
                var labels = ChromeVisuals(menu).OfType<TextBlock>().Where(text => text.Text.Contains("小说")).ToArray();
                True(labels.Length > 0 && labels.All(text => text.TextWrapping == TextWrapping.NoWrap &&
                    text.TextTrimming == TextTrimming.CharacterEllipsis), "long category labels remain single-line ellipsized");
                Equal("  草稿  ", input.Text, "opening categories preserves the draft");
                Equal(0, page.Submissions.Count, "expansion is not submission");
                expansion.Collapse(); await DrainAsync();
            }
            Click(f.Button("设置")); await DrainAsync();
            True(!input.IsEnabled && !picker.IsEnabled, "settings disables both search controls");
        });
        yield return Case("search submission admits only unmodified noncomposing Enter", async () =>
        {
            var module = new ProbeModule("search-entry"); var page = new SearchEntryProbe(module);
            await using var f = new Fixture([module], pages: [page]); await f.LoadAsync();
            f.Search.Text = "   "; f.Shell.TrySubmitSearch(VirtualKeyModifiers.None, false); await DrainAsync();
            Equal(0, page.Submissions.Count, "empty query cannot submit");
            f.Search.Text = "  草稿  "; await DesktopTestUi.SelectAsync(f.Element<ComboBox>("SearchTargetPicker"), page.Targets[2]);
            False(f.Shell.TrySubmitSearch(VirtualKeyModifiers.Control, false), "modified Enter does not submit");
            False(f.Shell.TrySubmitSearch(VirtualKeyModifiers.None, true), "composition does not submit");
            Equal(0, page.Submissions.Count, "composition preserves draft");
            True(f.Shell.TrySubmitSearch(VirtualKeyModifiers.None, false), "plain Enter admitted");
            await UntilAsync(() => page.Submissions.Count == 1 && f.Status == "", "search completed");
            Equal(("草稿", "novel"), page.Submissions.Single(), "query trims without changing target");
            Click(f.Button("设置")); await DrainAsync();
            False(f.Shell.TrySubmitSearch(VirtualKeyModifiers.None, false), "disabled search rejects submission");
            Equal(1, page.Submissions.Count, "settings cannot submit");
        });
        yield return Case("shared header composes different slot structures without a shell", async () =>
        {
            var navigation = new TextBox { Width = 160, Height = 36, Text = "retained navigation" };
            var center = new TextBox { MaxWidth = 430, Height = 34, MinHeight = 34, Text = "center search" };
            var host = new DesktopPageHeader { Navigation = navigation, Center = center };
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
            Rect Bounds(FrameworkElement element) => DesktopTestUi.Bounds(element, root);
            Same(slot, ((ContentControl)host.FindName("LeadingSlot")).Content, "automatic slot follows the visible title");
            True(Bounds(slot).Left > Bounds(heading).Right && Bounds(heading).Left > Bounds(navigation).Right,
                "the host measures navigation, heading and contributed structure in order");
            Same(center, ((ContentControl)host.FindName("CenterSlot")).Content, "center has its own retained slot");
            True(Bounds(center).Width > 0 && Bounds(center).Left >= Bounds(slot).Right,
                "center uses the space remaining after the leading contribution");
            Equal(new Size(120, 48), new Size(slot.ActualWidth, slot.ActualHeight), "a multi-row slot retains its own structure");
            Equal(24d, input.ActualHeight, "compact contributed input keeps its intended height");
            True(Bounds(input).Top >= Bounds((FrameworkElement)slot.Children[0]).Bottom,
                "contributed rows do not overlap");
            input.Focus(FocusState.Programmatic);
            var originalParent = ParentOf(slot);
            page.Publish(page.Header with { Title = "更新标题" });
            await DrainAsync(); LayoutHeader(root, 620);
            Same(originalParent, ParentOf(slot), "same-slot refresh does not remount controls");
            Same(input, DesktopVisual.Focused(root), "same-slot refresh retains native logical focus");
            Equal("slot draft", input.Text, "contributed editing state retained");
            var action = ChromeVisuals(host).OfType<Button>().Single();
            True(action.IsTabStop && action.UseSystemFocusVisuals, "generated action retains keyboard focus chrome");
            Equal("执行", AutomationProperties.GetName(action), "contributed accessible action name");
            Equal(FontWeights.Normal, action.FontWeight, "header action stays regular");
            Click(action); await DrainAsync(); Equal(1, executions, "real generated button executes its contribution");

            var toggle = new CheckBox { Content = "选项", Width = 88, Height = 36 };
            page.Publish(page.Header with { Slot = toggle, SlotPlacement = DesktopHeaderSlotPlacement.Trailing });
            await DrainAsync(); LayoutHeader(root, 620);
            True(ParentOf(slot) is null, "replaced custom structure is released");
            Same(toggle, ((ContentControl)host.FindName("TrailingSlot")).Content, "page selects the trailing slot");
            True(Bounds(toggle).Left > Bounds(heading).Right, "trailing content follows the measured leading group");
            True(Bounds(center).Left >= Bounds(heading).Right && Bounds(center).Right <= Bounds(toggle).Left,
                "moving the contribution to trailing preserves an independent center without overlap");

            page.Publish(page.Header with { SlotPlacement = DesktopHeaderSlotPlacement.Leading });
            await DrainAsync(); LayoutHeader(root, 420);
            Same(toggle, ((ContentControl)host.FindName("LeadingSlot")).Content, "same control can move between slots");
            True(toggle.TransformToVisual(root).TransformPoint(new Point()).X < 420, "leading content responds to the host width");
            Equal("retained navigation", navigation.Text, "page layout changes retain the supplied navigation");
            host.Retire();
            Equal(0, page.Subscribers, "explicit retirement releases the source");
            True(ParentOf(toggle) is null, "explicit retirement releases the slot");

        });
        yield return Case("header retains action buttons and bindings while current enabled states change", async () =>
        {
            var enabled = true;
            var additionalEnabled = false;
            var executions = 0;
            var additionalExecutions = 0;
            var action = new DesktopHeaderAction("action", "执行", _ => { executions++; return Task.CompletedTask; }, () => enabled);
            var second = new DesktopHeaderAction("second", "第二", _ => Task.CompletedTask);
            var additional = new DesktopHeaderAction("additional", "追加",
                _ => { additionalExecutions++; return Task.CompletedTask; }, () => additionalEnabled);
            var page = new HeaderContributionProbe("stable", new("stable", Actions: [action, second]));
            var host = new DesktopPageHeader();
            var root = HeaderRoot(host);
            host.Present(page, new("fallback"), additionalActions: [additional]);
            using var source = HeaderSource(root);
            LayoutHeader(root, 620); await DrainAsync();
            var items = (ItemsControl)host.FindName("HeaderActions");
            var buttons = ChromeVisuals(host).OfType<Button>().ToArray();
            var tags = buttons.Select(button => button.Tag).ToArray();
            Equal(3, buttons.Length, "source and additional actions are realized");
            True(buttons[0].IsEnabled && !buttons[2].IsEnabled, "initial dynamic enabled states are bound");
            var collectionChanges = 0;
            NotifyCollectionChangedEventHandler changed = (_, _) => collectionChanges++;
            ((INotifyCollectionChanged)items.ItemsSource).CollectionChanged += changed;
            for (var index = 0; index < 32; index++)
            {
                page.Publish(page.Header with { Title = "updated-" + index, Actions = [action, second] });
                host.Present(page, new("fallback"), additionalActions: [additional]);
            }
            await DrainAsync(); LayoutHeader(root, 620);
            Equal("updated-31", host.Contribution!.Title, "retaining actions still reads the latest contribution");
            Equal(0, collectionChanges, "same action references in new list containers do not reset the collection");
            for (var index = 0; index < buttons.Length; index++)
            {
                Same(buttons[index], ChromeVisuals(host).OfType<Button>().ElementAt(index), "same action keeps its realized button");
                Same(tags[index], buttons[index].Tag, "same action keeps its binding item");
            }
            foreach (var currentEnabled in new[] { false, true })
            {
                enabled = currentEnabled; additionalEnabled = !currentEnabled;
                await Task.Run(() => page.Publish(page.Header));
                await DrainAsync(); LayoutHeader(root, 620);
                Equal(enabled, buttons[0].IsEnabled, "source action enabled changes reach the retained button");
                Equal(additionalEnabled, buttons[2].IsEnabled, "additional action enabled changes reach the retained button");
                for (var index = 0; index < buttons.Length; index++)
                {
                    Same(buttons[index], ChromeVisuals(host).OfType<Button>().ElementAt(index), "enabled changes do not replace buttons");
                    Same(tags[index], buttons[index].Tag, "enabled changes do not replace binding items");
                }
                if (additionalEnabled) { Click(buttons[2]); await DrainAsync(); }
            }
            Equal(1, additionalExecutions, "enabled additional action uses the retained binding");
            enabled = false;
            True(buttons[0].IsEnabled, "a not-yet-published change has not updated the button");
            Click(buttons[0]); await DrainAsync();
            Equal(0, executions, "execution checks the latest action enabled state before admission");
            False(buttons[0].IsEnabled, "operation refresh publishes the latest disabled state");
            enabled = true; page.Publish(page.Header); await DrainAsync();
            Click(buttons[0]); await DrainAsync(); LayoutHeader(root, 620);
            Equal(1, executions, "reenabled retained button executes its action");
            Same(buttons[0], ChromeVisuals(host).OfType<Button>().First(), "operation completion retains the realized button");
            Same(tags[0], buttons[0].Tag, "operation completion retains the binding item");
            Equal(0, collectionChanges, "enabled changes and operation completion do not mutate the action collection");
            ((INotifyCollectionChanged)items.ItemsSource).CollectionChanged -= changed;
            host.Retire();
        });
        yield return Case("header action replacement reorder and removal reject old realized buttons", async () =>
        {
            var executions = 0;
            var replacementExecutions = 0;
            var secondExecutions = 0;
            var action = new DesktopHeaderAction("action", "执行", _ => { executions++; return Task.CompletedTask; });
            var replacement = new DesktopHeaderAction("action", "执行", _ => { replacementExecutions++; return Task.CompletedTask; });
            var second = new DesktopHeaderAction("second", "第二", _ => { secondExecutions++; return Task.CompletedTask; });
            var page = new HeaderContributionProbe("replace", new("replace", Actions: [action, second]));
            var host = new DesktopPageHeader();
            var root = HeaderRoot(host);
            host.Present(page, new("fallback"));
            using var source = HeaderSource(root);
            LayoutHeader(root, 620); await DrainAsync();
            var items = (ItemsControl)host.FindName("HeaderActions");
            var original = ChromeVisuals(host).OfType<Button>().ToArray();
            var originalItem = original[0].Tag;
            page.Publish(page.Header with { Actions = [replacement, second] });
            await DrainAsync(); LayoutHeader(root, 620);
            var replaced = ChromeVisuals(host).OfType<Button>().ToArray();
            False(ReferenceEquals(originalItem, replaced[0].Tag), "same id and label with a new action is a real replacement");
            foreach (var button in original) Click(button);
            await DrainAsync();
            Equal(0, executions + replacementExecutions + secondExecutions, "old buttons cannot execute old or replacement delegates");
            Click(replaced[0]); await DrainAsync();
            Equal(1, replacementExecutions, "current button executes the replacement delegate");

            page.Publish(page.Header with { Actions = [second, replacement] });
            await DrainAsync(); LayoutHeader(root, 620);
            var reordered = ChromeVisuals(host).OfType<Button>().ToArray();
            Sequence(["第二", "执行"], reordered.Select(button => (string)button.Content), "realized actions follow the new order");
            foreach (var button in replaced) Click(button);
            await DrainAsync();
            Equal(1, replacementExecutions, "pre-reorder replacement item is no longer admitted");
            Equal(0, secondExecutions, "pre-reorder second item is no longer admitted");
            Click(reordered[0]); Click(reordered[1]); await DrainAsync();
            Equal(1, secondExecutions, "first reordered button executes its exact action");
            Equal(2, replacementExecutions, "second reordered button executes its exact action");

            page.Publish(page.Header with { Actions = [replacement] });
            await DrainAsync(); LayoutHeader(root, 620);
            Equal(1, items.Items.Count, "removing an action updates the public collection");
            foreach (var button in reordered) Click(button);
            await DrainAsync();
            Equal(1, secondExecutions, "removed second action cannot execute");
            Equal(2, replacementExecutions, "old sequence items cannot execute after removal");
            var remaining = ChromeVisuals(host).OfType<Button>().Single();
            Click(remaining); await DrainAsync();
            Equal(3, replacementExecutions, "remaining current action executes");
            page.Publish(page.Header with { Actions = [] });
            await DrainAsync(); LayoutHeader(root, 620);
            Equal(0, items.Items.Count, "empty action sequence clears the public collection");
            Equal(0, ChromeVisuals(host).OfType<Button>().Count(), "empty sequence releases realized buttons");
            Click(remaining); await DrainAsync();
            Equal(3, replacementExecutions, "last removed item cannot execute");
            Equal(0, executions, "original delegate was never admitted after replacement");
            host.Retire();
        });
        yield return Case("header unload and retirement fence old realized buttons across remount", async () =>
        {
            var executions = 0;
            var action = new DesktopHeaderAction("action", "执行", _ => { executions++; return Task.CompletedTask; });
            var page = new HeaderContributionProbe("lifetime", new("lifetime", Actions: [action]));
            var host = new DesktopPageHeader();
            var root = HeaderRoot(host);
            host.Present(page, new("fallback"));
            using var source = HeaderSource(root);
            LayoutHeader(root, 620); await DrainAsync();
            var items = (ItemsControl)host.FindName("HeaderActions");
            var original = ChromeVisuals(host).OfType<Button>().Single();
            var originalItem = original.Tag;
            page.Publish(page.Header with { Title = "reloaded" });
            root.Child = null; await DrainAsync();
            Equal(0, page.Subscribers, "unload removes the source subscription");
            Equal(0, items.Items.Count, "unload releases the action collection");
            Click(original); await DrainAsync();
            Equal(0, executions, "unloaded button cannot execute");
            root.Child = host; LayoutHeader(root, 620); await DrainAsync();
            var reloaded = ChromeVisuals(host).OfType<Button>().Single();
            False(ReferenceEquals(originalItem, reloaded.Tag), "reload creates a fresh item even for the same action instance");
            Click(original); await DrainAsync();
            Equal(0, executions, "old button cannot execute in the new loaded generation");
            Click(reloaded); await DrainAsync();
            Equal(1, executions, "reloaded button executes");
            var reloadedItem = reloaded.Tag;
            page.Publish(page.Header with { Title = "returned" });
            host.Retire();
            var reads = page.Reads;
            Click(reloaded); await DrainAsync();
            Equal(reads, page.Reads, "retired queued event cannot read its old source");
            Equal(0, page.Subscribers, "retirement removes the subscription");
            Equal(0, items.Items.Count, "retirement releases the action collection");
            True(host.Contribution is null, "retired queued event cannot restore a contribution");
            Equal(1, executions, "retired button cannot execute");
            host.Present(page, new("fallback")); await DrainAsync(); LayoutHeader(root, 620);
            var returned = ChromeVisuals(host).OfType<Button>().Single();
            False(ReferenceEquals(reloadedItem, returned.Tag), "return from retirement creates a fresh item");
            Click(original); Click(reloaded); await DrainAsync();
            Equal(1, executions, "buttons from both old generations remain rejected");
            Click(returned); await DrainAsync();
            Equal(2, executions, "current returned button executes the same action");
            host.Retire();
        });
        yield return Case("shell header centers search in remaining content space and preserves contributions caption and search identity", async () =>
        {
            var executions = 0;
            var slot = new Button { Content = "筛选", Width = 72, Height = 28 };
            slot.Click += (_, _) => executions++;
            var page = new HeaderContributionProbe("layout", new("layout",
                Slot: slot, Actions: [new("action", "执行", _ => { executions++; return Task.CompletedTask; })]));
            await using var f = new Fixture([new ProbeModule("layout-domain")], pages: [page]);
            await f.LoadAsync();
            DesktopTestUi.Select(f.Element<ListView>("PublicNavigationList"), page);
            await UntilAsync(() => f.Status == "" && f.Shell.Title.EndsWith("layout", StringComparison.Ordinal), "header page");
            var search = f.Search;
            var root = f.Shell.Root;
            foreach (var width in new[] { 1110d, 860d, 1440d })
            {
                root.Width = width; root.Height = 800;
                root.Measure(new(width, 800)); root.Arrange(new Rect(0, 0, width, 800)); root.UpdateLayout();
                await DrainAsync();
                Rect Bounds(FrameworkElement element) => DesktopTestUi.Bounds(element, root);
                void Near(double expected, double actual, string label) =>
                    True(Math.Abs(expected - actual) <= 1, label + " (within layout rounding)");
                var caption = f.Element<StackPanel>("CaptionBar");
                var header = f.Element<DesktopPageHeader>("PageHeaderHost");
                var center = f.HeaderElement<ContentControl>("CenterSlot");
                var input = f.Element<Grid>("SearchInput");
                var navigation = f.Element<Grid>("SearchChrome");
                var button = ChromeVisuals(header).OfType<Button>()
                    .Single(item => AutomationProperties.GetName(item) == "执行");
                Same(navigation, header.Navigation, "back and forward share the navigation contribution");
                Same(input, header.Center, "the retained search belongs to the center contribution");
                Equal(2, navigation.Children.Count, "navigation contains only back and forward");
                True(navigation.Children.Contains(f.Back) && navigation.Children.Contains(f.Forward),
                    "both real history controls remain mounted");
                Near(430, input.MaxWidth, "search keeps the design width cap");
                Near(34, Bounds(search).Height, "search keeps the compact design height");
                Near(32, Bounds(search).Top + Bounds(search).Height / 2, "search is vertically centered in the caption");
                Near(Math.Min(430, center.ActualWidth), Bounds(input).Width, "search adapts to the available center width");
                Near(Bounds(center).Left + Bounds(center).Width / 2,
                    Bounds(input).Left + Bounds(input).Width / 2, "search is centered within the remaining content slot");
                True(Bounds(input).Width > 0 && Bounds(navigation).Left >= Bounds(f.Element<Grid>("ContentRegion")).Left &&
                    Bounds(navigation).Right <= Bounds(input).Left && Bounds(input).Right <= Bounds(slot).Left &&
                    Bounds(slot).Right <= Bounds(button).Left && Bounds(button).Right <= Bounds(caption).Left,
                    "navigation search contribution action and caption never overlap at any supported width");
                if (width == 1440) Near(430, Bounds(input).Width, "wide content reaches the design search width");
                if (width == 860) True(Bounds(input).Width < 430, "narrow content shrinks the search before the caption");
                Equal(2, caption.Children.OfType<Button>().Count(), "notification and settings remain client controls");
                foreach (var control in caption.Children.OfType<Button>()) Near(42, Bounds(control).Width, "caption button width");
                True(Bounds(caption).Right <= root.ActualWidth - 138, "client controls leave native caption-button space");
                Equal(64d, Bounds(f.Element<Grid>("ContentRegion")).Top, "compact content boundary");
                True(f.Shell.ExtendsContentIntoTitleBar, "system caption integrates the content root");
                Click(slot); Click(button); await DrainAsync();
            }
            Equal(6, executions, "contribution controls and generated actions remain executable across resize");
            Click(f.Element<Button>("SettingsButton")); await DrainAsync(); root.UpdateLayout();
            Same(search, f.Search, "settings do not replace the search instance");
            Equal(Visibility.Collapsed, f.Element<Grid>("SearchInput").Visibility, "settings hide the retained search");
            var heading = f.HeaderElement<TextBlock>("Heading");
            Equal("设置", heading.Text, "the shared component presents the settings title");
            True(heading.TransformToVisual(root).TransformPoint(new Point()).X > f.Back.TransformToVisual(root).TransformPoint(new Point(f.Back.ActualWidth, 0)).X,
                "settings heading remains beside back");
            Click(f.Back); await DrainAsync(); root.UpdateLayout();
            Same(search, f.Search, "leaving settings restores the same search");
            True(search.IsEnabled && f.Element<Grid>("SearchInput").Visibility == Visibility.Visible, "search is restored");

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
            var navigation = f.Element<ListView>("PublicNavigationList");
            DesktopTestUi.Select(navigation, first); await DrainAsync();
            var stale = f.HeaderElement<ItemsControl>("HeaderActions").Items.Cast<DesktopHeaderActionItem>().Single();
            InvokeHeaderAction(f.Shell, stale); await pending.Started.Task;
            first.Publish(first.Header with { Title = "updated" });
            DesktopTestUi.Select(navigation, second);
            True(ParentOf(slot) is null, "leaving the source detaches its contributed tree");
            Equal(0, first.Subscribers, "leaving unsubscribes before another presentation");
            DesktopTestUi.Select(navigation, first);
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
            True(ParentOf(slot) is null, "hiding releases its header slot without unmounting the content");
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
            True(ParentOf(slot) is null, "unload releases the contributed tree");
            page.Publish(page.Header with { Title = "reloaded" });
            root.Child = host; LayoutHeader(root, 620); await DrainAsync();
            Equal(1, page.Subscribers, "reload restores only one subscription");
            Equal("reloaded", host.Contribution!.Title, "reload reads the current contribution");
            Equal("persisted", slot.Text, "reload preserves page-owned slot state");
            pending.Fail(); await DrainAsync();
            Equal(0, failures, "unloaded operation cannot report a failure after reload");
            host.Retire();
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
                DesktopTestUi.Select(f.Element<ListView>("PublicNavigationList"), page); await DrainAsync();
                InvokeHeaderAction(f.Shell, f.HeaderElement<ItemsControl>("HeaderActions").Items.Cast<DesktopHeaderActionItem>().Single());
                await pending.Started.Task;
                if (boundary == "settings") { Click(f.Element<Button>("SettingsButton")); await DrainAsync(); }
                else if (boundary == "close") { f.Shell.Close(); await f.WaitClosedAsync(); }
                else { await f.Shell.AbortAsync(); await f.WaitClosedAsync(); }
                True(pending.Token.IsCancellationRequested, boundary + " cancels header work");
                Equal(0, page.Subscribers, boundary + " removes header subscriptions");
                True(ParentOf(slot) is null, boundary + " releases the slot");
                var status = f.Status;
                pending.Fail(); await DrainAsync();
                Equal(status, f.Status, "late failure cannot overwrite " + boundary + " status");
            });
    }

    private static Border HeaderRoot(DesktopPageHeader header) => new()
    { Child = header, Background = new SolidColorBrush(Colors.White), UseLayoutRounding = true };

    private static DesktopTestUi.Host HeaderSource(FrameworkElement root) => new(root);

    private static void LayoutHeader(FrameworkElement root, double width)
    {
        root.Width = width; root.Height = 64;
        root.Measure(new(width, 64)); root.Arrange(new Rect(0, 0, width, 64)); root.UpdateLayout();
    }

    private sealed class HeaderContributionView(DesktopHeaderContribution header) : TextBox, IDesktopHeaderSource
    {
        private EventHandler? changed;
        public int Subscribers { get; private set; }
        public new DesktopHeaderContribution Header => header;
        public event EventHandler? HeaderChanged
        {
            add { changed += value; Subscribers++; }
            remove { changed -= value; Subscribers--; }
        }
        public void Publish(DesktopHeaderContribution value) { header = value; changed?.Invoke(this, EventArgs.Empty); }
    }

    private sealed class SearchEntryProbe(IDesktopModule owner) : IDesktopSearchPage
    {
        public string Id => "search";
        public string Title => "搜索";
        public string Glyph => "";
        public bool ShowInSidebar => false;
        public FrameworkElement View { get; } = new Grid();
        public IReadOnlyList<DesktopSearchTarget> Targets { get; } =
            [new("music", "音乐"), new("film", "影视"), new("novel", "小说类别长项长项长项长项")];
        public string SelectedTarget { get; private set; } = "music";
        public string Query { get; private set; } = "";
        public IDesktopModule ContentOwner => owner;
        public List<(string Query, string Target)> Submissions { get; } = [];
        public Task ActivateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
        public Task SubmitAsync(string query, string target, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Query = query; SelectedTarget = target; Submissions.Add((query, target));
            return Task.CompletedTask;
        }
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
