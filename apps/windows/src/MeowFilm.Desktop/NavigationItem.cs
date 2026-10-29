namespace MeowFilm.Desktop;

public record NavigationItem(string Route, string Title, string Glyph, string? Group = null, bool ShowInSidebar = true);
