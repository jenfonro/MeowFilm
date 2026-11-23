using System.Text.Json;
using MeowFilm.Desktop;

namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    private static readonly MusicDesktopLyricsRecord DefaultDesktopLyrics =
        new(0, "single", "#FFB5C5", "#FFF2A8", null);

    private static MusicDesktopLyricsRecord DesktopLyricSettings(MusicLibrarySession doc)
    {
        var value = doc.ReadDesktopLyrics() ?? DefaultDesktopLyrics;
        try { ValidateDesktopLyricSettings(value); }
        catch (MusicLibraryException) { throw new MusicLibraryException("CORRUPT_STORAGE"); }
        return value;
    }

    private static void ValidateDesktopLyricSettings(MusicDesktopLyricsRecord value)
    {
        MusicRules.Require(value.Revision is >= 0 and <= MusicRules.MaxInteger);
        MusicRules.Require(value.Layout is "single" or "double" &&
            AppearanceColors.TryParse(value.NormalColor, out _) &&
            AppearanceColors.TryParse(value.HighlightColor, out _));
        if (value.Geometry is { } geometry)
        {
            MusicRules.Require(MusicRules.IsValidId(geometry.Monitor, 32));
            MusicRules.Require(double.IsFinite(geometry.X) && double.IsFinite(geometry.Y) &&
                double.IsFinite(geometry.Width) && geometry.Width > 0 &&
                double.IsFinite(geometry.Height) && geometry.Height > 0);
        }
    }

    private static MusicDesktopLyricsGeometryRecord ReadGeometry(JsonElement geometry)
    {
        MusicWireCodec.RequireFields(geometry, "monitor x y width height");
        string monitor = MusicWireCodec.RequireIdProp(geometry, "monitor", 32);
        double x = MusicWireCodec.RequireFiniteDouble(geometry.GetProperty("x"));
        double y = MusicWireCodec.RequireFiniteDouble(geometry.GetProperty("y"));
        double width = MusicWireCodec.RequireFiniteDouble(geometry.GetProperty("width"));
        double height = MusicWireCodec.RequireFiniteDouble(geometry.GetProperty("height"));
        MusicRules.Require(width > 0 && height > 0);
        return new MusicDesktopLyricsGeometryRecord(monitor, x, y, width, height);
    }

    private (JsonElement Result, bool Mutate) DesktopLyricSettingsCommand(string method, JsonElement p, MusicLibrarySession doc)
    {
        MusicRules.Require(pending is null, "PENDING_WRITE");
        var saved = DesktopLyricSettings(doc);
        if (method == "music.lyrics.desktop.settings.get")
        {
            return (MusicWireCodec.Ok(w =>
            {
                w.WritePropertyName("settings");
                MusicWireCodec.WriteDesktopLyrics(w, saved);
            }), false);
        }
        MusicRules.Require(method == "music.lyrics.desktop.settings.save", "CAPABILITY_UNAVAILABLE");
        MusicWireCodec.RequireFields(p, "ifRevision layout normalColor highlightColor");
        MusicRules.Require(MusicWireCodec.RequireIntProp(p, "ifRevision") == saved.Revision, "REVISION_CONFLICT");

        string layout = MusicWireCodec.RequireStrProp(p, "layout");
        string rawNormal = MusicWireCodec.RequireStrProp(p, "normalColor");
        string rawHighlight = MusicWireCodec.RequireStrProp(p, "highlightColor");
        MusicDesktopLyricsGeometryRecord? geometry = saved.Geometry;
        if (MusicWireCodec.TryGet(p, "geometry", out var geomEl))
        {
            geometry = ReadGeometry(MusicWireCodec.RequireObject(geomEl));
        }

        if (layout is not ("single" or "double") ||
            !AppearanceColors.TryParse(rawNormal, out var normal) ||
            !AppearanceColors.TryParse(rawHighlight, out var highlight))
            throw new MusicLibraryException("INVALID_REQUEST");
        string normalHex = AppearanceColors.Hex(normal);
        string highlightHex = AppearanceColors.Hex(highlight);

        if (saved.Layout == layout &&
            saved.NormalColor == normalHex &&
            saved.HighlightColor == highlightHex &&
            Equals(saved.Geometry, geometry))
        {
            var unchanged = saved with { NormalColor = normalHex, HighlightColor = highlightHex };
            return (MusicWireCodec.Ok(w =>
            {
                w.WritePropertyName("settings");
                MusicWireCodec.WriteDesktopLyrics(w, unchanged);
            }), false);
        }

        var next = new MusicDesktopLyricsRecord(
            checked(saved.Revision + 1), layout, normalHex, highlightHex, geometry);
        ValidateDesktopLyricSettings(next);
        doc.SetDesktopLyrics(next);
        return (MusicWireCodec.Ok(w =>
        {
            w.WritePropertyName("settings");
            MusicWireCodec.WriteDesktopLyrics(w, next);
        }), true);
    }
}
