"""Closed native music tables/projections, generated for Swift and .NET.

These are repository declarations, not a runtime schema or an arbitrary payload
store. Only third-party song metadata has explicitly declared extension nodes.
"""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
RECORDS = []
AREAS = {}


class Record:
    def __init__(self, name, keys, fields="", relations=(), identity=None, extensions=False, prefix="music_"):
        self.prefix = prefix
        self.name = prefix + name
        self.keys = keys
        self.fields = [tuple(field.split(":")) for field in fields.split()]
        self.relations = list(relations)
        self.identity = identity
        self.extensions = extensions
        RECORDS.append(self)


def relation(parent, name, kind, fields="", children=(), identity=None, extensions=False, table=None):
    keys = parent.keys + ([] if kind == "object" else [name + "_key"])
    child = Record(table or parent.name.removeprefix(parent.prefix) + "_" + name, keys,
                   fields, children, identity, extensions, prefix=parent.prefix)
    parent.relations.append((name, kind, child))
    return child


def values(parent, name, kind="t", dictionary=False, table=None, unique=False):
    return relation(parent, name, "mapValues" if dictionary else "values", "value:" + kind,
                    table=table, identity="value" if unique else None)


def context(parent, name):
    row = relation(parent, name, "object",
                   "collectionId:t playlistId:t sourceTitle:t playlist:t sourceType:t sourceId:t "
                   "sourcePlatform:t platform:t sourceLabel:t searchKeyword:t dailyBranch:t "
                   "heartbeatPlaylistId:t heartbeatAccountId:t provider:t remoteSession:t")
    relation(row, "queueSource", "object", "id:t title:t kind:t platform:t")
    relation(row, "similar", "object", "batchId:t basedOnSongId:t provider:t")
    return row


def source(parent):
    return relation(parent, "source", "object",
                    "id:t title:t kind:t platform:t sourceType:t sourceTitle:t sourceId:t playlist:t "
                    "seedEntryId:t remoteSession:t dailyBranch:t searchKeyword:t")


def queue_rows(parent):
    entries = relation(parent, "entries", "array", "songId:t", identity="id")
    context(entries, "context")
    for name in ("priority", "history"):
        values(parent, name)
    source(parent)


def status(parent, name):
    row = relation(parent, name, "object", "phase:t at:r code:t")
    relation(row, "issues", "array", "platform:t collectionID:t collectionId:t code:t")
    return row


# Permanent song/source identity. Native platform locators use declared scalar
# unions because upstream APIs legitimately return either numeric or textual IDs.
song = Record("songs", ["song_id"],
              "title:t artist:t album:t duration:n baselineDuration:n cover_url:t localFileId:t "
              "artworkSourceKey:t artworkSourceImage:t", identity="songId", extensions=True)
values(song, "artistNames")
sources = relation(song, "sources", "array", "source:t", identity="sourceKey", table="song_sources", extensions=True)
info = relation(sources, "musicInfo", "object",
                "source:t songmid:n mid:n id:n songId:n qqSongId:n songType:n type:n copyrightId:n "
                "strMediaMid:n hash:t albumId:n album_id:n albumMid:n albumAudioId:n mixsongid:n fileid:n "
                "fileId:n name:t singer:t albumName:t duration:n size:n contentId:n md5:t img:t qualityKnown:b",
                table="source_info", extensions=True)
values(info, "artistNames")
relation(info, "types", "array", "type:n hash:t size:n")
relation(info, "_types", "dictionary", "hash:t size:n")
format_fields = ("formatType:n resourceType:n size:n androidSize:n iosSize:n asize:n isize:n format:n "
                 "androidFormat:n iosFormat:n aformat:n iformat:n bitrate:n sampleRate:n bitDepth:n fileType:n "
                 "androidFileType:n iosFileType:n iosAccuracyLevel:n androidAccuracyLevel:n hash:t md5:t "
                 "androidMd5:t fileId:n contentId:n")
