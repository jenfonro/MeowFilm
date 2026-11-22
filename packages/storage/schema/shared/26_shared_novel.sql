-- Shared native App novel replacement rules (Windows & iOS).
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
