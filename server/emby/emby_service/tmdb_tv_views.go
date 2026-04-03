package emby_service

import (
	"sort"
	"strings"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
	metadata_tmdb "github.com/jenfonro/meowfilm/server/metadata/tmdb"
)

type TVSeasonListView struct {
	Series        *db.TMDBCachedDetail
	SeasonDetails map[int]*db.TMDBCachedSeasonDetail
}

type TVSeasonEpisodesView struct {
	Series *db.TMDBCachedDetail
	Season *db.TMDBCachedSeasonDetail
}

type nextUpEpisodeCandidate struct {
	Season     int
	SeasonName string
	Episode    db.TMDBCachedSeasonEpisode
	History    *db.PlayHistoryRow
}

type TVNextUpView struct {
	Series     *db.TMDBCachedDetail
	Candidates []nextUpEpisodeCandidate
}

type TVResumeEpisodeView struct {
	Series  *db.TMDBCachedDetail
	Season  *db.TMDBCachedSeasonDetail
	Episode *db.TMDBCachedSeasonEpisode
}

type tvSeriesProgressCursor struct {
	Season         int
	Episode        int
	IncludeUnaired bool
}

func loadTVSeriesDetailView(database *db.DB, tmdbID int) (*db.TMDBCachedDetail, error) {
	if database == nil || tmdbID <= 0 {
		return nil, nil
	}
	return metadata_tmdb.GetDetailForBackend(database, "tv", tmdbID)
}

func loadTVSeasonListView(database *db.DB, tmdbID int, includeUnaired bool) (*TVSeasonListView, error) {
	series, err := loadTVSeriesDetailView(database, tmdbID)
	if err != nil || series == nil {
		return nil, err
	}
	now := time.Now()
	out := &TVSeasonListView{
		Series:        series,
		SeasonDetails: map[int]*db.TMDBCachedSeasonDetail{},
	}
	for _, season := range series.Seasons {
		if season.SeasonNumber <= 0 {
			continue
		}
		detail, _ := metadata_tmdb.GetTVSeasonDetailForBackend(database, tmdbID, season.SeasonNumber)
		if detail == nil {
			out.SeasonDetails[season.SeasonNumber] = nil
			continue
		}
		detailCopy := *detail
		if includeUnaired {
			detailCopy.Episodes = detail.Episodes
		} else {
			detailCopy.Episodes = filterAiredSeasonEpisodes(detail.Episodes, now)
		}
		out.SeasonDetails[season.SeasonNumber] = &detailCopy
	}
	return out, nil
}

func loadTVSeasonEpisodesView(database *db.DB, tmdbID int, seasonNo int, includeUnaired bool) (*TVSeasonEpisodesView, error) {
	if seasonNo <= 0 {
		return nil, nil
	}
	series, err := loadTVSeriesDetailView(database, tmdbID)
	if err != nil || series == nil {
		return nil, err
	}
	season, err := metadata_tmdb.GetTVSeasonDetailForBackend(database, tmdbID, seasonNo)
	if err != nil || season == nil {
		return nil, err
	}
	seasonCopy := *season
	if includeUnaired {
		seasonCopy.Episodes = season.Episodes
	} else {
		seasonCopy.Episodes = filterAiredSeasonEpisodes(season.Episodes, time.Now())
	}
	return &TVSeasonEpisodesView{Series: series, Season: &seasonCopy}, nil
}

func loadTVNextUpView(database *db.DB, tmdbID int, hist *db.PlayHistoryRow, limit int) (*TVNextUpView, error) {
	series, err := loadTVSeriesDetailView(database, tmdbID)
	if err != nil || series == nil {
		return nil, err
	}
	candidates, err := collectTVNextUpEpisodes(database, series, hist, limit)
	if err != nil {
		return nil, err
	}
	return &TVNextUpView{Series: series, Candidates: candidates}, nil
}

func loadTVResumeEpisodeView(database *db.DB, tmdbID int, seasonNo int, episodeNo int) (*TVResumeEpisodeView, error) {
	if seasonNo <= 0 || episodeNo <= 0 {
		return nil, nil
	}
	view, err := loadTVSeasonEpisodesView(database, tmdbID, seasonNo, false)
	if err != nil || view == nil {
		return nil, err
	}
	out := &TVResumeEpisodeView{Series: view.Series, Season: view.Season}
	for _, ep := range view.Season.Episodes {
		if ep.EpisodeNumber != episodeNo {
			continue
		}
		epCopy := ep
		out.Episode = &epCopy
		break
	}
	return out, nil
}

func isAiredEpisode(ep db.TMDBCachedSeasonEpisode, now time.Time) bool {
	if ep.EpisodeNumber <= 0 {
		return false
	}
	if strings.TrimSpace(ep.AirDate) == "" {
		return false
	}
	return metadata_tmdb.IsAirDateAiredOrToday(ep.AirDate, now)
}

