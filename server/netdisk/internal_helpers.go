package netdisk

import (
	"fmt"
	"net/http"
	"encoding/json"
	"strconv"
	"strings"

	mfnet "github.com/jenfonro/meowfilm/server/net"
)

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

func boolFromForm(v string) bool {
	return mfnet.BoolFromForm(v)
}

func parseJSONMap(text string) map[string]any {
	return mfnet.ParseJSONMap(text)
}

func toString(v any) string {
	switch x := v.(type) {
	case nil:
		return ""
	case string:
		return x
	case float64:
		if x == float64(int64(x)) {
			return strconv.FormatInt(int64(x), 10)
		}
		return strings.TrimSpace(strconv.FormatFloat(x, 'f', -1, 64))
	case float32:
		f := float64(x)
		if f == float64(int64(f)) {
			return strconv.FormatInt(int64(f), 10)
		}
		return strings.TrimSpace(strconv.FormatFloat(f, 'f', -1, 64))
	case int:
		return strconv.FormatInt(int64(x), 10)
	case int64:
		return strconv.FormatInt(x, 10)
	case uint64:
		return strconv.FormatUint(x, 10)
	case json.Number:
		return x.String()
	default:
		return strings.TrimSpace(fmt.Sprint(x))
	}
}
