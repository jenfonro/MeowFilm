-- Windows native film settings (TMDB, Douban, rules, preferences, Cat, sources) and favorites.
CREATE TABLE windows_tmdb_settings (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    api_base TEXT NOT NULL,
    image_base TEXT NOT NULL,
    api_key TEXT NOT NULL,
    access_token TEXT NOT NULL,
    language TEXT NOT NULL,
    region TEXT NOT NULL,
    include_adult INTEGER NOT NULL CHECK (include_adult IN (0, 1))
) STRICT;

CREATE TABLE windows_douban_settings (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    api_base TEXT NOT NULL,
    search_base TEXT NOT NULL,
    image_base TEXT NOT NULL,
    api_proxy TEXT NOT NULL,
    image_proxy TEXT NOT NULL,
    cookie TEXT NOT NULL
) STRICT;

CREATE TABLE windows_film_rules (
    rule_group TEXT NOT NULL CHECK (rule_group IN ('episodeCleanRegexRules', 'episodeRules', 'movieRules', 'aggregateRegexRules')),
    position INTEGER NOT NULL CHECK (position >= 0),
    pattern TEXT NOT NULL,
    replacement TEXT,
    flags TEXT,
    PRIMARY KEY (rule_group, position),
    CHECK (rule_group IN ('episodeRules', 'movieRules') OR (replacement IS NULL AND flags IS NULL))
) STRICT;

CREATE TABLE windows_film_preferences (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    ios_auto_prepare INTEGER NOT NULL CHECK (ios_auto_prepare IN (0, 1)),
    metadata_view_provider TEXT NOT NULL CHECK (metadata_view_provider IN ('tmdb', 'douban'))
) STRICT;

CREATE TABLE windows_film_preference_items (
    list_kind TEXT NOT NULL CHECK (list_kind IN ('ruleOrder', 'panOrder', 'keywords', 'siteCleanKeywords')),
    position INTEGER NOT NULL CHECK (position >= 0),
    item_value TEXT NOT NULL,
    PRIMARY KEY (list_kind, position)
) STRICT;

CREATE TABLE windows_cat_settings (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    port INTEGER NOT NULL CHECK (port BETWEEN 0 AND 65535)
) STRICT;

CREATE TABLE windows_cat_providers (
    provider TEXT PRIMARY KEY NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1))
) STRICT;

CREATE TABLE windows_cat_mappings (
    position INTEGER PRIMARY KEY CHECK (position >= 0),
    provider TEXT NOT NULL
) STRICT;

CREATE TABLE windows_cat_mapping_aliases (
    mapping_position INTEGER NOT NULL REFERENCES windows_cat_mappings(position) ON DELETE CASCADE,
    position INTEGER NOT NULL CHECK (position >= 0),
    alias TEXT NOT NULL,
    PRIMARY KEY (mapping_position, position)
) STRICT;

CREATE TABLE windows_cat_scripts (
    installation_id TEXT PRIMARY KEY NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    name TEXT NOT NULL,
    source_type TEXT NOT NULL CHECK (source_type IN ('local', 'network')),
    source_url TEXT,
    local_path TEXT,
    local_hash TEXT,
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
    input_revision INTEGER NOT NULL CHECK (input_revision BETWEEN 1 AND 9007199254740991)
) STRICT;

CREATE INDEX idx_windows_cat_scripts_order ON windows_cat_scripts(position);

CREATE TABLE windows_film_source_cats (
    installation_id TEXT PRIMARY KEY NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    name TEXT NOT NULL,
    source_type TEXT NOT NULL CHECK (source_type IN ('local', 'network')),
    source_url TEXT,
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
    imported_at INTEGER,
    updated_at INTEGER,
    update_state TEXT NOT NULL,
    update_error TEXT,
    source_hash TEXT,
    input_revision INTEGER NOT NULL
) STRICT;

CREATE TABLE windows_film_source_cat_sites (
    installation_id TEXT NOT NULL REFERENCES windows_film_source_cats(installation_id) ON DELETE CASCADE,
    site_key TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    name TEXT NOT NULL,
    api TEXT NOT NULL,
    PRIMARY KEY (installation_id, site_key)
) STRICT;

CREATE TABLE windows_film_source_cat_site_actions (
    installation_id TEXT NOT NULL,
    site_key TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    action TEXT NOT NULL,
    PRIMARY KEY (installation_id, site_key, position),
    FOREIGN KEY (installation_id, site_key)
        REFERENCES windows_film_source_cat_sites(installation_id, site_key) ON DELETE CASCADE
) STRICT;

