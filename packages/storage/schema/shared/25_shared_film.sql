-- Shared native App film configuration inputs, favorites, and deleted source tombstones (Windows & iOS).
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
    source_id TEXT NOT NULL DEFAULT '',
    item_id TEXT NOT NULL,
    title TEXT NOT NULL DEFAULT '',
    kind TEXT,
    poster TEXT NOT NULL DEFAULT '',
    score TEXT NOT NULL DEFAULT '',
    text_badge TEXT NOT NULL DEFAULT '',
    subtitle TEXT NOT NULL DEFAULT '',
    target_id TEXT,
    target_kind TEXT CHECK (target_kind IS NULL OR target_kind IN ('movie', 'tv', 'source', 'group')),
    target_title TEXT,
    target_year INTEGER,
    metadata_provider TEXT,
    metadata_id_kind TEXT CHECK (metadata_id_kind IS NULL OR metadata_id_kind IN ('text', 'integer')),
    metadata_id_text TEXT,
    season INTEGER,
    episode INTEGER,
    global_episode INTEGER,
    source_key TEXT,
    source_engine TEXT,
    source_installation_id TEXT,
    source_site_kind TEXT CHECK (source_site_kind IS NULL OR source_site_kind IN ('text', 'integer')),
    source_site_text TEXT,
    source_detail_kind TEXT CHECK (source_detail_kind IS NULL OR source_detail_kind IN ('text', 'integer')),
    source_detail_text TEXT,
    source_line_kind TEXT CHECK (source_line_kind IS NULL OR source_line_kind IN ('text', 'integer')),
    source_line_text TEXT,
    added_at REAL NOT NULL DEFAULT 0,
    row_revision INTEGER NOT NULL DEFAULT 1 CHECK (row_revision BETWEEN 1 AND 9007199254740991),
    CHECK ((metadata_id_kind IS NULL) = (metadata_id_text IS NULL)),
    CHECK ((source_site_kind IS NULL) = (source_site_text IS NULL)),
    CHECK ((source_detail_kind IS NULL) = (source_detail_text IS NULL)),
    CHECK ((source_line_kind IS NULL) = (source_line_text IS NULL))
) STRICT;

CREATE INDEX idx_film_favorites_order ON film_favorites(position);
CREATE INDEX idx_film_favorites_recent ON film_favorites(added_at DESC, favorite_id);

CREATE TABLE film_favorite_members (
    favorite_id TEXT NOT NULL REFERENCES film_favorites(favorite_id) ON DELETE CASCADE,
    position INTEGER NOT NULL CHECK (position >= 0),
    source_key TEXT NOT NULL,
    detail_kind TEXT NOT NULL CHECK (detail_kind IN ('text', 'integer')),
    detail_text TEXT NOT NULL,
    PRIMARY KEY (favorite_id, position)
) STRICT;
