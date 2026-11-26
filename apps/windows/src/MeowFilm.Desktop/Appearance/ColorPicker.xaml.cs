using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace MeowFilm.Desktop;

public sealed class ColorCommittedEventArgs(string color) : EventArgs { public string Color { get; } = color; }

public partial class ColorPicker : UserControl
{
    public static readonly DependencyProperty SelectedColorProperty = DependencyProperty.Register(nameof(SelectedColor), typeof(string), typeof(ColorPicker), new PropertyMetadata(AppearanceColors.DefaultColor, SelectedChanged));
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(ColorPicker), new PropertyMetadata("自定义颜色"));
    public string SelectedColor { get => (string)GetValue(SelectedColorProperty); set => SetValue(SelectedColorProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public event EventHandler<ColorCommittedEventArgs>? ColorCommitted;
    private bool editing;
    private IDisposable? visibility;
    public ColorPicker()
    {
        InitializeComponent(); UpdateSelected();
        Loaded += (_, _) => { visibility?.Dispose(); visibility = DesktopVisual.ObserveVisibility(this, visible => { if (!visible && editing) DesktopModalHost.CloseFor(this); }); };
        Unloaded += (_, _) => { if (IsLoaded) return; visibility?.Dispose(); visibility = null; if (editing) DesktopModalHost.CloseFor(this); };
        IsEnabledChanged += (_, _) => { if (!IsEnabled && editing) DesktopModalHost.CloseFor(this); };
    }
    private static void SelectedChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args) => ((ColorPicker)owner).UpdateSelected();
    private void UpdateSelected()
    {
        if (SavedHex is not null && AppearanceColors.TryParse(SelectedColor, out var color)) SavedHex.Text = AppearanceColors.Hex(color);
    }
    private void SavedHex_LostFocus(object sender, RoutedEventArgs args) => CommitHex();
    private void SavedHex_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape) { UpdateSelected(); args.Handled = true; }
        else if (args.Key == VirtualKey.Enter) { CommitHex(); args.Handled = true; }
    }
    private void CommitHex()
    {
        if (IsEnabled && DesktopVisual.IsVisible(this) && AppearanceColors.TryParse(SavedHex.Text, out var color) && AppearanceColors.Hex(color) != SelectedColor)
            ColorCommitted?.Invoke(this, new(AppearanceColors.Hex(color)));
        UpdateSelected();
    }
    private async void Open_Click(object sender, RoutedEventArgs args)
    {
        if (editing || !AppearanceColors.TryParse(SelectedColor, out var color)) return;
        var picker = new Microsoft.UI.Xaml.Controls.ColorPicker { Color = color, IsAlphaEnabled = false, IsHexInputVisible = true, IsColorSpectrumVisible = true };
        var request = new DesktopDialogRequest
        {
            Title = Label,
            Content = picker,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = DesktopDialogDefaultButton.Close
        };
        editing = true;
        try
        {
            if (await DesktopModalHost.ShowDialogAsync(this, request) == DesktopDialogResult.Primary && IsEnabled && DesktopVisual.IsVisible(this))
                ColorCommitted?.Invoke(this, new(AppearanceColors.Hex(picker.Color)));
        }
        catch (Exception) { NoticeOutlet.Post(this, "暂时无法打开颜色设置", this); }
        finally { editing = false; }
    }
}
