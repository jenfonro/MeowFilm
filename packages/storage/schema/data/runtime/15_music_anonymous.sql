-- Music platform anonymous client protocol state (wy/kg/tx; owner CAS uses owner_revisions).
CREATE TABLE music_anonymous_wy_cookies (
    cookie_name TEXT PRIMARY KEY NOT NULL,
    cookie_value TEXT NOT NULL
) STRICT;

CREATE TABLE music_anonymous_kg (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    seed TEXT NOT NULL,
    mid TEXT NOT NULL,
    dfid TEXT
) STRICT;

CREATE TABLE music_anonymous_tx (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    installation_id TEXT NOT NULL,
    android_id TEXT NOT NULL,
    open_udid TEXT NOT NULL,
    open_udid2 TEXT NOT NULL,
    imei TEXT NOT NULL,
    qimei_q36 TEXT,
    qimei_saved_at_ms INTEGER,
    session_uid TEXT,
    session_sid TEXT,
    session_saved_at_ms INTEGER,
    CHECK ((qimei_q36 IS NULL AND qimei_saved_at_ms IS NULL) OR
      (qimei_q36 IS NOT NULL AND qimei_saved_at_ms IS NOT NULL AND qimei_saved_at_ms BETWEEN 0 AND 9007199254740991)),
    CHECK ((session_uid IS NULL AND session_sid IS NULL AND session_saved_at_ms IS NULL) OR
      (session_uid IS NOT NULL AND session_sid IS NOT NULL AND session_saved_at_ms IS NOT NULL AND session_saved_at_ms BETWEEN 0 AND 9007199254740991))
) STRICT;