for name in ("audioFormats", "newRateFormats"):
    relation(info, name, "array", format_fields)

library = Record("library", ["scope"], "schema:i scope:t revision:i")
lib = relation(library, "library", "object", "version:i ownerId:t revision:i nextId:i")
collections = relation(lib, "collections", "array",
                       "name:t kind:t revision:i manualOrder:b deleted:b locallyCreated:b", identity="id", table="collections")
values(collections, "order", table="collection_members", unique=True)
bindings = relation(collections, "bindings", "array", "platform:t accountId:t id:t writable:b name:t")
values(bindings, "baseline")
values(bindings, "map", dictionary=True)
intents = relation(collections, "intents", "dictionary", "present:b pending:b origin:t", table="collection_intents")
values(intents, "acknowledged")
values(lib, "accounts", dictionary=True)
status(lib, "status")
for name in ("networkReferences", "networkPlaylists"):
    refs = relation(lib, name, "array",
                    "platform:t remoteID:t remoteId:t name:t creator:t artwork:t cover:t count:i revision:i present:b pending:b",
                    identity="id")
    relation(refs, "relation", "object", "accountId:t baseline:b relationID:t locator:t")

sync = relation(library, "sync", "object", "schema:i phase:t")
relation(sync, "selections", "array", "platform:t favorites:b playlists:b")
values(sync, "scopes", dictionary=True)
relation(sync, "issues", "array", "platform:t collectionID:t code:t")
automatic = relation(sync, "automatic", "object", "revision:i intentRevision:i settledIntentRevision:i orderOnly:t")
relation(automatic, "selections", "array", "platform:t favorites:b playlists:b")
relation(automatic, "bindings", "dictionary", "accountID:t scope:t")
values(automatic, "relationshipPlatforms")
fences = relation(sync, "fences", "array",
                  "platform:t accountID:t scope:t localID:t localRevision:i acknowledgedID:t acknowledgedName:t")
mutation = relation(fences, "mutation", "object", "id:t action:t collectionID:t name:t directoryRevision:t")
for name in ("members",):
    relation(mutation, name, "array", "nativeID:t occurrenceID:t")
before = relation(mutation, "before", "object",
                  "id:t name:t kind:t writable:b canRename:b canDelete:b canOrder:b revision:t")
relation(before, "members", "array", "nativeID:t occurrenceID:t")
relation(mutation, "beforeSubscription", "object", "remoteID:t relationID:t name:t creator:t artwork:t count:i")

queue = Record("queue", ["scope"], "schema:i currentId:t serial:i revision:i runId:t")
queue_rows(queue)
values(queue, "failed")
similar = relation(queue, "similar", "object",
                   "schema:i seedSongId:t seedTitle:t seedEntryId:t fromHome:b provider:t continue:b offset:i "
                   "exhausted:b scope:t status:t")
return_queue = relation(similar, "returnQueue", "object", "currentId:t")
queue_rows(return_queue)
values(similar, "sourceIds")
values(similar, "attempted")
relation(similar, "providerContext", "object", "pid:n sid:n count:i")
batches = relation(similar, "batches", "array", "basedOnSongId:t basedOnTitle:t provider:t", identity="id")
values(batches, "entryIds")

history = Record("history", ["scope"], "schema:i")
archive = relation(history, "playlists", "array", "at:r title:t currentId:t", identity="id", table="history_playlists")
queue_rows(archive)
context(archive, "context")
days = relation(history, "days", "array", "date:t at:r id:t")
day_songs = relation(days, "songs", "array", "songId:t at:r", identity="songId")
context(day_songs, "context")

playback = Record("playback", ["scope"], "schema:i revision:i mode:i lyricsEnabled:b showTranslation:b")
quality = relation(playback, "qualitySettings", "object", "request:t")
for name in ("qualityOrder", "qualityEnabled", "spatialOrder", "spatialEnabled"):
    values(quality, name)
for name in ("officialSources", "metadataPlatforms", "metadataEnabled"):
    values(playback, name)
