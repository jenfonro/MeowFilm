using System.Windows;
using System.Windows.Controls;

namespace MeowFilm.Desktop;

public enum ManagementStatusTone { Neutral, Success, Failure }

public sealed class ManagementStatus : Button
{
    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(
        nameof(Tone), typeof(ManagementStatusTone), typeof(ManagementStatus), new PropertyMetadata(ManagementStatusTone.Neutral));
    public ManagementStatusTone Tone { get => (ManagementStatusTone)GetValue(ToneProperty); set => SetValue(ToneProperty, value); }

    public ManagementStatus()
    {
        SetResourceReference(StyleProperty, "MeowFilm.ManagementStatus");
        Focusable = false;
    }

    public ManagementStatus(string text, ManagementStatusTone tone = ManagementStatusTone.Neutral, Action? details = null) : this()
    {
        Content = text; Tone = tone;
        if (details is not null) { Focusable = true; Click += (_, _) => details(); }
    }
}
