using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace MeowFilm.Capabilities;

public partial class MusicPlayerDetailView : UserControl
{
    private readonly MusicCapability module;
    private readonly DispatcherTimer lyricResumeTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private int centeredLyric = -1;
    private int centeringLyric = -1;
    private long lyricMotionEpoch;
    private bool centerQueued;
    private bool realizingLyric;
    private bool? needlePlaying;
    private CancellationTokenSource? activationRequest;
    private IDisposable? artworkPresentation;

    public MusicPlayerDetailView(MusicCapability module)
    {
        this.module = module;
        InitializeComponent();
        DataContext = module;
        var footer = new MusicPlayerView(module, showArtwork: false);
        // The footer's own theme supplies the ordinary-page defaults; this page's backdrop palette wins.
        footer.Resources.MergedDictionaries.Add((ResourceDictionary)Resources["Player.FooterPalette"]);
        DetailPlayerHost.Content = footer;
        DetailRecord.PaletteUpdated += (_, _) => module.RefreshPlayerAppearance(DetailRecord.PaletteBrush, animate: IsVisible);
        lyricResumeTimer.Tick += (_, _) =>
        {
            lyricResumeTimer.Stop();
            if (!IsVisible) return;
            module.CancelLyricPreview();
            CenterLyric();
        };
        LyricList.RequestBringIntoView += (_, e) =>
        {
            if (realizingLyric || e.OriginalSource is not FrameworkElement element) return;
            var index = module.LyricDisplayIndex;
            if (index >= 0 && index < module.Lyrics.Count &&
                ReferenceEquals(element.DataContext, module.Lyrics[index]))
                e.Handled = true;
        };
        LyricList.PreviewMouseWheel += (_, e) =>
        {
            HoldLyrics();
            if (Descendant<ScrollViewer>(LyricList) is not { } scroll) return;
            var distance = SystemParameters.WheelScrollLines < 0
                ? scroll.ViewportHeight : SystemParameters.WheelScrollLines * 24d;
            MusicLyricScroll.Move(scroll, scroll.VerticalOffset - e.Delta / 120d * distance, manual: true);
            e.Handled = true;
        };
        LyricList.PreviewMouseDown += (_, _) => HoldLyrics();
        LyricList.PreviewKeyDown += (_, _) => HoldLyrics();
        LyricList.SizeChanged += (_, _) => UpdateLyricViewport();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { QueueCenterLyric(); SyncNeedle(); }
            else
            {
                centeredLyric = centeringLyric = -1;
                StopLyricMotion();
                NeedleRotation.BeginAnimation(RotateTransform.AngleProperty, null);
            }
        };
        Unloaded += (_, _) =>
        {
            activationRequest?.Cancel();
            artworkPresentation?.Dispose(); artworkPresentation = null;
        };
        module.PropertyChanged += OnModuleChanged;
    }

    internal async Task ActivateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); activationRequest?.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        activationRequest = request;
        IDisposable? prepared = null;
        try
        {
            while (true)
            {
                var identity = module.PlaybackPresentationKey;
                var address = DetailRecord.Address;
                await module.RestoreLyricsAsync().WaitAsync(request.Token);
                var latest = await DetailRecord.PrepareArtworkAsync(request.Token);
                prepared?.Dispose(); prepared = latest;
                request.Token.ThrowIfCancellationRequested();
                if (identity == module.PlaybackPresentationKey && address == DetailRecord.Address) break;
            }
            UpdateLayout();
            UpdateLyricViewport();
            CenterLyric(animate: false);
            SyncNeedle();
            artworkPresentation?.Dispose(); artworkPresentation = prepared; prepared = null;
        }
        finally
        {
            prepared?.Dispose();
            if (ReferenceEquals(activationRequest, request)) activationRequest = null;
        }
    }

    internal void Deactivate()
    {
        activationRequest?.Cancel();
        if (!IsVisible) { artworkPresentation?.Dispose(); artworkPresentation = null; }
        StopLyricMotion();
        module.CancelLyricPreview();
        ((MusicPlayerView)DetailPlayerHost.Content).Deactivate();
    }

    private void OnModuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MusicCapability.IsPlaying))
        {
            if (IsVisible) SyncNeedle();
            UpdateLyricViewport();
        }
        if (e.PropertyName == nameof(MusicCapability.LyricDisplayIndex) && IsVisible &&
            !module.HasLyricPreview && !lyricResumeTimer.IsEnabled &&
            module.LyricDisplayIndex != centeredLyric && module.LyricDisplayIndex != centeringLyric)
            QueueCenterLyric();
        if (e.PropertyName == nameof(MusicCapability.CanConfirmLyricPreview) &&
            !module.HasLyricPreview && !lyricResumeTimer.IsEnabled)
        {
            centeredLyric = centeringLyric = -1;
            QueueCenterLyric();
        }
    }

    private void UpdateLyricViewport()
    {
        if (module.Lyrics.Count == 0 || Descendant<ItemsPresenter>(LyricList) is not { } presenter || LyricList.ActualHeight <= 0) return;
        var anchor = module.IsPlaying ? .24 : .35;
        var margin = new Thickness(0, Math.Max(24, LyricList.ActualHeight * anchor - 22),
            0, Math.Max(24, LyricList.ActualHeight * (1 - anchor) - 22));
        if (presenter.Margin != margin)
        {
            lyricMotionEpoch++;
            centeredLyric = centeringLyric = -1;
            if (Descendant<ScrollViewer>(LyricList) is { } scroll) MusicLyricScroll.Stop(scroll);
            presenter.Margin = margin;
        }
        if (!lyricResumeTimer.IsEnabled) QueueCenterLyric();
    }

    private void SyncNeedle()
    {
        if (needlePlaying == module.IsPlaying) return;
        needlePlaying = module.IsPlaying;
        var from = NeedleRotation.Angle;
        NeedleRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        NeedleRotation.Angle = module.IsPlaying ? 25 : 0;
        if (IsVisible && SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast)
            NeedleRotation.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(from, NeedleRotation.Angle, TimeSpan.FromMilliseconds(450))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    FillBehavior = FillBehavior.Stop
                });
    }

    private void HoldLyrics()
    {
        lyricMotionEpoch++;
        centeredLyric = centeringLyric = -1;
        lyricResumeTimer.Stop();
        lyricResumeTimer.Start();
        if (Descendant<ScrollViewer>(LyricList) is { } scroll) MusicLyricScroll.Stop(scroll);
    }

    private void StopLyricMotion()
    {
        lyricMotionEpoch++;
        centeredLyric = centeringLyric = -1;
        lyricResumeTimer.Stop();
        if (Descendant<ScrollViewer>(LyricList) is { } scroll) MusicLyricScroll.Stop(scroll);
    }

    private void QueueCenterLyric()
    {
        if (centerQueued) return;
        centerQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            centerQueued = false;
            if (IsVisible && !lyricResumeTimer.IsEnabled && !module.HasLyricPreview)
                CenterLyric();
        }));
    }

    private void CenterLyric(bool animate = true)
    {
        var index = module.LyricDisplayIndex;
        if (index < 0 || index >= module.Lyrics.Count ||
            index == centeringLyric || index == centeredLyric && centeringLyric < 0) return;
        if (Descendant<ScrollViewer>(LyricList) is not { } scroll) return;
        var originalOffset = scroll.VerticalOffset;
        var row = RealizeLyric(index);
        if (row is null) return;
        var absoluteCenter = scroll.VerticalOffset +
            row.TranslatePoint(new Point(0, row.ActualHeight / 2), scroll).Y;
        if (Math.Abs(scroll.VerticalOffset - originalOffset) >= .5)
            MusicLyricScroll.Settle(scroll, originalOffset);
        var epoch = ++lyricMotionEpoch;
        var anchor = module.IsPlaying ? .24 : .35;
        if (!animate)
        {
            MusicLyricScroll.Settle(scroll, absoluteCenter - scroll.ActualHeight * anchor);
            scroll.UpdateLayout();
            SettleCenteredLyric(index, scroll, epoch, anchor, allowHidden: true);
            return;
        }
        centeringLyric = index;
        MusicLyricScroll.Move(scroll, absoluteCenter - scroll.ActualHeight * anchor,
            settled: () => SettleCenteredLyric(index, scroll, epoch, anchor));
    }

    private void SettleCenteredLyric(int index, ScrollViewer scroll, long epoch, double anchor, bool allowHidden = false)
    {
        if (epoch != lyricMotionEpoch || (!allowHidden && !IsVisible) || module.HasLyricPreview ||
            lyricResumeTimer.IsEnabled || module.LyricDisplayIndex != index) return;
        LyricList.UpdateLayout();
        var row = RealizeLyric(index);
        if (row is null)
        {
            if (centeringLyric == index) centeringLyric = -1;
            return;
        }
        var rowCenter = row.TranslatePoint(new Point(0, row.ActualHeight / 2), scroll).Y;
        var correction = rowCenter - scroll.ActualHeight * anchor;
        if (Math.Abs(correction) >= .5)
        {
            MusicLyricScroll.Settle(scroll, scroll.VerticalOffset + correction);
            scroll.UpdateLayout();
        }
        centeredLyric = index;
        centeringLyric = -1;
    }

    private FrameworkElement? RealizeLyric(int index)
    {
        if (LyricList.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement row) return row;
        realizingLyric = true;
        try
        {
            LyricList.ScrollIntoView(module.Lyrics[index]);
            LyricList.UpdateLayout();
            return LyricList.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement;
        }
        finally { realizingLyric = false; }
    }

    private static T? Descendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            if (Descendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private void LyricPreview_Click(object sender, RoutedEventArgs e)
    { if (sender is Button { Tag: MusicLyricRow row }) module.PreviewLyric(row); }
    private async void PlayLyric_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: MusicLyricRow row }) return;
        lyricResumeTimer.Stop();
        module.PreviewLyric(row);
        await module.ConfirmLyricPreviewAsync();
        QueueCenterLyric();
    }
    private async void DetailRecord_SwipeCommitted(object? sender, MusicRecordSwipeEventArgs e)
    {
        try { await module.StepFromPresentationAsync(e.Direction > 0, e.Identity); }
        finally { DetailRecord.ResolveSwipe(e.Identity); }
    }
}
