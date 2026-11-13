using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

/// <summary>Desktop-only ordinary text: retained glyphs and a narrow outline, without a lyric clock.</summary>
public sealed class MusicDesktopLyricTextView : Control
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text),
        typeof(string), typeof(MusicDesktopLyricTextView),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure |
            FrameworkPropertyMetadataOptions.AffectsRender, (owner, _) => ((MusicDesktopLyricTextView)owner).InvalidateText()));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    private DrawingGroup? textDrawing;
    private Geometry? outline;
    private Size textSize;
    private static readonly Pen OutlinePen = CreateOutlinePen();

    private static Pen CreateOutlinePen()
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(230, 25, 25, 25)), 1.1)
        { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return pen;
    }

    private void InvalidateText()
    {
        textDrawing = null;
        outline = null;
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == FontFamilyProperty || e.Property == FontSizeProperty ||
            e.Property == FontStyleProperty || e.Property == FontWeightProperty ||
            e.Property == FontStretchProperty || e.Property == FlowDirectionProperty ||
            e.Property == LanguageProperty || e.Property == TextOptions.TextFormattingModeProperty)
            InvalidateText();
        else if (e.Property == DesktopTheme.IsHighContrastProperty) InvalidateVisual();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidateText();
    }

    private void EnsureText()
    {
        if (textDrawing is not null) return;
        var text = new FormattedText(Text ?? "", Language.GetSpecificCulture(), FlowDirection,
            new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, Brushes.Black,
            null, TextOptions.GetTextFormattingMode(this), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        textSize = new(text.WidthIncludingTrailingWhitespace, text.Height);
        var shaped = new DrawingGroup();
        using (var drawing = shaped.Open()) drawing.DrawText(text, new());
        // DrawText retains frozen glyph layers; clone once so recoloring can reuse the glyphs.
        textDrawing = shaped.Clone();
        outline = text.BuildGeometry(new());
        outline.Freeze();
    }

    protected override Size MeasureOverride(Size constraint) { EnsureText(); return textSize; }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        EnsureText();
        bool highContrast = SystemParameters.HighContrast || DesktopTheme.GetIsHighContrast(this);
        Paint(textDrawing!, highContrast ? SystemColors.WindowTextBrush : Foreground);
        drawing.DrawGeometry(null, highContrast ? new Pen(SystemColors.WindowBrush, 1.1) : OutlinePen, outline);
        drawing.DrawDrawing(textDrawing);
    }

    private static void Paint(DrawingGroup drawing, Brush brush)
    {
        foreach (var child in drawing.Children)
            if (child is GlyphRunDrawing glyph) glyph.ForegroundBrush = brush;
            else if (child is DrawingGroup group) Paint(group, brush);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new TextPeer(this);
    private sealed class TextPeer(MusicDesktopLyricTextView owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetNameCore() => owner.Text;
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;
    }
}
