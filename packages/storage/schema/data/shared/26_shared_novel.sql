-- Shared native App novel bookshelf, reading progress, dynamic source variables, reading settings, and replacement rules (Windows & iOS).
CREATE TABLE novel_books (
    book_id TEXT PRIMARY KEY NOT NULL,
    position INTEGER NOT NULL DEFAULT 0 CHECK (position >= 0),
    installation_id TEXT NOT NULL,
    book_url TEXT NOT NULL,
    identity_key TEXT NOT NULL DEFAULT '',
    name TEXT NOT NULL,
    author TEXT NOT NULL DEFAULT '',
    toc_url TEXT NOT NULL DEFAULT '',
    cover_url TEXT NOT NULL DEFAULT '',
    intro TEXT NOT NULL DEFAULT '',
    latest_chapter_title TEXT NOT NULL DEFAULT '',
    on_shelf INTEGER NOT NULL CHECK (on_shelf IN (0, 1)),
    pinned_at_ms INTEGER,
    added_at_ms INTEGER NOT NULL DEFAULT 0,
    chapter_count INTEGER NOT NULL DEFAULT 0 CHECK (chapter_count >= 0),
    content_signature TEXT NOT NULL DEFAULT '',
    book_revision INTEGER NOT NULL DEFAULT 0,
    row_revision INTEGER NOT NULL DEFAULT 1 CHECK (row_revision BETWEEN 1 AND 9007199254740991),
    UNIQUE (installation_id, book_url)
) STRICT;

CREATE UNIQUE INDEX idx_novel_books_identity ON novel_books(identity_key) WHERE identity_key <> '';
CREATE INDEX idx_novel_books_order ON novel_books(position);
CREATE INDEX idx_novel_books_shelf ON novel_books(on_shelf, pinned_at_ms DESC, added_at_ms DESC, book_id);

CREATE TABLE novel_progress (
    book_id TEXT PRIMARY KEY NOT NULL REFERENCES novel_books(book_id) ON DELETE CASCADE,
    chapter_url TEXT NOT NULL,
    chapter_title TEXT NOT NULL,
    chapter_index INTEGER NOT NULL CHECK (chapter_index >= 0),
    block_index INTEGER NOT NULL CHECK (block_index >= 0),
    utf16_offset INTEGER NOT NULL CHECK (utf16_offset >= 0),
    ratio REAL NOT NULL CHECK (ratio BETWEEN 0 AND 1),
    read_at_ms INTEGER NOT NULL,
    row_revision INTEGER NOT NULL DEFAULT 1 CHECK (row_revision BETWEEN 1 AND 9007199254740991)
) STRICT;

CREATE INDEX idx_novel_progress_history ON novel_progress(read_at_ms DESC, book_id);

-- Third-party book source dynamic variable / extra attribute tree.
CREATE TABLE novel_book_variables (
    book_id TEXT NOT NULL REFERENCES novel_books(book_id) ON DELETE CASCADE,
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

CREATE TABLE novel_reading_settings (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    mode TEXT NOT NULL,
    font_size REAL NOT NULL,
    font_weight REAL NOT NULL DEFAULT 400,
    font_family TEXT NOT NULL,
    line_spacing REAL NOT NULL,
    paragraph_spacing REAL NOT NULL,
    margin REAL NOT NULL,
    content_width REAL NOT NULL,
    conversion TEXT NOT NULL,
    theme INTEGER NOT NULL,
    foreground TEXT NOT NULL,
    background TEXT NOT NULL,
    background_image_path TEXT NOT NULL DEFAULT '',
    background_image_blob BLOB,
    auto_speed REAL NOT NULL,
    row_revision INTEGER NOT NULL DEFAULT 1 CHECK (row_revision BETWEEN 1 AND 9007199254740991),
    CHECK (background_image_path = '' OR background_image_blob IS NULL)
) STRICT;

CREATE TABLE novel_rules (
    rule_id TEXT PRIMARY KEY NOT NULL,
    position INTEGER NOT NULL DEFAULT 0 CHECK (position >= 0),
    name TEXT NOT NULL,
    rule_group TEXT NOT NULL DEFAULT '',
    pattern TEXT NOT NULL,
    replacement TEXT NOT NULL DEFAULT '',
    is_regex INTEGER NOT NULL CHECK (is_regex IN (0, 1)),
    is_enabled INTEGER NOT NULL CHECK (is_enabled IN (0, 1)),
    scope_title INTEGER NOT NULL CHECK (scope_title IN (0, 1)),
    scope_content INTEGER NOT NULL CHECK (scope_content IN (0, 1)),
    scope TEXT NOT NULL DEFAULT '',
    exclude_scope TEXT NOT NULL DEFAULT '',
    sort_order INTEGER NOT NULL DEFAULT 0,
    timeout_ms INTEGER NOT NULL DEFAULT 3000 CHECK (timeout_ms > 0)
) STRICT;

CREATE INDEX idx_novel_rules_order ON novel_rules(position);
