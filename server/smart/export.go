package smart

import (
	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/catpawrunner"
)

// Exported aliases
type PriorityMatch = smartPriorityMatch
type SeasonEpisode = smartSeasonEpisode
type PlaybackSettings = smartPlaybackSettings
type Candidate = smartCandidate
type CandidateFeatures = smartCandidateFeatures
type PickResult = smartPickResult
type PanMockGroupAttempt = smartPanMockGroupAttempt

// Exported wrappers (matching previous emby helpers)
func ComputePriorityMatch(textLower string, tokensLower []string) PriorityMatch {
	return smartComputePriorityMatch(textLower, tokensLower)
}

func ComparePriorityMatch(a PriorityMatch, b PriorityMatch) int {
	return smartComparePriorityMatch(a, b)
}

func ParseChineseNumeralToInt(text string) int {
	return smartParseChineseNumeralToInt(text)
}

func ExtractRawNamesFromEpisodeURL(episodeURL string) []string {
	return smartExtractRawNamesFromEpisodeURL(episodeURL)
}

func BuildCandidateLowerText(texts []string) string {
	return smartBuildCandidateLowerText(texts)
}

func SplitDisplayPathSegments(display string) []string {
	return smartSplitDisplayPathSegments(display)
}

func EpisodePathLayers(ep catpawrunner.Episode) (fileName string, currentDir string, parentDir string) {
	return smartEpisodePathLayers(ep)
}

func ExtractSeasonMarkerText(text string) string {
	return smartExtractSeasonMarkerText(text)
}

func GuessQualityByLayers(fileName string, currentDir string, parentDir string) (quality string, currentDirIs4K bool) {
	return smartGuessQualityByLayers(fileName, currentDir, parentDir)
}

func ExtractEpisodeCandidateTexts(ep catpawrunner.Episode) []string {
	return smartExtractEpisodeCandidateTexts(ep)
}

func GuessQuality(hayRaw string) string {
	return smartGuessQuality(hayRaw)
}

func GuessFps60(hayRaw string) bool {
	return smartGuessFps60(hayRaw)
}

func QualityRankOf(q string) int {
	return smartQualityRankOf(q)
}

func BuildHayLower(c Candidate) string {
	return smartBuildHayLower(c)
}

func ComputeCandidateFeatures(c Candidate) CandidateFeatures {
	return smartComputeCandidateFeatures(c)
}

func ComputeBigHitCount(c Candidate, feat CandidateFeatures, explicit []string) int {
	return smartComputeBigHitCount(c, feat, explicit)
}

func CompareSmartMatchIgnorePanOrder(a Candidate, b Candidate, tmdbHasMultiSeason bool, preferSeasonNo int, settings PlaybackSettings) int {
	return smartCompareSmartMatchIgnorePanOrder(a, b, tmdbHasMultiSeason, preferSeasonNo, settings)
}

func ComparePanTokenIdx(a int, b int) int {
	return smartComparePanTokenIdx(a, b)
}

func CompareSmartMatch(a Candidate, b Candidate, tmdbHasMultiSeason bool, preferSeasonNo int, settings PlaybackSettings) int {
	return smartCompareSmartMatch(a, b, tmdbHasMultiSeason, preferSeasonNo, settings)
}

func ExtractMaxEpisodeFromBadgeText(text string) int {
	return smartExtractMaxEpisodeFromBadgeText(text)
}

func TMDBGlobalEpisodeNoOf(seasons []TMDBSeason, season int, episode int) int {
	return smartTMDBGlobalEpisodeNoOf(seasons, season, episode)
}

func NormalizeMaybeGlobalSeasonEpisode(seasons []TMDBSeason, se SeasonEpisode) SeasonEpisode {
	return smartNormalizeMaybeGlobalSeasonEpisode(seasons, se)
}

func TMDBSeasonEpisodeOfGlobal(seasons []TMDBSeason, global int) SeasonEpisode {
	return smartTMDBSeasonEpisodeOfGlobal(seasons, global)
}

func ParseVodPlayURLToEpisodes(vodPlayURL string) []catpawrunner.Episode {
	return smartParseVodPlayURLToEpisodes(vodPlayURL)
}

func ExtractMockPasscodeFromCandidate(c Candidate) string {
	return smartExtractMockPasscodeFromCandidate(c)
}

func ExtractTianyiMockMetaFromCandidate(c Candidate) (shareCode string, accessCode string) {
	return smartExtractTianyiMockMetaFromCandidate(c)
}

func ExtractMockPasscodeFromEpisodeURL(episodeURL string) string {
	return smartExtractMockPasscodeFromEpisodeURL(episodeURL)
}

