package emby_service

import (
	"log"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
	metadata_tmdb "github.com/jenfonro/meowfilm/server/metadata/tmdb"
	"github.com/jenfonro/meowfilm/server/smart"
)

var embyPlaybackSwitchSessions = newPlaybackSwitchStore()

const playbackSwitchSessionTTL = time.Hour
const playbackSwitchActionTTL = 10 * time.Minute

type playbackSwitchSkipItem struct {
	Site        string
	PanFlag     string
	EpisodeFile string
}

type playbackSwitchSession struct {
	UserID            int64
	TMDBType          string
	TMDBID            int
	CurrentSite       string
	CurrentSiteDetail string
	CurrentSpiderAPI  string
	CurrentPanFlag    string
	CurrentEpisodeFile string
	SkipItems         []playbackSwitchSkipItem
	ExpireAt          time.Time
}

type playbackSwitchActionState string

const (
	playbackSwitchActionPending playbackSwitchActionState = "pending"
	playbackSwitchActionRunning playbackSwitchActionState = "running"
	playbackSwitchActionDone    playbackSwitchActionState = "done"
)

type playbackSwitchActionRecord struct {
	UserID        int64
	ItemID        string
	PlaySessionID string
	Action        string
	State         playbackSwitchActionState
	ExpireAt      time.Time
}

type playbackSwitchStore struct {
	mu   sync.Mutex
	data map[string]playbackSwitchSession
}

type playbackSwitchActionStore struct {
	mu   sync.Mutex
	data map[string]playbackSwitchActionRecord
}

func newPlaybackSwitchStore() *playbackSwitchStore {
	return &playbackSwitchStore{data: map[string]playbackSwitchSession{}}
}

func newPlaybackSwitchActionStore() *playbackSwitchActionStore {
	return &playbackSwitchActionStore{data: map[string]playbackSwitchActionRecord{}}
}

func playbackSwitchSessionKey(userID int64, tmdbType string, tmdbID int) string {
	if userID <= 0 || tmdbID <= 0 {
		return ""
	}
	typ := strings.TrimSpace(tmdbType)
	if typ == "" {
		return ""
	}
	return StableMD5Hex(strings.Join([]string{
		strings.TrimSpace(strconv.FormatInt(userID, 10)),
		typ,
		strings.TrimSpace(strconv.Itoa(tmdbID)),
	}, "|"))
}

func clonePlaybackSwitchSession(in playbackSwitchSession) playbackSwitchSession {
	out := in
	if len(in.SkipItems) > 0 {
		out.SkipItems = make([]playbackSwitchSkipItem, 0, len(in.SkipItems))
		out.SkipItems = append(out.SkipItems, in.SkipItems...)
	}
	return out
}

func (s *playbackSwitchStore) cleanupLocked(now time.Time) {
	if s == nil {
		return
	}
	for key, session := range s.data {
		if session.ExpireAt.Before(now) {
			delete(s.data, key)
		}
	}
}

func (s *playbackSwitchStore) Ensure(userID int64, tmdbType string, tmdbID int, ttl time.Duration) (playbackSwitchSession, bool) {
	if s == nil {
		return playbackSwitchSession{}, false
	}
	key := playbackSwitchSessionKey(userID, tmdbType, tmdbID)
	if key == "" {
		return playbackSwitchSession{}, false
	}
	now := time.Now()
	s.mu.Lock()
	defer s.mu.Unlock()
	s.cleanupLocked(now)
	if session, ok := s.data[key]; ok && !session.ExpireAt.Before(now) {
		return clonePlaybackSwitchSession(session), false
	}
	session := playbackSwitchSession{
		UserID:   userID,
		TMDBType: strings.TrimSpace(tmdbType),
		TMDBID:   tmdbID,
		SkipItems: []playbackSwitchSkipItem{},
		ExpireAt: now.Add(ttl),
	}
	s.data[key] = session
	return clonePlaybackSwitchSession(session), true
}

func (s *playbackSwitchStore) Get(userID int64, tmdbType string, tmdbID int) (playbackSwitchSession, bool) {
	if s == nil {
		return playbackSwitchSession{}, false
	}
	key := playbackSwitchSessionKey(userID, tmdbType, tmdbID)
	if key == "" {
		return playbackSwitchSession{}, false
	}
	now := time.Now()
	s.mu.Lock()
	defer s.mu.Unlock()
	s.cleanupLocked(now)
	session, ok := s.data[key]
	if !ok || session.ExpireAt.Before(now) {
		return playbackSwitchSession{}, false
	}
	return clonePlaybackSwitchSession(session), true
}

