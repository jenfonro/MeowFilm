package api

import (
	"bytes"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"path/filepath"
	"testing"
	"time"

	"github.com/jenfonro/meowfilm/internal/auth"
	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/catpawrunner"
)

func TestPlayHistoryWatchReport(t *testing.T) {
	t.Setenv("MEOWFILM_DB_FILE", filepath.Join(t.TempDir(), "test.db"))
	database, err := db.Open()
	if err != nil {
		t.Fatal(err)
	}
	defer database.Close()
	userID, err := database.CreateUser("watch-report-test", "unused", "user")
	if err != nil {
		t.Fatal(err)
	}
	if err := database.InsertToken("test-token", userID, time.Now().Add(time.Hour)); err != nil {
		t.Fatal(err)
	}
	calls := 0
	var reports []map[string]any
	runner := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		calls++
		if r.URL.Path != "/prefix/0123456789/spider/site/3/report" {
			t.Errorf("wrong callback: %s", r.URL.Path)
		}
		var body map[string]any
		_ = json.NewDecoder(r.Body).Decode(&body)
		reports = append(reports, body)
		if body["id"] != "original-episode" {
			t.Errorf("wrong episode: %v", body)
		}
		if calls == 1 {
			w.WriteHeader(502)
			_, _ = w.Write([]byte(`{"ok":false}`))
			return
		}
		_, _ = w.Write([]byte(`{"ok":true}`))
	}))
	defer runner.Close()
	report := catpawrunner.WatchReportFromPlay(map[string]any{"watchReport": true}, runner.URL+"/prefix", "/0123456789/spider/site/3", "original-episode", "line")
	handler := auth.New(database, auth.Options{}).Middleware(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { handleAPIPlayHistory(w, r, database) }))
	body := map[string]any{"contentKey": "test-content", "siteKey": "source", "siteDetail": "detail-not-episode", "spiderApi": report.SpiderAPI, "playFlag": "line", "watchReport": report}
	send := func() map[string]any {
		t.Helper()
		raw, _ := json.Marshal(body)
		req := httptest.NewRequest("POST", "/api/playhistory", bytes.NewReader(raw))
		req.AddCookie(&http.Cookie{Name: auth.CookieName, Value: "test-token"})
		w := httptest.NewRecorder()
		handler.ServeHTTP(w, req)
		if w.Code != 200 {
			t.Fatalf("response=%d %s", w.Code, w.Body.String())
		}
		var result map[string]any
		_ = json.Unmarshal(w.Body.Bytes(), &result)
		if result["success"] != true {
			t.Fatal(result)
		}
		return result
	}
	send()
	if calls != 0 {
		t.Fatal("reported before playback")
	}
	body["playbackEvent"] = "started"
	result := send()
	if result["watchReport"].(map[string]any)["ok"] != false || calls != 1 {
		t.Fatal("callback failure not returned separately")
	}
	delete(body, "playbackEvent")
	body["playbackPositionTicks"] = 123450000
	body["playbackRuntimeTicks"] = 6000000000
	result = send()
	if result["watchReport"].(map[string]any)["ok"] != true || calls != 2 {
		t.Fatal("retry not acknowledged")
	}
	body["playbackPositionTicks"] = 240000000
	send()
	if calls != 3 {
		t.Fatal("successful start suppressed later progress")
	}
	body["playbackPositionTicks"] = 250000000
	body["playbackEvent"] = "stopped"
	send()
	if reports[1]["positionSeconds"] != 12.345 || reports[2]["positionSeconds"] != float64(24) || reports[3]["positionSeconds"] != float64(25) || reports[3]["event"] != "stopped" || reports[3]["durationSeconds"] != float64(600) {
		t.Fatalf("wrong progress payloads: %v", reports)
	}
	for _, r := range reports {
		if r["sessionId"] != report.SessionID {
			t.Fatal("session identity changed")
		}
	}

	delete(body, "watchReport")
	send()
	if calls != 4 {
		t.Fatal("ordinary site callback")
	}
	rows, err := database.ListPlayHistory(userID, 20)
	if err != nil {
		t.Fatal(err)
	}
	if len(rows) != 1 {
		t.Fatalf("local history rows=%d", len(rows))
	}
}
