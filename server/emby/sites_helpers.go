package emby

import "github.com/jenfonro/meowfilm/server/smart"

type site = smart.Site

func applySiteOrder(sitesList []site, order []string) []site {
	return smart.ApplySiteOrder(sitesList, order)
}

func isConfigCenterSite(s site) bool { return smart.IsConfigCenterSite(s) }
