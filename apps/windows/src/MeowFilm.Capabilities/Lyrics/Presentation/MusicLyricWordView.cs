using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using MeowFilm.Capabilities.Library;

namespace MeowFilm.Capabilities;

/// <summary>Native text shaping and partial-word highlighting. No web renderer or timer.</summary>
public sealed class MusicLyricWordView : Control
{
    public static readonly DependencyProperty WordProperty = DependencyProperty.Register(nameof(Word),
        typeof(MusicLyricWord), typeof(MusicLyricWordView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(nameof(Position),
        typeof(double), typeof(MusicLyricWordView), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty HighlightProperty = DependencyProperty.Register(nameof(Highlight),
        typeof(Brush), typeof(MusicLyricWordView), new FrameworkPropertyMetadata(Brushes.Crimson, FrameworkPropertyMetadataOptions.AffectsRender));
    public MusicLyricWord? Word { get => (MusicLyricWord?)GetValue(WordProperty); set => SetValue(WordProperty, value); }
    public double Position { get => (double)GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    public Brush Highlight { get => (Brush)GetValue(HighlightProperty); set => SetValue(HighlightProperty, value); }
    public double WordProgress => Word is { } word && double.IsFinite(Position)
        ? MusicLyricTiming.Progress(word.Time, word.Duration, Position) : 0;
    private FormattedText Format(Brush brush) => new(Word?.Text ?? "", CultureInfo.CurrentUICulture, FlowDirection,
        new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    protected override Size MeasureOverride(Size constraint)
    {
        var text = Format(Foreground ?? SystemColors.WindowTextBrush);
        return new(text.WidthIncludingTrailingWhitespace, text.Height);
    }
    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        var normal = Format(SystemParameters.HighContrast ? SystemColors.WindowTextBrush : Foreground ?? SystemColors.WindowTextBrush);
        drawing.DrawText(normal, new());
        double width = normal.WidthIncludingTrailingWhitespace, progress = WordProgress;
        if (progress <= 0 || width <= 0) return;
        var clip = new Rect(FlowDirection == FlowDirection.RightToLeft ? width * (1 - progress) : 0,
            0, width * progress, normal.Height);
        drawing.PushClip(new RectangleGeometry(clip));
        drawing.DrawText(Format(SystemParameters.HighContrast ? SystemColors.HighlightBrush : Highlight), new());
        drawing.Pop();
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new WordPeer(this);
    private sealed class WordPeer(MusicLyricWordView owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetNameCore() => owner.Word?.Text ?? "";
        protected override string GetClassNameCore() => nameof(MusicLyricWordView);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;
    }
}
