package api

import (
	"sort"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/sites"
)

func resolveSearchCoverSite(sites []map[string]any, preferredRaw string) string {
	preferred := strings.TrimSpace(preferredRaw)
	keySet := map[string]struct{}{}
	enabledFirst := ""
	first := ""
	for _, s := range sites {
		k, _ := s["key"].(string)
		k = strings.TrimSpace(k)
		if k == "" {
			continue
		}
		if first == "" {
			first = k
		}
		keySet[k] = struct{}{}
		if enabledFirst == "" {
			enabled, _ := s["enabled"].(bool)
			if enabled {
				enabledFirst = k
			}
		}
	}
	if preferred != "" {
		if _, ok := keySet[preferred]; ok {
			return preferred
		}
	}
	if enabledFirst != "" {
		return enabledFirst
	}
	return first
}

func mergeVideoSourceSites(database *db.DB) []map[string]any {
	if database == nil {
		return []map[string]any{}
	}
	rawSites, err := database.ListVideoSourceSites()
	if err != nil || len(rawSites) == 0 {
		return []map[string]any{}
	}
	states, _ := database.ReadVideoSourceSiteStates()

	sitesList := make([]sites.Site, 0, len(rawSites))
	for _, s := range rawSites {
		sitesList = append(sitesList, sites.Site{Key: s.Key, Name: s.Name, API: s.API, Type: s.Type})
	}

	statusMap := map[string]bool{}
	homeMap := map[string]bool{}
	searchMap := map[string]bool{}
	availabilityAny := map[string]any{}
	errorMap := map[string]string{}

	type decorated struct {
		k string
		o int
		i int
	}
	ds := make([]decorated, 0, len(sitesList))
	for i, s := range sitesList {
		st, ok := states[s.Key]
		if ok {
			statusMap[s.Key] = st.Enabled
			homeMap[s.Key] = st.Home
			searchMap[s.Key] = st.Search
			if strings.TrimSpace(st.Availability) != "" {
				availabilityAny[s.Key] = st.Availability
			}
			if strings.TrimSpace(st.Error) != "" {
				errorMap[s.Key] = st.Error
			}
			ds = append(ds, decorated{k: s.Key, o: st.OrderIndex, i: i})
		} else {
			statusMap[s.Key] = true
			homeMap[s.Key] = sites.DefaultHomeForSite(s)
			searchMap[s.Key] = false
			ds = append(ds, decorated{k: s.Key, o: 1_000_000_000, i: i})
		}
	}
	sort.Slice(ds, func(i, j int) bool {
		if ds[i].o != ds[j].o {
			return ds[i].o < ds[j].o
		}
		return ds[i].i < ds[j].i
	})
	order := make([]string, 0, len(ds))
	for _, d := range ds {
		order = append(order, d.k)
	}

	return sites.MergeSitesWithState(sitesList, statusMap, homeMap, order, availabilityAny, searchMap, errorMap)
}
