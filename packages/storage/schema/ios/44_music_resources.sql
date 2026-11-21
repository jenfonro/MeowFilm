-- Native iOS music resource caches, playback feedback outbox, lyrics, and audio file index.
CREATE TABLE ios_music_audio_urls (
    scope TEXT NOT NULL,
    platform TEXT NOT NULL,
    native_id TEXT NOT NULL,
    quality TEXT NOT NULL,
    url TEXT NOT NULL,
    full INTEGER CHECK (full IS NULL OR full IN (0, 1)),
    actual_quality TEXT,
    bitrate REAL,
    sample_rate REAL,
    duration REAL,
    expires_at REAL,
    license_expires_at REAL,
    saved_at REAL NOT NULL,
    PRIMARY KEY (scope, platform, native_id, quality)
) STRICT;

CREATE INDEX ios_music_audio_urls_age ON ios_music_audio_urls(saved_at);

CREATE TABLE ios_music_chorus (
    scope TEXT NOT NULL,
    platform TEXT NOT NULL,
    native_id TEXT NOT NULL,
    recording_duration REAL NOT NULL CHECK (recording_duration > 0),
    start REAL NOT NULL CHECK (start >= 0),
    end REAL NOT NULL CHECK (end >= start),
    expires_at REAL NOT NULL,
    PRIMARY KEY (scope, platform, native_id, recording_duration)
) STRICT;

CREATE TABLE ios_music_feedback_outbox (
    id TEXT PRIMARY KEY NOT NULL,
    instance TEXT NOT NULL,
    scope TEXT NOT NULL,
    generation INTEGER NOT NULL CHECK (generation >= 0),
    phase TEXT NOT NULL CHECK (phase IN ('start', 'end')),
    nativeID TEXT NOT NULL,
    source TEXT NOT NULL,
    sourceID TEXT NOT NULL,
    algorithm TEXT NOT NULL,
    heartbeat INTEGER NOT NULL CHECK (heartbeat IN (0, 1)),
    listened REAL NOT NULL CHECK (listened >= 0),
    media REAL NOT NULL CHECK (media >= 0),
    rate REAL NOT NULL CHECK (rate > 0),
    occurredAt REAL NOT NULL CHECK (occurredAt >= 0),
    reason TEXT,
    state TEXT NOT NULL CHECK (state IN ('pending', 'inflight')),
    startConfirmed INTEGER NOT NULL CHECK (startConfirmed IN (0, 1))
) STRICT;

CREATE INDEX ios_music_feedback_dispatch ON ios_music_feedback_outbox(scope, state, occurredAt);

CREATE TABLE ios_music_images (
    resource TEXT NOT NULL,
    filename TEXT PRIMARY KEY NOT NULL,
    updated_at REAL NOT NULL,
    bytes INTEGER NOT NULL CHECK (bytes > 0),
    ready INTEGER NOT NULL CHECK (ready IN (0, 1))
) STRICT;

CREATE UNIQUE INDEX ios_music_images_ready ON ios_music_images(resource) WHERE ready = 1;
CREATE INDEX ios_music_images_age ON ios_music_images(updated_at);

CREATE TABLE ios_music_lyrics (
    song_id TEXT PRIMARY KEY NOT NULL,
    source_key TEXT NOT NULL,
    updated_at REAL NOT NULL,
    bytes INTEGER NOT NULL CHECK (bytes > 0)
) STRICT;

CREATE TABLE ios_music_lyric_lines (
    song_id TEXT NOT NULL REFERENCES ios_music_lyrics(song_id) ON DELETE CASCADE,
    position INTEGER NOT NULL CHECK (position >= 0),
    line_index INTEGER NOT NULL,
    time REAL NOT NULL CHECK (time >= 0),
    end REAL NOT NULL CHECK (end >= time),
    text TEXT NOT NULL,
    translation TEXT NOT NULL,
    PRIMARY KEY (song_id, position)
) STRICT;

CREATE TABLE ios_music_lyric_words (
    song_id TEXT NOT NULL,
    line_position INTEGER NOT NULL,
    position INTEGER NOT NULL CHECK (position >= 0),
    time REAL NOT NULL,
    duration REAL NOT NULL CHECK (duration >= 0),
    text TEXT NOT NULL,
    PRIMARY KEY (song_id, line_position, position),
    FOREIGN KEY (song_id, line_position) REFERENCES ios_music_lyric_lines(song_id, position) ON DELETE CASCADE
) STRICT;

CREATE TABLE ios_music_audio_files (
    cache_key TEXT PRIMARY KEY NOT NULL CHECK (length(cache_key) = 64),
    file TEXT NOT NULL UNIQUE,
    quality TEXT NOT NULL,
    last_played_at REAL NOT NULL,
    license_expires_at REAL,
    expires_at REAL NOT NULL,
    scope TEXT NOT NULL,
    song_id TEXT NOT NULL,
    source_key TEXT NOT NULL,
    platform TEXT NOT NULL,
    native_id TEXT NOT NULL,
    requested_quality TEXT NOT NULL,
    provider TEXT NOT NULL,
    generation INTEGER NOT NULL CHECK (generation > 0),
    stamp_device INTEGER NOT NULL,
    stamp_inode TEXT NOT NULL,
    stamp_size INTEGER NOT NULL CHECK (stamp_size > 0),
    stamp_seconds INTEGER NOT NULL,
    stamp_nanos INTEGER NOT NULL,
    container TEXT NOT NULL,
    codec TEXT NOT NULL,
    duration REAL NOT NULL CHECK (duration > 0),
    decoded_frames INTEGER NOT NULL CHECK (decoded_frames > 0),
    sample_rate REAL NOT NULL CHECK (sample_rate > 0),
    channels INTEGER NOT NULL CHECK (channels > 0),
    bitrate REAL NOT NULL CHECK (bitrate > 0),
    bit_depth INTEGER,
    lossless INTEGER NOT NULL CHECK (lossless IN (0, 1)),
    complete INTEGER NOT NULL CHECK (complete IN (0, 1)),
    protected INTEGER NOT NULL CHECK (protected IN (0, 1))
) STRICT;

CREATE INDEX ios_music_audio_files_song ON ios_music_audio_files(scope, song_id, last_played_at);
CREATE INDEX ios_music_audio_files_age ON ios_music_audio_files(expires_at);
