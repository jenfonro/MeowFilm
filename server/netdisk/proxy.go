package netdisk

import (
	"net/http"
	"net/url"
	"strings"
	"sync/atomic"

	"github.com/jenfonro/meowfilm/internal/db"
)

type netdiskProxyState struct {
	enabled bool
	rawURL  string
	parsed  *url.URL
}

var (
	netdiskProxy atomic.Value // netdiskProxyState

	// Shared transport used by the 5 supported netdisks (189/quark/uc/139/baidu).
	// Proxy is resolved dynamically for hot toggle support.
	netdiskHTTPTransport = func() *http.Transport {
		t := http.DefaultTransport.(*http.Transport).Clone()
		t.Proxy = func(_ *http.Request) (*url.URL, error) {
			v := netdiskProxy.Load()
			st, _ := v.(netdiskProxyState)
			if !st.enabled || st.parsed == nil {
				return nil, nil
			}
			return st.parsed, nil
		}
		return t
	}()
)

func init() {
	netdiskProxy.Store(netdiskProxyState{})
}

func InitNetdiskProxyFromDB(database *db.DB) {
	if database == nil {
		SetNetdiskProxySettings(false, "")
		return
	}
	cfg, err := database.ReadAppConfig()
	if err != nil {
		return
	}
	SetNetdiskProxySettings(cfg.NetdiskProxyEnabled, cfg.NetdiskProxyURL)
}

func SetNetdiskProxySettings(enabled bool, proxyURL string) {
	raw := strings.TrimSpace(proxyURL)
	var parsed *url.URL
	if enabled && raw != "" {
		s := raw
		if !strings.Contains(s, "://") {
			s = "http://" + s
		}
		u, err := url.Parse(s)
		if err == nil && u != nil && u.Scheme != "" && u.Host != "" {
			parsed = u
			raw = u.String()
		} else {
			// Keep the app running even if persisted config is malformed.
			enabled = false
		}
	}

	netdiskProxy.Store(netdiskProxyState{enabled: enabled, rawURL: raw, parsed: parsed})

	// Close idle conns so new requests switch proxy immediately.
	if netdiskHTTPTransport != nil {
		netdiskHTTPTransport.CloseIdleConnections()
	}
}
