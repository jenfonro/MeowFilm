package catpawrunner

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"sync"
	"sync/atomic"
	"testing"
)

func TestWatchReportOptIn(t *testing.T) {
	for _, enabled := range []any{nil, false, "true", 1} {
		if got := WatchReportFromPlay(map[string]any{"watchReport": enabled}, "http://runner/prefix", "/0123456789/spider/site/3", "original", "line"); got != nil {
			t.Fatalf("unexpected opt-in: %v", enabled)
		}
	}
	got := WatchReportFromPlay(map[string]any{"watchReport": true}, "http://runner/prefix", "/0123456789/spider/site/3", "pic*author*duration****watch?v=video", "line")
	if got == nil || got.SessionID == "" || got.ID != "pic*author*duration****watch?v=video" || got.Flag != "line" {
		t.Fatalf("bad binding: %+v", got)
	}
}

func TestWatchReportOnceAndSourceIsolation(t *testing.T) {
	var calls atomic.Int32
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		calls.Add(1)
		if r.Method != "POST" || (r.URL.Path != "/prefix/0123456789/spider/site/3/report" && r.URL.Path != "/prefix/abcdef0123/spider/site/3/report") {
			t.Errorf("bad target %s %s", r.Method, r.URL.Path)
		}
		var body map[string]any
		_ = json.NewDecoder(r.Body).Decode(&body)
		if body["id"] != "original-episode" || body["flag"] != "line" {
			t.Errorf("bad payload: %v", body)
		}
		_, _ = w.Write([]byte(`{"ok":true}`))
	}))
	defer server.Close()
	report := WatchReportFromPlay(map[string]any{"watchReport": true}, server.URL+"/prefix", "/0123456789/spider/site/3", "original-episode", "line")
	var wg sync.WaitGroup
	for i := 0; i < 20; i++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			if err := ReportWatchOnce(1, report); err != nil {
				t.Error(err)
			}
		}()
	}
	wg.Wait()
	if calls.Load() != 1 {
		t.Fatalf("calls=%d", calls.Load())
	}
	other := *report
	other.SpiderAPI = "/abcdef0123/spider/site/3"
	if err := ReportWatchOnce(1, &other); err != nil {
		t.Fatal(err)
	}
	if calls.Load() != 2 {
		t.Fatalf("source not isolated: %d", calls.Load())
	}
}

func TestWatchReportRetriesFailure(t *testing.T) {
	var calls int
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		calls++
		if calls == 1 {
			_, _ = w.Write([]byte(`{"ok":false}`))
			return
		}
		_, _ = w.Write([]byte(`{"ok":true}`))
	}))
	defer server.Close()
	report := WatchReportFromPlay(map[string]any{"watchReport": true}, server.URL, "/spider/site/3", "ep", "line")
	if ReportWatchOnce(1, report) == nil {
		t.Fatal("failure acknowledged")
	}
	if err := ReportWatchOnce(1, report); err != nil {
		t.Fatal(err)
	}
	if err := ReportWatchOnce(1, report); err != nil {
		t.Fatal(err)
	}
	if calls != 2 {
		t.Fatalf("calls=%d", calls)
	}
}
