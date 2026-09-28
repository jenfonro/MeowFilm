package catpawrunner

import (
	"crypto/rand"
	"encoding/hex"
	"errors"
	"strings"
	"sync"
	"time"
)

// WatchReport binds a callback to the original play request, not its resolved URL.
type WatchReport struct {
	SessionID string `json:"sessionId"`
	APIBase   string `json:"apiBase"`
	SpiderAPI string `json:"spiderApi"`
	ID        string `json:"id"`
	Flag      string `json:"flag"`
}

func WatchReportFromPlay(raw map[string]any, apiBase, spiderAPI, id, flag string) *WatchReport {
	if enabled, _ := raw["watchReport"].(bool); !enabled {
		return nil
	}
	var session [16]byte
	_, _ = rand.Read(session[:])
	return &WatchReport{
		SessionID: hex.EncodeToString(session[:]),
		APIBase:   NormalizeAPIBase(apiBase),
		SpiderAPI: spiderAPI,
		ID:        strings.TrimSpace(id),
		Flag:      strings.TrimSpace(flag),
	}
}

type watchReportKey struct {
	UserID int64
	WatchReport
}

type watchReportResult struct {
	done    chan struct{}
	err     error
	expires time.Time
}

var watchReports = struct {
	sync.Mutex
	entries map[watchReportKey]*watchReportResult
}{entries: make(map[watchReportKey]*watchReportResult)}

// ReportWatchOnce coalesces concurrent notifications and only remembers success.
// A failed callback can be retried by the next existing playback progress event.
func ReportWatchOnce(userID int64, report *WatchReport) error {
	if report == nil {
		return nil
	}
	binding := *report
	binding.APIBase = NormalizeAPIBase(binding.APIBase)
	binding.SpiderAPI = strings.TrimSpace(binding.SpiderAPI)
	if userID <= 0 || binding.SessionID == "" || binding.APIBase == "" || binding.SpiderAPI == "" || binding.ID == "" {
		return errors.New("incomplete watch report binding")
	}
	key := watchReportKey{UserID: userID, WatchReport: binding}
	now := time.Now()
	watchReports.Lock()
	for k, entry := range watchReports.entries {
		if !entry.expires.IsZero() && now.After(entry.expires) {
			delete(watchReports.entries, k)
		}
	}
	if entry := watchReports.entries[key]; entry != nil {
		watchReports.Unlock()
		<-entry.done
		return entry.err
	}
	entry := &watchReportResult{done: make(chan struct{})}
	watchReports.entries[key] = entry
	watchReports.Unlock()

	out, err := RequestSpider(binding.APIBase, binding.SpiderAPI, "report", map[string]any{
		"id": binding.ID, "flag": binding.Flag,
	})
	if err == nil {
		if ok, _ := out["ok"].(bool); !ok {
			err = errors.New("site did not acknowledge watch report")
		}
	}
	watchReports.Lock()
	entry.err = err
	if err != nil {
		delete(watchReports.entries, key)
	} else {
		entry.expires = time.Now().Add(24 * time.Hour)
	}
	close(entry.done)
	watchReports.Unlock()
	return err
}
