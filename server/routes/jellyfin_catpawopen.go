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

type jellyfinCatSearchItem struct {
	ID     string
	Name   string
	Pic    string
	Remark string
}

type jellyfinCatEpisode struct {
	Name string
	URL  string
	Flag string
}

type jellyfinCatPan struct {
	Label    string
	Episodes []jellyfinCatEpisode
}

func jellyfinNormalizeCatPawOpenAPIBase(input string) string {
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

func jellyfinCatRequestSpider(apiBase string, spiderAPI string, action string, payload any) (map[string]any, error) {
	base := jellyfinNormalizeCatPawOpenAPIBase(apiBase)
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

	client := &http.Client{Timeout: 12 * time.Second}
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

func jellyfinCatRequestPlay(apiBase string, tvUser string, payload any) (map[string]any, error) {
	base := jellyfinNormalizeCatPawOpenAPIBase(apiBase)
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

func jellyfinCatNormalizeSearchList(data map[string]any) []jellyfinCatSearchItem {
	listAny, _ := data["list"].([]any)
	out := []jellyfinCatSearchItem{}
	for _, it := range listAny {
		m, ok := it.(map[string]any)
		if !ok {
			continue
		}
		id := jellyfinAnyToString(m["vod_id"])
		if id == "" {
			id = jellyfinAnyToString(m["id"])
		}
		name := jellyfinAnyToString(m["vod_name"])
		if name == "" {
			name = jellyfinAnyToString(m["name"])
		}
		if strings.TrimSpace(name) == "" {
			continue
		}
		pic := jellyfinAnyToString(m["vod_pic"])
		if pic == "" {
			pic = jellyfinAnyToString(m["pic"])
		}
		remark := jellyfinAnyToString(m["vod_remarks"])
		if remark == "" {
			remark = jellyfinAnyToString(m["remark"])
		}
		out = append(out, jellyfinCatSearchItem{
			ID:     strings.TrimSpace(id),
			Name:   strings.TrimSpace(name),
			Pic:    strings.TrimSpace(pic),
			Remark: strings.TrimSpace(remark),
		})
	}
	return out
}

func jellyfinExtractDetailPlayFromURL(data map[string]any) (playFrom string, playURL string) {
	// data may wrap in list[0] / data.list[0] etc (same as frontend extractDetailFromResponse)
	pick := func(m map[string]any) (string, string) {
		if m == nil {
			return "", ""
		}
		from := jellyfinAnyToString(m["vod_play_from"])
		if from == "" {
			from = jellyfinAnyToString(m["playFrom"])
		}
		urlStr := jellyfinAnyToString(m["vod_play_url"])
		if urlStr == "" {
			urlStr = jellyfinAnyToString(m["playUrl"])
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

func jellyfinParsePlaySources(fromStr string, urlStr string) []jellyfinCatPan {
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
	out := []jellyfinCatPan{}
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
		eps := []jellyfinCatEpisode{}
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
			eps = append(eps, jellyfinCatEpisode{Name: name, URL: id, Flag: label})
		}
		if len(eps) == 0 {
			continue
		}
		out = append(out, jellyfinCatPan{Label: label, Episodes: eps})
	}
	return out
}

func jellyfinAnyToString(v any) string {
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

func jellyfinResolveCatApiBaseForUser(database *db.DB, u *jellyfinUser) string {
	// MVP: mimic frontend logic (user role uses their own cat_api_base; admin prefers user's if set, else server).
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

func jellyfinResolvePlaybackFromTMDB(database *db.DB, u *jellyfinUser, parsed *jellyfinItemID) (finalURL string, finalHeaders map[string]string, err error) {
	if parsed == nil {
		return "", nil, errors.New("invalid item")
	}
	apiBase := jellyfinResolveCatApiBaseForUser(database, u)
	if apiBase == "" {
		return "", nil, errors.New("CatPawOpen 接口地址未设置")
	}
	tvUser := ""
	if u != nil {
		tvUser = u.Username
	}

	// Build search query from TMDB title.
	searchTitle := ""
	var globalEpisodeNo int
	if parsed.Kind == "movie" {
		md, err := jellyfinTMDBGetMovieDetail(database, parsed.TMDBID)
		if err != nil || md == nil || strings.TrimSpace(md.Title) == "" {
			return "", nil, errors.New("TMDB 请求失败")
		}
		searchTitle = md.Title
		globalEpisodeNo = 1
	} else if parsed.Kind == "tv" {
		td, err := jellyfinTMDBGetTVDetail(database, parsed.TMDBID)
		if err != nil || td == nil || strings.TrimSpace(td.Title) == "" {
			return "", nil, errors.New("TMDB 请求失败")
		}
		searchTitle = td.Title
		// Convert (season, episode) to global episode number for single-list spiders.
		if parsed.SubKind == "episode" {
			globalEpisodeNo = jellyfinGlobalEpisodeNo(td.Seasons, parsed.Season, parsed.Episode)
		} else {
			globalEpisodeNo = 1
		}
	} else {
		return "", nil, errors.New("unsupported kind")
	}
	searchTitle = strings.TrimSpace(searchTitle)
	if searchTitle == "" {
		return "", nil, errors.New("missing title")
	}

	// Search across enabled sites (server-level).
	sites := normalizeSitesFromJSON(database.GetSetting("video_source_sites"))
	statusMap := parseJSONBoolMap(database.GetSetting("video_source_site_status"))
	searchMap := parseJSONBoolMap(database.GetSetting("video_source_site_search"))
	ordered := applySiteOrder(sites, parseJSONStringArray(database.GetSetting("video_source_site_order")))

	type cand struct {
		Site site
		Item jellyfinCatSearchItem
		Score int
	}
	best := cand{Score: -1}

	qKey := jellyfinNormalizeAggKey(searchTitle)
	for _, s := range ordered {
		if s.Key == "" || s.API == "" {
			continue
		}
		if isConfigCenterSite(s) {
			continue
		}
		enabled, ok := statusMap[s.Key]
		if ok && !enabled {
			continue
		}
		searchEnabled, ok := searchMap[s.Key]
		if ok && !searchEnabled {
			continue
		}

		raw, err := jellyfinCatRequestSpider(apiBase, s.API, "search", map[string]any{"wd": searchTitle, "page": 1})
		if err != nil {
			continue
		}
		items := jellyfinCatNormalizeSearchList(raw)
		for _, it := range items {
			key := jellyfinNormalizeAggKey(it.Name)
			if key == "" {
				continue
			}
			score := jellyfinMatchScore(qKey, key)
			if score > best.Score {
				best = cand{Site: s, Item: it, Score: score}
			}
		}
		// quick exit on exact
		if best.Score >= 1000 {
			break
		}
	}

	if best.Score < 0 || best.Item.ID == "" {
		return "", nil, errors.New("未找到可用资源")
	}

	// Detail to get episode ids.
	detailRaw, err := jellyfinCatRequestSpider(apiBase, best.Site.API, "detail", map[string]any{"id": best.Item.ID})
	if err != nil {
		return "", nil, errors.New("获取详情失败")
	}
	playFrom, playURL := jellyfinExtractDetailPlayFromURL(detailRaw)
	pans := jellyfinParsePlaySources(playFrom, playURL)
	if len(pans) == 0 {
		return "", nil, errors.New("该资源无可用播放列表")
	}
	pan := pans[0]
	if len(pan.Episodes) == 0 {
		return "", nil, errors.New("该资源无可用剧集")
	}
	idx := 0
	if globalEpisodeNo > 0 {
		idx = globalEpisodeNo - 1
	}
	if idx < 0 {
		idx = 0
	}
	if idx >= len(pan.Episodes) {
		idx = len(pan.Episodes) - 1
	}
	ep := pan.Episodes[idx]
	if ep.URL == "" {
		return "", nil, errors.New("剧集无效")
	}

	siteID := jellyfinExtractSiteIDFromSpiderAPI(best.Site.API)
	playPayload := map[string]any{
		"flag":    strings.TrimSpace(ep.Flag),
		"id":      strings.TrimSpace(ep.URL),
		"siteApi": strings.TrimSpace(best.Site.API),
	}
	if siteID != "" {
		playPayload["siteId"] = siteID
	}
	playRaw, err := jellyfinCatRequestPlay(apiBase, tvUser, playPayload)
	if err != nil {
		return "", nil, errors.New("获取播放地址失败")
	}
	urlPicked := jellyfinPickFirstPlayableURL(playRaw)
	if urlPicked == "" {
		return "", nil, errors.New("无可用播放地址")
	}
	urlPicked = jellyfinRewriteProxyURLToBase(urlPicked, apiBase, tvUser)
	headers := map[string]string{}
	if h, ok := playRaw["header"].(map[string]any); ok {
		for k, v := range h {
			kk := strings.TrimSpace(k)
			if kk == "" {
				continue
			}
			sv := strings.TrimSpace(jellyfinAnyToString(v))
			if sv == "" {
				continue
			}
			headers[kk] = sv
		}
	}
	return urlPicked, headers, nil
}

func jellyfinNormalizeAggKey(s string) string {
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

func jellyfinMatchScore(qKey string, candKey string) int {
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
		posBoost := 60 - jellyfinMinInt(60, idx)
		lenBoost := 40 - jellyfinMinInt(40, jellyfinMaxInt(0, len(candKey)-len(qKey)))
		return 800 + posBoost + lenBoost
	}
	return 0
}

func jellyfinGlobalEpisodeNo(seasons []jellyfinTMDBSeason, season int, episode int) int {
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

func jellyfinExtractSiteIDFromSpiderAPI(spiderAPI string) string {
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

func jellyfinPickFirstPlayableURL(playRaw map[string]any) string {
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
			s0 := strings.TrimSpace(jellyfinAnyToString(arrAny[0]))
			s1 := strings.TrimSpace(jellyfinAnyToString(arrAny[1]))
		if !strings.HasPrefix(strings.ToLower(s0), "http") && strings.HasPrefix(strings.ToLower(s1), "http") {
			return s1
		}
	}
	for _, it := range arrAny {
		s := strings.TrimSpace(jellyfinAnyToString(it))
		if strings.HasPrefix(strings.ToLower(s), "http") {
			return s
		}
	}
	return ""
}

func jellyfinRewriteProxyURLToBase(raw string, apiBase string, tvUser string) string {
	in := strings.TrimSpace(raw)
	if in == "" {
		return ""
	}
	base := jellyfinNormalizeCatPawOpenAPIBase(apiBase)
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

func jellyfinRegisterCatM3U8(apiBase string, tvUser string, playURL string, headers map[string]string) (indexURL string, proxyURL string, err error) {
	base := jellyfinNormalizeCatPawOpenAPIBase(apiBase)
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
	token := strings.TrimSpace(jellyfinAnyToString(out["token"]))
	indexPath := strings.TrimSpace(jellyfinAnyToString(out["index"]))
	proxyPath := strings.TrimSpace(jellyfinAnyToString(out["proxy"]))
	if token == "" || indexPath == "" || proxyPath == "" {
		return "", "", errors.New("CatPawOpen m3u8 register 返回无效")
	}
	bu, _ := url.Parse(base)
	indexU, _ := bu.Parse(strings.TrimPrefix(indexPath, "/"))
	proxyU, _ := bu.Parse(strings.TrimPrefix(proxyPath, "/"))
	return indexU.String(), proxyU.String(), nil
}

func jellyfinIsProbablyM3U8(u string) bool {
	s := strings.ToLower(strings.TrimSpace(u))
	return strings.Contains(s, ".m3u8")
}

func jellyfinMinInt(a, b int) int {
	if a < b {
		return a
	}
	return b
}
func jellyfinMaxInt(a, b int) int {
	if a > b {
		return a
	}
	return b
}
