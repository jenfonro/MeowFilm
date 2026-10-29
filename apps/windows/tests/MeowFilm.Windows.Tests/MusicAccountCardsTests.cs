using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
        yield return Case("platform account cards keep unknown identity hidden through the first local account read", async () =>
        {
            await using var f = await SearchScene.Create();
            var view = new MusicAccountsView { DataContext = f.Module };
            Render(view, 840, 360);
            foreach (var row in f.Module.PlatformSettings)
            {
                Equal("unknown", row.Account.State);
                Equal("未读取", row.DisplayName);
                True(!row.IsLoggedIn && !row.ShowLogin && !row.ShowPlatformLogo, "unread identity is not logged out");
                AssertAccountCardImages(view, row, logoVisible: false, avatarVisible: false);
            }
            var first = f.Module.PlatformSettings[0];
            var unboundLogo = Visuals(view).OfType<Image>()
                .Single(image => AutomationProperties.GetName(image) == first.Name + "标志");
            var unboundAvatar = Visuals(view).OfType<ArtworkImage>()
                .Single(image => AutomationProperties.GetName(image) == first.Name + "头像");
            unboundLogo.DataContext = null; unboundAvatar.DataContext = null;
            Render(view, 840, 360);
            Equal(Visibility.Collapsed, unboundLogo.Visibility);
            Equal(Visibility.Collapsed, unboundAvatar.Visibility);
            unboundLogo.ClearValue(FrameworkElement.DataContextProperty);
            unboundAvatar.ClearValue(FrameworkElement.DataContextProperty);
            var hold = f.Store.HoldNextAccountRead();
            var activation = f.Module.ActivateSettingsAsync();
            try
            {
                await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Render(view, 840, 360);
                foreach (var row in f.Module.PlatformSettings)
                {
                    Equal("unknown", row.Account.State);
                    AssertAccountCardImages(view, row, logoVisible: false, avatarVisible: false);
                }
                hold.Release();
                await activation.WaitAsync(TimeSpan.FromSeconds(5));
                Render(view, 840, 360);
                foreach (var row in f.Module.PlatformSettings)
                {
                    Equal("none", row.Account.State);
                    True(row.ShowPlatformLogo && row.ShowLogin && !row.IsLoggedIn, "local read confirms logged-out identity");
                    AssertAccountCardImages(view, row, logoVisible: true, avatarVisible: false);
                }
                Equal(0, f.Http.Requests.Count); Equal(0, f.Runtime.Calls.Count);
            }
            finally { hold.Release(); await activation; }
        });
        yield return Case("platform account cards use only user avatars before and after logged-in local details complete", async () =>
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
                True(row.IsLoggedIn && !row.ShowLogin && !row.ShowPlatformLogo, "login identity does not depend on an avatar URL");
                Equal("", row.Avatar);
                AssertAccountCardImages(view, row, logoVisible: false, avatarVisible: true);
                var read = row.ActivateAsync(default);
                try
                {
                    await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Render(view, 840, 360);
                    AssertAccountCardImages(view, row, logoVisible: false, avatarVisible: true);
                    source.Release.TrySetResult();
                    await read.WaitAsync(TimeSpan.FromSeconds(5));
                    Render(view, 840, 360);
                    Equal("Cached native name", row.DisplayName);
                    Equal(item.Avatar, row.Avatar);
                    AssertAccountCardImages(view, row, logoVisible: false, avatarVisible: true);
                    foreach (string state in new[] { "unknown", "revoked", "none" })
                    {
                        f.Module.Accounts[accountIndex] = new("wy", row.Name, state, Generation: 2);
                        f.Module.RefreshPlatformSetting("wy");
                        Render(view, 840, 360);
                        Equal("", row.Avatar);
                        AssertAccountCardImages(view, row, logoVisible: state == "none", avatarVisible: false);
                    }
                    Equal(1, source.Reads);
                    Equal(0, f.Http.Requests.Count); Equal(0, f.Runtime.Calls.Count);
                }
                finally { source.Release.TrySetResult(); await read; }
            }
        });
        yield return Case("platform account cards show local logos and a smaller muted platform subtitle when logged out", async () =>
        {
            await using var f = await SearchScene.Create();
            await f.Module.ActivateSettingsAsync();
            var view = new MusicAccountsView { DataContext = f.Module };
            Render(view, 840, 360);
            var titles = Visuals(view).OfType<TextBlock>().Where(text => text.Text == "未登录").ToArray();
            Equal(3, titles.Length);
            foreach (var row in f.Module.PlatformSettings)
            {
                Equal(row.Name, row.Subtitle);
                AssertAccountCardImages(view, row, logoVisible: true, avatarVisible: false);
                True(!row.CanRefresh && !row.CanLogout, "logged-out actions have no admission");
                var subtitle = Visuals(view).OfType<TextBlock>().Single(text => text.Text == row.Name);
                True(subtitle.FontSize < titles[0].FontSize, "platform name is secondary text");
                True(!Equals(subtitle.Foreground, titles[0].Foreground), "platform name is muted rather than primary");
            }
            var logos = Visuals(view).OfType<Image>()
                .Where(image => AutomationProperties.GetName(image).EndsWith("标志", StringComparison.Ordinal)).ToArray();
            Equal(3, logos.Length);
            True(logos.All(image => image.Source is not null && image.Source.Width > 0 && image.Source.Height > 0),
                "official marks are available as local image resources");
            Equal(0, f.Http.Requests.Count); Equal(0, f.Runtime.Calls.Count);
        });
        yield return Case("platform account card actions retain outlined secondary and danger states across theme resources and high contrast", async () =>
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
                        ("Surface", Brushes.DarkSlateGray), ("Text", Brushes.WhiteSmoke),
                        ("Border", Brushes.SlateGray), ("Hover", Brushes.SlateGray),
                        ("Pressed", Brushes.Black), ("Error", Brushes.IndianRed),
                        ("Focus", Brushes.DodgerBlue), ("Disabled", Brushes.Gray)
                    })
                        view.Resources["MeowFilm.Brush." + resource.Key] = resource.Brush;
                foreach (bool highContrast in new[] { false, true })
                {
                    DesktopTheme.SetIsHighContrast(view, highContrast);
                    Render(view, 840, 360);
                    foreach (var button in buttons)
                    {
                        True(button.IsEnabled && button.FocusVisualStyle is not null, "card actions retain admission and keyboard focus visuals");
                        Equal(refresh.FontSize, button.FontSize);
                        var outline = Visuals(button).OfType<Border>().Single();
                        True(outline.BorderThickness.Left > 0 && outline.CornerRadius.TopLeft > 0, "button paints a rounded outline rather than bare text");
                        AssertCardActionBrush(button.Background, highContrast ? SystemColors.WindowBrush : (Brush)button.FindResource("MeowFilm.Brush.Surface"));
                        AssertCardActionBrush(button.Foreground, highContrast ? SystemColors.WindowTextBrush : (Brush)button.FindResource("MeowFilm.Brush.Text"));
                        AssertCardActionBrush(button.BorderBrush, highContrast ? SystemColors.WindowTextBrush :
                            (Brush)button.FindResource(button == logout ? "MeowFilm.Brush.Error" : "MeowFilm.Brush.Border"));
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
                refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await f.Http.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                True(!row.CanRefresh && !refresh.IsEnabled && row.RefreshText == "刷新中", "refresh is bounded by its current request");
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
                AssertAccountCardImages(view, row, logoVisible: false, avatarVisible: true);
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
                True(!popup.IsOpen && row.DisplayName == "未登录" && row.Subtitle == row.Name, "logout restores the platform capsule");
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
    private static void AssertAccountCardImages(DependencyObject view, MusicPlatformSettingsRow row,
        bool logoVisible, bool avatarVisible)
    {
        var logo = Visuals(view).OfType<Image>()
            .Single(image => AutomationProperties.GetName(image) == row.Name + "标志");
        var avatar = Visuals(view).OfType<ArtworkImage>()
            .Single(image => AutomationProperties.GetName(image) == row.Name + "头像");
        Equal(logoVisible ? Visibility.Visible : Visibility.Collapsed, logo.Visibility);
        Equal(avatarVisible ? Visibility.Visible : Visibility.Collapsed, avatar.Visibility);
        True(logo.Visibility != Visibility.Visible || avatar.Visibility != Visibility.Visible,
            "platform logo and user avatar are mutually exclusive");
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
