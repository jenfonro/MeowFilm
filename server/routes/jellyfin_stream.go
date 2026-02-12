package routes

import (
	"io"
	"net/http"
	"os"
	"strings"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
)

func handleJellyfinStream(w http.ResponseWriter, r *http.Request, database *db.DB, serverID string, parts []string) {
	_, ok := jellyfinRequireUser(w, r, database)
	if !ok {
		return
	}
	if r.Method != http.MethodGet && r.Method != http.MethodHead {
		w.WriteHeader(http.StatusMethodNotAllowed)
		return
	}
	if len(parts) < 1 || strings.TrimSpace(parts[0]) == "" {
		http.NotFound(w, r)
		return
	}
	streamID := strings.TrimSpace(parts[0])

	jellyfinStreams.Lock()
	sess, ok := jellyfinStreams.M[streamID]
	// prune expired opportunistically
	now := time.Now()
	for k, v := range jellyfinStreams.M {
		if v.Expire.Before(now) {
			delete(jellyfinStreams.M, k)
		}
	}
	jellyfinStreams.Unlock()

	if !ok || strings.TrimSpace(sess.URL) == "" {
		http.NotFound(w, r)
		return
	}

	upstream := strings.TrimSpace(sess.URL)
	forceProxyAll := strings.TrimSpace(os.Getenv("MEOWFILM_JELLYFIN_STREAM_PROXY_ALL")) == "1"
	if len(sess.Headers) == 0 && !forceProxyAll {
		http.Redirect(w, r, upstream, http.StatusFound)
		return
	}

	req, err := http.NewRequest(r.Method, upstream, nil)
	if err != nil {
		jellyfinWriteError(w, 502, "上游地址无效")
		return
	}
	// Pass through range for Infuse seeking.
	if rng := strings.TrimSpace(r.Header.Get("Range")); rng != "" {
		req.Header.Set("Range", rng)
	}
	// Apply required headers from CatPawOpen.
	for k, v := range sess.Headers {
		kk := strings.TrimSpace(k)
		vv := strings.TrimSpace(v)
		if kk == "" || vv == "" {
			continue
		}
		req.Header.Set(kk, vv)
	}
	// Keep a UA so some CDNs don't reject.
	if ua := strings.TrimSpace(r.Header.Get("User-Agent")); ua != "" {
		req.Header.Set("User-Agent", ua)
	}

	client := &http.Client{Timeout: 0}
	resp, err := client.Do(req)
	if err != nil {
		jellyfinWriteError(w, 502, "请求上游失败")
		return
	}
	defer resp.Body.Close()

	// Copy selected headers.
	for _, hk := range []string{
		"Content-Type",
		"Content-Length",
		"Content-Range",
		"Accept-Ranges",
		"Last-Modified",
		"ETag",
	} {
		if v := resp.Header.Get(hk); v != "" {
			w.Header().Set(hk, v)
		}
	}
	w.WriteHeader(resp.StatusCode)
	if r.Method != http.MethodHead {
		_, _ = io.Copy(w, resp.Body)
	}
}
