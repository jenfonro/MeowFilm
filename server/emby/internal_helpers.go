package emby

import (
	"net/http"
	"strings"

	mfnet "github.com/jenfonro/meowfilm/server/net"
)

func writeJSON(w http.ResponseWriter, status int, payload any) {
	mfnet.WriteJSON(w, status, payload)
}

func readJSON(r *http.Request, dst any) error {
	return mfnet.ReadJSONStrict(r, dst)
}

func readJSONLoose(r *http.Request, dst any) error {
	return mfnet.ReadJSONLoose(r, dst)
}

func parseJSONStringArray(text string) []string {
	return mfnet.ParseJSONStringArray(text)
}

func defaultString(v, def string) string {
	return mfnet.DefaultString(v, def)
}

func minInt(a, b int) int {
	return mfnet.MinInt(a, b)
}

func maxInt(a, b int) int {
	return mfnet.MaxInt(a, b)
}

func smartLogSiteName(siteKey string, siteName string) string {
	name := strings.TrimSpace(siteName)
	if strings.EqualFold(name, "nodejs_wuming") {
		name = ""
	}
	if name == "" {
		key := strings.TrimSpace(siteKey)
		if strings.EqualFold(key, "nodejs_wuming") {
			key = ""
		}
		name = key
	}
	if name == "" {
		name = "unknown"
	}
	return name
}
