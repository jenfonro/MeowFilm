package emby_service

import (
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"log"
	"net/http"
	neturl "net/url"
	"path"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/cache"
	"github.com/jenfonro/meowfilm/server/catpawrunner"
	"github.com/jenfonro/meowfilm/server/clientmeta"
	"github.com/jenfonro/meowfilm/server/goproxy"
	"github.com/jenfonro/meowfilm/server/magic"
	metadata_tmdb "github.com/jenfonro/meowfilm/server/metadata/tmdb"
	"github.com/jenfonro/meowfilm/server/netdisk"
	"github.com/jenfonro/meowfilm/server/relay"
	"github.com/jenfonro/meowfilm/server/smart"
)

type PlaybackInfoMediaSourceDTO struct {
	Chapters             []any             `json:"Chapters"`
	Protocol             string            `json:"Protocol"`
	ID                   string            `json:"Id"`
	Path                 string            `json:"Path"`
	Type                 string            `json:"Type"`
	Container            string            `json:"Container"`
	Size                 int64             `json:"Size"`
	Name                 string            `json:"Name"`
	IsRemote             bool              `json:"IsRemote"`
	HasMixedProtocols    bool              `json:"HasMixedProtocols"`
	RunTimeTicks         int64             `json:"RunTimeTicks"`
	SupportsTranscoding  bool              `json:"SupportsTranscoding"`
	SupportsDirectStream bool              `json:"SupportsDirectStream"`
	SupportsDirectPlay   bool              `json:"SupportsDirectPlay"`
	IsInfiniteStream     bool              `json:"IsInfiniteStream"`
	RequiresOpening      bool              `json:"RequiresOpening"`
	RequiresClosing      bool              `json:"RequiresClosing"`
	RequiresLooping      bool              `json:"RequiresLooping"`
	SupportsProbing      bool              `json:"SupportsProbing"`
	MediaStreams         []any             `json:"MediaStreams"`
	Formats              []any             `json:"Formats"`
	Bitrate              int               `json:"Bitrate"`
	RequiredHTTPHeaders  map[string]string `json:"RequiredHttpHeaders"`
	DirectStreamURL      string            `json:"DirectStreamUrl"`
	AddAPIKeyToDirect    bool              `json:"AddApiKeyToDirectStreamUrl"`
	ReadAtNativeFrame    bool              `json:"ReadAtNativeFramerate"`
	DefaultAudioStreamID int               `json:"DefaultAudioStreamIndex"`
	ItemID               string            `json:"ItemId"`
}

type PlaybackInfoResponseDTO struct {
	MediaSources  []PlaybackInfoMediaSourceDTO `json:"MediaSources"`
	PlaySessionID string                       `json:"PlaySessionId"`
}

type PlaybackStreamTarget struct {
	FinalURL         string
	FinalHeaders     map[string]string
	Offers           []smart.PlaybackOffer
	Path             string
	Container        string
	Name             string
	Runtime          int64
	Size             int64
	Bitrate          int
	ItemID           string
	MediaSourceID    string
	PlaySessionID    string
	UserID           int64
	DeviceID         string
	SiteKey          string
	SiteDetail       string
	PanFlag          string
	Provider         string
	SpiderAPI        string
	SiteEpisodeIndex int
	SiteEpisodeFile  string
	ExpireAt         time.Time
}

var embyPlaybackSessions = newPlaybackCacheStore()

const playbackSessionTTL = 15 * time.Minute

func EmptyPlaybackInfoResponse() PlaybackInfoResponseDTO {
	return PlaybackInfoResponseDTO{MediaSources: []PlaybackInfoMediaSourceDTO{}, PlaySessionID: ""}
}

func BuildPlaybackInfoPayload(database *db.DB, userID int64, apiToken string, serverID string, itemID string, r *http.Request) (PlaybackInfoResponseDTO, bool, error) {
	if database == nil || userID <= 0 {
		return EmptyPlaybackInfoResponse(), false, nil
	}
	deviceID := strings.TrimSpace(clientmeta.ClientDeviceID(r))
	userText := strconv.FormatInt(userID, 10)
	cacheKey := ResolvePlaybackCacheKey(userText, deviceID, itemID)
	ref := parseItemRefAny(strings.TrimSpace(itemID))
	isSettingsAction := ref != nil && ref.Source == "tmdb" && ref.SubKind == "episode" && strings.TrimSpace(ref.Variant) == "settings"
	if !isSettingsAction {
		if replayTarget, replayOK := embyPlaybackSessions.GetByReplayKey(cacheKey); replayOK {
			log.Printf("[emby][playback_cache_hit] item=%s source=replay finalized=true", strings.TrimSpace(itemID))
			rememberPlaybackSwitchCurrentSource(userID, itemID, replayTarget, playbackSwitchSessionTTL)
			return buildPlaybackInfoResponse(replayTarget, apiToken, r), true, nil
		}
	}
	target, ok, err := ResolvePlaybackOnce(cacheKey, func() (*PlaybackStreamTarget, bool, error) {
		mediaSourceID := randomPlaybackSessionID()
		playSessionID := randomPlaybackSessionID()
		resolved, built, resolveErr := ResolvePlaybackStreamTarget(database, userID, itemID, mediaSourceID, playSessionID, cacheKey, r)
		if resolveErr != nil || !built || resolved == nil {
			return resolved, built, resolveErr
		}
		resolved.UserID = userID
		resolved.DeviceID = deviceID
		resolved.ItemID = strings.TrimSpace(itemID)
		finalTarget := resolved
		if strings.TrimSpace(finalTarget.FinalURL) == "" {
			finalized, finalizedOK, finalizeErr := ConsumePlaybackOffersAndBuildTarget(database, userID, *resolved, cacheKey, r)
			if finalizeErr != nil || !finalizedOK || finalized == nil {
				return finalized, finalizedOK, finalizeErr
			}
			finalTarget = finalized
		}
		finalTarget.UserID = userID
		finalTarget.DeviceID = deviceID
		finalTarget.ItemID = strings.TrimSpace(itemID)
		if strings.TrimSpace(finalTarget.MediaSourceID) == "" {
			finalTarget.MediaSourceID = mediaSourceID
		}
		if strings.TrimSpace(finalTarget.PlaySessionID) == "" {
			finalTarget.PlaySessionID = playSessionID
		}
		embyPlaybackSessions.SetWithReplayKey(*finalTarget, cacheKey, playbackSessionTTL)
		return finalTarget, true, nil
	})
	if err != nil {
		return EmptyPlaybackInfoResponse(), true, err
	}
	if !ok || target == nil || strings.TrimSpace(target.Path) == "" {
		return EmptyPlaybackInfoResponse(), false, nil
	}
	rememberPlaybackSwitchCurrentSource(userID, itemID, *target, playbackSwitchSessionTTL)
	return buildPlaybackInfoResponse(*target, apiToken, r), true, nil
}

func buildPlaybackInfoResponse(target PlaybackStreamTarget, apiToken string, r *http.Request) PlaybackInfoResponseDTO {
	directPath := buildPlaybackDirectPath(strings.TrimSpace(target.ItemID), target.Container, apiToken, target.MediaSourceID, target.PlaySessionID, r)
	return PlaybackInfoResponseDTO{
		MediaSources: []PlaybackInfoMediaSourceDTO{{
			Chapters:             EmptyAnySlice(),
			Protocol:             "File",
			ID:                   target.MediaSourceID,
			Path:                 target.Path,
			Type:                 "Default",
			Container:            target.Container,
			Size:                 target.Size,
			Name:                 target.Name,
			IsRemote:             false,
			HasMixedProtocols:    false,
			RunTimeTicks:         target.Runtime,
			SupportsTranscoding:  playbackInfoSupportsTranscoding(r),
			SupportsDirectStream: true,
			SupportsDirectPlay:   true,
			IsInfiniteStream:     false,
			RequiresOpening:      false,
			RequiresClosing:      false,
			RequiresLooping:      false,
			SupportsProbing:      true,
			MediaStreams:         EmptyAnySlice(),
			Formats:              EmptyAnySlice(),
			Bitrate:              target.Bitrate,
			RequiredHTTPHeaders:  EmptyRequiredHTTPHeaders(),
			DirectStreamURL:      directPath,
			AddAPIKeyToDirect:    false,
			ReadAtNativeFrame:    false,
			DefaultAudioStreamID: 0,
			ItemID:               strings.TrimSpace(target.ItemID),
		}},
		PlaySessionID: target.PlaySessionID,
	}
}

func ResolvePlaybackStreamTarget(database *db.DB, userID int64, itemID string, mediaSourceID string, playSessionID string, cacheKey string, r *http.Request) (*PlaybackStreamTarget, bool, error) {
	ref := parseItemRefAny(strings.TrimSpace(itemID))
	if ref == nil {
		return nil, false, nil
	}
	switch ref.Source {
	case "tmdb":
		if ref.SubKind == "episode" && strings.TrimSpace(ref.Variant) == "settings" {
			return resolveTMDBSettingsPlaybackStreamTarget(database, userID, ref, r)
		}
		return resolveTMDBPlaybackStreamTarget(database, userID, ref, mediaSourceID, playSessionID, cacheKey, r)
	case "site":
		if ref.SubKind == "episode" {
			return resolveSiteEpisodePlaybackStreamTarget(database, userID, ref, r)
		}
		return nil, false, nil
	default:
		return nil, false, nil
	}
}

func resolveTMDBSettingsPlaybackStreamTarget(database *db.DB, userID int64, ref *itemRef, r *http.Request) (*PlaybackStreamTarget, bool, error) {
	if database == nil || ref == nil || ref.Source != "tmdb" || ref.MediaType != "tv" || ref.SubKind != "episode" || strings.TrimSpace(ref.Variant) != "settings" {
		return nil, false, nil
	}
	seriesRef := &itemRef{
		Kind:      "item",
		SubKind:   "series",
		MediaType: "tv",
		Source:    "tmdb",
		RawID:     buildSeriesID(ref.NumericID),
		NumericID: ref.NumericID,
	}
	items, ok, err := buildTMDBSettingsEpisodeSources(database, userID, "", seriesRef)
	if err != nil || !ok {
		return nil, ok, err
	}
	for _, item := range items {
		if item.IndexNumber != ref.Episode {
			continue
		}
		container := strings.TrimSpace(item.Container)
		if container == "" {
			container = "mp4"
		}
		target := &PlaybackStreamTarget{
			FinalURL:      buildPlaybackActionStaticVideoPath(strings.TrimSpace(ref.RawID)),
			FinalHeaders:  EmptyRequiredHTTPHeaders(),
			Path:          strings.TrimSpace(item.Path),
			Container:     container,
			Name:          strings.TrimSpace(item.Name),
			Runtime:       item.RunTimeTicks,
			Size:          item.Size,
			Bitrate:       item.Bitrate,
			ItemID:        strings.TrimSpace(ref.RawID),
			MediaSourceID: randomPlaybackSessionID(),
			PlaySessionID: randomPlaybackSessionID(),
		}
		initPlaybackSwitchAction(userID, strings.TrimSpace(ref.RawID), strings.TrimSpace(target.PlaySessionID))
		return target, true, nil
	}
	return nil, false, nil
}

