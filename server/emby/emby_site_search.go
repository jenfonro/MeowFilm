package emby

import (
	"sort"
	"strings"
	"sync"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/cache"
	"github.com/jenfonro/meowfilm/server/catpawrunner"
)

type embySiteSearchHit struct {
	SiteKey    string
	SiteName   string
	SpiderAPI  string
	SiteDetail string
	Name       string
	Pic        string
	Remark     string

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

	rawSites, _ := database.ListVideoSourceSites()
	sites := make([]site, 0, len(rawSites))
	for _, s := range rawSites {
		sites = append(sites, site{Key: s.Key, Name: s.Name, API: s.API, Type: s.Type})
	}
	states, _ := database.ReadVideoSourceSiteStates()
	statusMap := map[string]bool{}
	searchMap := map[string]bool{}
	for k, st := range states {
		if strings.TrimSpace(k) == "" {
			continue
		}
		statusMap[k] = st.Enabled
		searchMap[k] = st.Search
	}
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
		List []catpawrunner.SearchItem
	}

	expected := len(searchSites)
	jobs := make(chan job, expected)
	results := make(chan result, expected)

	workers := len(searchSites)
	if workers < 1 {
		workers = 1
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
				raw, err := cache.RequestSpiderSearchCachedWithTimeout(apiBase, jb.S.API, q, 1, remain)
				if err != nil || raw == nil {
					continue
				}
				items := catpawrunner.NormalizeSearchList(raw)
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
	apply := func(s site, items []catpawrunner.SearchItem) {
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
				SiteKey:    s.Key,
				SiteName:   s.Name,
				SpiderAPI:  s.API,
				SiteDetail: vid,
				Name:       name,
				Pic:        strings.TrimSpace(it.Pic),
				Remark:     strings.TrimSpace(it.Remark),
				Score:      score,
				TitleLen:   embyTitleLenForSort(name),
				SiteOrder:  so,
				Seq:        seq,
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
		// Primary: relevance score (same as frontend computeMatchScore).
		if a.Score != b.Score {
			return a.Score > b.Score
		}
		// Prefer earlier (higher-priority) sites when score ties.
		if a.SiteOrder != b.SiteOrder {
			return a.SiteOrder < b.SiteOrder
		}
		// Prefer shorter titles after relevance + site order tie.
		if a.TitleLen != b.TitleLen {
			return a.TitleLen < b.TitleLen
		}
		// Deterministic tie-breakers to keep ordering stable across runs.
		if a.SiteKey != b.SiteKey {
			return a.SiteKey < b.SiteKey
		}
		if a.SiteDetail != b.SiteDetail {
			return a.SiteDetail < b.SiteDetail
		}
		return a.Seq < b.Seq
	})
	return out
}
