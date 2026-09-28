package catpawrunner

import (
	"net/url"
	"strings"

	mfnet "github.com/jenfonro/meowfilm/server/net"
)

// NormalizeAPIBase normalizes user input into a catpawrunner base URL that ends with "/".
// It also accepts pasted spider/config URLs and trims them back to the service base.
func NormalizeAPIBase(inputURL string) string {
	raw := strings.TrimSpace(inputURL)
	if raw == "" {
		return ""
	}
	u, err := url.Parse(raw)
	if err != nil {
		return ""
	}
	if u.Scheme != "http" && u.Scheme != "https" {
		return ""
	}

	path := u.Path
	if path == "" {
		path = "/"
	}

	// If user pasted a spider API (/spider/...), trim back to the service base.
	if idx := strings.Index(path, "/spider/"); idx >= 0 {
		path = path[:idx]
		if path == "" {
			path = "/"
		}
	}

	// If user pasted an id-prefixed spider API like "/<id>/spider/...", drop the id segment.
	if isRuntimeIDPath(path) {
		path = "/"
	}

	path = strings.TrimRight(path, "/")
	if strings.HasSuffix(path, "/spider") {
		path = strings.TrimSuffix(path, "/spider")
	}
	path = strings.TrimRight(path, "/")
	for _, suffix := range []string{"/full-config", "/config", "/website"} {
		if strings.HasSuffix(path, suffix) {
			path = strings.TrimSuffix(path, suffix)
			path = strings.TrimRight(path, "/")
		}
	}
	if path == "" {
		path = "/"
	}
	if !strings.HasSuffix(path, "/") {
		path += "/"
	}

	u.Path = path
	u.RawQuery = ""
	u.Fragment = ""
	return u.String()
}

func NormalizeHTTPBase(value string) string {
	return mfnet.NormalizeHTTPBase(value)
}

func isRuntimeIDPath(path string) bool {
	p := strings.TrimSpace(path)
	if p == "" {
		return false
	}
	p = strings.TrimRight(p, "/")
	if len(p) != 11 || p[0] != '/' {
		return false
	}
	id := strings.ToLower(p[1:])
	for _, ch := range id {
		if (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f') {
			continue
		}
		return false
	}
	return true
}
