package douban

import (
	"crypto/sha512"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/http/cookiejar"
	"net/url"
	"regexp"
	"strconv"
	"strings"
	"time"
	"unicode"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/cache"
)

type SearchHTMLResult struct {
	Body []byte
}

var (
	challengeInputPattern = regexp.MustCompile(`(?is)<input[^>]+id="(tok|cha|red)"[^>]+value="([^"]*)"`)
	searchDataPattern     = regexp.MustCompile(`(?s)window\.__DATA__\s*=\s*(\{.*?\})\s*;\s*window\.__USER__`)
)

var doubanSearchDataCache = cache.NewTwoTierTTLInflightCache[map[string]any](6*time.Hour, 1024, 2*time.Minute, 2048)

func FetchSearchHTML(database *db.DB, keyword string) (*SearchHTMLResult, error) {
	q := strings.TrimSpace(keyword)
	if q == "" {
		return nil, fmt.Errorf("missing keyword")
	}

	jar, _ := cookiejar.New(nil)
	client := &http.Client{
		Timeout: 20 * time.Second,
		Jar:     jar,
		CheckRedirect: func(req *http.Request, via []*http.Request) error {
			return http.ErrUseLastResponse
		},
	}

	searchURL := "https://search.douban.com/movie/subject_search?search_text=" + url.QueryEscape(q) + "&cat=1002"
	resp, body, _, _, err := fetchSearchHTMLWithRedirects(client, searchURL, searchCookieFromDB(database))
	if err != nil {
		return nil, err
	}
	if resp != nil && resp.Body != nil {
		_ = resp.Body.Close()
	}
	return &SearchHTMLResult{Body: body}, nil
}

func fetchSearchHTMLWithRedirects(client *http.Client, initialURL string, cookieHeader string) (*http.Response, []byte, string, bool, error) {
	currentURL := strings.TrimSpace(initialURL)
	referer := ""
	challenged := false

	for step := 0; step < 6; step++ {
		resp, body, err := doDoubanSearchRequest(client, http.MethodGet, currentURL, referer, nil, cookieHeader)
		if err != nil {
			return nil, nil, currentURL, challenged, err
		}

		finalURL := currentURL
		if resp != nil && resp.Request != nil && resp.Request.URL != nil {
			finalURL = resp.Request.URL.String()
		}

		if isChallengeHTML(body) {
			challenged = true
			nextURL, challengeErr := completeSearchChallenge(client, finalURL, body, cookieHeader)
			if challengeErr != nil {
				return nil, nil, finalURL, challenged, challengeErr
			}
			referer = finalURL
			currentURL = nextURL
			if resp.Body != nil {
				_ = resp.Body.Close()
			}
			continue
		}

		if isRedirectStatus(resp.StatusCode) {
			locationURL := resolveRedirectURL(finalURL, resp.Header.Get("Location"))
			if locationURL == "" {
				return nil, nil, finalURL, challenged, fmt.Errorf("douban search redirect missing location")
			}
			if resp.Body != nil {
				_ = resp.Body.Close()
			}
			referer = finalURL
			currentURL = locationURL
			continue
		}

		return resp, body, finalURL, challenged, nil
	}

	return nil, nil, currentURL, challenged, fmt.Errorf("douban search redirect loop")
}

func FetchSearchPayload(database *db.DB, keyword string) (map[string]any, bool, error) {
	q := strings.TrimSpace(keyword)
	if q == "" {
		return nil, false, fmt.Errorf("missing keyword")
	}
	result, fromCache, err := doubanSearchDataCache.Do("search_html:"+strings.ToLower(q), func() (map[string]any, error) {
		htmlResult, fetchErr := FetchSearchHTML(database, q)
		if fetchErr != nil {
			return nil, fetchErr
		}
		data, parseErr := extractLegacySearchPayload(q, htmlResult)
		if parseErr != nil {
			return nil, parseErr
		}
		return data, nil
	})
	if err != nil {
		return nil, false, err
	}
	return result, fromCache, nil
}

func SearchCookieConfigured(database *db.DB) bool {
	return strings.TrimSpace(searchCookieFromDB(database)) != ""
}