func LoadPlaybackStreamTarget(userID int64, itemID string, mediaSourceID string, playSessionID string) (*PlaybackStreamTarget, bool) {
	if target, ok := embyPlaybackSessions.GetBySession(itemID, mediaSourceID, playSessionID); ok {
		return &target, true
	}
	return nil, false
}

func LoadPlaybackStreamTargetByMediaSource(mediaSourceID string) (*PlaybackStreamTarget, bool) {
	target, ok := embyPlaybackSessions.GetByMediaSourceID(mediaSourceID)
	if !ok {
		return nil, false
	}
	return &target, true
}

func ExtendPlaybackStreamTTL(mediaSourceID string, add time.Duration, capRemaining time.Duration) bool {
	return embyPlaybackSessions.ExtendIfLow(mediaSourceID, add, capRemaining)
}

func ResolvePlaybackCacheKey(userID string, deviceID string, itemID string) string {
	u := strings.TrimSpace(userID)
	i := strings.TrimSpace(itemID)
	if i == "" {
		return ""
	}
	if ref := parseItemRefAny(i); ref != nil && ref.Source == "site" {
		return StableMD5Hex(u + "||" + i)
	}
	return StableMD5Hex(u + "|" + strings.TrimSpace(deviceID) + "|" + i)
}

func ParseItemRefAnyPublic(raw string) *itemRef {
	return parseItemRefAny(raw)
}

func resolveTMDBPlaybackStreamTarget(database *db.DB, userID int64, ref *itemRef, mediaSourceID string, playSessionID string, cacheKey string, r *http.Request) (*PlaybackStreamTarget, bool, error) {
	req, display, ok := buildTMDBPlaybackRequest(database, userID, ref)
	if !ok || req == nil || display == nil {
		return nil, false, nil
	}
	user, err := resolveSmartUser(database, userID)
	if err != nil || user == nil {
		return nil, false, err
	}
	switchSession, created := loadOrInitPlaybackSwitchSession(userID, ref, playbackSwitchSessionTTL)
	if created {
		log.Printf("[emby][switch_session_init] tmdb=%s:%d", strings.TrimSpace(req.Kind), ref.NumericID)
	} else {
		log.Printf("[emby][switch_session_hit] tmdb=%s:%d skip=%d", strings.TrimSpace(req.Kind), ref.NumericID, len(switchSession.SkipItems))
	}
	matchSettings := smart.LoadPlaybackSettings(database)
	tmdbHasMultiSeason := strings.TrimSpace(req.Kind) == "tv" && strings.TrimSpace(req.SubKind) == "episode" && req.Season > 0
	entry := ensurePlaybackControlEntry(userID, strings.TrimSpace(clientmeta.ClientDeviceID(r)), strings.TrimSpace(ref.RawID), strings.TrimSpace(mediaSourceID), strings.TrimSpace(playSessionID), strings.TrimSpace(cacheKey), strings.TrimSpace(req.Kind), strings.TrimSpace(req.SubKind), req.Season, tmdbHasMultiSeason, matchSettings)
	if entry == nil {
		return nil, false, nil
	}
	stopCh := entry.stopCh
	go func() {
		manualItems, manualErr := database.ListSmartManualItems(strings.TrimSpace(req.Kind), ref.NumericID)
		if manualErr == nil {
			for _, manualItem := range manualItems {
				if !manualItem.Enabled {
					continue
				}
				if IsPlaybackResolveStopped(stopCh) {
					break
				}
				item := manualItem
				requestOK := false
				if strings.TrimSpace(item.PanFlag) != "" {
					requestOK = collectTMDBManualPlaybackFromListCandidates(database, ref, *req, item, stopCh, func(offer smart.PlaybackOffer) {
						episodeFile := firstNonEmptyString(strings.TrimSpace(offer.Cand.RawName), strings.TrimSpace(smart.FirstRawNameFromURL(offer.Cand.Ep.URL)))
						if playbackSwitchShouldSkip(switchSession, offer.Cand.SiteKey, offer.Cand.PanFlag, episodeFile) {
							log.Printf("[emby][manual_list_skip] item=%s tmdb=%s:%d reason=session_panflag_skip site=(%s|%s) panFlag=%s episodeFile=%s", strings.TrimSpace(ref.RawID), strings.TrimSpace(req.Kind), ref.NumericID, strings.TrimSpace(offer.Cand.SiteKey), strings.TrimSpace(offer.Cand.SiteName), strings.TrimSpace(offer.Cand.PanFlag), strings.TrimSpace(episodeFile))
							return
						}
						if EnqueueManualListOffer(entry.PlaySessionID, entry.MediaSourceID, entry.CacheKey, offer) {
							log.Printf("[emby][manual_offer_enqueue] item=%s tmdb=%s:%d stage=manual_list panFlag=%s", strings.TrimSpace(ref.RawID), strings.TrimSpace(req.Kind), ref.NumericID, strings.TrimSpace(offer.Cand.PanFlag))
						}
					})
				} else {
					requestOK = collectTMDBManualPlaybackFromDetailCandidates(database, userID, user, ref, *req, item, stopCh, func(offer smart.PlaybackOffer) {
						episodeFile := firstNonEmptyString(strings.TrimSpace(offer.Cand.RawName), strings.TrimSpace(smart.FirstRawNameFromURL(offer.Cand.Ep.URL)))
						if playbackSwitchShouldSkip(switchSession, offer.Cand.SiteKey, offer.Cand.PanFlag, episodeFile) {
							log.Printf("[emby][manual_detail_skip] item=%s tmdb=%s:%d reason=session_panflag_skip site=(%s|%s) panFlag=%s episodeFile=%s", strings.TrimSpace(ref.RawID), strings.TrimSpace(req.Kind), ref.NumericID, strings.TrimSpace(offer.Cand.SiteKey), strings.TrimSpace(offer.Cand.SiteName), strings.TrimSpace(offer.Cand.PanFlag), strings.TrimSpace(episodeFile))
							return
						}
						if EnqueueManualDetailOffer(entry.PlaySessionID, entry.MediaSourceID, entry.CacheKey, offer) {
							log.Printf("[emby][manual_offer_enqueue] item=%s tmdb=%s:%d stage=manual_detail panFlag=%s", strings.TrimSpace(ref.RawID), strings.TrimSpace(req.Kind), ref.NumericID, strings.TrimSpace(offer.Cand.PanFlag))
						}
					})
				}
				if item.ID > 0 {
					_ = database.ReportSmartManualItemResult(item.ID, requestOK)
				}
			}
		}
		CloseManualListOffers(entry.PlaySessionID, entry.MediaSourceID, entry.CacheKey)
		CloseManualDetailOffers(entry.PlaySessionID, entry.MediaSourceID, entry.CacheKey)
	}()
	go func() {
		hist, histErr := database.GetPlayHistoryLatestByTMDB(userID, req.Kind, ref.NumericID)
		if histErr == nil && hist != nil {
			collectTMDBHistoryPlaybackFromListCandidates(database, user, ref, *req, *hist, stopCh, func(offer smart.PlaybackOffer) {
				episodeFile := strings.TrimSpace(offer.Cand.RawName)
				if playbackSwitchShouldSkip(switchSession, offer.Cand.SiteKey, offer.Cand.PanFlag, episodeFile) {
					log.Printf("[emby][history_list_skip] item=%s tmdb=%s:%d site=(%s|%s) reason=session_panflag_skip panFlag=%s episodeFile=%s", strings.TrimSpace(ref.RawID), strings.TrimSpace(req.Kind), ref.NumericID, strings.TrimSpace(offer.Cand.SiteKey), strings.TrimSpace(offer.Cand.SiteName), strings.TrimSpace(offer.Cand.PanFlag), strings.TrimSpace(episodeFile))
					return
				}
				if EnqueueHistoryListOffer(entry.PlaySessionID, entry.MediaSourceID, entry.CacheKey, offer) {
					log.Printf("[emby][history_offer_enqueue] item=%s tmdb=%s:%d stage=history_list panFlag=%s", strings.TrimSpace(ref.RawID), strings.TrimSpace(req.Kind), ref.NumericID, strings.TrimSpace(offer.Cand.PanFlag))
				}
			})
		}
		CloseHistoryListOffers(entry.PlaySessionID, entry.MediaSourceID, entry.CacheKey)
		if histErr == nil && hist != nil {
			collectTMDBHistoryPlaybackFromDetailCandidates(database, userID, user, ref, *req, *hist, stopCh, func(offer smart.PlaybackOffer) {
				episodeFile := strings.TrimSpace(offer.Cand.RawName)
				if playbackSwitchShouldSkip(switchSession, offer.Cand.SiteKey, offer.Cand.PanFlag, episodeFile) {
					log.Printf("[emby][history_detail_skip] item=%s tmdb=%s:%d site=(%s|%s) reason=session_panflag_skip panFlag=%s episodeFile=%s", strings.TrimSpace(ref.RawID), strings.TrimSpace(req.Kind), ref.NumericID, strings.TrimSpace(offer.Cand.SiteKey), strings.TrimSpace(offer.Cand.SiteName), strings.TrimSpace(offer.Cand.PanFlag), strings.TrimSpace(episodeFile))
					return
				}
				if EnqueueHistoryDetailOffer(entry.PlaySessionID, entry.MediaSourceID, entry.CacheKey, offer) {
					log.Printf("[emby][history_offer_enqueue] item=%s tmdb=%s:%d stage=history_detail panFlag=%s", strings.TrimSpace(ref.RawID), strings.TrimSpace(req.Kind), ref.NumericID, strings.TrimSpace(offer.Cand.PanFlag))
				}
			})
		}
		CloseHistoryDetailOffers(entry.PlaySessionID, entry.MediaSourceID, entry.CacheKey)
	}()
	go func() {
		_ = smart.CollectPlaybackOffersFromTMDB(database, user, *req, func() bool {
			return IsPlaybackResolveStopped(stopCh)
		}, func(offer smart.PlaybackOffer, _ int) {
			episodeFile := firstNonEmptyString(strings.TrimSpace(offer.Cand.RawName), strings.TrimSpace(smart.FirstRawNameFromURL(offer.Cand.Ep.URL)))
			if playbackSwitchShouldSkip(switchSession, offer.Cand.SiteKey, offer.Cand.PanFlag, episodeFile) {
				log.Printf("[emby][full_offer_skip] item=%s tmdb=%s:%d reason=session_panflag_skip site=(%s|%s) panFlag=%s episodeFile=%s", strings.TrimSpace(ref.RawID), strings.TrimSpace(req.Kind), ref.NumericID, strings.TrimSpace(offer.Cand.SiteKey), strings.TrimSpace(offer.Cand.SiteName), strings.TrimSpace(offer.Cand.PanFlag), strings.TrimSpace(episodeFile))
				return
			}
			EnqueueFullOffer(entry.PlaySessionID, entry.MediaSourceID, entry.CacheKey, offer)
		})
		CloseFullOffers(entry.PlaySessionID, entry.MediaSourceID, entry.CacheKey)
	}()
	return buildTMDBPlaybackOfferTarget(ref, *display, nil, mediaSourceID, playSessionID), true, nil
}

