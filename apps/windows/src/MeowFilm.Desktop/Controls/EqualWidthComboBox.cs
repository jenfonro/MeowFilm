using Microsoft.UI.Xaml.Controls;

namespace MeowFilm.Desktop;

/// <summary>Retains native ComboBox input/automation while constraining the menu to its field via DropdownPlacement.</summary>
public sealed class EqualWidthComboBox : ComboBox
{
    public EqualWidthComboBox()
    {
        DropdownPlacement.SetEnabled(this, true);
    }
}
