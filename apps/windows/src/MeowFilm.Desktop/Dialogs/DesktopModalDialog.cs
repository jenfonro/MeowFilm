using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace MeowFilm.Desktop;

public enum DesktopDialogDefaultButton { None, Primary, Secondary, Close }

public sealed class DesktopDialogRequest
{
    private bool isPrimaryEnabled = true;
    public string Title { get; init; } = "";
    public string? Message { get; init; }
    public UIElement? Content { get; init; }
    public string? PrimaryButtonText { get; init; }
    public string? SecondaryButtonText { get; init; }
    public string? CloseButtonText { get; init; } = "取消";
    public DesktopDialogDefaultButton DefaultButton { get; init; } = DesktopDialogDefaultButton.Close;
    public double MinWidth { get; init; } = 320;
    public double MaxWidth { get; init; } = 460;
    public bool DismissOnOutside { get; init; } = true;
    public Func<Task<bool>>? OnPrimaryAsync { get; init; }
    public Action? OnOpened { get; init; }
    public Func<bool>? CanRestoreFocus { get; init; }

    public bool IsPrimaryEnabled
    {
        get => isPrimaryEnabled;
        set
        {
            if (isPrimaryEnabled == value) return;
            isPrimaryEnabled = value;
            PrimaryEnabledChanged?.Invoke(value);
        }
    }

    internal event Action<bool>? PrimaryEnabledChanged;
}

internal static class DesktopModalDialog
{
    internal sealed class BuiltDialog(Border surface, Func<DesktopDialogResult> result, Action focusInitial)
    {
        internal Border Surface { get; } = surface;
        internal DesktopDialogResult Result => result();
        internal void FocusInitial() => focusInitial();
    }

    internal static BuiltDialog Build(DesktopDialogRequest request, Action closeHost)
    {
        var outcome = DesktopDialogResult.None;
        bool busy = false;

        var root = new Grid { RowSpacing = 14 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        if (!string.IsNullOrWhiteSpace(request.Title))
        {
            var heading = new TextBlock
            {
                Text = request.Title,
                FontSize = 18,
                TextWrapping = TextWrapping.Wrap,
                FontWeight = Microsoft.UI.Text.FontWeights.Normal
            };
            heading.UseResource(FrameworkElement.StyleProperty, "MeowFilm.SectionHeading");
            heading.Margin = new Thickness(0);
            root.Children.Add(heading);
        }

        UIElement? body = request.Content;
        if (body is null && !string.IsNullOrWhiteSpace(request.Message))
        {
            body = new TextBlock
            {
                Text = request.Message,
                TextWrapping = TextWrapping.Wrap
            };
        }
        if (body is not null)
        {
            var scroller = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = body
            };
            scroller.UseResource(FrameworkElement.StyleProperty, "MeowFilm.SettingsScrollViewer");
            Grid.SetRow(scroller, 1);
            root.Children.Add(scroller);
        }

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        Grid.SetRow(actions, 2);

        Button? closeButton = null;
        Button? secondaryButton = null;
        Button? primaryButton = null;

        if (!string.IsNullOrWhiteSpace(request.CloseButtonText))
        {
            closeButton = new Button { Content = request.CloseButtonText, MinWidth = 72 };
            closeButton.UseResource(FrameworkElement.StyleProperty, "MeowFilm.GhostButton");
            closeButton.Click += (_, _) =>
            {
                if (busy) return;
                outcome = DesktopDialogResult.None;
                closeHost();
            };
            actions.Children.Add(closeButton);
        }

        if (!string.IsNullOrWhiteSpace(request.SecondaryButtonText))
        {
            secondaryButton = new Button { Content = request.SecondaryButtonText, MinWidth = 72 };
            secondaryButton.UseResource(FrameworkElement.StyleProperty, "MeowFilm.Button");
            secondaryButton.Click += (_, _) =>
            {
                if (busy) return;
                outcome = DesktopDialogResult.Secondary;
                closeHost();
            };
            actions.Children.Add(secondaryButton);
        }

        async Task InvokePrimaryAsync()
        {
            if (busy || !request.IsPrimaryEnabled) return;
            if (request.OnPrimaryAsync is not null)
            {
                busy = true;
                if (primaryButton is not null) primaryButton.IsEnabled = false;
                if (secondaryButton is not null) secondaryButton.IsEnabled = false;
                if (closeButton is not null) closeButton.IsEnabled = false;
                try
                {
                    if (!await request.OnPrimaryAsync()) return;
                }
                finally
                {
                    busy = false;
                    if (primaryButton is not null) primaryButton.IsEnabled = request.IsPrimaryEnabled;
                    if (secondaryButton is not null) secondaryButton.IsEnabled = true;
                    if (closeButton is not null) closeButton.IsEnabled = true;
                }
            }
            outcome = DesktopDialogResult.Primary;
            closeHost();
        }

        if (!string.IsNullOrWhiteSpace(request.PrimaryButtonText))
        {
            primaryButton = new Button
            {
                Content = request.PrimaryButtonText,
                MinWidth = 72,
                IsEnabled = request.IsPrimaryEnabled
            };
            primaryButton.UseResource(FrameworkElement.StyleProperty, "MeowFilm.PrimaryButton");
            primaryButton.Click += async (_, _) => await InvokePrimaryAsync();
            request.PrimaryEnabledChanged += enabled =>
            {
                if (!busy) primaryButton.IsEnabled = enabled;
            };
            actions.Children.Add(primaryButton);
        }

        root.Children.Add(actions);

        var surface = new Border
        {
            MinWidth = request.MinWidth,
            MaxWidth = request.MaxWidth,
            Padding = new Thickness(20),
            CornerRadius = new CornerRadius(12),
            TabFocusNavigation = KeyboardNavigationMode.Cycle,
            Child = root
        };
        surface.UseResource(FrameworkElement.StyleProperty, "MeowFilm.FloatingSurface");
        if (!string.IsNullOrWhiteSpace(request.Title))
            AutomationProperties.SetName(surface, request.Title);

        surface.KeyDown += async (_, args) =>
        {
            if (args.Key != VirtualKey.Enter || busy) return;
            if (args.OriginalSource is TextBox { AcceptsReturn: true }) return;
            if (args.OriginalSource is Button) return;
            switch (request.DefaultButton)
            {
                case DesktopDialogDefaultButton.Primary when primaryButton is { IsEnabled: true }:
                    args.Handled = true;
                    await InvokePrimaryAsync();
                    break;
                case DesktopDialogDefaultButton.Secondary when secondaryButton is { IsEnabled: true }:
                    args.Handled = true;
                    outcome = DesktopDialogResult.Secondary;
                    closeHost();
                    break;
                case DesktopDialogDefaultButton.Close when closeButton is { IsEnabled: true }:
                    args.Handled = true;
                    outcome = DesktopDialogResult.None;
                    closeHost();
                    break;
            }
        };

        void FocusInitial()
        {
            var target = request.DefaultButton switch
            {
                DesktopDialogDefaultButton.Primary => primaryButton,
                DesktopDialogDefaultButton.Secondary => secondaryButton,
                DesktopDialogDefaultButton.Close => closeButton,
                _ => null
            } ?? primaryButton ?? closeButton ?? secondaryButton;
            target?.Focus(FocusState.Programmatic);
        }

        return new BuiltDialog(surface, () => outcome, FocusInitial);
    }
}
