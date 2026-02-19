package search

import (
	"net/http"
	"strings"

	"github.com/jenfonro/meowfilm/internal/auth"
	"github.com/jenfonro/meowfilm/internal/db"
	mfnet "github.com/jenfonro/meowfilm/server/net"
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
			list, err := database.ListSearchHistory(u.ID, 20)
			if err != nil {
				list = []string{}
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
			_ = database.UpsertSearchHistory(u.ID, kw)
			HistoryHandler(database).ServeHTTP(w, cloneWithMethod(r, http.MethodGet))
		case http.MethodDelete:
			kw := strings.TrimSpace(r.URL.Query().Get("keyword"))
			if kw != "" {
				_ = database.DeleteSearchHistoryKeyword(u.ID, kw)
			} else {
				_ = database.ClearSearchHistory(u.ID)
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
	mfnet.WriteJSON(w, status, payload)
}

func readJSONLoose(r *http.Request, dst any) error {
	return mfnet.ReadJSONLoose(r, dst)
}

func methodNotAllowed(w http.ResponseWriter) {
	mfnet.MethodNotAllowed(w)
}

func parseForm(r *http.Request) {
	mfnet.ParseForm(r)
}
