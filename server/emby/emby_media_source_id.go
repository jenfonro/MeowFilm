package emby

import "strings"

func embyComputeMediaSourceID(userID string, deviceID string, itemID string) string {
	u := strings.TrimSpace(userID)
	d := strings.TrimSpace(deviceID)
	i := strings.TrimSpace(itemID)
	if i == "" {
		return ""
	}
	// Isolate by user+device to avoid cross-user collisions when playback sources differ.
	return embyStableHex32(u + "|" + d + "|" + i)
}

func embyComputeStatelessMediaSourceID(userID string, itemID string) string {
	u := strings.TrimSpace(userID)
	i := strings.TrimSpace(itemID)
	if i == "" {
		return ""
	}
	// Stateless site IDs already embed source identity; keep stable across devices.
	return embyStableHex32(u + "||" + i)
}

// embyComputeMediaSourceIDForItem chooses a stable MediaSourceId strategy based on item kind.
// - TMDB/Douban items: isolate by user+device+item
// - Stateless site items: stable across devices (user+item)
func embyComputeMediaSourceIDForItem(userID string, deviceID string, itemID string) string {
	if strings.TrimSpace(itemID) == "" {
		return ""
	}
	// If it looks like a canonical TMDB/Douban id, keep per-device isolation.
	if parsed, ok := embyParseItemID(strings.TrimSpace(itemID)); ok && parsed != nil {
		return embyComputeMediaSourceID(userID, deviceID, itemID)
	}
	// Stateless site IDs: stable across devices.
	if _, _, _, ok := embyParseSiteEpisodeIDV2(strings.TrimSpace(itemID)); ok {
		return embyComputeStatelessMediaSourceID(userID, itemID)
	}
	if _, _, ok := embyParseSiteSeasonIDV2(strings.TrimSpace(itemID)); ok {
		return embyComputeStatelessMediaSourceID(userID, itemID)
	}
	if _, ok := embyParseSiteSeriesIDV2(strings.TrimSpace(itemID)); ok {
		return embyComputeStatelessMediaSourceID(userID, itemID)
	}
	return embyComputeMediaSourceID(userID, deviceID, itemID)
}