func collectTMDBManualPlaybackFromListCandidates(database *db.DB, ref *itemRef, req smart.PlaybackRequest, item db.SmartManualItem, _ <-chan struct{}, emit func(smart.PlaybackOffer)) bool {
	panFlag := strings.TrimSpace(item.PanFlag)
	provider := historyListProvider(panFlag)
	if provider == "" || database == nil {
		return false
	}
	log.Printf("[emby][manual_list] item=%s tmdb=%s:%d panFlag=%s provider=%s", strings.TrimSpace(ref.RawID), strings.TrimSpace(req.Kind), ref.NumericID, panFlag, provider)
	episodes, ok := listHistoryPlayFlagEpisodes(database, provider, panFlag)
	if !ok || len(episodes) == 0 {
		log.Printf("[emby][manual_list_error] item=%s tmdb=%s:%d panFlag=%s provider=%s", strings.TrimSpace(ref.RawID), strings.TrimSpace(req.Kind), ref.NumericID, panFlag, provider)
		return false
	}
	ep, matchOK := pickManualEpisodeCandidate(database, ref.NumericID, req, episodes, strings.TrimSpace(item.SeasonHint))
	if !matchOK {
		log.Printf("[emby][manual_list_error] item=%s tmdb=%s:%d panFlag=%s provider=%s reason=no_episode_match", strings.TrimSpace(ref.RawID), strings.TrimSpace(req.Kind), ref.NumericID, panFlag, provider)
		return true
	}
	siteKey := firstNonEmptyString(strings.TrimSpace(item.SiteKey), provider)
	offer := smart.PlaybackOffer{
		Cand: smart.Candidate{
			Stage:      "manual_list",
			SiteKey:    siteKey,
			SiteName:   siteKey,
			SpiderAPI:  strings.TrimSpace(item.SpiderAPI),
			SiteDetail: strings.TrimSpace(item.SiteDetail),
			PanFlag:    firstNonEmptyString(strings.TrimSpace(ep.Flag), panFlag),
			Ep:         ep,
			RawName:    firstNonEmptyString(strings.TrimSpace(smart.FirstRawNameFromURL(ep.URL)), strings.TrimSpace(ep.Name)),
		},
	}
	if emit != nil {
		emit(offer)
	}
	return true
}

func collectTMDBManualPlaybackFromDetailCandidates(database *db.DB, userID int64, user *smart.User, ref *itemRef, req smart.PlaybackRequest, item db.SmartManualItem, _ <-chan struct{}, emit func(smart.PlaybackOffer)) bool {
	siteKey := strings.TrimSpace(item.SiteKey)
	siteName := siteKey
	spiderAPI := strings.TrimSpace(item.SpiderAPI)
	siteDetail := strings.TrimSpace(item.SiteDetail)
	seasonHint := strings.TrimSpace(item.SeasonHint)
	if spiderAPI == "" {
		spiderAPI = strings.TrimSpace(smart.ResolveSpiderAPIBySiteKey(database, siteKey))
	}
	if database == nil || user == nil || siteKey == "" || spiderAPI == "" || siteDetail == "" {
		return false
	}
	log.Printf("[emby][manual_detail] item=%s tmdb=%s:%d site=(%s|%s) spider=%s siteDetail=%s", strings.TrimSpace(ref.RawID), strings.TrimSpace(req.Kind), ref.NumericID, siteKey, siteName, spiderAPI, siteDetail)
	rawRecords, requestOK, hasRecords := fetchRawSiteDetailSourceRecordsWithSpiderAPIState(database, userID, siteKey, siteName, spiderAPI, siteDetail, seasonHint)
	if !requestOK {
		log.Printf("[emby][manual_detail_error] item=%s tmdb=%s:%d site=(%s|%s) spider=%s siteDetail=%s", strings.TrimSpace(ref.RawID), strings.TrimSpace(req.Kind), ref.NumericID, siteKey, siteName, spiderAPI, siteDetail)
		return false
	}
	if !hasRecords {
		log.Printf("[emby][manual_detail_error] item=%s tmdb=%s:%d site=(%s|%s) spider=%s siteDetail=%s reason=no_records", strings.TrimSpace(ref.RawID), strings.TrimSpace(req.Kind), ref.NumericID, siteKey, siteName, spiderAPI, siteDetail)
		return true
	}
	found := false
	var foundMu sync.Mutex
	setFound := func() {
		foundMu.Lock()
		found = true
		foundMu.Unlock()
	}
	isFound := func() bool {
		foundMu.Lock()
		defer foundMu.Unlock()
		return found
	}
	settings := smart.LoadPlaybackSettings(database)
	tmdbHasMultiSeason := strings.TrimSpace(req.Kind) == "tv" && strings.TrimSpace(req.SubKind) == "episode" && req.Season > 0
	emitSorted := func(offers []smart.PlaybackOffer) {
		if len(offers) == 0 || emit == nil {
			return
		}
		sort.SliceStable(offers, func(i, j int) bool {
			return smart.CompareSmartMatch(offers[i].Cand, offers[j].Cand, tmdbHasMultiSeason, req.Season, settings) < 0
		})
		for _, offer := range offers {
			emit(offer)
		}
	}

	directOffers := make([]smart.PlaybackOffer, 0, len(rawRecords))
	panMockRecords := make([]smart.DetailSourceRecord, 0, len(rawRecords))
	for _, record := range rawRecords {
		if record.PanMock && record.Supported {
			panMockRecords = append(panMockRecords, record)
			continue
		}
		resolved := buildResolvedSitePans(database, smart.ResolvedRecordsToPans([]smart.DetailSourceRecord{record}))
		for _, panResolved := range resolved {
			offer, ok := buildManualDetailOfferFromResolvedPan(database, ref, req, item, siteKey, siteName, spiderAPI, siteDetail, panResolved)
			if !ok {
				continue
			}
			setFound()
			directOffers = append(directOffers, offer)
			log.Printf("[emby][manual_detail_ok] item=%s tmdb=%s:%d site=(%s|%s) siteDetail=%s panFlag=%s", strings.TrimSpace(ref.RawID), strings.TrimSpace(req.Kind), ref.NumericID, offer.Cand.SiteKey, offer.Cand.SiteName, siteDetail, offer.Cand.PanFlag)
		}
	}
	emitSorted(directOffers)

	if len(panMockRecords) > 0 {
		tmdbSeasons := loadPlaybackTMDBSeasons(database, ref.NumericID)
		rawEpisodeRules, _ := database.ListMagicEpisodeRules()
		rawCleanRules, _ := database.ListMagicEpisodeCleanRegexRules()
		want := 0
		if req.Kind == "tv" && req.Season > 0 && req.Episode > 0 {
			want = smart.TMDBGlobalEpisodeNoOf(tmdbSeasons, req.Season, req.Episode)
		}
		hasMulti := smart.PositiveSeasonCount(tmdbSeasons) > 1
		_, _ = smart.ResolvePanMockSourceRecordsIncremental(database, siteKey, siteName, want, tmdbSeasons, hasMulti, rawCleanRules, rawEpisodeRules, panMockRecords, func(resolved []smart.DetailSourceRecord, accessDelta map[string]string, emitAllowed bool) {
			if !emitAllowed {
				return
			}
			resolvedPans := smart.ResolvedRecordsToPans(resolved)
			resolvedSitePans := buildResolvedSitePans(database, resolvedPans)
			listOffers := make([]smart.PlaybackOffer, 0, len(resolvedSitePans))
			for _, panResolved := range resolvedSitePans {
				offer, ok := buildManualDetailOfferFromResolvedPan(database, ref, req, item, siteKey, siteName, spiderAPI, siteDetail, panResolved)
				if !ok {
					continue
				}
				setFound()
				listOffers = append(listOffers, offer)
				log.Printf("[emby][manual_detail_ok] item=%s tmdb=%s:%d site=(%s|%s) siteDetail=%s panFlag=%s", strings.TrimSpace(ref.RawID), strings.TrimSpace(req.Kind), ref.NumericID, offer.Cand.SiteKey, offer.Cand.SiteName, siteDetail, offer.Cand.PanFlag)
			}
			emitSorted(listOffers)
		})
	}
	if !isFound() {
		log.Printf("[emby][manual_detail_error] item=%s tmdb=%s:%d site=(%s|%s) spider=%s siteDetail=%s reason=no_episode_match", strings.TrimSpace(ref.RawID), strings.TrimSpace(req.Kind), ref.NumericID, siteKey, siteName, spiderAPI, siteDetail)
	}
	return true
}

func buildManualDetailOfferFromResolvedPan(database *db.DB, ref *itemRef, req smart.PlaybackRequest, item db.SmartManualItem, siteKey string, siteName string, spiderAPI string, siteDetail string, pan resolvedSitePan) (smart.PlaybackOffer, bool) {
	ep, matchOK := pickManualEpisodeCandidate(database, ref.NumericID, req, pan.Episodes, strings.TrimSpace(item.SeasonHint))
	if !matchOK {
		return smart.PlaybackOffer{}, false
	}
	rawPanLabel := strings.TrimSpace(pan.RawLabel)
	ep.Flag = firstNonEmptyString(strings.TrimSpace(ep.Flag), rawPanLabel, strings.TrimSpace(item.PanFlag))
	return smart.PlaybackOffer{
		Cand: smart.Candidate{
			Stage:      "manual_detail",
			SiteKey:    strings.TrimSpace(siteKey),
			SiteName:   strings.TrimSpace(siteName),
			SpiderAPI:  strings.TrimSpace(spiderAPI),
			SiteDetail: strings.TrimSpace(siteDetail),
			PanFlag:    firstNonEmptyString(strings.TrimSpace(ep.Flag), rawPanLabel, strings.TrimSpace(item.PanFlag)),
			Ep:         ep,
			RawName:    strings.TrimSpace(smart.FirstRawNameFromURL(ep.URL)),
		},
	}, true
}

