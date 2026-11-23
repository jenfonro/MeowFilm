-- Shared native App device playback volume (Windows & iOS).
CREATE TABLE playback_volume (
    owner TEXT PRIMARY KEY NOT NULL,
    revision INTEGER NOT NULL DEFAULT 0 CHECK (revision BETWEEN 0 AND 9007199254740991),
    volume REAL NOT NULL CHECK (volume BETWEEN 0 AND 1)
) STRICT;
