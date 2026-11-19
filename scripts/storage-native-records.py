"""Fixed Windows owner declarations; third-party definitions use declared extension trees."""
import importlib.util
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("music_records", ROOT / "scripts/storage-music-records.py")
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)
start = len(m.RECORDS)
groups = []

def record(name, fields="", keys=None, identity=None, extensions=False):
    return m.Record(name, keys or ["scope"], fields, identity=identity, extensions=extensions, prefix="windows_")

def group(path, namespace, name, exports):
    groups.append((path, namespace, name, exports))

appearance = record("app_appearance", "schema:i themeColor:t")
window = record("app_window", "schema:i closeBehavior:t")
controls = record("app_media_controls", "schema:i globalEnabled:b systemMediaEnabled:b")
bindings = m.relation(controls, "bindings", "array", "action:t", identity="action")
for name in ("local", "global"):
    m.relation(bindings, name, "object", "key:t modifiers:i")
group("AppHost/AppStorageRecords.cs", "MeowFilm.AppHost", "AppStorageRecords",
      {"Appearance": appearance, "Window": window, "MediaControls": controls})

files = record("file_grants", "Schema:i")
m.relation(files, "Items", "array", "Path:t Title:t", identity="Id")
group("Platform/Files/FileGrantRecords.cs", "MeowFilm.Platform.Files", "FileGrantRecords", {"Files": files})

tmdb = record("tmdb", "version:i")
m.relation(tmdb, "settings", "object", "apiBase:t imageBase:t apiKey:t accessToken:t language:t region:t includeAdult:b")
douban = record("douban", "version:i")
m.relation(douban, "settings", "object", "apiBase:t searchBase:t imageBase:t apiProxy:t imageProxy:t cookie:t")
rules = record("film_rules", "version:i")
rule = m.relation(rules, "rules", "object")
for name in ("episodeCleanRegexRules", "aggregateRegexRules"):
    m.values(rule, name)
for name in ("episodeRules", "movieRules"):
    m.relation(rule, name, "array", "pattern:t replace:t flags:t")
preferences = record("film_preferences", "version:i")
prefs = m.relation(preferences, "preferences", "object", "iosAutoPrepare:b metadataViewProvider:t")
for name in ("ruleOrder", "panOrder", "keywords", "siteCleanKeywords"):
    m.values(prefs, name)
cat = record("cat_settings", "version:i port:i")
m.values(cat, "providers", kind="b", dictionary=True)
mapping = m.relation(cat, "mappings", "array", "provider:t")
m.values(mapping, "aliases")
m.relation(cat, "scripts", "array", "name:t sourceType:t sourceUrl:t localPath:t localHash:t enabled:b inputRevision:i", identity="installationId")
source = record("film_sources", "version:i")
cats = m.relation(source, "cats", "array", "name:t sourceType:t sourceUrl:t enabled:b importedAt:i updatedAt:i updateState:t updateError:t sourceHash:t inputRevision:i", identity="installationId")

def site(parent, name):
    row = m.relation(parent, name, "array", "name:t api:t", identity="key")
    m.values(row, "actions")
    m.values(row, "methods", dictionary=True)
    return row
site(cats, "sites")
videos = m.relation(source, "videos", "array", "name:t api:t order:i")
m.relation(videos, "key", "object", "installationId:t siteKey:t")
m.values(videos, "actions")
m.values(videos, "methods", dictionary=True)
m.relation(videos, "options", "object", "enabled:b homeVisible:b searchEnabled:b aggregateImages:b")
detection = m.relation(videos, "detection", "object", "result:t checkedAt:i playbackResolved:b")
m.relation(detection, "steps", "array", "operation:t state:t error:t")
m.relation(source, "deleted", "array", "installationId:t siteKey:t")
favorites = record("film_library", "version:i")
favorite = m.relation(favorites, "favorites", "array", "id:t title:t poster:t score:t textBadge:t subtitle:t", identity="favoriteId")
request = m.relation(favorite, "playbackContext", "object")
target = m.relation(request, "target", "object", "id:t kind:t title:t metadataProvider:t metadataId:t year:i season:i episode:i globalEpisode:i")
m.relation(target, "source", "object", "key:t engine:t installationId:t siteId:n detailId:n lineId:n")
m.relation(target, "members", "array", "sourceKey:t detailId:n")
group("Capabilities/Film/FilmStorageRecords.cs", "MeowFilm.Capabilities", "FilmStorageRecords", {
    "TMDB": tmdb, "Douban": douban, "Rules": rules, "Preferences": preferences,
    "Cat": cat, "Sources": source, "Favorites": favorites, "Favorite": favorite})

