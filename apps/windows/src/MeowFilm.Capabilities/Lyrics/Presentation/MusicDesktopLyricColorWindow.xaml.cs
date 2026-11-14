using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities;

internal partial class MusicDesktopLyricColorWindow : DesktopWindow
{
    internal static readonly IReadOnlyList<ColorPreset> Presets =
    [
        new("#FFB5C5", "浅粉", "#FFB5C5"),
        new("#FFF2A8", "浅金", "#FFF2A8"),
        new("#FFFFFF", "纯白", "#FFFFFF"),
        new("#B9E7FF", "晴蓝", "#B9E7FF"),
        new("#C5F3CD", "浅绿", "#C5F3CD")
    ];
    private readonly MusicCapability module;
    private readonly string role;
    private string draft;
    private bool closed;

    internal MusicDesktopLyricColorWindow(MusicCapability module, string role)
    {
        this.module = module; this.role = role;
        draft = role == "normal" ? module.DesktopLyricNormalColor : module.DesktopLyricHighlightColor;
        InitializeComponent();
        DataContext = module;
        Title = ColorLabel.Text = Selection.Label = role == "normal" ? "普通颜色" : "逐字高亮颜色";
        Selection.Presets = Presets;
        Selection.Value = draft;
    }

    private void Color_Committed(object? sender, ColorSelectionEventArgs e)
    {
        draft = e.Value;
        Selection.Value = draft;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!module.CanEditDesktopLyricSettings) return;
        string selected = draft;
        await module.SetDesktopLyricColorAsync(role, selected);
        if (closed) return;
        if ((role == "normal" ? module.DesktopLyricNormalColor : module.DesktopLyricHighlightColor) == selected) Close();
        else MessageBox.Show(this, "颜色未保存，请重试。", "meowfilm", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel) return;
        closed = true;
        // Disabling the shared picker retires its popup before this HWND disappears.
        Selection.IsEnabled = false;
        if (Mouse.Captured is DependencyObject target && (IsAncestorOf(target) || Window.GetWindow(target) == this))
            Mouse.Capture(null);
        DataContext = null;
    }
}