func (s *playbackSwitchStore) SetCurrentSource(userID int64, tmdbType string, tmdbID int, site string, siteDetail string, spiderAPI string, panFlag string, episodeFile string, ttl time.Duration) bool {
	if s == nil {
		return false
	}
	key := playbackSwitchSessionKey(userID, tmdbType, tmdbID)
	if key == "" {
		return false
	}
	now := time.Now()
	s.mu.Lock()
	defer s.mu.Unlock()
	s.cleanupLocked(now)
	session, ok := s.data[key]
	if !ok || session.ExpireAt.Before(now) {
		session = playbackSwitchSession{
			UserID:   userID,
			TMDBType: strings.TrimSpace(tmdbType),
			TMDBID:   tmdbID,
		}
	}
	session.CurrentSite = strings.TrimSpace(site)
	session.CurrentSiteDetail = strings.TrimSpace(siteDetail)
	session.CurrentSpiderAPI = strings.TrimSpace(spiderAPI)
	session.CurrentPanFlag = strings.TrimSpace(panFlag)
	session.CurrentEpisodeFile = normalizePlaybackSwitchEpisodeFile(episodeFile)
	session.ExpireAt = now.Add(ttl)
	s.data[key] = session
	return true
}

func (s *playbackSwitchStore) AddSkipItem(userID int64, tmdbType string, tmdbID int, site string, panFlag string, episodeFile string, ttl time.Duration) bool {
	if s == nil {
		return false
	}
	site = strings.TrimSpace(site)
	panFlag = strings.TrimSpace(panFlag)
	episodeFile = normalizePlaybackSwitchEpisodeFile(episodeFile)
	if panFlag == "" || episodeFile == "" {
		return false
	}
	key := playbackSwitchSessionKey(userID, tmdbType, tmdbID)
	if key == "" {
		return false
	}
	now := time.Now()
	s.mu.Lock()
	defer s.mu.Unlock()
	s.cleanupLocked(now)
	session, ok := s.data[key]
	if !ok || session.ExpireAt.Before(now) {
		session = playbackSwitchSession{
			UserID:   userID,
			TMDBType: strings.TrimSpace(tmdbType),
			TMDBID:   tmdbID,
		}
	}
	for _, item := range session.SkipItems {
		if playbackSwitchSkipMatches(item, site, panFlag, episodeFile) {
			session.ExpireAt = now.Add(ttl)
			s.data[key] = session
			return true
		}
	}
	session.SkipItems = append(session.SkipItems, playbackSwitchSkipItem{
		Site:        site,
		PanFlag:     panFlag,
		EpisodeFile: episodeFile,
	})
	session.ExpireAt = now.Add(ttl)
	s.data[key] = session
	return true
}

func (s *playbackSwitchStore) Extend(userID int64, tmdbType string, tmdbID int, ttl time.Duration) bool {
	if s == nil {
		return false
	}
	key := playbackSwitchSessionKey(userID, tmdbType, tmdbID)
	if key == "" {
		return false
	}
	now := time.Now()
	s.mu.Lock()
	defer s.mu.Unlock()
	s.cleanupLocked(now)
	session, ok := s.data[key]
	if !ok || session.ExpireAt.Before(now) {
		return false
	}
	session.ExpireAt = now.Add(ttl)
	s.data[key] = session
	return true
}

func (s *playbackSwitchStore) ClearSkipItems(userID int64, tmdbType string, tmdbID int, ttl time.Duration) bool {
	if s == nil {
		return false
	}
	key := playbackSwitchSessionKey(userID, tmdbType, tmdbID)
	if key == "" {
		return false
	}
	now := time.Now()
	s.mu.Lock()
	defer s.mu.Unlock()
	s.cleanupLocked(now)
	session, ok := s.data[key]
	if !ok || session.ExpireAt.Before(now) {
		return false
	}
	session.SkipItems = []playbackSwitchSkipItem{}
	session.ExpireAt = now.Add(ttl)
	s.data[key] = session
	return true
}

func playbackSwitchActionKey(userID int64, itemID string, playSessionID string) string {
	if userID <= 0 {
		return ""
	}
	item := strings.TrimSpace(itemID)
	ps := strings.TrimSpace(playSessionID)
	if item == "" || ps == "" {
		return ""
	}
	return StableMD5Hex(strings.Join([]string{strconv.FormatInt(userID, 10), item, ps}, "|"))
}

func (s *playbackSwitchActionStore) cleanupLocked(now time.Time) {
	if s == nil {
		return
	}
	for key, record := range s.data {
		if record.ExpireAt.Before(now) {
			delete(s.data, key)
		}
	}
}

