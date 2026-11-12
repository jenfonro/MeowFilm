using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using MeowFilm.Desktop;

namespace MeowFilm.Windows.Tests;

public static partial class DesktopTests
{
    private static IEnumerable<TestCase> PresentationCases()
    {
        yield return Case("window notices update deduplicate dismiss and never occupy page layout", async () =>
        {
            var root = new Grid();
            root.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri("/MeowFilm.Desktop;component/Theme.xaml", UriKind.Relative) });
            var page = new StackPanel();
            var notice = new NoticeOutlet { Message = "测试通知" };
            page.Children.Add(new Border { Height = 80 }); page.Children.Add(notice);
            var stack = new NoticeStack();
            root.Children.Add(page); root.Children.Add(stack);
            root.AddHandler(NoticeOutlet.NoticeEvent, new EventHandler<DesktopNoticeEventArgs>((_, e) => stack.Show(e.Outlet, e.Message)));
            using var source = new HwndSource(new HwndSourceParameters("notice-code-fixture")
            {
                WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x80,
                PositionX = -32000, PositionY = -32000, Width = 600, Height = 300
            });
            source.RootVisual = root;
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
            DesktopTheme.SetIsHighContrast(root, true);
            Equal(SystemColors.WindowBrush, card.Background, "live system contrast");
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
            f.Shell.Left = -32000; f.Shell.Top = -32000;
            f.Shell.ShowInTaskbar = false; f.Shell.ShowActivated = false;
            f.Shell.Show(); await f.LoadAsync(); f.Shell.UpdateLayout(); await DrainAsync();
            var surface = f.Element<Grid>("PlayerDetailSurface");
            var transform = (TranslateTransform)f.Shell.FindName("PlayerDetailTransform");
            var host = f.Element<ContentControl>("PlayerDetailHost");
            module.Request(DesktopGlobalSurface.PlayerDetail);
            await UntilAsync(() => module.PlayerActivations.Count == 1, "first preparation entered");
            Equal(Visibility.Visible, surface.Visibility, "pending data does not hide the base");
            if (SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast)
                True(transform.HasAnimatedProperties, "entrance begins while data is pending");
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
            if (SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast)
                await UntilAsync(() => Math.Abs(transform.Y) < .5, "the entrance completes independently of data");
            True(!second.Task.IsCompleted, "data is still pending after the entrance completes");
            second.SetResult();
        });
        yield return Case("global surface animations reverse from their presented offsets and release content", async () =>
        {
            var module = new ImmersiveProbe();
            await using var f = new Fixture([module]);
            f.Shell.Left = -32000;
            f.Shell.Top = -32000;
            f.Shell.ShowInTaskbar = false;
            f.Shell.ShowActivated = false;
            f.Shell.Width = 1110;
            f.Shell.Height = 800;
            f.Shell.Show();
            await f.LoadAsync();
            f.Shell.UpdateLayout();
            await DrainAsync();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            Task CallAsync(string name) => (Task)typeof(ShellWindow).GetMethod(name, flags)!.Invoke(f.Shell, null)!;

            module.Request(DesktopGlobalSurface.PlayerDetail);
            var playerSurface = f.Element<Grid>("PlayerDetailSurface");
            var playerTransform = (TranslateTransform)f.Shell.FindName("PlayerDetailTransform");
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
            True(f.Element<ContentControl>("PlayerDetailHost").Content is null &&
                !playerTransform.HasAnimatedProperties, "player surface releases content and animation");

            module.Request(DesktopGlobalSurface.PlayQueue);
            var queueSurface = f.Element<Border>("QueueSurface");
            var queueTransform = (TranslateTransform)f.Shell.FindName("QueueTransform");
            var queueWidth = queueSurface.Width + 24;
            await UntilAsync(() => queueSurface.Visibility == Visibility.Visible &&
                queueTransform.X > 1 && queueTransform.X < queueWidth - 1, "queue opening frame");
            var queueOffset = queueTransform.X;
            await CallAsync("CloseQueueAsync");
            var reversedQueueOffset = queueTransform.X;
            True(Math.Abs(reversedQueueOffset - queueOffset) < 3,
                $"queue reversal keeps the presented offset ({queueOffset:F3} -> {reversedQueueOffset:F3})");
            await UntilAsync(() => queueSurface.Visibility == Visibility.Collapsed, "queue close settles");
            True(f.Element<ContentControl>("QueueHost").Content is null &&
                !queueTransform.HasAnimatedProperties, "queue surface releases content and animation");

            f.Shell.Width = 900;
            f.Shell.Height = 650;
            f.Shell.UpdateLayout();
            await DrainAsync();
            var clip = (RectangleGeometry)f.Element<Grid>("PresentationSurface").Clip;
            Equal(0d, clip.RadiusX, "no duplicate WPF corner");
        });
    }
}
