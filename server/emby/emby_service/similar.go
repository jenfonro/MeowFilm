package emby_service

import (
	"sort"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
)

type SimilarResponseDTO struct {
	Items            []SimilarSeriesItemDTO `json:"Items"`
	TotalRecordCount int                    `json:"TotalRecordCount"`
}

type similarSeriesCandidate struct {
	Detail   db.TMDBCachedDetail
	Row      *db.PlayHistoryRow
	Score    int
	YearDiff int
}

func EmptySimilarResponse() SimilarResponseDTO {
	return SimilarResponseDTO{
		Items:            []SimilarSeriesItemDTO{},
		TotalRecordCount: 0,
	}
}

func BuildSimilarPayload(database *db.DB, userID int64, serverID string, itemID string, limit int) (SimilarResponseDTO, bool, error) {
	ref := parseItemRefAny(strings.TrimSpace(itemID))
	if ref == nil {
		return EmptySimilarResponse(), false, nil
	}
	if database == nil {
		return EmptySimilarResponse(), false, nil
	}
	if limit <= 0 {
		limit = 15
	}
	if limit > 15 {
		limit = 15
	}
	if ref.Source == "site" && ref.SubKind == "series" && isYouTubeSiteKey(ref.SiteKey) {
		items, err := buildYouTubeSiteSimilarItems(database, userID, serverID, ref, limit)
		if err != nil {
			return EmptySimilarResponse(), false, err
		}
		return SimilarResponseDTO{Items: items, TotalRecordCount: len(items)}, true, nil
	}
	if ref.Source != "tmdb" || (ref.SubKind != "movie" && ref.SubKind != "series") {
		return EmptySimilarResponse(), false, nil
	}

	targetType := "movie"
	if ref.SubKind == "series" {
		targetType = "tv"
	}
	target, err := database.ReadTMDBCachedDetail(targetType, ref.NumericID, "zh-CN")
	if err != nil {
		return EmptySimilarResponse(), false, err
	}
	if target == nil {
		return EmptySimilarResponse(), false, nil
	}

	candidates, err := buildSimilarSeriesCandidates(database, userID, ref.NumericID, target, limit)
	if err != nil {
		return EmptySimilarResponse(), false, err
	}
	items := renderSimilarSeriesItems(database, serverID, candidates)
	return SimilarResponseDTO{
		Items:            items,
		TotalRecordCount: len(items),
	}, true, nil
}

func buildYouTubeSiteSimilarItems(database *db.DB, userID int64, serverID string, ref *itemRef, limit int) ([]SimilarSeriesItemDTO, error) {
	if database == nil || ref == nil || !isYouTubeSiteKey(ref.SiteKey) || strings.TrimSpace(ref.SiteDetail) == "" {
		return []SimilarSeriesItemDTO{}, nil
	}
	pans, err := fetchResolvedSiteDetailPans(database, userID, ref.SiteKey, ref.SiteDetail)
	if err != nil {
		return nil, err
	}
	rec := resolvedSitePan{}
	recFound := false
	for _, pan := range pans {
		if strings.EqualFold(strings.TrimSpace(pan.RawLabel), "youtube-recommend") {
			rec = pan
			recFound = true
			break
		}
	}
	if !recFound || len(rec.Episodes) == 0 {
		return []SimilarSeriesItemDTO{}, nil
	}
	items := make([]SimilarSeriesItemDTO, 0, minInt(limit, len(rec.Episodes)))
	for idx, ep := range rec.Episodes {
		if limit > 0 && len(items) >= limit {
			break
		}
		detailID := extractYouTubeDetailIDFromEpisodeURL(strings.TrimSpace(ep.URL))
		if strings.TrimSpace(detailID) == "" {
			continue
		}
		itemID := buildSiteSeriesID(ref.SiteKey, detailID)
		if strings.TrimSpace(itemID) == "" {
			continue
		}
		name := siteEpisodeDisplayName(ep, rec.RawLabel, rec.PanMock, "", idx+1)
		if strings.TrimSpace(name) == "" {
			name = strings.TrimSpace(ep.Name)
		}
		if strings.TrimSpace(name) == "" {
			name = strings.TrimSpace(detailID)
		}
		pic, _, _ := parseYouTubeEpisodeMeta(strings.TrimSpace(ep.URL))
		primaryTag := PrimaryTagForItem(itemID)
		if tag := SearchSitePrimaryTag(strings.TrimSpace(pic)); strings.TrimSpace(tag) != "" {
			primaryTag = strings.TrimSpace(tag)
		}
		items = append(items, SimilarSeriesItemDTO{
			Name:                    strings.TrimSpace(name),
			ServerID:                strings.TrimSpace(serverID),
			ID:                      itemID,
			DateCreated:             EmbyZeroTimeString(),
			SupportsSync:            true,
			SortName:                SortNameOrName(name),
			PremiereDate:            "",
			RunTimeTicks:            0,
			ProductionYear:          0,
			ProviderIDs:             EmptyAnyMap(),
			IsFolder:                true,
			Type:                    "Series",
			UserData:                EmptyTVLatestUserData(),
			AirDays:                 EmptyStrings(),
			PrimaryImageAspectRatio: 0.6666667,
			ImageTags:               SimilarImageTagsDTO{Primary: primaryTag},
			BackdropImageTags:       EmptyStrings(),
		})
	}
	return items, nil
}

