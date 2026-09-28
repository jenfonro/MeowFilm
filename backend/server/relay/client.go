package relay

import (
	"net/url"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
	mfnet "github.com/jenfonro/meowfilm/server/net"
)

func EligibleServers(raw []db.RelayServer) []db.RelayServer {
	out := make([]db.RelayServer, 0, len(raw))
	seen := map[string]struct{}{}
	for _, item := range raw {
		base := mfnet.NormalizeHTTPBase(item.Base)
		secret := strings.TrimSpace(item.Secret)
		if base == "" || secret == "" {
			continue
		}
		key := strings.ToLower(base)
		if _, ok := seen[key]; ok {
			continue
		}
		seen[key] = struct{}{}
		item.Base = base
		item.Secret = secret
		out = append(out, item)
	}
	return out
}

func BuildPlaybackURL(base string, resolveURL string, secret string) string {
	relayBase := mfnet.NormalizeHTTPBase(base)
	targetResolveURL := strings.TrimSpace(resolveURL)
	accessSecret := strings.TrimSpace(secret)
	if relayBase == "" || targetResolveURL == "" || accessSecret == "" {
		return ""
	}
	separator := "?"
	if strings.Contains(targetResolveURL, "?") {
		separator = "&"
	}
	return relayBase + "/" + targetResolveURL + separator + "secret=" + url.QueryEscape(accessSecret)
}
