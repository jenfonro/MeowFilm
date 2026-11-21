-- Standalone TMDB and Douban settings (plaintext typed credentials).
CREATE TABLE metadata_standalone_tmdb (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    api_base TEXT NOT NULL,
    image_base TEXT NOT NULL,
    api_key TEXT NOT NULL,
    access_token TEXT NOT NULL,
    language TEXT NOT NULL,
    region TEXT NOT NULL,
    include_adult INTEGER NOT NULL CHECK (include_adult IN (0, 1)),
    row_revision INTEGER NOT NULL CHECK (row_revision BETWEEN 1 AND 9007199254740991)
) STRICT;

CREATE TABLE metadata_standalone_douban (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    api_base TEXT NOT NULL,
    search_base TEXT NOT NULL,
    image_base TEXT NOT NULL,
    api_proxy TEXT NOT NULL,
    image_proxy TEXT NOT NULL,
    cookie TEXT NOT NULL,
    row_revision INTEGER NOT NULL CHECK (row_revision >= 1)
) STRICT;
