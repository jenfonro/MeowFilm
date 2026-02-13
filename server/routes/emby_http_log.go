package routes

import (
	"net/http"
	"net/url"
	"sort"
	"strings"
	"time"
)

const embyDebugSlowRequestThreshold = 250 * time.Millisecond

func embyLogDone(r *http.Request, w *embyLogWriter, elapsed time.Duration) {
	if r == nil || w == nil {
		return
	}
	if w.Status() < 400 && elapsed < embyDebugSlowRequestThreshold {
		return
	}
	if w.Status() >= 400 {
		embyDebugPrintf("[emby] %s %s -> %d (%d bytes) cost=%s %q", r.Method, embyDebugURL(r, true), w.Status(), w.Bytes(), elapsed.String(), w.Sample())
		return
	}
	embyDebugPrintf("[emby] %s %s -> %d (%d bytes) cost=%s", r.Method, embyDebugURL(r, true), w.Status(), w.Bytes(), elapsed.String())
}

func embyDebugURL(r *http.Request, includeQuery bool) string {
	if r == nil {
		return ""
	}
	if !includeQuery {
		return r.URL.Path
	}
	redacted := embyRedactedQuery(r.URL.Query())
	if redacted == "" {
		return r.URL.Path
	}
	return r.URL.Path + "?" + redacted
}

func embyRedactedQuery(q url.Values) string {
	if len(q) == 0 {
		return ""
	}
	keys := make([]string, 0, len(q))
	for k := range q {
		keys = append(keys, k)
	}
	sort.Strings(keys)

	pairs := make([]string, 0, len(keys))
	for _, k := range keys {
		vals := q[k]
		v := ""
		if len(vals) > 0 {
			v = vals[0]
		}
		kl := strings.ToLower(strings.TrimSpace(k))
		if kl == "api_key" || kl == "token" || kl == "access_token" {
			v = "***"
		}
		pairs = append(pairs, url.QueryEscape(k)+"="+url.QueryEscape(v))
	}
	return strings.Join(pairs, "&")
}

type embyLogWriter struct {
	http.ResponseWriter
	status int
	bytes  int
	sample []byte
}

func (w *embyLogWriter) Write(p []byte) (int, error) {
	if w == nil {
		return 0, nil
	}
	if len(p) > 0 {
		w.bytes += len(p)
		if len(w.sample) < 512 {
			remain := 512 - len(w.sample)
			if remain > 0 {
				if len(p) < remain {
					w.sample = append(w.sample, p...)
				} else {
					w.sample = append(w.sample, p[:remain]...)
				}
			}
		}
	}
	return w.ResponseWriter.Write(p)
}

func (w *embyLogWriter) WriteHeader(statusCode int) {
	if w == nil {
		return
	}
	if w.status == 0 {
		w.status = statusCode
	}
	w.ResponseWriter.WriteHeader(statusCode)
}

func (w *embyLogWriter) Status() int {
	if w == nil {
		return 0
	}
	if w.status == 0 {
		return http.StatusOK
	}
	return w.status
}

func (w *embyLogWriter) Bytes() int {
	if w == nil {
		return 0
	}
	return w.bytes
}

func (w *embyLogWriter) Sample() string {
	if w == nil || len(w.sample) == 0 {
		return ""
	}
	s := string(w.sample)
	s = strings.ReplaceAll(s, "\n", "\\n")
	s = strings.ReplaceAll(s, "\r", "\\r")
	return s
}
