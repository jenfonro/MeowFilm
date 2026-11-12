using System.Text.Json.Nodes;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    private const string DesktopLyricSettingsArea = "native-desktop-lyrics";
    private JsonObject DesktopLyricSettings(JsonObject doc) => Value(doc, DesktopLyricSettingsArea) as JsonObject ??
        LJ.Obj(("schema", 1), ("revision", 0), ("layout", "single"),
            ("normalColor", "#FFB5C5"), ("highlightColor", "#FFF2A8"));

    private static void ValidateDesktopLyricSettings(JsonObject value)
    {
        LJ.Fields(value, "schema revision layout normalColor highlightColor",
            "schema revision layout normalColor highlightColor");
        LJ.Require(LJ.I(value["schema"]) == 1);
        _ = Revision(value);
        LJ.Require(LJ.S(value["layout"]) is "single" or "double" &&
            AppearanceColors.TryParse(LJ.S(value["normalColor"]), out _) &&
            AppearanceColors.TryParse(LJ.S(value["highlightColor"]), out _));
    }

    private (JsonObject Result, bool Mutate) DesktopLyricSettingsCommand(string method, JsonObject p, JsonObject doc)
    {
        LJ.Require(pending is null, "PENDING_WRITE");
        var saved = DesktopLyricSettings(doc);
        if (method == "music.lyrics.desktop.settings.get")
        {
            LJ.Fields(p, "");
            return (LJ.Obj(("settings", saved)), false);
        }
        LJ.Require(method == "music.lyrics.desktop.settings.save", "CAPABILITY_UNAVAILABLE");
        LJ.Fields(p, "ifRevision layout normalColor highlightColor",
            "ifRevision layout normalColor highlightColor");
        LJ.Require(LJ.I(p["ifRevision"]) == Revision(saved), "REVISION_CONFLICT");
        var next = LJ.Obj(("schema", 1), ("revision", checked(Revision(saved) + 1)),
            ("layout", p["layout"]), ("normalColor", p["normalColor"]), ("highlightColor", p["highlightColor"]));
        ValidateDesktopLyricSettings(next);
        AppearanceColors.TryParse(LJ.S(next["normalColor"]), out var normal);
        AppearanceColors.TryParse(LJ.S(next["highlightColor"]), out var highlight);
        next["normalColor"] = AppearanceColors.Hex(normal);
        next["highlightColor"] = AppearanceColors.Hex(highlight);
        if (LJ.S(saved["layout"]) == LJ.S(next["layout"]) &&
            LJ.S(saved["normalColor"]) == LJ.S(next["normalColor"]) &&
            LJ.S(saved["highlightColor"]) == LJ.S(next["highlightColor"]))
            return (LJ.Obj(("settings", saved)), false);
        Set(doc, DesktopLyricSettingsArea, next);
        return (LJ.Obj(("settings", next)), true);
    }
}
