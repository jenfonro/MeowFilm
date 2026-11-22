-- Normalized content-addressed playback context shared across queue entries, history playlists/entries/days, and similar return queues.
CREATE TABLE music_play_contexts (
    context_id TEXT PRIMARY KEY NOT NULL CHECK (length(context_id) = 64),
    collection_id TEXT,
    playlist_id TEXT,
    source_title TEXT,
    playlist TEXT,
    source_type TEXT,
    source_id TEXT,
    source_platform TEXT,
    platform TEXT,
    source_label TEXT,
    search_keyword TEXT,
    daily_branch TEXT,
    heartbeat_playlist_id TEXT,
    heartbeat_account_id TEXT,
    provider TEXT,
    remote_session TEXT,
    queue_source_id TEXT,
    queue_source_title TEXT,
    queue_source_kind TEXT,
    queue_source_platform TEXT,
    similar_batch_id TEXT,
    similar_based_on_song_id TEXT REFERENCES music_songs(song_id),
    similar_provider TEXT
) STRICT;

CREATE INDEX idx_music_play_contexts_similar_song ON music_play_contexts(similar_based_on_song_id);