func extractYouTubeDetailIDFromEpisodeURL(raw string) string {
	s := strings.TrimSpace(raw)
	if s == "" {
		return ""
	}
	parts := strings.Split(s, "*")
	if len(parts) == 0 {
		return ""
	}
	last := strings.TrimSpace(parts[len(parts)-1])
	last = strings.ReplaceAll(last, "%2A", "*")
	last = strings.TrimSpace(last)
	if strings.HasPrefix(last, "watch?") {
		return last
	}
	return ""
}

func buildSimilarSeriesCandidates(database *db.DB, userID int64, excludeTMDBID int, target *db.TMDBCachedDetail, limit int) ([]similarSeriesCandidate, error) {
	pool, err := database.ListTMDBCachedDetailsByType("tv", "zh-CN", 300)
	if err != nil {
		return nil, err
	}
	historyMap := map[int]*db.PlayHistoryRow{}
	if userID > 0 {
		if rows, err := database.ListPlayHistory(userID, 400); err == nil {
			for i := range rows {
				row := rows[i]
				if row.TMDBID <= 0 || !strings.EqualFold(strings.TrimSpace(row.TMDBType), "tv") {
					continue
				}
				current, ok := historyMap[row.TMDBID]
				if !ok || row.UpdatedAt > current.UpdatedAt {
					copyRow := row
					historyMap[row.TMDBID] = &copyRow
				}
			}
		}
	}

	targetYear := parseTMDBCachedYear(target)
	out := make([]similarSeriesCandidate, 0, len(pool))
	for _, item := range pool {
		if item.TMDBID <= 0 || item.TMDBID == excludeTMDBID {
			continue
		}
		if strings.TrimSpace(item.Title) == "" {
			continue
		}
		yearDiff := absInt(targetYear - parseTMDBCachedYear(&item))
		out = append(out, similarSeriesCandidate{
			Detail:   item,
			Row:      historyMap[item.TMDBID],
			Score:    scoreSimilarSeriesCandidate(target, &item),
			YearDiff: yearDiff,
		})
	}

	sort.SliceStable(out, func(i, j int) bool {
		left := out[i]
		right := out[j]
		if left.Score != right.Score {
			return left.Score > right.Score
		}
		if left.YearDiff != right.YearDiff {
			return left.YearDiff < right.YearDiff
		}
		if left.Detail.VoteAverage != right.Detail.VoteAverage {
			return left.Detail.VoteAverage > right.Detail.VoteAverage
		}
		if left.Detail.Popularity != right.Detail.Popularity {
			return left.Detail.Popularity > right.Detail.Popularity
		}
		return left.Detail.TMDBID < right.Detail.TMDBID
	})

	if limit > 0 && len(out) > limit {
		out = out[:limit]
	}
	return out, nil
}