CREATE TABLE windows_film_source_cat_site_methods (
    installation_id TEXT NOT NULL,
    site_key TEXT NOT NULL,
    method_key TEXT NOT NULL,
    method_name TEXT NOT NULL,
    PRIMARY KEY (installation_id, site_key, method_key),
    FOREIGN KEY (installation_id, site_key)
        REFERENCES windows_film_source_cat_sites(installation_id, site_key) ON DELETE CASCADE
) STRICT;

CREATE TABLE windows_film_source_videos (
    installation_id TEXT NOT NULL,
    site_key TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    sort_order INTEGER NOT NULL,
    name TEXT NOT NULL,
    api TEXT NOT NULL,
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
    home_visible INTEGER NOT NULL CHECK (home_visible IN (0, 1)),
    search_enabled INTEGER NOT NULL CHECK (search_enabled IN (0, 1)),
    aggregate_images INTEGER NOT NULL CHECK (aggregate_images IN (0, 1)),
    detection_result TEXT,
    detection_checked_at INTEGER,
    detection_playback_resolved INTEGER CHECK (detection_playback_resolved IS NULL OR detection_playback_resolved IN (0, 1)),
    PRIMARY KEY (installation_id, site_key)
) STRICT;

CREATE INDEX idx_windows_film_source_videos_order ON windows_film_source_videos(position);

CREATE TABLE windows_film_source_video_actions (
    installation_id TEXT NOT NULL,
    site_key TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    action TEXT NOT NULL,
    PRIMARY KEY (installation_id, site_key, position),
    FOREIGN KEY (installation_id, site_key)
        REFERENCES windows_film_source_videos(installation_id, site_key) ON DELETE CASCADE
) STRICT;

CREATE TABLE windows_film_source_video_methods (
    installation_id TEXT NOT NULL,
    site_key TEXT NOT NULL,
    method_key TEXT NOT NULL,
    method_name TEXT NOT NULL,
    PRIMARY KEY (installation_id, site_key, method_key),
    FOREIGN KEY (installation_id, site_key)
        REFERENCES windows_film_source_videos(installation_id, site_key) ON DELETE CASCADE
) STRICT;

CREATE TABLE windows_film_source_video_steps (
    installation_id TEXT NOT NULL,
    site_key TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    operation TEXT NOT NULL,
    state TEXT NOT NULL,
    error TEXT,
    PRIMARY KEY (installation_id, site_key, position),
    FOREIGN KEY (installation_id, site_key)
        REFERENCES windows_film_source_videos(installation_id, site_key) ON DELETE CASCADE
) STRICT;

CREATE TABLE windows_film_source_deletions (
    installation_id TEXT NOT NULL,
    site_key TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    PRIMARY KEY (installation_id, site_key)
) STRICT;

CREATE TABLE windows_film_favorites (
    favorite_id TEXT PRIMARY KEY NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    item_id TEXT NOT NULL,
    title TEXT NOT NULL,
    poster TEXT NOT NULL DEFAULT '',
    score TEXT NOT NULL DEFAULT '',
    text_badge TEXT NOT NULL DEFAULT '',
    subtitle TEXT NOT NULL DEFAULT '',
    target_id TEXT,
    target_kind TEXT,
    target_title TEXT,
    target_metadata_provider TEXT,
    target_metadata_id TEXT,
    target_year INTEGER,
    target_season INTEGER,
    target_episode INTEGER,
    target_global_episode INTEGER,
    source_key TEXT,
    source_engine TEXT,
    source_installation_id TEXT,
    source_site_kind TEXT CHECK (source_site_kind IS NULL OR source_site_kind IN ('text', 'number')),
    source_site_value TEXT,
    source_detail_kind TEXT CHECK (source_detail_kind IS NULL OR source_detail_kind IN ('text', 'number')),
    source_detail_value TEXT,
    source_line_kind TEXT CHECK (source_line_kind IS NULL OR source_line_kind IN ('text', 'number')),
    source_line_value TEXT,
    CHECK ((source_site_kind IS NULL) = (source_site_value IS NULL)),
    CHECK ((source_detail_kind IS NULL) = (source_detail_value IS NULL)),
    CHECK ((source_line_kind IS NULL) = (source_line_value IS NULL))
) STRICT;

CREATE INDEX idx_windows_film_favorites_order ON windows_film_favorites(position);

CREATE TABLE windows_film_favorite_members (
    favorite_id TEXT NOT NULL REFERENCES windows_film_favorites(favorite_id) ON DELETE CASCADE,
    position INTEGER NOT NULL CHECK (position >= 0),
    source_key TEXT NOT NULL,
    detail_kind TEXT NOT NULL CHECK (detail_kind IN ('text', 'number')),
    detail_value TEXT NOT NULL,
    PRIMARY KEY (favorite_id, position)
) STRICT;
