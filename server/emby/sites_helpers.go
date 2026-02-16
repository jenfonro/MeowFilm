package emby

import "github.com/jenfonro/meowfilm/server/sites"

type site = sites.Site

func parseJSONBoolMap(text string) map[string]bool { return sites.ParseJSONBoolMap(text) }

func normalizeSitesFromJSON(text string) []site { return sites.ParseSitesFromJSON(text) }

func applySiteOrder(sitesList []site, order []string) []site {
	return sites.ApplySiteOrder(sitesList, order)
}

func isConfigCenterSite(s site) bool { return sites.IsConfigCenterSite(s) }
