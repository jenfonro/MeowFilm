using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MeowFilm.Desktop;

public enum ManagementStatusTone { Neutral, Success, Failure }

public sealed class ManagementStatus : Button
{
    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(
        nameof(Tone), typeof(ManagementStatusTone), typeof(ManagementStatus), new PropertyMetadata(ManagementStatusTone.Neutral, (owner, _) => ((ManagementStatus)owner).ApplyTone()));
    public ManagementStatusTone Tone { get => (ManagementStatusTone)GetValue(ToneProperty); set => SetValue(ToneProperty, value); }

    public ManagementStatus()
    {
        if (ManagementList.TryFindAppStyle("MeowFilm.ManagementStatus") is { } style) Style = style;
        IsTabStop = false;
        Loaded += (_, _) => ApplyTone();
        ActualThemeChanged += (_, _) => ApplyTone();
    }

    private void ApplyTone()
    {
        string foreground = Tone == ManagementStatusTone.Failure ? "Error" : Tone == ManagementStatusTone.Success ? "AccentText" : "Muted";
        string background = Tone == ManagementStatusTone.Failure ? "ErrorSurface" : Tone == ManagementStatusTone.Success ? "ManagementSuccess" : "ManagementNeutral";
        Foreground = (Microsoft.UI.Xaml.Media.Brush)DesktopVisual.Resource(this, "MeowFilm.Brush." + foreground);
        Background = (Microsoft.UI.Xaml.Media.Brush)DesktopVisual.Resource(this, "MeowFilm.Brush." + background);
    }

    public ManagementStatus(string text, ManagementStatusTone tone = ManagementStatusTone.Neutral, Action? details = null) : this()
    {
        Content = text; Tone = tone;
        if (details is not null) { IsTabStop = true; Click += (_, _) => details(); }
    }
}
