package routes

import (
	"strings"

	"github.com/jenfonro/meowfilm/internal/auth"
	"github.com/jenfonro/meowfilm/internal/db"
)

type userCatSitesState struct {
	Sites        []site
	Status       map[string]bool
	Home         map[string]bool
	Order        []string
	Availability map[string]string
	HasUserAPI   bool
	CanFallback  bool
}

func resolveUserCatSites(database *db.DB, u *auth.User) (userCatSitesState, error) {
	var row struct {
		CatSites        string
		CatStatus       string
		CatHome         string
		CatOrder        string
		CatAvailability string
		CatAPIBase      string
	}
	if err := database.SQL().QueryRow(`
		SELECT cat_sites, cat_site_status, cat_site_home, cat_site_order, cat_site_availability, cat_api_base
		FROM users WHERE id = ? LIMIT 1
	`, u.ID).Scan(&row.CatSites, &row.CatStatus, &row.CatHome, &row.CatOrder, &row.CatAvailability, &row.CatAPIBase); err != nil {
		return userCatSitesState{}, err
	}

	hasUserAPI := strings.TrimSpace(row.CatAPIBase) != ""
	canFallback := u.Role != "user"

	var sites []site
	if hasUserAPI {
		sites = normalizeSitesFromJSON(row.CatSites)
	} else if canFallback {
		sites = normalizeSitesFromJSON(database.GetSetting("video_source_sites"))
	} else {
		sites = []site{}
	}

	statusMap := parseJSONBoolMap(row.CatStatus)
	homeMap := parseJSONBoolMap(row.CatHome)
	order := parseJSONStringArray(row.CatOrder)
	availability := parseAvailabilityJSON(row.CatAvailability)

	return userCatSitesState{
		Sites:        sites,
		Status:       statusMap,
		Home:         homeMap,
		Order:        order,
		Availability: availability,
		HasUserAPI:   hasUserAPI,
		CanFallback:  canFallback,
	}, nil
}

func userHasCatAPIBase(database *db.DB, u *auth.User) (bool, error) {
	if database == nil || u == nil {
		return false, nil
	}
	var base string
	if err := database.SQL().QueryRow(`SELECT cat_api_base FROM users WHERE id=? LIMIT 1`, u.ID).Scan(&base); err != nil {
		return false, err
	}
	return strings.TrimSpace(base) != "", nil
}

func userSiteKeySet(sites []site) map[string]struct{} {
	set := map[string]struct{}{}
	for _, s := range sites {
		if s.Key == "" {
			continue
		}
		set[s.Key] = struct{}{}
	}
	return set
}