func extractLegacySearchPayload(keyword string, htmlResult *SearchHTMLResult) (map[string]any, error) {
	if htmlResult == nil {
		return nil, fmt.Errorf("empty search result")
	}
	matches := searchDataPattern.FindSubmatch(htmlResult.Body)
	if len(matches) < 2 {
		return nil, fmt.Errorf("douban search data not found")
	}

	var payload struct {
		Count int    `json:"count"`
		Start int    `json:"start"`
		Total int    `json:"total"`
		Text  string `json:"text"`
		Items []struct {
			ID        int    `json:"id"`
			Title     string `json:"title"`
			URL       string `json:"url"`
			CoverURL  string `json:"cover_url"`
			Abstract  string `json:"abstract"`
			Abstract2 string `json:"abstract_2"`
			Labels    []struct {
				Color string `json:"color"`
				Text  string `json:"text"`
			} `json:"labels"`
			Rating struct {
				Count      int     `json:"count"`
				RatingInfo string  `json:"rating_info"`
				StarCount  float64 `json:"star_count"`
				Value      float64 `json:"value"`
			} `json:"rating"`
		} `json:"items"`
	}
	if err := json.Unmarshal(matches[1], &payload); err != nil {
		return nil, err
	}

	items := make([]map[string]any, 0, len(payload.Items))
	for _, item := range payload.Items {
		labels := make([]string, 0, len(item.Labels))
		kind := ""
		canPlay := false
		for _, label := range item.Labels {
			text := strings.TrimSpace(label.Text)
			if text == "" {
				continue
			}
			labels = append(labels, text)
			if kind == "" && (text == "剧集" || text == "电影") {
				kind = mapLabelToKind(text)
			}
			if text == "可播放" {
				canPlay = true
			}
		}
		if kind == "" {
			kind = inferKindFromURL(item.URL)
		}
		rawTitle := normalizeInvisibleText(item.Title)
		year := extractYear(rawTitle)
		title := cleanSearchTitle(rawTitle)
		typeName := "电影"
		if kind == "tv" {
			typeName = "电视剧"
		}
		id := strconv.Itoa(item.ID)
		items = append(items, map[string]any{
			"layout":      "subject",
			"type_name":   typeName,
			"target_id":   id,
			"target_type": kind,
			"target": map[string]any{
				"rating": map[string]any{
					"count":      item.Rating.Count,
					"max":        10,
					"star_count": item.Rating.StarCount,
					"value":      item.Rating.Value,
				},
				"controversy_reason": "",
				"title":              title,
				"abstract":           "",
				"has_linewatch":      canPlay,
				"uri":                "douban://douban.com/" + kind + "/" + id,
				"cover_url":          strings.TrimSpace(item.CoverURL),
				"year":               strconv.Itoa(year),
				"card_subtitle":      strings.TrimSpace(item.Abstract),
				"id":                 id,
				"null_rating_reason": strings.TrimSpace(item.Rating.RatingInfo),
			},
		})
	}
	keywordText := firstNonEmpty(strings.TrimSpace(payload.Text), keyword)
	return map[string]any{
		"banned":     "",
		"fuzzy":      "",
		"promotion":  []any{},
		"search_egg": nil,
		"smart_box":  []any{},
		"contents":   map[string]any{"count": 0, "start": 0, "total": 0, "items": []any{}},
		"subjects": map[string]any{
			"target_name":        `更多热门“` + keywordText + `”书影音`,
			"show_more_subjects": payload.Total > len(items),
			"items":              items,
		},
	}, nil
}

func doDoubanSearchRequest(client *http.Client, method string, targetURL string, referer string, form url.Values, cookieHeader string) (*http.Response, []byte, error) {
	var bodyReader io.Reader
	if method == http.MethodPost && form != nil {
		bodyReader = strings.NewReader(form.Encode())
	}
	req, err := http.NewRequest(method, targetURL, bodyReader)
	if err != nil {
		return nil, nil, err
	}
	req.Header.Set("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/132.0.0.0 Safari/537.36")
	req.Header.Set("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8")
	req.Header.Set("Accept-Language", "zh-CN,zh;q=0.9")
	req.Header.Set("Cache-Control", "no-cache")
	req.Header.Set("Pragma", "no-cache")
	req.Header.Set("Upgrade-Insecure-Requests", "1")
	if strings.TrimSpace(referer) != "" {
		req.Header.Set("Referer", referer)
	}
	if method == http.MethodPost && form != nil {
		req.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	}
	if strings.TrimSpace(cookieHeader) != "" {
		req.Header.Set("Cookie", strings.TrimSpace(cookieHeader))
	}
	resp, err := client.Do(req)
	if err != nil {
		return nil, nil, err
	}
	body, readErr := io.ReadAll(io.LimitReader(resp.Body, 8*1024*1024))
	if readErr != nil {
		_ = resp.Body.Close()
		return nil, nil, readErr
	}
	return resp, body, nil
}

