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
	candidates, err := collectTVNextUpEpisodes(database, tmdbID, hist, limit)
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

func collectTVNextUpEpisodes(database *db.DB, tmdbID int, hist *db.PlayHistoryRow, limit int) ([]nextUpEpisodeCandidate, error) {
	type cursor struct {
		season  int
		episode int
		resume  bool
	}
	cur := cursor{}
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

	out := make([]nextUpEpisodeCandidate, 0, limit)
	startSeason := maxInt(cur.season, 1)
	now := time.Now()
	for season := startSeason; len(out) < limit; season++ {
		seasonDetail, err := metadata_tmdb.GetTVSeasonDetailForBackend(database, tmdbID, season)
		if err != nil {
			return nil, err
		}
		if seasonDetail == nil {
			if season > startSeason+20 {
				break
			}
			continue
		}
		episodes := filterAiredSeasonEpisodes(seasonDetail.Episodes, now)
		if len(episodes) == 0 {
			if season > startSeason+20 {
				break
			}
			continue
		}
		sort.Slice(episodes, func(i, j int) bool { return episodes[i].EpisodeNumber < episodes[j].EpisodeNumber })
		startEpisode := 1
		if season == cur.season {
			startEpisode = cur.episode
			if !cur.resume {
				startEpisode++
			}
		}
		for _, ep := range episodes {
			if ep.EpisodeNumber <= 0 || ep.EpisodeNumber < startEpisode {
				continue
			}
			row := hist
			if row != nil && (season != cur.season || ep.EpisodeNumber != cur.episode) {
				row = nil
			}
			out = append(out, nextUpEpisodeCandidate{
				Season:     season,
				SeasonName: strings.TrimSpace(seasonDetail.Name),
				Episode:    ep,
				History:    row,
			})
			if len(out) >= limit {
				break
			}
		}
		cur.resume = false
		cur.episode = 0
		if season > startSeason+20 {
			break
		}
	}
	return out, nil
}