func ExtractTianyiMockMetaFromEpisodeURL(panLabel string, episodeURL string) (shareCode string, accessCode string) {
	return smartExtractTianyiMockMetaFromEpisodeURL(panLabel, episodeURL)
}

func BuildSourceKey(siteKey string, spiderAPI string, videoID string) string {
	return smartBuildSourceKey(siteKey, spiderAPI, videoID)
}

func ExtractSeasonHintFromSource(siteName string, videoRemark string) int {
	return smartExtractSeasonHintFromSource(siteName, videoRemark)
}

func HasExplicitSeasonMarkerInSource(siteName string, videoRemark string) bool {
	return smartHasExplicitSeasonMarkerInSource(siteName, videoRemark)
}

func IsPanMockEnabled(detailRaw map[string]any) bool {
	return smartIsPanMockEnabled(detailRaw)
}

func LabelTokenIdx(label string, panTokenOrderLower []string) int {
	return smartLabelTokenIdx(label, panTokenOrderLower)
}

func FirstRawNameFromURL(u string) string {
	return smartFirstRawNameFromURL(u)
}

func MaxInt(a, b int) int {
	return smartMaxInt(a, b)
}

func ShortURLForLog(raw string) string {
	return smartShortURLForLog(raw)
}

func NormalizeTitleForTMDB(kind string, title string) string {
	return smartNormalizeTitleForTMDB(kind, title)
}

func NormalizeTitleForTMDBCandidates(kind string, title string) []string {
	return smartNormalizeTitleForTMDBCandidates(kind, title)
}

func ToASCIIDigits(s string) string {
	return smartToASCIIDigits(s)
}

func NormalizeAggKey(s string) string {
	return smartNormalizeAggKey(s)
}

func MatchScore(qKey string, candKey string) int {
	return smartMatchScore(qKey, candKey)
}

func TitleLenForSort(title string) int {
	return smartTitleLenForSort(title)
}

func ComputeMatchScore(query string, title string) int {
	return smartComputeMatchScore(query, title)
}

func SeasonSuffixRegexPatterns() []string {
	return smartSeasonSuffixRegexPatterns()
}

func PanMatchLabelText(label string) string {
	return smartPanMatchLabelText(label)
}

func PanToProviderID(panLower string) string {
	return smartPanToProviderID(panLower)
}

func PlayFlagProviderID(flagLabel string) string {
	return smartPlayFlagProviderID(flagLabel)
}

func PanMockProviderID(database *db.DB, panLabel string) string {
	return smartPanMockProviderID(database, panLabel)
}

func ContainsInt(list []int, v int) bool {
	return containsInt(list, v)
}

func IntFromDigits(s string) int {
	return intFromDigits(s)
}

func MinInt(a, b int) int {
	return smartMinInt(a, b)
}

func BuildPanMockGroupAttempts(
	candidatesForNo []Candidate,
	settings PlaybackSettings,
	database *db.DB,
	tmdbHasMultiSeason bool,
	preferSeasonNo int,
) (allowedAttempts []PanMockGroupAttempt, fallbackAttempts []PanMockGroupAttempt, normalAllowed []Candidate, normalFallback []Candidate) {
	return smartBuildPanMockGroupAttempts(candidatesForNo, settings, database, tmdbHasMultiSeason, preferSeasonNo)
}

func ResolvePanMockCandidateFromVod(
	base Candidate,
	vodPlayURL string,
	want int,
	tmdbSeasons []TMDBSeason,
	tmdbHasMultiSeason bool,
	preferSeasonNo int,
	settings PlaybackSettings,
	rawCleanRules []string,
	rawEpisodeRules []string,
) *Candidate {
	return smartResolvePanMockCandidateFromVod(base, vodPlayURL, want, tmdbSeasons, tmdbHasMultiSeason, preferSeasonNo, settings, rawCleanRules, rawEpisodeRules)
}

func TryPanMockGroup(
	at PanMockGroupAttempt,
	base Candidate,
	want int,
	tmdbSeasons []TMDBSeason,
	tmdbHasMultiSeason bool,
	preferSeasonNo int,
	settings PlaybackSettings,
	rawCleanRules []string,
	rawEpisodeRules []string,
	database *db.DB,
	tvUser string,
	accessByShareID map[string]string,
) *PickResult {
	return smartTryPanMockGroup(at, base, want, tmdbSeasons, tmdbHasMultiSeason, preferSeasonNo, settings, rawCleanRules, rawEpisodeRules, database, tvUser, accessByShareID)
}
