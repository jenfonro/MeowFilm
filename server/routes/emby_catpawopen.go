package routes

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

	"github.com/jenfonro/meowfilm/internal/db"
)

type embyCatSearchItem struct {
	ID     string
	Name   string
	Pic    string
	Remark string
}

type embyCatEpisode struct {
	Name string
	URL  string
	Flag string
}

type embyCatPan struct {
	Label    string
	Episodes []embyCatEpisode
}

func embyNormalizeCatPawOpenAPIBase(input string) string {
	raw := strings.TrimSpace(input)
	if raw == "" {
		return ""
	}
	u, err := url.Parse(raw)
	if err != nil {
		return ""
	}
	u.Fragment = ""
	u.RawQuery = ""
	path := u.Path
	if path == "" {
		path = "/"
	}
	if idx := strings.Index(path, "/spider/"); idx >= 0 {
		path = path[:idx]
		if path == "" {
			path = "/"
		}
	}
	if regexp.MustCompile(`^/[a-f0-9]{10}/?$`).MatchString(path) {
		path = "/"
	}
	path = strings.TrimSuffix(path, "/spider")
	path = strings.TrimSuffix(path, "/full-config")
	path = strings.TrimSuffix(path, "/config")
	path = strings.TrimSuffix(path, "/website")
	if !strings.HasSuffix(path, "/") {
		path += "/"
	}
	u.Path = path
	return u.String()
}

func embyCatRequestSpider(apiBase string, spiderAPI string, action string, payload any) (map[string]any, error) {
	return embyCatRequestSpiderWithTimeout(apiBase, spiderAPI, action, payload, 12*time.Second)
}

func embyCatRequestSpiderWithTimeout(apiBase string, spiderAPI string, action string, payload any, timeout time.Duration) (map[string]any, error) {
	base := embyNormalizeCatPawOpenAPIBase(apiBase)
	if base == "" {
		return nil, errors.New("CatPawOpen 接口地址未设置")
	}
	act := strings.TrimSpace(action)
	sp := strings.TrimSpace(spiderAPI)
	if act == "" || sp == "" {
		return nil, errors.New("invalid args")
	}
	// spiderAPI is typically like "/spider/xxx" or "/<id>/spider/xxx"
	spiderPath := strings.TrimSuffix(sp, "/")
	target, err := url.Parse(base)
	if err != nil {
		return nil, errors.New("CatPawOpen base invalid")
	}
	target, _ = target.Parse(strings.TrimPrefix(spiderPath, "/") + "/" + url.PathEscape(act))

	body := payload
	if body == nil {
		body = map[string]any{}
	}
	b, _ := json.Marshal(body)
	req, _ := http.NewRequest(http.MethodPost, target.String(), bytes.NewReader(b))
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("Accept", "application/json")

	if timeout <= 0 {
		timeout = 12 * time.Second
	}
	client := &http.Client{Timeout: timeout}
	resp, err := client.Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	var out map[string]any
	_ = json.NewDecoder(resp.Body).Decode(&out)
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		msg := "请求失败"
		if out != nil {
			if m, ok := out["message"].(string); ok && strings.TrimSpace(m) != "" {
				msg = strings.TrimSpace(m)
			}
		}
		return nil, fmt.Errorf("%s (http %d)", msg, resp.StatusCode)
	}
	if out == nil {
		out = map[string]any{}
	}
	return out, nil
}

func embyCatRequestPlay(apiBase string, tvUser string, payload any) (map[string]any, error) {
	base := embyNormalizeCatPawOpenAPIBase(apiBase)
	if base == "" {
		return nil, errors.New("CatPawOpen 接口地址未设置")
	}
	target, _ := url.Parse(base)
	target, _ = target.Parse("play")

	body := payload
	if body == nil {
		body = map[string]any{}
	}
	b, _ := json.Marshal(body)
	req, _ := http.NewRequest(http.MethodPost, target.String(), bytes.NewReader(b))
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("Accept", "application/json")
	if strings.TrimSpace(tvUser) != "" {
		req.Header.Set("X-TV-User", strings.TrimSpace(tvUser))
	}

	client := &http.Client{Timeout: 15 * time.Second}
	resp, err := client.Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	var out map[string]any
	_ = json.NewDecoder(resp.Body).Decode(&out)
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		msg := "请求失败"
		if out != nil {
			if m, ok := out["message"].(string); ok && strings.TrimSpace(m) != "" {
				msg = strings.TrimSpace(m)
			}
		}
		return nil, fmt.Errorf("%s (http %d)", msg, resp.StatusCode)
	}
	if out == nil {
		out = map[string]any{}
	}
	return out, nil
}