relation(playback, "position", "object", "occurrenceId:t seconds:r")
for name in ("sourceExclusions", "recordingSourceExclusions"):
    relation(playback, name, "array", "songID:t title:t artist:t provider:t platform:t sourceKey:t", identity="id")
catalog = Record("catalog", ["scope"], "schema:i revision:i platform:t")
for name, row in (("library", library), ("queue", queue), ("history", history), ("playback", playback), ("catalogSource", catalog)):
    AREAS[name] = row

# Windows' native settings are separate business owners, not aliases to iOS.
def area(name, table, fields):
    row = Record(table, ["scope"], fields)
    AREAS[name] = row
    return row


area("playback-settings", "windows_playback_settings", "schema:i mode:i")
area("online-settings", "windows_online_settings", "schema:i revision:i lyrics:b")
area("native-device-settings", "windows_device_settings", "schema:i revision:i volume:i")
area("native-appearance", "windows_appearance", "schema:i revision:i playlistView:t playerTheme:t colorMode:t colorPalette:t")
area("native-catalog-source", "windows_catalog", "schema:i revision:i platform:t")
desktop = area("native-desktop-lyrics", "windows_desktop_lyrics", "schema:i revision:i layout:t normalColor:t highlightColor:t")
relation(desktop, "geometry", "object", "monitor:t x:r y:r width:r height:r")
quality = area("native-quality-policy", "windows_quality", "schema:i revision:i request:t")
for name in ("qualityOrder", "qualityEnabled", "spatialOrder", "spatialEnabled"):
    values(quality, name)
for area_name, table in (("native-official-sources", "windows_official_sources"),
                         ("native-metadata-policy", "windows_metadata"),
                         ("native-search-policy", "windows_search")):
    row = area(area_name, table, "schema:i revision:i")
    for name in ("platforms", "enabled"):
        values(row, name)
search = area("native-search-history", "windows_search_history", "schema:i")
values(search, "items")
exclusions = area("native-source-exclusions", "windows_exclusions", "schema:i revision:i")
relation(exclusions, "items", "array", "songId:t provider:t platform:t providerName:t title:t artist:t at:i")
lyrics = area("native-local-lyrics", "windows_local_lyrics", "schema:i")
rows = relation(lyrics, "items", "array", "songId:t contentHash:t")
lines = relation(rows, "lines", "array", "time:r text:t translation:t romanization:t end:r")
relation(lines, "words", "array", "time:r text:t duration:r offset:r")
windows_sync = area("library-sync", "windows_sync", "schema:i revision:i")
relation(windows_sync, "platforms", "dictionary", "favorites:b playlists:b accountId:t")
pending = relation(windows_sync, "pending", "object",
                   "platform:t accountId:t collectionId:t remoteId:t operation:t beforePresent:b desiredPresent:b name:t")
for name in ("before", "desired", "ids", "directory"):
    values(pending, name)
relation(pending, "locator", "object", "relationId:t relationID:t id:t remoteId:t")
for name in ("confirmation", "approved"):
    row = relation(windows_sync, name, "object", "id:t code:t platform:t count:i digest:t signature:t")
    values(row, "collections")
status(windows_sync, "status")

# Native service caches keep explicit, finite display/evidence projections.
discovery = Record("source_discovery", ["song_id"], "storedBytes:i")
platforms = relation(discovery, "platforms", "dictionary", "checkedAt:r")
values(platforms, "sourceKeys", unique=True)
relation(platforms, "observations", "array",
         "requestID:t startedAt:r order:i checkedAt:r platform:t query:t context:t page:i limit:i scope:t outcome:t failureCode:t")

home = Record("home_display", ["singleton"], "source:t")
sections = relation(home, "sections", "array",
                    "title:t layout:t selectedFilter:t selectedTab:t", identity="id")
