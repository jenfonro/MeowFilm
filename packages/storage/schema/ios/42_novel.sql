-- Native iOS novel shelf, reading progress, dynamic source variables, reading settings, and replacement rules.
CREATE TABLE ios_novel_books (
    id TEXT PRIMARY KEY,
    installationId TEXT NOT NULL,
    bookUrl TEXT NOT NULL,
    identity TEXT NOT NULL,
    name TEXT NOT NULL,
    author TEXT NOT NULL,
    tocUrl TEXT NOT NULL,
    coverUrl TEXT NOT NULL,
    intro TEXT NOT NULL,
    latestChapterTitle TEXT NOT NULL,
    onShelf INTEGER NOT NULL CHECK (onShelf IN (0, 1)),
    pinnedAt REAL,
    addedAt REAL NOT NULL,
    row_revision INTEGER NOT NULL CHECK (row_revision BETWEEN 1 AND 9007199254740991),
    UNIQUE (installationId, bookUrl)
) STRICT;

CREATE UNIQUE INDEX ios_novel_book_identity ON ios_novel_books(identity) WHERE identity <> '';
CREATE INDEX ios_novel_shelf ON ios_novel_books(onShelf, pinnedAt DESC, addedAt DESC, id);

CREATE TABLE ios_novel_progress (
    book_id TEXT PRIMARY KEY REFERENCES ios_novel_books(id) ON DELETE CASCADE,
    chapterUrl TEXT NOT NULL,
    chapterTitle TEXT NOT NULL,
    chapterIndex INTEGER NOT NULL CHECK (chapterIndex >= 0),
    blockIndex INTEGER NOT NULL CHECK (blockIndex >= 0),
    utf16Offset INTEGER NOT NULL CHECK (utf16Offset >= 0),
    ratio REAL NOT NULL CHECK (ratio BETWEEN 0 AND 1),
    readAt REAL NOT NULL,
    row_revision INTEGER NOT NULL CHECK (row_revision BETWEEN 1 AND 9007199254740991)
) STRICT;

CREATE INDEX ios_novel_history ON ios_novel_progress(readAt DESC, book_id);

-- Third-party book source dynamic variable tree.
CREATE TABLE ios_novel_variable_nodes (
    book_id TEXT NOT NULL REFERENCES ios_novel_books(id) ON DELETE CASCADE,
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
    fontSize REAL NOT NULL,
    fontWeight REAL NOT NULL,
    fontName TEXT NOT NULL,
    lineSpacing REAL NOT NULL,
    paragraphSpacing REAL NOT NULL,
    margin REAL NOT NULL,
    contentWidth REAL NOT NULL,
    conversion TEXT NOT NULL,
    theme INTEGER NOT NULL,
    foreground TEXT NOT NULL,
    background TEXT NOT NULL,
    background_image BLOB,
    automaticSpeed REAL NOT NULL,
    row_revision INTEGER NOT NULL CHECK (row_revision BETWEEN 1 AND 9007199254740991)
) STRICT;

CREATE TABLE ios_novel_rules (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    "group" TEXT NOT NULL,
    isEnabled INTEGER NOT NULL CHECK (isEnabled IN (0, 1)),
    "order" INTEGER NOT NULL,
    timeoutMillisecond INTEGER NOT NULL CHECK (timeoutMillisecond > 0),
    pattern TEXT NOT NULL,
    replacement TEXT NOT NULL,
    isRegex INTEGER NOT NULL CHECK (isRegex IN (0, 1)),
    scope TEXT NOT NULL,
    excludeScope TEXT NOT NULL,
    scopeTitle INTEGER NOT NULL CHECK (scopeTitle IN (0, 1)),
    scopeContent INTEGER NOT NULL CHECK (scopeContent IN (0, 1))
) STRICT;
