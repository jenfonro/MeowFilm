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
type PlaybackRequest = smartPlaybackRequest
type PlaybackPickedMeta = smartPlaybackPickedMeta
type User = SmartUser
type TMDBTVDetail = embyTMDBTVDetail
type TMDBMovieDetail = embyTMDBMovieDetail
type TMDBSearchItem = embyTMDBSearchItem
type TMDBCredits = embyTMDBCredits
type TMDBCast = embyTMDBCast
type TMDBCrew = embyTMDBCrew
type TMDBTVSeasonDetail = embyTMDBTVSeasonDetail
type DoubanHotItem = embyDoubanHotItem
type DoubanTMDBMap = smartDoubanTMDBMap

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

func PositiveSeasonCount(seasons []TMDBSeason) int {
	return smartPositiveSeasonCount(seasons)
}

func NormalizeMaybeGlobalSeasonEpisode(seasons []TMDBSeason, se SeasonEpisode) SeasonEpisode {
	return smartNormalizeMaybeGlobalSeasonEpisode(seasons, se)
}

func TMDBSeasonEpisodeOfGlobal(seasons []TMDBSeason, global int) SeasonEpisode {
	return smartTMDBSeasonEpisodeOfGlobal(seasons, global)
}

func ResolveExtractedSeasonEpisodeToGlobal(primarySeasons []TMDBSeason, baselineSeasons []TMDBSeason, se SeasonEpisode, allowSingleBaseline bool, primaryKind string, sourceHasBeyondFirstSeason bool) (match SeasonEpisode, global int, ok bool, loose bool, resolutionMode string, degradedReason string) {
	primaryFirstSeasonCount := 0
	for _, s := range primarySeasons {
		if s.Season == 1 && s.EpisodeCount > 0 {
			primaryFirstSeasonCount = s.EpisodeCount
			break
		}
	}
	return smartResolveEpisodeMappingForPlaybackWithMode(primarySeasons, se, baselineSeasons, primaryFirstSeasonCount, sourceHasBeyondFirstSeason, allowSingleBaseline, primaryKind)
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

func ResolvePlaybackFromTMDB(database *db.DB, u *SmartUser, req PlaybackRequest) (finalURL string, finalHeaders map[string]string, picked *PlaybackPickedMeta, err error) {
	return smartResolvePlaybackFromTMDB(database, u, req)
}

func ResolveCatApiBaseForUser(database *db.DB, u *SmartUser) string {
	return smartResolveCatApiBaseForUser(database, u)
}

func ResolveSpiderAPIBySiteKey(database *db.DB, siteKey string) string {
	return smartResolveSpiderAPIBySiteKey(database, siteKey)
}

func AnyToString(v any) string {
	return smartAnyToString(v)
}

func PanMock189AccessGet(shareID string) (string, bool) {
	return smartPanMock189AccessGet(shareID)
}

func PanMock189AccessPut(shareID string, accessCode string) {
	smartPanMock189AccessPut(shareID, accessCode)
}

func PanMockProviderFromLabel(label string) string {
	return smartPanMockProviderFromLabel(label)
}

func ResolvePanMockDetailPans(
	database *db.DB,
	siteKey string,
	siteName string,
	want int,
	tmdbSeasons []TMDBSeason,
	tmdbHasMultiSeason bool,
	rawCleanRules []string,
	rawEpisodeRules []string,
	pans []catpawrunner.Pan,
) ([]catpawrunner.Pan, map[string]string) {
	return smartResolvePanMockDetailPans(database, siteKey, siteName, want, tmdbSeasons, tmdbHasMultiSeason, rawCleanRules, rawEpisodeRules, pans)
}

func ResolvePanMockDetailPansIncremental(
	database *db.DB,
	siteKey string,
	siteName string,
	want int,
	tmdbSeasons []TMDBSeason,
	tmdbHasMultiSeason bool,
	rawCleanRules []string,
	rawEpisodeRules []string,
	pans []catpawrunner.Pan,
	onPanResolved func(panIndex int, episodes []catpawrunner.Episode, accessDelta map[string]string),
) ([]catpawrunner.Pan, map[string]string) {
	return smartResolvePanMockDetailPansIncremental(database, siteKey, siteName, want, tmdbSeasons, tmdbHasMultiSeason, rawCleanRules, rawEpisodeRules, pans, onPanResolved)
}

func LoadAggregateCleanRules(database *db.DB) []string {
	return embyLoadAggregateCleanRules(database)
}

func AggKeyWithRules(text string, rawRules []string) string {
	return embyAggKeyWithRules(text, rawRules)
}

func ScoreEpisodeDisplayName(name string, titleLower string) int {
	return embyScoreEpisodeDisplayName(name, titleLower)
}

func PickEpisodeDisplayName(displayName string, fileName string, titleLower string, preferFile bool) string {
	return embyPickEpisodeDisplayName(displayName, fileName, titleLower, preferFile)
}

func TMDBGetTVDetail(database *db.DB, tmdbID int) (*TMDBTVDetail, error) {
	return embyTMDBGetTVDetail(database, tmdbID)
}

func TMDBGetMovieDetail(database *db.DB, tmdbID int) (*TMDBMovieDetail, error) {
	return embyTMDBGetMovieDetail(database, tmdbID)
}

func TMDBSearchMulti(database *db.DB, query string) ([]TMDBSearchItem, error) {
	return embyTMDBSearchMulti(database, query)
}

func TMDBGetCredits(database *db.DB, mediaType string, tmdbID int) (*TMDBCredits, error) {
	return embyTMDBGetCredits(database, mediaType, tmdbID)
}

func TMDBGetPersonProfile(database *db.DB, personID int) (string, error) {
	return embyTMDBGetPersonProfile(database, personID)
}

func RememberPersonProfile(personID int, profilePath string) {
	embyRememberPersonProfile(personID, profilePath)
}

func LoadSiteOrder(database *db.DB, u *SmartUser) []string {
	return smartLoadSiteOrder(database, u)
}

func TMDBGetTVSeasonDetail(database *db.DB, tmdbID int, season int) (*embyTMDBTVSeasonDetail, error) {
	return embyTMDBGetTVSeasonDetail(database, tmdbID, season)
}

func TMDBGetTVSeasonDetailAtLeast(database *db.DB, tmdbID int, season int, minEpisodes int) (*embyTMDBTVSeasonDetail, error) {
	return embyTMDBGetTVSeasonDetailAtLeast(database, tmdbID, season, minEpisodes)
}

func TMDBGetTVSeasonEpisodes(database *db.DB, tmdbID int, season int) ([]TMDBSeasonEpisode, error) {
	return embyTMDBGetTVSeasonEpisodes(database, tmdbID, season)
}

func TMDBGetTVSeasonEpisodesAtLeast(database *db.DB, tmdbID int, season int, minEpisodes int) ([]TMDBSeasonEpisode, error) {
	return embyTMDBGetTVSeasonEpisodesAtLeast(database, tmdbID, season, minEpisodes)
}

func DoubanFetchRecentHot(database *db.DB, kind string, category string, hotType string, start int, limit int) ([]DoubanHotItem, error) {
	return embyDoubanFetchRecentHot(database, kind, category, hotType, start, limit)
}

func GetDoubanTMDBMap(database *db.DB, kind string, doubanID string) (*DoubanTMDBMap, error) {
	return smartGetDoubanTMDBMap(database, kind, doubanID)
}

func UpsertDoubanTMDBMap(database *db.DB, m DoubanTMDBMap) error {
	return smartUpsertDoubanTMDBMap(database, m)
}

func ResolveTMDBForDouban(database *db.DB, kind string, doubanID string, title string, year int) (int, error) {
	return smartResolveTMDBForDouban(database, kind, doubanID, title, year)
}

func DoubanProbeSeasons(database *db.DB, tmdbID int, keyword string, wantGlobal int) ([]TMDBSeason, bool) {
	return smartDoubanProbeSeasons(database, tmdbID, keyword, wantGlobal)
}

func DoubanAPIBase(database *db.DB) (base string, proxyBase string) {
	return smartDoubanAPIBase(database)
}

func DoubanToProxiedURL(targetURL string, proxyBase string) string {
	return smartDoubanToProxiedURL(targetURL, proxyBase)
}

func TMDBImageURL(database *db.DB, path string, size string) string {
	return embyTMDBImageURL(database, path, size)
}

func TMDBDiscover(database *db.DB, mediaType string, yearStart int, yearEnd int, sortBy string, page int) (items []TMDBSearchItem, total int, err error) {
	return embyTMDBDiscover(database, mediaType, yearStart, yearEnd, sortBy, page)
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
	primarySeasons []TMDBSeason,
	singleBaselineSeasons []TMDBSeason,
	tmdbHasMultiSeason bool,
	preferSeasonNo int,
	settings PlaybackSettings,
	rawCleanRules []string,
	rawEpisodeRules []string,
	allowSingleBaseline bool,
	primaryKind string,
) *Candidate {
	return smartResolvePanMockCandidateFromVod(base, vodPlayURL, want, primarySeasons, singleBaselineSeasons, tmdbHasMultiSeason, preferSeasonNo, settings, rawCleanRules, rawEpisodeRules, allowSingleBaseline, primaryKind)
}

func TryPanMockGroup(
	at PanMockGroupAttempt,
	base Candidate,
	want int,
	primarySeasons []TMDBSeason,
	singleBaselineSeasons []TMDBSeason,
	tmdbHasMultiSeason bool,
	preferSeasonNo int,
	settings PlaybackSettings,
	rawCleanRules []string,
	rawEpisodeRules []string,
	allowSingleBaseline bool,
	primaryKind string,
	database *db.DB,
	tvUser string,
	accessByShareID map[string]string,
) *PickResult {
	return smartTryPanMockGroup(at, base, want, primarySeasons, singleBaselineSeasons, tmdbHasMultiSeason, preferSeasonNo, settings, rawCleanRules, rawEpisodeRules, allowSingleBaseline, primaryKind, database, tvUser, accessByShareID)
}
