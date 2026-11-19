using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;

namespace MeowFilm.Capabilities;

/// <summary>Native source selection with a separate, nonselectable continuation action inside its popup.</summary>
public sealed class FilmSourcePicker : ComboBox
{
    private Popup? popup;
    private FrameworkElement? choices;
    public static readonly DependencyProperty CanSearchMoreProperty = DependencyProperty.Register(
        nameof(CanSearchMore), typeof(bool), typeof(FilmSourcePicker), new PropertyMetadata(false));
    public bool CanSearchMore { get => (bool)GetValue(CanSearchMoreProperty); set => SetValue(CanSearchMoreProperty, value); }
    public event RoutedEventHandler? SearchMoreRequested;
    internal Button? SearchMoreSourcesButton { get; private set; }
    internal Popup? SourcePopup => popup;

    public FilmSourcePicker()
    {
        DefaultStyleKey = typeof(ComboBox);
        DropDownOpened += (_, _) => Constrain();
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
        var menu = new Grid { Name = "FilmSourceMenu", Background = Background };
        menu.RowDefinitions.Add(new RowDefinition());
        menu.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        menu.Children.Add(choices);
        var more = new Button { Name = "SearchMoreSourcesButton", Content = "加载更多",
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
            FontWeight = Microsoft.UI.Text.FontWeights.Normal };
        AutomationProperties.SetName(more, "继续搜索未完成来源");
        more.SetBinding(IsEnabledProperty, new Binding { Source = this, Path = new PropertyPath(nameof(CanSearchMore)) });
        more.Click += (_, args) => SearchMoreRequested?.Invoke(this, args);
        Grid.SetRow(more, 1); menu.Children.Add(more);
        SearchMoreSourcesButton = more;
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
        }
    }
}
