package smart

import (
	"errors"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/catpawrunner"
	metadata_tmdb "github.com/jenfonro/meowfilm/server/metadata/tmdb"
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
type PlaybackOffer = smartCandidateOffer
type User = SmartUser
type TMDBCredits = smartTMDBCredits
type TMDBCast = smartTMDBCast
type TMDBCrew = smartTMDBCrew

// Exported wrappers (matching previous playback helpers)
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

func ExtractTianyiMockMetaFromEpisodeURL(panFlag string, episodeURL string) (shareCode string, accessCode string) {
	return smartExtractTianyiMockMetaFromEpisodeURL(panFlag, episodeURL)
}

func BuildSourceKey(siteKey string, spiderAPI string, siteDetail string) string {
	return smartBuildSourceKey(siteKey, spiderAPI, siteDetail)
}

func ExtractSeasonHintFromSource(siteName string, remark string) int {
	return smartExtractSeasonHintFromSource(siteName, remark)
}

func HasExplicitSeasonMarkerInSource(siteName string, remark string) bool {
	return smartHasExplicitSeasonMarkerInSource(siteName, remark)
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

func CollectPlaybackOffersFromTMDB(database *db.DB, u *SmartUser, req PlaybackRequest, shouldStop func() bool, emit func(PlaybackOffer, int)) error {
	return smartCollectPlaybackOffersFromTMDB(database, u, req, shouldStop, emit)
}

func TryPlaybackOffers(database *db.DB, u *SmartUser, offers []PlaybackOffer) (finalURL string, finalHeaders map[string]string, picked *PlaybackPickedMeta, err error) {
	return smartTryPlaybackOffersInternal(database, u, offers)
}

func BuildCatpawPlayPayload(playRaw map[string]any, apiBase string, tvUser string) map[string]any {
	return smartBuildCatpawPlayPayload(playRaw, apiBase, tvUser)
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

func ResolvePanProviderPlayback(database *db.DB, u *SmartUser, provider string, panFlag string, episodeURL string, accessByShareID map[string]string, dirPath string) (finalURL string, finalHeaders map[string]string, err error) {
	return smartResolvePanProviderPlayback(database, u, provider, panFlag, episodeURL, accessByShareID, dirPath)
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

func ResolveSinglePanMockPan(database *db.DB, pan catpawrunner.Pan) (catpawrunner.Pan, map[string]string, bool, string, error) {
	return smartResolveSinglePanMockPan(database, pan)
}

func LoadAggregateCleanRules(database *db.DB) []string {
	return loadAggregateCleanRules(database)
}

func AggKeyWithRules(text string, rawRules []string) string {
	return aggregateKeyWithRules(text, rawRules)
}

func ScoreEpisodeDisplayName(name string, titleLower string) int {
	return scoreEpisodeDisplayName(name, titleLower)
}

func PickEpisodeDisplayName(displayName string, fileName string, titleLower string, preferFile bool) string {
	return pickEpisodeDisplayName(displayName, fileName, titleLower, preferFile)
}

func TMDBGetCredits(database *db.DB, mediaType string, tmdbID int) (*TMDBCredits, error) {
	credits, err := metadata_tmdb.GetCredits(database, mediaType, tmdbID)
	if err != nil {
		return nil, err
	}
	if credits == nil {
		return nil, nil
	}
	out := &TMDBCredits{MediaType: credits.MediaType, ID: credits.ID}
	if len(credits.Cast) > 0 {
		out.Cast = make([]TMDBCast, 0, len(credits.Cast))
		for _, c := range credits.Cast {
			out.Cast = append(out.Cast, TMDBCast{
				ID:      c.ID,
				Name:    c.Name,
				Role:    c.Role,
				Profile: c.Profile,
				Order:   c.Order,
			})
		}
	}
	if len(credits.Crew) > 0 {
		out.Crew = make([]TMDBCrew, 0, len(credits.Crew))
		for _, c := range credits.Crew {
			out.Crew = append(out.Crew, TMDBCrew{
				ID:      c.ID,
				Name:    c.Name,
				Job:     c.Job,
				Dept:    c.Dept,
				Profile: c.Profile,
			})
		}
	}
	return out, nil
}

func TMDBGetPersonProfile(database *db.DB, personID int) (string, error) {
	return metadata_tmdb.GetPersonProfile(database, personID)
}

func RememberPersonProfile(personID int, profilePath string) {
	metadata_tmdb.RememberPersonProfile(personID, profilePath)
}

func LoadSiteOrder(database *db.DB, u *SmartUser) []string {
	return smartLoadSiteOrder(database, u)
}

func ResolveTMDBByTitleCached(database *db.DB, kind string, title string, year int) (int, error) {
	k := strings.TrimSpace(kind)
	q := strings.TrimSpace(title)
	if k == "" || q == "" {
		return 0, errors.New("invalid args")
	}
	cands := smartNormalizeTitleForTMDBCandidates(k, q)
	if len(cands) == 0 {
		return 0, nil
	}
	tid, _, err := metadata_tmdb.ResolveByTitlesCached(database, k, cands, year, "zh-CN")
	return tid, err
}

func TMDBImageURL(database *db.DB, path string, size string) string {
	return tmdbImageURL(database, path, size)
}

func TMDBDiscover(database *db.DB, mediaType string, yearStart int, yearEnd int, sortBy string, page int) (items []metadata_tmdb.DiscoverItem, total int, err error) {
	return metadata_tmdb.Discover(database, mediaType, yearStart, yearEnd, sortBy, page)
}

func PlayFlagProviderID(flagLabel string) string {
	return smartPlayFlagProviderID(flagLabel)
}

func PanMockProviderID(database *db.DB, panFlag string) string {
	return smartPanMockProviderID(database, panFlag)
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
