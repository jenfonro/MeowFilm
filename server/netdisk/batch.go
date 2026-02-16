package netdisk

import (
	"net/http"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
)

type batchListItem struct {
	Provider   string `json:"provider"`
	Flag       string `json:"flag"`
	Passcode   string `json:"passcode"`
	AccessCode string `json:"accessCode"`
	Pwd        string `json:"pwd"`
	Password   string `json:"password"`
}

func (it batchListItem) normalizedPasscode() string {
	if strings.TrimSpace(it.Passcode) != "" {
		return strings.TrimSpace(it.Passcode)
	}
	if strings.TrimSpace(it.AccessCode) != "" {
		return strings.TrimSpace(it.AccessCode)
	}
	if strings.TrimSpace(it.Pwd) != "" {
		return strings.TrimSpace(it.Pwd)
	}
	if strings.TrimSpace(it.Password) != "" {
		return strings.TrimSpace(it.Password)
	}
	return ""
}

func HandleAPIPanBatchList(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	var body struct {
		Items []batchListItem `json:"items"`
	}
	_ = readJSONLoose(r, &body)

	items := body.Items
	results := make([]map[string]any, 0, len(items))
	for _, raw := range items {
		provider := strings.TrimSpace(raw.Provider)
		flag := strings.TrimSpace(raw.Flag)
		pass := raw.normalizedPasscode()

		res := map[string]any{"provider": provider, "flag": flag, "ok": false}
		if provider == "" || flag == "" {
			res["message"] = "missing provider/flag"
			results = append(results, res)
			continue
		}

		switch provider {
		case "189", "tianyi":
			vod, shareID, shareCode, err := Tianyi189List(database, flag, pass)
			if err != nil {
				res["message"] = err.Error()
				break
			}
			res["ok"] = true
			res["vod_play_url"] = vod
			if shareID != "" {
				res["shareId"] = shareID
			}
			if shareCode != "" {
				res["shareCode"] = shareCode
			}
		case "quark":
			vod, shareID, err := QuarkList(database, flag, pass)
			if err != nil {
				res["message"] = err.Error()
				break
			}
			res["ok"] = true
			res["vod_play_url"] = vod
			if shareID != "" {
				res["shareId"] = shareID
			}
		case "uc":
			vod, shareID, err := UCList(database, flag, pass)
			if err != nil {
				res["message"] = err.Error()
				break
			}
			res["ok"] = true
			res["vod_play_url"] = vod
			if shareID != "" {
				res["shareId"] = shareID
			}
		case "139":
			vod, linkID, err := Yun139List(database, flag)
			if err != nil {
				res["message"] = err.Error()
				break
			}
			res["ok"] = true
			res["vod_play_url"] = vod
			if linkID != "" {
				res["linkId"] = linkID
			}
		case "baidu":
			vod, surl, err := BaiduList(database, flag, pass)
			if err != nil {
				res["message"] = err.Error()
				break
			}
			res["ok"] = true
			res["vod_play_url"] = vod
			if surl != "" {
				res["surl"] = surl
			}
		default:
			res["message"] = "unsupported provider"
		}

		results = append(results, res)
	}

	writeJSON(w, 200, map[string]any{"ok": true, "results": results})
}

