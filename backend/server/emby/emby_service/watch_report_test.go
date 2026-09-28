package emby_service

import (
	"net/http"
	"net/http/httptest"
	"testing"
	"time"

	"github.com/jenfonro/meowfilm/server/catpawrunner"
)

func TestSessionWatchReport(t *testing.T) {
	var calls int
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { calls++; _, _ = w.Write([]byte(`{"ok":true}`)) }))
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
	payload.PositionTicks = 10000000
	reportSessionWatch(92, payload)
	if calls != 0 {
		t.Fatal("wrong user reported")
	}
	if err := HandleSessionProgress(nil, 91, payload); err != nil {
		t.Fatal(err)
	}
	if err := HandleSessionStopped(nil, 91, payload); err != nil {
		t.Fatal(err)
	}
	if calls != 1 {
		t.Fatalf("calls=%d", calls)
	}
	target.MediaSourceID = "watch-report-ordinary"
	target.WatchReport = nil
	embyPlaybackSessions.Set(target, time.Minute)
	payload.MediaSourceID = target.MediaSourceID
	reportSessionWatch(91, payload)
	if calls != 1 {
		t.Fatal("ordinary source reported")
	}
}
