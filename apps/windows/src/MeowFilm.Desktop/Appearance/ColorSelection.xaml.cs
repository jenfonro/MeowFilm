using System.Windows;
using System.Windows.Controls;

namespace MeowFilm.Desktop;

public sealed record ColorPreset(string Key, string Name, string Color);
public sealed class ColorSelectionEventArgs(string value) : EventArgs
{
    public string Value { get; } = value;
}

/// <summary>A preset selector with an uncommitted custom choice; persistence remains with its owner.</summary>
public partial class ColorSelection : UserControl
{
    public static readonly DependencyProperty PresetsProperty = DependencyProperty.Register(
        nameof(Presets), typeof(IReadOnlyList<ColorPreset>), typeof(ColorSelection), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(string), typeof(ColorSelection), new PropertyMetadata(AppearanceColors.DefaultColor, Changed));
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(ColorSelection), new PropertyMetadata("颜色"));
    public IReadOnlyList<ColorPreset> Presets
    { get => (IReadOnlyList<ColorPreset>?)GetValue(PresetsProperty) ?? []; set => SetValue(PresetsProperty, value); }
    public string Value { get => (string)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public event EventHandler<ColorSelectionEventArgs>? ValueCommitted;
    private bool updating;

    public ColorSelection()
    {
        InitializeComponent();
        RestoreSelection();
        Loaded += (_, _) => RestoreSelection();
        IsVisibleChanged += (_, _) => { if (IsVisible) RestoreSelection(); };
    }
    private static void Changed(DependencyObject owner, DependencyPropertyChangedEventArgs e) =>
        ((ColorSelection)owner).RestoreSelection();
    public void RestoreSelection()
    {
        if (Choices is null) return;
        updating = true;
        Choices.ItemsSource = Presets.Append(new ColorPreset("custom", "自定义", "")).ToArray();
        var preset = Presets.FirstOrDefault(option => option.Key == Value);
        Choices.SelectedValue = preset?.Key ?? "custom";
        Custom.SelectedColor = preset?.Color ?? Value;
        Custom.Visibility = preset is null ? Visibility.Visible : Visibility.Collapsed;
        updating = false;
    }
    private void Choice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (updating || Choices.SelectedItem is not ColorPreset preset) return;
        Custom.Visibility = preset.Key == "custom" ? Visibility.Visible : Visibility.Collapsed;
        if (preset.Key != "custom" && preset.Key != Value)
            ValueCommitted?.Invoke(this, new(preset.Key));
    }
    private void Custom_Committed(object? sender, ColorCommittedEventArgs e) =>
        ValueCommitted?.Invoke(this, new(e.Color));
}