values(sections, "avatars")
relation(sections, "channel", "object", "id:t title:t kind:t pageCode:t platform:t")
relation(sections, "filters", "array", "title:t group:t", identity="id")
tabs = relation(sections, "tabs", "array", "title:t", identity="id")
for parent in (sections, tabs):
    items = relation(parent, "items", "array",
                     "title:t subtitle:t artwork:t recommendationReason:t coverLabel:t requiresAccount:b", identity="id")
    values(items, "previewSongIDs")
    ranking = relation(items, "ranking", "object", "songArtwork:t updateFrequency:t")
    relation(ranking, "preview", "array", "id:t title:t artist:t artwork:t rank:i lastRank:i")
    content = relation(items, "content", "object", "kind:t record:t")
    relation(content, "collection", "object", "platform:t nativeID:t kind:t title:t creator:t artwork:t count:i")
    relation(content, "channel", "object", "id:t title:t kind:t pageCode:t platform:t")
    action = relation(content, "action", "object",
                      "type:t provider:t mode:t submode:t entry:t ruleId:t tabTagId:t sourceName:t sourceType:t branch:t songId:t")
    values(action, "sourceIds")
    values(action, "ids")


def quote(name):
    return '"' + name.replace('"', '""') + '"'


def column(row, name):
    if name in row.keys and name != "scope":
        return name + "_field"
    # SQLite identifiers are case-insensitive; upstream property identities are
    # not (e.g. fileid/fileId). Give those declared fields distinct SQL names.
    return name + "_camel" if any(other != name and other.lower() == name.lower() for other, _ in row.fields) \
        and not name.endswith("ID") and any(char.isupper() for char in name) else name