func pickManualEpisodeCandidate(database *db.DB, tmdbID int, req smart.PlaybackRequest, episodes []catpawrunner.Episode, _ string) (catpawrunner.Episode, bool) {
	ep, ok := pickHistoryEpisodeCandidate(database, tmdbID, req, episodes)
	if ok {
		return ep, true
	}
	return catpawrunner.Episode{}, false
}

func collectTMDBHistoryPlaybackFromListCandidates(database *db.DB, user *smart.User, ref *itemRef, req smart.PlaybackRequest, hist db.PlayHistoryRow, stopCh <-chan struct{}, emit func(smart.PlaybackOffer)) bool {
	panFlag := strings.TrimSpace(hist.PlayFlag)
	provider := historyListProvider(panFlag)
	if provider == "" || database == nil || user == nil {
		return false
	}
	if IsPlaybackResolveStopped(stopCh) {
		return false
	}
	log.Printf("[emby][history_list] item=%s tmdb=%s:%d panFlag=%s provider=%s", strings.TrimSpace(ref.RawID), strings.TrimSpace(req.Kind), ref.NumericID, panFlag, provider)
	episodes, ok := listHistoryPlayFlagEpisodes(database, provider, panFlag)
	if !ok || len(episodes) == 0 {
		log.Printf("[emby][history_list_error] item=%s tmdb=%s:%d panFlag=%s provider=%s", strings.TrimSpace(ref.RawID), strings.TrimSpace(req.Kind), ref.NumericID, panFlag, provider)
		return false
	}
	ep, ok := pickHistoryEpisodeCandidate(database, ref.NumericID, req, episodes)
	if !ok {
		log.Printf("[emby][history_list_error] item=%s tmdb=%s:%d panFlag=%s provider=%s reason=no_episode_match", strings.TrimSpace(ref.RawID), strings.TrimSpace(req.Kind), ref.NumericID, panFlag, provider)
		return false
	}
	log.Printf(
		"[emby][history_list_ok] item=%s tmdb=%s:%d panFlag=%s provider=%s episodes=%d matchShowName=%s matchRawName=%s err=",
		strings.TrimSpace(ref.RawID),
		strings.TrimSpace(req.Kind),
		ref.NumericID,
		panFlag,
		provider,
		len(episodes),
		strings.TrimSpace(ep.Name),
		strings.TrimSpace(hist.SiteEpisodeFile),
	)
	offer := smart.PlaybackOffer{
		Cand: smart.Candidate{
			Stage:      "history_list",
			SiteKey:    strings.TrimSpace(hist.SiteKey),
			SiteName:   strings.TrimSpace(hist.SiteName),
			SiteDetail: strings.TrimSpace(hist.SiteDetail),
			PanFlag:    firstNonEmptyString(strings.TrimSpace(ep.Flag), panFlag),
			Ep:         ep,
			RawName:    strings.TrimSpace(hist.SiteEpisodeFile),
		},
	}
	if emit != nil {
		emit(offer)
	}
	return true
}

func collectTMDBHistoryPlaybackFromDetailCandidates(database *db.DB, userID int64, user *smart.User, ref *itemRef, req smart.PlaybackRequest, hist db.PlayHistoryRow, stopCh <-chan struct{}, emit func(smart.PlaybackOffer)) bool {
	siteKey := strings.TrimSpace(hist.SiteKey)
	spiderAPI := strings.TrimSpace(hist.SpiderAPI)
	siteDetail := strings.TrimSpace(hist.SiteDetail)
	if database == nil || user == nil || spiderAPI == "" || siteDetail == "" {
		return false
	}
	if IsPlaybackResolveStopped(stopCh) {
		return false
	}
	log.Printf("[emby][history_detail] item=%s tmdb=%s:%d site=(%s|%s) spider=%s siteDetail=%s", strings.TrimSpace(ref.RawID), strings.TrimSpace(req.Kind), ref.NumericID, siteKey, strings.TrimSpace(hist.SiteName), spiderAPI, siteDetail)
	rawRecords, ok := fetchRawSiteDetailSourceRecordsWithSpiderAPI(database, userID, siteKey, strings.TrimSpace(hist.SiteName), spiderAPI, siteDetail)
	if !ok || len(rawRecords) == 0 {
		log.Printf("[emby][history_detail_error] item=%s tmdb=%s:%d site=(%s|%s) spider=%s siteDetail=%s", strings.TrimSpace(ref.RawID), strings.TrimSpace(req.Kind), ref.NumericID, siteKey, strings.TrimSpace(hist.SiteName), spiderAPI, siteDetail)
		return false
	}
	found := false
	var foundMu sync.Mutex
	settings := smart.LoadPlaybackSettings(database)
	tmdbHasMultiSeason := strings.TrimSpace(req.Kind) == "tv" && strings.TrimSpace(req.SubKind) == "episode" && req.Season > 0
	emitSorted := func(offers []smart.PlaybackOffer) {
		if len(offers) == 0 || emit == nil {
			return
		}
		sort.SliceStable(offers, func(i, j int) bool {
			return smart.CompareSmartMatch(offers[i].Cand, offers[j].Cand, tmdbHasMultiSeason, req.Season, settings) < 0
		})
		for _, offer := range offers {
			emit(offer)
		}
	}

	directOffers := make([]smart.PlaybackOffer, 0, len(rawRecords))
	panMockRecords := make([]smart.DetailSourceRecord, 0, len(rawRecords))
	for _, record := range rawRecords {
		if record.PanMock && record.Supported {
			panMockRecords = append(panMockRecords, record)
			continue
		}
		// Non-panmock detail branches consume a single resolved record directly;
		// any Pan conversion here is edge assembly only for existing browse/playback helpers.
		resolved := buildResolvedSitePans(database, smart.ResolvedRecordsToPans([]smart.DetailSourceRecord{record}))
		for _, panResolved := range reorderHistoryPans(resolved, strings.TrimSpace(hist.PlayFlag)) {
			offer, ok := buildHistoryDetailOfferFromResolvedPan(database, ref, req, hist, siteKey, spiderAPI, siteDetail, panResolved)
			if !ok {
				continue
			}
			found = true
			directOffers = append(directOffers, offer)
			log.Printf("[emby][history_detail_ok] item=%s tmdb=%s:%d site=(%s|%s) siteDetail=%s panFlag=%s", strings.TrimSpace(ref.RawID), strings.TrimSpace(req.Kind), ref.NumericID, offer.Cand.SiteKey, offer.Cand.SiteName, siteDetail, offer.Cand.PanFlag)
		}
	}
	emitSorted(directOffers)

	if len(panMockRecords) > 0 {
		tmdbSeasons := loadPlaybackTMDBSeasons(database, ref.NumericID)
		rawEpisodeRules, _ := database.ListMagicEpisodeRules()
		rawCleanRules, _ := database.ListMagicEpisodeCleanRegexRules()
		want := 0
		if req.Kind == "tv" && req.Season > 0 && req.Episode > 0 {
			want = smart.TMDBGlobalEpisodeNoOf(tmdbSeasons, req.Season, req.Episode)
		}
		hasMulti := smart.PositiveSeasonCount(tmdbSeasons) > 1
		_, _ = smart.ResolvePanMockSourceRecordsIncremental(database, siteKey, strings.TrimSpace(hist.SiteName), want, tmdbSeasons, hasMulti, rawCleanRules, rawEpisodeRules, panMockRecords, func(resolved []smart.DetailSourceRecord, accessDelta map[string]string, emitAllowed bool) {
			if !emitAllowed || IsPlaybackResolveStopped(stopCh) {
				return
			}
			// Pan conversion here is a terminal edge adapter into existing
			// resolvedSitePan presentation helpers; the resolve flow itself stays record-first.
			resolvedPans := smart.ResolvedRecordsToPans(resolved)
			resolvedSitePans := buildResolvedSitePans(database, resolvedPans)
			listOffers := make([]smart.PlaybackOffer, 0, len(resolvedSitePans))
			for _, panResolved := range reorderHistoryPans(resolvedSitePans, strings.TrimSpace(hist.PlayFlag)) {
				offer, ok := buildHistoryDetailOfferFromResolvedPan(database, ref, req, hist, siteKey, spiderAPI, siteDetail, panResolved)
				if !ok {
					continue
				}
				foundMu.Lock()
				found = true
				foundMu.Unlock()
				listOffers = append(listOffers, offer)
				log.Printf("[emby][history_detail_ok] item=%s tmdb=%s:%d site=(%s|%s) siteDetail=%s panFlag=%s", strings.TrimSpace(ref.RawID), strings.TrimSpace(req.Kind), ref.NumericID, offer.Cand.SiteKey, offer.Cand.SiteName, siteDetail, offer.Cand.PanFlag)
			}
			emitSorted(listOffers)
		})
	}
	if found {
		return true
	}
	log.Printf("[emby][history_detail_error] item=%s tmdb=%s:%d site=(%s|%s) spider=%s siteDetail=%s reason=no_episode_match", strings.TrimSpace(ref.RawID), strings.TrimSpace(req.Kind), ref.NumericID, siteKey, strings.TrimSpace(hist.SiteName), spiderAPI, siteDetail)
	return false
}

func buildTMDBPlaybackOfferTarget(ref *itemRef, display playbackDisplayMeta, offers []smart.PlaybackOffer, mediaSourceID string, playSessionID string) *PlaybackStreamTarget {
	container := strings.TrimSpace(display.Container)
	if container == "" {
		container = "mp4"
	}
	return &PlaybackStreamTarget{
		Offers:        clonePlaybackOffers(offers),
		Path:          display.Path,
		Container:     container,
		Name:          display.Name,
		Runtime:       display.Runtime,
		Size:          display.Size,
		Bitrate:       display.Bitrate,
		ItemID:        strings.TrimSpace(ref.RawID),
		MediaSourceID: strings.TrimSpace(mediaSourceID),
		PlaySessionID: strings.TrimSpace(playSessionID),
	}
}

