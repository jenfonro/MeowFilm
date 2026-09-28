package netdisk

import (
	"crypto/rand"
	"crypto/subtle"
	"encoding/base64"
	"net/http"
	"strings"
	"sync"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
)

const relayTokenTTL = 1 * time.Hour

type relayTokenEntry struct {
	Payload map[string]any
	ExpAt   time.Time
}

type relayTokenStore struct {
	mu sync.Mutex
	m  map[string]relayTokenEntry
}

var relayTokens = &relayTokenStore{m: map[string]relayTokenEntry{}}

func BuildPlayPayload(playURL string, headers map[string]string) map[string]any {
	u := strings.TrimSpace(playURL)
	if u == "" {
		return map[string]any{}
	}
	payload := map[string]any{
		"ok":    true,
		"parse": 0,
		"url":   u,
	}
	if len(headers) != 0 {
		payload["header"] = copyStringMap(headers)
	}
	return payload
}

func PlayPayloadURLHeaders(payload map[string]any) (string, map[string]string) {
	if len(payload) == 0 {
		return "", nil
	}
	playURL := strings.TrimSpace(toString(payload["url"]))
	var headers map[string]string
	if rawHeader, ok := payload["header"].(map[string]any); ok {
		headers = map[string]string{}
		for k, v := range rawHeader {
			kk := strings.TrimSpace(k)
			sv := strings.TrimSpace(toString(v))
			if kk == "" || sv == "" {
				continue
			}
			headers[kk] = sv
		}
	} else if rawHeader, ok := payload["header"].(map[string]string); ok {
		headers = copyStringMap(rawHeader)
		for k, v := range headers {
			kk := strings.TrimSpace(k)
			sv := strings.TrimSpace(v)
			if kk == "" || sv == "" {
				delete(headers, k)
				continue
			}
			if kk != k {
				delete(headers, k)
				headers[kk] = sv
			} else {
				headers[k] = sv
			}
		}
	}
	if len(headers) == 0 {
		headers = nil
	}
	return playURL, headers
}

func cloneRelayPayload(payload map[string]any) map[string]any {
	if len(payload) == 0 {
		return map[string]any{}
	}
	out := make(map[string]any, len(payload))
	for k, v := range payload {
		key := strings.TrimSpace(k)
		if key == "" {
			continue
		}
		if sub, ok := v.(map[string]string); ok {
			out[key] = copyStringMap(sub)
			continue
		}
		if sub, ok := v.(map[string]any); ok {
			next := make(map[string]any, len(sub))
			for sk, sv := range sub {
				next[strings.TrimSpace(sk)] = sv
			}
			out[key] = next
			continue
		}
		out[key] = v
	}
	return out
}

func issueRelayToken(payload map[string]any) string {
	if len(payload) == 0 {
		return ""
	}
	u := strings.TrimSpace(toString(payload["url"]))
	if u == "" {
		return ""
	}
	b := make([]byte, 24)
	if _, err := rand.Read(b); err != nil {
		return ""
	}
	token := base64.RawURLEncoding.EncodeToString(b)
	now := time.Now()
	relayTokens.mu.Lock()
	relayTokens.m[token] = relayTokenEntry{
		Payload: cloneRelayPayload(payload),
		ExpAt:   now.Add(relayTokenTTL),
	}
	if len(relayTokens.m) > 4000 {
		for key, value := range relayTokens.m {
			if now.After(value.ExpAt) {
				delete(relayTokens.m, key)
			}
		}
	}
	relayTokens.mu.Unlock()
	return token
}

func attachRelayToken(r *http.Request, payload map[string]any) map[string]any {
	if len(payload) == 0 {
		return payload
	}
	token := issueRelayToken(payload)
	if token == "" {
		return payload
	}
	if tokenURL := buildRelayResolveURL(r, token); tokenURL != "" {
		payload["token"] = tokenURL
		return payload
	}
	payload["token"] = token
	return payload
}

func IssueRelayResolveURLFromPayload(r *http.Request, payload map[string]any) string {
	if r == nil {
		return ""
	}
	token := issueRelayToken(payload)
	if token == "" {
		return ""
	}
	return buildRelayResolveURL(r, token)
}

func resolveRelayToken(token string) (relayTokenEntry, bool) {
	key := strings.TrimSpace(token)
	if key == "" {
		return relayTokenEntry{}, false
	}
	now := time.Now()
	relayTokens.mu.Lock()
	defer relayTokens.mu.Unlock()
	entry, ok := relayTokens.m[key]
	if !ok {
		return relayTokenEntry{}, false
	}
	if now.After(entry.ExpAt) {
		delete(relayTokens.m, key)
		return relayTokenEntry{}, false
	}
	entry.ExpAt = now.Add(relayTokenTTL)
	relayTokens.m[key] = entry
	entry.Payload = cloneRelayPayload(entry.Payload)
	return entry, true
}

func relayAuthAllowed(database *db.DB, auth string) bool {
	if database == nil {
		return false
	}
	cfg, err := database.ReadAppConfig()
	if err != nil || !cfg.RelayEnabled {
		return false
	}
	needle := strings.TrimSpace(auth)
	candidate := strings.TrimSpace(cfg.RelayAuthToken)
	if needle == "" || candidate == "" {
		return false
	}
	return subtle.ConstantTimeCompare([]byte(candidate), []byte(needle)) == 1
}

func HandleAPIRelayResolve(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost && r.Method != http.MethodGet {
		methodNotAllowed(w)
		return
	}
	token := ""
	authToken := ""
	if r.Method == http.MethodPost {
		var body struct {
			Token string `json:"token"`
			Auth  string `json:"auth"`
		}
		_ = readJSONLoose(r, &body)
		token = strings.TrimSpace(body.Token)
		authToken = strings.TrimSpace(body.Auth)
	}
	if token == "" {
		token = strings.TrimSpace(r.URL.Query().Get("token"))
	}
	if authToken == "" {
		authToken = strings.TrimSpace(r.URL.Query().Get("auth"))
	}
	if authToken == "" {
		authToken = strings.TrimSpace(r.Header.Get("X-Relay-Auth"))
	}
	if token == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": "missing token"})
		return
	}
	if !relayAuthAllowed(database, authToken) {
		writeJSON(w, http.StatusForbidden, map[string]any{"ok": false, "message": "invalid auth"})
		return
	}
	entry, ok := resolveRelayToken(token)
	if !ok || len(entry.Payload) == 0 {
		writeJSON(w, http.StatusNotFound, map[string]any{"ok": false, "message": "token expired"})
		return
	}
	writeJSON(w, http.StatusOK, entry.Payload)
}
