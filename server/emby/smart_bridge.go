package emby

import (
	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/catpawrunner"
	"github.com/jenfonro/meowfilm/server/smart"
)

type smartPriorityMatch = smart.PriorityMatch
type smartSeasonEpisode = smart.SeasonEpisode
type smartPlaybackSettings = smart.PlaybackSettings
type smartCandidate = smart.Candidate
type smartCandidateFeatures = smart.CandidateFeatures
type smartPickResult = smart.PickResult
type smartPanMockGroupAttempt = smart.PanMockGroupAttempt

// Alias TMDB season model to shared smart type.
type embyTMDBSeason = smart.TMDBSeason

func smartComputePriorityMatch(textLower string, tokensLower []string) smartPriorityMatch {
	return smart.ComputePriorityMatch(textLower, tokensLower)
}

func smartComparePriorityMatch(a smartPriorityMatch, b smartPriorityMatch) int {
	return smart.ComparePriorityMatch(a, b)
}

func smartParseChineseNumeralToInt(text string) int {
	return smart.ParseChineseNumeralToInt(text)
}

func smartExtractRawNamesFromEpisodeURL(episodeURL string) []string {
	return smart.ExtractRawNamesFromEpisodeURL(episodeURL)
}

func smartBuildCandidateLowerText(texts []string) string {
	return smart.BuildCandidateLowerText(texts)
}

func smartSplitDisplayPathSegments(display string) []string {
	return smart.SplitDisplayPathSegments(display)
}

func smartEpisodePathLayers(ep catpawrunner.Episode) (fileName string, currentDir string, parentDir string) {
	return smart.EpisodePathLayers(ep)
}

func smartExtractSeasonMarkerText(text string) string {
	return smart.ExtractSeasonMarkerText(text)
}

func smartGuessQualityByLayers(fileName string, currentDir string, parentDir string) (quality string, currentDirIs4K bool) {
	return smart.GuessQualityByLayers(fileName, currentDir, parentDir)
}

func smartExtractEpisodeCandidateTexts(ep catpawrunner.Episode) []string {
	return smart.ExtractEpisodeCandidateTexts(ep)
}

func smartGuessQuality(hayRaw string) string {
	return smart.GuessQuality(hayRaw)
}

func smartGuessFps60(hayRaw string) bool {
	return smart.GuessFps60(hayRaw)
}

func smartQualityRankOf(q string) int {
	return smart.QualityRankOf(q)
}

func smartBuildHayLower(c smartCandidate) string {
	return smart.BuildHayLower(c)
}

func smartComputeCandidateFeatures(c smartCandidate) smartCandidateFeatures {
	return smart.ComputeCandidateFeatures(c)
}

func smartComputeBigHitCount(c smartCandidate, feat smartCandidateFeatures, explicit []string) int {
	return smart.ComputeBigHitCount(c, feat, explicit)
}

func smartCompareSmartMatchIgnorePanOrder(a smartCandidate, b smartCandidate, tmdbHasMultiSeason bool, preferSeasonNo int, settings smartPlaybackSettings) int {
	return smart.CompareSmartMatchIgnorePanOrder(a, b, tmdbHasMultiSeason, preferSeasonNo, settings)
}

func smartComparePanTokenIdx(a int, b int) int {
	return smart.ComparePanTokenIdx(a, b)
}

func smartCompareSmartMatch(a smartCandidate, b smartCandidate, tmdbHasMultiSeason bool, preferSeasonNo int, settings smartPlaybackSettings) int {
	return smart.CompareSmartMatch(a, b, tmdbHasMultiSeason, preferSeasonNo, settings)
}

func smartExtractMaxEpisodeFromBadgeText(text string) int {
	return smart.ExtractMaxEpisodeFromBadgeText(text)
}

func smartTMDBGlobalEpisodeNoOf(seasons []embyTMDBSeason, season int, episode int) int {
	return smart.TMDBGlobalEpisodeNoOf(seasons, season, episode)
}

func smartNormalizeMaybeGlobalSeasonEpisode(seasons []embyTMDBSeason, se smartSeasonEpisode) smartSeasonEpisode {
	return smart.NormalizeMaybeGlobalSeasonEpisode(seasons, se)
}

func smartTMDBSeasonEpisodeOfGlobal(seasons []embyTMDBSeason, global int) smartSeasonEpisode {
	return smart.TMDBSeasonEpisodeOfGlobal(seasons, global)
}

func smartParseVodPlayURLToEpisodes(vodPlayURL string) []catpawrunner.Episode {
	return smart.ParseVodPlayURLToEpisodes(vodPlayURL)
}

func smartExtractMockPasscodeFromCandidate(c smartCandidate) string {
	return smart.ExtractMockPasscodeFromCandidate(c)
}

func smartExtractTianyiMockMetaFromCandidate(c smartCandidate) (shareCode string, accessCode string) {
	return smart.ExtractTianyiMockMetaFromCandidate(c)
}

