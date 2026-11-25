using System.Text.Json;
using MeowFilm.Core.Debug;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace MeowFilm.Desktop;

internal sealed partial class ShellDebugAdapter
{
    private object? BuildActiveDialogStateOnUi()
    {
        if (shell.ActiveModalHost is not { ActiveContent: { } activePanel } host) return null;
        if (activePanel is IDesktopDebugModal modal)
        {
            var info = modal.DescribeDebugDialog();
            var explicitButtons = info.Buttons.Select((b, index) => (object)new
            {
                index,
                label = b.Label,
                automationName = b.AutomationName ?? b.Label,
                enabled = b.Enabled,
                kind = b.Action
            }).ToArray();
            return DescribeDialogSurface(
                id: info.Id,
                kind: info.Kind,
                fallbackTitle: info.FallbackTitle,
                message: info.Message,
                surface: info.Surface,
                explicitButtons: explicitButtons,
                stackDepth: host.StackDepth);
        }
        return DescribeDialogSurface(
            id: "channel-dialog",
            kind: "channel-dialog",
            fallbackTitle: NullIfEmpty(AutomationProperties.GetName(activePanel)) ?? "弹出层",
            message: "",
            surface: activePanel,
            explicitButtons: null,
            stackDepth: host.StackDepth);
    }

    private object HandleDialogAction(JsonElement? parameters)
    {
        var act = (DebugParams.GetString(parameters, "action") ?? "dismiss").Trim().ToLowerInvariant();
        var btnIndex = DebugParams.GetInt(parameters, "index");
        var btnLabel = DebugParams.GetString(parameters, "label")?.Trim();

        if (shell.ActiveModalHost is not { ActiveContent: { } activePanel } host)
            throw DebugApiException.InvalidState("No matching active dialog for dialog.action.");