def sql(records=None):
    records = RECORDS if records is None else records
    out = ["-- Generated closed columns/relations; no runtime payload columns.\n"]
    if song in records:
        out.append("CREATE TABLE music_owner_state (scope TEXT PRIMARY KEY NOT NULL, revision INTEGER NOT NULL "
                   "CHECK(revision BETWEEN 0 AND 9007199254740991), receipt TEXT NOT NULL) STRICT;\n")
    parents = {child.name: parent for parent in records for _, _, child in parent.relations}
    for row in records:
        # A scope key and its projected scope field are distinct (scope_key).
        keys = [key + "_key" if key == "scope" else key for key in row.keys]
        columns = [f"{quote(key)} TEXT NOT NULL" for key in keys]
        checks = []
        columns += ["ordinal INTEGER NOT NULL CHECK(ordinal>=0)",
                    f"shape_mask INTEGER NOT NULL CHECK(shape_mask BETWEEN 0 AND {(1 << (len(row.fields) + len(row.relations))) - 1})",
                    "null_mask INTEGER NOT NULL CHECK(null_mask>=0 AND (null_mask & ~shape_mask)=0)"]
        for index, (name, kind) in enumerate(row.fields):
            name = column(row, name)
            first = quote(name + "_kind" if kind == "n" else name)
            checks += [f"CHECK(({first} IS NULL)=((shape_mask & {1 << index})=0 OR (null_mask & {1 << index})<>0))"]
            if kind == "n":
                columns += [f"{quote(name + '_kind')} TEXT CHECK({quote(name + '_kind')} IN ('text','number'))",
                            f"{quote(name + '_value')} TEXT"]
                checks += [f"CHECK(({quote(name + '_kind')} IS NULL)=({quote(name + '_value')} IS NULL))"]
            else:
                columns += [f"{quote(name)} " + {"t": "TEXT", "i": "INTEGER", "b": "INTEGER", "r": "REAL", "d": "BLOB"}[kind] +
                            (f" CHECK({quote(name)} IN (0,1))" if kind == "b" else "")]
        if row is song:
            columns += ["lookup_key TEXT", "search_query TEXT", "fingerprint TEXT",
                        "baseline_duration REAL CHECK(baseline_duration>0)"]
        columns += checks
        if row is discovery:
            columns += ["FOREIGN KEY(song_id) REFERENCES music_songs(song_id)"]
        for name, kind in row.fields:
            if kind == "t" and name in ("songId", "songID", "seedSongId", "basedOnSongId") and \
                    not row.name.startswith("music_home_display"):
                columns += [f"FOREIGN KEY({quote(column(row, name))}) REFERENCES music_songs(song_id)"]
        if row.name == "music_collection_members" or row.name == "music_collections_bindings_map":
            columns += ["FOREIGN KEY(value) REFERENCES music_songs(song_id)"]
        if row.name == "music_collection_intents":
            columns += ["FOREIGN KEY(intents_key) REFERENCES music_songs(song_id)"]
        columns += ["PRIMARY KEY(" + ",".join(map(quote, keys)) + ")"]
        if row.name in parents:
            parent = parents[row.name]
            parent_keys = [key + "_key" if key == "scope" else key for key in parent.keys]
            columns += ["FOREIGN KEY(" + ",".join(map(quote, parent_keys)) + ") REFERENCES " + quote(parent.name) +
                        "(" + ",".join(map(quote, parent_keys)) + ") ON DELETE CASCADE"]
        out.append("CREATE TABLE " + quote(row.name) + " (\n    " + ",\n    ".join(columns) + "\n) STRICT;\n")
        if row.extensions:
            nodes = [f"{quote(key)} TEXT NOT NULL" for key in keys]
            nodes += ["node_id INTEGER NOT NULL CHECK(node_id>=0)", "parent_id INTEGER", "prop_key TEXT", "item_index INTEGER",
                      "kind TEXT NOT NULL CHECK(kind IN ('null','boolean','integer','real','number_text','text','array','object'))",
                      "bool_value INTEGER CHECK(bool_value IN (0,1))", "int_value INTEGER", "real_value REAL", "text_value TEXT",
                      "PRIMARY KEY(" + ",".join(map(quote, keys + ["node_id"])) + ")",
                      "FOREIGN KEY(" + ",".join(map(quote, keys)) + ") REFERENCES " + quote(row.name) +
                      "(" + ",".join(map(quote, keys)) + ") ON DELETE CASCADE"]
            out.append("CREATE TABLE " + quote(row.name + "_extensions") + " (\n    " + ",\n    ".join(nodes) + "\n) STRICT;\n")
    if song in records:
        out += [
        "CREATE UNIQUE INDEX music_source_identity ON music_song_sources(sources_key);\n",
        "CREATE INDEX music_song_lookup ON music_songs(lookup_key);\n",
        "CREATE INDEX music_song_query ON music_songs(search_query);\n",
        "CREATE INDEX music_song_fingerprint ON music_songs(fingerprint,baseline_duration);\n",
        "CREATE UNIQUE INDEX music_local_file ON music_songs(localFileId) WHERE localFileId IS NOT NULL AND localFileId<>'';\n",
        "CREATE INDEX music_song_artwork ON music_songs(cover_url);\n",
        "CREATE TRIGGER music_baseline_immutable BEFORE UPDATE OF baselineDuration_kind,baselineDuration_value "
        "ON music_songs WHEN OLD.baselineDuration_value IS NOT NULL AND "
        "(OLD.baselineDuration_kind IS NOT NEW.baselineDuration_kind OR OLD.baselineDuration_value IS NOT NEW.baselineDuration_value) "
        "BEGIN SELECT RAISE(ABORT,'immutable recording baseline'); END;\n",
        ]
    return "\n".join(out)