func smartExtractMockPasscodeFromEpisodeURL(episodeURL string) string {
	return smart.ExtractMockPasscodeFromEpisodeURL(episodeURL)
}

func smartExtractTianyiMockMetaFromEpisodeURL(panLabel string, episodeURL string) (shareCode string, accessCode string) {
	return smart.ExtractTianyiMockMetaFromEpisodeURL(panLabel, episodeURL)
}

func smartBuildSourceKey(siteKey string, spiderAPI string, videoID string) string {
	return smart.BuildSourceKey(siteKey, spiderAPI, videoID)
}

func smartExtractSeasonHintFromSource(siteName string, videoRemark string) int {
	return smart.ExtractSeasonHintFromSource(siteName, videoRemark)
}

func smartHasExplicitSeasonMarkerInSource(siteName string, videoRemark string) bool {
	return smart.HasExplicitSeasonMarkerInSource(siteName, videoRemark)
}

func smartIsPanMockEnabled(detailRaw map[string]any) bool {
	return smart.IsPanMockEnabled(detailRaw)
}

func smartLabelTokenIdx(label string, panTokenOrderLower []string) int {
	return smart.LabelTokenIdx(label, panTokenOrderLower)
}

func smartFirstRawNameFromURL(u string) string {
	return smart.FirstRawNameFromURL(u)
}

func smartMaxInt(a, b int) int {
	return smart.MaxInt(a, b)
}

func smartShortURLForLog(raw string) string {
	return smart.ShortURLForLog(raw)
}

func smartNormalizeTitleForTMDB(kind string, title string) string {
	return smart.NormalizeTitleForTMDB(kind, title)
}

func smartNormalizeTitleForTMDBCandidates(kind string, title string) []string {
	return smart.NormalizeTitleForTMDBCandidates(kind, title)
}

func smartToASCIIDigits(s string) string {
	return smart.ToASCIIDigits(s)
}

func smartNormalizeAggKey(s string) string {
	return smart.NormalizeAggKey(s)
}

func smartMatchScore(qKey string, candKey string) int {
	return smart.MatchScore(qKey, candKey)
}

func smartTitleLenForSort(title string) int {
	return smart.TitleLenForSort(title)
}

func smartComputeMatchScore(query string, title string) int {
	return smart.ComputeMatchScore(query, title)
}

func smartSeasonSuffixRegexPatterns() []string {
	return smart.SeasonSuffixRegexPatterns()
}

func smartPanMatchLabelText(label string) string {
	return smart.PanMatchLabelText(label)
}

func smartPanToProviderID(panLower string) string {
	return smart.PanToProviderID(panLower)
}

func smartPlayFlagProviderID(flagLabel string) string {
	return smart.PlayFlagProviderID(flagLabel)
}

func smartPanMockProviderID(database *db.DB, panLabel string) string {
	return smart.PanMockProviderID(database, panLabel)
}

func smartBuildPanMockGroupAttempts(
	candidatesForNo []smartCandidate,
	settings smartPlaybackSettings,
	database *db.DB,
	tmdbHasMultiSeason bool,
	preferSeasonNo int,
) (allowedAttempts []smartPanMockGroupAttempt, fallbackAttempts []smartPanMockGroupAttempt, normalAllowed []smartCandidate, normalFallback []smartCandidate) {
	return smart.BuildPanMockGroupAttempts(candidatesForNo, settings, database, tmdbHasMultiSeason, preferSeasonNo)
}

func smartResolvePanMockCandidateFromVod(
	base smartCandidate,
	vodPlayURL string,
	want int,
	tmdbSeasons []embyTMDBSeason,
	tmdbHasMultiSeason bool,
	preferSeasonNo int,
	settings smartPlaybackSettings,
	rawCleanRules []string,
	rawEpisodeRules []string,
) *smartCandidate {
	return smart.ResolvePanMockCandidateFromVod(base, vodPlayURL, want, tmdbSeasons, tmdbHasMultiSeason, preferSeasonNo, settings, rawCleanRules, rawEpisodeRules)
}

func smartTryPanMockGroup(
	at smartPanMockGroupAttempt,
	base smartCandidate,
	want int,
	tmdbSeasons []embyTMDBSeason,
	tmdbHasMultiSeason bool,
	preferSeasonNo int,
	settings smartPlaybackSettings,
	rawCleanRules []string,
	rawEpisodeRules []string,
	database *db.DB,
	tvUser string,
	accessByShareID map[string]string,
) *smartPickResult {
	return smart.TryPanMockGroup(at, base, want, tmdbSeasons, tmdbHasMultiSeason, preferSeasonNo, settings, rawCleanRules, rawEpisodeRules, database, tvUser, accessByShareID)
}

func containsInt(list []int, v int) bool {
	return smart.ContainsInt(list, v)
}

func intFromDigits(s string) int {
	return smart.IntFromDigits(s)
}

func smartMinInt(a, b int) int {
	return smart.MinInt(a, b)
}
