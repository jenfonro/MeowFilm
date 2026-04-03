package smart

import (
	"fmt"
	"sort"
	"strings"

	"github.com/jenfonro/meowfilm/server/catpawrunner"
	"github.com/jenfonro/meowfilm/server/magic"
)

func smartBuildPanMockResolveGroupKey(r smartDetailSourceRecord) string {
	provider := strings.TrimSpace(r.Provider)
	panFlag := strings.TrimSpace(r.PanFlag)
	sourceValue := strings.TrimSpace(r.SourceValue)
	if provider == "" || panFlag == "" {
		return ""
	}
	if provider == "189" {
		shareCode, accessCode := smartPanMock189CredentialsFromSourceValue(panFlag, sourceValue)
		if strings.TrimSpace(shareCode) == "" {
			return ""
		}
		return provider + "|" + strings.TrimSpace(shareCode) + "|" + strings.TrimSpace(accessCode)
	}
	return provider + "|" + strings.TrimSpace(catpawrunner.NormalizePanMockFlag(panFlag))
}

func smartBuildDetailSourceRecords(playFrom string, playURL string, panMock bool, src smartSource) []smartDetailSourceRecord {
	fromRaw := strings.TrimSpace(playFrom)
	urlRaw := strings.TrimSpace(playURL)
	if fromRaw == "" && urlRaw == "" {
		return nil
	}
	fromParts := strings.Split(fromRaw, "$$$")
	urlParts := strings.Split(urlRaw, "$$$")
	n := len(fromParts)
	if len(urlParts) > n {
		n = len(urlParts)
	}
	out := make([]smartDetailSourceRecord, 0, n)
	for i := 0; i < n; i++ {
		label := ""
		if i < len(fromParts) {
			label = strings.TrimSpace(fromParts[i])
		}
		if label == "" {
			label = fmt.Sprintf("源%d", i+1)
		}
		sourceValue := ""
		if i < len(urlParts) {
			sourceValue = strings.TrimSpace(urlParts[i])
		}
		record := smartDetailSourceRecord{
			SourceKey:   fmt.Sprintf("%s::%s::%d", strings.TrimSpace(src.SiteKey), strings.TrimSpace(src.SiteDetail), i),
			SiteKey:     strings.TrimSpace(src.SiteKey),
			SiteDetail:  strings.TrimSpace(src.SiteDetail),
			Label:       strings.TrimSpace(label),
			PanMock:     panMock,
			SourceValue: strings.TrimSpace(sourceValue),
			Status:      smartDetailSourceSkipped,
			AccessDelta: map[string]string{},
		}
		if panMock && catpawrunner.IsSupportedPanMockFlag(label) {
			record.Provider = strings.TrimSpace(catpawrunner.PanMockProviderFromFlag(label))
			record.PanFlag = strings.TrimSpace(catpawrunner.NormalizePanMockFlag(label))
			record.Supported = record.Provider != "" && record.PanFlag != ""
			if record.Supported {
				record.GroupKey = smartBuildPanMockResolveGroupKey(record)
				record.Status = smartDetailSourcePending
			}
			out = append(out, record)
			continue
		}
		if strings.TrimSpace(sourceValue) == "" {
			continue
		}
		segs := []string{}
		for _, s := range strings.Split(sourceValue, "#") {
			ss := strings.TrimSpace(s)
			if ss != "" {
				segs = append(segs, ss)
			}
		}
		eps := make([]catpawrunner.Episode, 0, len(segs))
		for _, seg := range segs {
			name := seg
			id := seg
			if idx := strings.Index(seg, "$"); idx > 0 {
				name = strings.TrimSpace(seg[:idx])
				id = strings.TrimSpace(seg[idx+1:])
			}
			if name == "" {
				name = seg
			}
			if id == "" {
				id = seg
			}
			eps = append(eps, catpawrunner.Episode{Name: name, URL: id, Flag: record.Label})
		}
		if len(eps) == 0 {
			continue
		}
		record.Episodes = eps
		record.Status = smartDetailSourceResolved
		out = append(out, record)
	}
	return out
}

// smartResolvedRecordsToPans derives edge display/output Pans from resolved
// source records. It is intentionally one-way and must not become an internal
// processing model again.
func smartResolvedRecordsToPans(records []smartDetailSourceRecord) []catpawrunner.Pan {
	if len(records) == 0 {
		return nil
	}
	out := make([]catpawrunner.Pan, 0, len(records))
	seenPanMock := map[string]struct{}{}
	for _, record := range records {
		if record.Status != smartDetailSourceResolved || len(record.Episodes) == 0 {
			continue
		}
		label := strings.TrimSpace(record.Label)
		if strings.TrimSpace(record.PanFlag) != "" {
			label = strings.TrimSpace(record.PanFlag)
		}
		if record.PanMock && record.Supported {
			key := strings.TrimSpace(record.GroupKey)
			if key == "" {
				key = smartBuildPanMockResolveGroupKey(record)
			}
			if key != "" {
				if _, ok := seenPanMock[key]; ok {
					continue
				}
				seenPanMock[key] = struct{}{}
			}
		}
		eps := make([]catpawrunner.Episode, len(record.Episodes))
		copy(eps, record.Episodes)
		out = append(out, catpawrunner.Pan{
			Label:          label,
			Episodes:       eps,
			PanMockEnabled: record.PanMock && record.Supported,
		})
	}
	return out
}

