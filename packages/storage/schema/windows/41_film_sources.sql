-- Windows-specific film source projections and video site detection records.
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
