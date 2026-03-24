package relay

import (
	"encoding/json"
	"errors"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
	mfnet "github.com/jenfonro/meowfilm/server/net"
)

type probeResp struct {
	Size int64 `json:"size"`
}

func NormalizeProvider(provider string) string {
	switch strings.ToLower(strings.TrimSpace(provider)) {
	case "uc":
		return "quark"
	case "quark", "baidu":
		return strings.ToLower(strings.TrimSpace(provider))
	default:
		return ""
	}
}

func EligibleServers(raw []db.RelayServer, provider string) []db.RelayServer {
	pan := NormalizeProvider(provider)
	out := make([]db.RelayServer, 0, len(raw))
	seen := map[string]struct{}{}
	for _, item := range raw {
		base := mfnet.NormalizeHTTPBase(item.Base)
		secret := strings.TrimSpace(item.Secret)
		if base == "" || secret == "" {
			continue
		}
		switch pan {
		case "baidu":
			if !item.PansBaidu {
				continue
			}
		case "quark":
			if !item.PansQuark {
				continue
			}
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

func BuildPlaybackURL(base string, resolveURL string, secret string, sizeMode bool) string {
	relayBase := mfnet.NormalizeHTTPBase(base)
	targetResolveURL := strings.TrimSpace(resolveURL)
	accessSecret := strings.TrimSpace(secret)
	if relayBase == "" || targetResolveURL == "" || accessSecret == "" {
		return ""
	}
	pathPrefix := ""
	if sizeMode {
		pathPrefix = "size/"
	}
	separator := "?"
	if strings.Contains(targetResolveURL, "?") {
		separator = "&"
	}
	return relayBase + "/" + pathPrefix + targetResolveURL + separator + "secret=" + url.QueryEscape(accessSecret)
}

func ProbeSize(base string, resolveURL string, secret string) (int64, error) {
	target := BuildPlaybackURL(base, resolveURL, secret, true)
	if target == "" {
		return 0, errors.New("relay size url invalid")
	}
	client := &http.Client{Timeout: 6 * time.Second}
	req, _ := http.NewRequest(http.MethodGet, target, nil)
	req.Header.Set("Accept", "application/json")
	resp, err := client.Do(req)
	if err != nil {
		return 0, err
	}
	defer func() { _ = resp.Body.Close() }()
	var out probeResp
	_ = json.NewDecoder(resp.Body).Decode(&out)
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return 0, errors.New("relay size http " + strconv.Itoa(resp.StatusCode))
	}
	if out.Size <= 0 {
		return 0, errors.New("invalid relay size")
	}
	return out.Size, nil
}
