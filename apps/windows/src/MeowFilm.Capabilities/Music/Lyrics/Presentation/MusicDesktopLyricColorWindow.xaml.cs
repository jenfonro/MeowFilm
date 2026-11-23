using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

sealed partial class MusicDesktopLyricColorWindow : Window
{
    internal static readonly IReadOnlyList<ColorPreset> Presets =
    [new("#FFB5C5", "浅粉", "#FFB5C5"), new("#FFF2A8", "浅金", "#FFF2A8"), new("#FFFFFF", "纯白", "#FFFFFF"), new("#B9E7FF", "晴蓝", "#B9E7FF"), new("#C5F3CD", "浅绿", "#C5F3CD")];
    private readonly MusicSettingsController settings;
    private readonly string role;
    private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool saving;

    internal MusicDesktopLyricColorWindow(MusicSettingsController settings, string role)
    {
        this.settings = settings; this.role = role; InitializeComponent();
        Title = ColorLabel.Text = role == "normal" ? "普通颜色" : "逐字高亮颜色";
        Selection.Color = DesktopVisual.Brush(role == "normal" ? settings.DesktopLyricNormalColor : settings.DesktopLyricHighlightColor).Color;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }
        AppWindow.IsShownInSwitchers = false;
        AppWindow.Resize(new(380, 540));
        Closed += (_, _) => closed.TrySetResult();
    }

    internal void ApplyHighContrast(bool enabled) => DesktopTheme.SetIsHighContrast(RootSurface, enabled);

    internal Task ShowModalAsync()
    {
        Activate();
        return closed.Task;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (saving) return;
        saving = true;
        SaveButton.IsEnabled = false;
        try
        {
            if (!settings.CanEditDesktopLyricSettings) return;
            var color = Selection.Color; string value = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            await settings.SetDesktopLyricColorAsync(role, value);
            if ((role == "normal" ? settings.DesktopLyricNormalColor : settings.DesktopLyricHighlightColor) == value)
                Close();
            else
                await settings.Notify("颜色未保存，请重试。");
        }
        finally
        {
            saving = false;
            SaveButton.IsEnabled = true;
        }
    }
}
