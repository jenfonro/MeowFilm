using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace MeowFilm.Capabilities;

/// <summary>Native source selection with a separate, nonselectable continuation action inside its popup.</summary>
public sealed class FilmSourcePicker : ComboBox
{
    [ThreadStatic] private static SolidColorBrush? transparentBrush;
    private Popup? popup;
    private FrameworkElement? choices;
    public static readonly DependencyProperty CanSearchMoreProperty = DependencyProperty.Register(
        nameof(CanSearchMore), typeof(bool), typeof(FilmSourcePicker),
        new PropertyMetadata(false, (owner, args) =>
        {
            if (((FilmSourcePicker)owner).SearchMoreSourcesButton is { } button)
                button.IsEnabled = (bool)args.NewValue;
        }));
    public bool CanSearchMore { get => (bool)GetValue(CanSearchMoreProperty); set => SetValue(CanSearchMoreProperty, value); }
    public event RoutedEventHandler? SearchMoreRequested;
    internal Button? SearchMoreSourcesButton { get; private set; }

    public FilmSourcePicker()
    {
        DefaultStyleKey = typeof(ComboBox);
        DropDownOpened += (_, _) =>
        {
            Constrain();
            DispatcherQueue?.TryEnqueue(() => { if (IsDropDownOpen) Constrain(); });
        };
        SizeChanged += (_, _) => Constrain();
    }
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        var next = GetTemplateChild("Popup") as Popup;
        if (ReferenceEquals(next, popup) && SearchMoreSourcesButton is not null) { Constrain(); return; }
        popup = next;
        SearchMoreSourcesButton = null;
        if (popup is null || popup.Child is not FrameworkElement currentChoices) { choices = null; return; }
        choices = currentChoices;
        // Retain the native items presenter, selection states and automation tree.
        // The footer is outside ItemsSource and can never become a source selection.
        popup.Child = null;
        if (choices is Border innerBorder)
        {
            innerBorder.BorderThickness = new Thickness(0);
            innerBorder.Background = transparentBrush ??= new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(choices);
        var more = new Button
        {
            Name = "SearchMoreSourcesButton", Content = "加载更多",
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
            FontWeight = Microsoft.UI.Text.FontWeights.Normal
        };
        AutomationProperties.SetName(more, "继续搜索未完成来源");
        more.IsEnabled = CanSearchMore;
        more.Click += (_, args) => SearchMoreRequested?.Invoke(this, args);
        Grid.SetRow(more, 1); layout.Children.Add(more);
        SearchMoreSourcesButton = more;
        var menu = new Border
        {
            Name = "FilmSourceMenu",
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Child = layout
        };
        popup.Child = menu;
        Constrain();
    }
    private void Constrain()
    {
        if (popup?.Child is not FrameworkElement menu || ActualWidth <= 0) return;
        menu.MinWidth = 0; menu.MaxWidth = double.PositiveInfinity;
        menu.Width = ActualWidth; menu.MinWidth = ActualWidth; menu.MaxWidth = ActualWidth;
        if (choices is not null)
        {
            choices.MinWidth = 0; choices.MaxWidth = double.PositiveInfinity;
            choices.Width = choices.MaxWidth = ActualWidth;
            if (choices.RenderTransform is TranslateTransform transform && transform.Y != 0)
                transform.Y = 0;
        }
    }
}