func embyCatNormalizeSearchList(data map[string]any) []embyCatSearchItem {
	listAny, _ := data["list"].([]any)
	out := []embyCatSearchItem{}
	for _, it := range listAny {
		m, ok := it.(map[string]any)
		if !ok {
			continue
		}
		id := embyAnyToString(m["vod_id"])
		if id == "" {
			id = embyAnyToString(m["id"])
		}
		name := embyAnyToString(m["vod_name"])
		if name == "" {
			name = embyAnyToString(m["name"])
		}
		if strings.TrimSpace(name) == "" {
			continue
		}
		pic := embyAnyToString(m["vod_pic"])
		if pic == "" {
			pic = embyAnyToString(m["pic"])
		}
		remark := embyAnyToString(m["vod_remarks"])
		if remark == "" {
			remark = embyAnyToString(m["remark"])
		}
		out = append(out, embyCatSearchItem{
			ID:     strings.TrimSpace(id),
			Name:   strings.TrimSpace(name),
			Pic:    strings.TrimSpace(pic),
			Remark: strings.TrimSpace(remark),
		})
	}
	return out
}

func embyExtractDetailPlayFromURL(data map[string]any) (playFrom string, playURL string) {
	// data may wrap in list[0] / data.list[0] etc (same as frontend extractDetailFromResponse)
	pick := func(m map[string]any) (string, string) {
		if m == nil {
			return "", ""
		}
		from := embyAnyToString(m["vod_play_from"])
		if from == "" {
			from = embyAnyToString(m["playFrom"])
		}
		urlStr := embyAnyToString(m["vod_play_url"])
		if urlStr == "" {
			urlStr = embyAnyToString(m["playUrl"])
		}
		return strings.TrimSpace(from), strings.TrimSpace(urlStr)
	}
	if v, ok := data["list"].([]any); ok && len(v) > 0 {
		if m, ok := v[0].(map[string]any); ok {
			return pick(m)
		}
	}
	if d, ok := data["data"].(map[string]any); ok {
		if v, ok := d["list"].([]any); ok && len(v) > 0 {
			if m, ok := v[0].(map[string]any); ok {
				return pick(m)
			}
		}
	}
	if m, ok := data["vod"].(map[string]any); ok {
		return pick(m)
	}
	return "", ""
}

func embyParsePlaySources(fromStr string, urlStr string) []embyCatPan {
	fromStr = strings.TrimSpace(fromStr)
	urlStr = strings.TrimSpace(urlStr)
	if fromStr == "" && urlStr == "" {
		return nil
	}
	fromParts := strings.Split(fromStr, "$$$")
	urlParts := strings.Split(urlStr, "$$$")
	n := len(fromParts)
	if len(urlParts) > n {
		n = len(urlParts)
	}
	out := []embyCatPan{}
	for i := 0; i < n; i++ {
		label := ""
		if i < len(fromParts) {
			label = strings.TrimSpace(fromParts[i])
		}
		if label == "" {
			label = fmt.Sprintf("源%d", i+1)
		}
		u := ""
		if i < len(urlParts) {
			u = strings.TrimSpace(urlParts[i])
		}
		if u == "" {
			continue
		}
		segs := []string{}
		for _, s := range strings.Split(u, "#") {
			ss := strings.TrimSpace(s)
			if ss != "" {
				segs = append(segs, ss)
			}
		}
		eps := []embyCatEpisode{}
		for _, seg := range segs {
			name := seg
			id := seg
			if idx := strings.Index(seg, "$"); idx > 0 {
				name = strings.TrimSpace(seg[:idx])
				id = strings.TrimSpace(seg[idx+1:])
			}
			if name == "" {
				name = seg
			}
			if id == "" {
				id = seg
			}
			eps = append(eps, embyCatEpisode{Name: name, URL: id, Flag: label})
		}
		if len(eps) == 0 {
			continue
		}
		out = append(out, embyCatPan{Label: label, Episodes: eps})
	}
	return out
}

func embyAnyToString(v any) string {
	switch vv := v.(type) {
	case string:
		return vv
	case json.Number:
		return vv.String()
	case float64:
		// avoid fmt import in hot path; but ok.
		return fmt.Sprintf("%.0f", vv)
	default:
		return ""
	}
}

func embyResolveCatApiBaseForUser(database *db.DB, u *embyUser) string {
	if database == nil {
		return ""
	}
	var userBase string
	if u != nil && strings.TrimSpace(u.Username) != "" {
		_ = database.SQL().QueryRow(`SELECT cat_api_base FROM users WHERE username=? LIMIT 1`, strings.TrimSpace(u.Username)).Scan(&userBase)
	}
	userBase = strings.TrimSpace(userBase)
	serverBase := strings.TrimSpace(resolveCatPawOpenActiveBase(parseCatPawOpenServers(database.GetSetting("catpawopen_servers")), database.GetSetting("catpawopen_active")))
	if u != nil && strings.TrimSpace(u.Role) == "user" {
		return strings.TrimSpace(userBase)
	}
	if userBase != "" {
		return userBase
	}
	return serverBase
}

