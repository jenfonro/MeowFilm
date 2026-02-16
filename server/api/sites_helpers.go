package api

import "github.com/jenfonro/meowfilm/server/sites"

type site = sites.Site

type reconciledSiteState = sites.ReconciledState

func normalizeSitesFromJSON(text string) []site { return sites.ParseSitesFromJSON(text) }

func applySiteOrder(list []site, order []string) []site { return sites.ApplySiteOrder(list, order) }

func defaultHomeForSite(s site) bool { return sites.DefaultHomeForSite(s) }

func normalizeAvailability(v string) string { return sites.NormalizeAvailability(v) }

func isConfigCenterSite(s site) bool { return sites.IsConfigCenterSite(s) }

func mergeSitesWithState(sitesList []site, statusMap map[string]bool, homeMap map[string]bool, order []string, availability map[string]any, searchMap map[string]bool, errorMap map[string]string) []map[string]any {
	return sites.MergeSitesWithState(sitesList, statusMap, homeMap, order, availability, searchMap, errorMap)
}

func parseAvailabilityJSON(text string) map[string]string { return sites.ParseAvailabilityJSON(text) }

func reconcileSites(nextSites []site, prevStatus map[string]bool, prevHome map[string]bool, prevSearch map[string]bool, prevOrder []string, prevAvailability map[string]string) reconciledSiteState {
	return sites.ReconcileSites(nextSites, prevStatus, prevHome, prevSearch, prevOrder, prevAvailability)
}

func marshalJSON(v any) string { return sites.MarshalJSON(v) }
