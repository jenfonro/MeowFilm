using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

sealed partial class MusicDesktopLyricColorWindow : ContentDialog
{
    internal static readonly IReadOnlyList<ColorPreset> Presets =
    [new("#FFB5C5", "浅粉", "#FFB5C5"), new("#FFF2A8", "浅金", "#FFF2A8"), new("#FFFFFF", "纯白", "#FFFFFF"), new("#B9E7FF", "晴蓝", "#B9E7FF"), new("#C5F3CD", "浅绿", "#C5F3CD")];
    private readonly MusicCapability module;
    private readonly string role;
    internal MusicDesktopLyricColorWindow(MusicCapability module, string role)
    {
        this.module = module; this.role = role; InitializeComponent();
        Title = ColorLabel.Text = role == "normal" ? "普通颜色" : "逐字高亮颜色";
        Selection.Color = MusicVisuals.Brush(role == "normal" ? module.DesktopLyricNormalColor : module.DesktopLyricHighlightColor).Color;
    }
    private async void Save_Click(ContentDialog sender, ContentDialogButtonClickEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            if (!module.CanEditDesktopLyricSettings) { e.Cancel = true; return; }
            var color = Selection.Color; string value = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            await module.SetDesktopLyricColorAsync(role, value);
            e.Cancel = (role == "normal" ? module.DesktopLyricNormalColor : module.DesktopLyricHighlightColor) != value;
            if (e.Cancel) await module.Notify("颜色未保存，请重试。");
        }
        finally { deferral.Complete(); }
    }
}
