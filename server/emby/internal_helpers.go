package emby

import (
	"net/http"

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
