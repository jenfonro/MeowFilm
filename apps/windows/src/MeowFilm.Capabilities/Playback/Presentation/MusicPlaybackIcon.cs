// SPDX-License-Identifier: GPL-3.0-only
// iWebPlus-derived heart/FM/scene artwork retains Apache-2.0 attribution; see LICENSES/NOTICE.md.
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MeowFilm.Capabilities;

/// <summary>Native playback and FM scene artwork, drawn without a control template.</summary>
public sealed class MusicPlaybackIcon : Control
{
    public static readonly DependencyProperty KeyProperty = DependencyProperty.Register(nameof(Key),
        typeof(string), typeof(MusicPlaybackIcon),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public string Key { get => (string)GetValue(KeyProperty); set => SetValue(KeyProperty, value); }

    static MusicPlaybackIcon()
    {
        WidthProperty.OverrideMetadata(typeof(MusicPlaybackIcon), new FrameworkPropertyMetadata(20d));
        HeightProperty.OverrideMetadata(typeof(MusicPlaybackIcon), new FrameworkPropertyMetadata(20d));
        FocusableProperty.OverrideMetadata(typeof(MusicPlaybackIcon), new FrameworkPropertyMetadata(false));
        IsHitTestVisibleProperty.OverrideMetadata(typeof(MusicPlaybackIcon), new FrameworkPropertyMetadata(false));
        ForegroundProperty.OverrideMetadata(typeof(MusicPlaybackIcon),
            new FrameworkPropertyMetadata(SystemColors.ControlTextBrush,
                FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));
    }

    private sealed record Icon(double Viewport, Geometry[] Shapes);
    private static readonly IReadOnlyDictionary<string, Icon> Icons = CreateIcons();

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (Key is not { } key || !Icons.TryGetValue(key, out var icon) || Foreground is not { } foreground)
            return;
        var scale = Math.Min(RenderSize.Width, RenderSize.Height) / icon.Viewport;
        if (scale <= 0) return;
        drawing.PushTransform(new MatrixTransform(scale, 0, 0, scale,
            (RenderSize.Width - icon.Viewport * scale) / 2,
            (RenderSize.Height - icon.Viewport * scale) / 2));
        foreach (var geometry in icon.Shapes) drawing.DrawGeometry(foreground, null, geometry);
        drawing.Pop();
    }

    private static Geometry Frozen(Geometry geometry)
    {
        geometry.Freeze();
        return geometry;
    }

    private static Geometry Fill(string data) => Frozen(Geometry.Parse(data));

    private static Geometry Stroke(string data, double width, bool round = true)
    {
        var pen = new Pen(Brushes.Black, width)
        {
            StartLineCap = round ? PenLineCap.Round : PenLineCap.Flat,
            EndLineCap = round ? PenLineCap.Round : PenLineCap.Flat,
            LineJoin = round ? PenLineJoin.Round : PenLineJoin.Miter,
            MiterLimit = 4
        };
        // Widen once in source coordinates; rendering then needs only the current Foreground.
        return Frozen(Geometry.Parse(data).GetWidenedPathGeometry(pen, .01, ToleranceType.Absolute));
    }

    private static Geometry Text(string value, double x, double baseline, double size,
        FontWeight weight, FontStyle style, Transform? transform = null)
    {
        var text = new FormattedText(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(SystemFonts.MessageFontFamily, style, weight, FontStretches.Normal),
            size, Brushes.Black, 1d);
        // SVG text-anchor="middle" uses the advance width; y is the alphabetic baseline.
        var geometry = text.BuildGeometry(new Point(x - text.Width / 2, baseline - text.Baseline));
        if (transform is not null) geometry.Transform = transform;
        return Frozen(geometry);
    }

