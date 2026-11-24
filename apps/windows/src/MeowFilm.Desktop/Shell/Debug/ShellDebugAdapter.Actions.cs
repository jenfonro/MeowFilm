using System.Diagnostics;
using System.Text.Json;
using MeowFilm.Core.Debug;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MeowFilm.Desktop;

internal sealed partial class ShellDebugAdapter
{
    public Task<object> PerformActionAsync(string action, JsonElement? parameters, bool waitIdle, int timeoutMs, CancellationToken cancellationToken) =>
        OnUiAsync<object>(async () =>
        {
            var started = Stopwatch.GetTimestamp();
            var normalized = action.Trim().ToLowerInvariant();
            DebugLog.Info(DebugCategories.UiAction, normalized, "action.start", new { action = normalized, @params = parameters });

            object? outcome;
            if (normalized == "search.submit")
            {
                var query = DebugParams.GetString(parameters, "query");
                if (string.IsNullOrWhiteSpace(query))
                    throw DebugApiException.InvalidArgument("search.submit requires non-empty 'query'.");
                await OpenSearchTargetAsync(parameters, cancellationToken);
                outcome = new { submitted = true, query };
            }
            else if (normalized == "dialog.action")
            {
                outcome = HandleDialogAction(parameters);
            }
            else if (normalized == "window.close_request")
            {
                outcome = await shell.CloseCoordinator.HandleDebugCloseRequestAsync();
            }
            else if (normalized == "search.scope.open")
            {
                outcome = HandleSearchScopeOpen();
            }
            else if (normalized == "search.scope.select")
            {
                outcome = HandleSearchScopeSelect(parameters, includeKind: false);
            }
            else if (normalized == "dropdown.open")
            {
                outcome = HandleDropdownOpen(parameters);
            }
            else if (normalized == "dropdown.select")
            {
                outcome = HandleDropdownSelect(parameters);
            }
            else if (normalized == "dropdown.close")
            {
                outcome = HandleDropdownClose();
            }
            else if (normalized == "nonclient.refresh")
            {
                shell.NativeCaption?.RefreshImmediate();
                outcome = shell.NativeCaption?.InspectDebugState() ?? new { refreshed = false };
            }
            else if (normalized == "tab.select" &&
                     !shell.IsQueueOpen && !shell.IsPlayerDetailOpen &&
                     string.Equals(DebugParams.GetString(parameters, "group"), "settings-section", StringComparison.OrdinalIgnoreCase) &&
                     shell.CurrentSession is { IsSettingsOpen: true } && shell.IsGeneralSettingsOpen &&
                     shell.GeneralSettingsHost.Content is GeneralSettingsView generalView)
            {
                var id = DebugParams.GetString(parameters, "id");
                if (string.IsNullOrWhiteSpace(id) && DebugParams.GetInt(parameters, "index") is { } idx)
                {
                    id = generalView.SelectAreaByIndex(idx)
                        ?? throw DebugApiException.InvalidArgument($"General settings tab index {idx} out of range.");
                    outcome = new { selected = id };
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(id) || !generalView.SelectArea(id))
                        throw DebugApiException.NotFound($"Unable to select general settings tab '{id}'.");
                    outcome = new { selected = id };
                }
            }
            else if (normalized == "setting.set" &&
                     string.Equals(DebugParams.GetString(parameters, "domain"), "general", StringComparison.OrdinalIgnoreCase))
            {
                var key = DebugParams.GetString(parameters, "key") ?? throw DebugApiException.InvalidArgument("Missing 'key'.");
                if (parameters is not { ValueKind: JsonValueKind.Object } obj || !obj.TryGetProperty("value", out var val))
                    throw DebugApiException.InvalidArgument("Missing 'value'.");
                if (shell.GeneralSettingsHost.Content is not GeneralSettingsView gv || !await gv.TryDebugSetAsync(key, val))
                    throw DebugApiException.InvalidArgument($"Unsupported general setting '{key}'.");
                outcome = new { saved = true, domain = "general", key };
            }
            else if (normalized == "setting.action" &&
                     string.Equals(DebugParams.GetString(parameters, "domain"), "general", StringComparison.OrdinalIgnoreCase))
            {
                var cmd = DebugParams.GetString(parameters, "command") ?? throw DebugApiException.InvalidArgument("Missing 'command'.");
                if (string.Equals(cmd, "reset_user_data", StringComparison.OrdinalIgnoreCase))
                {
                    shell.ConfirmUserDataReset();
                    outcome = new { dialogOpened = true };
                }
                else if (string.Equals(cmd, "clear_artwork_cache", StringComparison.OrdinalIgnoreCase))
                {
                    var loader = Artwork.ArtworkScope.GetLoader(shell.Root)
                        ?? throw DebugApiException.InvalidState("Global artwork loader is not attached.");
                    await loader.ClearDiskAsync(cancellationToken);
                    outcome = new { executed = true, command = cmd, stats = loader.GetStats() };
                }
                else throw DebugApiException.InvalidArgument($"Unsupported general settings command '{cmd}'.");
            }
            else if (normalized is not ("setting.set" or "setting.action") &&
                     ResolveForegroundOverlayAdapter(out var overlayTarget) is { } overlayAdapter)
            {
                outcome = await overlayAdapter.TryHandleDebugOverlayActionAsync(overlayTarget, normalized, parameters, cancellationToken)
                    ?? await overlayAdapter.TryHandleDebugActionAsync(normalized, parameters, settingsOpen: false, cancellationToken)
                    ?? throw DebugApiException.InvalidArgument($"Action '{normalized}' is not supported on foreground overlay '{overlayTarget}'.");
            }
            else
            {
                IDebugModuleAdapter? targetAdapter = null;
                bool settingsOpen = false;
                if (normalized is "setting.set" or "setting.action" && DebugParams.GetString(parameters, "domain") is { Length: > 0 } dom)
                {
                    targetAdapter = FindAdapter(dom);
                    settingsOpen = true;
                }
                else if (shell.CurrentPage is { } page && AsAdapter(page.Page) is { } pageAdapter)
                {
                    targetAdapter = pageAdapter;
                }
                else if (shell.CurrentSession is { } current && !(current.IsSettingsOpen && shell.IsGeneralSettingsOpen) &&
                         AsAdapter(current.Module) is { } modAdapter)
                {
                    targetAdapter = modAdapter;
                    settingsOpen = current.IsSettingsOpen;
                }
                if (targetAdapter is null)
                    throw DebugApiException.InvalidState($"No active module adapter can handle action '{normalized}'.");
                outcome = await targetAdapter.TryHandleDebugActionAsync(normalized, parameters, settingsOpen, cancellationToken)
                    ?? throw DebugApiException.InvalidArgument($"Action '{normalized}' is not supported on current surface '{targetAdapter.DebugModuleId}'.");
            }

            if (waitIdle)
                await WaitConditionOnUiAsync("idle", null, timeoutMs, cancellationToken);

            var state = BuildUiStateOnUi();
            DebugLog.Info(DebugCategories.UiAction, normalized, "action.end",
                new { action = normalized, outcome },
                durationMs: Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return new { action = normalized, result = outcome, state };
        }, cancellationToken);