func (s *playbackSwitchActionStore) InitPending(userID int64, itemID string, playSessionID string, action string, ttl time.Duration) bool {
	if s == nil {
		return false
	}
	key := playbackSwitchActionKey(userID, itemID, playSessionID)
	if key == "" {
		return false
	}
	now := time.Now()
	s.mu.Lock()
	defer s.mu.Unlock()
	s.cleanupLocked(now)
	s.data[key] = playbackSwitchActionRecord{
		UserID:        userID,
		ItemID:        strings.TrimSpace(itemID),
		PlaySessionID: strings.TrimSpace(playSessionID),
		Action:        strings.TrimSpace(action),
		State:         playbackSwitchActionPending,
		ExpireAt:      now.Add(ttl),
	}
	return true
}

func (s *playbackSwitchActionStore) Begin(userID int64, itemID string, playSessionID string, ttl time.Duration) (playbackSwitchActionRecord, bool, string) {
	if s == nil {
		return playbackSwitchActionRecord{}, false, "missing"
	}
	key := playbackSwitchActionKey(userID, itemID, playSessionID)
	if key == "" {
		return playbackSwitchActionRecord{}, false, "missing"
	}
	now := time.Now()
	s.mu.Lock()
	defer s.mu.Unlock()
	s.cleanupLocked(now)
	record, ok := s.data[key]
	if !ok || record.ExpireAt.Before(now) {
		return playbackSwitchActionRecord{}, false, "expired"
	}
	switch record.State {
	case playbackSwitchActionPending:
		record.State = playbackSwitchActionRunning
		record.ExpireAt = now.Add(ttl)
		s.data[key] = record
		return record, true, "running"
	case playbackSwitchActionRunning:
		return record, false, "running"
	case playbackSwitchActionDone:
		return record, false, "done"
	default:
		return record, false, string(record.State)
	}
}

func (s *playbackSwitchActionStore) Finish(userID int64, itemID string, playSessionID string, ttl time.Duration) bool {
	if s == nil {
		return false
	}
	key := playbackSwitchActionKey(userID, itemID, playSessionID)
	if key == "" {
		return false
	}
	now := time.Now()
	s.mu.Lock()
	defer s.mu.Unlock()
	s.cleanupLocked(now)
	record, ok := s.data[key]
	if !ok || record.ExpireAt.Before(now) {
		return false
	}
	record.State = playbackSwitchActionDone
	record.ExpireAt = now.Add(ttl)
	s.data[key] = record
	return true
}

var embyPlaybackSwitchActions = newPlaybackSwitchActionStore()

func initPlaybackSwitchAction(userID int64, itemID string, playSessionID string) {
	action := resolvePlaybackSwitchActionName(itemID)
	if strings.TrimSpace(action) == "" {
		return
	}
	embyPlaybackSwitchActions.InitPending(userID, itemID, playSessionID, action, playbackSwitchActionTTL)
}

func resolvePlaybackSwitchActionName(itemID string) string {
	ref := parseItemRefAny(strings.TrimSpace(itemID))
	if ref == nil || ref.Source != "tmdb" || ref.SubKind != "episode" || strings.TrimSpace(ref.Variant) != "settings" {
		return ""
	}
	switch ref.Episode {
	case 1:
		return "switch_source"
	case 2:
		return "source_error"
	case 3:
		return "reset_source"
	default:
		return ""
	}
}

func resolvePlaybackSwitchCurrentOrHistory(database *db.DB, userID int64, itemID string) (tmdbType string, tmdbID int, site string, spiderAPI string, siteDetail string, panFlag string, episodeFile string, ok bool) {
	ref := parseItemRefAny(strings.TrimSpace(itemID))
	tmdbType, tmdbID, ok = resolvePlaybackSwitchTMDBScope(ref)
	if !ok {
		return "", 0, "", "", "", "", "", false
	}
	if session, found := embyPlaybackSwitchSessions.Get(userID, tmdbType, tmdbID); found {
		site = strings.TrimSpace(session.CurrentSite)
		spiderAPI = strings.TrimSpace(session.CurrentSpiderAPI)
		siteDetail = strings.TrimSpace(session.CurrentSiteDetail)
		panFlag = strings.TrimSpace(session.CurrentPanFlag)
		episodeFile = normalizePlaybackSwitchEpisodeFile(session.CurrentEpisodeFile)
		if site != "" && siteDetail != "" && panFlag != "" {
			return tmdbType, tmdbID, site, spiderAPI, siteDetail, panFlag, episodeFile, true
		}
	}
	if database != nil {
		if hist, err := database.GetPlayHistoryLatestByTMDB(userID, tmdbType, tmdbID); err == nil && hist != nil {
			site = strings.TrimSpace(hist.SiteKey)
			spiderAPI = strings.TrimSpace(hist.SpiderAPI)
			siteDetail = strings.TrimSpace(hist.SiteDetail)
			panFlag = strings.TrimSpace(hist.PlayFlag)
			episodeFile = normalizePlaybackSwitchEpisodeFile(hist.SiteEpisodeFile)
			if site != "" && siteDetail != "" && panFlag != "" {
				return tmdbType, tmdbID, site, spiderAPI, siteDetail, panFlag, episodeFile, true
			}
		}
	}
	return tmdbType, tmdbID, "", "", "", "", "", false
}