    private static IReadOnlyDictionary<string, Icon> CreateIcons()
    {
        // Ordinary modes follow the supplied screenshots, including solid arrowheads and the crossing gap.
        var repeat = Stroke("M 22 13 C 22 17.42 17.52 21 12 21 C 6.48 21 2 17.42 2 13 C 2 8.58 6.48 5 12 5 L 20 5", 1.6);
        var repeatArrow = Fill("F1 M 18.8 1.9 L 22.5 5 L 18.8 8.1 Z");
        return new Dictionary<string, Icon>(StringComparer.Ordinal)
        {
            ["order"] = new(24, [
                Stroke("M 2 6 L 20 6 M 2 18 L 20 18", 1.6),
                Fill("F1 M 18 3 L 22 6 L 18 9 Z M 18 15 L 22 18 L 18 21 Z")]),
            ["repeat"] = new(24, [repeat, repeatArrow]),
            ["single"] = new(24, [repeat, repeatArrow,
                Text("1", 12, 17, 10, FontWeights.Bold, FontStyles.Normal)]),
            ["shuffle"] = new(24, [
                Stroke("M 2 20 L 4 20 C 12 20 12 4 20 4 M 2 4 L 4 4 C 7 4 8.4 6 9.8 8.4 M 14.2 15.6 C 15.6 18 17 20 20 20", 1.6),
                Fill("F1 M 18 1 L 22 4 L 18 7 Z M 18 17 L 22 20 L 18 23 Z")]),
            ["heartbeat"] = new(24, [
                Stroke("M 21 5 A 5.5 5.5 0 0 0 13 4 L 12 5 L 11 4 A 5.5 5.5 0 0 0 3 12 L 12 21 L 21 12 A 5.5 5.5 0 0 0 21 5 Z M 2 12 L 7 12 L 9 8 L 13 16 L 15 12 L 22 12", 1.65)]),
            ["fm"] = new(24, [
                Stroke("M 12 12 C 9 7 7 7 5 8 C 1 10 1 14 5 16 C 7 17 9 17 12 12 C 15 7 17 7 19 8 C 23 10 23 14 19 16 C 17 17 15 17 12 12 Z", 1.8, round: false)]),
            // iWebPlus fm-icons.js: all 42 source scenes, preserving official key spellings.
            ["NIGHT_EMO"] = new(32, [
                Fill("F1 M 25 6 A 3 3 0 1 0 19 6 A 3 3 0 1 0 25 6 Z"),
                Fill("F1 M 16 9 C 11 10 7 17 7 23 C 7 27 10 29 13 27 L 19 20 L 24 28 A 1.7 1.7 0 0 0 27 27 L 21 17 A 2 2 0 0 0 18 16.5 L 15 19.5 L 14 15.5 L 17 11.5 Z"),
                Stroke("M 17 11 L 21 16 L 26 17", 3)]),
            ["EXERCISE"] = new(32, [
                Fill("F1 M 23 5 A 3 3 0 1 0 17 5 A 3 3 0 1 0 23 5 Z"),
                Stroke("M 19 11 L 15 18 L 20 22 L 22 28 M 14 19 L 10 24 L 5 24 M 19 11 L 12 9 L 7 12 M 19 11 L 23 14 L 28 11", 3.5)]),
            ["SLEEP_HELP"] = new(32, [
                Fill("F1 M 28 18 A 12 12 0 0 1 14 4 A 12 12 0 1 0 28 18 Z"),
                Stroke("M 22 4 L 28 4 L 22 11 L 28 11", 2)]),
            ["RELAX"] = new(32, [
                Fill("F1 M 20 7 A 3 3 0 1 0 14 7 A 3 3 0 1 0 20 7 Z"),
                Stroke("M 17 13 L 13 20 L 21 19 L 23 14 M 17 13 L 11 14 L 6 8 M 17 13 L 23 10 L 27 4 M 3 10 C 9 23 23 25 29 8 M 13 25 L 11 29 M 22 24 L 23 29", 2.8)]),
            ["CHEERFUL"] = new(32, [
                Fill("F1 M 18 6 A 3 3 0 1 0 12 6 A 3 3 0 1 0 18 6 Z"),
                Stroke("M 16 12 L 14 20 L 23 21 L 18 28 M 14 20 L 6 27 M 16 12 L 9 14 L 4 12 M 16 12 L 22 8 L 25 2", 3.5)]),
            ["LYRICAL"] = new(32, [
                Fill("F1 M 15 7 A 3 3 0 1 0 9 7 A 3 3 0 1 0 15 7 Z"),
                Fill("F1 M 22 6 C 18 2 15 8 22 12 C 29 8 26 2 22 6 Z M 10 12 C 8 15 8 20 9 27 L 13 27 L 13 19 L 17 21 L 17 27 L 26 27 L 26 21 C 26 17 20 14 16 16 L 14 12 Z"),
                Stroke("M 8 10 L 5 15 L 6 27", 2.5)]),
            ["CURE"] = new(32, [
                Fill("F1 M 14 7 A 3 3 0 1 0 8 7 A 3 3 0 1 0 14 7 Z"),
                Fill("F1 M 24 7 A 3 3 0 1 0 18 7 A 3 3 0 1 0 24 7 Z"),
                Stroke("M 5 27 L 7 16 Q 8 11 13 14 L 18 18 L 24 18 M 27 27 L 25 16 Q 24 11 19 14 L 14 18 L 8 18 M 11 21 L 10 27 M 21 21 L 22 27", 3.5)]),
            ["FOCUS"] = new(32, [
                Fill("F1 M 7 6 L 21 9 L 21 29 L 7 26 Q 4 25.5 4 22 L 4 10 Q 4 6 7 6 Z"),
                Stroke("M 7 6 L 7 4 L 25 7 Q 27 7.3 27 9 L 27 25", 2.5)]),
            ["SWEET"] = new(32, [
                Stroke("M 13 22 L 5 15 C -1 9 7 3 12 8 L 13 9 L 14 8 C 18 4 24 8 22 13", 2.5),
                Fill("F1 M 22 28 L 14 20 C 9 15 16 9 20 13 L 22 15 L 24 13 C 28 9 34 15 29 20 Z")]),
            ["RHYTHM_BLUES"] = new(32, [
                Fill("F1 M 27 8 A 6 6 0 1 0 15 8 A 6 6 0 1 0 27 8 Z"),
                Stroke("M 16 12 L 7 22 L 11 25 L 22 15 M 9 26 C 6 30 11 31 14 28 L 21 20 Q 25 16 28 19", 2.5)]),
            ["RAINY"] = new(32, [
                Fill("F1 M 3 17 C 4 10 9 6 16 6 C 23 6 28 10 29 17 C 26 15 24 15 21 17 C 18 15 14 15 11 17 C 8 15 6 15 3 17 Z"),
                Stroke("M 16 6 L 16 25 A 4 4 0 0 1 8 25 M 7 3 L 7 5 M 16 1 L 16 3 M 25 3 L 25 5", 2.5)]),
            ["GAMES"] = new(32, [
                Fill("F0 M 10 7 L 22 7 C 27 7 29 12 30 22 C 31 29 25 30 22 24 L 20 21 L 12 21 L 10 24 C 7 30 1 29 2 22 C 3 12 5 7 10 7 Z M 8 12 L 8 15 L 5 15 L 5 18 L 8 18 L 8 21 L 11 21 L 11 18 L 14 18 L 14 15 L 11 15 L 11 12 Z M 21 12 A 1.5 1.5 0 1 0 21 15 A 1.5 1.5 0 0 0 21 12 Z M 26 17 A 1.5 1.5 0 1 0 26 20 A 1.5 1.5 0 0 0 26 17 Z")]),
            ["RAP"] = new(32, [
                Stroke("M 7 3 C 8 13 22 15 25 3 M 3 14 L 27 9 L 30 25 L 6 30 Z", 2.5),
                Text("RICH", 17, 23, 8, FontWeights.Bold, FontStyles.Normal, new RotateTransform(-12, 17, 20))]),
            ["K_POP"] = new(32, [
                Text("한", 16, 26, 28, FontWeights.Bold, FontStyles.Normal)]),
            ["ORIGINAL_MUSICIAL"] = new(32, [
                Fill("F0 M 28 16 A 12 12 0 1 1 16 4 L 16 14 Z M 16 14 A 2 2 0 1 0 16 18 A 2 2 0 0 0 16 14 Z"),
                Fill("F1 M 19 12 L 24 2 L 30 7 L 19 13 Z")]),
            ["ELECTRONIC"] = new(32, [
                Fill("F1 M 4 17 L 10 17 L 10 22 L 4 22 Z M 4 24 L 10 24 L 10 29 L 4 29 Z M 4 10 L 10 10 L 10 15 L 4 15 Z M 13 24 L 19 24 L 19 29 L 13 29 Z M 13 17 L 19 17 L 19 22 L 13 22 Z M 22 24 L 28 24 L 28 29 L 22 29 Z M 22 17 L 28 17 L 28 22 L 22 22 Z M 22 10 L 28 10 L 28 15 L 22 15 Z M 22 3 L 28 3 L 28 8 L 22 8 Z")]),
            ["COMMUTE"] = new(32, [
                Fill("F0 M 9 3 L 23 3 A 5 5 0 0 1 28 8 L 28 25 A 3 3 0 0 1 25 28 L 25 30 L 21 30 L 21 28 L 11 28 L 11 30 L 7 30 L 7 28 A 3 3 0 0 1 4 25 L 4 8 A 5 5 0 0 1 9 3 Z M 11 5 L 11 7 L 21 7 L 21 5 Z M 7 10 L 7 19 Q 16 22 25 19 L 25 10 Z M 7 23 L 7 25 L 12 25 L 12 23 Z M 20 23 L 20 25 L 25 25 L 25 23 Z")]),
            ["TAKE_SHOWER"] = new(32, [
                Fill("F1 M 3 16 L 29 16 L 29 21 A 6 6 0 0 1 23 27 L 9 27 A 6 6 0 0 1 3 21 Z"),
                Stroke("M 2 13 L 30 13 M 8 27 L 8 29 M 24 27 L 24 29 M 22 13 L 22 6 A 4 4 0 0 0 14 6 L 14 7", 2.5)]),
            ["COFFEE_SHOP"] = new(32, [
                Fill("F1 M 3 11 L 23 11 L 23 18 A 9 9 0 0 1 5 18 Z"),
                Stroke("M 23 12 L 25 12 A 5 5 0 0 1 25 22 L 21 22 M 9 29 L 22 29 M 15 11 L 19 3", 2.5)]),
            ["ROCK"] = new(32, [
                Stroke("M 6 18 L 6 9 A 2 2 0 0 1 10 9 L 10 17 A 2 2 0 0 0 14 17 L 14 14 A 2 2 0 0 0 10 14 M 14 17 A 2 2 0 0 0 18 17 L 18 13 A 2 2 0 0 0 14 13 M 18 15 L 18 4 A 2 2 0 0 1 22 4 L 22 16 M 6 18 C 6 25 9 29 15 29 C 22 29 25 24 27 17 C 28 14 24 12 22 15 L 17 20", 2.3)]),
            ["INSPIRATIONAL"] = new(32, [
                Fill("F1 M 18 6 A 3 3 0 1 0 12 6 A 3 3 0 1 0 18 6 Z"),
                Stroke("M 15 12 L 10 20 L 20 16 L 23 21 M 10 20 L 5 28 M 15 12 L 22 7 L 27 3 M 13 12 L 7 15 L 5 20", 3),
                Fill("F1 M 17 29 L 29 29 L 29 18 L 23 18 L 23 23 L 17 23 Z")]),
            ["CHINESE"] = new(32, [
                Stroke("M 7 3 L 25 3 A 3 3 0 0 1 28 6 L 28 26 A 3 3 0 0 1 25 29 L 7 29 A 3 3 0 0 1 4 26 L 4 6 A 3 3 0 0 1 7 3 Z", 2),
                Text("中", 16, 24, 23, FontWeights.Bold, FontStyles.Normal)]),
            ["ENGLISH"] = new(32, [
                Text("En", 16, 26, 25, FontWeights.Normal, FontStyles.Normal)]),
            ["YUEYU"] = new(32, [
                Text("勁", 16, 26, 28, FontWeights.Bold, FontStyles.Normal)]),
            ["MANYAO"] = new(32, [
                Text("DJ", 16, 26, 27, FontWeights.ExtraBold, FontStyles.Normal)]),
            ["JINGDIAN"] = new(32, [
                Fill("F0 M 5 5 L 27 5 A 3 3 0 0 1 30 8 L 30 25 A 3 3 0 0 1 27 28 L 5 28 A 3 3 0 0 1 2 25 L 2 8 A 3 3 0 0 1 5 5 Z M 10 11 A 5 5 0 1 0 10 21 L 22 21 A 5 5 0 1 0 22 11 Z"),
                Fill("F1 M 12.5 16 A 2.5 2.5 0 1 0 7.5 16 A 2.5 2.5 0 1 0 12.5 16 Z"),
                Fill("F1 M 24.5 16 A 2.5 2.5 0 1 0 19.5 16 A 2.5 2.5 0 1 0 24.5 16 Z")]),
            ["LIGHT"] = new(32, [
                Fill("F1 M 28 3 C 9 3 3 11 7 21 L 22 9 L 10 24 C 21 26 27 17 28 3 Z"),
                Stroke("M 5 29 C 8 21 14 15 22 9", 2.2)]),
            ["GUOFENG"] = new(32, [
                Fill("F0 M 2 14 Q 16 -4 30 14 L 16 27 Z M 9 15 L 14 23 L 11 13 Z M 15 11 L 15 23 L 17 23 L 17 11 Z M 23 13 L 18 23 L 25 15 Z")]),
            ["FOLK"] = new(32, [
                Fill("F0 M 13 13 C 9 8 3 12 5 16 C 0 18 1 24 5 27 C 10 31 16 28 17 24 C 22 25 25 19 20 16 L 27 9 L 24 6 Z M 12 16 A 3 3 0 1 0 12 22 A 3 3 0 0 0 12 16 Z"),
                Fill("F1 M 24 6 L 28 2 L 30 4 L 26 8 Z")]),
            ["ACG"] = new(32, [
                Fill("F1 M 2 28 L 12 4 L 22 4 L 27 13 L 21 13 L 18 8 L 15 8 L 8 28 Z"),
                Stroke("M 22 17 L 15 17 L 11 28 L 18 28 M 27 17 L 23 17 L 21 28 L 29 28 L 27 22 L 24 22", 2.4)]),
            ["GUDIAN"] = new(32, [
                Fill("F0 M 5 15 L 27 15 A 3 3 0 0 1 30 18 L 30 23 L 2 23 L 2 18 A 3 3 0 0 1 5 15 Z M 5 18 L 5 20 L 27 20 L 27 18 Z"),
                Stroke("M 4 15 L 23 3 M 20 5 L 26 15 M 7 24 L 7 29 M 24 24 L 24 29", 2.7)]),
            ["JAZZ"] = new(32, [
                Fill("F1 M 24 3 C 19 1 16 4 16 10 L 16 21 A 3 3 0 0 1 10 21 L 10 14 L 2 14 L 5 24 C 7 32 21 32 22 22 L 22 10 C 22 8 24 8 25 9 L 25 12 L 29 12 L 29 8 C 29 6 27 3 24 3 Z")]),
            ["JAPANESE"] = new(32, [
                Fill("F1 M 16 13 C 9 7 13 0 16 5 C 19 0 23 7 16 13 Z M 19 15 C 22 6 30 8 26 12 C 32 13 26 20 19 15 Z M 18 19 C 27 19 27 28 23 25 C 24 31 16 28 18 19 Z M 14 19 C 16 28 8 31 9 25 C 5 28 5 19 14 19 Z M 13 15 C 6 20 0 13 6 12 C 2 8 10 6 13 15 Z"),
                Fill("F1 M 17.5 16 A 1.5 1.5 0 1 0 14.5 16 A 1.5 1.5 0 1 0 17.5 16 Z")]),
            ["GLOBAL"] = new(32, [
                Stroke("M 28 16 A 12 12 0 1 0 4 16 A 12 12 0 1 0 28 16 Z", 2.5),
                Fill("F1 M 9 6 L 15 4 L 16 9 L 11 12 L 12 17 L 6 15 L 5 11 Z M 17 14 L 24 15 L 27 20 L 21 27 L 18 24 L 18 20 L 14 18 Z")]),
            ["FRANCH"] = new(32, [
                Fill("F0 M 15 2 L 17 2 L 18 11 L 21 19 L 30 30 L 23 30 L 19 24 L 13 24 L 9 30 L 2 30 L 11 19 L 14 11 Z M 16 11 L 14 18 L 18 18 Z M 13 20 L 12 22 L 20 22 L 19 20 Z"),
                Stroke("M 10 19 L 22 19 M 13 11 L 19 11", 1.8)]),
            ["BLUE"] = new(32, [
                Fill("F0 M 7 5 L 25 5 L 30 14 L 30 25 L 2 25 L 2 14 Z M 8 8 L 5 14 L 27 14 L 24 8 Z M 5 17 L 5 22 L 8 22 L 8 17 Z M 10 17 L 10 22 L 13 22 L 13 17 Z M 15 17 L 15 22 L 18 22 L 18 17 Z M 20 17 L 20 22 L 23 22 L 23 17 Z M 25 17 L 25 22 L 27 22 L 27 17 Z")]),
            ["DANCE"] = new(32, [
                Fill("F1 M 21 5 A 3 3 0 1 0 15 5 A 3 3 0 1 0 21 5 Z"),
                Stroke("M 17 11 L 14 18 M 17 11 L 10 13 L 6 9 M 17 11 L 24 6 L 26 2 M 15 24 L 13 30 M 18 24 L 22 28", 2.8),
                Fill("F1 M 14 16 L 23 23 C 18 27 12 26 8 22 Z")]),
            ["LATIN"] = new(32, [
                Fill("F1 M 12 3 L 5 22 L 25 22 L 25 19 L 10 19 L 16 3 Z"),
                Text("ATIN", 16, 30, 8, FontWeights.ExtraBold, FontStyles.Italic)]),
            ["PUNK"] = new(32, [
                Fill("F0 M 3 17 L 17 10 L 20 13 L 27 3 L 30 5 L 21 15 L 17 30 L 12 20 Z M 13 15 L 18 20 L 20 15 L 17 12 Z")]),
            ["COUNTRY"] = new(32, [
                Fill("F0 M 2 13 L 13 3 L 24 13 L 21 13 L 21 29 L 5 29 L 5 13 Z M 10 19 L 10 29 L 16 29 L 16 19 Z"),
                Fill("F1 M 27 9 L 23 17 L 25 17 L 22 24 L 26 24 L 26 29 L 28 29 L 28 24 L 31 24 L 28 17 L 30 17 Z"),
                Stroke("M 2 29 L 30 29", 2)]),
            ["MUSICAL"] = new(32, [
                Fill("F1 M 3 6 Q 16 0 29 6 L 29 29 L 25 29 C 24 21 22 18 19 13 C 22 13 24 11 25 9 L 7 9 C 8 11 10 13 13 13 C 10 18 8 21 7 29 L 3 29 Z"),
                Stroke("M 11 29 L 21 29", 2)]),
            ["YINGSHI"] = new(32, [
                Fill("F0 M 3 14 L 29 14 L 29 27 A 2 2 0 0 1 27 29 L 5 29 A 2 2 0 0 1 3 27 Z M 13 17 L 13 25 L 20 21 Z"),
                Fill("F1 M 3 6 L 8 5 L 13 11 L 5 13 Z M 11 4 L 16 3 L 21 9 L 16 10 Z M 19 3 L 27 2 L 29 8 L 24 9 Z")]),
            // HOT_SONG is absent from that reference snapshot: a matching filled flame with an inner cutout.
            ["HOT_SONG"] = new(32, [
                Fill("F0 M 17 2 C 18 7 14 9 15 13 C 17 12 19 9 20 6 C 26 11 29 16 28 21 C 27 27 23 30 16 30 C 9 30 4 26 4 20 C 4 15 7 11 10 8 C 9 13 10 16 12 17 C 12 10 17 8 17 2 Z M 16 18 C 15 22 12 23 12 25 C 12 27 14 28 16 28 C 19 28 21 26 21 24 C 21 22 19 20 18 19 C 18 22 17 23 16 24 C 16 22 16 20 16 18 Z")])
        };
    }
}
