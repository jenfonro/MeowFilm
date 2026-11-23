using System.Globalization;
using System.Text.Json.Nodes;

namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    private const string DesktopLyricSettingsArea = "native-desktop-lyrics";
    private static bool TryNormalizeHexColor(string? value, out string normalized)
    {
        normalized = "";
        if (value is not { Length: 7 } || value[0] != '#' ||
            !uint.TryParse(value.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint rgb))
            return false;
        normalized = $"#{rgb:X6}";
        return true;
    }

    private JsonObject DesktopLyricSettings(MusicLibraryData doc)
    {
        var value = Area(doc, DesktopLyricSettingsArea) ??
        LJ.Obj(("schema", 1), ("revision", 0), ("layout", "single"),
            ("normalColor", "#FFB5C5"), ("highlightColor", "#FFF2A8"));
        try { ValidateDesktopLyricSettings(value); }
        catch (MusicLibraryException) { throw new MusicLibraryException("CORRUPT_STORAGE"); }
        return value;
    }

    private static void ValidateDesktopLyricSettings(JsonObject value)
    {
        LJ.Fields(value, "schema revision layout normalColor highlightColor");
        LJ.Require(LJ.I(value["schema"]) == 1);
        _ = Revision(value);
        LJ.Require(LJ.S(value["layout"]) is "single" or "double" &&
            TryNormalizeHexColor(LJ.S(value["normalColor"]), out _) &&
            TryNormalizeHexColor(LJ.S(value["highlightColor"]), out _));
        if (value["geometry"] is JsonObject geometry)
        {
            LJ.Fields(geometry, "monitor x y width height");
            _ = LJ.Text(geometry["monitor"], 32);
            double x = GeometryNumber(geometry["x"]), y = GeometryNumber(geometry["y"]),
                width = GeometryNumber(geometry["width"]), height = GeometryNumber(geometry["height"]);
            LJ.Require(double.IsFinite(x) && double.IsFinite(y) &&
                double.IsFinite(width) && width > 0 && double.IsFinite(height) && height > 0);
        }
        else LJ.Require(!value.ContainsKey("geometry"));
    }

    private static double GeometryNumber(JsonNode? value)
    {
        LJ.Require(value is JsonValue number && number.TryGetValue<double>(out _));
        return ((JsonValue)value!).GetValue<double>();
    }

    private (JsonObject Result, bool Mutate) DesktopLyricSettingsCommand(string method, JsonObject p, MusicLibraryData doc)
    {
        LJ.Require(pending is null, "PENDING_WRITE");
        var saved = method == "music.lyrics.desktop.settings.get" ? DesktopLyricSettings(doc) :
            OverwriteSettings(doc, DesktopLyricSettingsArea, 1, LJ.Obj(("schema", 1), ("revision", 0)));
        if (method == "music.lyrics.desktop.settings.get")
        {
            return (LJ.Obj(("settings", saved)), false);
        }
        LJ.Require(method == "music.lyrics.desktop.settings.save", "CAPABILITY_UNAVAILABLE");
        LJ.Fields(p, "ifRevision layout normalColor highlightColor");
        LJ.Require(LJ.I(p["ifRevision"]) == Revision(saved), "REVISION_CONFLICT");
        var next = (JsonObject)saved.DeepClone();
        if (!p.ContainsKey("geometry") && doc.Select(DesktopLyricSettingsArea, "geometry") is { } geometry && geometry.ContainsKey("geometry"))
        {
            var consumed = LJ.Obj(("schema", 1), ("revision", 0), ("layout", "single"),
                ("normalColor", "#FFB5C5"), ("highlightColor", "#FFF2A8"), ("geometry", geometry["geometry"]));
            try { ValidateDesktopLyricSettings(consumed); }
            catch (MusicLibraryException) { throw new MusicLibraryException("CORRUPT_STORAGE"); }
            next["geometry"] = geometry["geometry"]?.DeepClone();
        }
        next["revision"] = checked(Revision(saved) + 1);
        next["layout"] = p["layout"]?.DeepClone();
        next["normalColor"] = p["normalColor"]?.DeepClone();
        next["highlightColor"] = p["highlightColor"]?.DeepClone();
        if (p.ContainsKey("geometry"))
        {
            LJ.Require(p["geometry"] is JsonObject);
            var incoming = LJ.Object(p["geometry"]);
            next["geometry"] = new JsonObject(new[] { "monitor", "x", "y", "width", "height" }
                .Where(incoming.ContainsKey).Select(name => KeyValuePair.Create(name, incoming[name]?.DeepClone())));
        }
        ValidateDesktopLyricSettings(next);
        TryNormalizeHexColor(LJ.S(next["normalColor"]), out var normal);
        TryNormalizeHexColor(LJ.S(next["highlightColor"]), out var highlight);
        next["normalColor"] = normal;
        next["highlightColor"] = highlight;
        var comparable = next.DeepClone().AsObject(); comparable.Remove("revision");
        if (doc.Select(DesktopLyricSettingsArea, "schema") is null && !next.ContainsKey("geometry") &&
            LJ.S(next["layout"]) == "single" && LJ.S(next["normalColor"]) == "#FFB5C5" && LJ.S(next["highlightColor"]) == "#FFF2A8" ||
            doc.Matches(DesktopLyricSettingsArea, [], [doc.Scope], comparable))
        { next["revision"] = saved["revision"]?.DeepClone(); return (LJ.Obj(("settings", next)), false); }
        Set(doc, DesktopLyricSettingsArea, next, partial: true);
        return (LJ.Obj(("settings", next)), true);
    }
}