func scoreSimilarSeriesCandidate(target *db.TMDBCachedDetail, candidate *db.TMDBCachedDetail) int {
	if target == nil || candidate == nil {
		return 0
	}
	score := 0
	score += genreOverlapCount(target.GenreIDs, candidate.GenreIDs) * 1000
	targetYear := parseTMDBCachedYear(target)
	candidateYear := parseTMDBCachedYear(candidate)
	if targetYear > 0 && candidateYear > 0 {
		diff := absInt(targetYear - candidateYear)
		score += maxInt(0, 100-diff)
	}
	if candidate.VoteAverage > 0 {
		score += int(candidate.VoteAverage * 10)
	}
	if candidate.Popularity > 0 {
		score += int(candidate.Popularity)
	}
	return score
}

func renderSimilarSeriesItems(database *db.DB, serverID string, rows []similarSeriesCandidate) []SimilarSeriesItemDTO {
	items := make([]SimilarSeriesItemDTO, 0, len(rows))
	for _, row := range rows {
		itemID := buildSeriesID(row.Detail.TMDBID)
		dateCreated := EmbyZeroTimeString()
		if row.Detail.LastRefreshAt > 0 {
			dateCreated, _ = ProtocolDatePairFromUnix(row.Detail.LastRefreshAt)
		}
		runtimeMinutes := row.Detail.RuntimeMinutes
		if runtimeMinutes <= 0 && database != nil {
			if avg, err := database.ReadTMDBAverageEpisodeRuntime(row.Detail.TMDBID); err == nil && avg > 0 {
				runtimeMinutes = avg
			}
		}
		items = append(items, SimilarSeriesItemDTO{
			Name:                    strings.TrimSpace(row.Detail.Title),
			ServerID:                strings.TrimSpace(serverID),
			ID:                      itemID,
			DateCreated:             dateCreated,
			SupportsSync:            true,
			SortName:                SortNameOrName(row.Detail.Title),
			PremiereDate:            preciseDateString(strings.TrimSpace(row.Detail.FirstAir)),
			RunTimeTicks:            runtimeTicksFromMinutes(runtimeMinutes),
			ProductionYear:          parseTMDBCachedYear(&row.Detail),
			ProviderIDs:             similarProviderIDs(database, &row.Detail),
			IsFolder:                true,
			Type:                    "Series",
			UserData:                BuildSeriesUserData(row.Row),
			AirDays:                 EmptyStrings(),
			PrimaryImageAspectRatio: PosterAspectRatio(row.Detail.PosterPath),
			ImageTags:               SimilarImageTagsDTO{Primary: PrimaryTagForItem(itemID)},
			BackdropImageTags:       BackdropTagsOrEmpty(backdropTagsFromAsset(row.Detail.Backdrop)),
		})
	}
	return items
}

func similarProviderIDs(database *db.DB, detail *db.TMDBCachedDetail) map[string]any {
	if detail == nil || detail.TMDBID <= 0 {
		return EmptyAnyMap()
	}
	out := ProviderIDsFromTMDBAny(detail.TMDBID)
	if detail.TMDBType == "" || database == nil {
		return out
	}
	ids, err := database.ReadTMDBExternalIDs(detail.TMDBType, detail.TMDBID)
	if err != nil {
		return out
	}
	for key, value := range ids {
		if strings.TrimSpace(key) == "" || strings.TrimSpace(value) == "" {
			continue
		}
		out[key] = strings.TrimSpace(value)
	}
	if _, ok := out["Official Website"]; !ok {
		if homepage, err := database.ReadTMDBHomepage(detail.TMDBType, detail.TMDBID); err == nil && strings.TrimSpace(homepage) != "" {
			out["Official Website"] = strings.TrimSpace(homepage)
		}
	}
	return out
}

func runtimeTicksFromMinutes(minutes int) int64 {
	if minutes <= 0 {
		return 0
	}
	return int64(minutes) * 60 * 10_000_000
}

func genreOverlapCount(a []int, b []int) int {
	if len(a) == 0 || len(b) == 0 {
		return 0
	}
	set := make(map[int]struct{}, len(a))
	for _, id := range a {
		if id > 0 {
			set[id] = struct{}{}
		}
	}
	count := 0
	for _, id := range b {
		if id <= 0 {
			continue
		}
		if _, ok := set[id]; ok {
			count++
		}
	}
	return count
}

func absInt(v int) int {
	if v < 0 {
		return -v
	}
	return v
}
