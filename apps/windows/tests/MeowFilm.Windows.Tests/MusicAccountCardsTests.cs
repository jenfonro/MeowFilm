using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using MeowFilm.Capabilities;
using MeowFilm.Capabilities.Accounts;
using MeowFilm.Desktop;
using MeowFilm.Desktop.Artwork;

namespace MeowFilm.Windows.Tests;

public static class MusicAccountCardsTests
{
    public static IEnumerable<TestCase> Cases() => MusicUiTests.PlatformAccountCardCases();
}

public static partial class MusicUiTests
{
    internal static IEnumerable<TestCase> PlatformAccountCardCases()
    {
        yield return Case("login mode buttons preserve paired colors across selection disabled hover and high contrast", async () =>
        {
            await using var f = await SearchScene.Create();
            await f.Module.ActivateSettingsAsync();
            var view = new MusicAccountsView { DataContext = f.Module };
            var window = AccountCardWindow(view);
            try
            {
                window.Show(); await Drain();
                // SearchScene uses an in-process transport: QR begin fails without contacting an upstream.
                // Keep the real login/mode handlers and exercise their usable post-failure surface.
                AccountCardButton(view, "网易云音乐登录").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await SearchUntil(() => !f.Module.AccountBusy); await Drain();
                var popup = (Popup)view.FindName("LoginPopup");
                True(popup.Child is AccountLoginSurface surface &&
                    ReferenceEquals(surface.Style, view.FindResource("Account.LoginSurface")),
                    "music consumes the shared neutral login surface");
                var qr = (Button)view.FindName("QrModeButton");
                var sms = (Button)view.FindName("SmsModeButton");
                True(popup.IsOpen && f.Module.ShowAccountSms && sms.IsEnabled,
                    "the real login entry admits the existing QR and SMS modes");
                var hoverKey = (DependencyPropertyKey)typeof(UIElement).GetField("IsMouseOverPropertyKey",
                    BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
                // Inject only dependency-property states; no mouse movement or capture.
                qr.SetValue(hoverKey, false); sms.SetValue(hoverKey, false);
                foreach (var selected in new[] { qr, sms, qr })
                {
                    selected.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Drain();
                    Equal(selected == qr ? Visibility.Visible : Visibility.Collapsed,
                        ((FrameworkElement)view.FindName("QrPanel")).Visibility);
                    Equal(selected == sms ? Visibility.Visible : Visibility.Collapsed,
                        ((FrameworkElement)view.FindName("SmsPanel")).Visibility);
                    foreach (bool contrast in new[] { false, true, false })
                    {
                        DesktopTheme.SetIsHighContrast(view, contrast); await Drain();
                        foreach (var button in new[] { qr, sms })
                        {
                            var normal = contrast ? SystemColors.WindowBrush :
                                button == selected ? (Brush)view.FindResource("MeowFilm.Brush.Surface") : Brushes.Transparent;
                            var foreground = contrast ? SystemColors.WindowTextBrush :
                                (Brush)view.FindResource("MeowFilm.Brush.Text");
                            AssertPair(button, normal, foreground);
                            button.SetValue(hoverKey, true); view.UpdateLayout();
                            AssertPair(button, contrast ? SystemColors.HighlightBrush :
                                (Brush)view.FindResource("MeowFilm.Brush.Hover"),
                                contrast ? SystemColors.HighlightTextBrush : foreground);
                            button.SetValue(hoverKey, false);
                            SetCardActionPressed(button, true); view.UpdateLayout();
                            AssertPair(button, contrast ? SystemColors.WindowBrush :
                                (Brush)view.FindResource("MeowFilm.Brush.Pressed"), foreground);
                            SetCardActionPressed(button, false);
                            // Parent disable preserves the SMS button's business IsEnabled binding.
                            var parent = (UIElement)VisualTreeHelper.GetParent(button);
                            parent.IsEnabled = false; view.UpdateLayout();
                            AssertPair(button, contrast ? SystemColors.WindowBrush : Brushes.Transparent,
                                contrast ? SystemColors.GrayTextBrush : (Brush)view.FindResource("MeowFilm.Brush.Disabled"));
                            parent.ClearValue(UIElement.IsEnabledProperty); view.UpdateLayout();
                            True(button.IsEnabled, "restoring the parent restores mode admission");
                            AssertPair(button, normal, foreground);
                        }
                    }
                }
                True(popup.IsOpen && f.Module.SelectedAccountPlatform == "wy",
                    "visual states do not retire login or change the selected account");
                AccountCardButton(popup.Child, "关闭登录").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Drain();
                True(!popup.IsOpen, "the original close action still retires login");

                void AssertPair(Button button, Brush background, Brush foreground)
                {
                    button.UpdateLayout();
                    AssertCardActionBrush(button.Background, background);
                    AssertCardActionBrush(button.Foreground, foreground);
                    var text = Visuals(button).OfType<TextBlock>().Single();
                    Equal(button.Content.ToString(), text.Text);
                    AssertCardActionBrush(text.Foreground, foreground);
                }
            }
            finally { window.Close(); await Drain(); }
        });
        yield return Case("platform account table keeps startup account state while a shared reread is pending", async () =>
        {
            await using var f = await SearchScene.Create();
            var view = new MusicAccountsView { DataContext = f.Module };
            Render(view, 840, 360);
            foreach (var row in f.Module.PlatformSettings)
            {
                Equal("none", row.Account.State);
                Equal("未登录", row.DisplayName);
                True(!row.IsLoggedIn && row.ShowLogin, "startup confirms the local logged-out identity");
                AssertAccountTableImages(view, row, avatarVisible: false);
            }
            var first = f.Module.PlatformSettings[0];
            var unboundLogo = Visuals(view).OfType<Image>()
                .Single(image => AutomationProperties.GetName(image) == first.Name + "标志");
            var unboundAvatar = Visuals(view).OfType<ArtworkImage>()
                .Single(image => AutomationProperties.GetName(image) == first.Name + "头像");
            unboundLogo.DataContext = null; unboundAvatar.DataContext = null;
            Render(view, 840, 360);
            Equal(Visibility.Visible, unboundLogo.Visibility);
            True(unboundLogo.Source is not null, "an unbound platform mark uses the local default, not account identity");
            Equal(Visibility.Collapsed, unboundAvatar.Visibility);
            unboundLogo.ClearValue(FrameworkElement.DataContextProperty);
            unboundAvatar.ClearValue(FrameworkElement.DataContextProperty);
            int accountReads = f.Store.AccountReads;
            var hold = f.Store.HoldNextAccountRead();
            var reread = f.Module.ReadAccountsAsync();
            var activation = f.Module.ActivateSettingsAsync();
            try
            {
                await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Equal(accountReads + 1, f.Store.AccountReads);
                Render(view, 840, 360);
                foreach (var row in f.Module.PlatformSettings)
                {
                    Equal("none", row.Account.State);
                    True(row.ShowLogin && !row.IsLoggedIn, "a pending reread preserves the last confirmed identity");
                    AssertAccountTableImages(view, row, avatarVisible: false);
                }
                hold.Release();
                await Task.WhenAll(reread, activation).WaitAsync(TimeSpan.FromSeconds(5));
                Equal(accountReads + 1, f.Store.AccountReads);
                Render(view, 840, 360);
                foreach (var row in f.Module.PlatformSettings)
                {
                    Equal("none", row.Account.State);
                    True(row.ShowLogin && !row.IsLoggedIn, "local read confirms logged-out identity");
                    AssertAccountTableImages(view, row, avatarVisible: false);
                }
                Equal(0, f.Http.Requests.Count); Equal(0, f.Runtime.Calls.Count);
            }
            finally { hold.Release(); await Task.WhenAll(reread, activation); }
        });
        yield return Case("platform account table never substitutes a brand mark for a logged-in user avatar", async () =>
        {
            foreach (var item in new[]
            {
                (State: "saved", Avatar: "https://p1.music.126.net/SYNTHETIC_SAVED_AVATAR.png"),
                (State: "verified", Avatar: "https://p1.music.126.net/SYNTHETIC_VERIFIED_AVATAR.png"),
                (State: "verified", Avatar: "")
            })
            {
                await using var f = await SearchScene.Create();
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var snapshot = new MusicAccountDetails("wy", 1, item.State,
                    new("wy", "1", "Cached native name", item.Avatar), now, now + 86400000,
                    new(null, "unknown", null), 0, 0, [], ProfileFreshness: MusicAccountDetailsFreshness.Fresh);
                var source = new HeldCardDetailsSource(snapshot);
                int rowIndex = f.Module.PlatformSettings.ToList().FindIndex(row => row.Platform == "wy");
                var oldRow = f.Module.PlatformSettings[rowIndex];
                await oldRow.DisposeAsync();
                var row = new MusicPlatformSettingsRow(f.Module, "wy", oldRow.Name, source);
                f.Module.PlatformSettings[rowIndex] = row;
                int accountIndex = f.Module.Accounts.ToList().FindIndex(account => account.Platform == "wy");
                f.Module.Accounts[accountIndex] = new("wy", row.Name, item.State, "Saved native name", "1", 1);
                f.Module.RefreshPlatformSetting("wy");
                var view = new MusicAccountsView { DataContext = f.Module };
                Render(view, 840, 360);
                True(row.IsLoggedIn && !row.ShowLogin, "login identity does not depend on an avatar URL");
                Equal("", row.Avatar);
                AssertAccountTableImages(view, row, avatarVisible: true);
                var read = row.ActivateAsync(default);
                try
                {
                    await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Render(view, 840, 360);
                    AssertAccountTableImages(view, row, avatarVisible: true);
                    source.Release.TrySetResult();
                    await read.WaitAsync(TimeSpan.FromSeconds(5));
                    Render(view, 840, 360);
                    Equal("Cached native name", row.DisplayName);
                    Equal(item.Avatar, row.Avatar);
                    AssertAccountTableImages(view, row, avatarVisible: true);
                    foreach (string state in new[] { "unknown", "revoked", "none" })
                    {
                        f.Module.Accounts[accountIndex] = new("wy", row.Name, state, Generation: 2);
                        f.Module.RefreshPlatformSetting("wy");
                        Render(view, 840, 360);
                        Equal("", row.Avatar);
                        AssertAccountTableImages(view, row, avatarVisible: false);
                    }
                    Equal(1, source.Reads);
                    Equal(0, f.Http.Requests.Count); Equal(0, f.Runtime.Calls.Count);
                }
                finally { source.Release.TrySetResult(); await read; }
            }
        });
        yield return Case("platform account table separates five local platform marks from muted native account states", async () =>
        {
            await using var f = await SearchScene.Create();
            await f.Module.ActivateSettingsAsync();
            var view = new MusicAccountsView { DataContext = f.Module };
            Render(view, 1104, 600);
            var titles = Visuals(view).OfType<TextBlock>()
                .Where(text => text.Text == "未登录" && IsAccountContentVisible(text)).ToArray();
            Equal(3, titles.Length);
            foreach (var row in f.Module.PlatformSettings)
            {
                AssertAccountTableImages(view, row, avatarVisible: false);
                True(!row.CanRefresh && !row.CanLogout, "logged-out actions have no admission");
                var platform = Visuals(view).OfType<TextBlock>().Single(text => text.Text == row.Name);
                var accountState = titles.Single(text => ReferenceEquals(text.DataContext, row));
                Equal(14d, platform.FontSize);
                Equal(13d, accountState.FontSize);
                True(!Equals(platform.Foreground, accountState.Foreground), "platform identity and account state have separate ink roles");
            }
            var logos = Visuals(view).OfType<Image>()
                .Where(image => AutomationProperties.GetName(image).EndsWith("标志", StringComparison.Ordinal) &&
                    IsAccountContentVisible(image)).ToArray();
            Equal(5, logos.Length);
            True(logos.All(image => image.Source is not null && image.Source.Width > 0 && image.Source.Height > 0),
                "official marks are available as local image resources");
            True(logos.All(image => image.Width == 26 && image.Height == 26), "platform marks use their own 26 DIP column");
            Equal(2, Visuals(view).OfType<TextBlock>().Count(text => text.Text == "无需登录" && IsAccountContentVisible(text)));
            Equal(0, f.Http.Requests.Count); Equal(0, f.Runtime.Calls.Count);
        });
        yield return Case("platform account table actions retain quiet text styling and accessible states across themes", async () =>
        {
            await using var f = await DetailsScene.Create();
            await f.Module.ActivateSettingsAsync();
            var view = new MusicAccountsView { DataContext = f.Module };
            Render(view, 840, 360);
            var refresh = AccountCardButton(view, "网易云音乐刷新资料");
            var logout = AccountCardButton(view, "网易云音乐退出账号");
            var buttons = new[] { refresh, logout };
            foreach (bool dark in new[] { false, true })
            {
                if (dark)
                    foreach (var resource in new (string Key, Brush Brush)[]
                    {
                        ("Surface", Brushes.DarkSlateGray), ("Text", Brushes.WhiteSmoke), ("Muted", Brushes.LightGray),
                        ("Border", Brushes.SlateGray), ("Hover", Brushes.SlateGray),
                        ("Pressed", Brushes.Black), ("Error", Brushes.IndianRed),
                        ("Focus", Brushes.DodgerBlue), ("Disabled", Brushes.Gray)
                    })
                        view.Resources["MeowFilm.Brush." + resource.Key] = resource.Brush;
                if (dark) view.Resources["Settings.Brush.Active"] = Brushes.SlateGray;
                foreach (bool highContrast in new[] { false, true })
                {
                    DesktopTheme.SetIsHighContrast(view, highContrast);
                    Render(view, 840, 360);
                    if (highContrast)
                    {
                        foreach (var text in Visuals(view).OfType<TextBlock>().Where(text =>
                            IsAccountContentVisible(text) && text.DataContext is MusicPlatformSettingsRow))
                            AssertCardActionBrush(text.Foreground, SystemColors.WindowTextBrush);
                    }
                    foreach (var button in buttons)
                    {
                        True(button.IsEnabled && button.FocusVisualStyle is not null, "table actions retain admission and keyboard focus visuals");
                        Equal(13d, button.FontSize); Equal(32d, button.ActualHeight);
                        var surface = Visuals(button).OfType<Border>().Single();
                        Equal(new Thickness(0), surface.BorderThickness);
                        Equal(10d, surface.CornerRadius.TopLeft);
                        AssertCardActionBrush(button.Background, highContrast ? SystemColors.WindowBrush : Brushes.Transparent);
                        AssertCardActionBrush(button.Foreground, highContrast ? SystemColors.WindowTextBrush : (Brush)button.FindResource("MeowFilm.Brush.Text"));
                        AssertCardActionBrush(button.BorderBrush, highContrast ? SystemColors.WindowTextBrush :
                            (Brush)button.FindResource("MeowFilm.Brush.Border"));
                        SimulateFocus(button, UIElement.IsMouseOverProperty, true);
                        Render(view, 840, 360);
                        AssertCardActionBrush(button.Background, highContrast ? SystemColors.HighlightBrush : (Brush)button.FindResource("MeowFilm.Brush.Hover"));
                        AssertCardActionBrush(button.Foreground, highContrast ? SystemColors.HighlightTextBrush : (Brush)button.FindResource("MeowFilm.Brush.Text"));
                        SimulateFocus(button, UIElement.IsMouseOverProperty, false);
                        SetCardActionPressed(button, true);
                        Render(view, 840, 360);
                        AssertCardActionBrush(button.Background, highContrast ? SystemColors.WindowBrush : (Brush)button.FindResource("MeowFilm.Brush.Pressed"));
                        SetCardActionPressed(button, false);
                        SimulateFocus(button, UIElement.IsKeyboardFocusedProperty, true);
                        Render(view, 840, 360);
                        AssertCardActionBrush(button.BorderBrush, highContrast ? SystemColors.WindowTextBrush : (Brush)button.FindResource("MeowFilm.Brush.Focus"));
                        SimulateFocus(button, UIElement.IsKeyboardFocusedProperty, false);
                    }
                    view.IsEnabled = false;
                    Render(view, 840, 360);
                    foreach (var button in buttons)
                    {
                        True(!button.IsEnabled, "disabled parent preserves the action binding while denying interaction");
                        AssertCardActionBrush(button.Foreground, highContrast ? SystemColors.GrayTextBrush : (Brush)button.FindResource("MeowFilm.Brush.Disabled"));
                    }
                    view.IsEnabled = true;
                }
            }
            Equal(0, f.Http.Requests); Equal(0, f.Runtime.Calls.Count);
        });
        yield return Case("platform account card refresh action updates native profile avatar and membership once", async () =>
        {
            await using var f = await DetailsScene.Create();
            await f.Module.ActivateSettingsAsync();
            var row = f.Module.PlatformSettings.Single(item => item.Platform == "wy");
            f.Http.DisplayName = "Updated native name";
            f.Http.Avatar = "https://p1.music.126.net/SYNTHETIC_ACCOUNT_AVATAR.png";
            f.Http.HoldRights = true;
            var view = new MusicAccountsView { DataContext = f.Module };
            var window = AccountCardWindow(view);
            try
            {
                window.Show(); await Drain();
                var refresh = AccountCardButton(view, "网易云音乐刷新资料");
                Equal("刷新", (string)refresh.Content);
                Equal("退出登录", (string)AccountCardButton(view, "网易云音乐退出账号").Content);
                refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await f.Http.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                True(!row.CanRefresh && !refresh.IsEnabled && row.RefreshText == "刷新中", "refresh is bounded by its current request");
                Equal("刷新中", (string)refresh.Content);
                refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Equal(2, f.Http.Requests);
                f.Http.Release.TrySetResult();
                await SearchUntil(() => row.CanRefresh && row.DisplayName == f.Http.DisplayName);
                Equal(f.Http.Avatar, row.Avatar);
                Equal(row.Membership, row.Subtitle);
                True(row.Membership.StartsWith("VIP有效 · ", StringComparison.Ordinal), "fresh membership is displayed");
                var avatar = Visuals(view).OfType<ArtworkImage>()
                    .Single(image => AutomationProperties.GetName(image) == "网易云音乐头像");
                Equal(row.Avatar, avatar.Address);
                AssertAccountTableImages(view, row, avatarVisible: true);
                Equal(2, f.Http.Requests); Equal(0, f.Runtime.Calls.Count);
            }
            finally { f.Http.Release.TrySetResult(); window.Close(); await Drain(); }
        });
        yield return Case("platform account logout bubble cancels or confirms the displayed platform without changing selection", async () =>
        {
            await using var f = await DetailsScene.Create();
            await f.Module.ActivateSettingsAsync();
            f.Module.SelectedAccount = f.Module.Accounts.Single(account => account.Platform == "tx");
            var row = f.Module.PlatformSettings.Single(item => item.Platform == "wy");
            var view = new MusicAccountsView { DataContext = f.Module };
            var window = AccountCardWindow(view);
            try
            {
                window.Show(); await Drain();
                var logout = AccountCardButton(view, "网易云音乐退出账号");
                var popup = (Popup)view.FindName("LogoutPopup");
                logout.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Drain();
                True(popup.IsOpen && ReferenceEquals(popup.PlacementTarget, logout), "confirmation belongs to the clicked button");
                var placement = popup.CustomPopupPlacementCallback!(new Size(216, 120), new Size(64, 32), new Point()).Single();
                True(placement.Point.Y >= 32, "confirmation is positioned below the button");
                True(((Button)view.FindName("CancelLogoutButton")).IsKeyboardFocusWithin, "cancel receives initial keyboard focus");
                AccountCardButton(popup.Child, "取消退出账号").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Drain(); await SearchUntil(() => !f.Module.AccountBusy);
                True(!popup.IsOpen && row.IsLoggedIn, "cancel does not change the account");
                Equal(0, f.Http.Requests);
                logout.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Drain();
                AccountCardButton(popup.Child, "确定退出账号").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await SearchUntil(() => !row.IsLoggedIn && !f.Module.AccountBusy);
                True(!popup.IsOpen && row.DisplayName == "未登录", "logout restores the native account state without changing platform identity");
                Equal("tx", f.Module.SelectedAccountPlatform);
                Equal(0, f.Http.Requests); Equal(0, f.Runtime.Calls.Count);
            }
            finally { window.Close(); await Drain(); }
        });
        yield return Case("platform account logout confirmation retires when the view hides or its account is replaced", async () =>
        {
            await using var f = await DetailsScene.Create();
            await f.Module.ActivateSettingsAsync();
            var view = new MusicAccountsView { DataContext = f.Module };
            var window = AccountCardWindow(view);
            try
            {
                window.Show(); await Drain();
                var logout = AccountCardButton(view, "网易云音乐退出账号");
                var popup = (Popup)view.FindName("LogoutPopup");
                logout.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Drain();
                var confirm = AccountCardButton(popup.Child, "确定退出账号");
                view.Visibility = Visibility.Collapsed; await Drain();
                True(!popup.IsOpen, "hidden view retires confirmation");
                confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                True(f.Module.PlatformSettings[0].IsLoggedIn, "retired confirmation cannot submit logout");
                view.Visibility = Visibility.Visible; await Drain();
                logout.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Drain();
                var old = f.Module.Accounts.Single(account => account.Platform == "wy");
                int index = f.Module.Accounts.IndexOf(old);
                f.Module.Accounts[index] = old with { Generation = old.Generation + 1 };
                f.Module.SelectedAccount = f.Module.Accounts[index]; await Drain();
                True(!popup.IsOpen, "replacement generation retires confirmation");
                confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await f.Module.LogoutAccountAsync(old, confirmed: true);
                True(f.Module.PlatformSettings[0].IsLoggedIn, "old generation cannot log out its replacement");
                Equal(0, f.Http.Requests);
            }
            finally { window.Close(); await Drain(); }
        });
        yield return Case("platform account table public catalogs have no native account or action admission", async () =>
        {
            await using var f = await SearchScene.Create();
            int reads = f.Store.AccountReads;
            var accounts = f.Module.Accounts.ToArray();
            var selection = f.Module.SelectedAccount;
            var view = new MusicAccountsView { DataContext = f.Module };
            var window = AccountCardWindow(view);
            try
            {
                window.Show(); await Drain();
                Equal(5, AccountTableItems(view).Items.Count);
                True(view.FindName("PlatformRecordCount") is null && view.FindName("PlatformFilter") is null,
                    "the table has no count or display filter above it");
                Equal(3, f.Module.Accounts.Count); Equal(3, f.Module.PlatformSettings.Count);
                var rows = view.AccountTableRows.Cast<MusicAccountTableRow>().ToArray();
                True(rows.Select(row => row.Platform).SequenceEqual(["wy", "tx", "kg", "kw", "mg"]),
                    "three native rows precede the two public catalogs");
                foreach (var row in rows.Where(row => !row.IsPublicCatalog))
                    True(ReferenceEquals(row.Source, f.Module.PlatformSettings.Single(source => source.Platform == row.Platform)),
                        "the table projects the existing native row");
                foreach (var row in rows.Where(row => row.IsPublicCatalog))
                {
                    True(row.Source is null && !f.Module.Accounts.Any(account => account.Platform == row.Platform),
                        "public catalog identity does not fabricate an account");
                    var element = AccountTableRowElement(view, row);
                    Equal(1, Visuals(element).OfType<TextBlock>().Count(text => text.Text == "无需登录"));
                    Equal(4, Visuals(element).OfType<TextBlock>().Count(text => text.Text == "—" && text.Visibility == Visibility.Visible));
                    Equal(0, Visuals(element).OfType<ArtworkImage>().Count());
                    foreach (var button in Visuals(element).OfType<Button>())
                    {
                        True(!button.IsEnabled && button.Tag is null && !button.IsVisible, "public catalog has no login or account action");
                        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    foreach (var toggle in Visuals(element).OfType<CheckBox>())
                    {
                        True(!toggle.IsEnabled && toggle.Tag is null && !toggle.IsVisible, "public catalog has no sync or feedback consent");
                        toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    }
                }
                await Drain();
                True(f.Module.Accounts.SequenceEqual(accounts) && ReferenceEquals(selection, f.Module.SelectedAccount),
                    "public display and stale routed actions do not change native accounts or selection");
                Equal(reads, f.Store.AccountReads);
                Equal(0, f.Http.Requests.Count); Equal(0, f.Runtime.Calls.Count);
            }
            finally { window.Close(); await Drain(); }
        });
        yield return Case("platform account table follows native collection and owner replacement and detaches on unload", async () =>
        {
            await using var first = await SearchScene.Create();
            await using var next = await SearchScene.Create();
            int firstReads = first.Store.AccountReads, nextReads = next.Store.AccountReads;
            var view = new MusicAccountsView { DataContext = first.Module };
            Render(view, 1104, 600);
            first.Module.PlatformSettings.Move(2, 0);
            Render(view, 1104, 600);
            Equal("kg", ((MusicAccountTableRow)AccountTableItems(view).Items[0]).Platform);
            var old = first.Module.PlatformSettings.Single(row => row.Platform == "wy");
            int index = first.Module.PlatformSettings.IndexOf(old);
            var replacement = new MusicPlatformSettingsRow(first.Module, old.Platform, old.Name, first.App);
            first.Module.PlatformSettings[index] = replacement;
            await old.DisposeAsync();
            Render(view, 1104, 600);
            True(view.AccountTableRows.Cast<MusicAccountTableRow>().Any(row => ReferenceEquals(row.Source, replacement)) &&
                !view.AccountTableRows.Cast<MusicAccountTableRow>().Any(row => ReferenceEquals(row.Source, old)),
                "collection changes before load replace the projection rather than copying account state");
            var window = AccountCardWindow(view);
            try
            {
                window.Show(); await Drain();
                view.DataContext = next.Module; await Drain();
                var nextRows = next.Module.PlatformSettings.ToArray();
                True(view.AccountTableRows.Cast<MusicAccountTableRow>().Where(row => !row.IsPublicCatalog)
                    .Select(row => row.Source).SequenceEqual(nextRows), "owner replacement uses only the new native collection");
                first.Module.PlatformSettings.Move(0, 2); await Drain();
                True(view.AccountTableRows.Cast<MusicAccountTableRow>().Where(row => !row.IsPublicCatalog)
                    .Select(row => row.Source).SequenceEqual(nextRows), "retired owner changes are no longer observed");
                next.Module.PlatformSettings.RemoveAt(0); await Drain();
                Equal(4, AccountTableItems(view).Items.Count);
                next.Module.PlatformSettings.Insert(0, nextRows[0]); await Drain();
                next.Module.PlatformSettings.Clear(); await Drain();
                Equal(2, AccountTableItems(view).Items.Count);
                True(view.AccountTableRows.Cast<MusicAccountTableRow>().All(row => row.IsPublicCatalog),
                    "a reset never replaces missing native rows with public or demo accounts");
                foreach (var source in nextRows) next.Module.PlatformSettings.Add(source);
                await Drain(); Equal(5, AccountTableItems(view).Items.Count);
                window.Content = null; await Drain();
                Equal(0, AccountTableItems(view).Items.Count);
                next.Module.PlatformSettings.Move(2, 0); await Drain();
                Equal(0, AccountTableItems(view).Items.Count);
                window.Content = view; await Drain();
                True(view.AccountTableRows.Cast<MusicAccountTableRow>().Where(row => !row.IsPublicCatalog)
                    .Select(row => row.Source).SequenceEqual(next.Module.PlatformSettings), "reload observes current rows once");
                Equal(5, AccountTableItems(view).Items.Count);
                Equal(firstReads, first.Store.AccountReads); Equal(nextReads, next.Store.AccountReads);
                Equal(0, first.Http.Requests.Count); Equal(0, next.Http.Requests.Count);
                Equal(0, first.Runtime.Calls.Count); Equal(0, next.Runtime.Calls.Count);
            }
            finally { window.Close(); await Drain(); }
        });
        yield return Case("platform account table keeps preview columns independent and horizontal overflow inside its own viewport", async () =>
        {
            await using var f = await DetailsScene.Create();
            await f.Module.ActivateSettingsAsync();
            var view = new MusicAccountsView { DataContext = f.Module };
            foreach (int width in new[] { 1104, 850, 849, 640, 500, 360 })
            {
                Render(view, width, 600);
                var horizontal = (ScrollViewer)view.FindName("AccountTableScroll");
                var header = (Grid)view.FindName("AccountTableHeader");
                var items = AccountTableItems(view);
                bool compact = width < 850;
                Equal(compact, MusicAccountsView.GetIsCompactTable(view));
                AssertAccountDimension(30d, header.ActualHeight);
                True(view.FindName("AccountBodyScroll") is null, "account rows use the settings page wheel");
                True(view.DesiredSize.Width <= width + .5 && horizontal.ActualWidth <= width + .5,
                    "the table minimum width cannot expand its host or sidebar");
                True(Math.Abs(header.ActualWidth - items.ActualWidth) < .5, "header accounts for the body's scrollbar width");
                AssertAccountDimension(compact ? 132d : 196d, header.ColumnDefinitions[0].ActualWidth);
                AssertAccountDimension(compact ? 144d : 146d, header.ColumnDefinitions[10].ActualWidth);
                foreach (int column in new[] { 4, 6, 8 }) AssertAccountDimension(compact ? 72d : 112d, header.ColumnDefinitions[column].ActualWidth);
                foreach (int column in new[] { 1, 3, 5, 7, 9 }) AssertAccountDimension(compact ? 6d : 8d, header.ColumnDefinitions[column].ActualWidth);
                True(horizontal.HorizontalScrollBarVisibility == ScrollBarVisibility.Auto &&
                    horizontal.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled,
                    "one horizontal viewport moves the header and body together");
                if (width >= 640) True(horizontal.ScrollableWidth < .5, "sufficient table width has no horizontal overflow");
                else True(horizontal.ScrollableWidth >= 640 - width - .5, "narrow table owns its horizontal overflow");
                var rows = items.Items.Cast<MusicAccountTableRow>().ToArray();
                foreach (var row in rows)
                {
                    var element = AccountTableRowElement(view, row);
                    var cells = Visuals(element).OfType<Grid>().Single(grid => grid.Name == "AccountCells");
                    AssertAccountDimension(76d, element.ActualHeight);
                    for (int column = 0; column < header.ColumnDefinitions.Count; column++)
                        True(Math.Abs(header.ColumnDefinitions[column].ActualWidth - cells.ColumnDefinitions[column].ActualWidth) < .5,
                            "row and fixed header share the same column geometry");
                    True(element.BorderThickness.Left == 0 && element.BorderThickness.Right == 0 &&
                        element.BorderThickness.Bottom == 0 && element.CornerRadius == new CornerRadius(0),
                        "rows do not paint cards or a terminal rule");
                    Equal(ReferenceEquals(row, rows[0]) ? 0d : 1d, element.BorderThickness.Top);
                    var logo = Visuals(element).OfType<Image>()
                        .Single(image => AutomationProperties.GetName(image) == row.Name + "标志");
                    AssertAccountDimension(26d, logo.ActualWidth); AssertAccountDimension(26d, logo.ActualHeight);
                    if (row.Source is not { IsLoggedIn: true } source) continue;
                    var avatar = Visuals(element).OfType<ArtworkImage>().Single();
                    AssertAccountDimension(32d, avatar.ActualWidth); AssertAccountDimension(32d, avatar.ActualHeight);
                    var name = Visuals(element).OfType<TextBlock>()
                        .Single(text => text.Text == source.DisplayName && text.Visibility == Visibility.Visible);
                    var membership = Visuals(element).OfType<TextBlock>().Single(text => text.Text == source.Membership);
                    Equal(14d, name.FontSize); Equal(11d, membership.FontSize);
                    foreach (var toggle in Visuals(element).OfType<CheckBox>())
                    {
                        AssertAccountDimension(32d, toggle.ActualWidth); AssertAccountDimension(18d, toggle.ActualHeight);
                        var cell = (Grid)VisualTreeHelper.GetParent(toggle);
                        True(Math.Abs(toggle.TranslatePoint(new Point(16, 9), cell).X - cell.ActualWidth / 2) < .5,
                            "each preference switch is centered in its independent column");
                    }
                    foreach (var button in Visuals(element).OfType<Button>().Where(button => button.Visibility == Visibility.Visible))
                    {
                        AssertAccountDimension(32d, button.ActualHeight); Equal(13d, button.FontSize);
                        Equal(new Thickness(0), button.BorderThickness);
                    }
                }
                var login = AccountCardButton(view, "酷狗音乐登录");
                AssertAccountDimension(32d, login.ActualHeight);
                AssertCardActionBrush(login.Background, (Brush)view.FindResource("MeowFilm.Brush.Accent"));
            }
            Equal(0, f.Http.Requests); Equal(0, f.Runtime.Calls.Count);
        });
        yield return Case("platform account table shows every platform without terminal rule or vertical wheel capture", async () =>
        {
            await using var f = await SearchScene.Create();
            var view = new MusicAccountsView { DataContext = f.Module };
            Render(view, 500, 600);
            var horizontal = (ScrollViewer)view.FindName("AccountTableScroll");
            var header = (Grid)view.FindName("AccountTableHeader");
            True(horizontal.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled &&
                !Visuals(view).OfType<ScrollViewer>().Any(scroll => scroll.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled),
                "account rows never own vertical wheel scrolling");
            var rows = AccountTableItems(view).Items.Cast<MusicAccountTableRow>().ToArray();
            Equal(5, rows.Length);
            var firstElement = AccountTableRowElement(view, rows[0]);
            horizontal.ScrollToRightEnd(); await Drain();
            Render(view, 500, 600);
            True(Math.Abs(header.TranslatePoint(new Point(), view).X - firstElement.TranslatePoint(new Point(), view).X) < .5,
                "horizontal scrolling keeps header and row aligned");
            Equal(new Thickness(0), firstElement.BorderThickness);
            Equal(new Thickness(0, 1, 0, 0), AccountTableRowElement(view, rows[^1]).BorderThickness);
            Equal(0, f.Http.Requests.Count); Equal(0, f.Runtime.Calls.Count);
        });
        yield return Case("platform account transient retirement cannot submit logout or refocus the enclosing settings scroll", async () =>
        {
            await using var f = await DetailsScene.Create();
            await f.Module.ActivateSettingsAsync();
            var view = new MusicAccountsView { DataContext = f.Module };
            var content = new StackPanel();
            content.Children.Add(view); content.Children.Add(new Border { Height = 800 });
            var scroll = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            var window = AccountCardWindow(scroll);
            try
            {
                window.Show(); await Drain();
                var logout = AccountCardButton(view, "网易云音乐退出账号");
                var popup = (Popup)view.FindName("LogoutPopup");
                logout.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Drain();
                True(popup.IsOpen, "current logout confirmation is open");
                var confirm = AccountCardButton(popup.Child, "确定退出账号");
                scroll.ScrollToVerticalOffset(160); await Drain();
                double offset = scroll.VerticalOffset;
                True(offset > 0, "settings host was scrolled before navigation retirement");
                view.CloseTransientPopups(); await Drain();
                True(!popup.IsOpen && popup.PlacementTarget is null && !((Popup)view.FindName("LoginPopup")).IsOpen,
                    "retirement closes all account transient surfaces");
                Equal(offset, scroll.VerticalOffset);
                confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Drain();
                True(f.Module.PlatformSettings.Single(row => row.Platform == "wy").IsLoggedIn,
                    "a retired confirmation has no logout admission");
                Equal(0, f.Http.Requests); Equal(0, f.Runtime.Calls.Count);
            }
            finally { window.Close(); await Drain(); }
        });
        yield return Case("platform account membership summaries distinguish VIP SVIP inactive expired and unknown rights", async () =>
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long future = now + 10 * 86400000L;
            string date = DateTimeOffset.FromUnixTimeMilliseconds(future).ToLocalTime().ToString("yyyy.MM.dd");
            var cases = new (bool? Vip, string Tier, long? Expiry, MusicAccountDetailsFreshness Freshness, string Text)[]
            {
                (true, "vip", future, MusicAccountDetailsFreshness.Fresh, "VIP有效 · " + date),
                (true, "svip", future, MusicAccountDetailsFreshness.Fresh, "SVIP有效 · " + date),
                (true, "huge_vip", future, MusicAccountDetailsFreshness.Fresh, "SVIP有效 · " + date),
                (true, "concept:svip", future, MusicAccountDetailsFreshness.Fresh, "SVIP有效 · " + date),
                (true, "vip", null, MusicAccountDetailsFreshness.Fresh, "VIP有效"),
                (false, "none", now - 1, MusicAccountDetailsFreshness.Fresh, "VIP无效"),
                (true, "svip", now - 1, MusicAccountDetailsFreshness.Stale, "VIP无效"),
                (null, "unknown", null, MusicAccountDetailsFreshness.Fresh, "权益未确认"),
                (false, "none", null, MusicAccountDetailsFreshness.Stale, "权益未确认")
            };
            foreach (var item in cases)
            {
                var snapshot = new MusicAccountDetails("wy", 3, "verified", new("wy", "3", "账号-wy"),
                    now, future, new(item.Vip, item.Tier, item.Expiry), now, future, [],
                    MembershipFreshness: item.Freshness);
                await using var vm = new MusicAccountDetailsViewModel(new CardMembershipSource(snapshot));
                vm.SelectAccount(DetailsAccount("wy", 3)); await vm.ActivateAsync();
                Equal(item.Text, vm.MembershipSummary);
            }
        });
    }

    private static Window AccountCardWindow(FrameworkElement view) => new()
    {
        Width = 840, Height = 360, Content = view, ShowInTaskbar = false,
        WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize
    };
    private static Button AccountCardButton(DependencyObject view, string name) => Visuals(view).OfType<Button>()
        .Single(button => AutomationProperties.GetName(button) == name);
    private static ItemsControl AccountTableItems(MusicAccountsView view) => (ItemsControl)view.FindName("PlatformSettingsList");
    private static Border AccountTableRowElement(MusicAccountsView view, MusicAccountTableRow row)
    {
        var container = AccountTableItems(view).ItemContainerGenerator.ContainerFromItem(row);
        True(container is not null, "table row is generated");
        return Visuals(container!).OfType<Border>().Single(border => border.Name == "AccountTableRow");
    }
    private static bool IsAccountContentVisible(DependencyObject element)
    {
        // Offscreen Render has no presentation source; inspect ancestor Visibility instead of IsVisible.
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is UIElement { Visibility: not Visibility.Visible }) return false;
        return true;
    }
    private static void AssertAccountDimension(double expected, double actual,
        [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        const double epsilon = 1e-6;
        True(Math.Abs(expected - actual) <= epsilon,
            $"line {line}: expected {expected:R} DIP, got {actual:R} DIP (epsilon {epsilon:R})");
    }
    private static void AssertAccountTableImages(DependencyObject view, MusicPlatformSettingsRow row, bool avatarVisible)
    {
        var logo = Visuals(view).OfType<Image>()
            .Single(image => AutomationProperties.GetName(image) == row.Name + "标志");
        var avatar = Visuals(view).OfType<ArtworkImage>()
            .Single(image => AutomationProperties.GetName(image) == row.Name + "头像");
        Equal(Visibility.Visible, logo.Visibility);
        Equal(avatarVisible ? Visibility.Visible : Visibility.Collapsed, avatar.Visibility);
        var platformIdentity = VisualTreeHelper.GetParent(logo);
        var accountIdentity = VisualTreeHelper.GetParent(avatar);
        True(!ReferenceEquals(platformIdentity, accountIdentity) &&
            Visuals(accountIdentity).OfType<Image>().All(image => Visuals(avatar).Contains(image)),
            "the account avatar has no brand image beneath it");
        Equal(row.Avatar, avatar.Address);
    }
    private static void AssertCardActionBrush(Brush actual, Brush expected) =>
        Equal(((SolidColorBrush)expected).Color, ((SolidColorBrush)actual).Color);
    private static void SetCardActionPressed(Button button, bool value)
    {
        var field = typeof(ButtonBase).GetField("IsPressedPropertyKey", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Pressed-state test seam unavailable");
        button.SetValue((DependencyPropertyKey)field.GetValue(null)!, value);
    }
    private sealed class HeldCardDetailsSource(MusicAccountDetails snapshot) : IMusicAccountDetailsSource
    {
        internal int Reads;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<MusicAccountDetails> ReadDetailsAsync(string platform, CancellationToken token = default)
        {
            Reads++; Entered.TrySetResult();
            await Release.Task.WaitAsync(token);
            return snapshot;
        }
        public Task<MusicAccountDetails> RefreshDetailsAsync(string platform, long expectedGeneration, CancellationToken token = default) =>
            Task.FromException<MusicAccountDetails>(new InvalidOperationException("avatar display must not refresh details"));
    }
    private sealed class CardMembershipSource(MusicAccountDetails snapshot) : IMusicAccountDetailsSource
    {
        public Task<MusicAccountDetails> ReadDetailsAsync(string platform, CancellationToken token = default) => Task.FromResult(snapshot);
        public Task<MusicAccountDetails> RefreshDetailsAsync(string platform, long expectedGeneration, CancellationToken token = default) =>
            Task.FromResult(snapshot);
    }
}