func completeSearchChallenge(client *http.Client, challengeURL string, body []byte, cookieHeader string) (string, error) {
	fields := parseChallengeFields(body)
	tok := fields["tok"]
	cha := fields["cha"]
	red := fields["red"]
	if tok == "" || cha == "" || red == "" {
		return "", fmt.Errorf("douban challenge fields missing")
	}

	sol := solveChallenge(cha, 4)
	form := url.Values{}
	form.Set("tok", tok)
	form.Set("cha", cha)
	form.Set("sol", strconv.Itoa(sol))
	form.Set("red", red)

	resp, body2, err := doDoubanSearchRequest(client, http.MethodPost, "https://sec.douban.com/c", challengeURL, form, cookieHeader)
	if err != nil {
		return "", err
	}
	defer func() {
		if resp != nil && resp.Body != nil {
			_ = resp.Body.Close()
		}
	}()

	if isRedirectStatus(resp.StatusCode) {
		locationURL := resolveRedirectURL("https://sec.douban.com/c", resp.Header.Get("Location"))
		if locationURL != "" {
			return locationURL, nil
		}
	}
	if isChallengeHTML(body2) {
		return "", fmt.Errorf("douban challenge not passed")
	}
	if strings.TrimSpace(red) != "" {
		return red, nil
	}
	if resp != nil && resp.Request != nil && resp.Request.URL != nil {
		return resp.Request.URL.String(), nil
	}
	return "", fmt.Errorf("douban challenge redirect missing")
}

func parseChallengeFields(body []byte) map[string]string {
	out := map[string]string{}
	for _, hit := range challengeInputPattern.FindAllSubmatch(body, -1) {
		if len(hit) < 3 {
			continue
		}
		key := strings.TrimSpace(string(hit[1]))
		val := htmlEntityDecode(strings.TrimSpace(string(hit[2])))
		if key != "" {
			out[key] = val
		}
	}
	return out
}

func solveChallenge(cha string, difficulty int) int {
	prefix := strings.Repeat("0", difficulty)
	for nonce := 1; nonce < 1_000_000_000; nonce++ {
		sum := sha512.Sum512([]byte(cha + strconv.Itoa(nonce)))
		if strings.HasPrefix(hex.EncodeToString(sum[:]), prefix) {
			return nonce
		}
	}
	return 0
}

func isChallengeHTML(body []byte) bool {
	s := string(body)
	return strings.Contains(s, `id="sec"`) && strings.Contains(s, `id="cha"`) && strings.Contains(s, `id="sol"`)
}

func isRedirectStatus(code int) bool {
	return code == http.StatusMovedPermanently ||
		code == http.StatusFound ||
		code == http.StatusSeeOther ||
		code == http.StatusTemporaryRedirect ||
		code == http.StatusPermanentRedirect
}

func resolveRedirectURL(baseURL string, location string) string {
	loc := strings.TrimSpace(location)
	if loc == "" {
		return ""
	}
	u, err := url.Parse(loc)
	if err == nil && u.IsAbs() {
		return u.String()
	}
	base, err := url.Parse(strings.TrimSpace(baseURL))
	if err != nil {
		return ""
	}
	return base.ResolveReference(u).String()
}

func htmlEntityDecode(v string) string {
	replacer := strings.NewReplacer(
		"&amp;", "&",
		"&quot;", `"`,
		"&#39;", "'",
		"&lt;", "<",
		"&gt;", ">",
	)
	return replacer.Replace(v)
}

func extractYear(title string) int {
	m := regexp.MustCompile(`\((19|20)\d{2}\)`).FindString(normalizeInvisibleText(title))
	if m == "" {
		return 0
	}
	n, _ := strconv.Atoi(strings.Trim(m, "()"))
	return n
}

func cleanSearchTitle(title string) string {
	s := strings.TrimSpace(normalizeInvisibleText(title))
	return regexp.MustCompile(`\s*[（(](19|20)\d{2}[)）]\s*$`).ReplaceAllString(s, "")
}

func mapLabelToKind(label string) string {
	switch strings.TrimSpace(label) {
	case "剧集":
		return "tv"
	case "电影":
		return "movie"
	default:
		return ""
	}
}

func inferKindFromURL(raw string) string {
	if strings.Contains(raw, "/subject/") {
		return "movie"
	}
	return ""
}

func searchCookieFromDB(database *db.DB) string {
	if database == nil {
		return ""
	}
	cfg, err := database.ReadAppConfig()
	if err != nil {
		return ""
	}
	return strings.TrimSpace(cfg.DoubanSearchCookie)
}

func normalizeInvisibleText(value string) string {
	if strings.TrimSpace(value) == "" {
		return ""
	}
	return strings.Map(func(r rune) rune {
		switch r {
		case '\u200e', '\u200f', '\u202a', '\u202b', '\u202c', '\u202d', '\u202e', '\u2066', '\u2067', '\u2068', '\u2069':
			return -1
		}
		if unicode.Is(unicode.Cf, r) {
			return -1
		}
		return r
	}, value)
}
