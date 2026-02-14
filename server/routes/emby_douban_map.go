package routes

import (
	"database/sql"
	"errors"
	"strings"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
)

type embyDoubanTMDBMap struct {
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

func embyGetDoubanTMDBMap(database *db.DB, kind string, doubanID string) (*embyDoubanTMDBMap, error) {
	if database == nil {
		return nil, errors.New("db nil")
	}
	k := strings.TrimSpace(kind)
	id := strings.TrimSpace(doubanID)
	if k == "" || id == "" {
		return nil, errors.New("invalid args")
	}
	var row embyDoubanTMDBMap
	err := database.SQL().QueryRow(`
		SELECT kind, douban_id, title, year, tmdb_id, tmdb_kind, last_try_at, last_try_key, updated_at
		FROM douban_tmdb_map
		WHERE kind=? AND douban_id=?
		LIMIT 1
	`, k, id).Scan(&row.Kind, &row.DoubanID, &row.Title, &row.Year, &row.TMDBID, &row.TMDBKind, &row.LastTryAt, &row.LastTryKey, &row.UpdatedAt)
	if err != nil {
		if err == sql.ErrNoRows {
			return nil, nil
		}
		return nil, err
	}
	return &row, nil
}

func embyUpsertDoubanTMDBMap(database *db.DB, m embyDoubanTMDBMap) error {
	if database == nil {
		return errors.New("db nil")
	}
	k := strings.TrimSpace(m.Kind)
	id := strings.TrimSpace(m.DoubanID)
	if k == "" || id == "" {
		return errors.New("invalid args")
	}
	now := time.Now().UnixMilli()
	title := strings.TrimSpace(m.Title)
	year := m.Year
	tmdbID := m.TMDBID
	tmdbKind := strings.TrimSpace(m.TMDBKind)
	lastTryAt := m.LastTryAt
	if lastTryAt <= 0 {
		lastTryAt = 0
	}
	lastTryKey := strings.TrimSpace(m.LastTryKey)
	_, err := database.SQL().Exec(`
		INSERT INTO douban_tmdb_map(kind, douban_id, title, year, tmdb_id, tmdb_kind, last_try_at, last_try_key, updated_at)
		VALUES(?,?,?,?,?,?,?,?,?)
		ON CONFLICT(kind, douban_id) DO UPDATE SET
		  title = CASE WHEN excluded.title != '' THEN excluded.title ELSE douban_tmdb_map.title END,
		  year = CASE WHEN excluded.year > 0 THEN excluded.year ELSE douban_tmdb_map.year END,
		  tmdb_id = CASE WHEN excluded.tmdb_id > 0 THEN excluded.tmdb_id ELSE douban_tmdb_map.tmdb_id END,
		  tmdb_kind = CASE WHEN excluded.tmdb_kind != '' THEN excluded.tmdb_kind ELSE douban_tmdb_map.tmdb_kind END,
		  last_try_at = CASE WHEN excluded.last_try_at > 0 THEN excluded.last_try_at ELSE douban_tmdb_map.last_try_at END,
		  last_try_key = CASE WHEN excluded.last_try_key != '' THEN excluded.last_try_key ELSE douban_tmdb_map.last_try_key END,
		  updated_at = excluded.updated_at
	`, k, id, title, year, tmdbID, tmdbKind, lastTryAt, lastTryKey, now)
	return err
}

func embyResolveTMDBForDouban(database *db.DB, kind string, doubanID string, title string, year int) (tmdbID int, err error) {
	k := strings.TrimSpace(kind)
	id := strings.TrimSpace(doubanID)
	if k == "" || id == "" {
		return 0, errors.New("invalid args")
	}
	tryKeyBase := resolveTMDBAPIBase(database)

	existing, err := embyGetDoubanTMDBMap(database, k, id)
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
		_ = embyUpsertDoubanTMDBMap(database, embyDoubanTMDBMap{
			Kind:       k,
			DoubanID:   id,
			Title:      title,
			Year:       year,
			LastTryAt:  time.Now().UnixMilli(),
			LastTryKey: tryKeyBase,
		})
		return 0, nil
	}

	cands := embyNormalizeTitleForTMDBCandidates(k, q)
	if len(cands) == 0 {
		return 0, nil
	}
	qPrimary := strings.TrimSpace(cands[0])
	qUsed := qPrimary
	// Include candidates hash in the throttling key, so a new normalization rule can retry immediately.
	tryKey := strings.TrimSpace(tryKeyBase) + "|" + embyStableHex32(strings.Join(cands, "\n"))

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
			_ = embyUpsertDoubanTMDBMap(database, embyDoubanTMDBMap{
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

	_ = embyUpsertDoubanTMDBMap(database, embyDoubanTMDBMap{
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
