CREATE TABLE ios_music_audio_files (
    cache_key TEXT PRIMARY KEY NOT NULL CHECK(length(cache_key)=64), file TEXT NOT NULL UNIQUE,
    quality TEXT NOT NULL, last_played_at REAL NOT NULL, license_expires_at REAL, expires_at REAL NOT NULL,
    scope TEXT NOT NULL, song_id TEXT NOT NULL, source_key TEXT NOT NULL, platform TEXT NOT NULL,
    native_id TEXT NOT NULL, requested_quality TEXT NOT NULL, provider TEXT NOT NULL,
    generation INTEGER NOT NULL CHECK(generation>0),
    stamp_device INTEGER NOT NULL, stamp_inode TEXT NOT NULL, stamp_size INTEGER NOT NULL CHECK(stamp_size>0),
    stamp_seconds INTEGER NOT NULL, stamp_nanos INTEGER NOT NULL,
    container TEXT NOT NULL, codec TEXT NOT NULL, duration REAL NOT NULL CHECK(duration>0),
    decoded_frames INTEGER NOT NULL CHECK(decoded_frames>0), sample_rate REAL NOT NULL CHECK(sample_rate>0),
    channels INTEGER NOT NULL CHECK(channels>0), bitrate REAL NOT NULL CHECK(bitrate>0), bit_depth INTEGER,
    lossless INTEGER NOT NULL CHECK(lossless IN (0,1)), complete INTEGER NOT NULL CHECK(complete IN (0,1)),
    protected INTEGER NOT NULL CHECK(protected IN (0,1))
) STRICT;
CREATE INDEX ios_music_audio_files_song ON ios_music_audio_files(scope,song_id,last_played_at);
CREATE INDEX ios_music_audio_files_age ON ios_music_audio_files(expires_at);