novel = record("novel_library", "version:i")
entry = m.relation(novel, "entries", "array", "identityKey:t onShelf:b addedAt:t pinnedAt:t readAt:t chapterCount:i contentSignature:t", identity="bookId")
m.relation(entry, "book", "object", "installationId:t revision:i bookUrl:t name:t author:t coverUrl:t intro:t tocUrl:t latestChapterTitle:t", extensions=True)
m.relation(entry, "anchor", "object", "chapterUrl:t chapterTitle:t chapterIndex:i blockIndex:i utf16Offset:i ratio:r")
novel_appearance = m.relation(novel, "appearance", "object", "mode:t fontSize:r fontFamily:t bold:b lineHeight:r paragraphSpacing:r margin:r contentWidth:r conversion:t theme:i background:t foreground:t backgroundImage:t autoSpeed:r")
novel_rule = m.relation(novel, "rules", "array", "name:t group:t pattern:t replacement:t isRegex:b isEnabled:b scopeTitle:b scopeContent:b scope:t excludeScope:t order:i timeoutMillisecond:i", identity="id", extensions=True)
novel_sources = record("novel_sources", "version:i revision:i")
m.relation(novel_sources, "sources", "array", "revision:i name:t sourceUrl:t group:t order:i enabled:b enabledExplore:b protocol:t", identity="installationId", extensions=True)
group("Capabilities/Novel/NovelStorageRecords.cs", "MeowFilm.Capabilities", "NovelStorageRecords",
      {"Library": novel, "Entry": entry, "Appearance": novel_appearance, "Rule": novel_rule, "Sources": novel_sources})

feedback = record("feedback", "version:i revision:i")
m.values(feedback, "enabled", unique=True)
outbox = record("playback_outbox", "version:i")
playbacks = m.relation(outbox, "playbacks", "array", "consentRevision:i recordingId:t source:t sourceId:t algorithm:t startConfirmed:b playScene:t", identity="playbackId")
m.relation(playbacks, "account", "object", "scope:t generation:i userId:t")
for name in ("start", "end"):
    m.relation(playbacks, name, "object", "id:t at:i inflight:b time:r realtime:r reason:t rate:r")
group("Capabilities/Feedback/FeedbackStorageRecords.cs", "MeowFilm.Capabilities.Feedback", "FeedbackStorageRecords", {"Settings": feedback, "Outbox": outbox})

fm = record("fm_preference", "mode:t submode:t ruleId:t tabTagId:t", keys=["account_id"])
group("Capabilities/Recommendations/FMStorageRecords.cs", "MeowFilm.Capabilities.Recommendations", "FMStorageRecords", {"Selection": fm})

account = record("music_account", "version:i generation:i cookie:t scope:t validatedAt:i", keys=["platform"])
m.relation(account, "profile", "object", "platform:t userId:t displayName:t avatar:t")
group("Capabilities/Accounts/AccountStorageRecords.cs", "MeowFilm.Capabilities.Accounts", "AccountStorageRecords", {"Account": account})

chorus = record("chorus", "at:i start:r end:r", keys=["resource_key"])
playback_source = record("playback_source", "signature:t at:i until:i storedBytes:i", keys=["account_scope", "song_id", "entry_kind"])
info = m.relation(playback_source, "info", "object", "ok:b kind:t platform:t songId:t sourceKey:t scope:t generation:i quality:t actualQuality:t qualityVerified:b playbackVerified:b qualityPolicySignature:t qualityCacheSignature:t url:t expiresAt:i duration:r size:i full:b delivery:t requestedQuality:t")
m.values(info, "headers", dictionary=True)

def copy_source(parent, name, template):
    fields = " ".join(field + ":" + kind for field, kind in template.fields)
    if template.identity:
        fields += " " + template.identity + ":t"
    child = m.relation(parent, name, "object", fields, extensions=template.extensions)
    for member, kind, nested in template.relations:
        copy_relation(child, member, kind, nested)
    return child

def copy_relation(parent, name, kind, template):
    fields = " ".join(field + ":" + value for field, value in template.fields)
    child = m.relation(parent, name, kind, fields, identity=template.identity, extensions=template.extensions)
    for member, nested_kind, nested in template.relations:
        copy_relation(child, member, nested_kind, nested)
    return child

copy_source(info, "recording", m.sources)
lyrics = record("lyrics", "sourceKey:t updatedAt:i storedBytes:i", keys=["song_id"])
lines = m.relation(lyrics, "lines", "array", "time:r text:t translation:t romanization:t end:r")
m.relation(lines, "words", "array", "time:r text:t duration:r offset:r")
group("Capabilities/PlaybackCache/PlaybackCacheRecords.cs", "MeowFilm.Capabilities.PlaybackCache", "PlaybackCacheRecords",
      {"Chorus": chorus, "Sources": playback_source, "Lyrics": lyrics})

