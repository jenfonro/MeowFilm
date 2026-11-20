using System.IO;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.System;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

public sealed partial class NovelView
{
    private const string DiscoveryThemeUri = "ms-appx:///MeowFilm.Capabilities/Novel/Presentation/NovelDiscoveryTheme.xaml";
    private const string StackIcon = "M3,7 L12,3 21,7 12,11 Z M3,12 L12,16 21,12 M3,17 L12,21 21,17";
    private long shownDiscoveryFailure;
    private bool showingDiscoveryFailure, changingDiscoveryShelf;
    private NovelDiscoveryChannelBar? discoveryChannelBar;

    private Action? updateDiscovery;
    private bool discoveryBooksChanged = true;
    private readonly List<(RadioButton Choice, NovelKind[] Kinds)> discoveryChoices = [];
    private readonly Dictionary<string, DiscoveryBookView> discoveryCards = [];
    private sealed class DiscoveryBookView(NovelBook book)
    {
        internal NovelBook Book = book;
        internal FrameworkElement Element = null!;
        internal Action<NovelBook>? Update;
        internal Action? UpdateShelf;
        internal void Present(NovelBook next)
        {
            if (Book != next) { Book = next; Update?.Invoke(next); }
        }
    }
    private static (string Source, long Revision, string Url, string Cover) CoverIdentity(NovelBook book) =>
        (book.InstallationId, book.Revision, book.BookUrl, book.CoverUrl);
    private static void PlaceChild(Panel panel, UIElement child, int index)
    {
        if (index < panel.Children.Count && ReferenceEquals(panel.Children[index], child)) return;
        panel.Children.Remove(child); panel.Children.Insert(index, child);
    }
    private void PresentDiscovery() => updateDiscovery?.Invoke();
    private void AddDiscoveryTheme() => Resources.MergedDictionaries.Add(new()
        { Source = new Uri(DiscoveryThemeUri) });

