using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities;

internal sealed record FilmManualMatchForm(
    string ExistingId,
    string TargetId,
    string Title,
    string Name,
    string Kind,
    string SourceKey,
    string DetailId,
    bool DetailIsNumber,
    string? Provider,
    string ShareUrl,
    string SharePassword,
    string SeasonText,
    bool Enabled,
    bool AutoDisable);

internal static class FilmManualMatchPayload
{
    internal static bool TryBuild(FilmManualMatchForm form, JsonElement storedValue,
        out JsonElement payload, out string? errorMessage)
    {
        payload = default;
        errorMessage = null;
        if (form.SeasonText.Length > 0 &&
            (!int.TryParse(form.SeasonText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int season) || season < 0))
        {
            errorMessage = "季度编号无效";
            return false;
        }
        int parsedSeason = form.SeasonText.Length > 0
            ? int.Parse(form.SeasonText, NumberStyles.Integer, CultureInfo.InvariantCulture)
            : 0;
        string kind = form.Kind == "share" ? "share" : "site";
        var fields = new JsonObject
        {
            ["id"] = form.ExistingId.Length > 0 ? form.ExistingId : Guid.NewGuid().ToString(),
            ["targetId"] = form.TargetId.Trim(),
            ["title"] = form.Title.Trim(),
            ["kind"] = kind,
            ["enabled"] = form.Enabled,
            ["autoDisable"] = form.AutoDisable
        };
        if (form.Name.Length > 0) fields["name"] = form.Name;
        if (FilmJson.Integer(storedValue, "failureCount") is { } failures) fields["failureCount"] = failures;
        if (form.SeasonText.Length > 0) fields["seasonHint"] = parsedSeason;
        if (kind == "site")
        {
            fields["sourceKey"] = form.SourceKey;
            if (form.DetailIsNumber)
            {
                if (!long.TryParse(form.DetailId, NumberStyles.Integer, CultureInfo.InvariantCulture, out long detailId) ||
                    detailId is < -9007199254740991 or > 9007199254740991)
                {
                    errorMessage = "数字详情编号无效";
                    return false;
                }
                fields["detailId"] = detailId;
            }
            else fields["detailId"] = form.DetailId;
        }
        else
        {
            fields["provider"] = form.Provider;
            fields["share"] = new JsonObject { ["url"] = form.ShareUrl.Trim(), ["password"] = form.SharePassword };
        }
        payload = JsonSerializer.SerializeToElement(fields);
        return true;
    }
}
