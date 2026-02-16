package emby

import (
	"crypto/sha1"
	"encoding/hex"
	"net/http"
	"strings"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
)

// EmbyHandler implements a minimal subset of the Emby API surface (compatible with Emby clients).
//
// Notes:
// - This is not a full Emby server implementation.
// - Only routes needed by supported clients are exposed; add more incrementally as clients request them.
func EmbyHandler(database *db.DB) http.Handler {
	serverID := embyStableServerID(defaultString(database.GetSetting("site_name"), "MeowFilm"))
	type routeHandler func(w http.ResponseWriter, r *http.Request, tail []string)
	routes := map[string]routeHandler{
		"branding": func(w http.ResponseWriter, r *http.Request, tail []string) {
			handleEmbyBranding(w, r, database, serverID, tail)
		},
		"media": func(w http.ResponseWriter, r *http.Request, tail []string) {
			handleEmbyMediaFiles(w, r, database, serverID, tail)
		},
		"persons": func(w http.ResponseWriter, r *http.Request, tail []string) {
			handleEmbyPersons(w, r, database, serverID, tail)
		},
		"search": func(w http.ResponseWriter, r *http.Request, tail []string) {
			handleEmbySearch(w, r, database, serverID, tail)
		},
		"system": func(w http.ResponseWriter, r *http.Request, tail []string) {
			handleEmbySystem(w, r, database, serverID, tail)
		},
		"userviews": func(w http.ResponseWriter, r *http.Request, tail []string) {
			handleEmbyUserViews(w, r, database, serverID, tail)
		},
		"mediasegments": func(w http.ResponseWriter, r *http.Request, tail []string) {
			handleEmbyMediaSegments(w, r, database, serverID, tail)
		},
		"displaypreferences": func(w http.ResponseWriter, r *http.Request, tail []string) {
			handleEmbyDisplayPreferences(w, r, database, serverID, tail)
		},
		"library": func(w http.ResponseWriter, r *http.Request, tail []string) {
			handleEmbyLibrary(w, r, database, serverID, tail)
		},
		"users": func(w http.ResponseWriter, r *http.Request, tail []string) {
			handleEmbyUsers(w, r, database, serverID, tail)
		},
		"items": func(w http.ResponseWriter, r *http.Request, tail []string) {
			handleEmbyItems(w, r, database, serverID, tail)
		},
		"shows": func(w http.ResponseWriter, r *http.Request, tail []string) {
			handleEmbyShows(w, r, database, serverID, tail)
		},
		"sessions": func(w http.ResponseWriter, r *http.Request, tail []string) {
			handleEmbySessions(w, r, database, serverID, tail)
		},
		"videos": func(w http.ResponseWriter, r *http.Request, tail []string) {
			handleEmbyVideos(w, r, database, serverID, tail)
		},
	}

	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		startAt := time.Now()
		debugLog := embyDebugLogEnabled()
		lw := &embyLogWriter{ResponseWriter: w, status: 0}

		skipLogDone := false
		defer func() {
			if debugLog && !skipLogDone {
				embyLogDone(r, lw, time.Since(startAt))
			}
		}()

		path := embyTrimAPIPrefix(r.URL.Path)

		// Basic CORS for LAN usage / clients.
		embyWriteCORSHeaders(lw.Header())
		if r.Method == http.MethodOptions {
			lw.WriteHeader(http.StatusNoContent)
			if debugLog {
				skipLogDone = true
				embyDebugPrintf("[emby] %s %s -> %d (preflight)", r.Method, embyDebugURL(r, true), lw.Status())
			}
			return
		}

		parts := embySplitPathParts(path)

		if len(parts) == 0 {
			embyNotFound(lw)
			return
		}

		head, tail := embyHeadTail(parts)
		if head == "" {
			embyNotFound(lw)
			return
		}
		h, ok := routes[head]
		if !ok {
			embyNotFound(lw)
			return
		}
		h(lw, r, tail)
	})
}

func embyStableServerID(seed string) string {
	if strings.TrimSpace(seed) == "" {
		seed = "MeowFilm"
	}
	h := sha1.Sum([]byte(seed))
	return hex.EncodeToString(h[:])
}
