-- iOS-specific film player preferences, source probes, and video site intents (owner CAS uses owner_revisions).
CREATE TABLE ios_film_player_preferences (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    engine TEXT NOT NULL,
    default_rate REAL NOT NULL,
    hold_rate REAL NOT NULL,
    seek_step REAL NOT NULL,
    shows_control_center INTEGER NOT NULL CHECK (shows_control_center IN (0, 1)),
    recognizes_chapters INTEGER NOT NULL CHECK (recognizes_chapters IN (0, 1)),
    single_tap_center TEXT NOT NULL,
    single_tap_sides TEXT NOT NULL,
    double_tap_center TEXT NOT NULL,
    double_tap_sides TEXT NOT NULL,
    swipe_volume INTEGER NOT NULL CHECK (swipe_volume IN (0, 1)),
    swipe_brightness INTEGER NOT NULL CHECK (swipe_brightness IN (0, 1)),
    swipe_seek INTEGER NOT NULL CHECK (swipe_seek IN (0, 1)),
    swipe_episodes INTEGER NOT NULL CHECK (swipe_episodes IN (0, 1)),
    haptic_hold INTEGER NOT NULL CHECK (haptic_hold IN (0, 1)),
    haptic_brightness INTEGER NOT NULL CHECK (haptic_brightness IN (0, 1)),
    haptic_volume INTEGER NOT NULL CHECK (haptic_volume IN (0, 1)),
    haptic_seek INTEGER NOT NULL CHECK (haptic_seek IN (0, 1)),
    row_revision INTEGER NOT NULL CHECK (row_revision BETWEEN 1 AND 9007199254740991)
) STRICT;

CREATE TABLE ios_film_source_probes (
    installation_id TEXT PRIMARY KEY NOT NULL,
    input_revision INTEGER NOT NULL,
    imported_at INTEGER,
    updated_at INTEGER,
    update_state TEXT NOT NULL,
    update_error TEXT,
    source_hash TEXT
) STRICT;

CREATE TABLE ios_film_source_sites (
    installation_id TEXT NOT NULL REFERENCES ios_film_source_probes(installation_id) ON DELETE CASCADE,
    site_key TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    name TEXT NOT NULL,
    api TEXT NOT NULL,
    PRIMARY KEY (installation_id, site_key)
) STRICT;

CREATE TABLE ios_film_source_actions (
    installation_id TEXT NOT NULL,
    site_key TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    action TEXT NOT NULL,
    PRIMARY KEY (installation_id, site_key, position),
    FOREIGN KEY (installation_id, site_key) REFERENCES ios_film_source_sites(installation_id, site_key) ON DELETE CASCADE
) STRICT;

CREATE TABLE ios_film_source_methods (
    installation_id TEXT NOT NULL,
    site_key TEXT NOT NULL,
    action TEXT NOT NULL,
    method TEXT NOT NULL,
    PRIMARY KEY (installation_id, site_key, action),
    FOREIGN KEY (installation_id, site_key) REFERENCES ios_film_source_sites(installation_id, site_key) ON DELETE CASCADE
) STRICT;

CREATE TABLE ios_film_video_intents (
    installation_id TEXT NOT NULL,
    site_key TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    name TEXT NOT NULL,
    api TEXT NOT NULL,
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
    home_visible INTEGER NOT NULL CHECK (home_visible IN (0, 1)),
    search_enabled INTEGER NOT NULL CHECK (search_enabled IN (0, 1)),
    aggregate_images INTEGER NOT NULL CHECK (aggregate_images IN (0, 1)),
    PRIMARY KEY (installation_id, site_key)
) STRICT;

CREATE UNIQUE INDEX idx_ios_film_one_aggregate_images ON ios_film_video_intents(aggregate_images) WHERE aggregate_images = 1;

CREATE TABLE ios_film_video_actions (
    installation_id TEXT NOT NULL,
    site_key TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    action TEXT NOT NULL,
    PRIMARY KEY (installation_id, site_key, position),
    FOREIGN KEY (installation_id, site_key) REFERENCES ios_film_video_intents(installation_id, site_key) ON DELETE CASCADE
) STRICT;

CREATE TABLE ios_film_video_methods (
    installation_id TEXT NOT NULL,
    site_key TEXT NOT NULL,
    action TEXT NOT NULL,
    method TEXT NOT NULL,
    PRIMARY KEY (installation_id, site_key, action),
    FOREIGN KEY (installation_id, site_key) REFERENCES ios_film_video_intents(installation_id, site_key) ON DELETE CASCADE
) STRICT;

CREATE TABLE ios_film_video_detections (
    installation_id TEXT NOT NULL,
    site_key TEXT NOT NULL,
    result TEXT NOT NULL,
    checked_at INTEGER NOT NULL CHECK (checked_at BETWEEN 0 AND 9007199254740991),
    playback_resolved INTEGER NOT NULL CHECK (playback_resolved IN (0, 1)),
    PRIMARY KEY (installation_id, site_key),
    FOREIGN KEY (installation_id, site_key) REFERENCES ios_film_video_intents(installation_id, site_key) ON DELETE CASCADE
) STRICT;

CREATE TABLE ios_film_video_probe_steps (
    installation_id TEXT NOT NULL,
    site_key TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    operation TEXT NOT NULL,
    state TEXT NOT NULL,
    error TEXT,
    PRIMARY KEY (installation_id, site_key, position),
    FOREIGN KEY (installation_id, site_key) REFERENCES ios_film_video_detections(installation_id, site_key) ON DELETE CASCADE
) STRICT;
