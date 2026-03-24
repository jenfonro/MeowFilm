package emby

import (
	"net/http"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/catpawrunner"
	"github.com/jenfonro/meowfilm/server/goproxy"
	"github.com/jenfonro/meowfilm/server/netdisk"
	"github.com/jenfonro/meowfilm/server/relay"
)

// embyProxyIfNeeded converts header-required play URLs into relay/goproxy proxy URLs when configured.
func embyProxyIfNeeded(database *db.DB, u *embyUser, r *http.Request, provider string, payload map[string]any) (finalURL string, finalHeaders map[string]string) {
	url0, header0 := netdisk.PlayPayloadURLHeaders(payload)
	if url0 == "" {
		return "", header0
	}
	if len(header0) == 0 {
		return url0, header0
	}
	if database == nil {
		return url0, header0
	}

	cfg, _ := database.ReadAppConfig()
	relayGoProxyThresholdBytes := relayGoProxyThresholdBytes(cfg.RelayGoProxyThresholdGB)
	relayServers, _ := database.ListRelayServers()
	relayEligibleServers := relay.EligibleServers(relayServers, provider)
	resolveURL := ""
	if cfg.RelayEnabled {
		resolveURL = strings.TrimSpace(netdisk.IssueRelayResolveURLFromPayload(r, payload))
	}
	relayEligible := cfg.RelayEnabled && resolveURL != "" && len(relayEligibleServers) > 0
	goProxyEligible := cfg.GoProxyEnabled

	if embyDebugLogEnabled() {
		embyDebugPrintf(
			"[emby][relay] provider=%s relayEnabled=%t relayEligible=%t goproxyEnabled=%t goproxyEligible=%t resolveURL=%t headers=%d",
			strings.TrimSpace(provider),
			cfg.RelayEnabled,
			relayEligible,
			cfg.GoProxyEnabled,
			goProxyEligible,
			resolveURL != "",
			len(header0),
		)
	}

	if relayEligible && !goProxyEligible {
		picked := relayEligibleServers[0]
		if out := relay.BuildPlaybackURL(picked.Base, resolveURL, picked.Secret, false); strings.TrimSpace(out) != "" {
			if embyDebugLogEnabled() {
				embyDebugPrintf("[emby][relay] selected=relay mode=relay_only provider=%s url=%s", strings.TrimSpace(provider), strings.TrimSpace(out))
			}
			return strings.TrimSpace(out), nil
		} else if embyDebugLogEnabled() {
			embyDebugPrintf("[emby][relay] selected=relay mode=relay_only provider=%s err=build playback url failed", strings.TrimSpace(provider))
		}
	}

	if !relayEligible && goProxyEligible {
		if pickedURL, ok, err := goproxy.ProxyIfNeeded(database, relay.NormalizeProvider(provider), url0, header0); err == nil && ok && strings.TrimSpace(pickedURL) != "" {
			if embyDebugLogEnabled() {
				embyDebugPrintf("[emby][relay] selected=goproxy mode=goproxy_only provider=%s url=%s", strings.TrimSpace(provider), strings.TrimSpace(pickedURL))
			}
			return strings.TrimSpace(pickedURL), nil
		} else if embyDebugLogEnabled() {
			if err != nil {
				embyDebugPrintf("[emby][relay] selected=goproxy mode=goproxy_only provider=%s err=%s", strings.TrimSpace(provider), err.Error())
			} else {
				embyDebugPrintf("[emby][relay] selected=goproxy mode=goproxy_only provider=%s err=no goproxy url", strings.TrimSpace(provider))
			}
		}
	}

	if relayEligible && goProxyEligible {
		if relayGoProxyThresholdBytes <= 0 {
			picked := relayEligibleServers[0]
			if out := relay.BuildPlaybackURL(picked.Base, resolveURL, picked.Secret, false); strings.TrimSpace(out) != "" {
				if embyDebugLogEnabled() {
					embyDebugPrintf("[emby][relay] threshold=%d provider=%s selected=relay skip_size_probe=true", cfg.RelayGoProxyThresholdGB, strings.TrimSpace(provider))
				}
				return strings.TrimSpace(out), nil
			}
		}
		useRelay := false
		picked := relayEligibleServers[0]
		sizeURL := relay.BuildPlaybackURL(picked.Base, resolveURL, picked.Secret, true)
		if embyDebugLogEnabled() {
			embyDebugPrintf("[emby][relay] size_probe provider=%s url=%s", strings.TrimSpace(provider), strings.TrimSpace(sizeURL))
		}
		if size, err := relay.ProbeSize(picked.Base, resolveURL, picked.Secret); err == nil && size > 0 && size < relayGoProxyThresholdBytes {
			useRelay = true
			if embyDebugLogEnabled() {
				embyDebugPrintf("[emby][relay] size=%d threshold=%d provider=%s selected=relay", size, relayGoProxyThresholdBytes, strings.TrimSpace(provider))
			}
		} else if embyDebugLogEnabled() {
			if err != nil {
				embyDebugPrintf("[emby][relay] size_probe_fail provider=%s err=%s", strings.TrimSpace(provider), err.Error())
			} else {
				embyDebugPrintf("[emby][relay] size=%d threshold=%d provider=%s selected=goproxy", size, relayGoProxyThresholdBytes, strings.TrimSpace(provider))
			}
		}
		if useRelay {
			if out := relay.BuildPlaybackURL(picked.Base, resolveURL, picked.Secret, false); strings.TrimSpace(out) != "" {
				return strings.TrimSpace(out), nil
			} else if embyDebugLogEnabled() {
				embyDebugPrintf("[emby][relay] selected=relay provider=%s err=build playback url failed", strings.TrimSpace(provider))
			}
		}
		if pickedURL, ok, err := goproxy.ProxyIfNeeded(database, relay.NormalizeProvider(provider), url0, header0); err == nil && ok && strings.TrimSpace(pickedURL) != "" {
			if embyDebugLogEnabled() {
				embyDebugPrintf("[emby][relay] selected=goproxy provider=%s url=%s", strings.TrimSpace(provider), strings.TrimSpace(pickedURL))
			}
			return strings.TrimSpace(pickedURL), nil
		} else if embyDebugLogEnabled() {
			if err != nil {
				embyDebugPrintf("[emby][relay] selected=goproxy provider=%s err=%s", strings.TrimSpace(provider), err.Error())
			} else {
				embyDebugPrintf("[emby][relay] selected=goproxy provider=%s err=no goproxy url", strings.TrimSpace(provider))
			}
		}
	}

	apiBase := strings.TrimSpace(embyResolveCatApiBaseForUser(database, u))
	if apiBase == "" {
		return url0, header0
	}
	tvUser := ""
	if u != nil {
		tvUser = strings.TrimSpace(u.Username)
	}
	if pickedURL, err := catpawrunner.RegisterProxy(apiBase, tvUser, url0, header0); err == nil && strings.TrimSpace(pickedURL) != "" {
		if embyDebugLogEnabled() {
			embyDebugPrintf("[emby][relay] selected=catpaw provider=%s url=%s", strings.TrimSpace(provider), strings.TrimSpace(pickedURL))
		}
		return strings.TrimSpace(pickedURL), nil
	} else if embyDebugLogEnabled() {
		if err != nil {
			embyDebugPrintf("[emby][relay] selected=catpaw provider=%s err=%s", strings.TrimSpace(provider), err.Error())
		} else {
			embyDebugPrintf("[emby][relay] selected=catpaw provider=%s err=no catpaw url", strings.TrimSpace(provider))
		}
	}

	return url0, header0
}

func relayGoProxyThresholdBytes(thresholdGB int) int64 {
	if thresholdGB <= 0 {
		return 0
	}
	return int64(thresholdGB) * 1024 * 1024 * 1024
}
