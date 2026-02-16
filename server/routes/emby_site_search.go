package routes

import (
	"sort"
	"strings"
	"sync"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/catpawopen"
)

type embySiteSearchHit struct {
	SiteKey   string
	SiteName  string
	SpiderAPI string
	VideoID   string
	Name      string
	Pic       string
	Remark    string

	Score     int
	TitleLen  int
	SiteOrder int
	Seq       int
}

// embySearchSitesHits runs a best-effort, concurrent search across all enabled sites and returns a
// ranked flat list of hits within maxWait. Any results that would arrive after the deadline are discarded.
//
// Note: this function intentionally does NOT truncate the result set; callers should page on the final list.
func embySearchSitesHits(database *db.DB, u *embyUser, query string, maxWait time.Duration, limit int) []embySiteSearchHit {
	if database == nil {
		return nil
	}
	q := strings.TrimSpace(query)
	if q == "" {
		return nil
	}
	if maxWait <= 0 {
		maxWait = 3 * time.Second
	}
	if limit <= 0 {
		limit = 24
	}

	apiBase := strings.TrimSpace(embyResolveCatApiBaseForUser(database, u))
	if apiBase == "" {
		return nil
	}

	sites := normalizeSitesFromJSON(database.GetSetting("video_source_sites"))
	statusMap := parseJSONBoolMap(database.GetSetting("video_source_site_status"))
	searchMap := parseJSONBoolMap(database.GetSetting("video_source_site_search"))
	ordered := applySiteOrder(sites, smartLoadSiteOrder(database, u))

	searchSites := make([]site, 0, len(ordered))
	for _, s := range ordered {
		if s.Key == "" || s.API == "" {
			continue
		}
		if isConfigCenterSite(s) {
			continue
		}
		if enabled, ok := statusMap[s.Key]; ok && !enabled {
			continue
		}
		if searchEnabled, ok := searchMap[s.Key]; ok && !searchEnabled {
			continue
		}
		searchSites = append(searchSites, s)
	}
	if len(searchSites) == 0 {
		return nil
	}

	startAt := time.Now()
	deadline := startAt.Add(maxWait)

	type job struct{ S site }
	type result struct {
		Site site
		List []catpawopen.SearchItem
	}

	expected := len(searchSites)
	jobs := make(chan job, expected)
	results := make(chan result, expected)

	workers := smartGetSearchThreadCount(database, u)
	if workers < 1 {
		workers = 5
	}
	if workers > 20 {
		workers = 20
	}

	var wg sync.WaitGroup
	for i := 0; i < workers; i++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			for jb := range jobs {
				remain := time.Until(deadline)
				if remain <= 0 {
					continue
				}
				raw, err := catpawopen.RequestSpiderWithTimeout(apiBase, jb.S.API, "search", map[string]any{"wd": q, "page": 1}, remain)
				if err != nil || raw == nil {
					continue
				}
				items := catpawopen.NormalizeSearchList(raw)
				if len(items) == 0 {
					continue
				}
				select {
				case results <- result{Site: jb.S, List: items}:
				default:
				}
			}
		}()
	}

	for _, s := range searchSites {
		jobs <- job{S: s}
	}
	close(jobs)

	go func() {
		wg.Wait()
		close(results)
	}()

	out := make([]embySiteSearchHit, 0, limit*2)
	timer := time.NewTimer(time.Until(deadline))
	defer timer.Stop()

	siteOrder := map[string]int{}
	for i, s := range searchSites {
		if s.Key != "" {
			siteOrder[s.Key] = i
		}
	}

	seq := 0
	apply := func(s site, items []catpawopen.SearchItem) {
		for _, it := range items {
			vid := strings.TrimSpace(it.ID)
			name := strings.TrimSpace(it.Name)
			if vid == "" || name == "" {
				continue
			}
			score := embyComputeMatchScore(q, name)
			if score <= 0 {
				continue
			}
			seq++
			so := siteOrder[s.Key]
			out = append(out, embySiteSearchHit{
				SiteKey:   s.Key,
				SiteName:  s.Name,
				SpiderAPI: s.API,
				VideoID:   vid,
				Name:      name,
				Pic:       strings.TrimSpace(it.Pic),
				Remark:    strings.TrimSpace(it.Remark),
				Score:     score,
				TitleLen:  embyTitleLenForSort(name),
				SiteOrder: so,
				Seq:       seq,
			})
		}
	}

	for {
		select {
		case r, ok := <-results:
			if !ok {
				goto done
			}
			apply(r.Site, r.List)
		case <-timer.C:
			goto done
		}
	}

done:
	sort.SliceStable(out, func(i, j int) bool {
		a := out[i]
		b := out[j]
		// Primary: short-to-long titles (closest to query tends to be shorter).
		if a.TitleLen != b.TitleLen {
			return a.TitleLen < b.TitleLen
		}
		// Secondary: relevance score (frontend computeMatchScore).
		if a.Score != b.Score {
			return a.Score > b.Score
		}
		// Prefer earlier (higher-priority) sites when score ties.
		if a.SiteOrder != b.SiteOrder {
			return a.SiteOrder < b.SiteOrder
		}
		// Deterministic tie-breakers to keep ordering stable across runs.
		if a.SiteKey != b.SiteKey {
			return a.SiteKey < b.SiteKey
		}
		if a.VideoID != b.VideoID {
			return a.VideoID < b.VideoID
		}
		return a.Seq < b.Seq
	})
	return out
}
