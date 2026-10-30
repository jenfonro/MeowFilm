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
    private Thickness lyricEdgeSpace;

    public MusicPlayerDetailView(MusicCapability module)
    {
        this.module = module;
        InitializeComponent();
        DataContext = module;
        DetailPlayerHost.Content = new MusicPlayerView(module);
        DetailRecord.PaletteUpdated += (_, _) => module.RefreshPlayerAppearance(DetailRecord.PaletteBrush);
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
            centeredLyric = centeringLyric = -1;
            if (IsVisible) { QueueCenterLyric(); SyncNeedle(); }
            else
            {
                StopLyricMotion();
                LyricOptionsPopup.IsOpen = false;
                NeedleRotation.BeginAnimation(RotateTransform.AngleProperty, null);
            }
        };
        module.PropertyChanged += OnModuleChanged;
    }

    internal Task ActivateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UpdateLyricViewport();
        QueueCenterLyric();
        SyncNeedle();
        return module.RestoreLyricsAsync();
    }

    internal void Deactivate()
    {
        StopLyricMotion();
        module.CancelLyricPreview();
        LyricOptionsPopup.IsOpen = false;
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
        if (LyricList.ActualHeight <= 0) return;
        var space = MusicLyricRowMarginConverter.EdgeSpace(LyricList.ActualHeight, module.IsPlaying);
        if (lyricEdgeSpace != space)
        {
            lyricMotionEpoch++;
            centeredLyric = centeringLyric = -1;
            if (Descendant<ScrollViewer>(LyricList) is { } scroll) MusicLyricScroll.Stop(scroll);
            lyricEdgeSpace = space;
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
        if (SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast)
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

    private void CenterLyric()
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
        centeringLyric = index;
        var anchor = MusicLyricRowMarginConverter.ReadingAnchor(module.IsPlaying);
        MusicLyricScroll.Move(scroll, absoluteCenter - scroll.ViewportHeight * anchor,
            settled: () => SettleCenteredLyric(index, scroll, epoch, anchor));
    }

    private void SettleCenteredLyric(int index, ScrollViewer scroll, long epoch, double anchor)
    {
        if (epoch != lyricMotionEpoch || !IsVisible || module.HasLyricPreview ||
            lyricResumeTimer.IsEnabled || module.LyricDisplayIndex != index) return;
        LyricList.UpdateLayout();
        var row = RealizeLyric(index);
        if (row is null)
        {
            if (centeringLyric == index) centeringLyric = -1;
            return;
        }
        var rowCenter = row.TranslatePoint(new Point(0, row.ActualHeight / 2), scroll).Y;
        var correction = rowCenter - scroll.ViewportHeight * anchor;
        if (Math.Abs(correction) >= .5)
            MusicLyricScroll.Settle(scroll, scroll.VerticalOffset + correction);
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

    private void LyricOptions_Click(object sender, RoutedEventArgs e) =>
        LyricOptionsPopup.IsOpen = !LyricOptionsPopup.IsOpen;
    private async void Lyrics_Click(object sender, RoutedEventArgs e) => await module.RefreshLyricsAsync();
    private void CancelLyrics_Click(object sender, RoutedEventArgs e) => module.CancelLyrics();
    private async void ImportLyrics_Click(object sender, RoutedEventArgs e) => await module.ImportLocalLyricsAsync();
    private async void RemoveLocalLyrics_Click(object sender, RoutedEventArgs e) => await module.RemoveLocalLyricsAsync();
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