func buildHistoryDetailOfferFromResolvedPan(database *db.DB, ref *itemRef, req smart.PlaybackRequest, hist db.PlayHistoryRow, siteKey string, spiderAPI string, siteDetail string, pan resolvedSitePan) (smart.PlaybackOffer, bool) {
	ep, matchOK := pickHistoryEpisodeCandidate(database, ref.NumericID, req, pan.Episodes)
	if !matchOK {
		return smart.PlaybackOffer{}, false
	}
	rawPanLabel := strings.TrimSpace(pan.RawLabel)
	ep.Flag = firstNonEmptyString(strings.TrimSpace(ep.Flag), rawPanLabel, strings.TrimSpace(hist.PlayFlag))
	return smart.PlaybackOffer{
		Cand: smart.Candidate{
			Stage:      "history_detail",
			SiteKey:    siteKey,
			SiteName:   strings.TrimSpace(hist.SiteName),
			SpiderAPI:  spiderAPI,
			SiteDetail: siteDetail,
			PanFlag:    firstNonEmptyString(strings.TrimSpace(ep.Flag), rawPanLabel, strings.TrimSpace(hist.PlayFlag)),
			Ep:         ep,
			RawName:    firstNonEmptyString(strings.TrimSpace(hist.SiteEpisodeFile), strings.TrimSpace(smart.FirstRawNameFromURL(ep.URL))),
		},
	}, true
}

func buildTMDBPlaybackStreamTarget(database *db.DB, user *smart.User, ref *itemRef, display playbackDisplayMeta, picked *smart.PlaybackPickedMeta, finalURL string, finalHeaders map[string]string, r *http.Request) *PlaybackStreamTarget {
	adaptedURL, adaptMode := adaptPlaybackTargetURL(database, user, picked, finalURL, finalHeaders, r)
	if strings.TrimSpace(adaptedURL) != "" {
		finalURL = adaptedURL
	}
	targetURL := strings.TrimSpace(finalURL)
	log.Printf("[emby][playback_target] item=%s mode=%s headers=%d url=%s", strings.TrimSpace(ref.RawID), strings.TrimSpace(adaptMode), len(copyStringMap(finalHeaders)), targetURL)
	container := strings.TrimSpace(display.Container)
	if container == "" {
		container = detectContainerFromPlaybackURL(targetURL)
	}
	if container == "" {
		container = "mp4"
	}
	mediaSourceID := stablePlaybackIdentityFromPicked("media", strings.TrimSpace(ref.RawID), picked)
	playSessionID := stablePlaybackIdentityFromPicked("play", strings.TrimSpace(ref.RawID), picked)
	return &PlaybackStreamTarget{
		FinalURL:         targetURL,
		FinalHeaders:     copyStringMap(finalHeaders),
		Path:             display.Path,
		Container:        container,
		Name:             display.Name,
		Runtime:          display.Runtime,
		Size:             display.Size,
		Bitrate:          display.Bitrate,
		ItemID:           strings.TrimSpace(ref.RawID),
		SiteKey:          strings.TrimSpace(picked.SiteKey),
		SiteDetail:       strings.TrimSpace(picked.SiteDetail),
		PanFlag:          strings.TrimSpace(picked.PanFlag),
		Provider:         strings.TrimSpace(picked.Provider),
		SpiderAPI:        strings.TrimSpace(smart.ResolveSpiderAPIBySiteKey(database, strings.TrimSpace(picked.SiteKey))),
		SiteEpisodeIndex: maxInt(0, ref.Episode),
		SiteEpisodeFile:  strings.TrimSpace(picked.RawName),
		MediaSourceID:    mediaSourceID,
		PlaySessionID:    playSessionID,
	}
}

func resolveSiteEpisodePlaybackStreamTarget(database *db.DB, userID int64, ref *itemRef, r *http.Request) (*PlaybackStreamTarget, bool, error) {
	if database == nil || ref == nil || ref.Source != "site" || ref.SubKind != "episode" {
		return nil, false, nil
	}
	user, err := resolveSmartUser(database, userID)
	if err != nil || user == nil {
		return nil, false, err
	}
	if strings.TrimSpace(ref.SiteKey) == "" || strings.TrimSpace(ref.SiteDetail) == "" || strings.TrimSpace(ref.SiteTitle) == "" || strings.TrimSpace(ref.SiteEpisodeURL) == "" || ref.Pan <= 0 || ref.Episode <= 0 {
		return nil, false, nil
	}
	pan := resolvedSitePan{
		RawLabel:     strings.TrimSpace(ref.SitePlayFlag),
		DisplayLabel: "",
		PanMock:      strings.TrimSpace(smart.PanMockProviderFromLabel(strings.TrimSpace(ref.SitePlayFlag))) != "",
	}
	ep := catpawrunner.Episode{
		URL:  strings.TrimSpace(ref.SiteEpisodeURL),
		Flag: strings.TrimSpace(ref.SitePlayFlag),
	}
	rawURL := strings.TrimSpace(ep.URL)
	if rawURL == "" {
		return nil, false, nil
	}
	flag := firstNonEmptyString(strings.TrimSpace(ref.SitePlayFlag), strings.TrimSpace(ep.Flag))
	finalURL, finalHeaders, picked, err := resolveSiteEpisodeDirectPlayback(database, user, ref, ep, flag)
	if err != nil || strings.TrimSpace(finalURL) == "" || picked == nil {
		return nil, false, err
	}
	return buildSiteEpisodePlaybackStreamTarget(database, user, ref, pan, ep, picked, finalURL, finalHeaders, r), true, nil
}

func buildSiteEpisodePlaybackStreamTarget(database *db.DB, user *smart.User, ref *itemRef, pan resolvedSitePan, ep catpawrunner.Episode, picked *smart.PlaybackPickedMeta, finalURL string, finalHeaders map[string]string, r *http.Request) *PlaybackStreamTarget {
	adaptedURL, adaptMode := adaptPlaybackTargetURL(database, user, picked, finalURL, finalHeaders, r)
	if strings.TrimSpace(adaptedURL) != "" {
		finalURL = adaptedURL
	}
	targetURL := strings.TrimSpace(finalURL)
	log.Printf("[emby][playback_target] item=%s mode=%s headers=%d url=%s", strings.TrimSpace(ref.RawID), strings.TrimSpace(adaptMode), len(copyStringMap(finalHeaders)), targetURL)
	seriesName := resolveSiteSeriesName(database, ref.SiteKey, ref.SiteDetail, siteDetailMeta{})
	if seriesName == "" {
		seriesName = strings.TrimSpace(ref.SiteTitle)
	}
	displayName := siteEpisodeDisplayName(ep, pan.RawLabel, pan.PanMock, seriesName, ref.Episode)
	if displayName == "" {
		displayName = siteEpisodeFileName(ep, "", ref.Episode)
	}
	fileName := siteEpisodeFileName(ep, displayName, ref.Episode) + ".mp4"
	mediaSourceID := stablePlaybackIdentityFromPicked("media", strings.TrimSpace(ref.RawID), picked)
	playSessionID := stablePlaybackIdentityFromPicked("play", strings.TrimSpace(ref.RawID), picked)
	return &PlaybackStreamTarget{
		FinalURL:         targetURL,
		FinalHeaders:     copyStringMap(finalHeaders),
		Path:             VirtualEpisodePath(seriesName, 0, ref.Pan, fileName),
		Container:        firstNonEmptyString(detectContainerFromPlaybackURL(targetURL), "mp4"),
		Name:             displayName,
		Runtime:          0,
		Size:             0,
		Bitrate:          0,
		ItemID:           strings.TrimSpace(ref.RawID),
		SiteKey:          strings.TrimSpace(picked.SiteKey),
		SiteDetail:       strings.TrimSpace(picked.SiteDetail),
		PanFlag:          strings.TrimSpace(picked.PanFlag),
		Provider:         strings.TrimSpace(picked.Provider),
		SpiderAPI:        strings.TrimSpace(smart.ResolveSpiderAPIBySiteKey(database, strings.TrimSpace(picked.SiteKey))),
		SiteEpisodeIndex: maxInt(0, ref.Episode),
		SiteEpisodeFile:  strings.TrimSpace(picked.RawName),
		MediaSourceID:    mediaSourceID,
		PlaySessionID:    playSessionID,
	}
}

func resolveSiteEpisodeDirectPlayback(database *db.DB, user *smart.User, ref *itemRef, ep catpawrunner.Episode, flag string) (finalURL string, finalHeaders map[string]string, picked *smart.PlaybackPickedMeta, err error) {
	if database == nil || user == nil || ref == nil {
		return "", nil, nil, nil
	}
	rawURL := strings.TrimSpace(ep.URL)
	if rawURL == "" {
		return "", nil, nil, nil
	}
	panFlag := strings.TrimSpace(flag)
	provider := strings.TrimSpace(smart.PlayFlagProviderID(panFlag))
	if provider != "" {
		finalURL, finalHeaders, err = smart.ResolvePanProviderPlayback(database, user, provider, panFlag, rawURL, nil, "/MeowFilm")
		if err != nil {
			log.Printf("[emby][site_playback_error] item=%s provider=%s panFlag=%s url=%s err=%v", strings.TrimSpace(ref.RawID), provider, panFlag, smart.ShortURLForLog(rawURL), err)
		}
	} else {
		apiBase := strings.TrimSpace(smart.ResolveCatApiBaseForUser(database, user))
		if apiBase == "" {
			return "", nil, nil, nil
		}
		spiderAPI := strings.TrimSpace(smart.ResolveSpiderAPIBySiteKey(database, ref.SiteKey))
		playPayload := map[string]any{
			"flag":    strings.TrimSpace(ep.Flag),
			"id":      rawURL,
			"siteApi": spiderAPI,
		}
		if siteID := strings.TrimSpace(catpawrunner.ExtractSiteIDFromSpiderAPI(spiderAPI)); siteID != "" {
			playPayload["siteId"] = siteID
		}
		playRaw, playErr := catpawrunner.RequestPlayWithTimeout(apiBase, strings.TrimSpace(user.Username), playPayload, 8*time.Second)
		if playErr != nil {
			log.Printf("[emby][site_playback_error] item=%s provider=site panFlag=%s url=%s err=%v", strings.TrimSpace(ref.RawID), panFlag, smart.ShortURLForLog(rawURL), playErr)
			return "", nil, nil, playErr
		}
		payloadOut := smart.BuildCatpawPlayPayload(playRaw, apiBase, strings.TrimSpace(user.Username))
		finalURL, finalHeaders = netdisk.PlayPayloadURLHeaders(payloadOut)
	}
	if strings.TrimSpace(finalURL) == "" {
		return "", nil, nil, err
	}
	rawName := strings.TrimSpace(smart.FirstRawNameFromURL(rawURL))
	if rawName == "" {
		rawName = strings.TrimSpace(siteEpisodeFileName(ep, "", maxInt(1, ref.Episode)))
	}
	siteName := resolveSiteSeriesName(database, ref.SiteKey, ref.SiteDetail, siteDetailMeta{})
	if siteName == "" {
		siteName = strings.TrimSpace(ref.SiteTitle)
	}
	return strings.TrimSpace(finalURL), copyStringMap(finalHeaders), &smart.PlaybackPickedMeta{
		SiteKey:    strings.TrimSpace(ref.SiteKey),
		SiteName:   siteName,
		SiteDetail: strings.TrimSpace(ref.SiteDetail),
		PanFlag:    panFlag,
		Provider:   provider,
		ShowName:   strings.TrimSpace(ep.Name),
		RawName:    rawName,
	}, nil
}