details = record("account_details", "accountGeneration:i accountScope:t accountFingerprint:t userId:t profileFetchedAt:i profileExpiresAt:i", keys=["platform"])
m.relation(details, "profile", "object", "platform:t userId:t displayName:t avatar:t")
rights = m.relation(details, "rights", "object", "vip:b tier:t expiresAt:i checkedAt:i recheckAt:i verificationDeferred:b errorCode:t retryAt:i")
m.values(rights, "audioLevels")
group("Capabilities/Accounts/AccountDetailsRecords.cs", "MeowFilm.Capabilities.Accounts", "AccountDetailsRecords", {"Details": details})

home = record("home", "platform:t")
pages = m.relation(home, "pages", "array", "platform:t")

def action(parent, name):
    row = m.relation(parent, name, "object", "type:t id:n branch:t pageCode:t provider:t mode:t submode:t ruleId:t tabTagId:t sourceType:t sourceName:t entry:t songId:t")
    m.relation(row, "channel", "object", "id:n name:t pageCode:t channelKind:t placement:t")
    m.values(row, "sourceIds")
    m.values(row, "ids")
    locator(row)

def locator(parent):
    m.relation(parent, "locator", "object", "kind:t id:n playlistId:n dirId:n listId:n version:n totalVersion:n creatorId:n creatorListId:n")

def cards(parent, name):
    row = m.relation(parent, name, "array", "songId:t id:n key:n name:t title:t label:t subtitle:t contextText:t creator:t cover:t coverLabel:t kind:t pageCode:t channelKind:t available:b errorCode:t requiresAccount:b display:b placement:t canPlay:b rankText:t salesText:t relationship:t count:i playCount:i presentation:t dayOfMonth:i decoration:t updateFrequency:t")
    m.relation(row, "song", "object", "songId:t")
    action(row, "action")
    action(row, "playbackSource")
    locator(row)
    m.relation(row, "coverRef", "object", "kind:t id:n")
    preview = m.relation(row, "preview", "array", "title:t artist:t cover:t rank:i lastRank:i")
    m.relation(preview, "song", "object", "songId:t")
    return row

sections = m.relation(pages, "sections", "array", "id:n title:t type:t layout:t available:b errorCode:t requiresAccount:b display:b selectedTab:t selectedTag:t")
action(sections, "action")
cards(sections, "items")
tabs = m.relation(sections, "tabs", "array", "id:n name:t")
cards(tabs, "items")
m.relation(sections, "tags", "array", "id:n name:t group:t selected:b")
group("Capabilities/Catalog/HomeStorageRecords.cs", "MeowFilm.Capabilities", "HomeStorageRecords", {"Home": home})

audio = record("audio_index", "file:t size:i sha256:t signature:t confirmedAt:i duration:r", keys=["resource_key"], identity="resourceKey")
m.relation(audio, "info", "object", "kind:t platform:t songId:t sourceKey:t scope:t generation:i installationId:t providerName:t quality:t requestedQuality:t actualQuality:t qualityVerified:b playbackVerified:b qualityPolicySignature:t qualityCacheSignature:t duration:r size:i full:b delivery:t qualityEvidence:t codec:t sampleRate:i bitDepth:i channels:i bitrate:i")
group("Capabilities/PlaybackCache/AudioStorageRecords.cs", "MeowFilm.Capabilities.PlaybackCache", "AudioStorageRecords", {"Index": audio})
match = record("source_match", "storedBytes:i", keys=["song_id"], identity="songId")
m.relation(match, "platforms", "dictionary", "state:t checkedAt:i context:t query:t code:t matchedAt:i")
m.values(match, "excluded", unique=True)
group("Capabilities/Library/MatchStorageRecords.cs", "MeowFilm.Capabilities.Library", "MatchStorageRecords", {"Entry": match})

def generate(check=False):
    products = {ROOT / "packages/storage/schema/43_windows_owners.sql": m.sql(m.RECORDS[start:])}
    for path, namespace, name, exports in groups:
        target = ROOT / "apps/windows/src" / ("MeowFilm." + path)
        products[target] = m.declarations("csharp", roots=list(exports.values()), exports=exports, namespace=namespace, class_name=name)
    for path, source in products.items():
        if check:
            if not path.is_file() or path.read_text(encoding="utf-8") != source:
                raise SystemExit(f"generated native storage differs: {path.relative_to(ROOT)}")
        else:
            path.write_text(source, encoding="utf-8")
