package emby_service

import (
	"sync"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/catpawrunner"
	"github.com/jenfonro/meowfilm/server/smart"
)

func resolveSiteDetailPansForBrowse(database *db.DB, rawPans []catpawrunner.Pan) ([]catpawrunner.Pan, error) {
	if len(rawPans) == 0 {
		return []catpawrunner.Pan{}, nil
	}
	out := make([]catpawrunner.Pan, len(rawPans))
	copy(out, rawPans)

	var wg sync.WaitGroup
	for idx := range out {
		if !out[idx].PanMockEnabled {
			continue
		}
		if smart.PanMockProviderFromLabel(out[idx].Label) == "" {
			continue
		}
		wg.Add(1)
		go func(i int) {
			defer wg.Done()
			out[i] = expandPanMockPanEpisodes(database, out[i])
		}(idx)
	}
	wg.Wait()
	return out, nil
}

func expandPanMockPanEpisodes(database *db.DB, pan catpawrunner.Pan) catpawrunner.Pan {
	if database == nil || !pan.PanMockEnabled {
		return pan
	}
	resolvedPan, _, _, status, err := smart.ResolveSinglePanMockPan(database, pan)
	if err != nil {
		return pan
	}
	if status != "ok" || len(resolvedPan.Episodes) == 0 {
		return pan
	}
	return resolvedPan
}