func embyResolvePlaybackFromTMDB(database *db.DB, u *embyUser, parsed *embyItemID) (finalURL string, finalHeaders map[string]string, err error) {
	if parsed == nil {
		return "", nil, errors.New("invalid item")
	}
	req := smartPlaybackRequest{
		Kind:    strings.TrimSpace(parsed.Kind),
		TMDBID:  parsed.TMDBID,
		Season:  parsed.Season,
		Episode: parsed.Episode,
		SubKind: strings.TrimSpace(parsed.SubKind),
	}
	return smartResolvePlaybackFromTMDB(database, u, req)
}

func embyNormalizeAggKey(s string) string {
	raw := strings.ToLower(strings.TrimSpace(s))
	if raw == "" {
		return ""
	}
	// remove common separators
	re := regexp.MustCompile(`[\s\.\-_,，:：;；!！?？·•/\\|]+`)
	raw = re.ReplaceAllString(raw, "")
	raw = strings.ReplaceAll(raw, "\u200b", "")
	raw = strings.ReplaceAll(raw, "\u200c", "")
	raw = strings.ReplaceAll(raw, "\u200d", "")
	raw = strings.ReplaceAll(raw, "\ufeff", "")
	return strings.TrimSpace(raw)
}

func embyMatchScore(qKey string, candKey string) int {
	if qKey == "" || candKey == "" {
		return 0
	}
	if candKey == qKey {
		return 1000
	}
	if strings.HasPrefix(candKey, qKey) {
		return 900
	}
	if idx := strings.Index(candKey, qKey); idx >= 0 {
		posBoost := 60 - embyMinInt(60, idx)
		lenBoost := 40 - embyMinInt(40, embyMaxInt(0, len(candKey)-len(qKey)))
		return 800 + posBoost + lenBoost
	}
	return 0
}

func embyGlobalEpisodeNo(seasons []embyTMDBSeason, season int, episode int) int {
	if episode <= 0 {
		return 0
	}
	if season <= 1 {
		return episode
	}
	sum := 0
	for _, s := range seasons {
		if s.Season <= 0 || s.EpisodeCount <= 0 {
			continue
		}
		if s.Season < season {
			sum += s.EpisodeCount
		}
	}
	return sum + episode
}

func embyExtractSiteIDFromSpiderAPI(spiderAPI string) string {
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

func embyPickFirstPlayableURL(playRaw map[string]any) string {
	// Mirror frontend pickFirstPlayableUrl:
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
	// special-case: [id, httpUrl]
	if len(arrAny) >= 2 {
		s0 := strings.TrimSpace(embyAnyToString(arrAny[0]))
		s1 := strings.TrimSpace(embyAnyToString(arrAny[1]))
		if !strings.HasPrefix(strings.ToLower(s0), "http") && strings.HasPrefix(strings.ToLower(s1), "http") {
			return s1
		}
	}
	for _, it := range arrAny {
		s := strings.TrimSpace(embyAnyToString(it))
		if strings.HasPrefix(strings.ToLower(s), "http") {
			return s
		}
	}
	return ""
}

func embyRewriteProxyURLToBase(raw string, apiBase string, tvUser string) string {
	in := strings.TrimSpace(raw)
	if in == "" {
		return ""
	}
	base := embyNormalizeCatPawOpenAPIBase(apiBase)
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
	// Resolve pathname against configured base; keep query/hash.
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

func embyRegisterCatM3U8(apiBase string, tvUser string, playURL string, headers map[string]string) (indexURL string, proxyURL string, err error) {
	base := embyNormalizeCatPawOpenAPIBase(apiBase)
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
	defer resp.Body.Close()
	var out map[string]any
	_ = json.NewDecoder(resp.Body).Decode(&out)
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return "", "", fmt.Errorf("m3u8 register http %d", resp.StatusCode)
	}
	token := strings.TrimSpace(embyAnyToString(out["token"]))
	indexPath := strings.TrimSpace(embyAnyToString(out["index"]))
	proxyPath := strings.TrimSpace(embyAnyToString(out["proxy"]))
	if token == "" || indexPath == "" || proxyPath == "" {
		return "", "", errors.New("CatPawOpen m3u8 register 返回无效")
	}
	bu, _ := url.Parse(base)
	indexU, _ := bu.Parse(strings.TrimPrefix(indexPath, "/"))
	proxyU, _ := bu.Parse(strings.TrimPrefix(proxyPath, "/"))
	return indexU.String(), proxyU.String(), nil
}

func embyIsProbablyM3U8(u string) bool {
	s := strings.ToLower(strings.TrimSpace(u))
	return strings.Contains(s, ".m3u8")
}

func embyMinInt(a, b int) int {
	if a < b {
		return a
	}
	return b
}
func embyMaxInt(a, b int) int {
	if a > b {
		return a
	}
	return b
}
