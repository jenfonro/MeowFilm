package netdisk

import (
	"net/http"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
)

func HandleDashboardPanSettings(w http.ResponseWriter, r *http.Request, database *db.DB) {
	parseForm(r)
	switch r.Method {
	case http.MethodGet:
		key := strings.TrimSpace(r.URL.Query().Get("key"))
		store := readPanLoginSettings(database)
		if key != "" {
			v, ok := store[key]
			if !ok {
				v = map[string]any{}
			}
			writeJSON(w, 200, map[string]any{"success": true, "settings": map[string]any{key: v}})
			return
		}
		writeJSON(w, 200, map[string]any{"success": true, "settings": store})
	case http.MethodPost:
		key := strings.TrimSpace(r.FormValue("key"))
		typ := strings.TrimSpace(r.FormValue("type"))
		if key == "" {
			writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "key 不能为空"})
			return
		}
		if typ != "cookie" && typ != "account" && typ != "authorization" && typ != "quark_tv" && typ != "uc_tv" {
			writeJSON(w, http.StatusBadRequest, map[string]any{"success": false, "message": "type 参数无效"})
			return
		}
		store := readPanLoginSettings(database)
		cur := store[key]
		if cur == nil {
			cur = map[string]any{}
		}
		var payload any
		if typ == "cookie" {
			cookie := r.FormValue("cookie")
			cur["cookie"] = cookie
			payload = map[string]any{"cookie": cookie}
		} else if typ == "authorization" {
			authorization := r.FormValue("authorization")
			cur["authorization"] = authorization
			payload = map[string]any{"authorization": authorization}
		} else if typ == "quark_tv" || typ == "uc_tv" {
			refreshToken := r.FormValue("refresh_token")
			deviceID := r.FormValue("device_id")
			accessToken := r.FormValue("access_token")
			accessTokenExpAt := r.FormValue("access_token_exp_at")
			cur["refresh_token"] = refreshToken
			cur["device_id"] = deviceID
			if strings.TrimSpace(accessToken) != "" || strings.TrimSpace(accessTokenExpAt) != "" {
				cur["access_token"] = accessToken
				cur["access_token_exp_at"] = accessTokenExpAt
			} else {
				// Clear derived fields so backend can re-fetch fresh access_token.
				delete(cur, "access_token")
				delete(cur, "access_token_exp_at")
			}
			payload = map[string]any{
				"refresh_token":        refreshToken,
				"device_id":           deviceID,
				"access_token":        accessToken,
				"access_token_exp_at": accessTokenExpAt,
			}
		} else {
			username := r.FormValue("username")
			password := r.FormValue("password")
			cur["username"] = username
			cur["password"] = password
			if key == "189" {
				cookie := r.FormValue("cookie")
				cur["cookie"] = cookie
				payload = map[string]any{"username": username, "password": password, "cookie": cookie}
			} else {
				// Clear persisted cookie when account changes.
				delete(cur, "cookie")
				payload = map[string]any{"username": username, "password": password}
			}
		}
		store[key] = cur
		_ = writePanLoginSettings(database, store)
		writeJSON(w, 200, map[string]any{"success": true, "settings": store, "sync": map[string]any{"ok": nil, "skipped": true}, "payload": payload})
	default:
		methodNotAllowed(w)
	}
}
