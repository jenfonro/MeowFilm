using System.Globalization;

namespace MeowFilm.Capabilities.Library;

public sealed partial class MusicLibraryOwner
{
    private static readonly MusicDesktopLyricsRecord DefaultDesktopLyrics =
        new(0, "single", "#FFB5C5", "#FFF2A8", null);

    private static bool TryNormalizeHexColor(string? value, out string normalized)
    {
        normalized = "";
        if (value is not { Length: 7 } || value[0] != '#' ||
            !uint.TryParse(value.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint rgb))
            return false;
        normalized = $"#{rgb:X6}";
        return true;
    }

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
            TryNormalizeHexColor(value.NormalColor, out _) &&
            TryNormalizeHexColor(value.HighlightColor, out _));
        if (value.Geometry is { } geometry)
        {
            MusicRules.Require(MusicRules.IsValidId(geometry.Monitor, 32));
            MusicRules.Require(double.IsFinite(geometry.X) && double.IsFinite(geometry.Y) &&
                double.IsFinite(geometry.Width) && geometry.Width > 0 &&
                double.IsFinite(geometry.Height) && geometry.Height > 0);
        }
    }

    public Task<MusicDesktopLyricsRecord> GetDesktopLyricsAsync(CancellationToken cancellationToken = default) =>
        RunLibraryReadAsync(doc =>
        {
            MusicRules.Require(pending is null, "PENDING_WRITE");
            return DesktopLyricSettings(doc);
        }, cancellationToken, authoritativeFor: PendingOwner.DesktopLyrics);

    public Task<MusicDesktopLyricsRecord> SaveDesktopLyricsAsync(
        long ifRevision,
        string layout,
        string normalColor,
        string highlightColor,
        MusicDesktopLyricsGeometryRecord? geometry = null,
        bool includeGeometry = false,
        CancellationToken cancellationToken = default) =>
        RunLibraryWriteAsync(PendingOwner.DesktopLyrics, doc =>
        {
            MusicRules.Require(pending is null, "PENDING_WRITE");
            var saved = DesktopLyricSettings(doc);
            MusicRules.Require(ifRevision == saved.Revision, "REVISION_CONFLICT");

            if (layout is not ("single" or "double") ||
                !TryNormalizeHexColor(normalColor, out string normalHex) ||
                !TryNormalizeHexColor(highlightColor, out string highlightHex))
                throw new MusicLibraryException("INVALID_REQUEST");

            MusicDesktopLyricsGeometryRecord? nextGeometry = saved.Geometry;
            if (includeGeometry)
            {
                MusicRules.Require(geometry is not null, "INVALID_REQUEST");
                nextGeometry = geometry;
            }

            if (saved.Layout == layout &&
                saved.NormalColor == normalHex &&
                saved.HighlightColor == highlightHex &&
                Equals(saved.Geometry, nextGeometry))
            {
                return (saved with { NormalColor = normalHex, HighlightColor = highlightHex }, false);
            }

            var next = new MusicDesktopLyricsRecord(
                checked(saved.Revision + 1), layout, normalHex, highlightHex, nextGeometry);
            ValidateDesktopLyricSettings(next);
            doc.SetDesktopLyrics(next);
            return (next, true);
        }, cancellationToken);
}
