package emby

import (
	"fmt"
	"net/http"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
)

func handleEmbySessions(w http.ResponseWriter, r *http.Request, database *db.DB, serverID string, parts []string) {
	u, ok := embyRequireUser(w, r, database)
	if !ok {
		return
	}
	debug := embyDebugLogEnabled()

	if len(parts) == 0 {
		embyNotFound(w)
		return
	}

	head := strings.ToLower(strings.TrimSpace(parts[0]))
	tail := ""
	if len(parts) >= 2 {
		tail = strings.ToLower(strings.TrimSpace(parts[1]))
	}

	if head == "playing" && (tail == "" || tail == "progress" || tail == "stopped") {
		if r.Method != http.MethodPost {
			embyMethodNotAllowed(w)
			return
		}
		if err := embyRecordPlayHistoryFromSession(r, database, u); err != nil && debug {
			embyDebugPrintf("[emby][sessions] record play history failed err=%q", err.Error())
		}
		w.WriteHeader(http.StatusNoContent)
		return
	}

	embyNotFound(w)
}

type embyNowPlayingItem struct {
	Id              string `json:"Id"`
	Name            string `json:"Name"`
	SeriesName      string `json:"SeriesName"`
	RunTimeTicks    int64  `json:"RunTimeTicks"`
	IndexNumber     int    `json:"IndexNumber"`
	ParentIndexNo   int    `json:"ParentIndexNumber"`
	ProductionYear  int    `json:"ProductionYear"`
	PremiereDateRaw string `json:"PremiereDate"`
}

type embySessionReport struct {
	ItemId        string              `json:"ItemId"`
	PositionTicks int64               `json:"PositionTicks"`
	NowPlaying    *embyNowPlayingItem `json:"NowPlayingItem"`
}

type embyBasicMeta struct {
	Expire time.Time
	Title  string
	Poster string
	Year   int
}

var embyBasicMetaCache = struct {
	sync.Mutex
	M map[string]embyBasicMeta
}{
	M: map[string]embyBasicMeta{},
}

const embyBasicMetaTTL = 6 * time.Hour

func embyCachedBasicMeta(key string) (embyBasicMeta, bool) {
	now := time.Now()
	embyBasicMetaCache.Lock()
	defer embyBasicMetaCache.Unlock()
	hit, ok := embyBasicMetaCache.M[key]
	if !ok {
		return embyBasicMeta{}, false
	}
	if !hit.Expire.IsZero() && hit.Expire.Before(now) {
		delete(embyBasicMetaCache.M, key)
		return embyBasicMeta{}, false
	}
	return hit, true
}

func embyRememberBasicMeta(key string, v embyBasicMeta) {
	embyBasicMetaCache.Lock()
	defer embyBasicMetaCache.Unlock()
	embyBasicMetaCache.M[key] = v
}