    private static TextBlock DiscoveryText(string value, double size = 12, bool muted = false)
    {
        var text = NovelUi.Text(value, size, muted);
        text.Margin = new(0); text.TextWrapping = TextWrapping.NoWrap; text.TextTrimming = TextTrimming.CharacterEllipsis;
        DesktopTheme.Observe(text, () => text.Foreground = DesktopTheme.GetIsHighContrast(text)
            ? new SolidColorBrush(new global::Windows.UI.ViewManagement.UISettings().UIElementColor(global::Windows.UI.ViewManagement.UIElementType.WindowText))
            : (Brush)DesktopVisual.Resource(text, muted ? "MeowFilm.Brush.Muted" : "MeowFilm.Brush.Text"));
        return text;
    }
    private static void DiscoveryCanvas(Border card)
    {
        DesktopTheme.Observe(card, () =>
        {
            if (DesktopTheme.GetIsHighContrast(card))
                card.Background = new SolidColorBrush(new global::Windows.UI.ViewManagement.UISettings().UIElementColor(global::Windows.UI.ViewManagement.UIElementType.Window));
            else card.ClearValue(Border.BackgroundProperty);
        });
    }
    private static Viewbox DiscoveryIcon(string data, double size = 14)
    {
        var path = new Microsoft.UI.Xaml.Shapes.Path { Data = NovelPresentation.Geometry(data), StrokeThickness = 1.4,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        path.UseResource(StyleProperty, "Novel.Discovery.Icon");
        var canvas = new Canvas { Width = 24, Height = 24 }; canvas.Children.Add(path);
        return new Viewbox { Width = size, Height = size, Child = canvas, VerticalAlignment = VerticalAlignment.Center };
    }
    private static Button DiscoveryButton(string name, Action action, string style = "Novel.Discovery.Action")
    {
        var button = NovelUi.Button(name, action, style: style);
        button.ClearValue(MarginProperty); button.ClearValue(Control.PaddingProperty);
        return button;
    }
    private FrameworkElement DiscoveryCover(NovelBook book, double width, double height, bool scale = false)
    {
        var image = new NovelImage(module.Sources, book) { Width = width, Height = height };
        FrameworkElement content = image;
        bool missing = book.CoverUrl.Length == 0;
        if (missing)
        {
            var placeholder = new Grid { Width = width, Height = height };
            var label = DiscoveryText("暂无封面", 12, true);
            label.HorizontalAlignment = HorizontalAlignment.Center; label.VerticalAlignment = VerticalAlignment.Center;
            placeholder.Children.Add(label); placeholder.Children.Add(image);
            content = placeholder;
        }
        if (scale) content = new Viewbox { Child = content, Stretch = Stretch.Uniform };
        var border = new Border { Child = content, CornerRadius = new(3) };
        if (missing) border.UseResource(StyleProperty, "Novel.Discovery.Surface");
        return border;
    }
    private string DiscoverySourceName(NovelBook book) =>
        module.Sources.Sources.FirstOrDefault(s => s.InstallationId == book.InstallationId)?.Name
        ?? NovelJson.Text(book.Data, "originName");
    private static string DiscoveryMetadata(NovelBook book) => string.Join(" · ",
        new[] { NovelJson.Text(book.Data, "kind"), NovelJson.Text(book.Data, "wordCount") }.Where(s => s.Length > 0));
    private bool IsOnShelf(NovelBook book) => module.Library.Entries.Any(e => e.OnShelf && e.Book.SourceIdentity == book.SourceIdentity);
    private async Task ShelfDiscoveryBookAsync(NovelBook book, bool add, CancellationToken token)
    {
        if (changingDiscoveryShelf) return;
        changingDiscoveryShelf = true;
        try
        {
            string identity = await module.Rules.IdentityAsync(book, token);
            token.ThrowIfCancellationRequested();
            var entry = module.Library.Find(book, identity);
            if (entry is not null)
            {
                if (entry.OnShelf != add) await module.Library.SetShelfAsync(entry.BookId, add, token);
            }
            else if (add)
                await module.Library.ToggleShelfAsync(new(Guid.NewGuid().ToString("N"), identity, book), token);
            module.Notify(add ? "已加入书架" : "已移出书架");
        }
        finally { changingDiscoveryShelf = false; }
    }
    private async Task ReadDiscoveryBookAsync(NovelBook book, CancellationToken token)
    {
        await module.ReceiveBookAsync(book, token);
        token.ThrowIfCancellationRequested();
        await module.StartReadingAsync(token);
    }
    private FrameworkElement DiscoveryTools(out ComboBox sources, out Action updateLoading)
    {
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Height = 44, VerticalAlignment = VerticalAlignment.Top };
        var sourceBox = sources = new ComboBox { Width = 154, MaxDropDownHeight = 300, Margin = new(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        sourceBox.UseResource(StyleProperty, "Novel.Discovery.Source");
        AutomationProperties.SetName(sourceBox, "发现书源"); AutomationProperties.SetAutomationId(sourceBox, "novel.discovery.source");
        sourceBox.SelectionChanged += (_, _) =>
        {
            if (!presentingDiscovery && sourceBox.SelectedItem is NovelSource source && source != module.ExploreSource && NovelCapability.CanExplore(source))
                module.RunUi(ct => module.LoadDiscoveryAsync(source, null, true, ct));
        };
        tools.Children.Add(sourceBox);
        var refresh = DiscoveryButton("刷新", () =>
        {
            if (module.LoadingDiscovery) module.CancelDiscovery();
            else module.RunUi(ct => module.LoadDiscoveryAsync(module.ExploreSource, module.ExploreKind, true, ct));
        });
        refresh.Width = 30;
        bool? loading = null;
        updateLoading = () =>
        {
            if (loading == module.LoadingDiscovery) return;
            loading = module.LoadingDiscovery;
            string name = loading == true ? "取消加载" : "刷新";
            AutomationProperties.SetName(refresh, name); ToolTipService.SetToolTip(refresh, name);
            refresh.Content = DiscoveryIcon(loading == true ? "M6,6 L18,18 M6,18 L18,6" : "M20,7 A9,9 0 1 0 21,15 M20,2 L20,7 15,7", 18);
        };
        tools.Children.Add(refresh); return tools;
    }
    private bool presentingDiscovery;
    private FrameworkElement DiscoveryTabs(IEnumerable<NovelKind> kinds)
    {
        var tabs = new List<RadioButton>();
        foreach (var kind in kinds)
        {
            tabs.Add(DiscoveryTab(kind.Title, kind, module.ExploreKind?.Url == kind.Url,
                "novel.discovery.kind:" + kind.Url));
        }
        return discoveryChannelBar = new(tabs);
    }
    private RadioButton DiscoveryTab(string title, NovelKind kind, bool selected, string id)
    {
        var tab = new RadioButton { Content = title, IsChecked = selected, GroupName = "novel.discovery.channels" };
        discoveryChoices.Add((tab, [kind]));
        tab.UseResource(StyleProperty, "Novel.Discovery.Tab");
        AutomationProperties.SetName(tab, title); AutomationProperties.SetAutomationId(tab, id);
        var route = module.Route;
        var source = module.ExploreSource;
        tab.Checked += (_, _) =>
        {
            if (presentingDiscovery) return;
            // Finish the checked/overflow gesture before publishing a replacement page.
            tab.DispatcherQueue.TryEnqueue(() =>
            {
                if (!presentingDiscovery && module.Route == route && module.ExploreSource?.InstallationId == source?.InstallationId && DesktopVisual.Contains(body, tab) &&
                    tab.IsChecked == true && kind.Url != module.ExploreKind?.Url)
                    module.RunUi(ct => module.LoadDiscoveryAsync(source, kind, false, ct));
            });
        };
        return tab;
    }
    private FrameworkElement DiscoveryChannels(IReadOnlyList<NovelDiscoveryGroup> groups)
    {
        var tabs = new List<RadioButton>();
        foreach (var group in groups)
        {
            if (string.IsNullOrWhiteSpace(group.Title))
            {
                foreach (var kind in group.Kinds)
                    tabs.Add(DiscoveryTab(kind.Title, kind, module.ExploreKind?.Url == kind.Url,
                        "novel.discovery.kind:" + kind.Url));
                continue;
            }
            var entry = group.Kinds.FirstOrDefault(kind => kind.Url == module.ExploreKind?.Url) ?? group.Kinds[0];
            var tab = DiscoveryTab(group.Title, entry, group.Kinds.Any(kind => kind.Url == module.ExploreKind?.Url),
                "novel.discovery.group:" + group.Kinds[0].Url);
            discoveryChoices[^1] = (tab, group.Kinds.ToArray()); tabs.Add(tab);
        }
        return discoveryChannelBar = new(tabs);
    }
    private static ScrollViewer DiscoveryNavigation(Panel choices)
    {
        var scroll = new ScrollViewer { Content = choices, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Left };
        scroll.Resources.MergedDictionaries.Add(new()
            { Source = new Uri("ms-appx:///MeowFilm.Desktop/SettingsScrollChrome.xaml") });
        scroll.UseResource(StyleProperty, "MeowFilm.SettingsScrollViewer");
        return scroll;
    }
    private static void DiscoveryNavigation_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not RadioButton { Parent: Panel panel } current ||
            e.Key is not (VirtualKey.Home or VirtualKey.End or VirtualKey.Left or VirtualKey.Right)) return;
        var choices = panel.Children.OfType<RadioButton>().ToArray();
        int index = Array.IndexOf(choices, current);
        int next = e.Key switch { VirtualKey.Home => 0, VirtualKey.End => choices.Length - 1,
            VirtualKey.Left => (index + choices.Length - 1) % choices.Length, _ => (index + 1) % choices.Length };
        choices[next].Focus(FocusState.Programmatic); choices[next].IsChecked = true; choices[next].StartBringIntoView(); e.Handled = true;
    }
    private FrameworkElement BuildDiscovery(bool categories)
    {
        discoveryCards.Clear(); discoveryChoices.Clear(); discoveryBooksChanged = true;
        var panel = new StackPanel { MaxWidth = 1100 };
        AutomationProperties.SetName(panel, categories ? "小说分类" : "小说首页");
        var heading = new Grid { MinHeight = 44 };
        heading.ColumnDefinitions.Add(new()); heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var tools = DiscoveryTools(out var sources, out var updateLoading); Grid.SetColumn(tools, 1); heading.Children.Add(tools);
        var channels = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        heading.Children.Add(channels); panel.Children.Add(DiscoveryRule(heading));
        var filters = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch }; panel.Children.Add(filters);
        var feature = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        if (!categories)
        {
            panel.Children.Add(feature);
            var all = DiscoveryButton("全部分类", () => module.Request(NovelCapability.CategoriesRoute));
            var label = new StackPanel { Orientation = Orientation.Horizontal };
            label.Children.Add(new TextBlock { Text = "全部分类", VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 8, 0) });
            label.Children.Add(DiscoveryIcon("M10,6 L16,12 10,18"));
            all.Content = label; all.HorizontalAlignment = HorizontalAlignment.Right;
            all.Margin = new(0, 25, 0, 16); panel.Children.Add(all);
        }
        var books = new NovelBookGrid(categories ? 3 : 6, categories ? 310 : 138, categories ? 24 : 16, 22);
        AutomationProperties.SetName(books, categories ? "分类结果" : "发现书籍"); panel.Children.Add(books);
        var status = DiscoveryText("", 12, true); status.Margin = new(0, 18, 0, 0);
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite); panel.Children.Add(status);
        var retry = DiscoveryButton("重试", () => module.RunUi(ShowDiscoveryRetryAsync)); panel.Children.Add(retry);
        var import = DiscoveryButton("导入书源", () => module.RunUi(ct => module.ImportSourcesAsync(this, ct))); panel.Children.Add(import);
        var more = DiscoveryButton("加载更多", () => module.RunUi(ct => module.LoadDiscoveryAsync(module.ExploreSource, module.ExploreKind, false, ct)));
        more.Margin = new(0, 18, 0, 0); more.HorizontalAlignment = HorizontalAlignment.Center; panel.Children.Add(more);
        IReadOnlyList<NovelSource>? displayedSources = null;
        NovelSource? displayedSource = null;
        NovelKind[]? displayedKinds = null;
        IReadOnlyList<NovelKind>? displayedKindsInput = null;
        (string? Id, long? Revision)? channelSource = null;
        IReadOnlyList<NovelDiscoveryGroup> groups = [];
        string? displayedFilter = null;
        string? displayedKind = null;
        bool showBooks = categories;
        IReadOnlyList<NovelEntry>? displayedEntries = null;
        HashSet<string> displayedShelf = [];
        DiscoveryBookView? featured = null;
        Action? updateContinuation = null;
        var featuredOwner = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        if (!categories)
        {
            feature.Content = DiscoveryFeatureRow(featuredOwner, DiscoveryContinuation(out updateContinuation));
            feature.Visibility = Visibility.Collapsed;
        }
        bool? displayedBooks = null;
        var frame = new Grid { Margin = new(32, 12, 32, 16) }; frame.Children.Add(panel);
        var scroll = NovelUi.Scroll(frame); scroll.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        updateDiscovery = () =>
        {
            presentingDiscovery = true;
            try
            {
                bool sourceChanged = !ReferenceEquals(displayedSources, module.Sources.Sources) || displayedSource != module.ExploreSource;
                if (sourceChanged)
                {
                    displayedSources = module.Sources.Sources; displayedSource = module.ExploreSource;
                    var choices = module.Sources.Sources.Where(NovelCapability.CanExplore).ToList();
                    if (module.ExploreSource is { } selected && !choices.Any(s => s.InstallationId == selected.InstallationId)) choices.Insert(0, selected);
                    sources.ItemsSource = choices; sources.SelectedItem = module.ExploreSource;
                }
                bool kindsChanged = displayedKinds is null ||
                    !ReferenceEquals(displayedKindsInput, module.Kinds) && !displayedKinds.SequenceEqual(module.Kinds) ||
                    channelSource != (module.ExploreSource?.InstallationId, module.ExploreSource?.Revision);
                displayedKindsInput = module.Kinds;
                if (kindsChanged)
                {
                    channelSource = (module.ExploreSource?.InstallationId, module.ExploreSource?.Revision);
                    displayedKinds = module.Kinds.ToArray(); groups = NovelDiscoveryKinds.Groups(module.Kinds, rankingsOnly: !categories);
                    discoveryChoices.Clear(); discoveryChannelBar?.Close();
                    channels.Content = categories ? DiscoveryChannels(groups) : DiscoveryTabs(groups.SelectMany(group => group.Kinds));
                    displayedFilter = null;
                }
                if (kindsChanged || displayedKind != module.ExploreKind?.Url)
                {
                    displayedKind = module.ExploreKind?.Url;
                    var current = groups.FirstOrDefault(group => group.Kinds.Any(kind => kind.Url == displayedKind));
                    string filter = categories && current is not null && !string.IsNullOrWhiteSpace(current.Title) ? current.Kinds[0].Url : "";
                    if (displayedFilter != filter)
                    {
                        discoveryChoices.RemoveAll(choice => choice.Choice.GroupName == "novel.discovery.kinds");
                        filters.Content = filter.Length > 0 ? DiscoveryFilters(current!) : null; displayedFilter = filter;
                    }
                    foreach (var choice in discoveryChoices)
                    {
                        bool selected = choice.Kinds.Any(kind => kind.Url == displayedKind);
                        if (choice.Choice.IsChecked != selected) choice.Choice.IsChecked = selected;
                    }
                    showBooks = categories || groups.Any(group => group.Kinds.Any(kind => kind.Url == displayedKind));
                }
                if (discoveryBooksChanged || displayedBooks != showBooks || sourceChanged)
                {
                    discoveryBooksChanged = false; displayedBooks = showBooks;
                    var desired = showBooks ? module.DiscoverBooks.ToArray() : [];
                    var identities = desired.Select(book => book.SourceIdentity).ToHashSet(StringComparer.Ordinal);
                    foreach (var key in discoveryCards.Keys.Where(key => !identities.Contains(key)).ToArray())
                    { books.Children.Remove(discoveryCards[key].Element); discoveryCards.Remove(key); }
                    for (int index = 0; index < desired.Length; index++)
                    {
                        var book = desired[index];
                        if (!discoveryCards.TryGetValue(book.SourceIdentity, out var card))
                        {
                            card = categories ? DiscoveryCatalogBook(book) : DiscoveryTile(book);
                            discoveryCards.Add(book.SourceIdentity, card);
                        }
                        card.Present(book); if (sourceChanged) card.Update?.Invoke(book); card.UpdateShelf?.Invoke(); PlaceChild(books, card.Element, index);
                    }
                    if (!categories)
                    {
                        if (desired.FirstOrDefault() is not { } first)
                        { featuredOwner.Content = null; featured = null; feature.Visibility = Visibility.Collapsed; }
                        else if (featured is null || featured.Book.SourceIdentity != first.SourceIdentity)
                        {
                            featured = DiscoveryFeaturedBook(first);
                            featuredOwner.Content = featured.Element; feature.Visibility = Visibility.Visible;
                        }
                        else { featured.Present(first); if (sourceChanged) featured.Update?.Invoke(first); }
                    }
                }
                if (!ReferenceEquals(displayedEntries, module.Library.Entries))
                {
                    displayedEntries = module.Library.Entries;
                    var shelf = module.Library.Entries.Where(entry => entry.OnShelf).Select(entry => entry.Book.SourceIdentity).ToHashSet(StringComparer.Ordinal);
                    var changed = new HashSet<string>(displayedShelf, StringComparer.Ordinal); changed.SymmetricExceptWith(shelf);
                    foreach (var identity in changed)
                        if (discoveryCards.TryGetValue(identity, out var card)) card.UpdateShelf?.Invoke();
                    if (featured is not null && changed.Contains(featured.Book.SourceIdentity)) featured.UpdateShelf?.Invoke();
                    displayedShelf = shelf; updateContinuation?.Invoke();
                }
                updateLoading(); more.IsEnabled = !module.LoadingDiscovery;
                more.Visibility = showBooks && module.MoreDiscovery ? Visibility.Visible : Visibility.Collapsed;
                status.Text = DiscoveryStatus(categories); status.Visibility = status.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                retry.Visibility = module.DiscoveryState == NovelDiscoveryState.Failed ? Visibility.Visible : Visibility.Collapsed;
                import.Visibility = module.ExploreSource is null && !module.LoadingDiscovery ? Visibility.Visible : Visibility.Collapsed;
            }
            finally { presentingDiscovery = false; }
        };
        return scroll;
    }
    private static Border DiscoveryRule(UIElement child)
    {
        var rule = new Border { Child = child, BorderThickness = new(0, 0, 0, 1), Margin = new(0, 0, 0, 16) };
        Canvas.SetZIndex(rule, 1);
        rule.UseResource(Border.BorderBrushProperty, "MeowFilm.Brush.SidebarBorder"); return rule;
    }
    private FrameworkElement DiscoveryFilters(NovelDiscoveryGroup group)
    {
        var row = new Grid { MinHeight = 36, Margin = new(0, 0, 0, 18) };
        AutomationProperties.SetName(row, "当前频道筛选");
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new());
        var label = new TextBlock { Text = group.Title, MaxWidth = 150,
            TextTrimming = TextTrimming.CharacterEllipsis };
        ToolTipService.SetToolTip(label, group.Title);
        label.UseResource(StyleProperty, "MeowFilm.FilterLabel");
        var labelSlot = new Grid { Height = 36, VerticalAlignment = VerticalAlignment.Top, Margin = new(0, 0, 14, 0) };
        labelSlot.Children.Add(label); row.Children.Add(labelSlot);
        var choices = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var kind in group.Kinds)
        {
            var choice = new RadioButton { Content = kind.Title, IsChecked = kind.Url == module.ExploreKind?.Url,
                GroupName = "novel.discovery.kinds" };
            discoveryChoices.Add((choice, [kind]));
            choice.UseResource(StyleProperty, "Novel.Discovery.Choice");
            AutomationProperties.SetName(choice, kind.Title);
            AutomationProperties.SetAutomationId(choice, "novel.discovery.kind:" + kind.Url);
            choice.Checked += (_, _) =>
            {
                if (!presentingDiscovery && kind.Url != module.ExploreKind?.Url)
                    module.RunUi(ct => module.LoadDiscoveryAsync(module.ExploreSource, kind, false, ct));
            };
            choice.KeyDown += DiscoveryNavigation_KeyDown;
            choice.Loaded += (_, _) => { if (choice.IsChecked == true) choice.StartBringIntoView(); };
            choices.Children.Add(choice);
        }
        var capsule = new Border { Child = DiscoveryNavigation(choices), HorizontalAlignment = HorizontalAlignment.Left };
        capsule.UseResource(StyleProperty, "MeowFilm.FilterGroup");
        Grid.SetColumn(capsule, 1); row.Children.Add(capsule);
        return row;
    }
    private FrameworkElement DiscoveryFeatureRow(FrameworkElement featured, FrameworkElement continuation)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new() { Width = new(9, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = new(16) });
        row.ColumnDefinitions.Add(new() { Width = new(3, GridUnitType.Star) });
        row.RowDefinitions.Add(new() { Height = GridLength.Auto });
        row.RowDefinitions.Add(new() { Height = new(0) });
        row.RowDefinitions.Add(new() { Height = GridLength.Auto });
        row.Children.Add(featured); Grid.SetColumn(continuation, 2); row.Children.Add(continuation);
        bool? stacked = null;
        row.SizeChanged += (_, _) =>
        {
            bool next = row.ActualWidth < 760; if (next == stacked) return; stacked = next;
            row.ColumnDefinitions[0].Width = new(next ? 1 : 9, GridUnitType.Star);
            row.ColumnDefinitions[1].Width = new(next ? 0 : 16);
            row.ColumnDefinitions[2].Width = next ? new(0) : new(3, GridUnitType.Star);
            row.RowDefinitions[1].Height = new(next ? 16 : 0);
            Grid.SetColumn(continuation, next ? 0 : 2); Grid.SetRow(continuation, next ? 2 : 0);
        };
        return row;
    }
    private ContentControl DiscoveryCoverSlot(NovelBook book, double width, double height, out Action<NovelBook> update, bool scale = false)
    {
        var slot = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        (string Source, long Revision, string Url, string Cover)? displayed = null;
        update = next =>
        {
            var identity = CoverIdentity(next);
            if (displayed == identity) return;
            displayed = identity; slot.Content = DiscoveryCover(next, width, height, scale);
        };
        update(book); return slot;
    }
    private Action DiscoveryShelf(Button button, DiscoveryBookView view, bool featured)
    {
        bool? displayed = null;
        return () =>
        {
            bool onShelf = IsOnShelf(view.Book);
            if (displayed == onShelf) return;
            displayed = onShelf;
            string name = onShelf ? "移出书架" : "加入书架";
            AutomationProperties.SetName(button, name); ToolTipService.SetToolTip(button, name);
            button.Content = featured ? onShelf ? "✓  已加入书架" : "＋  加入书架" :
                DiscoveryIcon(onShelf ? "M5,12 L9,16 19,6" : "M12,5 L12,19 M5,12 L19,12");
        };
    }
    private DiscoveryBookView DiscoveryFeaturedBook(NovelBook book)
    {
        var view = new DiscoveryBookView(book);
        var card = new Border { Height = 216, Padding = new(30, 22, 30, 22) };
        card.UseResource(StyleProperty, "Novel.Discovery.Feature"); DiscoveryCanvas(card);
        AutomationProperties.SetName(card, "选读");
        var layout = new Grid(); layout.ColumnDefinitions.Add(new()); layout.ColumnDefinitions.Add(new() { Width = new(149) });
        var cover = DiscoveryCoverSlot(book, 121, 168, out var updateCover); cover.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(cover, 1); layout.Children.Add(cover);
        card.SizeChanged += (_, _) =>
        {
            bool hideCover = card.ActualWidth < 500;
            cover.Visibility = hideCover ? Visibility.Collapsed : Visibility.Visible;
            layout.ColumnDefinitions[1].Width = new(hideCover ? 0 : 149);
        };
        var copy = new DockPanel(); var actions = new WrapPanel { VerticalAlignment = VerticalAlignment.Bottom };
        var read = DiscoveryButton("开始阅读", () => module.RunUi(ct => ReadDiscoveryBookAsync(view.Book, ct)), "Novel.Discovery.Primary");
        read.Content = "开始阅读  →"; actions.Children.Add(read);
        var shelf = DiscoveryButton("加入书架", () => module.RunUi(ct => ShelfDiscoveryBookAsync(view.Book, !IsOnShelf(view.Book), ct)));
        shelf.Margin = new(16, 0, 0, 0); actions.Children.Add(shelf);
        view.UpdateShelf = DiscoveryShelf(shelf, view, true); view.UpdateShelf();
        DockPanel.SetDock(actions, Dock.Bottom); copy.Children.Add(actions);
        var words = new StackPanel(); var source = DiscoveryText("", 11, true); words.Children.Add(source);
        var title = DiscoveryButton(book.Name, () => module.RunUi(ct => module.OpenBookAsync(view.Book, ct)));
        var name = DiscoveryText(book.Name, 28); title.Content = name; title.HorizontalContentAlignment = HorizontalAlignment.Left;
        title.Height = 43; words.Children.Add(title);
        var metadata = DiscoveryText("", 12, true); words.Children.Add(metadata);
        var intro = DiscoveryText(book.Intro, 13, true); intro.TextWrapping = TextWrapping.Wrap; intro.LineHeight = 23;
        intro.MaxHeight = 46; intro.Margin = new(0, 12, 0, 0); words.Children.Add(intro);
        copy.Children.Add(words); layout.Children.Add(copy); card.Child = layout;
        view.Element = card;
        view.Update = next =>
        {
            updateCover(next); source.Text = "选读 · " + DiscoverySourceName(next);
            name.Text = next.Name; AutomationProperties.SetName(title, next.Name);
            metadata.Text = string.Join(" · ", new[] { next.Author, DiscoveryMetadata(next) }.Where(s => s.Length > 0)); intro.Text = next.Intro;
        };
        view.Update(book); return view;
    }
    private FrameworkElement DiscoveryContinuation(out Action update)
    {
        var card = new Border { Height = 216, Padding = new(20) };
        card.UseResource(StyleProperty, "Novel.Discovery.Surface"); DiscoveryCanvas(card);
        AutomationProperties.SetName(card, "继续阅读");
        var panel = new DockPanel(); card.Child = panel;
        var heading = new Grid(); heading.Children.Add(DiscoveryText("继续阅读", 13));
        var date = DiscoveryText("", 11, true); date.HorizontalAlignment = HorizontalAlignment.Right; heading.Children.Add(date);
        DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
        NovelEntry? last = null;
        var bottom = new Grid { Margin = new(0, 12, 0, 0) };
        var resume = DiscoveryButton("接着读", () => { if (last is { } entry) module.RunUi(ct => module.ContinueReadingAsync(entry, ct)); });
        resume.Content = "接着读 →"; resume.HorizontalAlignment = HorizontalAlignment.Left;
        resume.UseResource(Control.ForegroundProperty, "MeowFilm.Brush.AccentText"); bottom.Children.Add(resume);
        var percent = DiscoveryText("", 11, true); percent.HorizontalAlignment = HorizontalAlignment.Right;
        percent.VerticalAlignment = VerticalAlignment.Center; bottom.Children.Add(percent);
        DockPanel.SetDock(bottom, Dock.Bottom); panel.Children.Add(bottom);
        var content = new StackPanel { Margin = new(0, 17, 0, 0) };
        var book = new Grid(); book.ColumnDefinitions.Add(new() { Width = new(57) }); book.ColumnDefinitions.Add(new());
        var cover = new ContentControl(); book.Children.Add(cover);
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var name = DiscoveryText("", 14); info.Children.Add(name);
        var author = DiscoveryText("", 11, true); author.Margin = new(0, 5, 0, 0); info.Children.Add(author);
        Grid.SetColumn(info, 1); book.Children.Add(info); content.Children.Add(book);
        var chapter = DiscoveryText("", 11, true); chapter.Margin = new(0, 15, 0, 0); content.Children.Add(chapter); panel.Children.Add(content);
        var empty = DiscoveryText("暂无阅读历史", 12, true); empty.Margin = new(0, 20, 0, 0); panel.Children.Add(empty);
        update = () =>
        {
            var next = module.Library.Entries.Where(e => e.ReadAt is not null).OrderByDescending(e => e.ReadAt).FirstOrDefault();
            if (next != last)
            {
                if (next is null) cover.Content = null;
                else
                {
                    if (last is null || CoverIdentity(last.Book) != CoverIdentity(next.Book)) cover.Content = DiscoveryCover(next.Book, 45, 61);
                    name.Text = next.Book.Name; author.Text = next.Book.Author; chapter.Text = next.Anchor?.ChapterTitle ?? "";
                    percent.Text = next.Anchor is { } anchor ? $"本章 {anchor.Ratio:P0}" : "";
                    if (next.ReadAt is { } readAt)
                    {
                        int days = (DateTimeOffset.Now.Date - readAt.LocalDateTime.Date).Days;
                        date.Text = days == 0 ? "今天" : days == 1 ? "昨天" : readAt.LocalDateTime.ToString("MM-dd");
                    }
                }
                last = next;
            }
            empty.Visibility = last is null ? Visibility.Visible : Visibility.Collapsed;
            content.Visibility = bottom.Visibility = last is null ? Visibility.Collapsed : Visibility.Visible;
            date.Visibility = last is null ? Visibility.Collapsed : Visibility.Visible;
        };
        update(); return card;
    }
    private DiscoveryBookView DiscoveryTile(NovelBook book)
    {
        var view = new DiscoveryBookView(book);
        var panel = new StackPanel();
        panel.Children.Add(DiscoveryCoverSlot(book, 170, 170 * 4d / 3, out var updateCover, scale: true));
        var title = DiscoveryText(book.Name, 14); title.Margin = new(0, 10, 0, 0); panel.Children.Add(title);
        var author = DiscoveryText("", 11, true); author.Margin = new(0, 3, 0, 0); panel.Children.Add(author);
        var source = new Grid { Margin = new(0, 7, 0, 0) }; source.ColumnDefinitions.Add(new()); source.ColumnDefinitions.Add(new() { Width = new(14) });
        var sourceName = DiscoveryText("", 11, true); source.Children.Add(sourceName);
        var icon = DiscoveryIcon(StackIcon, 12); Grid.SetColumn(icon, 1); source.Children.Add(icon); panel.Children.Add(source);
        var button = DiscoveryButton(book.Name, () => module.RunUi(ct => module.OpenBookAsync(view.Book, ct)), "Novel.Discovery.OpenBook");
        button.Content = panel; view.Element = button;
        view.Update = next =>
        {
            updateCover(next); title.Text = next.Name; AutomationProperties.SetName(button, next.Name);
            author.Text = string.Join(" · ", new[] { next.Author, NovelJson.Text(next.Data, "kind") }.Where(s => s.Length > 0));
            sourceName.Text = DiscoverySourceName(next);
        };
        view.Update(book); return view;
    }
    private DiscoveryBookView DiscoveryCatalogBook(NovelBook book)
    {
        var view = new DiscoveryBookView(book);
        var layout = new Grid(); layout.ColumnDefinitions.Add(new() { Width = new(126) }); layout.ColumnDefinitions.Add(new());
        var cover = DiscoveryCoverSlot(book, 111, 156, out var updateCover); cover.HorizontalAlignment = HorizontalAlignment.Left; layout.Children.Add(cover);
        var info = new DockPanel { Height = 156 };
        var source = DiscoveryText("", 11, true); DockPanel.SetDock(source, Dock.Bottom); info.Children.Add(source);
        var text = new StackPanel();
        var title = DiscoveryText(book.Name, 15); title.Margin = new(0, 0, 26, 0); text.Children.Add(title);
        var author = DiscoveryText(book.Author, 11, true); author.Margin = new(0, 5, 0, 0); text.Children.Add(author);
        var metadata = DiscoveryText("", 11, true); metadata.Margin = new(0, 7, 0, 0); text.Children.Add(metadata);
        var summary = DiscoveryText(book.Intro, 12, true); summary.TextWrapping = TextWrapping.Wrap;
        summary.LineHeight = 21; summary.MaxHeight = 42; summary.Margin = new(0, 9, 0, 0); text.Children.Add(summary);
        info.Children.Add(text); Grid.SetColumn(info, 1); layout.Children.Add(info);
        var button = DiscoveryButton(book.Name, () => module.RunUi(ct => module.OpenBookAsync(view.Book, ct)), "Novel.Discovery.OpenBook");
        button.Content = layout;
        var row = new Grid(); row.Children.Add(button);
        var shelf = DiscoveryButton("加入书架", () => module.RunUi(ct => ShelfDiscoveryBookAsync(view.Book, !IsOnShelf(view.Book), ct)));
        shelf.Width = 24; shelf.Height = 24; shelf.HorizontalAlignment = HorizontalAlignment.Right; shelf.VerticalAlignment = VerticalAlignment.Top; row.Children.Add(shelf);
        view.UpdateShelf = DiscoveryShelf(shelf, view, false); view.UpdateShelf();
        var border = new Border { Child = row, Padding = new(0, 0, 0, 22), BorderThickness = new(0, 0, 0, 1) };
        border.UseResource(Border.BorderBrushProperty, "MeowFilm.Brush.SidebarBorder"); view.Element = border;
        view.Update = next =>
        {
            updateCover(next); title.Text = next.Name; AutomationProperties.SetName(button, next.Name);
            author.Text = next.Author; metadata.Text = DiscoveryMetadata(next); summary.Text = next.Intro; source.Text = DiscoverySourceName(next);
        };
        view.Update(book); return view;
    }
    private string DiscoveryStatus(bool categories) => module.DiscoveryState switch
    {
        NovelDiscoveryState.Loading => "加载中…",
        NovelDiscoveryState.Cancelling => "正在取消…",
        NovelDiscoveryState.Cancelled => "已取消",
        NovelDiscoveryState.Unavailable => module.ExploreSource is null ? "无可用发现书源" : "书源不可用",
        NovelDiscoveryState.Empty => module.ExploreKind is null ? categories ? "暂无可用分类" : "暂无可用榜单" : "暂无书籍",
        _ => ""
    };
    private void PresentDiscoveryFailure()
    {
        if (module.Route is not (NovelCapability.HomeRoute or NovelCapability.CategoriesRoute) ||
            module.DiscoveryState != NovelDiscoveryState.Failed || module.LoadingDiscovery || !IsLoaded || !DesktopVisual.IsVisible(this)) return;
        if (showingDiscoveryFailure || shownDiscoveryFailure == module.DiscoveryGeneration) return;
        module.RunUi(ShowDiscoveryRetryAsync);
    }
    private async Task ShowDiscoveryRetryAsync(CancellationToken token)
    {
        if (showingDiscoveryFailure) return;
        showingDiscoveryFailure = true;
        shownDiscoveryFailure = module.DiscoveryGeneration;
        try
        {
            var body = new StackPanel();
            body.Children.Add(DiscoveryText(module.ExploreSource?.Name ?? "书源", 14));
            if (module.DiscoveryError is { Length: > 0 } detail && detail != "发现加载失败")
                body.Children.Add(DiscoveryText(detail, 14));
            await NovelUi.DialogAsync(this, "发现加载失败", body,
                "重试", async ct =>
                {
                    await module.RetryDiscoveryAsync(ct);
                    if (module.DiscoveryState == NovelDiscoveryState.Failed)
                    {
                        shownDiscoveryFailure = module.DiscoveryGeneration;
                        throw new InvalidDataException("发现加载失败，请重试");
                    }
                    return true;
                }, token, applicationOverlay: PlayerDialogHost.Find(this) is not null);
        }
        finally { showingDiscoveryFailure = false; PresentDiscoveryFailure(); }
    }
}