def declarations(language, roots=None, exports=None, namespace=None, class_name=None):
    kinds = {"t": "text", "i": "integer", "r": "real", "b": "boolean", "n": "identifier", "d": "bytes"}
    # Children must be declared before their parents.
    ordered, seen = [], set()
    def visit(row):
        if row.name in seen:
            return
        for _, _, child in row.relations:
            visit(child)
        seen.add(row.name)
        ordered.append(row)
    area_names = {"library", "queue", "history", "playback", "catalogSource"} if language == "swift" else \
        set(AREAS) - {"playback", "catalogSource"}
    if roots is None:
        roots = [song] + [AREAS[name] for name in AREAS if name in area_names]
        if language == "swift":
            roots += [discovery, home]
    for row in roots:
        visit(row)
    out = []
    for row in ordered:
        keys = [key + "_key" if key == "scope" else key for key in row.keys]
        fields = ", ".join(('.init(' + repr(name).replace("'", '"') + ", ." + kinds[kind] +
                            (f', column: "{column(row, name)}"' if column(row, name) != name else "") + ")")
                           if language == "swift" else f'new("{name}", RecordFieldKind.{kinds[kind].title()}' +
                           (f', "{column(row, name)}"' if column(row, name) != name else "") + ")"
                           for name, kind in row.fields)
        children = ", ".join(f'.init("{name}", .{kind}, {child.name}' +
                             (f', identity: "{child.identity}"' if child.identity and kind in ("array", "values") else "") + ")" if language == "swift"
                             else f'new("{name}", RecordRelationKind.{kind[0].upper() + kind[1:]}, {child.name}' +
                             (f', "{child.identity}"' if child.identity and kind in ("array", "values") else "") + ")"
                             for name, kind, child in row.relations)
        key_text = ", ".join(f'"{key}"' for key in keys)
        if language == "swift":
            optional = (f', extensionTable: "{row.name}_extensions"' if row.extensions else "") + \
                       (f', identityField: "{row.identity}"' if row.identity else "")
            out.append(f'    static let {row.name} = RuntimeRelationalRecord("{row.name}", keys: [{key_text}], '
                       f'fields: [{fields}], relations: [{children}]{optional})')
        else:
            out.append(f'    private static readonly RelationalRecord {row.name} = new("{row.name}", [{key_text}], '
                       f'[{fields}], [{children}], ' + (f'"{row.name}_extensions"' if row.extensions else "null") +
                       ", " + (f'"{row.identity}"' if row.identity else "null") + ");")
    if language == "swift":
        out.append('    static let song = music_songs')
        out.append('    static let areas: [String: RuntimeRelationalRecord] = [')
        out += [f'        "{name}": {row.name},' for name, row in AREAS.items() if name in area_names]
        out.append("    ]")
        return "// Generated by scripts/storage-music-records.py.\nimport MeowRuntime\n\nenum MusicStorageRecords {\n" + "\n".join(out) + "\n}\n"
    if exports is not None:
        out += [f'    internal static RelationalRecord {name} => {row.name};' for name, row in exports.items()]
        return ("// Generated by scripts/storage-native-records.py.\nusing MeowFilm.Platform.Storage;\n" +
                f"namespace {namespace};\n\ninternal static class {class_name}\n{{\n" + "\n".join(out) + "\n}\n")
    out.append("    internal static RelationalRecord Song => music_songs;")
    out.append("    internal static readonly IReadOnlyDictionary<string, RelationalRecord> Areas = new Dictionary<string, RelationalRecord> {")
    out += [f'        ["{name}"] = {row.name},' for name, row in AREAS.items() if name in area_names]
    out.append("    };")
    return ("// Generated by scripts/storage-music-records.py.\nusing MeowFilm.Platform.Storage;\n"
            "namespace MeowFilm.Capabilities.Library;\n\ninternal static class MusicStorageRecords\n{\n" +
            "\n".join(out) + "\n}\n")


def generate(check=False):
    products = {
        ROOT / "packages/storage/schema/30_native_music.sql": sql(),
        ROOT / "native/MeowKit/Sources/MeowMusicNativeService/Persistence/MusicStorageRecords.swift": declarations("swift"),
        ROOT / "apps/windows/src/MeowFilm.Capabilities/Library/MusicStorageRecords.cs": declarations("csharp"),
    }
    for path, source in products.items():
        if check:
            if not path.is_file() or path.read_text(encoding="utf-8") != source:
                raise SystemExit(f"generated music storage differs: {path.relative_to(ROOT)}")
        else:
            path.write_text(source, encoding="utf-8")
