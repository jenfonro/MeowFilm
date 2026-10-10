package smart

import (
	"context"
	"sort"
	"strings"
	"sync"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/catpawrunner"
)

func smartNavigationOptions(settings smartPlaybackSettings, stop func() bool) catpawrunner.NavigationOptions {
	return catpawrunner.NavigationOptions{
		Sequential: true, ShouldStop: stop,
		Order: func(items []any) []any {
			rank := func(value any) int {
				item, _ := value.(map[string]any)
				nav, ok := catpawrunner.DetailNavigation(item)
				if !ok {
					return len(settings.PanTokenOrderLower) + 2
				}
				label := nav.ShareFlag
				if label == "" {
					label = map[string]string{"baidu": "百度", "quark": "夸克", "uc": "UC", "189": "天翼", "139": "移动"}[nav.Provider]
				}
				idx := smartLabelRuleIdx(label, settings.PanTokenOrderLower, settings.PanMatchEntries)
				if idx >= 0 {
					return idx
				}
				if nav.Provider != "" {
					return len(settings.PanTokenOrderLower)
				}
				return len(settings.PanTokenOrderLower) + 1
			}
			sort.SliceStable(items, func(i, j int) bool { return rank(items[i]) < rank(items[j]) })
			return items
		},
	}
}

// Only adapts a newly discovered navigation leaf to the existing source-record
// and candidate builders. Their matching/episode/ranking rules are unchanged.
func smartNavigationLeafEntry(database *db.DB, src smartSource, raw map[string]any,
	primarySeasons, baseline []smartTMDBSeason, hasMulti bool, settings smartPlaybackSettings,
	cleanRules, episodeRules []string, allowSingle bool, primaryKind string,
) *smartDetailCacheEntry {
	mode := smartIsPanMockEnabled(raw)
	from, url := catpawrunner.ExtractDetailPlayFromURL(raw)
	records := smartBuildDetailSourceRecords(from, url, mode, src)
	access := map[string]string{}
	if mode {
		records, access = smartResolvePanMockSourceRecords(database, src.SiteKey, src.SiteName, 0,
			primarySeasons, hasMulti, cleanRules, episodeRules, records)
	}
	if len(cleanRules) == 0 || len(episodeRules) == 0 {
		return &smartDetailCacheEntry{OK: false}
	}
	epMap, epLoose, _ := smartBuildCandidatesFromResolvedRecords(src, records, false,
		primarySeasons, baseline, hasMulti, settings, cleanRules, episodeRules, nil, allowSingle, primaryKind)
	return &smartDetailCacheEntry{OK: true, Source: src, SourceRecords: records,
		PanMockEnabled: mode, PanMock189AccessByShareID: access, EpisodeMap: epMap, EpisodeMapLoose: epLoose}
}

func smartNavigationHasPlayableResult(result *smartPickResult) bool {
	return result != nil && strings.TrimSpace(result.PlayURL) != ""
}

// NavigationOfferWait provides backpressure only for new navigation leaves.
// Existing playback consumers report their normal success/failure; the walker
// neither changes candidate ranking nor treats enqueueing as playable success.
type NavigationOfferWait func(context.Context, []PlaybackOffer)

func smartWalkNavigationOffers(ctx context.Context, raw map[string]any, settings smartPlaybackSettings,
	shouldStop func() bool, request func(string, map[string]any) (map[string]any, error),
	consume func(map[string]any, func(smartCandidateOffer)), wait NavigationOfferWait,
) error {
	stopped := func() bool { return ctx.Err() != nil || (shouldStop != nil && shouldStop()) }
	return catpawrunner.WalkDetailNavigation(raw, request, func(leaf map[string]any) bool {
		var mu sync.Mutex
		var offers []smartCandidateOffer
		consume(leaf, func(offer smartCandidateOffer) {
			mu.Lock()
			offers = append(offers, offer)
			mu.Unlock()
		})
		if wait != nil && len(offers) > 0 && !stopped() {
			wait(ctx, offers)
		}
		return stopped()
	}, smartNavigationOptions(settings, stopped))
}
