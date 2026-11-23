using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace MeowFilm.Capabilities;

/// <summary>Native source selection with a separate, nonselectable continuation action inside its popup template.</summary>
public sealed class FilmSourcePicker : ComboBox
{
    private Popup? popup;
    private FrameworkElement? popupBorder;

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
        DropDownOpened += (_, _) =>
        {
            Constrain();
            DispatcherQueue?.TryEnqueue(() => { if (IsDropDownOpen) Constrain(); });
        };
        SizeChanged += (_, _) => Constrain();
    }

    protected override void OnApplyTemplate()
    {
        if (SearchMoreSourcesButton is not null) SearchMoreSourcesButton.Click -= OnSearchMoreClick;
        base.OnApplyTemplate();
        popup = GetTemplateChild("Popup") as Popup;
        popupBorder = GetTemplateChild("PopupBorder") as FrameworkElement;
        SearchMoreSourcesButton = GetTemplateChild("SearchMoreSourcesButton") as Button;
        if (SearchMoreSourcesButton is not null)
        {
            SearchMoreSourcesButton.IsEnabled = CanSearchMore;
            SearchMoreSourcesButton.Click += OnSearchMoreClick;
        }
        Constrain();
    }

    private void OnSearchMoreClick(object sender, RoutedEventArgs args) => SearchMoreRequested?.Invoke(this, args);

    private void Constrain()
    {
        var target = popupBorder ?? popup?.Child as FrameworkElement;
        if (target is null || ActualWidth <= 0) return;
        target.Width = target.MinWidth = target.MaxWidth = ActualWidth;
    }
}
