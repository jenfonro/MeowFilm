package smart

import (
	"errors"
	"strings"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/metadata/tmdb"
)

type smartDoubanTMDBMap struct {
	Kind       string // "movie" | "tv"
	DoubanID   string
	Title      string
	Year       int
	TMDBID     int
	TMDBKind   string
	LastTryAt  int64
	LastTryKey string
	UpdatedAt  int64
}

func smartGetDoubanTMDBMap(database *db.DB, kind string, doubanID string) (*smartDoubanTMDBMap, error) {
	if database == nil {
		return nil, errors.New("db nil")
	}
	k := strings.TrimSpace(kind)
	id := strings.TrimSpace(doubanID)
	if k == "" || id == "" {
		return nil, errors.New("invalid args")
	}
	row, err := database.GetDoubanTMDBMap(k, id)
	if err != nil || row == nil {
		return nil, err
	}
	return &smartDoubanTMDBMap{
		Kind:       row.Kind,
		DoubanID:   row.DoubanID,
		Title:      row.Title,
		Year:       row.Year,
		TMDBID:     row.TMDBID,
		TMDBKind:   row.TMDBKind,
		LastTryAt:  row.LastTryAt,
		LastTryKey: row.LastTryKey,
		UpdatedAt:  row.UpdatedAt,
	}, nil
}

func smartUpsertDoubanTMDBMap(database *db.DB, m smartDoubanTMDBMap) error {
	if database == nil {
		return errors.New("db nil")
	}
	k := strings.TrimSpace(m.Kind)
	id := strings.TrimSpace(m.DoubanID)
	if k == "" || id == "" {
		return errors.New("invalid args")
	}
	title := strings.TrimSpace(m.Title)
	year := m.Year
	tmdbID := m.TMDBID
	tmdbKind := strings.TrimSpace(m.TMDBKind)
	lastTryAt := m.LastTryAt
	if lastTryAt <= 0 {
		lastTryAt = 0
	}
	lastTryKey := strings.TrimSpace(m.LastTryKey)
	err := database.UpsertDoubanTMDBMap(db.DoubanTMDBMap{
		Kind:       k,
		DoubanID:   id,
		Title:      title,
		Year:       year,
		TMDBID:     tmdbID,
		TMDBKind:   tmdbKind,
		LastTryAt:  lastTryAt,
		LastTryKey: lastTryKey,
	})
	if err == nil && tmdbID > 0 && (tmdbKind == "tv" || tmdbKind == "movie") {
		_ = database.UpsertTMDBExternalID(tmdbKind, tmdbID, "douban", id)
	}
	return err
}

func smartResolveTMDBForDouban(database *db.DB, kind string, doubanID string, title string, year int) (tmdbID int, err error) {
	k := strings.TrimSpace(kind)
	id := strings.TrimSpace(doubanID)
	if k == "" || id == "" {
		return 0, errors.New("invalid args")
	}
	tryKeyBase := tmdb.ResolveAPIBase(database)

	existing, err := smartGetDoubanTMDBMap(database, k, id)
	if err != nil {
		return 0, err
	}
	if existing != nil && existing.TMDBID > 0 {
		return existing.TMDBID, nil
	}

	q := strings.TrimSpace(title)
	if q == "" && existing != nil {
		q = strings.TrimSpace(existing.Title)
	}
	yy := year
	if yy <= 0 && existing != nil {
		yy = existing.Year
	}
	if q == "" {
		// Persist at least the existence so future calls can fill it.
		_ = smartUpsertDoubanTMDBMap(database, smartDoubanTMDBMap{
			Kind:       k,
			DoubanID:   id,
			Title:      title,
			Year:       year,
			LastTryAt:  time.Now().UnixMilli(),
			LastTryKey: tryKeyBase,
		})
		return 0, nil
	}

	cands := smartNormalizeTitleForTMDBCandidates(k, q)
	if len(cands) == 0 {
		return 0, nil
	}
	qPrimary := strings.TrimSpace(cands[0])
	qUsed := qPrimary
	// Include candidates hash in the throttling key, so a new normalization rule can retry immediately.
	tryKey := strings.TrimSpace(tryKeyBase) + "|" + smartStableHex32(strings.Join(cands, "\n"))

	// Throttle failing lookups to avoid hammering TMDB, but allow retry when our query changes
	// (e.g. after stripping "第X季/Season X" suffix) or when the stored year/title is stale.
	if existing != nil && existing.TMDBID <= 0 && existing.LastTryAt > 0 {
		sameTitle := false
		for _, qq := range cands {
			if strings.TrimSpace(existing.Title) == strings.TrimSpace(qq) {
				sameTitle = true
				break
			}
		}
		sameYear := existing.Year == yy || yy <= 0 || existing.Year <= 0
		sameKey := strings.TrimSpace(existing.LastTryKey) != "" && strings.TrimSpace(existing.LastTryKey) == strings.TrimSpace(tryKey)
		if sameTitle && sameYear && sameKey && time.Since(time.UnixMilli(existing.LastTryAt)) < 12*time.Hour {
			return 0, nil
		}
	}

	pickBest := func(items []embyTMDBSearchItem) int {
		best := 0
		for _, it := range items {
			if it.ID <= 0 || it.MediaType != k {
				continue
			}
			// First pass: strict year match when we have a year (if TMDB doesn't return a year, don't accept it here).
			if yy > 0 && yy != it.Year {
				continue
			}
			best = it.ID
			break
		}
		if best != 0 {
			return best
		}
		for _, it := range items {
			if it.ID <= 0 || it.MediaType != k {
				continue
			}
			return it.ID
		}
		return 0
	}

	// Search TMDB (try a few normalized query variants), then cache.
	best := 0
	for _, qq := range cands {
		items, err := embyTMDBSearchMulti(database, qq)
		if err != nil {
			_ = smartUpsertDoubanTMDBMap(database, smartDoubanTMDBMap{
				Kind:       k,
				DoubanID:   id,
				Title:      qPrimary,
				Year:       yy,
				LastTryAt:  time.Now().UnixMilli(),
				LastTryKey: tryKey,
			})
			return 0, err
		}
		best = pickBest(items)
		if best > 0 {
			qUsed = strings.TrimSpace(qq)
			break
		}
	}

	_ = smartUpsertDoubanTMDBMap(database, smartDoubanTMDBMap{
		Kind:       k,
		DoubanID:   id,
		Title:      qUsed,
		Year:       yy,
		TMDBID:     best,
		TMDBKind:   k,
		LastTryAt:  time.Now().UnixMilli(),
		LastTryKey: tryKey,
	})
	return best, nil
}
