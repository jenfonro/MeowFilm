using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace MeowFilm.Capabilities;

/// <summary>Directional transport feedback; never changes queue or playback state.</summary>
public sealed class SkipButton : Button
{
    public static readonly DependencyProperty NextProperty = DependencyProperty.Register(nameof(Next), typeof(bool), typeof(SkipButton), new PropertyMetadata(true, (owner, _) => ((SkipButton)owner).Refresh()));
    public bool Next { get => (bool)GetValue(NextProperty); set => SetValue(NextProperty, value); }
    private readonly Canvas glyph = new() { Width = 16, Height = 16 };
    private readonly Canvas arrows = new() { Width = 12, Height = 16, Clip = new RectangleGeometry { Rect = new Rect(0, 0, 12, 16) } };
    private readonly Rectangle edge = new() { Width = 3, Height = 16 };
    private readonly Microsoft.UI.Xaml.Shapes.Path first = new(), second = new();
    private readonly TranslateTransform translation = new();
    private readonly ScaleTransform direction = new() { CenterX = 8, CenterY = 8 };
    private readonly ScaleTransform pressed = new() { CenterX = 8, CenterY = 8 };
    private Storyboard? motion;
    public SkipButton()
    {
        Width = Height = 36; Padding = new Thickness(0); Background = MusicVisuals.Brush("#00000000"); BorderThickness = new Thickness(0);
        var moving = new Canvas { RenderTransform = translation };
        first.Data = MusicVisuals.Geometry("M0,0 L12,8 L0,16 Z"); second.Data = MusicVisuals.Geometry("M0,0 L12,8 L0,16 Z");
        Canvas.SetLeft(second, -16); moving.Children.Add(first); moving.Children.Add(second); arrows.Children.Add(moving);
        Canvas.SetLeft(edge, 13); glyph.Children.Add(arrows); glyph.Children.Add(edge);
        glyph.RenderTransform = new TransformGroup { Children = { direction, pressed } }; Content = glyph;
        RegisterPropertyChangedCallback(ForegroundProperty, (_, _) => Refresh());
        RegisterPropertyChangedCallback(IsPressedProperty, (_, _) => pressed.ScaleX = pressed.ScaleY = IsPressed ? .9 : 1);
        RegisterPropertyChangedCallback(IsEnabledProperty, (_, _) => { if (!IsEnabled) Reset(); });
        PointerEntered += (_, _) => SetPointerWithin(true); PointerExited += (_, _) => SetPointerWithin(false); Click += (_, _) => Animate();
        Unloaded += (_, _) => Reset(); Refresh();
    }
    internal void SetPointerWithin(bool within) { if (within) Animate(); else Reset(); }
    internal bool HasActiveAnimation => motion?.GetCurrentState() == ClockState.Active;
    private void Refresh() { direction.ScaleX = Next ? 1 : -1; first.Fill = second.Fill = edge.Fill = Foreground; }
    private void Reset() { motion?.Stop(); motion = null; translation.X = 0; }
    private void Animate()
    {
        Reset(); if (!IsEnabled || !MusicVisuals.CanAnimate(this)) return;
        motion = MusicVisuals.AnimateDouble(translation, "X", 0, 16, TimeSpan.FromMilliseconds(240), Reset);
    }
}
