package search

import (
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"strings"
	"time"

	"github.com/jenfonro/meowfilm/internal/auth"
	"github.com/jenfonro/meowfilm/internal/db"
)

func HistoryHandler(database *db.DB) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		u := auth.CurrentUser(r)
		if u == nil {
			writeJSON(w, http.StatusUnauthorized, map[string]any{"error": "Unauthorized"})
			return
		}
		switch r.Method {
		case http.MethodGet:
			rows, err := database.SQL().Query(`SELECT keyword FROM search_history WHERE user_id=? ORDER BY updated_at DESC LIMIT 20`, u.ID)
			if err != nil {
				writeJSON(w, 200, []string{})
				return
			}
			defer func() { _ = rows.Close() }()
			list := []string{}
			for rows.Next() {
				var kw string
				_ = rows.Scan(&kw)
				kw = strings.TrimSpace(kw)
				if kw != "" {
					list = append(list, kw)
				}
			}
			writeJSON(w, 200, list)
		case http.MethodPost:
			parseForm(r)
			kw := strings.TrimSpace(r.FormValue("keyword"))
			if kw == "" && strings.Contains(r.Header.Get("Content-Type"), "application/json") {
				var body struct {
					Keyword string `json:"keyword"`
				}
				_ = readJSONLoose(r, &body)
				kw = strings.TrimSpace(body.Keyword)
			}
			kw = strings.Join(strings.Fields(kw), " ")
			if kw == "" {
				writeJSON(w, http.StatusBadRequest, map[string]any{"error": "Keyword is required"})
				return
			}
			_, _ = database.SQL().Exec(`
				INSERT INTO search_history(user_id, keyword, updated_at)
				VALUES(?,?,?)
				ON CONFLICT(user_id, keyword) DO UPDATE SET updated_at = excluded.updated_at
			`, u.ID, kw, time.Now().Unix())
			HistoryHandler(database).ServeHTTP(w, cloneWithMethod(r, http.MethodGet))
		case http.MethodDelete:
			kw := strings.TrimSpace(r.URL.Query().Get("keyword"))
			if kw != "" {
				_, _ = database.SQL().Exec(`DELETE FROM search_history WHERE user_id=? AND keyword=?`, u.ID, kw)
			} else {
				_, _ = database.SQL().Exec(`DELETE FROM search_history WHERE user_id=?`, u.ID)
			}
			HistoryHandler(database).ServeHTTP(w, cloneWithMethod(r, http.MethodGet))
		default:
			methodNotAllowed(w)
		}
	})
}

func cloneWithMethod(r *http.Request, method string) *http.Request {
	cp := r.Clone(r.Context())
	cp.Method = method
	return cp
}

func writeJSON(w http.ResponseWriter, status int, payload any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(status)
	enc := json.NewEncoder(w)
	enc.SetEscapeHTML(false)
	_ = enc.Encode(payload)
}

func readJSONLoose(r *http.Request, dst any) error {
	if r == nil || dst == nil {
		return errors.New("invalid args")
	}
	defer func() { _ = r.Body.Close() }()
	dec := json.NewDecoder(io.LimitReader(r.Body, 1<<20))
	return dec.Decode(dst)
}

func methodNotAllowed(w http.ResponseWriter) {
	writeJSON(w, http.StatusMethodNotAllowed, map[string]any{"success": false, "message": "Method not allowed"})
}

func parseForm(r *http.Request) {
	if r == nil {
		return
	}
	_ = r.ParseForm()
}