func triggerPlaybackSwitchAction(database *db.DB, userID int64, payload SessionPlaybackPayload) (handled bool, action string, applied bool, status string) {
	itemID := strings.TrimSpace(payload.ItemID)
	playSessionID := strings.TrimSpace(payload.PlaySessionID)
	action = resolvePlaybackSwitchActionName(itemID)
	if action == "" {
		return false, "", false, ""
	}
	record, proceed, state := embyPlaybackSwitchActions.Begin(userID, itemID, playSessionID, playbackSwitchActionTTL)
	if !proceed {
		return true, action, false, state
	}
	defer embyPlaybackSwitchActions.Finish(userID, itemID, playSessionID, playbackSwitchActionTTL)
	switch action {
	case "switch_source":
		tmdbType, tmdbID, site, _, _, panFlag, episodeFile, ok := resolvePlaybackSwitchCurrentOrHistory(database, userID, itemID)
		if !ok || panFlag == "" {
			return true, action, false, "no_source"
		}
		if episodeFile == "" {
			return true, action, false, "no_episode_file"
		}
		applied = embyPlaybackSwitchSessions.AddSkipItem(userID, tmdbType, tmdbID, site, panFlag, episodeFile, playbackSwitchSessionTTL)
		return true, action, applied, string(record.State)
	case "source_error":
		tmdbType, _, site, spiderAPI, siteDetail, panFlag, _, ok := resolvePlaybackSwitchCurrentOrHistory(database, userID, itemID)
		if !ok || site == "" || siteDetail == "" || panFlag == "" {
			return true, action, false, "no_source"
		}
		keyword, poster := resolvePlaybackSwitchBlockKeywordPoster(database, tmdbType, tmdbIDFromItem(itemID))
		if keyword == "" {
			return true, action, false, "no_keyword"
		}
		_ = database.DeleteSmartMatchBlockItem(keyword, site, siteDetail, "", "play")
		if err := database.UpsertSmartMatchBlockItem(keyword, site, spiderAPI, siteDetail, poster, panFlag, "play"); err != nil {
			log.Printf("[emby][switch_action_error] item=%s action=%s err=%v", itemID, action, err)
			return true, action, false, "db_error"
		}
		return true, action, true, string(record.State)
	case "reset_source":
		tmdbType, tmdbID, ok := "", 0, false
		if ref := parseItemRefAny(strings.TrimSpace(itemID)); ref != nil {
			tmdbType, tmdbID, ok = resolvePlaybackSwitchTMDBScope(ref)
		}
		if !ok {
			return true, action, false, "no_scope"
		}
		applied = embyPlaybackSwitchSessions.ClearSkipItems(userID, tmdbType, tmdbID, playbackSwitchSessionTTL)
		return true, action, applied, string(record.State)
	default:
		return true, action, false, string(record.State)
	}
}

func playbackSwitchSkipMatches(item playbackSwitchSkipItem, site string, panFlag string, episodeFile string) bool {
	skipPanFlag := strings.TrimSpace(item.PanFlag)
	candPanFlag := strings.TrimSpace(panFlag)
	skipEpisodeFile := normalizePlaybackSwitchEpisodeFile(item.EpisodeFile)
	candEpisodeFile := normalizePlaybackSwitchEpisodeFile(episodeFile)
	if skipPanFlag == "" || candPanFlag == "" || skipEpisodeFile == "" || candEpisodeFile == "" {
		return false
	}
	if strings.TrimSpace(smart.PlayFlagProviderID(candPanFlag)) != "" {
		return skipPanFlag == candPanFlag && skipEpisodeFile == candEpisodeFile
	}
	return skipPanFlag == candPanFlag && strings.TrimSpace(item.Site) == strings.TrimSpace(site) && skipEpisodeFile == candEpisodeFile
}