func fetchRawSiteDetailSourceRecordsWithSpiderAPIState(database *db.DB, userID int64, siteKey string, siteName string, spiderAPI string, siteDetail string, remark string) ([]smart.DetailSourceRecord, bool, bool) {
	if database == nil || spiderAPI == "" || siteDetail == "" {
		return nil, false, false
	}
	apiBase := strings.TrimSpace(smart.ResolveCatApiBaseForUser(database, &smart.User{ID: strconv.FormatInt(userID, 10)}))
	if apiBase == "" {
		return nil, false, false
	}
	raw, err := cache.RequestSpiderDetailDirect(apiBase, strings.TrimSpace(spiderAPI), strings.TrimSpace(siteDetail))
	if err != nil || raw == nil {
		return nil, false, false
	}
	playFrom, playURL := catpawrunner.ExtractDetailPlayFromURL(raw)
	records := smart.BuildDetailSourceRecords(playFrom, playURL, smart.IsPanMockEnabled(raw), siteKey, siteName, spiderAPI, siteDetail, strings.TrimSpace(remark))
	if records == nil {
		records = []smart.DetailSourceRecord{}
	}
	return records, true, len(records) > 0
}

func fetchRawSiteDetailSourceRecordsWithSpiderAPI(database *db.DB, userID int64, siteKey string, siteName string, spiderAPI string, siteDetail string) ([]smart.DetailSourceRecord, bool) {
	records, requestOK, hasRecords := fetchRawSiteDetailSourceRecordsWithSpiderAPIState(database, userID, siteKey, siteName, spiderAPI, siteDetail, "")
	if !requestOK {
		return nil, false
	}
	return records, hasRecords
}

func ConsumePlaybackOffersAndBuildTarget(database *db.DB, userID int64, target PlaybackStreamTarget, cacheKey string, r *http.Request) (*PlaybackStreamTarget, bool, error) {
	if database == nil || userID <= 0 {
		return nil, false, nil
	}
	if strings.TrimSpace(target.FinalURL) != "" {
		out := clonePlaybackTarget(target)
		return &out, true, nil
	}
	user, err := resolveSmartUser(database, userID)
	if err != nil || user == nil {
		return nil, false, err
	}
	resolveKey := "play:" + firstNonEmptyString(strings.TrimSpace(target.MediaSourceID), strings.TrimSpace(cacheKey), strings.TrimSpace(target.ItemID))
	resolved, ok, err := ResolvePlaybackOnce(resolveKey, func() (*PlaybackStreamTarget, bool, error) {
		if len(target.Offers) > 0 {
			for _, offer := range target.Offers {
				built, ok, buildErr := tryBuildPlaybackTargetFromOffer(database, userID, user, target, cacheKey, r, offer)
				if ok && built != nil {
					return built, true, nil
				}
				if buildErr != nil {
					continue
				}
			}
			return nil, false, nil
		}
		stage := ""
		for {
			nextStage := CurrentPlaybackOfferStage(strings.TrimSpace(target.PlaySessionID), strings.TrimSpace(target.MediaSourceID), strings.TrimSpace(cacheKey))
			if nextStage != "" && nextStage != stage {
				stage = nextStage
				log.Printf("[emby][play_stage] item=%s stage=%s", strings.TrimSpace(target.ItemID), stage)
			}
			offer, nextOK := NextPlaybackOffer(strings.TrimSpace(target.PlaySessionID), strings.TrimSpace(target.MediaSourceID), strings.TrimSpace(cacheKey))
			if !nextOK {
				return nil, false, nil
			}
			built, ok, buildErr := tryBuildPlaybackTargetFromOffer(database, userID, user, target, cacheKey, r, offer)
			if ok && built != nil {
				MarkPlaybackDone(strings.TrimSpace(target.PlaySessionID), strings.TrimSpace(target.MediaSourceID), strings.TrimSpace(cacheKey))
				return built, true, nil
			}
			MarkPlaybackOfferFailed(strings.TrimSpace(target.PlaySessionID), strings.TrimSpace(target.MediaSourceID), strings.TrimSpace(cacheKey), offer)
			if buildErr != nil {
				continue
			}
		}
	})
	if !ok || resolved == nil {
		return nil, ok, err
	}
	return resolved, true, err
}

func tryBuildPlaybackTargetFromOffer(database *db.DB, userID int64, user *smart.User, target PlaybackStreamTarget, cacheKey string, r *http.Request, offer smart.PlaybackOffer) (*PlaybackStreamTarget, bool, error) {
	finalURL, finalHeaders, picked, playErr := smart.TryPlaybackOffers(database, user, []smart.PlaybackOffer{offer})
	if playErr != nil || strings.TrimSpace(finalURL) == "" || picked == nil {
		return nil, false, playErr
	}
	ref := parseItemRefAny(strings.TrimSpace(target.ItemID))
	if ref == nil {
		return nil, false, nil
	}
	var built *PlaybackStreamTarget
	if !(ref.Source == "site" && ref.SubKind == "episode") {
		display, ok := buildTMDBPlaybackDisplayMeta(database, userID, ref)
		if ok {
			built = buildTMDBPlaybackStreamTarget(database, user, ref, display, picked, finalURL, finalHeaders, r)
		}
	}
	if built == nil {
		return nil, false, nil
	}
	built.UserID = target.UserID
	built.DeviceID = target.DeviceID
	built.ItemID = target.ItemID
	built.Offers = clonePlaybackOffers(target.Offers)
	RebindPlaybackControlIdentity(target.PlaySessionID, target.MediaSourceID, cacheKey, built.PlaySessionID, built.MediaSourceID)
	embyPlaybackSessions.SetWithReplayKey(*built, cacheKey, playbackSessionTTL)
	return built, true, nil
}

func listHistoryPlayFlagEpisodes(database *db.DB, provider string, playFlag string) ([]catpawrunner.Episode, bool) {
	flag := strings.TrimSpace(playFlag)
	if database == nil || flag == "" {
		return nil, false
	}
	if strings.TrimSpace(provider) == "" {
		return nil, false
	}
	eps, _, _, status, _, err := smart.ResolvePanMockEpisodesBySourceValue(database, flag, "")
	if err != nil || status != "ok" || len(eps) == 0 {
		return nil, false
	}
	for i := range eps {
		eps[i].Flag = flag
	}
	return eps, len(eps) > 0
}

func historyListProvider(playFlag string) string {
	return strings.TrimSpace(smart.PlayFlagProviderID(strings.TrimSpace(playFlag)))
}

func reorderHistoryPans(pans []resolvedSitePan, playFlag string) []resolvedSitePan {
	if len(pans) <= 1 || strings.TrimSpace(playFlag) == "" {
		return pans
	}
	out := make([]resolvedSitePan, 0, len(pans))
	for _, pan := range pans {
		if strings.TrimSpace(pan.RawLabel) == strings.TrimSpace(playFlag) {
			out = append(out, pan)
		}
	}
	for _, pan := range pans {
		if strings.TrimSpace(pan.RawLabel) != strings.TrimSpace(playFlag) {
			out = append(out, pan)
		}
	}
	return out
}

func pickHistoryEpisodeCandidate(database *db.DB, tmdbID int, req smart.PlaybackRequest, episodes []catpawrunner.Episode) (catpawrunner.Episode, bool) {
	if len(episodes) == 0 {
		return catpawrunner.Episode{}, false
	}
	if req.Kind != "tv" || req.Season <= 0 || req.Episode <= 0 {
		for _, ep := range episodes {
			if strings.TrimSpace(ep.URL) != "" {
				return ep, true
			}
		}
		return catpawrunner.Episode{}, false
	}
	tmdbSeasons := loadPlaybackTMDBSeasons(database, tmdbID)
	doubanSeasons := loadPlaybackDoubanSeasons(database, tmdbID)
	rawEpisodeRules, _ := database.ListMagicEpisodeRules()
	rawCleanRules, _ := database.ListMagicEpisodeCleanRegexRules()
	wantGlobal := smart.TMDBGlobalEpisodeNoOf(tmdbSeasons, req.Season, req.Episode)
	sourceMaxEpisode := playbackSourceMaxExtractedEpisode(episodes, rawCleanRules, rawEpisodeRules)
	tmdbSeasonCount := smart.PositiveSeasonCount(tmdbSeasons)
	doubanSeasonCount := smart.PositiveSeasonCount(doubanSeasons)
	allowTMDBSingleBaseline := tmdbSeasonCount >= 2 && doubanSeasonCount == 1
	allowDoubanSingleBaseline := tmdbSeasonCount == 1 && doubanSeasonCount >= 2
	tmdbSourceHasBeyondFallbackBoundary := playbackSourceHasEpisodeBeyondFallbackBoundary(tmdbSeasons, doubanSeasons, sourceMaxEpisode)
	doubanSourceHasBeyondFallbackBoundary := playbackSourceHasEpisodeBeyondFallbackBoundary(doubanSeasons, tmdbSeasons, sourceMaxEpisode)
	matchWanted := func(match smart.SeasonEpisode, global int, ok bool) bool {
		if !ok {
			return false
		}
		if match.Season == req.Season && match.Episode == req.Episode {
			return true
		}
		return wantGlobal > 0 && global == wantGlobal
	}
	for _, ep := range episodes {
		texts := smart.ExtractEpisodeCandidateTexts(ep)
		if len(texts) == 0 {
			continue
		}
		jsMatch, err := magic.MagicEpisodeExtractFromCandidates(texts, rawCleanRules, rawEpisodeRules)
		if err != nil {
			continue
		}
		extracted := smart.SeasonEpisode{Season: jsMatch.Season, Episode: jsMatch.Episode}
		match, global, ok, _, _, _ := smart.ResolveExtractedSeasonEpisodeToGlobal(
			tmdbSeasons,
			nil,
			extracted,
			false,
			"tmdb",
			false,
		)
		if matchWanted(match, global, ok) {
			return ep, true
		}
		match, global, ok, _, _, _ = smart.ResolveExtractedSeasonEpisodeToGlobal(
			doubanSeasons,
			nil,
			extracted,
			false,
			"douban",
			false,
		)
		if matchWanted(match, global, ok) {
			return ep, true
		}
		if allowTMDBSingleBaseline {
			match, global, ok, _, _, _ = smart.ResolveExtractedSeasonEpisodeToGlobal(
				tmdbSeasons,
				doubanSeasons,
				extracted,
				true,
				"tmdb",
				tmdbSourceHasBeyondFallbackBoundary,
			)
			if matchWanted(match, global, ok) {
				return ep, true
			}
		}
		if allowDoubanSingleBaseline {
			match, global, ok, _, _, _ = smart.ResolveExtractedSeasonEpisodeToGlobal(
				doubanSeasons,
				tmdbSeasons,
				extracted,
				true,
				"douban",
				doubanSourceHasBeyondFallbackBoundary,
			)
			if matchWanted(match, global, ok) {
				return ep, true
			}
		}
	}
	return catpawrunner.Episode{}, false
}