func filterAiredSeasonEpisodes(in []db.TMDBCachedSeasonEpisode, now time.Time) []db.TMDBCachedSeasonEpisode {
	if len(in) == 0 {
		return nil
	}
	out := make([]db.TMDBCachedSeasonEpisode, 0, len(in))
	for _, ep := range in {
		if !isAiredEpisode(ep, now) {
			continue
		}
		out = append(out, ep)
	}
	return out
}

func filterSeasonEpisodesForNextUp(in []db.TMDBCachedSeasonEpisode, includeUnaired bool, now time.Time) []db.TMDBCachedSeasonEpisode {
	if includeUnaired {
		out := make([]db.TMDBCachedSeasonEpisode, 0, len(in))
		for _, ep := range in {
			if ep.EpisodeNumber <= 0 {
				continue
			}
			out = append(out, ep)
		}
		return out
	}
	return filterAiredSeasonEpisodes(in, now)
}

func listedPositiveSeasonNumbers(series *db.TMDBCachedDetail) []int {
	if series == nil || len(series.Seasons) == 0 {
		return nil
	}
	seen := map[int]struct{}{}
	out := make([]int, 0, len(series.Seasons))
	for _, season := range series.Seasons {
		if season.SeasonNumber <= 0 {
			continue
		}
		if _, ok := seen[season.SeasonNumber]; ok {
			continue
		}
		seen[season.SeasonNumber] = struct{}{}
		out = append(out, season.SeasonNumber)
	}
	sort.Ints(out)
	return out
}

func findSeasonStartIndex(list []int, seasonNo int) int {
	if len(list) == 0 || seasonNo <= 0 {
		return -1
	}
	for i, season := range list {
		if season == seasonNo {
			return i
		}
	}
	return -1
}

func minInt(a int, b int) int {
	if a < b {
		return a
	}
	return b
}

func resolveTVSeriesProgressCursor(hist *db.PlayHistoryRow, tmdbID int) tvSeriesProgressCursor {
	cur := tvSeriesProgressCursor{}
	if hist == nil || tmdbID <= 0 {
		return cur
	}
	cur.IncludeUnaired = hist.PreOrder
	if ref := parseItemRef(hist.PlaybackItemID); ref != nil && ref.Source == "tmdb" && ref.MediaType == "tv" && ref.NumericID == tmdbID && ref.SubKind == "episode" {
		cur.Season = ref.Pan
		cur.Episode = ref.Episode
		return cur
	}
	cur.Season = hist.TMDBSeason
	cur.Episode = hist.TMDBEpisode
	return cur
}

func buildFollowingCandidatesFromSeason(
	seasonNo int,
	seasonName string,
	episodes []db.TMDBCachedSeasonEpisode,
	curSeason int,
	curEpisode int,
	limit int,
) []nextUpEpisodeCandidate {
	if len(episodes) == 0 {
		return nil
	}
	sort.Slice(episodes, func(i, j int) bool { return episodes[i].EpisodeNumber < episodes[j].EpisodeNumber })
	startEpisode := 1
	if seasonNo == curSeason {
		startEpisode = curEpisode + 1
	}
	if startEpisode <= 0 {
		startEpisode = 1
	}
	capSize := len(episodes)
	if limit > 0 {
		capSize = minInt(limit, len(episodes))
	}
	out := make([]nextUpEpisodeCandidate, 0, capSize)
	for _, ep := range episodes {
		if ep.EpisodeNumber < startEpisode {
			continue
		}
		out = append(out, nextUpEpisodeCandidate{
			Season:     seasonNo,
			SeasonName: strings.TrimSpace(seasonName),
			Episode:    ep,
			History:    nil,
		})
		if limit > 0 && len(out) >= limit {
			break
		}
	}
	return out
}

func collectTVSeriesFollowingEpisodes(database *db.DB, tmdbID int, cur tvSeriesProgressCursor, limit int) ([]nextUpEpisodeCandidate, error) {
	if database == nil || tmdbID <= 0 || cur.Season <= 0 || cur.Episode <= 0 {
		return []nextUpEpisodeCandidate{}, nil
	}
	series, err := loadTVSeriesDetailView(database, tmdbID)
	if err != nil || series == nil {
		return nil, err
	}
	seasonNumbers := listedPositiveSeasonNumbers(series)
	startIndex := findSeasonStartIndex(seasonNumbers, cur.Season)
	if startIndex < 0 {
		return []nextUpEpisodeCandidate{}, nil
	}
	out := make([]nextUpEpisodeCandidate, 0, maxInt(1, limit))
	now := time.Now()
	for i := startIndex; i < len(seasonNumbers); i++ {
		seasonNo := seasonNumbers[i]
		seasonDetail, err := metadata_tmdb.GetTVSeasonDetailForBackend(database, tmdbID, seasonNo)
		if err != nil {
			return nil, err
		}
		if seasonDetail == nil {
			continue
		}
		episodes := filterSeasonEpisodesForNextUp(seasonDetail.Episodes, cur.IncludeUnaired, now)
		remaining := 0
		if limit > 0 {
			remaining = limit - len(out)
			if remaining <= 0 {
				break
			}
		}
		candidates := buildFollowingCandidatesFromSeason(
			seasonNo,
			seasonDetail.Name,
			episodes,
			cur.Season,
			cur.Episode,
			remaining,
		)
		if len(candidates) == 0 {
			continue
		}
		out = append(out, candidates...)
		if limit > 0 && len(out) >= limit {
			break
		}
	}
	return out, nil
}

