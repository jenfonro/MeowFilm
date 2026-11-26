-- Shared native App film configuration inputs, favorites, source probes, and video sources (Windows & iOS).
CREATE TABLE cat_scripts (
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

CREATE INDEX idx_cat_scripts_order ON cat_scripts(position, installation_id);

CREATE TABLE film_source_deletions (
    installation_id TEXT NOT NULL,
    site_key TEXT NOT NULL,
    position INTEGER NOT NULL DEFAULT 0 CHECK (position >= 0),
    PRIMARY KEY (installation_id, site_key)
) STRICT;

CREATE TABLE film_favorites (
    favorite_id TEXT PRIMARY KEY NOT NULL,
    position INTEGER NOT NULL DEFAULT 0 CHECK (position >= 0),
    keyword TEXT NOT NULL,
    title TEXT NOT NULL,
    poster TEXT NOT NULL DEFAULT '',
    score TEXT NOT NULL DEFAULT '',
    text_badge TEXT NOT NULL DEFAULT '',
    subtitle TEXT NOT NULL DEFAULT '',
    content_kind TEXT CHECK (content_kind IS NULL OR content_kind IN ('movie', 'tv')),
    year INTEGER,
    tmdb_kind TEXT CHECK (tmdb_kind IS NULL OR tmdb_kind IN ('movie', 'tv')),
    tmdb_id INTEGER CHECK (tmdb_id IS NULL OR tmdb_id > 0),
    douban_kind TEXT CHECK (douban_kind IS NULL OR douban_kind IN ('movie', 'tv')),
    douban_id TEXT,
    added_at_ms INTEGER NOT NULL DEFAULT 0,
    CHECK ((tmdb_kind IS NULL) = (tmdb_id IS NULL)),
    CHECK ((douban_kind IS NULL) = (douban_id IS NULL))
) STRICT;

CREATE INDEX idx_film_favorites_order ON film_favorites(position, favorite_id);

-- Unified cross-platform film Cat source probes and video site configuration / detection records.
-- Cat configuration fields (name, source_type, source_url, enabled) are owned exclusively by cat_scripts.
CREATE TABLE film_source_probes (
    installation_id TEXT PRIMARY KEY NOT NULL,
    position INTEGER NOT NULL DEFAULT 0 CHECK (position >= 0),
    input_revision INTEGER NOT NULL,
    imported_at_ms INTEGER,
    updated_at_ms INTEGER,
    update_state TEXT NOT NULL,
    update_error TEXT,
    source_hash TEXT
) STRICT;

CREATE INDEX idx_film_source_probes_order ON film_source_probes(position, installation_id);

CREATE TABLE film_source_sites (
    installation_id TEXT NOT NULL REFERENCES film_source_probes(installation_id) ON DELETE CASCADE,
    site_key TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    name TEXT NOT NULL,
    api TEXT NOT NULL,
    PRIMARY KEY (installation_id, site_key)
) STRICT;

CREATE TABLE film_source_site_actions (
    installation_id TEXT NOT NULL,
    site_key TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    action TEXT NOT NULL,
    PRIMARY KEY (installation_id, site_key, position),
    FOREIGN KEY (installation_id, site_key)
        REFERENCES film_source_sites(installation_id, site_key) ON DELETE CASCADE
) STRICT;

CREATE TABLE film_source_site_methods (
    installation_id TEXT NOT NULL,
    site_key TEXT NOT NULL,
    action TEXT NOT NULL,
    method TEXT NOT NULL,
    PRIMARY KEY (installation_id, site_key, action),
    FOREIGN KEY (installation_id, site_key)
        REFERENCES film_source_sites(installation_id, site_key) ON DELETE CASCADE
) STRICT;

CREATE TABLE film_video_sources (
    installation_id TEXT NOT NULL,
    site_key TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    sort_order INTEGER NOT NULL DEFAULT 0,
    name TEXT NOT NULL,
    api TEXT NOT NULL,
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
    home_visible INTEGER NOT NULL CHECK (home_visible IN (0, 1)),
    search_enabled INTEGER NOT NULL CHECK (search_enabled IN (0, 1)),
    aggregate_images INTEGER NOT NULL CHECK (aggregate_images IN (0, 1)),
    detection_result TEXT,
    detection_checked_at_ms INTEGER CHECK (detection_checked_at_ms IS NULL OR detection_checked_at_ms BETWEEN 0 AND 9007199254740991),
    detection_playback_resolved INTEGER CHECK (detection_playback_resolved IS NULL OR detection_playback_resolved IN (0, 1)),
    PRIMARY KEY (installation_id, site_key),
    CHECK (
        (detection_result IS NULL) = (detection_checked_at_ms IS NULL) AND
        (detection_result IS NULL) = (detection_playback_resolved IS NULL)
    )
) STRICT;

CREATE INDEX idx_film_video_sources_order ON film_video_sources(position);
CREATE UNIQUE INDEX idx_film_video_one_aggregate_images ON film_video_sources(aggregate_images) WHERE aggregate_images = 1;

CREATE TABLE film_video_actions (
    installation_id TEXT NOT NULL,
    site_key TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    action TEXT NOT NULL,
    PRIMARY KEY (installation_id, site_key, position),
    FOREIGN KEY (installation_id, site_key)
        REFERENCES film_video_sources(installation_id, site_key) ON DELETE CASCADE
) STRICT;

CREATE TABLE film_video_methods (
    installation_id TEXT NOT NULL,
    site_key TEXT NOT NULL,
    action TEXT NOT NULL,
    method TEXT NOT NULL,
    PRIMARY KEY (installation_id, site_key, action),
    FOREIGN KEY (installation_id, site_key)
        REFERENCES film_video_sources(installation_id, site_key) ON DELETE CASCADE
) STRICT;

CREATE TABLE film_video_probe_steps (
    installation_id TEXT NOT NULL,
    site_key TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    operation TEXT NOT NULL,
    state TEXT NOT NULL,
    error TEXT,
    PRIMARY KEY (installation_id, site_key, position),
    FOREIGN KEY (installation_id, site_key)
        REFERENCES film_video_sources(installation_id, site_key) ON DELETE CASCADE
) STRICT;