func loadPlaybackTMDBSeasons(database *db.DB, tmdbID int) []smart.TMDBSeason {
	if database == nil || tmdbID <= 0 {
		return nil
	}
	detail, _ := metadata_tmdb.GetTVDetails(database, tmdbID)
	if detail == nil || len(detail.Seasons) == 0 {
		return nil
	}
	out := make([]smart.TMDBSeason, 0, len(detail.Seasons))
	for _, season := range detail.Seasons {
		if season.SeasonNumber <= 0 || season.EpisodeCount <= 0 {
			continue
		}
		out = append(out, smart.TMDBSeason{
			Season:       season.SeasonNumber,
			EpisodeCount: season.EpisodeCount,
			Poster:       strings.TrimSpace(season.PosterPath),
		})
	}
	return out
}

func loadPlaybackDoubanSeasons(database *db.DB, tmdbID int) []smart.TMDBSeason {
	if database == nil || tmdbID <= 0 {
		return nil
	}
	hints, err := database.ListTMDBSeasonHints("tv", tmdbID, "douban")
	if err != nil || len(hints) == 0 {
		return nil
	}
	out := make([]smart.TMDBSeason, 0, len(hints))
	for _, hint := range hints {
		if hint.SeasonNumber <= 0 || hint.EpisodeCount <= 0 {
			continue
		}
		out = append(out, smart.TMDBSeason{
			Season:       hint.SeasonNumber,
			EpisodeCount: hint.EpisodeCount,
		})
	}
	return out
}

func playbackSourceMaxExtractedEpisode(episodes []catpawrunner.Episode, rawCleanRules []string, rawEpisodeRules []string) int {
	if len(episodes) == 0 {
		return 0
	}
	maxEpisode := 0
	for _, ep := range episodes {
		texts := smart.ExtractEpisodeCandidateTexts(ep)
		if len(texts) == 0 {
			continue
		}
		jsMatch, err := magic.MagicEpisodeExtractFromCandidates(texts, rawCleanRules, rawEpisodeRules)
		if err != nil {
			continue
		}
		if jsMatch.Episode > maxEpisode {
			maxEpisode = jsMatch.Episode
		}
	}
	return maxEpisode
}

func playbackSourceHasEpisodeBeyondFallbackBoundary(primarySeasons []smart.TMDBSeason, singleBaselineSeasons []smart.TMDBSeason, sourceMaxEpisode int) bool {
	if sourceMaxEpisode <= 0 {
		return false
	}
	boundary := playbackSingleBaselineFallbackBoundary(primarySeasons, singleBaselineSeasons)
	return boundary > 0 && sourceMaxEpisode > boundary
}

func playbackSingleBaselineFallbackBoundary(primarySeasons []smart.TMDBSeason, singleBaselineSeasons []smart.TMDBSeason) int {
	primaryRows := playbackPositiveSeasonRows(primarySeasons)
	primaryMultiSeason := smart.PositiveSeasonCount(primaryRows) >= 2
	baselineSingleSeason := smart.PositiveSeasonCount(singleBaselineSeasons) == 1
	if primaryMultiSeason && baselineSingleSeason && len(primaryRows) > 1 {
		sum := 0
		for i := 0; i < len(primaryRows)-1; i++ {
			sum += primaryRows[i].EpisodeCount
		}
		if sum > 0 {
			return sum
		}
	}
	firstSeasonCount := 0
	for _, season := range primaryRows {
		if season.Season == 1 && season.EpisodeCount > 0 {
			firstSeasonCount = season.EpisodeCount
			break
		}
	}
	return firstSeasonCount
}

func playbackPositiveSeasonRows(seasons []smart.TMDBSeason) []smart.TMDBSeason {
	rows := make([]smart.TMDBSeason, 0, len(seasons))
	for _, season := range seasons {
		if season.Season <= 0 || season.EpisodeCount <= 0 {
			continue
		}
		rows = append(rows, season)
	}
	sort.SliceStable(rows, func(i, j int) bool { return rows[i].Season < rows[j].Season })
	return rows
}

func adaptPlaybackTargetURL(database *db.DB, user *smart.User, picked *smart.PlaybackPickedMeta, finalURL string, finalHeaders map[string]string, r *http.Request) (string, string) {
	rawURL := strings.TrimSpace(finalURL)
	headers := copyStringMap(finalHeaders)
	if rawURL == "" || len(headers) == 0 || database == nil {
		return rawURL, "raw"
	}

	cfg, _ := database.ReadAppConfig()
	provider := ""
	if picked != nil {
		provider = strings.TrimSpace(picked.Provider)
	}
	if cfg.RelayEnabled && r != nil {
		payload := netdisk.BuildPlayPayload(rawURL, headers)
		resolveURL := strings.TrimSpace(netdisk.IssueRelayResolveURLFromPayload(r, payload))
		relayServers, _ := database.ListRelayServers()
		relayEligible := relay.EligibleServers(relayServers)
		if resolveURL != "" && len(relayEligible) > 0 {
			if out := strings.TrimSpace(relay.BuildPlaybackURL(relayEligible[0].Base, resolveURL, relayEligible[0].Secret)); out != "" {
				log.Printf("[emby][playback_target_pick] mode=function provider=%s url=%s", strings.TrimSpace(provider), smart.ShortURLForLog(out))
				return out, "function"
			}
			log.Printf("[emby][playback_target_skip] mode=function provider=%s reason=build_failed", strings.TrimSpace(provider))
		} else {
			log.Printf("[emby][playback_target_skip] mode=function provider=%s reason=not_eligible", strings.TrimSpace(provider))
		}
	} else {
		log.Printf("[emby][playback_target_skip] mode=function provider=%s reason=disabled", strings.TrimSpace(provider))
	}

	if cfg.GoProxyEnabled {
		if proxiedURL, ok, err := goproxy.ProxyIfNeeded(database, normalizePlaybackPanProvider(provider), rawURL, headers); err == nil && ok && strings.TrimSpace(proxiedURL) != "" {
			log.Printf("[emby][playback_target_pick] mode=goproxy provider=%s url=%s", strings.TrimSpace(provider), smart.ShortURLForLog(proxiedURL))
			return strings.TrimSpace(proxiedURL), "goproxy"
		}
		log.Printf("[emby][playback_target_skip] mode=goproxy provider=%s reason=register_failed", strings.TrimSpace(provider))
	} else {
		log.Printf("[emby][playback_target_skip] mode=goproxy provider=%s reason=disabled", strings.TrimSpace(provider))
	}

	return rawURL, "raw"
}

func normalizePlaybackPanProvider(provider string) string {
	switch strings.ToLower(strings.TrimSpace(provider)) {
	case "bd", "baidu":
		return "baidu"
	case "quark":
		return "quark"
	default:
		return ""
	}
}

type playbackDisplayMeta struct {
	Path      string
	Container string
	Name      string
	Runtime   int64
	Size      int64
	Bitrate   int
}

func buildTMDBPlaybackRequest(database *db.DB, userID int64, ref *itemRef) (*smart.PlaybackRequest, *playbackDisplayMeta, bool) {
	if database == nil || ref == nil || ref.Source != "tmdb" {
		return nil, nil, false
	}
	switch {
	case ref.MediaType == "movie" && ref.SubKind == "movie":
		return buildTMDBMoviePlaybackRequest(database, userID, ref)
	case ref.MediaType == "tv" && ref.SubKind == "episode":
		return buildTMDBEpisodePlaybackRequest(database, userID, ref.NumericID, ref.Pan, ref.Episode, ref.RawID)
	case ref.MediaType == "tv" && ref.SubKind == "series":
		hist, _ := database.GetPlayHistoryLatestByTMDB(userID, "tv", ref.NumericID)
		view, err := loadTVNextUpView(database, ref.NumericID, hist, 1)
		if err != nil || view == nil || len(view.Candidates) == 0 {
			return nil, nil, false
		}
		candidate := view.Candidates[0]
		return buildTMDBEpisodePlaybackRequest(database, userID, ref.NumericID, candidate.Season, candidate.Episode.EpisodeNumber, ref.RawID)
	case ref.MediaType == "tv" && ref.SubKind == "season":
		view, err := loadTVSeasonEpisodesView(database, ref.NumericID, ref.Pan, false)
		if err != nil || view == nil || view.Season == nil || len(view.Season.Episodes) == 0 {
			return nil, nil, false
		}
		ep := view.Season.Episodes[0]
		return buildTMDBEpisodePlaybackRequest(database, userID, ref.NumericID, ref.Pan, ep.EpisodeNumber, ref.RawID)
	default:
		return nil, nil, false
	}
}

func buildTMDBMoviePlaybackRequest(database *db.DB, userID int64, ref *itemRef) (*smart.PlaybackRequest, *playbackDisplayMeta, bool) {
	req := &smart.PlaybackRequest{
		Kind:    "movie",
		TMDBID:  ref.NumericID,
		SubKind: "movie",
	}
	hist, _ := database.GetPlayHistoryLatestByTMDB(userID, "movie", ref.NumericID)
	path := ""
	container := ""
	name := ""
	runtime := int64(0)
	size := int64(0)
	bitrate := 0
	if hist != nil {
		runtime = hist.PlaybackRuntimeTicks
	}
	if strings.TrimSpace(name) == "" {
		if detail, _ := metadata_tmdb.GetMovieDetails(database, ref.NumericID); detail != nil && strings.TrimSpace(detail.Title) != "" {
			name = strings.TrimSpace(detail.Title)
			if runtime <= 0 && detail.Runtime > 0 {
				runtime = int64(detail.Runtime) * 60 * 10_000_000
			}
			path = VirtualMoviePath(name, YearFromDate(strings.TrimSpace(detail.ReleaseDate)), name+".mp4")
		}
	}
	if strings.TrimSpace(name) == "" {
		name = strings.TrimSpace(ref.RawID)
	}
	if path == "" {
		path = VirtualMoviePath(name, 0, name+".mp4")
	}
	return req, &playbackDisplayMeta{
		Path:      path,
		Container: container,
		Name:      name,
		Runtime:   runtime,
		Size:      size,
		Bitrate:   bitrate,
	}, true
}