func embyRecordPlayHistoryFromSession(r *http.Request, database *db.DB, u *embyUser) error {
	if r == nil || database == nil || database.SQL() == nil || u == nil {
		return nil
	}

	var dto embySessionReport
	if err := readJSONLoose(r, &dto); err != nil {
		return nil
	}

	itemID := strings.TrimSpace(dto.ItemId)
	if itemID == "" && dto.NowPlaying != nil {
		itemID = strings.TrimSpace(dto.NowPlaying.Id)
	}
	if itemID == "" {
		return nil
	}
	position := dto.PositionTicks
	if position < 0 {
		position = 0
	}
	runtime := int64(0)
	if dto.NowPlaying != nil && dto.NowPlaying.RunTimeTicks > 0 {
		runtime = dto.NowPlaying.RunTimeTicks
	}

	parsed, ok := embyParseItemID(itemID)
	if !ok || parsed == nil {
		return nil
	}
	if parsed.SubKind == "series" || parsed.SubKind == "season" {
		return nil
	}

	userID, _ := strconv.ParseInt(strings.TrimSpace(u.ID), 10, 64)
	if userID <= 0 {
		return nil
	}

	tmdbID := parsed.TMDBID
	tmdbType := ""
	if parsed.Kind == "tv" {
		tmdbType = "tv"
	} else if parsed.Kind == "movie" {
		tmdbType = "movie"
	}

	videoID := strings.TrimSpace(itemID)
	episodeIndex := 0
	episodeName := ""
	videoTitle := ""
	videoPoster := ""
	videoRemark := ""

	// Prefer data already present in session payload.
	if dto.NowPlaying != nil {
		if tmdbType == "tv" && strings.TrimSpace(dto.NowPlaying.SeriesName) != "" {
			videoTitle = strings.TrimSpace(dto.NowPlaying.SeriesName)
		}
		if videoTitle == "" && strings.TrimSpace(dto.NowPlaying.Name) != "" {
			videoTitle = strings.TrimSpace(dto.NowPlaying.Name)
		}
		if dto.NowPlaying.ProductionYear > 0 {
			videoRemark = strconv.Itoa(dto.NowPlaying.ProductionYear)
		}
	}

	if parsed.Kind == "tv" && parsed.SubKind == "episode" {
		videoID = embyBuildSeriesID(parsed.TMDBID)
		episodeIndex = parsed.Episode
		if dto.NowPlaying != nil && strings.TrimSpace(dto.NowPlaying.Name) != "" {
			episodeName = strings.TrimSpace(dto.NowPlaying.Name)
		}
		if episodeName == "" && parsed.Episode > 0 {
			episodeName = fmt.Sprintf("S%02dE%02d", parsed.Season, parsed.Episode)
		}
	}

	// Fill missing title/poster from TMDB (cached).
	metaKey := tmdbType + ":" + strconv.Itoa(tmdbID)
	if (videoTitle == "" || videoPoster == "" || videoRemark == "") && tmdbID > 0 && tmdbType != "" {
		if hit, ok := embyCachedBasicMeta(metaKey); ok {
			if videoTitle == "" {
				videoTitle = hit.Title
			}
			if videoPoster == "" {
				videoPoster = hit.Poster
			}
			if videoRemark == "" && hit.Year > 0 {
				videoRemark = strconv.Itoa(hit.Year)
			}
		} else {
			title := ""
			poster := ""
			year := 0
			if tmdbType == "tv" {
				d, err := embyTMDBGetTVDetail(database, tmdbID)
				if err == nil && d != nil {
					title = strings.TrimSpace(d.Title)
					poster = strings.TrimSpace(d.Poster)
					year = d.Year
				}
			} else if tmdbType == "movie" {
				d, err := embyTMDBGetMovieDetail(database, tmdbID)
				if err == nil && d != nil {
					title = strings.TrimSpace(d.Title)
					poster = strings.TrimSpace(d.Poster)
					year = d.Year
				}
			}
			embyRememberBasicMeta(metaKey, embyBasicMeta{
				Expire: time.Now().Add(embyBasicMetaTTL),
				Title:  title,
				Poster: poster,
				Year:   year,
			})
			if videoTitle == "" {
				videoTitle = title
			}
			if videoPoster == "" {
				videoPoster = poster
			}
			if videoRemark == "" && year > 0 {
				videoRemark = strconv.Itoa(year)
			}
		}
	}

	if videoTitle == "" {
		videoTitle = itemID
	}
	if videoPoster != "" {
		videoPoster = embyTMDBImageURL(database, videoPoster, "w500")
	}

	contentKey := strings.TrimSpace(strings.ToLower(fmt.Sprintf("tmdb:%s:%d", tmdbType, tmdbID)))
	if contentKey == "tmdb::0" || contentKey == "tmdb:0:0" || tmdbID <= 0 || tmdbType == "" {
		contentKey = "emby::" + strings.ToLower(videoID)
	}

	now := time.Now().Unix()
	return database.UpsertPlayHistory(db.PlayHistoryUpsert{
		UserID:                userID,
		ContentKey:            contentKey,
		SiteKey:               "emby",
		SiteName:              "Emby",
		SpiderAPI:             "emby",
		VideoID:               videoID,
		VideoTitle:            videoTitle,
		VideoPoster:           videoPoster,
		VideoRemark:           videoRemark,
		TMDBID:                tmdbID,
		TMDBType:              tmdbType,
		PanLabel:              "",
		PlayFlag:              "emby",
		EpisodeIndex:          episodeIndex,
		EpisodeName:           episodeName,
		UpdatedAt:             now,
		PlaybackPositionTicks: position,
		PlaybackRuntimeTicks:  runtime,
		PlaybackItemID:        itemID,
	})
}
