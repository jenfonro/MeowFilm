-- Windows-owned native novel source configurations (pushed to source-runtime via library.apply).
CREATE TABLE windows_novel_sources (
    installation_id TEXT PRIMARY KEY NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    revision INTEGER NOT NULL CHECK (revision >= 1),
    name TEXT NOT NULL,
    source_url TEXT NOT NULL,
    source_group TEXT NOT NULL DEFAULT '',
    sort_order INTEGER NOT NULL DEFAULT 0,
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
    enabled_explore INTEGER NOT NULL CHECK (enabled_explore IN (0, 1)),
    protocol TEXT NOT NULL
) STRICT;

CREATE INDEX idx_windows_novel_sources_order ON windows_novel_sources(position);

-- Bounded third-party novel source definition value tree.
CREATE TABLE windows_novel_source_definition_nodes (
    installation_id TEXT NOT NULL REFERENCES windows_novel_sources(installation_id) ON DELETE CASCADE,
    node_id INTEGER NOT NULL CHECK (node_id >= 0),
    parent_id INTEGER,
    prop_key TEXT,
    item_index INTEGER,
    kind TEXT NOT NULL CHECK (kind IN ('null', 'boolean', 'integer', 'real', 'number_text', 'text', 'array', 'object')),
    bool_value INTEGER CHECK (bool_value IN (0, 1)),
    int_value INTEGER,
    real_value REAL,
    text_value TEXT,
    PRIMARY KEY (installation_id, node_id)
) STRICT;