func buildTMDBEpisodePlaybackRequest(database *db.DB, userID int64, tmdbID int, seasonNo int, episodeNo int, itemID string) (*smart.PlaybackRequest, *playbackDisplayMeta, bool) {
	if tmdbID <= 0 || seasonNo <= 0 || episodeNo <= 0 {
		return nil, nil, false
	}
	req := &smart.PlaybackRequest{
		Kind:    "tv",
		TMDBID:  tmdbID,
		Season:  seasonNo,
		Episode: episodeNo,
		SubKind: "episode",
	}
	hist, _ := database.GetPlayHistoryLatestByTMDB(userID, "tv", tmdbID)
	view, _ := loadTVResumeEpisodeView(database, tmdbID, seasonNo, episodeNo)
	path := ""
	container := ""
	name := ""
	runtime := int64(0)
	size := int64(0)
	bitrate := 0
	if hist != nil {
		runtime = hist.PlaybackRuntimeTicks
	}
	if view != nil && view.Episode != nil {
		if strings.TrimSpace(name) == "" {
			name = strings.TrimSpace(view.Episode.Name)
		}
		if runtime <= 0 && view.Episode.Runtime > 0 {
			runtime = int64(view.Episode.Runtime) * 60 * 10_000_000
		}
		if strings.TrimSpace(path) == "" {
			seriesTitle := ""
			seriesYear := 0
			if view.Series != nil {
				seriesTitle = strings.TrimSpace(view.Series.Title)
				seriesYear = YearFromDate(strings.TrimSpace(view.Series.Release))
			}
			path = VirtualEpisodePath(seriesTitle, seriesYear, seasonNo, playbackEpisodeFileName(view))
		}
	}
	if strings.TrimSpace(name) == "" {
		name = strings.TrimSpace(itemID)
	}
	if path == "" {
		path = VirtualEpisodePath("", 0, seasonNo, name+".mp4")
	}
	return req, &playbackDisplayMeta{
		Path:      path,
		Container: container,
		Name:      name,
		Runtime:   runtime,
		Size:      size,
		Bitrate:   bitrate,
	}, true
}

func buildTMDBPlaybackDisplayMeta(database *db.DB, userID int64, ref *itemRef) (playbackDisplayMeta, bool) {
	_, display, ok := buildTMDBPlaybackRequest(database, userID, ref)
	if !ok || display == nil {
		return playbackDisplayMeta{}, false
	}
	return *display, true
}

func playbackEpisodeFileName(view *TVResumeEpisodeView) string {
	if view == nil || view.Episode == nil {
		return ""
	}
	base := strings.TrimSpace(view.Episode.Name)
	if base == "" {
		base = "episode"
	}
	return base + ".mp4"
}

func buildPlaybackDirectPath(itemID string, container string, apiToken string, mediaSourceID string, playSessionID string, r *http.Request) string {
	directPath := "/videos/" + strings.TrimSpace(itemID) + "/original." + directStreamExt(container)
	q := make([]string, 0, 4)
	if r != nil {
		if deviceID := strings.TrimSpace(r.URL.Query().Get("DeviceId")); deviceID != "" {
			q = append(q, "DeviceId="+httpQueryEscape(deviceID))
		}
	}
	if mediaSourceID != "" {
		q = append(q, "MediaSourceId="+httpQueryEscape(mediaSourceID))
	}
	if playSessionID != "" {
		q = append(q, "PlaySessionId="+httpQueryEscape(playSessionID))
	}
	if tok := strings.TrimSpace(apiToken); tok != "" {
		q = append(q, "api_key="+httpQueryEscape(tok))
	}
	if len(q) > 0 {
		directPath += "?" + strings.Join(q, "&")
	}
	return directPath
}

func buildPlaybackActionStaticVideoPath(itemID string) string {
	id := strings.TrimSpace(itemID)
	if id == "" {
		return ""
	}
	name := strings.TrimSpace(resolveTMDBSettingsStaticBaseName(id))
	if name == "" {
		return ""
	}
	return "/emby/static/settings/videos/" + neturl.PathEscape(name) + ".mp4"
}

func resolveSmartUser(database *db.DB, userID int64) (*smart.User, error) {
	row, err := database.GetUserAuthByID(userID)
	if err != nil {
		return nil, err
	}
	return &smart.User{
		ID:       strconv.FormatInt(row.ID, 10),
		Username: strings.TrimSpace(row.Username),
		Role:     strings.TrimSpace(row.Role),
		Status:   strings.TrimSpace(row.Status),
	}, nil
}

func copyStringMap(in map[string]string) map[string]string {
	if len(in) == 0 {
		return nil
	}
	out := make(map[string]string, len(in))
	for k, v := range in {
		kk := strings.TrimSpace(k)
		vv := strings.TrimSpace(v)
		if kk == "" || vv == "" {
			continue
		}
		out[kk] = vv
	}
	if len(out) == 0 {
		return nil
	}
	return out
}

func playbackSessionKey(itemID string, mediaSourceID string, playSessionID string) string {
	itemID = strings.TrimSpace(itemID)
	mediaSourceID = strings.TrimSpace(mediaSourceID)
	playSessionID = strings.TrimSpace(playSessionID)
	if itemID == "" || mediaSourceID == "" || playSessionID == "" {
		return ""
	}
	return itemID + "|" + mediaSourceID + "|" + playSessionID
}

func randomPlaybackSessionID() string {
	var b [16]byte
	if _, err := rand.Read(b[:]); err != nil {
		return StableMD5Hex(strconv.FormatInt(time.Now().UnixNano(), 10))
	}
	return hex.EncodeToString(b[:])
}

func stablePlaybackIdentityFromPicked(kind string, itemID string, picked *smart.PlaybackPickedMeta) string {
	if picked == nil {
		return StableMD5Hex(strings.TrimSpace(kind) + "|" + strings.TrimSpace(itemID))
	}
	provider := strings.TrimSpace(smart.PlayFlagProviderID(strings.TrimSpace(picked.PanFlag)))
	fileIdentity := normalizePlaybackFileIdentity(strings.TrimSpace(picked.RawName))
	var key string
	if provider != "" {
		key = stablePlaybackIdentityForListFlag(kind, itemID, provider, strings.TrimSpace(picked.PanFlag), fileIdentity)
	} else {
		key = stablePlaybackIdentityForSiteBound(kind, itemID, strings.TrimSpace(picked.SiteKey), strings.TrimSpace(picked.SiteDetail), strings.TrimSpace(picked.PanFlag), fileIdentity)
	}
	if strings.TrimSpace(key) == "" {
		return StableMD5Hex(strings.TrimSpace(kind) + "|" + strings.TrimSpace(itemID))
	}
	return StableMD5Hex(key)
}

func stablePlaybackIdentityForListFlag(kind string, itemID string, provider string, panFlag string, fileIdentity string) string {
	if strings.TrimSpace(fileIdentity) == "" {
		return strings.Join([]string{
			strings.TrimSpace(kind),
			strings.TrimSpace(itemID),
			strings.TrimSpace(provider),
			strings.TrimSpace(panFlag),
		}, "|")
	}
	return strings.Join([]string{
		strings.TrimSpace(kind),
		strings.TrimSpace(itemID),
		strings.TrimSpace(provider),
		strings.TrimSpace(panFlag),
		strings.TrimSpace(fileIdentity),
	}, "|")
}

func stablePlaybackIdentityForSiteBound(kind string, itemID string, siteKey string, siteDetail string, panFlag string, fileIdentity string) string {
	parts := []string{
		strings.TrimSpace(kind),
		strings.TrimSpace(itemID),
		strings.TrimSpace(siteKey),
		strings.TrimSpace(siteDetail),
		strings.TrimSpace(panFlag),
	}
	if strings.TrimSpace(fileIdentity) != "" {
		parts = append(parts, strings.TrimSpace(fileIdentity))
	}
	return strings.Join(parts, "|")
}

func normalizePlaybackFileIdentity(rawName string) string {
	s := strings.TrimSpace(rawName)
	if s == "" {
		return ""
	}
	s = strings.ReplaceAll(s, "\\", "/")
	s = path.Base(s)
	s = strings.ToLower(strings.TrimSpace(s))
	if s == "." || s == "/" {
		return ""
	}
	return strings.Join(strings.Fields(s), " ")
}

func playbackInfoSupportsTranscoding(r *http.Request) bool {
	if r == nil {
		return true
	}
	type reqBody struct {
		EnableTranscoding *bool `json:"EnableTranscoding"`
	}
	var body reqBody
	if err := jsonNewDecoderNoEscape(r).Decode(&body); err == nil && body.EnableTranscoding != nil {
		return *body.EnableTranscoding
	}
	return true
}

func directStreamExt(container string) string {
	c := strings.TrimSpace(strings.ToLower(container))
	c = strings.TrimPrefix(c, ".")
	if c == "" {
		return "mp4"
	}
	if strings.Contains(c, ",") {
		c = strings.TrimSpace(strings.Split(c, ",")[0])
	}
	return c
}

func firstNonEmptyString(values ...string) string {
	for _, value := range values {
		if s := strings.TrimSpace(value); s != "" {
			return s
		}
	}
	return ""
}

func httpQueryEscape(s string) string {
	replacer := strings.NewReplacer(
		"%", "%25",
		" ", "%20",
		"+", "%2B",
		"&", "%26",
		"=", "%3D",
		"?", "%3F",
		"#", "%23",
	)
	return replacer.Replace(strings.TrimSpace(s))
}

func detectContainerFromPlaybackURL(raw string) string {
	u, err := neturl.Parse(strings.TrimSpace(raw))
	if err != nil {
		return ""
	}
	ext := strings.TrimPrefix(strings.ToLower(path.Ext(u.Path)), ".")
	switch ext {
	case "mp4", "mkv", "m4v", "mov", "ts", "flv":
		return ext
	default:
		return ""
	}
}

func jsonNewDecoderNoEscape(r *http.Request) *json.Decoder {
	if r == nil || r.Body == nil {
		return json.NewDecoder(strings.NewReader("{}"))
	}
	return json.NewDecoder(r.Body)
}
