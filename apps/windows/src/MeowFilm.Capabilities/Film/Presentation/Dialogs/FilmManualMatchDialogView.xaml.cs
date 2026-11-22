using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        FilmPreferences.Defaults.PanOrder.Select(p => new FilmOrderOption(p, FilmPlaybackSettingsView.FormatPanLabel(p))).ToArray();

    internal FilmManualMatchDialogView(JsonElement? stored, string defaultTargetId)
    {
        storedValue = stored ?? default;
        existingId = FilmJson.Text(storedValue, "id");
        InitializeComponent();

        TargetBox.Text = FilmJson.Text(storedValue, "targetId") is { Length: > 0 } targetId ? targetId : defaultTargetId;
        TitleBox.Text = FilmJson.Text(storedValue, "title");
        NameBox.Text = FilmJson.Text(storedValue, "name");
        KindBox.SelectedValue = FilmJson.Text(storedValue, "kind") == "share" ? "share" : "site";
        SourceBox.Text = FilmJson.Text(storedValue, "sourceKey");
        DetailBox.Text = FilmJson.Text(storedValue, "detailId");
        DetailTypeBox.SelectedValue = FilmJson.Member(storedValue, "detailId").ValueKind == JsonValueKind.Number ? "number" : "string";
        ProviderBox.SelectedValue = FilmJson.Text(storedValue, "provider");
        var share = FilmJson.Member(storedValue, "share");
        UrlBox.Text = FilmJson.Text(share, "url");
        PasswordBox.Text = FilmJson.Text(share, "password");
        SeasonBox.Text = FilmJson.Text(storedValue, "seasonHint");
        EnabledCheck.IsChecked = stored is null || FilmJson.Bool(storedValue, "enabled");
        AutoDisableCheck.IsChecked = FilmJson.Bool(storedValue, "autoDisable");
    }

    internal bool TryBuildPayload(out JsonElement payload, out string? errorMessage)
    {
        payload = default;
        errorMessage = null;
        if (!int.TryParse(SeasonBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int season) && SeasonBox.Text.Length > 0 ||
            SeasonBox.Text.Length > 0 && season < 0)
        {
            errorMessage = "季度编号无效";
            return false;
        }
        string kind = (string)KindBox.SelectedValue;
        var fields = new JsonObject
        {
            ["id"] = existingId.Length > 0 ? existingId : Guid.NewGuid().ToString(),
            ["targetId"] = TargetBox.Text.Trim(),
            ["title"] = TitleBox.Text.Trim(),
            ["kind"] = kind,
            ["enabled"] = EnabledCheck.IsChecked == true,
            ["autoDisable"] = AutoDisableCheck.IsChecked == true
        };
        if (NameBox.Text.Length > 0) fields["name"] = NameBox.Text;
        if (FilmJson.Integer(storedValue, "failureCount") is { } failures) fields["failureCount"] = failures;
        if (SeasonBox.Text.Length > 0) fields["seasonHint"] = season;
        if (kind == "site")
        {
            fields["sourceKey"] = SourceBox.Text;
            if (Equals(DetailTypeBox.SelectedValue, "number"))
            {
                if (!long.TryParse(DetailBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long detailId) ||
                    detailId is < -9007199254740991 or > 9007199254740991)
                {
                    errorMessage = "数字详情编号无效";
                    return false;
                }
                fields["detailId"] = detailId;
            }
            else fields["detailId"] = DetailBox.Text;
        }
        else
        {
            fields["provider"] = ProviderBox.SelectedValue as string;
            fields["share"] = new JsonObject { ["url"] = UrlBox.Text.Trim(), ["password"] = PasswordBox.Text };
        }
        payload = JsonSerializer.SerializeToElement(fields);
        return true;
    }

    internal void ClearSensitiveInputs()
    {
        UrlBox.Text = "";
        PasswordBox.Text = "";
    }
}
