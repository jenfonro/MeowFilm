package tmdb

import (
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
)

type Credits struct {
	MediaType string
	ID        int
	Cast      []Cast
	Crew      []Crew
}

type Cast struct {
	ID      int
	Name    string
	Role    string
	Profile string
	Order   int
}

type Crew struct {
	ID      int
	Name    string
	Job     string
	Dept    string
	Profile string
}

type creditsCacheEntry struct {
	Expire time.Time
	Data   *Credits
}

type personProfileCacheEntry struct {
	Expire time.Time
	Path   string
}

var metadataCreditsCache = struct {
	sync.Mutex
	M map[string]creditsCacheEntry
}{M: map[string]creditsCacheEntry{}}

var metadataPersonProfileCache = struct {
	sync.Mutex
	M map[int]personProfileCacheEntry
}{M: map[int]personProfileCacheEntry{}}

const metadataCreditsCacheTTL = 10 * time.Minute
const metadataPersonProfileCacheTTL = 24 * time.Hour

func RememberPersonProfile(personID int, profilePath string) {
	if personID <= 0 {
		return
	}
	p := strings.TrimSpace(profilePath)
	if p == "" {
		return
	}
	now := time.Now()
	metadataPersonProfileCache.Lock()
	if metadataPersonProfileCache.M == nil {
		metadataPersonProfileCache.M = map[int]personProfileCacheEntry{}
	}
	metadataPersonProfileCache.M[personID] = personProfileCacheEntry{
		Expire: now.Add(metadataPersonProfileCacheTTL),
		Path:   p,
	}
	metadataPersonProfileCache.Unlock()
}

func cachedPersonProfile(personID int) string {
	if personID <= 0 {
		return ""
	}
	now := time.Now()
	metadataPersonProfileCache.Lock()
	defer metadataPersonProfileCache.Unlock()
	if metadataPersonProfileCache.M == nil {
		return ""
	}
	hit, ok := metadataPersonProfileCache.M[personID]
	if !ok || strings.TrimSpace(hit.Path) == "" {
		return ""
	}
	if !hit.Expire.IsZero() && hit.Expire.Before(now) {
		delete(metadataPersonProfileCache.M, personID)
		return ""
	}
	return strings.TrimSpace(hit.Path)
}

