-- Shared native music library state, user collections, song membership, bindings, and intents.
CREATE TABLE music_library_state (
    scope TEXT PRIMARY KEY NOT NULL,
    revision INTEGER NOT NULL DEFAULT 0 CHECK (revision BETWEEN 0 AND 9007199254740991),
    owner_id TEXT NOT NULL DEFAULT 'device',
    library_revision INTEGER NOT NULL DEFAULT 0 CHECK (library_revision BETWEEN 0 AND 9007199254740991),
    next_id INTEGER NOT NULL DEFAULT 1 CHECK (next_id BETWEEN 1 AND 9007199254740991),
    status_phase TEXT NOT NULL DEFAULT 'idle',
    status_at REAL NOT NULL DEFAULT 0,
    status_code TEXT
) STRICT;

CREATE TABLE music_library_accounts (
    scope TEXT NOT NULL REFERENCES music_library_state(scope) ON DELETE CASCADE,
    platform TEXT NOT NULL,
    account_id TEXT NOT NULL,
    PRIMARY KEY (scope, platform)
) STRICT;

CREATE TABLE music_library_status_issues (
    scope TEXT NOT NULL REFERENCES music_library_state(scope) ON DELETE CASCADE,
    position INTEGER NOT NULL CHECK (position >= 0),
    platform TEXT,
    collection_id TEXT,
    code TEXT NOT NULL,
    PRIMARY KEY (scope, position)
) STRICT;

CREATE TABLE music_collections (
    scope TEXT NOT NULL REFERENCES music_library_state(scope) ON DELETE CASCADE,
    collection_id TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    name TEXT NOT NULL,
    kind TEXT NOT NULL,
    revision INTEGER NOT NULL DEFAULT 0 CHECK (revision BETWEEN 0 AND 9007199254740991),
    manual_order INTEGER NOT NULL DEFAULT 0 CHECK (manual_order IN (0, 1)),
    deleted INTEGER NOT NULL DEFAULT 0 CHECK (deleted IN (0, 1)),
    locally_created INTEGER NOT NULL DEFAULT 0 CHECK (locally_created IN (0, 1)),
    PRIMARY KEY (scope, collection_id)
) STRICT;

CREATE INDEX idx_music_collections_order ON music_collections(scope, position);

CREATE TABLE music_collection_songs (
    scope TEXT NOT NULL,
    collection_id TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    song_id TEXT NOT NULL REFERENCES music_songs(song_id),
    PRIMARY KEY (scope, collection_id, position),
    UNIQUE (scope, collection_id, song_id),
    FOREIGN KEY (scope, collection_id) REFERENCES music_collections(scope, collection_id) ON DELETE CASCADE
) STRICT;

CREATE INDEX idx_music_collection_songs_song ON music_collection_songs(song_id);

CREATE TABLE music_collection_bindings (
    scope TEXT NOT NULL,
    collection_id TEXT NOT NULL,
    binding_index INTEGER NOT NULL CHECK (binding_index >= 0),
    platform TEXT NOT NULL,
    account_id TEXT NOT NULL,
    remote_id TEXT NOT NULL,
    writable INTEGER NOT NULL DEFAULT 0 CHECK (writable IN (0, 1)),
    name TEXT,
    PRIMARY KEY (scope, collection_id, binding_index),
    FOREIGN KEY (scope, collection_id) REFERENCES music_collections(scope, collection_id) ON DELETE CASCADE
) STRICT;

CREATE TABLE music_collection_binding_baseline (
    scope TEXT NOT NULL,
    collection_id TEXT NOT NULL,
    binding_index INTEGER NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    native_id TEXT NOT NULL,
    PRIMARY KEY (scope, collection_id, binding_index, position),
    FOREIGN KEY (scope, collection_id, binding_index)
        REFERENCES music_collection_bindings(scope, collection_id, binding_index) ON DELETE CASCADE
) STRICT;

CREATE TABLE music_collection_binding_map (
    scope TEXT NOT NULL,
    collection_id TEXT NOT NULL,
    binding_index INTEGER NOT NULL,
    song_id TEXT NOT NULL REFERENCES music_songs(song_id),
    remote_entry_id TEXT NOT NULL,
    PRIMARY KEY (scope, collection_id, binding_index, song_id),
    FOREIGN KEY (scope, collection_id, binding_index)
        REFERENCES music_collection_bindings(scope, collection_id, binding_index) ON DELETE CASCADE
) STRICT;

CREATE TABLE music_collection_intents (
    scope TEXT NOT NULL,
    collection_id TEXT NOT NULL,
    intent_key TEXT NOT NULL,
    present INTEGER NOT NULL CHECK (present IN (0, 1)),
    pending INTEGER NOT NULL CHECK (pending IN (0, 1)),
    origin TEXT,
    PRIMARY KEY (scope, collection_id, intent_key),
    FOREIGN KEY (scope, collection_id) REFERENCES music_collections(scope, collection_id) ON DELETE CASCADE
) STRICT;

CREATE TABLE music_collection_intent_acks (
    scope TEXT NOT NULL,
    collection_id TEXT NOT NULL,
    intent_key TEXT NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    ack_value TEXT NOT NULL,
    PRIMARY KEY (scope, collection_id, intent_key, position),
    FOREIGN KEY (scope, collection_id, intent_key)
        REFERENCES music_collection_intents(scope, collection_id, intent_key) ON DELETE CASCADE
) STRICT;