func countTVSeriesFollowingEpisodes(database *db.DB, tmdbID int, hist *db.PlayHistoryRow) (int, error) {
	cur := resolveTVSeriesProgressCursor(hist, tmdbID)
	if cur.Season <= 0 || cur.Episode <= 0 {
		return 0, nil
	}
	candidates, err := collectTVSeriesFollowingEpisodes(database, tmdbID, cur, 0)
	if err != nil {
		return 0, err
	}
	return len(candidates), nil
}

func nextUpStartEpisode(curSeason int, curEpisode int, resumeCurrent bool, season int) int {
	if season != curSeason {
		return 1
	}
	startEpisode := curEpisode
	if !resumeCurrent {
		startEpisode++
	}
	if startEpisode <= 0 {
		return 1
	}
	return startEpisode
}

func buildNextUpCandidatesFromSeason(
	seasonNo int,
	seasonName string,
	episodes []db.TMDBCachedSeasonEpisode,
	curSeason int,
	curEpisode int,
	resumeCurrent bool,
	hist *db.PlayHistoryRow,
	limit int,
) []nextUpEpisodeCandidate {
	if len(episodes) == 0 || limit <= 0 {
		return nil
	}
	sort.Slice(episodes, func(i, j int) bool { return episodes[i].EpisodeNumber < episodes[j].EpisodeNumber })
	startEpisode := nextUpStartEpisode(curSeason, curEpisode, resumeCurrent, seasonNo)
	out := make([]nextUpEpisodeCandidate, 0, minInt(limit, len(episodes)))
	for _, ep := range episodes {
		if ep.EpisodeNumber < startEpisode {
			continue
		}
		row := hist
		if row != nil && (seasonNo != curSeason || ep.EpisodeNumber != curEpisode) {
			row = nil
		}
		out = append(out, nextUpEpisodeCandidate{
			Season:     seasonNo,
			SeasonName: strings.TrimSpace(seasonName),
			Episode:    ep,
			History:    row,
		})
		if len(out) >= limit {
			break
		}
	}
	return out
}

func collectTVNextUpEpisodes(database *db.DB, series *db.TMDBCachedDetail, hist *db.PlayHistoryRow, limit int) ([]nextUpEpisodeCandidate, error) {
	type cursor struct {
		season  int
		episode int
		resume  bool
	}
	cur := cursor{}
	tmdbID := 0
	if series != nil {
		tmdbID = series.TMDBID
	}
	if hist != nil {
		if ref := parseItemRef(hist.PlaybackItemID); ref != nil && ref.Source == "tmdb" && ref.MediaType == "tv" && ref.NumericID == tmdbID && ref.SubKind == "episode" {
			cur.season = ref.Pan
			cur.episode = ref.Episode
		} else {
			cur.season = hist.TMDBSeason
			cur.episode = hist.TMDBEpisode
		}
		if hist.PlaybackPositionTicks > 0 {
			cur.resume = true
		}
	}
	if cur.season <= 0 {
		cur.season = 1
		cur.episode = 0
		cur.resume = false
	}
	if limit <= 0 {
		limit = 1
	}
	seasonNumbers := listedPositiveSeasonNumbers(series)
	startIndex := findSeasonStartIndex(seasonNumbers, cur.season)
	if startIndex < 0 {
		return []nextUpEpisodeCandidate{}, nil
	}
	includeUnaired := hist != nil && hist.PreOrder

	out := make([]nextUpEpisodeCandidate, 0, limit)
	now := time.Now()
	for i := startIndex; i < len(seasonNumbers) && len(out) < limit; i++ {
		seasonNo := seasonNumbers[i]
		seasonDetail, err := metadata_tmdb.GetTVSeasonDetailForBackend(database, tmdbID, seasonNo)
		if err != nil {
			return nil, err
		}
		if seasonDetail == nil {
			continue
		}
		episodes := filterSeasonEpisodesForNextUp(seasonDetail.Episodes, includeUnaired, now)
		candidates := buildNextUpCandidatesFromSeason(
			seasonNo,
			seasonDetail.Name,
			episodes,
			cur.season,
			cur.episode,
			cur.resume,
			hist,
			limit-len(out),
		)
		if len(candidates) == 0 {
			continue
		}
		out = append(out, candidates...)
	}
	return out, nil
}
