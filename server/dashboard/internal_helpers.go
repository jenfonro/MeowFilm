package dashboard

import (
	"errors"
	"net/http"
	"net/url"
	"strconv"
	"strings"

	"github.com/jenfonro/meowfilm/server/catpawrunner"
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

func parseAnyBool(v any, def bool) bool {
	return mfnet.ParseAnyBool(v, def)
}

func defaultString(v, def string) string {
	return mfnet.DefaultString(v, def)
}

func normalizeHTTPBase(value string) string {
	return mfnet.NormalizeHTTPBase(value)
}

func normalizeNetdiskProxyURL(input string) (string, error) {
	raw := strings.TrimSpace(input)
	if raw == "" {
		return "", nil
	}
	s := raw
	if !strings.Contains(s, "://") {
		s = "http://" + s
	}
	u, err := url.Parse(s)
	if err != nil || u == nil {
		return "", errors.New("代理地址不是合法 URL")
	}
	if u.Scheme != "http" && u.Scheme != "https" {
		return "", errors.New("代理仅支持 http/https")
	}
	if strings.TrimSpace(u.Host) == "" {
		return "", errors.New("代理地址缺少 host:port")
	}
	// Keep as an origin, since it is a proxy base.
	u.Path = ""
	u.RawQuery = ""
	u.Fragment = ""
	return u.String(), nil
}

func normalizecatpawrunnerAPIBase(inputURL string) string {
	return catpawrunner.NormalizeAPIBase(inputURL)
}

func parseJSONMap(text string) map[string]any {
	return mfnet.ParseJSONMap(text)
}

func parseJSONBoolMap(text string) map[string]bool {
	return mfnet.ParseJSONBoolMap(text)
}

func parseJSONStringArray(text string) []string {
	return mfnet.ParseJSONStringArray(text)
}

func parseJSONStringMap(text string) map[string]string {
	return mfnet.ParseJSONStringMap(text)
}

type goProxyServer struct {
	Name        string      `json:"name"`
	DisplayName string      `json:"displayName"`
	Base        string      `json:"base"`
	Pans        goProxyPans `json:"pans"`
}

type goProxyPans struct {
	Baidu bool `json:"baidu"`
	Quark bool `json:"quark"`
}

func normalizeGoProxyServers(value string) []goProxyServer {
	servers := mfnet.NormalizeGoProxyServers(value)
	out := make([]goProxyServer, 0, len(servers))
	for _, s := range servers {
		out = append(out, goProxyServer{
			Name:        s.Name,
			DisplayName: s.DisplayName,
			Base:        s.Base,
			Pans:        goProxyPans{Baidu: s.Pans.Baidu, Quark: s.Pans.Quark},
		})
	}
	return out
}

func normalizeContentKey(s string) string {
	return strings.TrimSpace(strings.ToLower(strings.Join(strings.Fields(s), "")))
}

func parseIntQuery(v string, def, min, max int) int {
	s := strings.TrimSpace(v)
	if s == "" {
		return def
	}
	n, err := strconv.Atoi(s)
	if err != nil {
		return def
	}
	if n < min {
		return min
	}
	if n > max {
		return max
	}
	return n
}
