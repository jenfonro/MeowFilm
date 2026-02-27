package goproxy

import (
	"bytes"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/cache"
	mfnet "github.com/jenfonro/meowfilm/server/net"
)

type headerLine struct {
	Key   string `json:"key"`
	Value string `json:"value"`
}

type registerResp struct {
	Token string `json:"token"`
}

var pickedBaseCache = cache.NewTTLInflightCache[string](30*time.Minute, 32)

func eligibleServers(raw []db.GoProxyServer, pan string) []string {
	p := strings.ToLower(strings.TrimSpace(pan))
	out := make([]string, 0, len(raw))
	seen := map[string]struct{}{}
	for _, s := range raw {
		base := mfnet.NormalizeHTTPBase(s.Base)
		if base == "" {
			continue
		}
		if p == "baidu" && !s.PansBaidu {
			continue
		}
		if p == "quark" && !s.PansQuark {
			continue
		}
		key := strings.ToLower(base)
		if _, ok := seen[key]; ok {
			continue
		}
		seen[key] = struct{}{}
		out = append(out, base)
	}
	return out
}

func joinBaseURL(base string, path string) (string, error) {
	b := mfnet.NormalizeHTTPBase(base)
	if b == "" {
		return "", errors.New("invalid base")
	}
	u, err := url.Parse(b + "/")
	if err != nil {
		return "", err
	}
	p := strings.TrimLeft(strings.TrimSpace(path), "/")
	next, err := u.Parse(p)
	if err != nil {
		return "", err
	}
	return next.String(), nil
}

func probeSpeedBps(base string, timeout time.Duration) (float64, bool) {
	u, err := joinBaseURL(base, "speed?bytes=2097152&_="+strconv.FormatInt(time.Now().UnixMilli(), 10))
	if err != nil {
		return 0, false
	}
	client := &http.Client{Timeout: timeout}
	req, _ := http.NewRequest(http.MethodGet, u, nil)
	start := time.Now()
	resp, err := client.Do(req)
	if err != nil {
		return 0, false
	}
	defer func() { _ = resp.Body.Close() }()
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		_, _ = io.Copy(io.Discard, io.LimitReader(resp.Body, 64<<10))
		return 0, false
	}
	n, _ := io.Copy(io.Discard, resp.Body)
	sec := time.Since(start).Seconds()
	if sec <= 0 {
		return 0, false
	}
	return float64(n) / sec, n > 0
}

func PickBase(database *db.DB, pan string) (string, error) {
	if database == nil {
		return "", errors.New("invalid database")
	}
	cfg, _ := database.ReadAppConfig()
	if !cfg.GoProxyEnabled {
		return "", errors.New("goproxy disabled")
	}
	raw, _ := database.ListGoProxyServers()
	eligible := eligibleServers(raw, pan)
	if len(eligible) == 0 {
		return "", errors.New("no goproxy servers")
	}

	key := "pick:" + strings.ToLower(strings.TrimSpace(pan))
	val, _, err := pickedBaseCache.Do(key, func() (string, error) {
		if !cfg.GoProxyAutoSelect || len(eligible) == 1 {
			return eligible[0], nil
		}
		type res struct {
			base string
			bps  float64
			ok   bool
		}
		ch := make(chan res, len(eligible))
		for _, b := range eligible {
			base := b
			go func() {
				bps, ok := probeSpeedBps(base, 8*time.Second)
				ch <- res{base: base, bps: bps, ok: ok}
			}()
		}
		best := res{base: eligible[0], bps: 0, ok: false}
		for i := 0; i < len(eligible); i++ {
			r := <-ch
			if !r.ok || r.base == "" {
				continue
			}
			if !best.ok || r.bps > best.bps {
				best = r
			}
		}
		if best.base == "" {
			return eligible[0], nil
		}
		return best.base, nil
	})
	return val, err
}

func Register(base string, playURL string, headers map[string]string) (proxyURL string, err error) {
	b := mfnet.NormalizeHTTPBase(base)
	if b == "" {
		return "", errors.New("invalid goproxy base")
	}
	u := strings.TrimSpace(playURL)
	if u == "" {
		return "", errors.New("missing url")
	}
	lines := make([]headerLine, 0, len(headers))
	for k, v := range headers {
		key := strings.TrimSpace(k)
		val := strings.TrimSpace(v)
		if key == "" || val == "" {
			continue
		}
		lines = append(lines, headerLine{Key: key, Value: val})
	}
	registerURL, err := joinBaseURL(b, "register")
	if err != nil {
		return "", err
	}
	payload := map[string]any{
		"url":         u,
		"headersList": lines,
	}
	body, _ := json.Marshal(payload)
	req, _ := http.NewRequest(http.MethodPost, registerURL, bytes.NewReader(body))
	req.Header.Set("Content-Type", "application/json")
	client := &http.Client{Timeout: 12 * time.Second}
	resp, err := client.Do(req)
	if err != nil {
		return "", err
	}
	defer func() { _ = resp.Body.Close() }()
	var out registerResp
	_ = json.NewDecoder(resp.Body).Decode(&out)
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return "", fmt.Errorf("goproxy register http %d", resp.StatusCode)
	}
	token := strings.TrimSpace(out.Token)
	if token == "" {
		return "", errors.New("missing goproxy token")
	}
	proxyURL, err = joinBaseURL(b, url.PathEscape(token))
	if err != nil {
		return "", err
	}
	return proxyURL, nil
}

func ProxyIfNeeded(database *db.DB, pan string, playURL string, headers map[string]string) (string, bool, error) {
	if len(headers) == 0 || strings.TrimSpace(playURL) == "" {
		return strings.TrimSpace(playURL), false, nil
	}
	if database == nil {
		return "", false, errors.New("invalid database")
	}
	base, err := PickBase(database, pan)
	if err != nil || strings.TrimSpace(base) == "" {
		return "", false, err
	}
	u, err := Register(base, playURL, headers)
	if err != nil || strings.TrimSpace(u) == "" {
		return "", false, err
	}
	return u, true, nil
}
