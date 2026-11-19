CREATE TABLE windows_pan_state (
    singleton INTEGER PRIMARY KEY CHECK(singleton=1),
    revision INTEGER NOT NULL CHECK(revision BETWEEN 0 AND 9007199254740991)
) STRICT;
INSERT INTO windows_pan_state VALUES(1,0);
CREATE TABLE windows_pan_accounts (
    provider TEXT PRIMARY KEY NOT NULL CHECK(provider IN ('baidu','quark','uc','tianyi','yidong')),
    account_revision INTEGER NOT NULL CHECK(account_revision BETWEEN 0 AND 9007199254740991),
    row_revision INTEGER NOT NULL CHECK(row_revision BETWEEN 0 AND 9007199254740991),
    cookie TEXT, username TEXT, password TEXT, authorization TEXT,
    refreshedAt INTEGER CHECK(refreshedAt BETWEEN 0 AND 9007199254740991),
    CHECK(provider='tianyi' OR (username IS NULL AND password IS NULL)),
    CHECK(provider='yidong' OR (authorization IS NULL AND refreshedAt IS NULL)),
    CHECK(provider<>'yidong' OR cookie IS NULL)
) STRICT;
INSERT INTO windows_pan_accounts(provider,account_revision,row_revision) VALUES
    ('baidu',0,0),('quark',0,0),('uc',0,0),('tianyi',0,0),('yidong',0,0);
