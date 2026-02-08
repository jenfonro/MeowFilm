package routes

import (
	"encoding/json"
	"net/http"
	"net/url"
	"runtime"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
)

type tmdbMultiSearchResponse struct {
	Results []struct {
		ID          int    `json:"id"`
		MediaType   string `json:"media_type"`
		Title       string `json:"title"`
		Name        string `json:"name"`
		PosterPath  string `json:"poster_path"`
		ReleaseDate string `json:"release_date"`
		FirstAir    string `json:"first_air_date"`
	} `json:"results"`
}

type tmdbTVDetailsResponse struct {
	Status           string `json:"status"`
	NumberOfEpisodes int    `json:"number_of_episodes"`
	LastEpisodeToAir *struct {
		EpisodeNumber int `json:"episode_number"`
	} `json:"last_episode_to_air"`
}

func handleAPITMDBSearch(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodGet {
		methodNotAllowed(w)
		return
	}

	q := strings.TrimSpace(r.URL.Query().Get("q"))
	if q == "" {
		q = strings.TrimSpace(r.URL.Query().Get("query"))
	}
	if q == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"error": "参数无效"})
		return
	}

	v4 := strings.TrimSpace(database.GetSetting("tmdb_v4_token"))
	v3 := strings.TrimSpace(database.GetSetting("tmdb_v3_key"))
	if v4 == "" && v3 == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{
			"error": "TMDB 未配置",
			"code":  "TMDB_TOKEN_INVALID",
		})
		return
	}

	language := strings.TrimSpace(database.GetSetting("tmdb_language"))
	if language == "" {
		language = "zh-CN"
	}
	region := strings.TrimSpace(database.GetSetting("tmdb_region"))
	if region == "" {
		region = "CN"
	}
	includeAdult := strings.TrimSpace(database.GetSetting("tmdb_include_adult")) == "1"

	u, _ := url.Parse("https://api.themoviedb.org/3/search/multi")
	params := u.Query()
	params.Set("query", q)
	params.Set("page", "1")
	if language != "" {
		params.Set("language", language)
	}
	if region != "" {
		params.Set("region", region)
	}
	params.Set("include_adult", boolToStr(includeAdult))
	if v3 != "" {
		params.Set("api_key", v3)
	}
	u.RawQuery = params.Encode()

	req, err := http.NewRequest(http.MethodGet, u.String(), nil)
	if err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"error": "请求构造失败"})
		return
	}
	req.Header.Set("Accept", "application/json")
	if v4 != "" {
		req.Header.Set("Authorization", "Bearer "+v4)
	}

	client := &http.Client{Timeout: 8 * time.Second}
	resp, err := client.Do(req)
	if err != nil {
		writeJSON(w, http.StatusBadGateway, map[string]any{
			"error": "TMDB 请求失败",
			"code":  "TMDB_CONNECT_FAILED",
		})
		return
	}
	defer resp.Body.Close()

	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		writeJSON(w, http.StatusBadGateway, map[string]any{
			"error":  "TMDB 返回异常",
			"code":   "TMDB_UPSTREAM_BAD_STATUS",
			"status": resp.StatusCode,
		})
		return
	}

	var data tmdbMultiSearchResponse
	if err := json.NewDecoder(resp.Body).Decode(&data); err != nil {
		writeJSON(w, http.StatusBadGateway, map[string]any{
			"error": "TMDB 解析失败",
			"code":  "TMDB_PARSE_FAILED",
		})
		return
	}

	type outItem struct {
		ID        string
		TMDBID    int
		MediaType string
		Name      string
		Pic       string
		Year      int
		Badge     string
	}
	items := []outItem{}
	for _, it := range data.Results {
		typ := strings.TrimSpace(it.MediaType)
		if typ != "movie" && typ != "tv" {
			continue
		}
		name := strings.TrimSpace(it.Title)
		if name == "" {
			name = strings.TrimSpace(it.Name)
		}
		if name == "" || it.ID <= 0 {
			continue
		}
		year := parseYear(it.ReleaseDate)
		if year == 0 {
			year = parseYear(it.FirstAir)
		}
		pic := ""
		if strings.TrimSpace(it.PosterPath) != "" {
			pic = "https://image.tmdb.org/t/p/w342" + strings.TrimSpace(it.PosterPath)
		}
		badge := ""
		if typ == "movie" {
			badge = "电影"
		}
		items = append(items, outItem{
			ID:        typ + ":" + strconv.Itoa(it.ID),
			TMDBID:    it.ID,
			MediaType: typ,
			Name:      name,
			Pic:       pic,
			Year:      year,
			Badge:     badge,
		})
	}

	fetchTVBadge := func(id int) (badge string, _ bool) {
		if id <= 0 {
			return "", false
		}
		u, _ := url.Parse("https://api.themoviedb.org/3/tv/" + strconv.Itoa(id))
		q := u.Query()
		if language != "" {
			q.Set("language", language)
		}
		if v3 != "" {
			q.Set("api_key", v3)
		}
		u.RawQuery = q.Encode()

		req, err := http.NewRequest(http.MethodGet, u.String(), nil)
		if err != nil {
			return "", false
		}
		req.Header.Set("Accept", "application/json")
		if v4 != "" {
			req.Header.Set("Authorization", "Bearer "+v4)
		}
		resp, err := client.Do(req)
		if err != nil {
			return "", false
		}
		defer resp.Body.Close()
		if resp.StatusCode < 200 || resp.StatusCode >= 300 {
			return "", false
		}
		var d tmdbTVDetailsResponse
		if err := json.NewDecoder(resp.Body).Decode(&d); err != nil {
			return "", false
		}
		status := strings.TrimSpace(d.Status)
		last := 0
		if d.LastEpisodeToAir != nil && d.LastEpisodeToAir.EpisodeNumber > 0 {
			last = d.LastEpisodeToAir.EpisodeNumber
		}
		total := 0
		if d.NumberOfEpisodes > 0 {
			total = d.NumberOfEpisodes
		}

		ended := status == "Ended"
		if ended {
			if total > 0 {
				return strconv.Itoa(total) + "集", true
			}
			if last > 0 {
				return strconv.Itoa(last) + "集", true
			}
			return "完结", true
		}

		if last > 0 {
			return "更新至" + strconv.Itoa(last) + "集", true
		}
		if total > 0 {
			return "更新至" + strconv.Itoa(total) + "集", true
		}
		return "更新中", true
	}

	// Fill TV badges with a bounded parallelism so search remains responsive.
	workerCount := 4
	if n := runtime.GOMAXPROCS(0); n > 0 && n < workerCount {
		workerCount = n
	}
	sem := make(chan struct{}, workerCount)
	var wg sync.WaitGroup
	for i := range items {
		if items[i].MediaType != "tv" || items[i].TMDBID <= 0 {
			continue
		}
		wg.Add(1)
		sem <- struct{}{}
		go func(idx int) {
			defer wg.Done()
			defer func() { <-sem }()
			if b, ok := fetchTVBadge(items[idx].TMDBID); ok && strings.TrimSpace(b) != "" {
				items[idx].Badge = b
			} else if items[idx].Badge == "" {
				items[idx].Badge = "剧集"
			}
		}(i)
	}
	wg.Wait()

	list := make([]map[string]any, 0, len(items))
	for _, it := range items {
		list = append(list, map[string]any{
			"id":        it.ID,
			"tmdbId":    it.TMDBID,
			"mediaType": it.MediaType,
			"name":      it.Name,
			"pic":       it.Pic,
			"year":      it.Year,
			"badge":     strings.TrimSpace(it.Badge),
		})
	}

	writeJSON(w, 200, map[string]any{"success": true, "list": list})
}

func boolToStr(v bool) string {
	if v {
		return "true"
	}
	return "false"
}

func parseYear(s string) int {
	raw := strings.TrimSpace(s)
	if len(raw) < 4 {
		return 0
	}
	y, err := strconv.Atoi(raw[:4])
	if err != nil {
		return 0
	}
	if y < 1900 || y > 2100 {
		return 0
	}
	return y
}
