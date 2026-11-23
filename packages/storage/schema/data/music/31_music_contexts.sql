-- Normalized content-addressed playback context shared across queue entries, history entries/days, and similar return queues.
-- Platform-specific context fields live in windows_music_play_context_extras / ios_music_play_context_extras (1:1 CASCADE).
CREATE TABLE music_play_contexts (
    context_id TEXT PRIMARY KEY NOT NULL CHECK (length(context_id) = 64),
    source_title TEXT,
    playlist TEXT,
    source_type TEXT,
    source_id TEXT,
    search_keyword TEXT,
    daily_branch TEXT
) STRICT;