func smartFilterSourceRecordsByBlockedFlags(records []smartDetailSourceRecord, blocked map[string]struct{}) []smartDetailSourceRecord {
	if len(records) == 0 || len(blocked) == 0 {
		return records
	}
	out := make([]smartDetailSourceRecord, 0, len(records))
	for _, record := range records {
		label := strings.TrimSpace(record.Label)
		if strings.TrimSpace(record.PanFlag) != "" {
			label = strings.TrimSpace(record.PanFlag)
		}
		if label != "" {
			if _, ok := blocked[label]; ok {
				continue
			}
		}
		out = append(out, record)
	}
	return out
}

func smartBuildCandidatesFromResolvedRecords(
	src smartSource,
	records []smartDetailSourceRecord,
	isMovieMode bool,
	primarySeasons []smartTMDBSeason,
	singleBaselineSeasons []smartTMDBSeason,
	tmdbHasMultiSeason bool,
	settings smartPlaybackSettings,
	rawCleanRules []string,
	rawEpisodeRules []string,
	rawMovieRules []string,
	allowSingleBaseline bool,
	primaryKind string,
) (map[int][]smartCandidate, map[int][]smartCandidate, []smartCandidate) {
	if isMovieMode {
		return nil, nil, smartBuildMovieCandidatesFromResolvedRecords(src, records, settings, rawCleanRules, rawMovieRules)
	}
	epMap, epLoose := smartBuildEpisodeMapsFromResolvedRecords(
		src,
		records,
		primarySeasons,
		singleBaselineSeasons,
		tmdbHasMultiSeason,
		settings,
		rawCleanRules,
		rawEpisodeRules,
		allowSingleBaseline,
		primaryKind,
	)
	return epMap, epLoose, nil
}

func smartSourceHasEpisodeBeyondFirstSeasonRecords(
	records []smartDetailSourceRecord,
	rawCleanRules []string,
	rawEpisodeRules []string,
	firstSeasonCount int,
) bool {
	if firstSeasonCount <= 0 || len(records) == 0 || len(rawCleanRules) == 0 || len(rawEpisodeRules) == 0 {
		return false
	}
	for _, record := range records {
		if record.Status != smartDetailSourceResolved {
			continue
		}
		for _, ep := range record.Episodes {
			if strings.TrimSpace(ep.URL) == "" {
				continue
			}
			texts := smartExtractEpisodeCandidateTexts(ep)
			jsMatch, err := magic.MagicEpisodeExtractFromCandidates(texts, rawCleanRules, rawEpisodeRules)
			if err != nil {
				continue
			}
			if jsMatch.Episode > firstSeasonCount {
				return true
			}
		}
	}
	return false
}