        if (activePanel is IDesktopDebugModal modal)
        {
            if (act == "button")
            {
                var btns = modal.DescribeDebugDialog().Buttons;
                if (btnIndex is { } idx && idx >= 0 && idx < btns.Length)
                    act = btns[idx].Action;
                else if (!string.IsNullOrWhiteSpace(btnLabel) &&
                    btns.FirstOrDefault(b => string.Equals(b.Label, btnLabel, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(b.AutomationName, btnLabel, StringComparison.OrdinalIgnoreCase)) is { Label: not null } matched)
                    act = matched.Action;
            }
            return modal.HandleDebugAction(act);
        }

        if (act is "cancel" or "dismiss" or "close")
        {
            host.RequestClose();
            return new { dialog = "channel-dialog", action = "dismiss" };
        }

        var buttons = CollectVisibleButtons(activePanel);
        Button? matchedButton = null;
        if (btnIndex is { } bi && bi >= 0 && bi < buttons.Count)
        {
            matchedButton = buttons[bi];
        }
        else if (!string.IsNullOrWhiteSpace(btnLabel))
        {
            matchedButton = buttons.FirstOrDefault(b =>
                string.Equals(DescribeButtonLabel(b), btnLabel, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(AutomationProperties.GetName(b), btnLabel, StringComparison.OrdinalIgnoreCase));
        }
        else if (act is "confirm" or "primary")
        {
            matchedButton = buttons.LastOrDefault(b => b.IsEnabled);
        }
        if (matchedButton is not null)
        {
            if (!matchedButton.IsEnabled)
                throw DebugApiException.InvalidState($"Dialog button '{DescribeButtonLabel(matchedButton)}' is disabled.");
            if (FrameworkElementAutomationPeer.CreatePeerForElement(matchedButton)?.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke)
                invoke.Invoke();
            return new { dialog = "channel-dialog", action = act, label = DescribeButtonLabel(matchedButton) };
        }
        throw DebugApiException.InvalidArgument($"No matching button for action '{act}' on 'channel-dialog'.");
    }

    private object DescribeDialogSurface(
        string id,
        string kind,
        string fallbackTitle,
        string message,
        FrameworkElement surface,
        object[]? explicitButtons,
        int stackDepth)
    {
        var visibleTitles = CollectVisibleTitles(surface, maxCount: 6);
        if (visibleTitles.Count == 0 && !string.IsNullOrWhiteSpace(fallbackTitle))
            visibleTitles.Add(fallbackTitle);

        object? bounds = null;
        object? windowCenterOffset = null;
        if (surface.IsLoaded && surface.ActualWidth > 0 && surface.ActualHeight > 0)
        {
            try
            {
                var rect = surface.TransformToVisual(shell.Root).TransformBounds(new Rect(0, 0, surface.ActualWidth, surface.ActualHeight));
                bounds = new
                {
                    x = Math.Round(rect.X, 1),
                    y = Math.Round(rect.Y, 1),
                    w = Math.Round(rect.Width, 1),
                    h = Math.Round(rect.Height, 1)
                };
                if (shell.Root.ActualWidth > 0 && shell.Root.ActualHeight > 0)
                {
                    double centerX = rect.X + rect.Width / 2.0;
                    double centerY = rect.Y + rect.Height / 2.0;
                    windowCenterOffset = new
                    {
                        dx = Math.Round(centerX - shell.Root.ActualWidth / 2.0, 1),
                        dy = Math.Round(centerY - shell.Root.ActualHeight / 2.0, 1)
                    };
                }
            }
            catch { }
        }

        object[] buttons;
        if (explicitButtons is not null)
        {
            buttons = explicitButtons;
        }
        else
        {
            var foundButtons = CollectVisibleButtons(surface);
            buttons = foundButtons.Count > 0
                ? foundButtons.Select((btn, index) => (object)new
                {
                    index,
                    label = DescribeButtonLabel(btn),
                    automationName = NullIfEmpty(AutomationProperties.GetName(btn)),
                    enabled = btn.IsEnabled,
                    kind = ClassifyDialogButton(btn, index, foundButtons.Count)
                }).ToArray()
                : [new { index = 0, label = "关闭", automationName = "关闭", enabled = true, kind = "dismiss" }];
        }

        return new
        {
            open = true,
            id,
            kind,
            title = visibleTitles.FirstOrDefault() ?? fallbackTitle,
            message,
            visibleTitles = visibleTitles.ToArray(),
            bounds,
            windowCenterOffset,
            stackDepth,
            buttons
        };
    }

    private static List<string> CollectVisibleTitles(DependencyObject root, int maxCount)
    {
        var titles = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Walk(DependencyObject node)
        {
            if (titles.Count >= maxCount) return;
            if (node is UIElement { Visibility: Visibility.Collapsed }) return;
            if (node is Button) return;
            if (node is TextBlock tb && tb.Visibility == Visibility.Visible && tb.Opacity > 0.001)
            {
                var text = tb.Text?.Trim();
                if (!string.IsNullOrWhiteSpace(text) && seen.Add(text))
                    titles.Add(text);
            }
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count && titles.Count < maxCount; i++)
                Walk(VisualTreeHelper.GetChild(node, i));
        }
        Walk(root);
        return titles;
    }

    private static List<Button> CollectVisibleButtons(DependencyObject root)
    {
        var result = new List<Button>();
        void Walk(DependencyObject node)
        {
            if (node is UIElement { Visibility: Visibility.Collapsed }) return;
            if (node is Button button && button.Visibility == Visibility.Visible && button.Opacity > 0.001)
            {
                result.Add(button);
                return;
            }
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
                Walk(VisualTreeHelper.GetChild(node, i));
        }
        Walk(root);
        return result;
    }

    private static string DescribeButtonLabel(Button button)
    {
        if (button.Content is string text && !string.IsNullOrWhiteSpace(text))
            return text.Trim();
        var auto = AutomationProperties.GetName(button);
        if (!string.IsNullOrWhiteSpace(auto))
            return auto.Trim();
        if (button.Content is DependencyObject contentRoot)
        {
            var texts = CollectVisibleTitles(contentRoot, 1);
            if (texts.Count > 0) return texts[0];
        }
        return button.Name ?? "";
    }

    private static string ClassifyDialogButton(Button button, int index, int total)
    {
        string label = DescribeButtonLabel(button);
        if (label is "取消" or "关闭" || label.Contains("取消", StringComparison.Ordinal))
            return "cancel";
        if (label is "确定" or "确认" or "保存" or "提交" || label.Contains("确认", StringComparison.Ordinal))
            return "confirm";
        return index == total - 1 ? "primary" : "button";
    }
}