internal sealed class NovelBookGrid(int maximumColumns, double minimumWidth, double columnGap, double rowGap) : Panel
{
    private int columns = 1;
    private double itemWidth;
    private readonly List<double> rowHeights = [];
    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsFinite(availableSize.Width) ? availableSize.Width : minimumWidth;
        columns = Math.Min(maximumColumns, Math.Max(1, (int)((width + columnGap) / (minimumWidth + columnGap))));
        itemWidth = Math.Max(0, (width - (columns - 1) * columnGap) / columns);
        rowHeights.Clear();
        for (int i = 0; i < Children.Count; i++)
        {
            Children[i].Measure(new Size(itemWidth, double.PositiveInfinity));
            int row = i / columns; if (row == rowHeights.Count) rowHeights.Add(0);
            rowHeights[row] = Math.Max(rowHeights[row], Children[i].DesiredSize.Height);
        }
        return new Size(width, rowHeights.Sum() + Math.Max(0, rowHeights.Count - 1) * rowGap);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        double top = 0;
        for (int i = 0; i < Children.Count; i++)
        {
            int row = i / columns, column = i % columns;
            if (column == 0 && row > 0) top += rowHeights[row - 1] + rowGap;
            Children[i].Arrange(new Rect(column * (itemWidth + columnGap), top, itemWidth, rowHeights[row]));
        }
        return finalSize;
    }
}
