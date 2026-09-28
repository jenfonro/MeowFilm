package catpawrunner

import (
	"crypto/rand"
	"encoding/hex"
	"errors"
	"strings"
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

// WatchProgress comes from the existing player history events, in seconds.
type WatchProgress struct {
	PositionSeconds float64 `json:"positionSeconds"`
	DurationSeconds float64 `json:"durationSeconds"`
	Event           string  `json:"event"`
}

// ReportWatchProgress forwards every existing player progress event. A successful
// start must not suppress later progress or stop reports.
func ReportWatchProgress(userID int64, report *WatchReport, progress WatchProgress) error {
	if report == nil {
		return nil
	}
	binding := *report
	binding.APIBase = NormalizeAPIBase(binding.APIBase)
	binding.SpiderAPI = strings.TrimSpace(binding.SpiderAPI)
	if userID <= 0 || binding.SessionID == "" || binding.APIBase == "" || binding.SpiderAPI == "" || binding.ID == "" {
		return errors.New("incomplete watch report binding")
	}
	out, err := RequestSpider(binding.APIBase, binding.SpiderAPI, "report", map[string]any{
		"id": binding.ID, "flag": binding.Flag, "sessionId": binding.SessionID,
		"positionSeconds": progress.PositionSeconds, "durationSeconds": progress.DurationSeconds, "event": progress.Event,
	})
	if err == nil {
		if ok, _ := out["ok"].(bool); !ok {
			err = errors.New("site did not acknowledge watch report")
		}
	}
	return err
}
