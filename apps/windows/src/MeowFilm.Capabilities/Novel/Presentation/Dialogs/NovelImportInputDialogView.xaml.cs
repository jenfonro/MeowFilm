using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace MeowFilm.Capabilities;

public sealed partial class NovelImportInputDialogView : UserControl
{
    public string InputText => InputBox.Text;

    internal NovelImportInputDialogView(
        string label,
        string initialValue = "",
        int rows = 1,
        string? automationId = null,
        string? placeholder = null)
    {
        InitializeComponent();
        LabelText.Text = label;
        InputBox.Text = initialValue;
        InputBox.PlaceholderText = placeholder ?? "";
        InputBox.AcceptsReturn = rows > 1;
        InputBox.Height = rows > 1 ? Math.Max(92, rows * 23 + 16) : 40;
        InputBox.VerticalContentAlignment = rows > 1 ? VerticalAlignment.Top : VerticalAlignment.Center;
        ScrollViewer.SetVerticalScrollBarVisibility(InputBox, rows > 1 ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden);
        AutomationProperties.SetName(InputBox, label);
        if (!string.IsNullOrEmpty(automationId))
            AutomationProperties.SetAutomationId(InputBox, automationId);
    }

    internal void ShowError(string message) => Notices.Show(this, message);
    internal void FocusInput() => InputBox.Focus(FocusState.Programmatic);
}
