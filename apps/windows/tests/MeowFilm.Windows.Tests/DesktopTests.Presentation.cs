using System.Reflection;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Windows.System;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static partial class DesktopTests
{
    private static IEnumerable<TestCase> PresentationCases()
    {
        yield return Case("owned setting notices survive page retirement ordinary notices and expiry until their own receipt clears", async () =>
        {
            var module = new OwnedNoticeProbe();
            var first = new object(); var second = new object();
            const string message = "保存结果未确认";
            module.Set(new(first, message), new(second, message));
            await using var f = new Fixture([module]);
            await f.LoadAsync(); await DrainAsync();
            var stack = f.Element<NoticeStack>("Notices");
            Equal(2, stack.Children.Count, "same-message independent owners remain visible");
            True(LogicalDescendants(stack).OfType<Button>().All(button => button.Visibility == Visibility.Collapsed),
                "unknown receipts cannot be dismissed as if they were settled");
            stack.Show(new object(), message);
            for (int index = 0; index < 5; index++) stack.Show(new object(), "普通通知" + index);
            Equal(5, stack.Children.Count, "ordinary bounded queue does not evict owned notices");
            Click(f.Element<Button>("SettingsButton")); await DrainAsync();
            Click(f.Back); await DrainAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(8500));
            Equal(2, stack.Children.Count, "page retirement and normal expiry leave unknown receipts");
            module.Set(new DesktopOwnedNotice(second, message)); await DrainAsync();
            Equal(1, stack.Children.Count, "one successful receipt clears only its owner");
            module.Set(); await DrainAsync();
            Equal(0, stack.Children.Count, "last successful receipt withdraws its notice");
        }, TimeSpan.FromSeconds(12));
        yield return Case("window notices update deduplicate dismiss and never occupy page layout", async () =>
        {
            var root = new Grid();
            root.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri("ms-appx:///MeowFilm.Desktop/Theme.xaml") });
            var page = new StackPanel();
            var notice = new NoticeOutlet { Message = "测试通知" };
            page.Children.Add(new Border { Height = 80 }); page.Children.Add(notice);
            var stack = new NoticeStack();
            root.Children.Add(page); root.Children.Add(stack);
            NoticeOutlet.SetReceiver(root, e => stack.Show(e.Identity, e.Message));
            using var source = new DesktopTestUi.Host(root, 600, 300);
            root.Measure(new(600, 300)); root.Arrange(new(0, 0, 600, 300)); root.UpdateLayout();
            await DrainAsync();
            Equal(1, stack.Children.Count, "loaded binding reaches this host");
            Equal(80d, page.DesiredSize.Height, "notice is not a content row");
            notice.Message = "测试更新"; await DrainAsync();
            Equal(1, stack.Children.Count, "same source replaced");
            stack.Show(new object(), "测试更新");
            Equal(1, stack.Children.Count, "same message deduplicated");
            for (int i = 0; i < 5; i++) stack.Show(new object(), "通知" + i);
            Equal(3, stack.Children.Count, "bounded queue");
            var card = (Border)stack.Children[2];
            True(card.Background is not null, "card paints a real surface");
            True(DesktopVisual.Resource(card, "MeowFilm.Brush.FloatingSurface") is Brush, "card uses theme surface resources");
            Click(LogicalDescendants(card).OfType<Button>().Single());
            Equal(2, stack.Children.Count, "close works without page mutation");
            stack.Clear();
            notice.Message = "隐藏前"; await DrainAsync(); Equal(1, stack.Children.Count, "new message");
            page.Visibility = Visibility.Collapsed; await DrainAsync();
            Equal(0, stack.Children.Count, "hidden page withdraws");
            page.Visibility = Visibility.Visible; await DrainAsync();
            Equal(0, stack.Children.Count, "returning does not replay an already consumed save");
            notice.Message = "隐藏前"; await DrainAsync();
            Equal(1, stack.Children.Count, "a second real action can publish the same message");
            page.Visibility = Visibility.Collapsed; await DrainAsync();
            Equal(0, stack.Children.Count, "the second notice also withdraws");
            notice.Message = "隐藏后"; await DrainAsync();
            Equal(0, stack.Children.Count, "hidden changes do not notify");
            page.Visibility = Visibility.Visible; await DrainAsync();
            Equal(1, stack.Children.Count, "visible error is still surfaced");
            page.Children.Remove(notice); await DrainAsync();
            Equal(0, stack.Children.Count, "unload retires source");
            page.Children.Add(notice); await DrainAsync();
            Equal(0, stack.Children.Count, "remount does not replay the consumed error");
            notice.Message = "隐藏后"; await DrainAsync();
            Equal(1, stack.Children.Count, "an explicit same-message action after remount still notifies");
        });
        yield return Case("player entrance and close never wait for data and late activation cannot change a newer page", async () =>
        {
            var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var module = new ImmersiveProbe { PlayerReady = first.Task };
            await using var f = new Fixture([module]);
            f.Shell.Show(); await f.LoadAsync(); f.Shell.Root.UpdateLayout(); await DrainAsync();
            var surface = f.Element<Grid>("PlayerDetailSurface");
            var transform = (TranslateTransform)f.Shell.Root.FindName("PlayerDetailTransform");
            var host = f.Element<ContentControl>("PlayerDetailHost");
            module.Request(DesktopGlobalSurface.PlayerDetail);
            await UntilAsync(() => module.PlayerActivations.Count == 1, "first preparation entered");
            Equal(Visibility.Visible, surface.Visibility, "pending data does not hide the base");
            if (new global::Windows.UI.ViewManagement.UISettings().AnimationsEnabled && !new global::Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
                True(transform.Y >= 0, "entrance runs while data is pending");
            True(host.Content is not null && host.ActualHeight > 0, "preparation has its final layout");
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            await (Task)typeof(ShellWindow).GetMethod("ClosePlayerDetailAsync", flags)!.Invoke(f.Shell, null)!;
            await UntilAsync(() => surface.Visibility == Visibility.Collapsed, "pending presentation closes without data");
            True(host.Content is null && module.PlayerActivations[0].IsCancellationRequested,
                "pending close cancels preparation and releases its tree");
            module.PlayerReady = second.Task;
            module.Request(DesktopGlobalSurface.PlayerDetail);
            await UntilAsync(() => module.PlayerActivations.Count == 2, "replacement preparation entered");
            first.SetResult(); await DrainAsync();
            Equal(Visibility.Visible, surface.Visibility, "replacement is already visible while its data is pending");
            await UntilAsync(() => surface.Visibility == Visibility.Visible, "ready replacement enters");
            Same(module.GetGlobalSurface(DesktopGlobalSurface.PlayerDetail), host.Content, "the ready retained view is presented");
            if (new global::Windows.UI.ViewManagement.UISettings().AnimationsEnabled && !new global::Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
                await UntilAsync(() => Math.Abs(transform.Y) < .5, "the entrance completes independently of data");
            True(!second.Task.IsCompleted, "data is still pending after the entrance completes");
            second.SetResult();
        });
        yield return Case("global surface animations reverse from their presented offsets and release content", async () =>
        {
            var module = new ImmersiveProbe();
            await using var f = new Fixture([module]);
            f.Shell.Show();
            await f.LoadAsync();
            f.Shell.Root.UpdateLayout();
            await DrainAsync();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            Task CallAsync(string name) => (Task)typeof(ShellWindow).GetMethod(name, flags)!.Invoke(f.Shell, null)!;

            module.Request(DesktopGlobalSurface.PlayerDetail);
            var playerSurface = f.Element<Grid>("PlayerDetailSurface");
            var playerTransform = (TranslateTransform)f.Shell.Root.FindName("PlayerDetailTransform");
            var playerHeight = Math.Max(1, f.Element<Grid>("PresentationSurface").ActualHeight);
            await UntilAsync(() => playerSurface.Visibility == Visibility.Visible &&
                playerTransform.Y > 1 && playerTransform.Y < playerHeight - 1, "player opening frame");
            var playerOffset = playerTransform.Y;
            await CallAsync("ClosePlayerDetailAsync");
            var reversedPlayerOffset = playerTransform.Y;
            True(Math.Abs(reversedPlayerOffset - playerOffset) < 3,
                $"player reversal keeps the presented offset ({playerOffset:F3} -> {reversedPlayerOffset:F3})");
            await UntilAsync(() => playerSurface.Visibility == Visibility.Collapsed, "player close settles");
            Equal(playerHeight, playerTransform.Y, "complete page slides beyond the full window height");
            True(f.Element<ContentControl>("PlayerDetailHost").Content is null, "player surface releases content and animation");

            module.Request(DesktopGlobalSurface.PlayQueue);
            var queueSurface = f.Element<Border>("QueueSurface");
            var queueTransform = (TranslateTransform)f.Shell.Root.FindName("QueueTransform");
            var queueWidth = queueSurface.Width + 24;
            await UntilAsync(() => queueSurface.Visibility == Visibility.Visible &&
                queueTransform.X > 1 && queueTransform.X < queueWidth - 1, "queue opening frame");
            var queueOffset = queueTransform.X;
            await CallAsync("CloseQueueAsync");
            var reversedQueueOffset = queueTransform.X;
            True(Math.Abs(reversedQueueOffset - queueOffset) < 3,
                $"queue reversal keeps the presented offset ({queueOffset:F3} -> {reversedQueueOffset:F3})");
            await UntilAsync(() => queueSurface.Visibility == Visibility.Collapsed, "queue close settles");
            True(f.Element<ContentControl>("QueueHost").Content is null, "queue surface releases content and animation");

            f.Shell.Root.UpdateLayout();
            await DrainAsync();
            var clip = (RectangleGeometry)f.Element<Grid>("PresentationSurface").Clip;
            Equal(new Rect(0, 0, f.Shell.Root.ActualWidth, f.Shell.Root.ActualHeight), clip.Rect, "root clip follows actual content bounds");
        });
    }

    private sealed class OwnedNoticeProbe() : ProbeModule("owned-notices"), IDesktopOwnedNoticeSource
    {
        public IReadOnlyList<DesktopOwnedNotice> OwnedNotices { get; private set; } = [];
        public event EventHandler? OwnedNoticesChanged;
        internal void Set(params DesktopOwnedNotice[] notices)
        {
            OwnedNotices = notices;
            OwnedNoticesChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
