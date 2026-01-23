package routes

import (
	"encoding/json"
	"strings"

	"github.com/jenfonro/TV_Server/internal/auth"
	"github.com/jenfonro/TV_Server/internal/db"
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

	// Shared/admin users without their own CatPawOpen: use global list, but keep per-user enable/home/order.
	if !hasUserAPI && canFallback {
		reconciled := reconcileSites(sites, statusMap, homeMap, nil, order, availability)
		bSites, _ := json.Marshal(reconciled.Sites)
		bStatus, _ := json.Marshal(reconciled.Status)
		bHome, _ := json.Marshal(reconciled.Home)
		bOrder, _ := json.Marshal(reconciled.Order)
		bAvail, _ := json.Marshal(reconciled.Availability)
		_, _ = database.SQL().Exec(`
			UPDATE users
			SET cat_sites = ?, cat_site_status = ?, cat_site_home = ?, cat_site_order = ?, cat_site_availability = ?
			WHERE id = ?
		`, string(bSites), string(bStatus), string(bHome), string(bOrder), string(bAvail), u.ID)

		sites = reconciled.Sites
		statusMap = reconciled.Status
		homeMap = reconciled.Home
		order = reconciled.Order
		availability = reconciled.Availability
	}

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
