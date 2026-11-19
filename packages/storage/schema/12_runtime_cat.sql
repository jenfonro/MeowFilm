-- Cat runtime gateway settings and installation facts
CREATE TABLE cat_gateway_settings (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    port INTEGER NOT NULL CHECK (port BETWEEN 0 AND 65535)
) STRICT;

CREATE TABLE cat_provider_entries (
    provider_id TEXT PRIMARY KEY NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    enabled INTEGER NOT NULL CHECK(enabled IN (0,1))
) STRICT;

CREATE TABLE cat_mappings (
    position INTEGER PRIMARY KEY CHECK (position >= 0),
    provider_id TEXT NOT NULL
) STRICT;

CREATE TABLE cat_mapping_aliases (
    position INTEGER NOT NULL REFERENCES cat_mappings(position) ON DELETE CASCADE,
    ordinal INTEGER NOT NULL CHECK(ordinal >= 0),
    alias TEXT NOT NULL,
    PRIMARY KEY (position, ordinal)
) STRICT;

CREATE TABLE cat_installations (
    mode TEXT NOT NULL CHECK (mode IN ('standalone', 'native')),
    installation_id TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    name TEXT,
    source_type TEXT CHECK (source_type IS NULL OR source_type IN ('network', 'local')),
    source_url TEXT,
    local_path TEXT,
    local_hash TEXT,
    enabled INTEGER CHECK (enabled IS NULL OR enabled IN (0, 1)),
    input_revision INTEGER,
    source_hash TEXT,
    version TEXT NOT NULL,
    loaded_identity TEXT,
    imported_at_ms INTEGER,
    updated_at_ms INTEGER,
    update_state TEXT NOT NULL,
    update_error TEXT,
    PRIMARY KEY (mode, installation_id)
) STRICT;

CREATE TABLE cat_installation_config_nodes (
    mode TEXT NOT NULL,
    installation_id TEXT NOT NULL,
    node_id INTEGER NOT NULL CHECK (node_id >= 0),
    parent_id INTEGER,
    prop_key TEXT,
    item_index INTEGER,
    kind TEXT NOT NULL CHECK (kind IN ('null', 'boolean', 'integer', 'real', 'number_text', 'text', 'array', 'object')),
    bool_value INTEGER CHECK (bool_value IN (0, 1)),
    int_value INTEGER,
    real_value REAL,
    text_value TEXT,
    PRIMARY KEY (mode, installation_id, node_id),
    FOREIGN KEY (mode, installation_id)
        REFERENCES cat_installations(mode, installation_id) ON DELETE CASCADE
) STRICT;

CREATE TABLE cat_installation_sites (
    mode TEXT NOT NULL,
    installation_id TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    site_key TEXT NOT NULL,
    site_name TEXT NOT NULL,
    site_api TEXT NOT NULL,
    discovery TEXT NOT NULL,
    PRIMARY KEY (mode, installation_id, site_key),
    UNIQUE (mode, installation_id, position),
    FOREIGN KEY (mode, installation_id)
        REFERENCES cat_installations(mode, installation_id) ON DELETE CASCADE
) STRICT;

CREATE TABLE cat_site_actions (
    mode TEXT NOT NULL,
    installation_id TEXT NOT NULL,
    site_key TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    action TEXT NOT NULL,
    PRIMARY KEY (mode, installation_id, site_key, position),
    FOREIGN KEY (mode, installation_id, site_key)
        REFERENCES cat_installation_sites(mode, installation_id, site_key) ON DELETE CASCADE
) STRICT;

CREATE TABLE cat_site_methods (
    mode TEXT NOT NULL,
    installation_id TEXT NOT NULL,
    site_key TEXT NOT NULL,
    method_key TEXT NOT NULL,
    method_name TEXT NOT NULL,
    PRIMARY KEY (mode, installation_id, site_key, method_key),
    FOREIGN KEY (mode, installation_id, site_key)
        REFERENCES cat_installation_sites(mode, installation_id, site_key) ON DELETE CASCADE
) STRICT;