func smartBuildEpisodeMapsFromResolvedRecords(
	src smartSource,
	records []smartDetailSourceRecord,
	primarySeasons []smartTMDBSeason,
	singleBaselineSeasons []smartTMDBSeason,
	tmdbHasMultiSeason bool,
	settings smartPlaybackSettings,
	rawCleanRules []string,
	rawEpisodeRules []string,
	allowSingleBaseline bool,
	primaryKind string,
) (map[int][]smartCandidate, map[int][]smartCandidate) {
	episodeMap := map[int][]smartCandidate{}
	episodeMapLoose := map[int][]smartCandidate{}
	primaryFirstSeasonCount := 0
	for _, s := range primarySeasons {
		if s.Season == 1 && s.EpisodeCount > 0 {
			primaryFirstSeasonCount = s.EpisodeCount
			break
		}
	}
	sourceHasBeyondFirstSeason := smartSourceHasEpisodeBeyondFirstSeasonRecords(records, rawCleanRules, rawEpisodeRules, primaryFirstSeasonCount)

	srcRemarkLower := strings.ToLower(strings.TrimSpace(src.Remark))
	for _, record := range records {
		if record.Status != smartDetailSourceResolved {
			continue
		}
		panFlag := strings.TrimSpace(record.Label)
		if strings.TrimSpace(record.PanFlag) != "" {
			panFlag = strings.TrimSpace(record.PanFlag)
		}
		panTokenIdx := smartLabelRuleIdx(panFlag, settings.PanTokenOrderLower, settings.PanMatchEntries)
		for _, ep := range record.Episodes {
			if strings.TrimSpace(ep.URL) == "" {
				continue
			}
			texts := smartExtractEpisodeCandidateTexts(ep)
			primary := ""
			if len(texts) > 0 {
				primary = texts[0]
			}
			if primary == "" {
				primary = ep.Name
			}
			rawLower := smartBuildCandidateLowerText(texts)
			if rawLower == "" {
				rawLower = strings.ToLower(strings.TrimSpace(primary))
			}
			rawLower = strings.TrimSpace(rawLower + " " + srcRemarkLower)

			jsMatch, err := magic.MagicEpisodeExtractFromCandidates(texts, rawCleanRules, rawEpisodeRules)
			if err != nil {
				continue
			}
			rawSeason := jsMatch.Season
			match, keyNo, ok, loose, resolutionMode, degradedReason := smartResolveEpisodeMappingForPlaybackWithMode(
				primarySeasons,
				smartSeasonEpisode{Season: jsMatch.Season, Episode: jsMatch.Episode},
				singleBaselineSeasons,
				primaryFirstSeasonCount,
				sourceHasBeyondFirstSeason,
				allowSingleBaseline,
				primaryKind,
			)
			seasonNo := match.Season
			epNo := match.Episode
			if !ok || epNo <= 0 {
				continue
			}
			rawName := smartStableRawNameFromEpisodeURL(ep.URL)
			cand := smartCandidate{
				Stage:           smartCandidateStageFull,
				SiteKey:         src.SiteKey,
				SiteName:        src.SiteName,
				SpiderAPI:       src.SpiderAPI,
				SiteDetail:      src.SiteDetail,
				SrcRemarkLower:  srcRemarkLower,
				PanFlag:         panFlag,
				PanTokenIdx:     panTokenIdx,
				Ep:              ep,
				RawName:         rawName,
				RawLower:        rawLower,
				MatchSeason:     seasonNo,
				HasSeasonMarker: rawSeason > 0,
				MatchKeyword:    smartComputePriorityMatch(rawLower, settings.KeywordTokensLower),
				ResolutionMode:  resolutionMode,
				LockedGlobal:    keyNo,
				DegradedReason:  degradedReason,
				StrictMatched:   resolutionMode == "strict-tmdb" || resolutionMode == "strict-douban",
				DegradedMatched: resolutionMode == "degraded-single-baseline",
			}

			if loose && tmdbHasMultiSeason {
				episodeMapLoose[epNo] = append(episodeMapLoose[epNo], cand)
				continue
			}
			episodeMap[keyNo] = append(episodeMap[keyNo], cand)
		}
	}
	return episodeMap, episodeMapLoose
}

func smartBuildMovieCandidatesFromResolvedRecords(
	src smartSource,
	records []smartDetailSourceRecord,
	settings smartPlaybackSettings,
	rawCleanRules []string,
	rawMovieRules []string,
) []smartCandidate {
	if len(rawMovieRules) == 0 || len(records) == 0 {
		return nil
	}
	out := make([]smartCandidate, 0, 16)
	srcRemarkLower := strings.ToLower(strings.TrimSpace(src.Remark))
	for _, record := range records {
		if record.Status != smartDetailSourceResolved {
			continue
		}
		panFlag := strings.TrimSpace(record.Label)
		if strings.TrimSpace(record.PanFlag) != "" {
			panFlag = strings.TrimSpace(record.PanFlag)
		}
		panTokenIdx := smartLabelRuleIdx(panFlag, settings.PanTokenOrderLower, settings.PanMatchEntries)
		for _, ep := range record.Episodes {
			if strings.TrimSpace(ep.URL) == "" {
				continue
			}
			rawNames := smartExtractRawNamesFromEpisodeURL(ep.URL)
			rawName := ""
			for i := len(rawNames) - 1; i >= 0; i-- {
				if strings.TrimSpace(rawNames[i]) != "" {
					rawName = strings.TrimSpace(rawNames[i])
					break
				}
			}
			if rawName == "" {
				continue
			}
			texts := []string{rawName}
			hit, err := magic.MagicMovieMatchFromCandidates(texts, rawCleanRules, rawMovieRules)
			if err != nil || !hit {
				continue
			}
			rawLower := smartBuildCandidateLowerText(texts)
			if rawLower == "" {
				rawLower = strings.ToLower(rawName)
			}
			rawLower = strings.TrimSpace(rawLower + " " + srcRemarkLower)
			out = append(out, smartCandidate{
				Stage:          smartCandidateStageFull,
				SiteKey:        src.SiteKey,
				SiteName:       src.SiteName,
				SpiderAPI:      src.SpiderAPI,
				SiteDetail:     src.SiteDetail,
				SrcRemarkLower: srcRemarkLower,
				PanFlag:        panFlag,
				PanTokenIdx:    panTokenIdx,
				Ep:             ep,
				RawName:        rawName,
				RawLower:       rawLower,
				MatchKeyword:   smartComputePriorityMatch(rawLower, settings.KeywordTokensLower),
			})
		}
	}
	if len(out) == 0 {
		return nil
	}
	sort.SliceStable(out, func(i, j int) bool {
		return smartCompareSmartMatchIgnorePanOrder(out[i], out[j], false, 0, settings) < 0
	})
	return out
}