func GetCredits(database *db.DB, mediaType string, tmdbID int) (*Credits, error) {
	typ := strings.ToLower(strings.TrimSpace(mediaType))
	if (typ != "movie" && typ != "tv") || tmdbID <= 0 {
		return nil, errors.New("invalid args")
	}

	cacheKey := typ + ":" + strconv.Itoa(tmdbID) + ":credits"
	now := time.Now()
	metadataCreditsCache.Lock()
	if metadataCreditsCache.M != nil {
		if hit, ok := metadataCreditsCache.M[cacheKey]; ok && hit.Data != nil && hit.Expire.After(now) {
			d := hit.Data
			metadataCreditsCache.Unlock()
			return d, nil
		}
	}
	metadataCreditsCache.Unlock()

	token, tokenKind := resolveTMDBToken(database)
	if token == "" || tokenKind == "" {
		return nil, errors.New("TMDB not configured")
	}
	lang := tmdbDetailLanguage(database)
	apiBase := resolveTMDBAPIBase(database)
	u, _ := url.Parse(joinTMDBAPI(apiBase, fmt.Sprintf("%s/%d/credits", typ, tmdbID)))
	params := u.Query()
	if strings.TrimSpace(lang) != "" {
		params.Set("language", strings.TrimSpace(lang))
	}
	if tokenKind == "v3" {
		params.Set("api_key", token)
	}
	u.RawQuery = params.Encode()

	req, _ := http.NewRequest(http.MethodGet, u.String(), nil)
	req.Header.Set("Accept", "application/json")
	if tokenKind == "v4" {
		req.Header.Set("Authorization", "Bearer "+token)
	}
	client := &http.Client{Timeout: 10 * time.Second}
	resp, err := client.Do(req)
	if err != nil {
		return nil, err
	}
	defer func() { _ = resp.Body.Close() }()
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return nil, fmt.Errorf("tmdb http %d", resp.StatusCode)
	}

	var raw struct {
		Cast []struct {
			ID          int    `json:"id"`
			Name        string `json:"name"`
			Character   string `json:"character"`
			Order       int    `json:"order"`
			ProfilePath string `json:"profile_path"`
		} `json:"cast"`
		Crew []struct {
			ID          int    `json:"id"`
			Name        string `json:"name"`
			Job         string `json:"job"`
			Department  string `json:"department"`
			ProfilePath string `json:"profile_path"`
		} `json:"crew"`
	}
	if err := json.NewDecoder(resp.Body).Decode(&raw); err != nil {
		return nil, err
	}

	out := &Credits{MediaType: typ, ID: tmdbID}
	if len(raw.Cast) > 0 {
		out.Cast = make([]Cast, 0, len(raw.Cast))
		for _, c := range raw.Cast {
			name := strings.TrimSpace(c.Name)
			if c.ID <= 0 || name == "" {
				continue
			}
			RememberPersonProfile(c.ID, c.ProfilePath)
			out.Cast = append(out.Cast, Cast{
				ID:      c.ID,
				Name:    name,
				Role:    strings.TrimSpace(c.Character),
				Profile: strings.TrimSpace(c.ProfilePath),
				Order:   c.Order,
			})
		}
	}
	if len(raw.Crew) > 0 {
		out.Crew = make([]Crew, 0, len(raw.Crew))
		for _, c := range raw.Crew {
			name := strings.TrimSpace(c.Name)
			if c.ID <= 0 || name == "" {
				continue
			}
			RememberPersonProfile(c.ID, c.ProfilePath)
			out.Crew = append(out.Crew, Crew{
				ID:      c.ID,
				Name:    name,
				Job:     strings.TrimSpace(c.Job),
				Dept:    strings.TrimSpace(c.Department),
				Profile: strings.TrimSpace(c.ProfilePath),
			})
		}
	}

	metadataCreditsCache.Lock()
	if metadataCreditsCache.M == nil {
		metadataCreditsCache.M = map[string]creditsCacheEntry{}
	}
	metadataCreditsCache.M[cacheKey] = creditsCacheEntry{
		Expire: now.Add(metadataCreditsCacheTTL),
		Data:   out,
	}
	metadataCreditsCache.Unlock()
	return out, nil
}

func GetPersonProfile(database *db.DB, personID int) (string, error) {
	if personID <= 0 {
		return "", errors.New("invalid person id")
	}
	if hit := cachedPersonProfile(personID); hit != "" {
		return hit, nil
	}

	token, tokenKind := resolveTMDBToken(database)
	if token == "" || tokenKind == "" {
		return "", errors.New("TMDB not configured")
	}
	lang := tmdbDetailLanguage(database)
	apiBase := resolveTMDBAPIBase(database)
	u, _ := url.Parse(joinTMDBAPI(apiBase, fmt.Sprintf("person/%d", personID)))
	params := u.Query()
	if strings.TrimSpace(lang) != "" {
		params.Set("language", strings.TrimSpace(lang))
	}
	if tokenKind == "v3" {
		params.Set("api_key", token)
	}
	u.RawQuery = params.Encode()

	req, _ := http.NewRequest(http.MethodGet, u.String(), nil)
	req.Header.Set("Accept", "application/json")
	if tokenKind == "v4" {
		req.Header.Set("Authorization", "Bearer "+token)
	}
	client := &http.Client{Timeout: 10 * time.Second}
	resp, err := client.Do(req)
	if err != nil {
		return "", err
	}
	defer func() { _ = resp.Body.Close() }()
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return "", fmt.Errorf("tmdb http %d", resp.StatusCode)
	}

	var raw struct {
		ProfilePath string `json:"profile_path"`
	}
	if err := json.NewDecoder(resp.Body).Decode(&raw); err != nil {
		return "", err
	}
	p := strings.TrimSpace(raw.ProfilePath)
	if p != "" {
		RememberPersonProfile(personID, p)
	}
	return p, nil
}
