-- iOS-specific novel shelf, reading progress, dynamic source variables, and reading settings.
CREATE TABLE ios_novel_books (
    book_id TEXT PRIMARY KEY NOT NULL,
    installation_id TEXT NOT NULL,
    book_url TEXT NOT NULL,
    identity TEXT NOT NULL,
    name TEXT NOT NULL,
    author TEXT NOT NULL,
    toc_url TEXT NOT NULL,
    cover_url TEXT NOT NULL,
    intro TEXT NOT NULL,
    latest_chapter_title TEXT NOT NULL,
    on_shelf INTEGER NOT NULL CHECK (on_shelf IN (0, 1)),
    pinned_at REAL,
    added_at REAL NOT NULL,
    row_revision INTEGER NOT NULL CHECK (row_revision BETWEEN 1 AND 9007199254740991),
    UNIQUE (installation_id, book_url)
) STRICT;

CREATE UNIQUE INDEX idx_ios_novel_book_identity ON ios_novel_books(identity) WHERE identity <> '';
CREATE INDEX idx_ios_novel_shelf ON ios_novel_books(on_shelf, pinned_at DESC, added_at DESC, book_id);

CREATE TABLE ios_novel_progress (
    book_id TEXT PRIMARY KEY NOT NULL REFERENCES ios_novel_books(book_id) ON DELETE CASCADE,
    chapter_url TEXT NOT NULL,
    chapter_title TEXT NOT NULL,
    chapter_index INTEGER NOT NULL CHECK (chapter_index >= 0),
    block_index INTEGER NOT NULL CHECK (block_index >= 0),
    utf16_offset INTEGER NOT NULL CHECK (utf16_offset >= 0),
    ratio REAL NOT NULL CHECK (ratio BETWEEN 0 AND 1),
    read_at REAL NOT NULL,
    row_revision INTEGER NOT NULL CHECK (row_revision BETWEEN 1 AND 9007199254740991)
) STRICT;

CREATE INDEX idx_ios_novel_history ON ios_novel_progress(read_at DESC, book_id);

-- Third-party book source dynamic variable tree.
CREATE TABLE ios_novel_variable_nodes (
    book_id TEXT NOT NULL REFERENCES ios_novel_books(book_id) ON DELETE CASCADE,
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

CREATE TABLE ios_novel_reading_settings (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    mode TEXT NOT NULL,
    font_size REAL NOT NULL,
    font_weight REAL NOT NULL,
    font_name TEXT NOT NULL,
    line_spacing REAL NOT NULL,
    paragraph_spacing REAL NOT NULL,
    margin REAL NOT NULL,
    content_width REAL NOT NULL,
    conversion TEXT NOT NULL,
    theme INTEGER NOT NULL,
    foreground TEXT NOT NULL,
    background TEXT NOT NULL,
    background_image BLOB,
    automatic_speed REAL NOT NULL,
    row_revision INTEGER NOT NULL CHECK (row_revision BETWEEN 1 AND 9007199254740991)
) STRICT;
