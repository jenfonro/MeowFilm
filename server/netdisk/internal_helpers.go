package netdisk

import (
	"fmt"
	"encoding/json"
	"net/http"
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

func sanitizePanDisplaySegment(value string) string {
	s := strings.TrimSpace(value)
	if s == "" {
		return ""
	}
	s = strings.ReplaceAll(s, "\\", "/")
	s = strings.ReplaceAll(s, "#", " ")
	s = strings.ReplaceAll(s, "$", " ")
	s = strings.ReplaceAll(s, "\r", " ")
	s = strings.ReplaceAll(s, "\n", " ")
	s = strings.ReplaceAll(s, "\x00", "")
	s = strings.TrimSpace(s)
	if s == "" {
		return ""
	}
	s = strings.Join(strings.Fields(s), " ")
	if len(s) > 120 {
		s = s[:120]
	}
	return s
}

func sanitizePanRootPrefix(prefix string) string {
	raw := strings.TrimSpace(prefix)
	if raw == "" {
		return ""
	}
	raw = strings.ReplaceAll(raw, "\\", "/")
	parts := strings.Split(raw, "/")
	segs := make([]string, 0, len(parts))
	for _, p := range parts {
		if seg := sanitizePanDisplaySegment(p); seg != "" && seg != "." && seg != ".." {
			segs = append(segs, strings.Trim(seg, "/"))
		}
	}
	return strings.Join(segs, "/")
}

func prefixRootDirDisplay(dirDisplay string, rootPrefix string) string {
	root := sanitizePanRootPrefix(rootPrefix)
	if root == "" {
		return dirDisplay
	}
	d := strings.TrimSpace(dirDisplay)
	if d == "" {
		d = "/"
	}
	if d == "/" {
		return "/" + root
	}
	if strings.HasPrefix(d, "/") {
		return "/" + root + d
	}
	return "/" + root + "/" + strings.TrimLeft(d, "/")
}

func isSupportedVideoFilename(name string) bool {
	s := strings.ToLower(strings.TrimSpace(name))
	if s == "" {
		return false
	}
	switch {
	case strings.HasSuffix(s, ".mp4"):
		return true
	case strings.HasSuffix(s, ".mkv"):
		return true
	case strings.HasSuffix(s, ".mov"):
		return true
	case strings.HasSuffix(s, ".iso"):
		return true
	case strings.HasSuffix(s, ".flv"):
		return true
	default:
		return false
	}
}
