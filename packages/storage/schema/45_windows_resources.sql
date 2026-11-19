CREATE TABLE windows_images (
    address TEXT PRIMARY KEY NOT NULL,
    file_id TEXT NOT NULL CHECK(length(file_id)=64),
    bytes INTEGER NOT NULL CHECK(bytes BETWEEN 1 AND 8388608),
    updated_at INTEGER NOT NULL CHECK(updated_at>0)
) STRICT;
CREATE INDEX windows_images_updated ON windows_images(updated_at);
CREATE INDEX windows_images_file ON windows_images(file_id);
CREATE INDEX windows_lyrics_updated ON windows_lyrics(updatedAt);
CREATE INDEX windows_playback_source_capacity ON windows_playback_source(account_scope,entry_kind,at);
CREATE INDEX windows_chorus_updated ON windows_chorus(at);
CREATE INDEX windows_audio_confirmed ON windows_audio_index(confirmedAt);
CREATE UNIQUE INDEX windows_audio_file_identity ON windows_audio_index(file);
CREATE INDEX windows_audio_song ON windows_audio_index_info(songId,kind);