    private object HandleSearchScopeOpen()
    {
        if (!DesktopVisual.IsVisible(shell.SearchTargetPickerElement) || !shell.SearchTargetPickerElement.IsEnabled)
            throw DebugApiException.InvalidState("Search scope picker is not visible or enabled.");
        shell.SearchTargetPickerElement.Open();
        return DropdownPlacement.DescribeSearchScope(shell.SearchTargetPickerElement, shell.Root);
    }

    private object HandleSearchScopeSelect(JsonElement? parameters, bool includeKind)
    {
        if (!DesktopVisual.IsVisible(shell.SearchTargetPickerElement) || !shell.SearchTargetPickerElement.IsEnabled)
            throw DebugApiException.InvalidState("Search scope picker is not visible or enabled.");
        var id = DebugParams.GetString(parameters, "id") ?? DebugParams.GetString(parameters, "value") ?? DebugParams.GetString(parameters, "target");
        var label = DebugParams.GetString(parameters, "label");
        var index = DebugParams.GetInt(parameters, "optionIndex") ?? DebugParams.GetInt(parameters, "itemIndex") ?? DebugParams.GetInt(parameters, "index");
        if (!shell.SearchTargetPickerElement.TrySelect(id, label, index))
            throw DebugApiException.NotFound("No matching search scope option found.");
        return includeKind
            ? new
            {
                kind = "search-scope",
                selectedValue = shell.SearchTargetPickerElement.SelectedValue,
                selectedLabel = shell.SearchTargetPickerElement.SelectedItem?.Label
            }
            : new
            {
                selectedValue = shell.SearchTargetPickerElement.SelectedValue,
                selectedLabel = shell.SearchTargetPickerElement.SelectedItem?.Label
            };
    }

    private object HandleDropdownOpen(JsonElement? parameters)
    {
        var kind = DebugParams.GetString(parameters, "kind")?.Trim().ToLowerInvariant();
        var name = DebugParams.GetString(parameters, "name");
        var automationName = DebugParams.GetString(parameters, "automationName");
        var index = DebugParams.GetInt(parameters, "index");

        if (kind == "search-scope" || string.Equals(name, "SearchTargetPicker", StringComparison.OrdinalIgnoreCase))
            return HandleSearchScopeOpen();

