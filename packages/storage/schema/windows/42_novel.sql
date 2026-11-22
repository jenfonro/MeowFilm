-- Windows-specific novel shelf, reading progress, reader appearance, and source configuration (revisions use owner_revisions).
CREATE TABLE windows_novel_entries (
    book_id TEXT PRIMARY KEY NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    identity_key TEXT NOT NULL,
    on_shelf INTEGER NOT NULL CHECK (on_shelf IN (0, 1)),
    added_at TEXT,
    pinned_at TEXT,
    read_at TEXT,
    chapter_count INTEGER NOT NULL DEFAULT 0 CHECK (chapter_count >= 0),
    content_signature TEXT NOT NULL DEFAULT '',
    installation_id TEXT NOT NULL,
    book_revision INTEGER NOT NULL DEFAULT 0,
    book_url TEXT NOT NULL,
    name TEXT NOT NULL,
    author TEXT NOT NULL DEFAULT '',
    cover_url TEXT NOT NULL DEFAULT '',
    intro TEXT NOT NULL DEFAULT '',
    toc_url TEXT NOT NULL DEFAULT '',
    latest_chapter_title TEXT NOT NULL DEFAULT '',
    anchor_chapter_url TEXT,
    anchor_chapter_title TEXT,
    anchor_chapter_index INTEGER,
    anchor_block_index INTEGER,
    anchor_utf16_offset INTEGER,
    anchor_ratio REAL
) STRICT;

CREATE INDEX idx_windows_novel_entries_order ON windows_novel_entries(position);
CREATE INDEX idx_windows_novel_entries_shelf ON windows_novel_entries(on_shelf, pinned_at DESC, added_at DESC);
CREATE INDEX idx_windows_novel_entries_history ON windows_novel_entries(read_at DESC);

-- Bounded third-party source dynamic book fields (e.g. variable / rule-specific attributes).
CREATE TABLE windows_novel_book_extras (
    book_id TEXT NOT NULL REFERENCES windows_novel_entries(book_id) ON DELETE CASCADE,
    node_id INTEGER NOT NULL CHECK (node_id >= 0),
    parent_id INTEGER,
    prop_key TEXT,
    item_index INTEGER,
    kind TEXT NOT NULL CHECK (kind IN ('null', 'boolean', 'integer', 'real', 'number_text', 'text', 'array', 'object')),
    bool_value INTEGER CHECK (bool_value IN (0, 1)),
    int_value INTEGER,
    real_value REAL,
    text_value TEXT,
    PRIMARY KEY (book_id, node_id)
) STRICT;

CREATE TABLE windows_novel_appearance (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    mode TEXT NOT NULL,
    font_size REAL NOT NULL,
    font_family TEXT NOT NULL,
    bold INTEGER NOT NULL CHECK (bold IN (0, 1)),
    line_height REAL NOT NULL,
    paragraph_spacing REAL NOT NULL,
    margin REAL NOT NULL,
    content_width REAL NOT NULL,
    conversion TEXT NOT NULL,
    theme INTEGER NOT NULL,
    background TEXT NOT NULL,
    foreground TEXT NOT NULL,
    background_image TEXT NOT NULL DEFAULT '',
    auto_speed REAL NOT NULL
) STRICT;

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
