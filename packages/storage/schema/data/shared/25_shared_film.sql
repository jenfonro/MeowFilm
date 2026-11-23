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
    input_revision INTEGER NOT NULL CHECK (input_revision BETWEEN 1 AND 9007199254740991),
    row_revision INTEGER NOT NULL DEFAULT 1 CHECK (row_revision BETWEEN 1 AND 9007199254740991)
) STRICT;

CREATE INDEX idx_cat_scripts_order ON cat_scripts(position);

CREATE TABLE film_source_deletions (
    installation_id TEXT NOT NULL,
    site_key TEXT NOT NULL,
    position INTEGER NOT NULL DEFAULT 0 CHECK (position >= 0),
    PRIMARY KEY (installation_id, site_key)
) STRICT;

CREATE TABLE film_favorites (
    favorite_id TEXT PRIMARY KEY NOT NULL,
    position INTEGER NOT NULL DEFAULT 0 CHECK (position >= 0),
    item_id TEXT NOT NULL,
    target_id TEXT,
    target_kind TEXT CHECK (target_kind IS NULL OR target_kind IN ('movie', 'tv', 'source', 'group')),
    target_title TEXT,
    target_year INTEGER,
    metadata_provider TEXT,
    metadata_id TEXT,
    season INTEGER,
    episode INTEGER,
    global_episode INTEGER,
    source_key TEXT,
    source_engine TEXT,
    source_installation_id TEXT,
    source_site_id TEXT,
    source_detail_kind TEXT CHECK (source_detail_kind IS NULL OR source_detail_kind IN ('text', 'integer')),
    source_detail_text TEXT,
    source_line_kind TEXT CHECK (source_line_kind IS NULL OR source_line_kind IN ('text', 'integer')),
    source_line_text TEXT,
    added_at_ms INTEGER NOT NULL DEFAULT 0,
    row_revision INTEGER NOT NULL DEFAULT 1 CHECK (row_revision BETWEEN 1 AND 9007199254740991),
    CHECK ((source_detail_kind IS NULL) = (source_detail_text IS NULL)),
    CHECK ((source_line_kind IS NULL) = (source_line_text IS NULL))
) STRICT;

CREATE INDEX idx_film_favorites_order ON film_favorites(position);
CREATE INDEX idx_film_favorites_recent ON film_favorites(added_at_ms DESC, favorite_id);

CREATE TABLE film_favorite_members (
    favorite_id TEXT NOT NULL REFERENCES film_favorites(favorite_id) ON DELETE CASCADE,
    position INTEGER NOT NULL CHECK (position >= 0),
    source_key TEXT NOT NULL,
    detail_kind TEXT NOT NULL CHECK (detail_kind IN ('text', 'integer')),
    detail_text TEXT NOT NULL,
    PRIMARY KEY (favorite_id, position)
) STRICT;

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

CREATE INDEX idx_film_source_probes_order ON film_source_probes(position);

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
    PRIMARY KEY (installation_id, site_key)
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