func playbackSwitchShouldSkip(session playbackSwitchSession, site string, panFlag string, episodeFile string) bool {
	if strings.TrimSpace(panFlag) == "" {
		return false
	}
	for _, item := range session.SkipItems {
		if playbackSwitchSkipMatches(item, site, panFlag, episodeFile) {
			return true
		}
	}
	return false
}

func resolvePlaybackSwitchTMDBScope(ref *itemRef) (tmdbType string, tmdbID int, ok bool) {
	if ref == nil || ref.Source != "tmdb" || ref.NumericID <= 0 {
		return "", 0, false
	}
	typ := strings.TrimSpace(ref.MediaType)
	if typ == "" {
		return "", 0, false
	}
	return typ, ref.NumericID, true
}

func loadOrInitPlaybackSwitchSession(userID int64, ref *itemRef, ttl time.Duration) (playbackSwitchSession, bool) {
	tmdbType, tmdbID, ok := resolvePlaybackSwitchTMDBScope(ref)
	if !ok {
		return playbackSwitchSession{}, false
	}
	session, created := embyPlaybackSwitchSessions.Ensure(userID, tmdbType, tmdbID, ttl)
	if created {
		return session, true
	}
	return session, false
}

func rememberPlaybackSwitchCurrentSource(userID int64, itemID string, target PlaybackStreamTarget, ttl time.Duration) {
	ref := parseItemRefAny(strings.TrimSpace(itemID))
	tmdbType, tmdbID, ok := resolvePlaybackSwitchTMDBScope(ref)
	if !ok {
		return
	}
	site := strings.TrimSpace(target.SiteKey)
	panFlag := strings.TrimSpace(target.PanFlag)
	if site == "" || panFlag == "" {
		return
	}
	siteDetail := strings.TrimSpace(target.SiteDetail)
	spiderAPI := strings.TrimSpace(target.SpiderAPI)
	episodeFile := normalizePlaybackSwitchEpisodeFile(target.SiteEpisodeFile)
	embyPlaybackSwitchSessions.SetCurrentSource(userID, tmdbType, tmdbID, site, siteDetail, spiderAPI, panFlag, episodeFile, ttl)
	log.Printf("[emby][switch_current] item=%s tmdb=%s:%d site=%s siteDetail=%s panFlag=%s episodeFile=%s", strings.TrimSpace(itemID), strings.TrimSpace(tmdbType), tmdbID, site, siteDetail, panFlag, episodeFile)
}

func extendPlaybackSwitchSessionByItem(userID int64, itemID string, ttl time.Duration) bool {
	ref := parseItemRefAny(strings.TrimSpace(itemID))
	tmdbType, tmdbID, ok := resolvePlaybackSwitchTMDBScope(ref)
	if !ok {
		return false
	}
	return embyPlaybackSwitchSessions.Extend(userID, tmdbType, tmdbID, ttl)
}

func resolveTMDBSettingsStaticBaseName(itemID string) string {
	ref := parseItemRefAny(strings.TrimSpace(itemID))
	if ref == nil || ref.Source != "tmdb" || ref.SubKind != "episode" || strings.TrimSpace(ref.Variant) != "settings" {
		return ""
	}
	switch ref.Episode {
	case 1:
		return "switch_source"
	case 2:
		return "source_error"
	case 3:
		return "reset_source"
	default:
		return ""
	}
}

func normalizePlaybackSwitchEpisodeFile(name string) string {
	return normalizePlaybackFileIdentity(strings.TrimSpace(name))
}

func tmdbIDFromItem(itemID string) int {
	ref := parseItemRefAny(strings.TrimSpace(itemID))
	if ref == nil {
		return 0
	}
	return ref.NumericID
}

func resolvePlaybackSwitchBlockKeywordPoster(database *db.DB, tmdbType string, tmdbID int) (keyword string, poster string) {
	if database == nil || tmdbID <= 0 {
		return "", ""
	}
	switch strings.TrimSpace(tmdbType) {
	case "movie":
		if detail, err := metadata_tmdb.GetMovieDetails(database, tmdbID); err == nil && detail != nil {
			keyword = strings.TrimSpace(detail.Title)
			poster = strings.TrimSpace(detail.PosterPath)
		}
	case "tv":
		if detail, err := metadata_tmdb.GetTVDetails(database, tmdbID); err == nil && detail != nil {
			keyword = firstNonEmptyString(strings.TrimSpace(detail.Name), strings.TrimSpace(detail.OriginalName))
			poster = strings.TrimSpace(detail.PosterPath)
		}
	}
	return strings.TrimSpace(keyword), strings.TrimSpace(poster)
}
