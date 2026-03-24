package dashboard

import (
	"encoding/json"
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

type relayServer struct {
	Name        string      `json:"name"`
	DisplayName string      `json:"displayName"`
	Base        string      `json:"base"`
	Secret      string      `json:"secret"`
	Pans        goProxyPans `json:"pans"`
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

func normalizeRelayServers(value string) []relayServer {
	var list []any
	if err := json.Unmarshal([]byte(value), &list); err != nil {
		return []relayServer{}
	}
	out := []relayServer{}
	seen := map[string]struct{}{}
	for _, it := range list {
		row, _ := it.(map[string]any)
		if row == nil {
			continue
		}
		name, _ := row["name"].(string)
		displayName, _ := row["displayName"].(string)
		base, _ := row["base"].(string)
		secret, _ := row["secret"].(string)
		pans, _ := row["pans"].(map[string]any)
		base = normalizeHTTPBase(base)
		name = strings.TrimSpace(name)
		displayName = strings.TrimSpace(displayName)
		secret = strings.TrimSpace(secret)
		if base == "" {
			continue
		}
		if name == "" {
			u, err := url.Parse(base)
			if err == nil && u != nil {
				name = strings.TrimSpace(u.Hostname())
				if name == "" {
					name = strings.TrimSpace(u.Host)
				}
			}
		}
		if name == "" {
			continue
		}
		key := strings.ToLower(base)
		if _, ok := seen[key]; ok {
			continue
		}
		seen[key] = struct{}{}
		if displayName == "" {
			displayName = name
		}
		baidu := true
		quark := true
		if pans != nil {
			if v, ok := pans["baidu"]; ok {
				baidu = parseAnyBool(v, true)
			}
			if v, ok := pans["quark"]; ok {
				quark = parseAnyBool(v, true)
			}
		}
		out = append(out, relayServer{
			Name:        name,
			DisplayName: displayName,
			Base:        base,
			Secret:      secret,
			Pans:        goProxyPans{Baidu: baidu, Quark: quark},
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
