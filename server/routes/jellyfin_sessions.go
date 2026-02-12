package routes

import (
	"database/sql"
	"fmt"
	"net/http"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
)

// Jellyfin clients may report playback state via /Sessions/Playing*, but MeowFilm
// doesn't track sessions yet. We record minimal play history timestamps/ticks for future integration.
func handleJellyfinSessions(w http.ResponseWriter, r *http.Request, database *db.DB, serverID string, parts []string) {
	u, ok := jellyfinRequireUser(w, r, database)
	if !ok {
		return
	}

	if len(parts) == 0 {
		http.NotFound(w, r)
		return
	}

	head := strings.ToLower(strings.TrimSpace(parts[0]))
	tail := ""
	if len(parts) >= 2 {
		tail = strings.ToLower(strings.TrimSpace(parts[1]))
	}

	if head == "playing" && (tail == "" || tail == "progress" || tail == "stopped") {
		if r.Method != http.MethodPost {
			methodNotAllowed(w)
			return
		}
		_ = jellyfinRecordPlayHistoryFromSession(r, database, u)
		w.WriteHeader(http.StatusNoContent)
		return
	}

	http.NotFound(w, r)
}

type jellyfinNowPlayingItem struct {
	Id              string `json:"Id"`
	Name            string `json:"Name"`
	SeriesName      string `json:"SeriesName"`
	RunTimeTicks    int64  `json:"RunTimeTicks"`
	IndexNumber     int    `json:"IndexNumber"`
	ParentIndexNo   int    `json:"ParentIndexNumber"`
	ProductionYear  int    `json:"ProductionYear"`
	PremiereDateRaw string `json:"PremiereDate"`
}

type jellyfinSessionReport struct {
	ItemId        string                  `json:"ItemId"`
	PositionTicks int64                   `json:"PositionTicks"`
	NowPlaying    *jellyfinNowPlayingItem `json:"NowPlayingItem"`
}

type jellyfinBasicMeta struct {
	Expire time.Time
	Title  string
	Poster string
	Year   int
}

var jellyfinBasicMetaCache = struct {
	sync.Mutex
	M map[string]jellyfinBasicMeta
}{
	M: map[string]jellyfinBasicMeta{},
}

const jellyfinBasicMetaTTL = 6 * time.Hour

func jellyfinCachedBasicMeta(key string) (jellyfinBasicMeta, bool) {
	now := time.Now()
	jellyfinBasicMetaCache.Lock()
	defer jellyfinBasicMetaCache.Unlock()
	if jellyfinBasicMetaCache.M == nil {
		jellyfinBasicMetaCache.M = map[string]jellyfinBasicMeta{}
		return jellyfinBasicMeta{}, false
	}
	hit, ok := jellyfinBasicMetaCache.M[key]
	if !ok {
		return jellyfinBasicMeta{}, false
	}
	if !hit.Expire.IsZero() && hit.Expire.Before(now) {
		delete(jellyfinBasicMetaCache.M, key)
		return jellyfinBasicMeta{}, false
	}
	return hit, true
}

func jellyfinRememberBasicMeta(key string, v jellyfinBasicMeta) {
	jellyfinBasicMetaCache.Lock()
	if jellyfinBasicMetaCache.M == nil {
		jellyfinBasicMetaCache.M = map[string]jellyfinBasicMeta{}
	}
	jellyfinBasicMetaCache.M[key] = v
	jellyfinBasicMetaCache.Unlock()
}

func jellyfinRecordPlayHistoryFromSession(r *http.Request, database *db.DB, u *jellyfinUser) error {
	if r == nil || database == nil || database.SQL() == nil || u == nil {
		return nil
	}

	var dto jellyfinSessionReport
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

	parsed, ok := jellyfinParseItemID(itemID)
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
		videoID = jellyfinBuildSeriesID(parsed.TMDBID)
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
		if hit, ok := jellyfinCachedBasicMeta(metaKey); ok {
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
				d, err := jellyfinTMDBGetTVDetail(database, tmdbID)
				if err == nil && d != nil {
					title = strings.TrimSpace(d.Title)
					poster = strings.TrimSpace(d.Poster)
					year = d.Year
				}
			} else if tmdbType == "movie" {
				d, err := jellyfinTMDBGetMovieDetail(database, tmdbID)
				if err == nil && d != nil {
					title = strings.TrimSpace(d.Title)
					poster = strings.TrimSpace(d.Poster)
					year = d.Year
				}
			}
			jellyfinRememberBasicMeta(metaKey, jellyfinBasicMeta{
				Expire: time.Now().Add(jellyfinBasicMetaTTL),
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
		videoPoster = jellyfinTMDBImageURL(videoPoster, "w500")
	}

	contentKey := strings.TrimSpace(strings.ToLower(fmt.Sprintf("tmdb:%s:%d", tmdbType, tmdbID)))
	if contentKey == "tmdb::0" || contentKey == "tmdb:0:0" || tmdbID <= 0 || tmdbType == "" {
		contentKey = "jellyfin::" + strings.ToLower(videoID)
	}

	now := time.Now().Unix()

	_, err := database.SQL().Exec(`
		INSERT INTO play_history(
		  user_id, content_key, site_key, site_name, spider_api, video_id, video_title, video_poster, video_remark,
		  tmdb_id, tmdb_type, pan_label, play_flag, episode_index, episode_name, updated_at,
		  playback_position_ticks, playback_runtime_ticks
		)
		VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)
		ON CONFLICT(user_id, site_key, video_id) DO UPDATE SET
		  content_key = excluded.content_key,
		  video_title = excluded.video_title,
		  video_poster = CASE WHEN excluded.video_poster <> '' THEN excluded.video_poster ELSE play_history.video_poster END,
		  video_remark = excluded.video_remark,
		  tmdb_id = excluded.tmdb_id,
		  tmdb_type = excluded.tmdb_type,
		  episode_index = excluded.episode_index,
		  episode_name = excluded.episode_name,
		  updated_at = excluded.updated_at,
		  playback_position_ticks = excluded.playback_position_ticks,
		  playback_runtime_ticks = excluded.playback_runtime_ticks
	`, userID, contentKey,
		"jellyfin", "Jellyfin", "jellyfin", videoID, videoTitle, videoPoster, videoRemark,
		tmdbID, tmdbType, "", "jellyfin", episodeIndex, episodeName, now,
		position, runtime,
	)
	if err != nil && err != sql.ErrNoRows {
		return err
	}
	return nil
}
