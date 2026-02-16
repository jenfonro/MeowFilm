package catpawopen

import (
	"bytes"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"net/url"
	"regexp"
	"strings"
	"time"
)

func ExtractSiteIDFromSpiderAPI(spiderAPI string) string {
	s := strings.TrimSpace(spiderAPI)
	if s == "" {
		return ""
	}
	re := regexp.MustCompile(`^/([a-f0-9]{10})/spider/`)
	m := re.FindStringSubmatch(s)
	if len(m) >= 2 {
		return strings.TrimSpace(m[1])
	}
	return ""
}

// PickFirstPlayableURL mirrors frontend pickFirstPlayableUrl behavior.
func PickFirstPlayableURL(playRaw map[string]any) string {
	if playRaw == nil {
		return ""
	}
	if v, ok := playRaw["url"].(string); ok && strings.TrimSpace(v) != "" {
		return strings.TrimSpace(v)
	}
	arrAny, ok := playRaw["url"].([]any)
	if !ok || len(arrAny) == 0 {
		return ""
	}
	if len(arrAny) >= 2 {
		s0 := strings.TrimSpace(anyToString(arrAny[0]))
		s1 := strings.TrimSpace(anyToString(arrAny[1]))
		if !strings.HasPrefix(strings.ToLower(s0), "http") && strings.HasPrefix(strings.ToLower(s1), "http") {
			return s1
		}
	}
	for _, it := range arrAny {
		s := strings.TrimSpace(anyToString(it))
		if strings.HasPrefix(strings.ToLower(s), "http") {
			return s
		}
	}
	return ""
}

func RewriteProxyURLToBase(raw string, apiBase string, tvUser string) string {
	in := strings.TrimSpace(raw)
	if in == "" {
		return ""
	}
	base := NormalizeAPIBase(apiBase)
	if base == "" {
		return in
	}
	u, err := url.Parse(in)
	if err != nil {
		return in
	}
	host := strings.ToLower(strings.TrimSpace(u.Hostname()))
	if host == "" {
		return in
	}
	loopback := map[string]bool{"127.0.0.1": true, "0.0.0.0": true, "localhost": true}
	if !loopback[host] && u.Port() != "3006" {
		return in
	}
	baseU, err := url.Parse(base)
	if err != nil {
		return in
	}
	next, err := baseU.Parse(strings.TrimPrefix(u.Path, "/"))
	if err != nil {
		return in
	}
	next.RawQuery = u.RawQuery
	next.Fragment = u.Fragment
	if tv := strings.TrimSpace(tvUser); tv != "" {
		q := next.Query()
		if q.Get("__tvuser") == "" {
			q.Set("__tvuser", tv)
			next.RawQuery = q.Encode()
		}
	}
	return next.String()
}

func RegisterM3U8(apiBase string, tvUser string, playURL string, headers map[string]string) (indexURL string, proxyURL string, err error) {
	base := NormalizeAPIBase(apiBase)
	if base == "" {
		return "", "", errors.New("CatPawOpen 接口地址未设置")
	}
	target, _ := url.Parse(base)
	target, _ = target.Parse("api/m3u8/register")
	payload := map[string]any{"url": strings.TrimSpace(playURL), "headers": headers}
	b, _ := json.Marshal(payload)
	req, _ := http.NewRequest(http.MethodPost, target.String(), bytes.NewReader(b))
	req.Header.Set("Content-Type", "application/json")
	if strings.TrimSpace(tvUser) != "" {
		req.Header.Set("X-TV-User", strings.TrimSpace(tvUser))
	}
	client := &http.Client{Timeout: 12 * time.Second}
	resp, err := client.Do(req)
	if err != nil {
		return "", "", err
	}
	defer func() { _ = resp.Body.Close() }()
	var out map[string]any
	_ = json.NewDecoder(resp.Body).Decode(&out)
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return "", "", fmt.Errorf("m3u8 register http %d", resp.StatusCode)
	}
	token := strings.TrimSpace(anyToString(out["token"]))
	indexPath := strings.TrimSpace(anyToString(out["index"]))
	proxyPath := strings.TrimSpace(anyToString(out["proxy"]))
	if token == "" || indexPath == "" || proxyPath == "" {
		return "", "", errors.New("CatPawOpen m3u8 register 返回无效")
	}
	bu, _ := url.Parse(base)
	indexU, _ := bu.Parse(strings.TrimPrefix(indexPath, "/"))
	proxyU, _ := bu.Parse(strings.TrimPrefix(proxyPath, "/"))
	return indexU.String(), proxyU.String(), nil
}

func IsProbablyM3U8(u string) bool {
	s := strings.ToLower(strings.TrimSpace(u))
	return strings.Contains(s, ".m3u8")
}
