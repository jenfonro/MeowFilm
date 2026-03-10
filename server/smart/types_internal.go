package smart

import "github.com/jenfonro/meowfilm/server/catpawrunner"

// Internal types (kept unexported) to minimize code churn; exported aliases live in export.go.
type smartPriorityMatch struct {
	Count   int
	Indices []int
}

type smartSeasonEpisode struct {
	Season  int
	Episode int
}

type smartPlaybackSettings struct {
	Mode               string   // "无" | "网盘" | "关键字"
	KeywordTokensLower []string // smart_source_priority_tokens
	PanTokenOrderLower []string // smart_pan_match_tokens
	OrderKeys          []string // order preference keys
	ExplicitKeys       []string // explicit big conditions
}

type smartCandidate struct {
	SiteKey          string
	SiteName         string
	SpiderAPI        string
	VideoID          string
	SrcRemarkLower   string
	PanLabel         string
	PanTokenIdx      int
	Ep               catpawrunner.Episode
	RawLower         string
	MatchSeason      int
	HasSeasonMarker  bool
	SearchSeasonHint int
	MatchKeyword     smartPriorityMatch
}

type smartCandidateFeatures struct {
	HayLower     string
	Quality      string
	QualityRank  int
	Fps60        bool
	HasHdr       bool
	TierRank     int
	EnhanceMatch smartPriorityMatch
}

type smartPickResult struct {
	Cand    smartCandidate
	PlayURL string
	Headers map[string]string
}

// User context for smart playback (consumer-specific).
type SmartUser struct {
	ID       string
	Username string
	Role     string
	Status   string
}

// TMDB season model shared by smart matching.
type TMDBSeason struct {
	Season       int
	EpisodeCount int
	Poster       string
}

// Keep legacy name inside smart package so existing code can stay untouched.
type embyTMDBSeason = TMDBSeason
