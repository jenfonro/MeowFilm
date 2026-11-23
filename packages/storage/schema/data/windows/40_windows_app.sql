-- Windows desktop shell appearance, window close behavior, media shortcut bindings, and file grants.
CREATE TABLE windows_app_appearance (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    theme_color TEXT NOT NULL
) STRICT;

CREATE TABLE windows_app_window (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    close_behavior TEXT NOT NULL CHECK (close_behavior IN ('ask', 'minimizeToTray', 'exit'))
) STRICT;

CREATE TABLE windows_app_media_controls (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    global_enabled INTEGER NOT NULL CHECK (global_enabled IN (0, 1)),
    system_media_enabled INTEGER NOT NULL CHECK (system_media_enabled IN (0, 1))
) STRICT;

CREATE TABLE windows_app_media_bindings (
    action TEXT PRIMARY KEY NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    local_key TEXT,
    local_modifiers INTEGER,
    global_key TEXT,
    global_modifiers INTEGER,
    CHECK ((local_key IS NULL) = (local_modifiers IS NULL)),
    CHECK ((global_key IS NULL) = (global_modifiers IS NULL))
) STRICT;

CREATE TABLE windows_file_grants (
    grant_id TEXT PRIMARY KEY NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    file_path TEXT NOT NULL,
    title TEXT NOT NULL DEFAULT ''
) STRICT;

CREATE INDEX idx_windows_file_grants_order ON windows_file_grants(position);

-- Windows-specific film favorite display metadata and manual ordering paired 1:1 with film_favorites.
CREATE TABLE windows_film_favorite_display (
    favorite_id TEXT PRIMARY KEY NOT NULL REFERENCES film_favorites(favorite_id) ON DELETE CASCADE,
    position INTEGER NOT NULL DEFAULT 0 CHECK (position >= 0),
    title TEXT NOT NULL,
    poster TEXT NOT NULL DEFAULT '',
    score TEXT NOT NULL DEFAULT '',
    text_badge TEXT NOT NULL DEFAULT '',
    subtitle TEXT NOT NULL DEFAULT ''
) STRICT;

CREATE INDEX idx_windows_film_favorite_display_order ON windows_film_favorite_display(position);
