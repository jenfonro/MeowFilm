using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace MeowFilm.Desktop;

/// <summary>Retains native ComboBox input/automation while constraining the menu to its field.</summary>
public sealed class EqualWidthComboBox : ComboBox
{
    private Popup? popup;
    public EqualWidthComboBox()
    {
        DropdownPlacement.SetEnabled(this, true);
        DropDownOpened += (_, _) => Constrain();
        SizeChanged += (_, _) => Constrain();
    }
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        popup = GetTemplateChild("Popup") as Popup;
        Constrain();
    }
    private void Constrain()
    {
        if (popup?.Child is FrameworkElement menu && ActualWidth > 0)
        { menu.MinWidth = menu.MaxWidth = menu.Width = ActualWidth; }
    }
}
