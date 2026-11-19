CREATE TABLE storage_identity (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    schema_hash TEXT NOT NULL CHECK (length(schema_hash) = 64)
) STRICT;

CREATE TABLE owner_revisions (
    owner TEXT PRIMARY KEY NOT NULL,
    revision INTEGER NOT NULL CHECK (revision >= 1),
    receipt TEXT NOT NULL
) STRICT;

CREATE TABLE operation_receipts (
    owner TEXT NOT NULL REFERENCES owner_revisions(owner) ON DELETE CASCADE,
    operation_id TEXT NOT NULL,
    revision INTEGER NOT NULL CHECK (revision >= 1),
    PRIMARY KEY (owner, operation_id)
) STRICT;
