package emby_service

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"testing"
	"time"

	"github.com/jenfonro/meowfilm/server/catpawrunner"
)

func TestSessionWatchReport(t *testing.T) {
	var calls int
	var reports []catpawrunner.WatchProgress
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		calls++
		var progress catpawrunner.WatchProgress
		_ = json.NewDecoder(r.Body).Decode(&progress)
		reports = append(reports, progress)
		_, _ = w.Write([]byte(`{"ok":true}`))
	}))
	defer server.Close()
	report := catpawrunner.WatchReportFromPlay(map[string]any{"watchReport": true}, server.URL, "/0123456789/spider/site/3", "ep", "line")
	target := PlaybackStreamTarget{UserID: 91, ItemID: "watch-report-item", MediaSourceID: "watch-report-media", PlaySessionID: "watch-report-session", FinalURL: "http://media/video", WatchReport: report}
	embyPlaybackSessions.Set(target, time.Minute)
	// The cache owns its binding; mutation of a returned clone must not affect it.
	clone, ok := embyPlaybackSessions.GetByMediaSourceID(target.MediaSourceID)
	if !ok || clone.WatchReport == nil {
		t.Fatal("binding lost")
	}
	clone.WatchReport.ID = "wrong"
	clone, _ = embyPlaybackSessions.GetByMediaSourceID(target.MediaSourceID)
	if clone.WatchReport.ID != "ep" {
		t.Fatal("binding not cloned")
	}
	payload := SessionPlaybackPayload{ItemID: target.ItemID, MediaSourceID: target.MediaSourceID, PlaySessionID: target.PlaySessionID}
	if err := HandleSessionProgress(nil, 91, payload); err != nil {
		t.Fatal(err)
	}
	if calls != 0 {
		t.Fatal("zero position reported")
	}
	payload.PositionTicks = 123450000
	payload.RunTimeTicks = 600000000
	reportSessionWatch(92, payload, "progress")
	if calls != 0 {
		t.Fatal("wrong user reported")
	}
	if err := HandleSessionProgress(nil, 91, payload); err != nil {
		t.Fatal(err)
	}
	payload.PositionTicks = 240000000
	if err := HandleSessionProgress(nil, 91, payload); err != nil {
		t.Fatal(err)
	}
	payload.PositionTicks = 250000000
	if err := HandleSessionStopped(nil, 91, payload); err != nil {
		t.Fatal(err)
	}
	if calls != 3 {
		t.Fatalf("calls=%d", calls)
	}
	if reports[0].PositionSeconds != 12.345 || reports[1].PositionSeconds != 24 || reports[2].PositionSeconds != 25 || reports[2].DurationSeconds != 60 || reports[2].Event != "stopped" {
		t.Fatalf("wrong progress: %+v", reports)
	}
	target.MediaSourceID = "watch-report-ordinary"
	target.WatchReport = nil
	embyPlaybackSessions.Set(target, time.Minute)
	payload.MediaSourceID = target.MediaSourceID
	reportSessionWatch(91, payload, "progress")
	if calls != 3 {
		t.Fatal("ordinary source reported")
	}
}
