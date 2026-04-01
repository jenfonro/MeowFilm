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

type smartPlaybackRuleKey string

const (
	smartPlaybackRuleQuality smartPlaybackRuleKey = "quality"
	smartPlaybackRulePan     smartPlaybackRuleKey = "pan"
	smartPlaybackRuleKeyword smartPlaybackRuleKey = "keyword"
)

type smartCandidateStage string

const (
	smartCandidateStageHistoryList   smartCandidateStage = "history_list"
	smartCandidateStageHistoryDetail smartCandidateStage = "history_detail"
	smartCandidateStageFull          smartCandidateStage = "full"
)

type smartPlaybackSettings struct {
	KeywordTokensLower []string // smart_source_priority_tokens
	PanTokenOrderLower []string // smart_pan_match_tokens ordered by provider preference
	PanMatchEntries    []smartPanMatchEntry
	Rules              []smartPlaybackRuleKey
	OrderedRules       []smartPlaybackRuleKey
}

type smartCandidate struct {
	Stage            smartCandidateStage
	SiteKey          string
	SiteName         string
	SpiderAPI        string
	SiteDetail       string
	SrcRemarkLower   string
	PanFlag          string
	PanTokenIdx      int
	Ep               catpawrunner.Episode
	RawName          string
	RawLower         string
	MatchSeason      int
	HasSeasonMarker  bool
	SearchSeasonHint int
	MatchKeyword     smartPriorityMatch
	ResolutionMode   string
	LockedGlobal     int
	DegradedReason   string
	StrictMatched    bool
	DegradedMatched  bool
}

type smartCandidateScore struct {
	Stage        smartCandidateStage
	StageScore   int
	QualityScore int
	PanScore     int
	KeywordScore int
}

type smartCandidateFeatures struct {
	HayLower     string
	Quality      string
	QualityRank  int
	Fps60        bool
	HasHdr       bool
	HasDDP       bool
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

type smartTMDBSeason = TMDBSeason
