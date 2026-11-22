-- Music script catalog, declared capabilities, check outcomes, and per-key LX guest KV (revision via owner_revisions).
CREATE TABLE music_script_installations (
    installation_id TEXT PRIMARY KEY NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    name TEXT NOT NULL,
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
    origin_kind TEXT NOT NULL CHECK (origin_kind IN ('file', 'network')),
    origin_url TEXT,
    current_hash TEXT NOT NULL,
    current_bytes INTEGER NOT NULL CHECK (current_bytes > 0),
    current_version TEXT NOT NULL,
    current_author TEXT NOT NULL,
    current_description TEXT NOT NULL,
    current_homepage TEXT NOT NULL,
    previous_hash TEXT,
    previous_bytes INTEGER CHECK (previous_bytes IS NULL OR previous_bytes > 0),
    previous_version TEXT,
    previous_author TEXT,
    previous_description TEXT,
    previous_homepage TEXT,
    declared_hash TEXT,
    check_hash TEXT,
    checked_at_ms INTEGER,
    check_failure TEXT
) STRICT;

CREATE INDEX idx_music_script_installations_position ON music_script_installations(position);

CREATE TABLE music_script_declared_sites (
    installation_id TEXT NOT NULL REFERENCES music_script_installations(installation_id) ON DELETE CASCADE,
    site_id TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    has_qualities INTEGER NOT NULL CHECK (has_qualities IN (0, 1)),
    PRIMARY KEY (installation_id, site_id)
) STRICT;

CREATE TABLE music_script_declared_actions (
    installation_id TEXT NOT NULL,
    site_id TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    action TEXT NOT NULL,
    PRIMARY KEY (installation_id, site_id, position),
    FOREIGN KEY (installation_id, site_id)
        REFERENCES music_script_declared_sites(installation_id, site_id) ON DELETE CASCADE
) STRICT;

CREATE TABLE music_script_declared_qualities (
    installation_id TEXT NOT NULL,
    site_id TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    quality TEXT NOT NULL,
    PRIMARY KEY (installation_id, site_id, position),
    FOREIGN KEY (installation_id, site_id)
        REFERENCES music_script_declared_sites(installation_id, site_id) ON DELETE CASCADE
) STRICT;

CREATE TABLE music_script_kv_entries (
    installation_id TEXT NOT NULL,
    site_id TEXT NOT NULL,
    entry_key TEXT NOT NULL,
    PRIMARY KEY (installation_id, site_id, entry_key)
) STRICT;

-- Third-party script declared extra capability fields (bounded typed value tree).
CREATE TABLE music_script_declared_nodes (
    installation_id TEXT NOT NULL,
    site_id TEXT NOT NULL,
    node_id INTEGER NOT NULL CHECK (node_id >= 0),
    parent_id INTEGER,
    prop_key TEXT,
    item_index INTEGER,
    kind TEXT NOT NULL CHECK (kind IN ('null', 'boolean', 'integer', 'real', 'number_text', 'text', 'array', 'object')),
    bool_value INTEGER CHECK (bool_value IN (0, 1)),
    int_value INTEGER,
    real_value REAL,
    text_value TEXT,
    PRIMARY KEY (installation_id, site_id, node_id),
    FOREIGN KEY (installation_id, site_id)
        REFERENCES music_script_declared_sites(installation_id, site_id) ON DELETE CASCADE
) STRICT;

-- Third-party LX guest script per-key dynamic KV values (bounded typed value tree).
CREATE TABLE music_script_kv_nodes (
    installation_id TEXT NOT NULL,
    site_id TEXT NOT NULL,
    entry_key TEXT NOT NULL,
    node_id INTEGER NOT NULL CHECK (node_id >= 0),
    parent_id INTEGER,
    prop_key TEXT,
    item_index INTEGER,
    kind TEXT NOT NULL CHECK (kind IN ('null', 'boolean', 'integer', 'real', 'number_text', 'text', 'array', 'object')),
    bool_value INTEGER CHECK (bool_value IN (0, 1)),
    int_value INTEGER,
    real_value REAL,
    text_value TEXT,
    PRIMARY KEY (installation_id, site_id, entry_key, node_id),
    FOREIGN KEY (installation_id, site_id, entry_key)
        REFERENCES music_script_kv_entries(installation_id, site_id, entry_key) ON DELETE CASCADE
) STRICT;

CREATE TABLE music_script_checks (
    installation_id TEXT NOT NULL REFERENCES music_script_installations(installation_id) ON DELETE CASCADE,
    site_id TEXT NOT NULL,
    state TEXT NOT NULL CHECK (state IN ('ok', 'failed', 'unsupported')),
    quality TEXT,
    sample TEXT,
    code TEXT,
    PRIMARY KEY (installation_id, site_id)
) STRICT;
