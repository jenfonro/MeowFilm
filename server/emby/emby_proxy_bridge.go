package emby

import (
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/catpawrunner"
	"github.com/jenfonro/meowfilm/server/goproxy"
)

// embyProxyIfNeeded converts header-required play URLs into header-free proxy URLs.
// Priority: GoProxy -> catpawrunner /api/proxy/register fallback.
func embyProxyIfNeeded(database *db.DB, u *embyUser, provider string, playURL string, headers map[string]string) (finalURL string, finalHeaders map[string]string) {
	url0 := strings.TrimSpace(playURL)
	if url0 == "" || len(headers) == 0 {
		return url0, headers
	}

	if pickedURL, ok, err := goproxy.ProxyIfNeeded(database, strings.TrimSpace(provider), url0, headers); err == nil && ok && strings.TrimSpace(pickedURL) != "" {
		return strings.TrimSpace(pickedURL), nil
	}

	apiBase := strings.TrimSpace(embyResolveCatApiBaseForUser(database, u))
	if apiBase == "" {
		return url0, headers
	}
	tvUser := ""
	if u != nil {
		tvUser = strings.TrimSpace(u.Username)
	}
	if pickedURL, err := catpawrunner.RegisterProxy(apiBase, tvUser, url0, headers); err == nil && strings.TrimSpace(pickedURL) != "" {
		return strings.TrimSpace(pickedURL), nil
	}

	return url0, headers
}