        var combo = FindVisibleComboBox(name, automationName, index)
            ?? throw DebugApiException.NotFound("No matching visible ComboBox found.");
        if (!combo.IsEnabled)
            throw DebugApiException.InvalidState("Target ComboBox is disabled.");
        combo.IsDropDownOpen = true;
        combo.UpdateLayout();
        return DropdownPlacement.DescribeComboBox(combo, shell.Root);
    }

    private object HandleDropdownSelect(JsonElement? parameters)
    {
        var kind = DebugParams.GetString(parameters, "kind")?.Trim().ToLowerInvariant();
        if (kind == "search-scope" || shell.SearchTargetPickerElement.IsDropDownOpen)
            return HandleSearchScopeSelect(parameters, includeKind: true);

        var name = DebugParams.GetString(parameters, "name");
        var automationName = DebugParams.GetString(parameters, "automationName");
        var comboIndex = DebugParams.GetInt(parameters, "comboIndex");
        var combo = DropdownPlacement.ActiveComboBox
            ?? FindVisibleComboBox(name, automationName, comboIndex)
            ?? throw DebugApiException.InvalidState("No active or matching ComboBox found.");
        if (!combo.IsEnabled)
            throw DebugApiException.InvalidState("Target ComboBox is disabled.");

        int? targetIndex = DebugParams.GetInt(parameters, "optionIndex") ?? DebugParams.GetInt(parameters, "itemIndex") ?? DebugParams.GetInt(parameters, "index");
        string? wantedValue = DebugParams.GetString(parameters, "value") ?? DebugParams.GetString(parameters, "id");
        string? wantedLabel = DebugParams.GetString(parameters, "label");

        int matchedIndex = -1;
        if (targetIndex is { } idx && idx >= 0 && idx < combo.Items.Count)
        {
            matchedIndex = idx;
        }
        else
        {
            for (int i = 0; i < combo.Items.Count; i++)
            {
                var item = combo.Items[i];
                var itemValue = DropdownPlacement.DescribeItemValue(combo, item);
                var itemLabel = DropdownPlacement.DescribeItemLabel(combo, item);
                if ((!string.IsNullOrWhiteSpace(wantedValue) && string.Equals(itemValue, wantedValue, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(wantedLabel) && string.Equals(itemLabel, wantedLabel, StringComparison.OrdinalIgnoreCase)))
                {
                    matchedIndex = i;
                    break;
                }
            }
        }
        if (matchedIndex < 0)
            throw DebugApiException.NotFound("No matching dropdown option found.");

        combo.SelectedIndex = matchedIndex;
        combo.IsDropDownOpen = false;
        return new
        {
            kind = combo is EqualWidthComboBox ? "equal-width-combobox" : "combobox",
            selectedIndex = combo.SelectedIndex,
            selectedValue = DropdownPlacement.DescribeItemValue(combo, combo.SelectedItem),
            selectedLabel = DropdownPlacement.DescribeItemLabel(combo, combo.SelectedItem)
        };
    }

    private object HandleDropdownClose()
    {
        bool closedAny = false;
        if (shell.SearchTargetPickerElement.IsDropDownOpen)
        {
            shell.SearchTargetPickerElement.Close();
            closedAny = true;
        }
        if (DropdownPlacement.ActiveComboBox is { IsDropDownOpen: true } active)
        {
            active.IsDropDownOpen = false;
            closedAny = true;
        }
        foreach (var combo in CollectVisibleComboBoxes())
        {
            if (combo.IsDropDownOpen)
            {
                combo.IsDropDownOpen = false;
                closedAny = true;
            }
        }
        return new { closed = closedAny };
    }

    private ComboBox? FindVisibleComboBox(string? name, string? automationName, int? index)
    {
        var combos = CollectVisibleComboBoxes();
        if (!string.IsNullOrWhiteSpace(name))
            return combos.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(automationName))
            return combos.FirstOrDefault(c => string.Equals(AutomationProperties.GetName(c), automationName, StringComparison.OrdinalIgnoreCase));
        int targetIndex = index ?? 0;
        return targetIndex >= 0 && targetIndex < combos.Count ? combos[targetIndex] : null;
    }

    private List<ComboBox> CollectVisibleComboBoxes()
    {
        var list = new List<ComboBox>();
        void Walk(DependencyObject node)
        {
            if (node is UIElement { Visibility: Visibility.Collapsed }) return;
            if (node is ComboBox combo && combo.IsLoaded && DesktopVisual.IsVisible(combo))
            {
                list.Add(combo);
                return;
            }
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
                Walk(VisualTreeHelper.GetChild(node, i));
        }
        Walk(shell.Root);
        return list;
    }
}
