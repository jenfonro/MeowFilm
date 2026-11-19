-- Generated closed columns/relations; no runtime payload columns.

CREATE TABLE music_owner_state (scope TEXT PRIMARY KEY NOT NULL, revision INTEGER NOT NULL CHECK(revision BETWEEN 0 AND 9007199254740991), receipt TEXT NOT NULL) STRICT;

CREATE TABLE "music_songs" (
    "song_id" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 2047),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "title" TEXT,
    "artist" TEXT,
    "album" TEXT,
    "duration_kind" TEXT CHECK("duration_kind" IN ('text','number')),
    "duration_value" TEXT,
    "baselineDuration_kind" TEXT CHECK("baselineDuration_kind" IN ('text','number')),
    "baselineDuration_value" TEXT,
    "cover_url" TEXT,
    "localFileId" TEXT,
    "artworkSourceKey" TEXT,
    "artworkSourceImage" TEXT,
    lookup_key TEXT,
    search_query TEXT,
    fingerprint TEXT,
    baseline_duration REAL CHECK(baseline_duration>0),
    CHECK(("title" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("artist" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("album" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("duration_kind" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("duration_kind" IS NULL)=("duration_value" IS NULL)),
    CHECK(("baselineDuration_kind" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("baselineDuration_kind" IS NULL)=("baselineDuration_value" IS NULL)),
    CHECK(("cover_url" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("localFileId" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("artworkSourceKey" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("artworkSourceImage" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    PRIMARY KEY("song_id")
) STRICT;

CREATE TABLE "music_songs_extensions" (
    "song_id" TEXT NOT NULL,
    node_id INTEGER NOT NULL CHECK(node_id>=0),
    parent_id INTEGER,
    prop_key TEXT,
    item_index INTEGER,
    kind TEXT NOT NULL CHECK(kind IN ('null','boolean','integer','real','number_text','text','array','object')),
    bool_value INTEGER CHECK(bool_value IN (0,1)),
    int_value INTEGER,
    real_value REAL,
    text_value TEXT,
    PRIMARY KEY("song_id","node_id"),
    FOREIGN KEY("song_id") REFERENCES "music_songs"("song_id") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_songs_artistNames" (
    "song_id" TEXT NOT NULL,
    "artistNames_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("song_id","artistNames_key"),
    FOREIGN KEY("song_id") REFERENCES "music_songs"("song_id") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_song_sources" (
    "song_id" TEXT NOT NULL,
    "sources_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 3),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "source" TEXT,
    CHECK(("source" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("song_id","sources_key"),
    FOREIGN KEY("song_id") REFERENCES "music_songs"("song_id") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_song_sources_extensions" (
    "song_id" TEXT NOT NULL,
    "sources_key" TEXT NOT NULL,
    node_id INTEGER NOT NULL CHECK(node_id>=0),
    parent_id INTEGER,
    prop_key TEXT,
    item_index INTEGER,
    kind TEXT NOT NULL CHECK(kind IN ('null','boolean','integer','real','number_text','text','array','object')),
    bool_value INTEGER CHECK(bool_value IN (0,1)),
    int_value INTEGER,
    real_value REAL,
    text_value TEXT,
    PRIMARY KEY("song_id","sources_key","node_id"),
    FOREIGN KEY("song_id","sources_key") REFERENCES "music_song_sources"("song_id","sources_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_source_info" (
    "song_id" TEXT NOT NULL,
    "sources_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 4294967295),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "source" TEXT,
    "songmid_kind" TEXT CHECK("songmid_kind" IN ('text','number')),
    "songmid_value" TEXT,
    "mid_kind" TEXT CHECK("mid_kind" IN ('text','number')),
    "mid_value" TEXT,
    "id_kind" TEXT CHECK("id_kind" IN ('text','number')),
    "id_value" TEXT,
    "songId_kind" TEXT CHECK("songId_kind" IN ('text','number')),
    "songId_value" TEXT,
    "qqSongId_kind" TEXT CHECK("qqSongId_kind" IN ('text','number')),
    "qqSongId_value" TEXT,
    "songType_kind" TEXT CHECK("songType_kind" IN ('text','number')),
    "songType_value" TEXT,
    "type_kind" TEXT CHECK("type_kind" IN ('text','number')),
    "type_value" TEXT,
    "copyrightId_kind" TEXT CHECK("copyrightId_kind" IN ('text','number')),
    "copyrightId_value" TEXT,
    "strMediaMid_kind" TEXT CHECK("strMediaMid_kind" IN ('text','number')),
    "strMediaMid_value" TEXT,
    "hash" TEXT,
    "albumId_kind" TEXT CHECK("albumId_kind" IN ('text','number')),
    "albumId_value" TEXT,
    "album_id_kind" TEXT CHECK("album_id_kind" IN ('text','number')),
    "album_id_value" TEXT,
    "albumMid_kind" TEXT CHECK("albumMid_kind" IN ('text','number')),
    "albumMid_value" TEXT,
    "albumAudioId_kind" TEXT CHECK("albumAudioId_kind" IN ('text','number')),
    "albumAudioId_value" TEXT,
    "mixsongid_kind" TEXT CHECK("mixsongid_kind" IN ('text','number')),
    "mixsongid_value" TEXT,
    "fileid_kind" TEXT CHECK("fileid_kind" IN ('text','number')),
    "fileid_value" TEXT,
    "fileId_camel_kind" TEXT CHECK("fileId_camel_kind" IN ('text','number')),
    "fileId_camel_value" TEXT,
    "name" TEXT,
    "singer" TEXT,
    "albumName" TEXT,
    "duration_kind" TEXT CHECK("duration_kind" IN ('text','number')),
    "duration_value" TEXT,
    "size_kind" TEXT CHECK("size_kind" IN ('text','number')),
    "size_value" TEXT,
    "contentId_kind" TEXT CHECK("contentId_kind" IN ('text','number')),
    "contentId_value" TEXT,
    "md5" TEXT,
    "img" TEXT,
    "qualityKnown" INTEGER CHECK("qualityKnown" IN (0,1)),
    CHECK(("source" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("songmid_kind" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("songmid_kind" IS NULL)=("songmid_value" IS NULL)),
    CHECK(("mid_kind" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("mid_kind" IS NULL)=("mid_value" IS NULL)),
    CHECK(("id_kind" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("id_kind" IS NULL)=("id_value" IS NULL)),
    CHECK(("songId_kind" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("songId_kind" IS NULL)=("songId_value" IS NULL)),
    CHECK(("qqSongId_kind" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("qqSongId_kind" IS NULL)=("qqSongId_value" IS NULL)),
    CHECK(("songType_kind" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("songType_kind" IS NULL)=("songType_value" IS NULL)),
    CHECK(("type_kind" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("type_kind" IS NULL)=("type_value" IS NULL)),
    CHECK(("copyrightId_kind" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("copyrightId_kind" IS NULL)=("copyrightId_value" IS NULL)),
    CHECK(("strMediaMid_kind" IS NULL)=((shape_mask & 512)=0 OR (null_mask & 512)<>0)),
    CHECK(("strMediaMid_kind" IS NULL)=("strMediaMid_value" IS NULL)),
    CHECK(("hash" IS NULL)=((shape_mask & 1024)=0 OR (null_mask & 1024)<>0)),
    CHECK(("albumId_kind" IS NULL)=((shape_mask & 2048)=0 OR (null_mask & 2048)<>0)),
    CHECK(("albumId_kind" IS NULL)=("albumId_value" IS NULL)),
    CHECK(("album_id_kind" IS NULL)=((shape_mask & 4096)=0 OR (null_mask & 4096)<>0)),
    CHECK(("album_id_kind" IS NULL)=("album_id_value" IS NULL)),
    CHECK(("albumMid_kind" IS NULL)=((shape_mask & 8192)=0 OR (null_mask & 8192)<>0)),
    CHECK(("albumMid_kind" IS NULL)=("albumMid_value" IS NULL)),
    CHECK(("albumAudioId_kind" IS NULL)=((shape_mask & 16384)=0 OR (null_mask & 16384)<>0)),
    CHECK(("albumAudioId_kind" IS NULL)=("albumAudioId_value" IS NULL)),
    CHECK(("mixsongid_kind" IS NULL)=((shape_mask & 32768)=0 OR (null_mask & 32768)<>0)),
    CHECK(("mixsongid_kind" IS NULL)=("mixsongid_value" IS NULL)),
    CHECK(("fileid_kind" IS NULL)=((shape_mask & 65536)=0 OR (null_mask & 65536)<>0)),
    CHECK(("fileid_kind" IS NULL)=("fileid_value" IS NULL)),
    CHECK(("fileId_camel_kind" IS NULL)=((shape_mask & 131072)=0 OR (null_mask & 131072)<>0)),
    CHECK(("fileId_camel_kind" IS NULL)=("fileId_camel_value" IS NULL)),
    CHECK(("name" IS NULL)=((shape_mask & 262144)=0 OR (null_mask & 262144)<>0)),
    CHECK(("singer" IS NULL)=((shape_mask & 524288)=0 OR (null_mask & 524288)<>0)),
    CHECK(("albumName" IS NULL)=((shape_mask & 1048576)=0 OR (null_mask & 1048576)<>0)),
    CHECK(("duration_kind" IS NULL)=((shape_mask & 2097152)=0 OR (null_mask & 2097152)<>0)),
    CHECK(("duration_kind" IS NULL)=("duration_value" IS NULL)),
    CHECK(("size_kind" IS NULL)=((shape_mask & 4194304)=0 OR (null_mask & 4194304)<>0)),
    CHECK(("size_kind" IS NULL)=("size_value" IS NULL)),
    CHECK(("contentId_kind" IS NULL)=((shape_mask & 8388608)=0 OR (null_mask & 8388608)<>0)),
    CHECK(("contentId_kind" IS NULL)=("contentId_value" IS NULL)),
    CHECK(("md5" IS NULL)=((shape_mask & 16777216)=0 OR (null_mask & 16777216)<>0)),
    CHECK(("img" IS NULL)=((shape_mask & 33554432)=0 OR (null_mask & 33554432)<>0)),
    CHECK(("qualityKnown" IS NULL)=((shape_mask & 67108864)=0 OR (null_mask & 67108864)<>0)),
    PRIMARY KEY("song_id","sources_key"),
    FOREIGN KEY("song_id","sources_key") REFERENCES "music_song_sources"("song_id","sources_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_source_info_extensions" (
    "song_id" TEXT NOT NULL,
    "sources_key" TEXT NOT NULL,
    node_id INTEGER NOT NULL CHECK(node_id>=0),
    parent_id INTEGER,
    prop_key TEXT,
    item_index INTEGER,
    kind TEXT NOT NULL CHECK(kind IN ('null','boolean','integer','real','number_text','text','array','object')),
    bool_value INTEGER CHECK(bool_value IN (0,1)),
    int_value INTEGER,
    real_value REAL,
    text_value TEXT,
    PRIMARY KEY("song_id","sources_key","node_id"),
    FOREIGN KEY("song_id","sources_key") REFERENCES "music_source_info"("song_id","sources_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_source_info_artistNames" (
    "song_id" TEXT NOT NULL,
    "sources_key" TEXT NOT NULL,
    "artistNames_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("song_id","sources_key","artistNames_key"),
    FOREIGN KEY("song_id","sources_key") REFERENCES "music_source_info"("song_id","sources_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_source_info_types" (
    "song_id" TEXT NOT NULL,
    "sources_key" TEXT NOT NULL,
    "types_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "type_kind" TEXT CHECK("type_kind" IN ('text','number')),
    "type_value" TEXT,
    "hash" TEXT,
    "size_kind" TEXT CHECK("size_kind" IN ('text','number')),
    "size_value" TEXT,
    CHECK(("type_kind" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("type_kind" IS NULL)=("type_value" IS NULL)),
    CHECK(("hash" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("size_kind" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("size_kind" IS NULL)=("size_value" IS NULL)),
    PRIMARY KEY("song_id","sources_key","types_key"),
    FOREIGN KEY("song_id","sources_key") REFERENCES "music_source_info"("song_id","sources_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_source_info__types" (
    "song_id" TEXT NOT NULL,
    "sources_key" TEXT NOT NULL,
    "_types_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 3),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "hash" TEXT,
    "size_kind" TEXT CHECK("size_kind" IN ('text','number')),
    "size_value" TEXT,
    CHECK(("hash" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("size_kind" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("size_kind" IS NULL)=("size_value" IS NULL)),
    PRIMARY KEY("song_id","sources_key","_types_key"),
    FOREIGN KEY("song_id","sources_key") REFERENCES "music_source_info"("song_id","sources_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_source_info_audioFormats" (
    "song_id" TEXT NOT NULL,
    "sources_key" TEXT NOT NULL,
    "audioFormats_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 33554431),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "formatType_kind" TEXT CHECK("formatType_kind" IN ('text','number')),
    "formatType_value" TEXT,
    "resourceType_kind" TEXT CHECK("resourceType_kind" IN ('text','number')),
    "resourceType_value" TEXT,
    "size_kind" TEXT CHECK("size_kind" IN ('text','number')),
    "size_value" TEXT,
    "androidSize_kind" TEXT CHECK("androidSize_kind" IN ('text','number')),
    "androidSize_value" TEXT,
    "iosSize_kind" TEXT CHECK("iosSize_kind" IN ('text','number')),
    "iosSize_value" TEXT,
    "asize_kind" TEXT CHECK("asize_kind" IN ('text','number')),
    "asize_value" TEXT,
    "isize_kind" TEXT CHECK("isize_kind" IN ('text','number')),
    "isize_value" TEXT,
    "format_kind" TEXT CHECK("format_kind" IN ('text','number')),
    "format_value" TEXT,
    "androidFormat_kind" TEXT CHECK("androidFormat_kind" IN ('text','number')),
    "androidFormat_value" TEXT,
    "iosFormat_kind" TEXT CHECK("iosFormat_kind" IN ('text','number')),
    "iosFormat_value" TEXT,
    "aformat_kind" TEXT CHECK("aformat_kind" IN ('text','number')),
    "aformat_value" TEXT,
    "iformat_kind" TEXT CHECK("iformat_kind" IN ('text','number')),
    "iformat_value" TEXT,
    "bitrate_kind" TEXT CHECK("bitrate_kind" IN ('text','number')),
    "bitrate_value" TEXT,
    "sampleRate_kind" TEXT CHECK("sampleRate_kind" IN ('text','number')),
    "sampleRate_value" TEXT,
    "bitDepth_kind" TEXT CHECK("bitDepth_kind" IN ('text','number')),
    "bitDepth_value" TEXT,
    "fileType_kind" TEXT CHECK("fileType_kind" IN ('text','number')),
    "fileType_value" TEXT,
    "androidFileType_kind" TEXT CHECK("androidFileType_kind" IN ('text','number')),
    "androidFileType_value" TEXT,
    "iosFileType_kind" TEXT CHECK("iosFileType_kind" IN ('text','number')),
    "iosFileType_value" TEXT,
    "iosAccuracyLevel_kind" TEXT CHECK("iosAccuracyLevel_kind" IN ('text','number')),
    "iosAccuracyLevel_value" TEXT,
    "androidAccuracyLevel_kind" TEXT CHECK("androidAccuracyLevel_kind" IN ('text','number')),
    "androidAccuracyLevel_value" TEXT,
    "hash" TEXT,
    "md5" TEXT,
    "androidMd5" TEXT,
    "fileId_kind" TEXT CHECK("fileId_kind" IN ('text','number')),
    "fileId_value" TEXT,
    "contentId_kind" TEXT CHECK("contentId_kind" IN ('text','number')),
    "contentId_value" TEXT,
    CHECK(("formatType_kind" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("formatType_kind" IS NULL)=("formatType_value" IS NULL)),
    CHECK(("resourceType_kind" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("resourceType_kind" IS NULL)=("resourceType_value" IS NULL)),
    CHECK(("size_kind" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("size_kind" IS NULL)=("size_value" IS NULL)),
    CHECK(("androidSize_kind" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("androidSize_kind" IS NULL)=("androidSize_value" IS NULL)),
    CHECK(("iosSize_kind" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("iosSize_kind" IS NULL)=("iosSize_value" IS NULL)),
    CHECK(("asize_kind" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("asize_kind" IS NULL)=("asize_value" IS NULL)),
    CHECK(("isize_kind" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("isize_kind" IS NULL)=("isize_value" IS NULL)),
    CHECK(("format_kind" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("format_kind" IS NULL)=("format_value" IS NULL)),
    CHECK(("androidFormat_kind" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("androidFormat_kind" IS NULL)=("androidFormat_value" IS NULL)),
    CHECK(("iosFormat_kind" IS NULL)=((shape_mask & 512)=0 OR (null_mask & 512)<>0)),
    CHECK(("iosFormat_kind" IS NULL)=("iosFormat_value" IS NULL)),
    CHECK(("aformat_kind" IS NULL)=((shape_mask & 1024)=0 OR (null_mask & 1024)<>0)),
    CHECK(("aformat_kind" IS NULL)=("aformat_value" IS NULL)),
    CHECK(("iformat_kind" IS NULL)=((shape_mask & 2048)=0 OR (null_mask & 2048)<>0)),
    CHECK(("iformat_kind" IS NULL)=("iformat_value" IS NULL)),
    CHECK(("bitrate_kind" IS NULL)=((shape_mask & 4096)=0 OR (null_mask & 4096)<>0)),
    CHECK(("bitrate_kind" IS NULL)=("bitrate_value" IS NULL)),
    CHECK(("sampleRate_kind" IS NULL)=((shape_mask & 8192)=0 OR (null_mask & 8192)<>0)),
    CHECK(("sampleRate_kind" IS NULL)=("sampleRate_value" IS NULL)),
    CHECK(("bitDepth_kind" IS NULL)=((shape_mask & 16384)=0 OR (null_mask & 16384)<>0)),
    CHECK(("bitDepth_kind" IS NULL)=("bitDepth_value" IS NULL)),
    CHECK(("fileType_kind" IS NULL)=((shape_mask & 32768)=0 OR (null_mask & 32768)<>0)),
    CHECK(("fileType_kind" IS NULL)=("fileType_value" IS NULL)),
    CHECK(("androidFileType_kind" IS NULL)=((shape_mask & 65536)=0 OR (null_mask & 65536)<>0)),
    CHECK(("androidFileType_kind" IS NULL)=("androidFileType_value" IS NULL)),
    CHECK(("iosFileType_kind" IS NULL)=((shape_mask & 131072)=0 OR (null_mask & 131072)<>0)),
    CHECK(("iosFileType_kind" IS NULL)=("iosFileType_value" IS NULL)),
    CHECK(("iosAccuracyLevel_kind" IS NULL)=((shape_mask & 262144)=0 OR (null_mask & 262144)<>0)),
    CHECK(("iosAccuracyLevel_kind" IS NULL)=("iosAccuracyLevel_value" IS NULL)),
    CHECK(("androidAccuracyLevel_kind" IS NULL)=((shape_mask & 524288)=0 OR (null_mask & 524288)<>0)),
    CHECK(("androidAccuracyLevel_kind" IS NULL)=("androidAccuracyLevel_value" IS NULL)),
    CHECK(("hash" IS NULL)=((shape_mask & 1048576)=0 OR (null_mask & 1048576)<>0)),
    CHECK(("md5" IS NULL)=((shape_mask & 2097152)=0 OR (null_mask & 2097152)<>0)),
    CHECK(("androidMd5" IS NULL)=((shape_mask & 4194304)=0 OR (null_mask & 4194304)<>0)),
    CHECK(("fileId_kind" IS NULL)=((shape_mask & 8388608)=0 OR (null_mask & 8388608)<>0)),
    CHECK(("fileId_kind" IS NULL)=("fileId_value" IS NULL)),
    CHECK(("contentId_kind" IS NULL)=((shape_mask & 16777216)=0 OR (null_mask & 16777216)<>0)),
    CHECK(("contentId_kind" IS NULL)=("contentId_value" IS NULL)),
    PRIMARY KEY("song_id","sources_key","audioFormats_key"),
    FOREIGN KEY("song_id","sources_key") REFERENCES "music_source_info"("song_id","sources_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_source_info_newRateFormats" (
    "song_id" TEXT NOT NULL,
    "sources_key" TEXT NOT NULL,
    "newRateFormats_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 33554431),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "formatType_kind" TEXT CHECK("formatType_kind" IN ('text','number')),
    "formatType_value" TEXT,
    "resourceType_kind" TEXT CHECK("resourceType_kind" IN ('text','number')),
    "resourceType_value" TEXT,
    "size_kind" TEXT CHECK("size_kind" IN ('text','number')),
    "size_value" TEXT,
    "androidSize_kind" TEXT CHECK("androidSize_kind" IN ('text','number')),
    "androidSize_value" TEXT,
    "iosSize_kind" TEXT CHECK("iosSize_kind" IN ('text','number')),
    "iosSize_value" TEXT,
    "asize_kind" TEXT CHECK("asize_kind" IN ('text','number')),
    "asize_value" TEXT,
    "isize_kind" TEXT CHECK("isize_kind" IN ('text','number')),
    "isize_value" TEXT,
    "format_kind" TEXT CHECK("format_kind" IN ('text','number')),
    "format_value" TEXT,
    "androidFormat_kind" TEXT CHECK("androidFormat_kind" IN ('text','number')),
    "androidFormat_value" TEXT,
    "iosFormat_kind" TEXT CHECK("iosFormat_kind" IN ('text','number')),
    "iosFormat_value" TEXT,
    "aformat_kind" TEXT CHECK("aformat_kind" IN ('text','number')),
    "aformat_value" TEXT,
    "iformat_kind" TEXT CHECK("iformat_kind" IN ('text','number')),
    "iformat_value" TEXT,
    "bitrate_kind" TEXT CHECK("bitrate_kind" IN ('text','number')),
    "bitrate_value" TEXT,
    "sampleRate_kind" TEXT CHECK("sampleRate_kind" IN ('text','number')),
    "sampleRate_value" TEXT,
    "bitDepth_kind" TEXT CHECK("bitDepth_kind" IN ('text','number')),
    "bitDepth_value" TEXT,
    "fileType_kind" TEXT CHECK("fileType_kind" IN ('text','number')),
    "fileType_value" TEXT,
    "androidFileType_kind" TEXT CHECK("androidFileType_kind" IN ('text','number')),
    "androidFileType_value" TEXT,
    "iosFileType_kind" TEXT CHECK("iosFileType_kind" IN ('text','number')),
    "iosFileType_value" TEXT,
    "iosAccuracyLevel_kind" TEXT CHECK("iosAccuracyLevel_kind" IN ('text','number')),
    "iosAccuracyLevel_value" TEXT,
    "androidAccuracyLevel_kind" TEXT CHECK("androidAccuracyLevel_kind" IN ('text','number')),
    "androidAccuracyLevel_value" TEXT,
    "hash" TEXT,
    "md5" TEXT,
    "androidMd5" TEXT,
    "fileId_kind" TEXT CHECK("fileId_kind" IN ('text','number')),
    "fileId_value" TEXT,
    "contentId_kind" TEXT CHECK("contentId_kind" IN ('text','number')),
    "contentId_value" TEXT,
    CHECK(("formatType_kind" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("formatType_kind" IS NULL)=("formatType_value" IS NULL)),
    CHECK(("resourceType_kind" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("resourceType_kind" IS NULL)=("resourceType_value" IS NULL)),
    CHECK(("size_kind" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("size_kind" IS NULL)=("size_value" IS NULL)),
    CHECK(("androidSize_kind" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("androidSize_kind" IS NULL)=("androidSize_value" IS NULL)),
    CHECK(("iosSize_kind" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("iosSize_kind" IS NULL)=("iosSize_value" IS NULL)),
    CHECK(("asize_kind" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("asize_kind" IS NULL)=("asize_value" IS NULL)),
    CHECK(("isize_kind" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("isize_kind" IS NULL)=("isize_value" IS NULL)),
    CHECK(("format_kind" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("format_kind" IS NULL)=("format_value" IS NULL)),
    CHECK(("androidFormat_kind" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("androidFormat_kind" IS NULL)=("androidFormat_value" IS NULL)),
    CHECK(("iosFormat_kind" IS NULL)=((shape_mask & 512)=0 OR (null_mask & 512)<>0)),
    CHECK(("iosFormat_kind" IS NULL)=("iosFormat_value" IS NULL)),
    CHECK(("aformat_kind" IS NULL)=((shape_mask & 1024)=0 OR (null_mask & 1024)<>0)),
    CHECK(("aformat_kind" IS NULL)=("aformat_value" IS NULL)),
    CHECK(("iformat_kind" IS NULL)=((shape_mask & 2048)=0 OR (null_mask & 2048)<>0)),
    CHECK(("iformat_kind" IS NULL)=("iformat_value" IS NULL)),
    CHECK(("bitrate_kind" IS NULL)=((shape_mask & 4096)=0 OR (null_mask & 4096)<>0)),
    CHECK(("bitrate_kind" IS NULL)=("bitrate_value" IS NULL)),
    CHECK(("sampleRate_kind" IS NULL)=((shape_mask & 8192)=0 OR (null_mask & 8192)<>0)),
    CHECK(("sampleRate_kind" IS NULL)=("sampleRate_value" IS NULL)),
    CHECK(("bitDepth_kind" IS NULL)=((shape_mask & 16384)=0 OR (null_mask & 16384)<>0)),
    CHECK(("bitDepth_kind" IS NULL)=("bitDepth_value" IS NULL)),
    CHECK(("fileType_kind" IS NULL)=((shape_mask & 32768)=0 OR (null_mask & 32768)<>0)),
    CHECK(("fileType_kind" IS NULL)=("fileType_value" IS NULL)),
    CHECK(("androidFileType_kind" IS NULL)=((shape_mask & 65536)=0 OR (null_mask & 65536)<>0)),
    CHECK(("androidFileType_kind" IS NULL)=("androidFileType_value" IS NULL)),
    CHECK(("iosFileType_kind" IS NULL)=((shape_mask & 131072)=0 OR (null_mask & 131072)<>0)),
    CHECK(("iosFileType_kind" IS NULL)=("iosFileType_value" IS NULL)),
    CHECK(("iosAccuracyLevel_kind" IS NULL)=((shape_mask & 262144)=0 OR (null_mask & 262144)<>0)),
    CHECK(("iosAccuracyLevel_kind" IS NULL)=("iosAccuracyLevel_value" IS NULL)),
    CHECK(("androidAccuracyLevel_kind" IS NULL)=((shape_mask & 524288)=0 OR (null_mask & 524288)<>0)),
    CHECK(("androidAccuracyLevel_kind" IS NULL)=("androidAccuracyLevel_value" IS NULL)),
    CHECK(("hash" IS NULL)=((shape_mask & 1048576)=0 OR (null_mask & 1048576)<>0)),
    CHECK(("md5" IS NULL)=((shape_mask & 2097152)=0 OR (null_mask & 2097152)<>0)),
    CHECK(("androidMd5" IS NULL)=((shape_mask & 4194304)=0 OR (null_mask & 4194304)<>0)),
    CHECK(("fileId_kind" IS NULL)=((shape_mask & 8388608)=0 OR (null_mask & 8388608)<>0)),
    CHECK(("fileId_kind" IS NULL)=("fileId_value" IS NULL)),
    CHECK(("contentId_kind" IS NULL)=((shape_mask & 16777216)=0 OR (null_mask & 16777216)<>0)),
    CHECK(("contentId_kind" IS NULL)=("contentId_value" IS NULL)),
    PRIMARY KEY("song_id","sources_key","newRateFormats_key"),
    FOREIGN KEY("song_id","sources_key") REFERENCES "music_source_info"("song_id","sources_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_library" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 31),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "schema" INTEGER,
    "scope" TEXT,
    "revision" INTEGER,
    CHECK(("schema" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("scope" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("revision" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    PRIMARY KEY("scope_key")
) STRICT;

CREATE TABLE "music_library_library" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 511),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "version" INTEGER,
    "ownerId" TEXT,
    "revision" INTEGER,
    "nextId" INTEGER,
    CHECK(("version" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("ownerId" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("revision" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("nextId" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    PRIMARY KEY("scope_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_library"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_collections" (
    "scope_key" TEXT NOT NULL,
    "collections_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 511),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "name" TEXT,
    "kind" TEXT,
    "revision" INTEGER,
    "manualOrder" INTEGER CHECK("manualOrder" IN (0,1)),
    "deleted" INTEGER CHECK("deleted" IN (0,1)),
    "locallyCreated" INTEGER CHECK("locallyCreated" IN (0,1)),
    CHECK(("name" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("kind" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("revision" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("manualOrder" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("deleted" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("locallyCreated" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    PRIMARY KEY("scope_key","collections_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_library_library"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_collection_members" (
    "scope_key" TEXT NOT NULL,
    "collections_key" TEXT NOT NULL,
    "order_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    FOREIGN KEY(value) REFERENCES music_songs(song_id),
    PRIMARY KEY("scope_key","collections_key","order_key"),
    FOREIGN KEY("scope_key","collections_key") REFERENCES "music_collections"("scope_key","collections_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_collections_bindings" (
    "scope_key" TEXT NOT NULL,
    "collections_key" TEXT NOT NULL,
    "bindings_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 127),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "platform" TEXT,
    "accountId" TEXT,
    "id" TEXT,
    "writable" INTEGER CHECK("writable" IN (0,1)),
    "name" TEXT,
    CHECK(("platform" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("accountId" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("id" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("writable" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("name" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    PRIMARY KEY("scope_key","collections_key","bindings_key"),
    FOREIGN KEY("scope_key","collections_key") REFERENCES "music_collections"("scope_key","collections_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_collections_bindings_baseline" (
    "scope_key" TEXT NOT NULL,
    "collections_key" TEXT NOT NULL,
    "bindings_key" TEXT NOT NULL,
    "baseline_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","collections_key","bindings_key","baseline_key"),
    FOREIGN KEY("scope_key","collections_key","bindings_key") REFERENCES "music_collections_bindings"("scope_key","collections_key","bindings_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_collections_bindings_map" (
    "scope_key" TEXT NOT NULL,
    "collections_key" TEXT NOT NULL,
    "bindings_key" TEXT NOT NULL,
    "map_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    FOREIGN KEY(value) REFERENCES music_songs(song_id),
    PRIMARY KEY("scope_key","collections_key","bindings_key","map_key"),
    FOREIGN KEY("scope_key","collections_key","bindings_key") REFERENCES "music_collections_bindings"("scope_key","collections_key","bindings_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_collection_intents" (
    "scope_key" TEXT NOT NULL,
    "collections_key" TEXT NOT NULL,
    "intents_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 15),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "present" INTEGER CHECK("present" IN (0,1)),
    "pending" INTEGER CHECK("pending" IN (0,1)),
    "origin" TEXT,
    CHECK(("present" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("pending" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("origin" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    FOREIGN KEY(intents_key) REFERENCES music_songs(song_id),
    PRIMARY KEY("scope_key","collections_key","intents_key"),
    FOREIGN KEY("scope_key","collections_key") REFERENCES "music_collections"("scope_key","collections_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_collection_intents_acknowledged" (
    "scope_key" TEXT NOT NULL,
    "collections_key" TEXT NOT NULL,
    "intents_key" TEXT NOT NULL,
    "acknowledged_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","collections_key","intents_key","acknowledged_key"),
    FOREIGN KEY("scope_key","collections_key","intents_key") REFERENCES "music_collection_intents"("scope_key","collections_key","intents_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_library_library_accounts" (
    "scope_key" TEXT NOT NULL,
    "accounts_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","accounts_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_library_library"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_library_library_status" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 15),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "phase" TEXT,
    "at" REAL,
    "code" TEXT,
    CHECK(("phase" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("at" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("code" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    PRIMARY KEY("scope_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_library_library"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_library_library_status_issues" (
    "scope_key" TEXT NOT NULL,
    "issues_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 15),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "platform" TEXT,
    "collectionID" TEXT,
    "collectionId_camel" TEXT,
    "code" TEXT,
    CHECK(("platform" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("collectionID" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("collectionId_camel" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("code" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    PRIMARY KEY("scope_key","issues_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_library_library_status"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_library_library_networkReferences" (
    "scope_key" TEXT NOT NULL,
    "networkReferences_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 4095),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "platform" TEXT,
    "remoteID" TEXT,
    "remoteId_camel" TEXT,
    "name" TEXT,
    "creator" TEXT,
    "artwork" TEXT,
    "cover" TEXT,
    "count" INTEGER,
    "revision" INTEGER,
    "present" INTEGER CHECK("present" IN (0,1)),
    "pending" INTEGER CHECK("pending" IN (0,1)),
    CHECK(("platform" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("remoteID" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("remoteId_camel" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("name" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("creator" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("artwork" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("cover" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("count" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("revision" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("present" IS NULL)=((shape_mask & 512)=0 OR (null_mask & 512)<>0)),
    CHECK(("pending" IS NULL)=((shape_mask & 1024)=0 OR (null_mask & 1024)<>0)),
    PRIMARY KEY("scope_key","networkReferences_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_library_library"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_library_library_networkReferences_relation" (
    "scope_key" TEXT NOT NULL,
    "networkReferences_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 15),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "accountId" TEXT,
    "baseline" INTEGER CHECK("baseline" IN (0,1)),
    "relationID" TEXT,
    "locator" TEXT,
    CHECK(("accountId" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("baseline" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("relationID" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("locator" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    PRIMARY KEY("scope_key","networkReferences_key"),
    FOREIGN KEY("scope_key","networkReferences_key") REFERENCES "music_library_library_networkReferences"("scope_key","networkReferences_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_library_library_networkPlaylists" (
    "scope_key" TEXT NOT NULL,
    "networkPlaylists_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 4095),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "platform" TEXT,
    "remoteID" TEXT,
    "remoteId_camel" TEXT,
    "name" TEXT,
    "creator" TEXT,
    "artwork" TEXT,
    "cover" TEXT,
    "count" INTEGER,
    "revision" INTEGER,
    "present" INTEGER CHECK("present" IN (0,1)),
    "pending" INTEGER CHECK("pending" IN (0,1)),
    CHECK(("platform" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("remoteID" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("remoteId_camel" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("name" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("creator" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("artwork" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("cover" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("count" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("revision" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("present" IS NULL)=((shape_mask & 512)=0 OR (null_mask & 512)<>0)),
    CHECK(("pending" IS NULL)=((shape_mask & 1024)=0 OR (null_mask & 1024)<>0)),
    PRIMARY KEY("scope_key","networkPlaylists_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_library_library"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_library_library_networkPlaylists_relation" (
    "scope_key" TEXT NOT NULL,
    "networkPlaylists_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 15),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "accountId" TEXT,
    "baseline" INTEGER CHECK("baseline" IN (0,1)),
    "relationID" TEXT,
    "locator" TEXT,
    CHECK(("accountId" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("baseline" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("relationID" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("locator" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    PRIMARY KEY("scope_key","networkPlaylists_key"),
    FOREIGN KEY("scope_key","networkPlaylists_key") REFERENCES "music_library_library_networkPlaylists"("scope_key","networkPlaylists_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_library_sync" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 127),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "schema" INTEGER,
    "phase" TEXT,
    CHECK(("schema" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("phase" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    PRIMARY KEY("scope_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_library"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_library_sync_selections" (
    "scope_key" TEXT NOT NULL,
    "selections_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "platform" TEXT,
    "favorites" INTEGER CHECK("favorites" IN (0,1)),
    "playlists" INTEGER CHECK("playlists" IN (0,1)),
    CHECK(("platform" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("favorites" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("playlists" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    PRIMARY KEY("scope_key","selections_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_library_sync"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_library_sync_scopes" (
    "scope_key" TEXT NOT NULL,
    "scopes_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","scopes_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_library_sync"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_library_sync_issues" (
    "scope_key" TEXT NOT NULL,
    "issues_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "platform" TEXT,
    "collectionID" TEXT,
    "code" TEXT,
    CHECK(("platform" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("collectionID" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("code" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    PRIMARY KEY("scope_key","issues_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_library_sync"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_library_sync_automatic" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 127),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "revision" INTEGER,
    "intentRevision" INTEGER,
    "settledIntentRevision" INTEGER,
    "orderOnly" TEXT,
    CHECK(("revision" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("intentRevision" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("settledIntentRevision" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("orderOnly" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    PRIMARY KEY("scope_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_library_sync"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_library_sync_automatic_selections" (
    "scope_key" TEXT NOT NULL,
    "selections_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "platform" TEXT,
    "favorites" INTEGER CHECK("favorites" IN (0,1)),
    "playlists" INTEGER CHECK("playlists" IN (0,1)),
    CHECK(("platform" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("favorites" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("playlists" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    PRIMARY KEY("scope_key","selections_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_library_sync_automatic"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_library_sync_automatic_bindings" (
    "scope_key" TEXT NOT NULL,
    "bindings_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 3),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "accountID" TEXT,
    "scope" TEXT,
    CHECK(("accountID" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("scope" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    PRIMARY KEY("scope_key","bindings_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_library_sync_automatic"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_library_sync_automatic_relationshipPlatforms" (
    "scope_key" TEXT NOT NULL,
    "relationshipPlatforms_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","relationshipPlatforms_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_library_sync_automatic"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_library_sync_fences" (
    "scope_key" TEXT NOT NULL,
    "fences_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 255),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "platform" TEXT,
    "accountID" TEXT,
    "scope" TEXT,
    "localID" TEXT,
    "localRevision" INTEGER,
    "acknowledgedID" TEXT,
    "acknowledgedName" TEXT,
    CHECK(("platform" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("accountID" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("scope" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("localID" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("localRevision" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("acknowledgedID" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("acknowledgedName" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    PRIMARY KEY("scope_key","fences_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_library_sync"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_library_sync_fences_mutation" (
    "scope_key" TEXT NOT NULL,
    "fences_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 255),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "id" TEXT,
    "action" TEXT,
    "collectionID" TEXT,
    "name" TEXT,
    "directoryRevision" TEXT,
    CHECK(("id" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("action" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("collectionID" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("name" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("directoryRevision" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    PRIMARY KEY("scope_key","fences_key"),
    FOREIGN KEY("scope_key","fences_key") REFERENCES "music_library_sync_fences"("scope_key","fences_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_library_sync_fences_mutation_members" (
    "scope_key" TEXT NOT NULL,
    "fences_key" TEXT NOT NULL,
    "members_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 3),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "nativeID" TEXT,
    "occurrenceID" TEXT,
    CHECK(("nativeID" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("occurrenceID" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    PRIMARY KEY("scope_key","fences_key","members_key"),
    FOREIGN KEY("scope_key","fences_key") REFERENCES "music_library_sync_fences_mutation"("scope_key","fences_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_library_sync_fences_mutation_before" (
    "scope_key" TEXT NOT NULL,
    "fences_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 511),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "id" TEXT,
    "name" TEXT,
    "kind" TEXT,
    "writable" INTEGER CHECK("writable" IN (0,1)),
    "canRename" INTEGER CHECK("canRename" IN (0,1)),
    "canDelete" INTEGER CHECK("canDelete" IN (0,1)),
    "canOrder" INTEGER CHECK("canOrder" IN (0,1)),
    "revision" TEXT,
    CHECK(("id" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("name" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("kind" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("writable" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("canRename" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("canDelete" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("canOrder" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("revision" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    PRIMARY KEY("scope_key","fences_key"),
    FOREIGN KEY("scope_key","fences_key") REFERENCES "music_library_sync_fences_mutation"("scope_key","fences_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_library_sync_fences_mutation_before_members" (
    "scope_key" TEXT NOT NULL,
    "fences_key" TEXT NOT NULL,
    "members_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 3),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "nativeID" TEXT,
    "occurrenceID" TEXT,
    CHECK(("nativeID" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("occurrenceID" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    PRIMARY KEY("scope_key","fences_key","members_key"),
    FOREIGN KEY("scope_key","fences_key") REFERENCES "music_library_sync_fences_mutation_before"("scope_key","fences_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_library_sync_fences_mutation_beforeSubscription" (
    "scope_key" TEXT NOT NULL,
    "fences_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 63),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "remoteID" TEXT,
    "relationID" TEXT,
    "name" TEXT,
    "creator" TEXT,
    "artwork" TEXT,
    "count" INTEGER,
    CHECK(("remoteID" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("relationID" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("name" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("creator" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("artwork" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("count" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    PRIMARY KEY("scope_key","fences_key"),
    FOREIGN KEY("scope_key","fences_key") REFERENCES "music_library_sync_fences_mutation"("scope_key","fences_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_queue" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 2047),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "schema" INTEGER,
    "currentId" TEXT,
    "serial" INTEGER,
    "revision" INTEGER,
    "runId" TEXT,
    CHECK(("schema" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("currentId" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("serial" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("revision" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("runId" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    PRIMARY KEY("scope_key")
) STRICT;

CREATE TABLE "music_queue_entries" (
    "scope_key" TEXT NOT NULL,
    "entries_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 3),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "songId" TEXT,
    CHECK(("songId" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    FOREIGN KEY("songId") REFERENCES music_songs(song_id),
    PRIMARY KEY("scope_key","entries_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_queue"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_queue_entries_context" (
    "scope_key" TEXT NOT NULL,
    "entries_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 131071),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "collectionId" TEXT,
    "playlistId" TEXT,
    "sourceTitle" TEXT,
    "playlist" TEXT,
    "sourceType" TEXT,
    "sourceId" TEXT,
    "sourcePlatform" TEXT,
    "platform" TEXT,
    "sourceLabel" TEXT,
    "searchKeyword" TEXT,
    "dailyBranch" TEXT,
    "heartbeatPlaylistId" TEXT,
    "heartbeatAccountId" TEXT,
    "provider" TEXT,
    "remoteSession" TEXT,
    CHECK(("collectionId" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("playlistId" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("sourceTitle" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("playlist" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("sourceType" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("sourceId" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("sourcePlatform" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("platform" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("sourceLabel" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("searchKeyword" IS NULL)=((shape_mask & 512)=0 OR (null_mask & 512)<>0)),
    CHECK(("dailyBranch" IS NULL)=((shape_mask & 1024)=0 OR (null_mask & 1024)<>0)),
    CHECK(("heartbeatPlaylistId" IS NULL)=((shape_mask & 2048)=0 OR (null_mask & 2048)<>0)),
    CHECK(("heartbeatAccountId" IS NULL)=((shape_mask & 4096)=0 OR (null_mask & 4096)<>0)),
    CHECK(("provider" IS NULL)=((shape_mask & 8192)=0 OR (null_mask & 8192)<>0)),
    CHECK(("remoteSession" IS NULL)=((shape_mask & 16384)=0 OR (null_mask & 16384)<>0)),
    PRIMARY KEY("scope_key","entries_key"),
    FOREIGN KEY("scope_key","entries_key") REFERENCES "music_queue_entries"("scope_key","entries_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_queue_entries_context_queueSource" (
    "scope_key" TEXT NOT NULL,
    "entries_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 15),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "id" TEXT,
    "title" TEXT,
    "kind" TEXT,
    "platform" TEXT,
    CHECK(("id" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("title" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("kind" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("platform" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    PRIMARY KEY("scope_key","entries_key"),
    FOREIGN KEY("scope_key","entries_key") REFERENCES "music_queue_entries_context"("scope_key","entries_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_queue_entries_context_similar" (
    "scope_key" TEXT NOT NULL,
    "entries_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "batchId" TEXT,
    "basedOnSongId" TEXT,
    "provider" TEXT,
    CHECK(("batchId" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("basedOnSongId" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("provider" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    FOREIGN KEY("basedOnSongId") REFERENCES music_songs(song_id),
    PRIMARY KEY("scope_key","entries_key"),
    FOREIGN KEY("scope_key","entries_key") REFERENCES "music_queue_entries_context"("scope_key","entries_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_queue_priority" (
    "scope_key" TEXT NOT NULL,
    "priority_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","priority_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_queue"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_queue_history" (
    "scope_key" TEXT NOT NULL,
    "history_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","history_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_queue"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_queue_source" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 4095),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "id" TEXT,
    "title" TEXT,
    "kind" TEXT,
    "platform" TEXT,
    "sourceType" TEXT,
    "sourceTitle" TEXT,
    "sourceId" TEXT,
    "playlist" TEXT,
    "seedEntryId" TEXT,
    "remoteSession" TEXT,
    "dailyBranch" TEXT,
    "searchKeyword" TEXT,
    CHECK(("id" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("title" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("kind" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("platform" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("sourceType" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("sourceTitle" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("sourceId" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("playlist" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("seedEntryId" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("remoteSession" IS NULL)=((shape_mask & 512)=0 OR (null_mask & 512)<>0)),
    CHECK(("dailyBranch" IS NULL)=((shape_mask & 1024)=0 OR (null_mask & 1024)<>0)),
    CHECK(("searchKeyword" IS NULL)=((shape_mask & 2048)=0 OR (null_mask & 2048)<>0)),
    PRIMARY KEY("scope_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_queue"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_queue_failed" (
    "scope_key" TEXT NOT NULL,
    "failed_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","failed_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_queue"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_queue_similar" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 65535),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "schema" INTEGER,
    "seedSongId" TEXT,
    "seedTitle" TEXT,
    "seedEntryId" TEXT,
    "fromHome" INTEGER CHECK("fromHome" IN (0,1)),
    "provider" TEXT,
    "continue" INTEGER CHECK("continue" IN (0,1)),
    "offset" INTEGER,
    "exhausted" INTEGER CHECK("exhausted" IN (0,1)),
    "scope" TEXT,
    "status" TEXT,
    CHECK(("schema" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("seedSongId" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("seedTitle" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("seedEntryId" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("fromHome" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("provider" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("continue" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("offset" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("exhausted" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("scope" IS NULL)=((shape_mask & 512)=0 OR (null_mask & 512)<>0)),
    CHECK(("status" IS NULL)=((shape_mask & 1024)=0 OR (null_mask & 1024)<>0)),
    FOREIGN KEY("seedSongId") REFERENCES music_songs(song_id),
    PRIMARY KEY("scope_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_queue"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_queue_similar_returnQueue" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 31),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "currentId" TEXT,
    CHECK(("currentId" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_queue_similar"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_queue_similar_returnQueue_entries" (
    "scope_key" TEXT NOT NULL,
    "entries_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 3),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "songId" TEXT,
    CHECK(("songId" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    FOREIGN KEY("songId") REFERENCES music_songs(song_id),
    PRIMARY KEY("scope_key","entries_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_queue_similar_returnQueue"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_queue_similar_returnQueue_entries_context" (
    "scope_key" TEXT NOT NULL,
    "entries_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 131071),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "collectionId" TEXT,
    "playlistId" TEXT,
    "sourceTitle" TEXT,
    "playlist" TEXT,
    "sourceType" TEXT,
    "sourceId" TEXT,
    "sourcePlatform" TEXT,
    "platform" TEXT,
    "sourceLabel" TEXT,
    "searchKeyword" TEXT,
    "dailyBranch" TEXT,
    "heartbeatPlaylistId" TEXT,
    "heartbeatAccountId" TEXT,
    "provider" TEXT,
    "remoteSession" TEXT,
    CHECK(("collectionId" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("playlistId" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("sourceTitle" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("playlist" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("sourceType" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("sourceId" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("sourcePlatform" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("platform" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("sourceLabel" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("searchKeyword" IS NULL)=((shape_mask & 512)=0 OR (null_mask & 512)<>0)),
    CHECK(("dailyBranch" IS NULL)=((shape_mask & 1024)=0 OR (null_mask & 1024)<>0)),
    CHECK(("heartbeatPlaylistId" IS NULL)=((shape_mask & 2048)=0 OR (null_mask & 2048)<>0)),
    CHECK(("heartbeatAccountId" IS NULL)=((shape_mask & 4096)=0 OR (null_mask & 4096)<>0)),
    CHECK(("provider" IS NULL)=((shape_mask & 8192)=0 OR (null_mask & 8192)<>0)),
    CHECK(("remoteSession" IS NULL)=((shape_mask & 16384)=0 OR (null_mask & 16384)<>0)),
    PRIMARY KEY("scope_key","entries_key"),
    FOREIGN KEY("scope_key","entries_key") REFERENCES "music_queue_similar_returnQueue_entries"("scope_key","entries_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_queue_similar_returnQueue_entries_context_queueSource" (
    "scope_key" TEXT NOT NULL,
    "entries_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 15),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "id" TEXT,
    "title" TEXT,
    "kind" TEXT,
    "platform" TEXT,
    CHECK(("id" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("title" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("kind" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("platform" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    PRIMARY KEY("scope_key","entries_key"),
    FOREIGN KEY("scope_key","entries_key") REFERENCES "music_queue_similar_returnQueue_entries_context"("scope_key","entries_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_queue_similar_returnQueue_entries_context_similar" (
    "scope_key" TEXT NOT NULL,
    "entries_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "batchId" TEXT,
    "basedOnSongId" TEXT,
    "provider" TEXT,
    CHECK(("batchId" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("basedOnSongId" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("provider" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    FOREIGN KEY("basedOnSongId") REFERENCES music_songs(song_id),
    PRIMARY KEY("scope_key","entries_key"),
    FOREIGN KEY("scope_key","entries_key") REFERENCES "music_queue_similar_returnQueue_entries_context"("scope_key","entries_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_queue_similar_returnQueue_priority" (
    "scope_key" TEXT NOT NULL,
    "priority_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","priority_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_queue_similar_returnQueue"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_queue_similar_returnQueue_history" (
    "scope_key" TEXT NOT NULL,
    "history_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","history_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_queue_similar_returnQueue"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_queue_similar_returnQueue_source" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 4095),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "id" TEXT,
    "title" TEXT,
    "kind" TEXT,
    "platform" TEXT,
    "sourceType" TEXT,
    "sourceTitle" TEXT,
    "sourceId" TEXT,
    "playlist" TEXT,
    "seedEntryId" TEXT,
    "remoteSession" TEXT,
    "dailyBranch" TEXT,
    "searchKeyword" TEXT,
    CHECK(("id" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("title" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("kind" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("platform" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("sourceType" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("sourceTitle" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("sourceId" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("playlist" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("seedEntryId" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("remoteSession" IS NULL)=((shape_mask & 512)=0 OR (null_mask & 512)<>0)),
    CHECK(("dailyBranch" IS NULL)=((shape_mask & 1024)=0 OR (null_mask & 1024)<>0)),
    CHECK(("searchKeyword" IS NULL)=((shape_mask & 2048)=0 OR (null_mask & 2048)<>0)),
    PRIMARY KEY("scope_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_queue_similar_returnQueue"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_queue_similar_sourceIds" (
    "scope_key" TEXT NOT NULL,
    "sourceIds_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","sourceIds_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_queue_similar"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_queue_similar_attempted" (
    "scope_key" TEXT NOT NULL,
    "attempted_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","attempted_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_queue_similar"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_queue_similar_providerContext" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "pid_kind" TEXT CHECK("pid_kind" IN ('text','number')),
    "pid_value" TEXT,
    "sid_kind" TEXT CHECK("sid_kind" IN ('text','number')),
    "sid_value" TEXT,
    "count" INTEGER,
    CHECK(("pid_kind" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("pid_kind" IS NULL)=("pid_value" IS NULL)),
    CHECK(("sid_kind" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("sid_kind" IS NULL)=("sid_value" IS NULL)),
    CHECK(("count" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    PRIMARY KEY("scope_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_queue_similar"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_queue_similar_batches" (
    "scope_key" TEXT NOT NULL,
    "batches_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 15),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "basedOnSongId" TEXT,
    "basedOnTitle" TEXT,
    "provider" TEXT,
    CHECK(("basedOnSongId" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("basedOnTitle" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("provider" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    FOREIGN KEY("basedOnSongId") REFERENCES music_songs(song_id),
    PRIMARY KEY("scope_key","batches_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_queue_similar"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_queue_similar_batches_entryIds" (
    "scope_key" TEXT NOT NULL,
    "batches_key" TEXT NOT NULL,
    "entryIds_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","batches_key","entryIds_key"),
    FOREIGN KEY("scope_key","batches_key") REFERENCES "music_queue_similar_batches"("scope_key","batches_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_history" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "schema" INTEGER,
    CHECK(("schema" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key")
) STRICT;

CREATE TABLE "music_history_playlists" (
    "scope_key" TEXT NOT NULL,
    "playlists_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 255),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "at" REAL,
    "title" TEXT,
    "currentId" TEXT,
    CHECK(("at" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("title" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("currentId" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    PRIMARY KEY("scope_key","playlists_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_history"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_history_playlists_entries" (
    "scope_key" TEXT NOT NULL,
    "playlists_key" TEXT NOT NULL,
    "entries_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 3),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "songId" TEXT,
    CHECK(("songId" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    FOREIGN KEY("songId") REFERENCES music_songs(song_id),
    PRIMARY KEY("scope_key","playlists_key","entries_key"),
    FOREIGN KEY("scope_key","playlists_key") REFERENCES "music_history_playlists"("scope_key","playlists_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_history_playlists_entries_context" (
    "scope_key" TEXT NOT NULL,
    "playlists_key" TEXT NOT NULL,
    "entries_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 131071),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "collectionId" TEXT,
    "playlistId" TEXT,
    "sourceTitle" TEXT,
    "playlist" TEXT,
    "sourceType" TEXT,
    "sourceId" TEXT,
    "sourcePlatform" TEXT,
    "platform" TEXT,
    "sourceLabel" TEXT,
    "searchKeyword" TEXT,
    "dailyBranch" TEXT,
    "heartbeatPlaylistId" TEXT,
    "heartbeatAccountId" TEXT,
    "provider" TEXT,
    "remoteSession" TEXT,
    CHECK(("collectionId" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("playlistId" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("sourceTitle" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("playlist" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("sourceType" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("sourceId" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("sourcePlatform" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("platform" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("sourceLabel" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("searchKeyword" IS NULL)=((shape_mask & 512)=0 OR (null_mask & 512)<>0)),
    CHECK(("dailyBranch" IS NULL)=((shape_mask & 1024)=0 OR (null_mask & 1024)<>0)),
    CHECK(("heartbeatPlaylistId" IS NULL)=((shape_mask & 2048)=0 OR (null_mask & 2048)<>0)),
    CHECK(("heartbeatAccountId" IS NULL)=((shape_mask & 4096)=0 OR (null_mask & 4096)<>0)),
    CHECK(("provider" IS NULL)=((shape_mask & 8192)=0 OR (null_mask & 8192)<>0)),
    CHECK(("remoteSession" IS NULL)=((shape_mask & 16384)=0 OR (null_mask & 16384)<>0)),
    PRIMARY KEY("scope_key","playlists_key","entries_key"),
    FOREIGN KEY("scope_key","playlists_key","entries_key") REFERENCES "music_history_playlists_entries"("scope_key","playlists_key","entries_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_history_playlists_entries_context_queueSource" (
    "scope_key" TEXT NOT NULL,
    "playlists_key" TEXT NOT NULL,
    "entries_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 15),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "id" TEXT,
    "title" TEXT,
    "kind" TEXT,
    "platform" TEXT,
    CHECK(("id" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("title" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("kind" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("platform" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    PRIMARY KEY("scope_key","playlists_key","entries_key"),
    FOREIGN KEY("scope_key","playlists_key","entries_key") REFERENCES "music_history_playlists_entries_context"("scope_key","playlists_key","entries_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_history_playlists_entries_context_similar" (
    "scope_key" TEXT NOT NULL,
    "playlists_key" TEXT NOT NULL,
    "entries_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "batchId" TEXT,
    "basedOnSongId" TEXT,
    "provider" TEXT,
    CHECK(("batchId" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("basedOnSongId" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("provider" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    FOREIGN KEY("basedOnSongId") REFERENCES music_songs(song_id),
    PRIMARY KEY("scope_key","playlists_key","entries_key"),
    FOREIGN KEY("scope_key","playlists_key","entries_key") REFERENCES "music_history_playlists_entries_context"("scope_key","playlists_key","entries_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_history_playlists_priority" (
    "scope_key" TEXT NOT NULL,
    "playlists_key" TEXT NOT NULL,
    "priority_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","playlists_key","priority_key"),
    FOREIGN KEY("scope_key","playlists_key") REFERENCES "music_history_playlists"("scope_key","playlists_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_history_playlists_history" (
    "scope_key" TEXT NOT NULL,
    "playlists_key" TEXT NOT NULL,
    "history_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","playlists_key","history_key"),
    FOREIGN KEY("scope_key","playlists_key") REFERENCES "music_history_playlists"("scope_key","playlists_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_history_playlists_source" (
    "scope_key" TEXT NOT NULL,
    "playlists_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 4095),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "id" TEXT,
    "title" TEXT,
    "kind" TEXT,
    "platform" TEXT,
    "sourceType" TEXT,
    "sourceTitle" TEXT,
    "sourceId" TEXT,
    "playlist" TEXT,
    "seedEntryId" TEXT,
    "remoteSession" TEXT,
    "dailyBranch" TEXT,
    "searchKeyword" TEXT,
    CHECK(("id" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("title" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("kind" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("platform" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("sourceType" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("sourceTitle" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("sourceId" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("playlist" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("seedEntryId" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("remoteSession" IS NULL)=((shape_mask & 512)=0 OR (null_mask & 512)<>0)),
    CHECK(("dailyBranch" IS NULL)=((shape_mask & 1024)=0 OR (null_mask & 1024)<>0)),
    CHECK(("searchKeyword" IS NULL)=((shape_mask & 2048)=0 OR (null_mask & 2048)<>0)),
    PRIMARY KEY("scope_key","playlists_key"),
    FOREIGN KEY("scope_key","playlists_key") REFERENCES "music_history_playlists"("scope_key","playlists_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_history_playlists_context" (
    "scope_key" TEXT NOT NULL,
    "playlists_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 131071),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "collectionId" TEXT,
    "playlistId" TEXT,
    "sourceTitle" TEXT,
    "playlist" TEXT,
    "sourceType" TEXT,
    "sourceId" TEXT,
    "sourcePlatform" TEXT,
    "platform" TEXT,
    "sourceLabel" TEXT,
    "searchKeyword" TEXT,
    "dailyBranch" TEXT,
    "heartbeatPlaylistId" TEXT,
    "heartbeatAccountId" TEXT,
    "provider" TEXT,
    "remoteSession" TEXT,
    CHECK(("collectionId" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("playlistId" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("sourceTitle" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("playlist" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("sourceType" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("sourceId" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("sourcePlatform" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("platform" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("sourceLabel" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("searchKeyword" IS NULL)=((shape_mask & 512)=0 OR (null_mask & 512)<>0)),
    CHECK(("dailyBranch" IS NULL)=((shape_mask & 1024)=0 OR (null_mask & 1024)<>0)),
    CHECK(("heartbeatPlaylistId" IS NULL)=((shape_mask & 2048)=0 OR (null_mask & 2048)<>0)),
    CHECK(("heartbeatAccountId" IS NULL)=((shape_mask & 4096)=0 OR (null_mask & 4096)<>0)),
    CHECK(("provider" IS NULL)=((shape_mask & 8192)=0 OR (null_mask & 8192)<>0)),
    CHECK(("remoteSession" IS NULL)=((shape_mask & 16384)=0 OR (null_mask & 16384)<>0)),
    PRIMARY KEY("scope_key","playlists_key"),
    FOREIGN KEY("scope_key","playlists_key") REFERENCES "music_history_playlists"("scope_key","playlists_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_history_playlists_context_queueSource" (
    "scope_key" TEXT NOT NULL,
    "playlists_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 15),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "id" TEXT,
    "title" TEXT,
    "kind" TEXT,
    "platform" TEXT,
    CHECK(("id" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("title" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("kind" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("platform" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    PRIMARY KEY("scope_key","playlists_key"),
    FOREIGN KEY("scope_key","playlists_key") REFERENCES "music_history_playlists_context"("scope_key","playlists_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_history_playlists_context_similar" (
    "scope_key" TEXT NOT NULL,
    "playlists_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "batchId" TEXT,
    "basedOnSongId" TEXT,
    "provider" TEXT,
    CHECK(("batchId" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("basedOnSongId" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("provider" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    FOREIGN KEY("basedOnSongId") REFERENCES music_songs(song_id),
    PRIMARY KEY("scope_key","playlists_key"),
    FOREIGN KEY("scope_key","playlists_key") REFERENCES "music_history_playlists_context"("scope_key","playlists_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_history_days" (
    "scope_key" TEXT NOT NULL,
    "days_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 15),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "date" TEXT,
    "at" REAL,
    "id" TEXT,
    CHECK(("date" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("at" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("id" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    PRIMARY KEY("scope_key","days_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_history"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_history_days_songs" (
    "scope_key" TEXT NOT NULL,
    "days_key" TEXT NOT NULL,
    "songs_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "songId" TEXT,
    "at" REAL,
    CHECK(("songId" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("at" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    FOREIGN KEY("songId") REFERENCES music_songs(song_id),
    PRIMARY KEY("scope_key","days_key","songs_key"),
    FOREIGN KEY("scope_key","days_key") REFERENCES "music_history_days"("scope_key","days_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_history_days_songs_context" (
    "scope_key" TEXT NOT NULL,
    "days_key" TEXT NOT NULL,
    "songs_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 131071),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "collectionId" TEXT,
    "playlistId" TEXT,
    "sourceTitle" TEXT,
    "playlist" TEXT,
    "sourceType" TEXT,
    "sourceId" TEXT,
    "sourcePlatform" TEXT,
    "platform" TEXT,
    "sourceLabel" TEXT,
    "searchKeyword" TEXT,
    "dailyBranch" TEXT,
    "heartbeatPlaylistId" TEXT,
    "heartbeatAccountId" TEXT,
    "provider" TEXT,
    "remoteSession" TEXT,
    CHECK(("collectionId" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("playlistId" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("sourceTitle" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("playlist" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("sourceType" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("sourceId" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("sourcePlatform" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("platform" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("sourceLabel" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("searchKeyword" IS NULL)=((shape_mask & 512)=0 OR (null_mask & 512)<>0)),
    CHECK(("dailyBranch" IS NULL)=((shape_mask & 1024)=0 OR (null_mask & 1024)<>0)),
    CHECK(("heartbeatPlaylistId" IS NULL)=((shape_mask & 2048)=0 OR (null_mask & 2048)<>0)),
    CHECK(("heartbeatAccountId" IS NULL)=((shape_mask & 4096)=0 OR (null_mask & 4096)<>0)),
    CHECK(("provider" IS NULL)=((shape_mask & 8192)=0 OR (null_mask & 8192)<>0)),
    CHECK(("remoteSession" IS NULL)=((shape_mask & 16384)=0 OR (null_mask & 16384)<>0)),
    PRIMARY KEY("scope_key","days_key","songs_key"),
    FOREIGN KEY("scope_key","days_key","songs_key") REFERENCES "music_history_days_songs"("scope_key","days_key","songs_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_history_days_songs_context_queueSource" (
    "scope_key" TEXT NOT NULL,
    "days_key" TEXT NOT NULL,
    "songs_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 15),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "id" TEXT,
    "title" TEXT,
    "kind" TEXT,
    "platform" TEXT,
    CHECK(("id" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("title" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("kind" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("platform" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    PRIMARY KEY("scope_key","days_key","songs_key"),
    FOREIGN KEY("scope_key","days_key","songs_key") REFERENCES "music_history_days_songs_context"("scope_key","days_key","songs_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_history_days_songs_context_similar" (
    "scope_key" TEXT NOT NULL,
    "days_key" TEXT NOT NULL,
    "songs_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "batchId" TEXT,
    "basedOnSongId" TEXT,
    "provider" TEXT,
    CHECK(("batchId" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("basedOnSongId" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("provider" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    FOREIGN KEY("basedOnSongId") REFERENCES music_songs(song_id),
    PRIMARY KEY("scope_key","days_key","songs_key"),
    FOREIGN KEY("scope_key","days_key","songs_key") REFERENCES "music_history_days_songs_context"("scope_key","days_key","songs_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_playback" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 4095),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "schema" INTEGER,
    "revision" INTEGER,
    "mode" INTEGER,
    "lyricsEnabled" INTEGER CHECK("lyricsEnabled" IN (0,1)),
    "showTranslation" INTEGER CHECK("showTranslation" IN (0,1)),
    CHECK(("schema" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("revision" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("mode" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("lyricsEnabled" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("showTranslation" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    PRIMARY KEY("scope_key")
) STRICT;

CREATE TABLE "music_playback_qualitySettings" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 31),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "request" TEXT,
    CHECK(("request" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_playback"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_playback_qualitySettings_qualityOrder" (
    "scope_key" TEXT NOT NULL,
    "qualityOrder_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","qualityOrder_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_playback_qualitySettings"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_playback_qualitySettings_qualityEnabled" (
    "scope_key" TEXT NOT NULL,
    "qualityEnabled_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","qualityEnabled_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_playback_qualitySettings"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_playback_qualitySettings_spatialOrder" (
    "scope_key" TEXT NOT NULL,
    "spatialOrder_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","spatialOrder_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_playback_qualitySettings"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_playback_qualitySettings_spatialEnabled" (
    "scope_key" TEXT NOT NULL,
    "spatialEnabled_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","spatialEnabled_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_playback_qualitySettings"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_playback_officialSources" (
    "scope_key" TEXT NOT NULL,
    "officialSources_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","officialSources_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_playback"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_playback_metadataPlatforms" (
    "scope_key" TEXT NOT NULL,
    "metadataPlatforms_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","metadataPlatforms_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_playback"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_playback_metadataEnabled" (
    "scope_key" TEXT NOT NULL,
    "metadataEnabled_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","metadataEnabled_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_playback"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_playback_position" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 3),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "occurrenceId" TEXT,
    "seconds" REAL,
    CHECK(("occurrenceId" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("seconds" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    PRIMARY KEY("scope_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_playback"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_playback_sourceExclusions" (
    "scope_key" TEXT NOT NULL,
    "sourceExclusions_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 63),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "songID" TEXT,
    "title" TEXT,
    "artist" TEXT,
    "provider" TEXT,
    "platform" TEXT,
    "sourceKey" TEXT,
    CHECK(("songID" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("title" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("artist" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("provider" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("platform" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("sourceKey" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    FOREIGN KEY("songID") REFERENCES music_songs(song_id),
    PRIMARY KEY("scope_key","sourceExclusions_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_playback"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_playback_recordingSourceExclusions" (
    "scope_key" TEXT NOT NULL,
    "recordingSourceExclusions_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 63),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "songID" TEXT,
    "title" TEXT,
    "artist" TEXT,
    "provider" TEXT,
    "platform" TEXT,
    "sourceKey" TEXT,
    CHECK(("songID" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("title" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("artist" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("provider" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("platform" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("sourceKey" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    FOREIGN KEY("songID") REFERENCES music_songs(song_id),
    PRIMARY KEY("scope_key","recordingSourceExclusions_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_playback"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_catalog" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "schema" INTEGER,
    "revision" INTEGER,
    "platform" TEXT,
    CHECK(("schema" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("revision" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("platform" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    PRIMARY KEY("scope_key")
) STRICT;

CREATE TABLE "music_windows_playback_settings" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 3),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "schema" INTEGER,
    "mode" INTEGER,
    CHECK(("schema" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("mode" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    PRIMARY KEY("scope_key")
) STRICT;

CREATE TABLE "music_windows_online_settings" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "schema" INTEGER,
    "revision" INTEGER,
    "lyrics" INTEGER CHECK("lyrics" IN (0,1)),
    CHECK(("schema" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("revision" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("lyrics" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    PRIMARY KEY("scope_key")
) STRICT;

CREATE TABLE "music_windows_device_settings" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "schema" INTEGER,
    "revision" INTEGER,
    "volume" INTEGER,
    CHECK(("schema" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("revision" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("volume" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    PRIMARY KEY("scope_key")
) STRICT;

CREATE TABLE "music_windows_appearance" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 63),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "schema" INTEGER,
    "revision" INTEGER,
    "playlistView" TEXT,
    "playerTheme" TEXT,
    "colorMode" TEXT,
    "colorPalette" TEXT,
    CHECK(("schema" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("revision" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("playlistView" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("playerTheme" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("colorMode" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("colorPalette" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    PRIMARY KEY("scope_key")
) STRICT;

CREATE TABLE "music_windows_catalog" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "schema" INTEGER,
    "revision" INTEGER,
    "platform" TEXT,
    CHECK(("schema" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("revision" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("platform" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    PRIMARY KEY("scope_key")
) STRICT;

CREATE TABLE "music_windows_desktop_lyrics" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 63),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "schema" INTEGER,
    "revision" INTEGER,
    "layout" TEXT,
    "normalColor" TEXT,
    "highlightColor" TEXT,
    CHECK(("schema" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("revision" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("layout" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("normalColor" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("highlightColor" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    PRIMARY KEY("scope_key")
) STRICT;

CREATE TABLE "music_windows_desktop_lyrics_geometry" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 31),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "monitor" TEXT,
    "x" REAL,
    "y" REAL,
    "width" REAL,
    "height" REAL,
    CHECK(("monitor" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("x" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("y" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("width" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("height" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    PRIMARY KEY("scope_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_desktop_lyrics"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_quality" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 127),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "schema" INTEGER,
    "revision" INTEGER,
    "request" TEXT,
    CHECK(("schema" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("revision" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("request" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    PRIMARY KEY("scope_key")
) STRICT;

CREATE TABLE "music_windows_quality_qualityOrder" (
    "scope_key" TEXT NOT NULL,
    "qualityOrder_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","qualityOrder_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_quality"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_quality_qualityEnabled" (
    "scope_key" TEXT NOT NULL,
    "qualityEnabled_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","qualityEnabled_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_quality"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_quality_spatialOrder" (
    "scope_key" TEXT NOT NULL,
    "spatialOrder_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","spatialOrder_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_quality"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_quality_spatialEnabled" (
    "scope_key" TEXT NOT NULL,
    "spatialEnabled_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","spatialEnabled_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_quality"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_official_sources" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 15),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "schema" INTEGER,
    "revision" INTEGER,
    CHECK(("schema" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("revision" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    PRIMARY KEY("scope_key")
) STRICT;

CREATE TABLE "music_windows_official_sources_platforms" (
    "scope_key" TEXT NOT NULL,
    "platforms_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","platforms_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_official_sources"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_official_sources_enabled" (
    "scope_key" TEXT NOT NULL,
    "enabled_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","enabled_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_official_sources"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_metadata" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 15),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "schema" INTEGER,
    "revision" INTEGER,
    CHECK(("schema" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("revision" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    PRIMARY KEY("scope_key")
) STRICT;

CREATE TABLE "music_windows_metadata_platforms" (
    "scope_key" TEXT NOT NULL,
    "platforms_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","platforms_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_metadata"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_metadata_enabled" (
    "scope_key" TEXT NOT NULL,
    "enabled_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","enabled_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_metadata"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_search" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 15),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "schema" INTEGER,
    "revision" INTEGER,
    CHECK(("schema" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("revision" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    PRIMARY KEY("scope_key")
) STRICT;

CREATE TABLE "music_windows_search_platforms" (
    "scope_key" TEXT NOT NULL,
    "platforms_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","platforms_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_search"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_search_enabled" (
    "scope_key" TEXT NOT NULL,
    "enabled_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","enabled_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_search"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_search_history" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 3),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "schema" INTEGER,
    CHECK(("schema" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key")
) STRICT;

CREATE TABLE "music_windows_search_history_items" (
    "scope_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","items_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_search_history"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_exclusions" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "schema" INTEGER,
    "revision" INTEGER,
    CHECK(("schema" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("revision" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    PRIMARY KEY("scope_key")
) STRICT;

CREATE TABLE "music_windows_exclusions_items" (
    "scope_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 127),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "songId" TEXT,
    "provider" TEXT,
    "platform" TEXT,
    "providerName" TEXT,
    "title" TEXT,
    "artist" TEXT,
    "at" INTEGER,
    CHECK(("songId" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("provider" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("platform" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("providerName" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("title" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("artist" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("at" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    FOREIGN KEY("songId") REFERENCES music_songs(song_id),
    PRIMARY KEY("scope_key","items_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_exclusions"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_local_lyrics" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 3),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "schema" INTEGER,
    CHECK(("schema" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key")
) STRICT;

CREATE TABLE "music_windows_local_lyrics_items" (
    "scope_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "songId" TEXT,
    "contentHash" TEXT,
    CHECK(("songId" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("contentHash" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    FOREIGN KEY("songId") REFERENCES music_songs(song_id),
    PRIMARY KEY("scope_key","items_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_local_lyrics"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_local_lyrics_items_lines" (
    "scope_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    "lines_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 63),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "time" REAL,
    "text" TEXT,
    "translation" TEXT,
    "romanization" TEXT,
    "end" REAL,
    CHECK(("time" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("text" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("translation" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("romanization" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("end" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    PRIMARY KEY("scope_key","items_key","lines_key"),
    FOREIGN KEY("scope_key","items_key") REFERENCES "music_windows_local_lyrics_items"("scope_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_local_lyrics_items_lines_words" (
    "scope_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    "lines_key" TEXT NOT NULL,
    "words_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 15),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "time" REAL,
    "text" TEXT,
    "duration" REAL,
    "offset" REAL,
    CHECK(("time" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("text" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("duration" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("offset" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    PRIMARY KEY("scope_key","items_key","lines_key","words_key"),
    FOREIGN KEY("scope_key","items_key","lines_key") REFERENCES "music_windows_local_lyrics_items_lines"("scope_key","items_key","lines_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_sync" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 127),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "schema" INTEGER,
    "revision" INTEGER,
    CHECK(("schema" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("revision" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    PRIMARY KEY("scope_key")
) STRICT;

CREATE TABLE "music_windows_sync_platforms" (
    "scope_key" TEXT NOT NULL,
    "platforms_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "favorites" INTEGER CHECK("favorites" IN (0,1)),
    "playlists" INTEGER CHECK("playlists" IN (0,1)),
    "accountId" TEXT,
    CHECK(("favorites" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("playlists" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("accountId" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    PRIMARY KEY("scope_key","platforms_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_sync"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_sync_pending" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 8191),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "platform" TEXT,
    "accountId" TEXT,
    "collectionId" TEXT,
    "remoteId" TEXT,
    "operation" TEXT,
    "beforePresent" INTEGER CHECK("beforePresent" IN (0,1)),
    "desiredPresent" INTEGER CHECK("desiredPresent" IN (0,1)),
    "name" TEXT,
    CHECK(("platform" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("accountId" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("collectionId" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("remoteId" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("operation" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("beforePresent" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("desiredPresent" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("name" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    PRIMARY KEY("scope_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_sync"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_sync_pending_before" (
    "scope_key" TEXT NOT NULL,
    "before_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","before_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_sync_pending"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_sync_pending_desired" (
    "scope_key" TEXT NOT NULL,
    "desired_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","desired_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_sync_pending"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_sync_pending_ids" (
    "scope_key" TEXT NOT NULL,
    "ids_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","ids_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_sync_pending"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_sync_pending_directory" (
    "scope_key" TEXT NOT NULL,
    "directory_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","directory_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_sync_pending"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_sync_pending_locator" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 15),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "relationId_camel" TEXT,
    "relationID" TEXT,
    "id" TEXT,
    "remoteId" TEXT,
    CHECK(("relationId_camel" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("relationID" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("id" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("remoteId" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    PRIMARY KEY("scope_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_sync_pending"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_sync_confirmation" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 127),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "id" TEXT,
    "code" TEXT,
    "platform" TEXT,
    "count" INTEGER,
    "digest" TEXT,
    "signature" TEXT,
    CHECK(("id" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("code" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("platform" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("count" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("digest" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("signature" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    PRIMARY KEY("scope_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_sync"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_sync_confirmation_collections" (
    "scope_key" TEXT NOT NULL,
    "collections_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","collections_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_sync_confirmation"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_sync_approved" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 127),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "id" TEXT,
    "code" TEXT,
    "platform" TEXT,
    "count" INTEGER,
    "digest" TEXT,
    "signature" TEXT,
    CHECK(("id" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("code" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("platform" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("count" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("digest" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("signature" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    PRIMARY KEY("scope_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_sync"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_sync_approved_collections" (
    "scope_key" TEXT NOT NULL,
    "collections_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("scope_key","collections_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_sync_approved"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_sync_status" (
    "scope_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 15),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "phase" TEXT,
    "at" REAL,
    "code" TEXT,
    CHECK(("phase" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("at" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("code" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    PRIMARY KEY("scope_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_sync"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_windows_sync_status_issues" (
    "scope_key" TEXT NOT NULL,
    "issues_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 15),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "platform" TEXT,
    "collectionID" TEXT,
    "collectionId_camel" TEXT,
    "code" TEXT,
    CHECK(("platform" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("collectionID" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("collectionId_camel" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("code" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    PRIMARY KEY("scope_key","issues_key"),
    FOREIGN KEY("scope_key") REFERENCES "music_windows_sync_status"("scope_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_source_discovery" (
    "song_id" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 3),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "storedBytes" INTEGER,
    CHECK(("storedBytes" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    FOREIGN KEY(song_id) REFERENCES music_songs(song_id),
    PRIMARY KEY("song_id")
) STRICT;

CREATE TABLE "music_source_discovery_platforms" (
    "song_id" TEXT NOT NULL,
    "platforms_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "checkedAt" REAL,
    CHECK(("checkedAt" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("song_id","platforms_key"),
    FOREIGN KEY("song_id") REFERENCES "music_source_discovery"("song_id") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_source_discovery_platforms_sourceKeys" (
    "song_id" TEXT NOT NULL,
    "platforms_key" TEXT NOT NULL,
    "sourceKeys_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("song_id","platforms_key","sourceKeys_key"),
    FOREIGN KEY("song_id","platforms_key") REFERENCES "music_source_discovery_platforms"("song_id","platforms_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_source_discovery_platforms_observations" (
    "song_id" TEXT NOT NULL,
    "platforms_key" TEXT NOT NULL,
    "observations_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 4095),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "requestID" TEXT,
    "startedAt" REAL,
    "order" INTEGER,
    "checkedAt" REAL,
    "platform" TEXT,
    "query" TEXT,
    "context" TEXT,
    "page" INTEGER,
    "limit" INTEGER,
    "scope" TEXT,
    "outcome" TEXT,
    "failureCode" TEXT,
    CHECK(("requestID" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("startedAt" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("order" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("checkedAt" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("platform" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("query" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("context" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("page" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("limit" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("scope" IS NULL)=((shape_mask & 512)=0 OR (null_mask & 512)<>0)),
    CHECK(("outcome" IS NULL)=((shape_mask & 1024)=0 OR (null_mask & 1024)<>0)),
    CHECK(("failureCode" IS NULL)=((shape_mask & 2048)=0 OR (null_mask & 2048)<>0)),
    PRIMARY KEY("song_id","platforms_key","observations_key"),
    FOREIGN KEY("song_id","platforms_key") REFERENCES "music_source_discovery_platforms"("song_id","platforms_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display" (
    "singleton" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 3),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "source" TEXT,
    CHECK(("source" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("singleton")
) STRICT;

CREATE TABLE "music_home_display_sections" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 511),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "title" TEXT,
    "layout" TEXT,
    "selectedFilter" TEXT,
    "selectedTab" TEXT,
    CHECK(("title" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("layout" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("selectedFilter" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("selectedTab" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    PRIMARY KEY("singleton","sections_key"),
    FOREIGN KEY("singleton") REFERENCES "music_home_display"("singleton") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_avatars" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "avatars_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("singleton","sections_key","avatars_key"),
    FOREIGN KEY("singleton","sections_key") REFERENCES "music_home_display_sections"("singleton","sections_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_channel" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 31),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "id" TEXT,
    "title" TEXT,
    "kind" TEXT,
    "pageCode" TEXT,
    "platform" TEXT,
    CHECK(("id" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("title" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("kind" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("pageCode" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("platform" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    PRIMARY KEY("singleton","sections_key"),
    FOREIGN KEY("singleton","sections_key") REFERENCES "music_home_display_sections"("singleton","sections_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_filters" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "filters_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 3),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "title" TEXT,
    "group" TEXT,
    CHECK(("title" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("group" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    PRIMARY KEY("singleton","sections_key","filters_key"),
    FOREIGN KEY("singleton","sections_key") REFERENCES "music_home_display_sections"("singleton","sections_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_tabs" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "tabs_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 3),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "title" TEXT,
    CHECK(("title" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("singleton","sections_key","tabs_key"),
    FOREIGN KEY("singleton","sections_key") REFERENCES "music_home_display_sections"("singleton","sections_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_items" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 65535),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "title" TEXT,
    "subtitle" TEXT,
    "artwork" TEXT,
    "recommendationReason" TEXT,
    "coverLabel" TEXT,
    "requiresAccount" INTEGER CHECK("requiresAccount" IN (0,1)),
    "kind" TEXT,
    "label" TEXT,
    "contextText" TEXT,
    "playCount" INTEGER,
    "presentation" TEXT,
    CHECK(("title" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("subtitle" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("artwork" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("recommendationReason" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("coverLabel" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("requiresAccount" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("kind" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("label" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("contextText" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("playCount" IS NULL)=((shape_mask & 512)=0 OR (null_mask & 512)<>0)),
    CHECK(("presentation" IS NULL)=((shape_mask & 1024)=0 OR (null_mask & 1024)<>0)),
    PRIMARY KEY("singleton","sections_key","items_key"),
    FOREIGN KEY("singleton","sections_key") REFERENCES "music_home_display_sections"("singleton","sections_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_items_previewSongIDs" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    "previewSongIDs_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("singleton","sections_key","items_key","previewSongIDs_key"),
    FOREIGN KEY("singleton","sections_key","items_key") REFERENCES "music_home_display_sections_items"("singleton","sections_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_items_coverRef" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "platform" TEXT,
    "kind" TEXT,
    "id" TEXT,
    CHECK(("platform" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("kind" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("id" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    PRIMARY KEY("singleton","sections_key","items_key"),
    FOREIGN KEY("singleton","sections_key","items_key") REFERENCES "music_home_display_sections_items"("singleton","sections_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_items_displayOnlyPreviews" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    "displayOnlyPreviews_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 31),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "title" TEXT,
    "artist" TEXT,
    "album" TEXT,
    "artwork" TEXT,
    "duration" REAL,
    CHECK(("title" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("artist" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("album" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("artwork" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("duration" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    PRIMARY KEY("singleton","sections_key","items_key","displayOnlyPreviews_key"),
    FOREIGN KEY("singleton","sections_key","items_key") REFERENCES "music_home_display_sections_items"("singleton","sections_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_items_ranking" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "songArtwork" TEXT,
    "updateFrequency" TEXT,
    CHECK(("songArtwork" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("updateFrequency" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    PRIMARY KEY("singleton","sections_key","items_key"),
    FOREIGN KEY("singleton","sections_key","items_key") REFERENCES "music_home_display_sections_items"("singleton","sections_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_items_ranking_preview" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    "preview_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 63),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "id" TEXT,
    "title" TEXT,
    "artist" TEXT,
    "artwork" TEXT,
    "rank" INTEGER,
    "lastRank" INTEGER,
    CHECK(("id" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("title" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("artist" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("artwork" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("rank" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("lastRank" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    PRIMARY KEY("singleton","sections_key","items_key","preview_key"),
    FOREIGN KEY("singleton","sections_key","items_key") REFERENCES "music_home_display_sections_items_ranking"("singleton","sections_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_items_content" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 31),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "kind" TEXT,
    "record" TEXT,
    CHECK(("kind" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("record" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    PRIMARY KEY("singleton","sections_key","items_key"),
    FOREIGN KEY("singleton","sections_key","items_key") REFERENCES "music_home_display_sections_items"("singleton","sections_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_items_content_collection" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 255),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "platform" TEXT,
    "nativeID" TEXT,
    "kind" TEXT,
    "title" TEXT,
    "creator" TEXT,
    "artwork" TEXT,
    "count" INTEGER,
    "playCount" INTEGER,
    CHECK(("platform" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("nativeID" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("kind" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("title" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("creator" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("artwork" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("count" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("playCount" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    PRIMARY KEY("singleton","sections_key","items_key"),
    FOREIGN KEY("singleton","sections_key","items_key") REFERENCES "music_home_display_sections_items_content"("singleton","sections_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_items_content_channel" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 31),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "id" TEXT,
    "title" TEXT,
    "kind" TEXT,
    "pageCode" TEXT,
    "platform" TEXT,
    CHECK(("id" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("title" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("kind" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("pageCode" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("platform" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    PRIMARY KEY("singleton","sections_key","items_key"),
    FOREIGN KEY("singleton","sections_key","items_key") REFERENCES "music_home_display_sections_items_content"("singleton","sections_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_items_content_action" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 8191),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "type" TEXT,
    "provider" TEXT,
    "mode" TEXT,
    "submode" TEXT,
    "entry" TEXT,
    "ruleId" TEXT,
    "tabTagId" TEXT,
    "sourceName" TEXT,
    "sourceType" TEXT,
    "branch" TEXT,
    "songId" TEXT,
    CHECK(("type" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("provider" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("mode" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("submode" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("entry" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("ruleId" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("tabTagId" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("sourceName" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("sourceType" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("branch" IS NULL)=((shape_mask & 512)=0 OR (null_mask & 512)<>0)),
    CHECK(("songId" IS NULL)=((shape_mask & 1024)=0 OR (null_mask & 1024)<>0)),
    PRIMARY KEY("singleton","sections_key","items_key"),
    FOREIGN KEY("singleton","sections_key","items_key") REFERENCES "music_home_display_sections_items_content"("singleton","sections_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_items_content_action_sourceIds" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    "sourceIds_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("singleton","sections_key","items_key","sourceIds_key"),
    FOREIGN KEY("singleton","sections_key","items_key") REFERENCES "music_home_display_sections_items_content_action"("singleton","sections_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_items_content_action_ids" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    "ids_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("singleton","sections_key","items_key","ids_key"),
    FOREIGN KEY("singleton","sections_key","items_key") REFERENCES "music_home_display_sections_items_content_action"("singleton","sections_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_tabs_items" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "tabs_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 65535),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "title" TEXT,
    "subtitle" TEXT,
    "artwork" TEXT,
    "recommendationReason" TEXT,
    "coverLabel" TEXT,
    "requiresAccount" INTEGER CHECK("requiresAccount" IN (0,1)),
    "kind" TEXT,
    "label" TEXT,
    "contextText" TEXT,
    "playCount" INTEGER,
    "presentation" TEXT,
    CHECK(("title" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("subtitle" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("artwork" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("recommendationReason" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("coverLabel" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("requiresAccount" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("kind" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("label" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("contextText" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("playCount" IS NULL)=((shape_mask & 512)=0 OR (null_mask & 512)<>0)),
    CHECK(("presentation" IS NULL)=((shape_mask & 1024)=0 OR (null_mask & 1024)<>0)),
    PRIMARY KEY("singleton","sections_key","tabs_key","items_key"),
    FOREIGN KEY("singleton","sections_key","tabs_key") REFERENCES "music_home_display_sections_tabs"("singleton","sections_key","tabs_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_tabs_items_previewSongIDs" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "tabs_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    "previewSongIDs_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("singleton","sections_key","tabs_key","items_key","previewSongIDs_key"),
    FOREIGN KEY("singleton","sections_key","tabs_key","items_key") REFERENCES "music_home_display_sections_tabs_items"("singleton","sections_key","tabs_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_tabs_items_coverRef" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "tabs_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "platform" TEXT,
    "kind" TEXT,
    "id" TEXT,
    CHECK(("platform" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("kind" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("id" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    PRIMARY KEY("singleton","sections_key","tabs_key","items_key"),
    FOREIGN KEY("singleton","sections_key","tabs_key","items_key") REFERENCES "music_home_display_sections_tabs_items"("singleton","sections_key","tabs_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_tabs_items_displayOnlyPreviews" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "tabs_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    "displayOnlyPreviews_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 31),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "title" TEXT,
    "artist" TEXT,
    "album" TEXT,
    "artwork" TEXT,
    "duration" REAL,
    CHECK(("title" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("artist" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("album" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("artwork" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("duration" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    PRIMARY KEY("singleton","sections_key","tabs_key","items_key","displayOnlyPreviews_key"),
    FOREIGN KEY("singleton","sections_key","tabs_key","items_key") REFERENCES "music_home_display_sections_tabs_items"("singleton","sections_key","tabs_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_tabs_items_ranking" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "tabs_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 7),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "songArtwork" TEXT,
    "updateFrequency" TEXT,
    CHECK(("songArtwork" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("updateFrequency" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    PRIMARY KEY("singleton","sections_key","tabs_key","items_key"),
    FOREIGN KEY("singleton","sections_key","tabs_key","items_key") REFERENCES "music_home_display_sections_tabs_items"("singleton","sections_key","tabs_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_tabs_items_ranking_preview" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "tabs_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    "preview_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 63),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "id" TEXT,
    "title" TEXT,
    "artist" TEXT,
    "artwork" TEXT,
    "rank" INTEGER,
    "lastRank" INTEGER,
    CHECK(("id" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("title" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("artist" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("artwork" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("rank" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("lastRank" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    PRIMARY KEY("singleton","sections_key","tabs_key","items_key","preview_key"),
    FOREIGN KEY("singleton","sections_key","tabs_key","items_key") REFERENCES "music_home_display_sections_tabs_items_ranking"("singleton","sections_key","tabs_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_tabs_items_content" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "tabs_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 31),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "kind" TEXT,
    "record" TEXT,
    CHECK(("kind" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("record" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    PRIMARY KEY("singleton","sections_key","tabs_key","items_key"),
    FOREIGN KEY("singleton","sections_key","tabs_key","items_key") REFERENCES "music_home_display_sections_tabs_items"("singleton","sections_key","tabs_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_tabs_items_content_collection" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "tabs_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 255),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "platform" TEXT,
    "nativeID" TEXT,
    "kind" TEXT,
    "title" TEXT,
    "creator" TEXT,
    "artwork" TEXT,
    "count" INTEGER,
    "playCount" INTEGER,
    CHECK(("platform" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("nativeID" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("kind" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("title" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("creator" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("artwork" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("count" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("playCount" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    PRIMARY KEY("singleton","sections_key","tabs_key","items_key"),
    FOREIGN KEY("singleton","sections_key","tabs_key","items_key") REFERENCES "music_home_display_sections_tabs_items_content"("singleton","sections_key","tabs_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_tabs_items_content_channel" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "tabs_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 31),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "id" TEXT,
    "title" TEXT,
    "kind" TEXT,
    "pageCode" TEXT,
    "platform" TEXT,
    CHECK(("id" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("title" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("kind" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("pageCode" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("platform" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    PRIMARY KEY("singleton","sections_key","tabs_key","items_key"),
    FOREIGN KEY("singleton","sections_key","tabs_key","items_key") REFERENCES "music_home_display_sections_tabs_items_content"("singleton","sections_key","tabs_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_tabs_items_content_action" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "tabs_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 8191),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "type" TEXT,
    "provider" TEXT,
    "mode" TEXT,
    "submode" TEXT,
    "entry" TEXT,
    "ruleId" TEXT,
    "tabTagId" TEXT,
    "sourceName" TEXT,
    "sourceType" TEXT,
    "branch" TEXT,
    "songId" TEXT,
    CHECK(("type" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    CHECK(("provider" IS NULL)=((shape_mask & 2)=0 OR (null_mask & 2)<>0)),
    CHECK(("mode" IS NULL)=((shape_mask & 4)=0 OR (null_mask & 4)<>0)),
    CHECK(("submode" IS NULL)=((shape_mask & 8)=0 OR (null_mask & 8)<>0)),
    CHECK(("entry" IS NULL)=((shape_mask & 16)=0 OR (null_mask & 16)<>0)),
    CHECK(("ruleId" IS NULL)=((shape_mask & 32)=0 OR (null_mask & 32)<>0)),
    CHECK(("tabTagId" IS NULL)=((shape_mask & 64)=0 OR (null_mask & 64)<>0)),
    CHECK(("sourceName" IS NULL)=((shape_mask & 128)=0 OR (null_mask & 128)<>0)),
    CHECK(("sourceType" IS NULL)=((shape_mask & 256)=0 OR (null_mask & 256)<>0)),
    CHECK(("branch" IS NULL)=((shape_mask & 512)=0 OR (null_mask & 512)<>0)),
    CHECK(("songId" IS NULL)=((shape_mask & 1024)=0 OR (null_mask & 1024)<>0)),
    PRIMARY KEY("singleton","sections_key","tabs_key","items_key"),
    FOREIGN KEY("singleton","sections_key","tabs_key","items_key") REFERENCES "music_home_display_sections_tabs_items_content"("singleton","sections_key","tabs_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_tabs_items_content_action_sourceIds" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "tabs_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    "sourceIds_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("singleton","sections_key","tabs_key","items_key","sourceIds_key"),
    FOREIGN KEY("singleton","sections_key","tabs_key","items_key") REFERENCES "music_home_display_sections_tabs_items_content_action"("singleton","sections_key","tabs_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE TABLE "music_home_display_sections_tabs_items_content_action_ids" (
    "singleton" TEXT NOT NULL,
    "sections_key" TEXT NOT NULL,
    "tabs_key" TEXT NOT NULL,
    "items_key" TEXT NOT NULL,
    "ids_key" TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal>=0),
    shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND 1),
    null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0),
    "value" TEXT,
    CHECK(("value" IS NULL)=((shape_mask & 1)=0 OR (null_mask & 1)<>0)),
    PRIMARY KEY("singleton","sections_key","tabs_key","items_key","ids_key"),
    FOREIGN KEY("singleton","sections_key","tabs_key","items_key") REFERENCES "music_home_display_sections_tabs_items_content_action"("singleton","sections_key","tabs_key","items_key") ON DELETE CASCADE
) STRICT;

CREATE UNIQUE INDEX music_source_identity ON music_song_sources(sources_key);

CREATE INDEX music_song_lookup ON music_songs(lookup_key);

CREATE INDEX music_song_query ON music_songs(search_query);

CREATE INDEX music_song_fingerprint ON music_songs(fingerprint,baseline_duration);

CREATE UNIQUE INDEX music_local_file ON music_songs(localFileId) WHERE localFileId IS NOT NULL AND localFileId<>'';

CREATE INDEX music_song_artwork ON music_songs(cover_url);

CREATE TRIGGER music_baseline_immutable BEFORE UPDATE OF baselineDuration_kind,baselineDuration_value ON music_songs WHEN OLD.baselineDuration_value IS NOT NULL AND (OLD.baselineDuration_kind IS NOT NEW.baselineDuration_kind OR OLD.baselineDuration_value IS NOT NEW.baselineDuration_value) BEGIN SELECT RAISE(ABORT,'immutable recording baseline'); END;
