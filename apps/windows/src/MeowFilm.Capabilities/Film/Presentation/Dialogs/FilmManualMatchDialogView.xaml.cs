using System.Text.Json;
using Microsoft.UI.Xaml.Controls;

namespace MeowFilm.Capabilities;

public sealed partial class FilmManualMatchDialogView : UserControl
{
    private readonly JsonElement storedValue;
    private readonly string existingId;

    public IReadOnlyList<FilmOrderOption> KindOptions { get; } =
    [
        new("site", "站点"),
        new("share", "分享")
    ];

    public IReadOnlyList<FilmOrderOption> DetailTypeOptions { get; } =
    [
        new("string", "文本编号"),
        new("number", "数字编号")
    ];

    public IReadOnlyList<FilmOrderOption> ProviderOptions { get; } =
        FilmPreferences.Defaults.PanOrder.Select(p => new FilmOrderOption(p, FilmSettingsLogic.FormatPanLabel(p))).ToArray();

    internal FilmManualMatchDialogView(JsonElement? stored, string defaultTargetId)
    {
        storedValue = stored ?? default;
        existingId = FilmJson.Text(storedValue, "id");
        InitializeComponent();

        TargetBox.Text = FilmJson.Text(storedValue, "targetId") is { Length: > 0 } targetId ? targetId : defaultTargetId;
        TitleBox.Text = FilmJson.Text(storedValue, "title");
        NameBox.Text = FilmJson.Text(storedValue, "name");
        KindBox.ItemsSource = KindOptions;
        KindBox.SelectedValue = FilmJson.Text(storedValue, "kind") == "share" ? "share" : "site";
        SourceBox.Text = FilmJson.Text(storedValue, "sourceKey");
        DetailBox.Text = FilmJson.Text(storedValue, "detailId");
        DetailTypeBox.ItemsSource = DetailTypeOptions;
        DetailTypeBox.SelectedValue = FilmJson.Member(storedValue, "detailId").ValueKind == JsonValueKind.Number ? "number" : "string";
        ProviderBox.ItemsSource = ProviderOptions;
        ProviderBox.SelectedValue = FilmJson.Text(storedValue, "provider");
        var share = FilmJson.Member(storedValue, "share");
        UrlBox.Text = FilmJson.Text(share, "url");
        PasswordBox.Text = FilmJson.Text(share, "password");
        SeasonBox.Text = FilmJson.Text(storedValue, "seasonHint");
        EnabledCheck.IsChecked = stored is null || FilmJson.Bool(storedValue, "enabled");
        AutoDisableCheck.IsChecked = FilmJson.Bool(storedValue, "autoDisable");
    }

    internal bool TryBuildPayload(out JsonElement payload, out string? errorMessage) =>
        FilmManualMatchPayload.TryBuild(new(
            existingId,
            TargetBox.Text,
            TitleBox.Text,
            NameBox.Text,
            KindBox.SelectedValue as string ?? "site",
            SourceBox.Text,
            DetailBox.Text,
            Equals(DetailTypeBox.SelectedValue, "number"),
            ProviderBox.SelectedValue as string,
            UrlBox.Text,
            PasswordBox.Text,
            SeasonBox.Text,
            EnabledCheck.IsChecked == true,
            AutoDisableCheck.IsChecked == true),
            storedValue, out payload, out errorMessage);

    internal void ClearSensitiveInputs()
    {
        UrlBox.Text = "";
        PasswordBox.Text = "";
    }
}
