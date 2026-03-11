package smart

import (
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/magic"
	"github.com/jenfonro/meowfilm/server/netdisk"
)

type smartPanMockGroupAttempt struct {
	Key      string
	Allowed  bool
	Provider string
	Base     smartCandidate
	MetaA    string // provider-specific meta: shareCode for tianyi, passcode for others
	MetaB    string // provider-specific meta: accessCode for tianyi
}

func smartBuildPanMockGroupAttempts(
	candidatesForNo []smartCandidate,
	settings smartPlaybackSettings,
	database *db.DB,
	tmdbHasMultiSeason bool,
	preferSeasonNo int,
) (allowedAttempts []smartPanMockGroupAttempt, fallbackAttempts []smartPanMockGroupAttempt, normalAllowed []smartCandidate, normalFallback []smartCandidate) {
	allowedTokens := settings.PanTokenOrderLower
	isAllowedLabel := func(label string) bool {
		if len(allowedTokens) == 0 {
			return true
		}
		ll := smartPanMatchLabelText(label)
		for _, t := range allowedTokens {
			tt := strings.ToLower(strings.TrimSpace(t))
			if tt == "" {
				continue
			}
			if strings.Contains(ll, tt) {
				return true
			}
		}
		return false
	}

	groups := map[string]smartPanMockGroupAttempt{}
	normalAllowed = []smartCandidate{}
	normalFallback = []smartCandidate{}
	for _, c := range candidatesForNo {
		pid := smartPanMockProviderID(database, c.PanLabel)
		allowed := isAllowedLabel(c.PanLabel)
		if pid == "" {
			if allowed {
				normalAllowed = append(normalAllowed, c)
			} else {
				normalFallback = append(normalFallback, c)
			}
			continue
		}
		// pan_mock sources should not be affected by pan priority/order filters:
		// always allow them, and let whichever provider resolves fastest win.
		allowed = true

		shareKey := strings.TrimSpace(c.PanLabel)
		metaA := ""
		metaB := ""
		if pid == "189" {
			sc, ac := smartExtractTianyiMockMetaFromCandidate(c)
			metaA = sc
			metaB = ac
			if sc != "" {
				shareKey = sc
			}
		} else {
			metaA = smartExtractMockPasscodeFromCandidate(c)
		}
		key := pid + "|" + shareKey
		if prev, ok := groups[key]; ok {
			best := smartPickBestMatchIgnorePanOrder([]smartCandidate{prev.Base, c}, tmdbHasMultiSeason, preferSeasonNo, settings)
			if best != nil && strings.TrimSpace(best.Ep.URL) != strings.TrimSpace(prev.Base.Ep.URL) {
				prev.Base = c
			}
			groups[key] = prev
			continue
		}
		groups[key] = smartPanMockGroupAttempt{
			Key:      key,
			Allowed:  allowed,
			Provider: pid,
			Base:     c,
			MetaA:    metaA,
			MetaB:    metaB,
		}
	}

	allowedAttempts = []smartPanMockGroupAttempt{}
	fallbackAttempts = []smartPanMockGroupAttempt{}
	for _, at := range groups {
		if at.Allowed {
			allowedAttempts = append(allowedAttempts, at)
		} else {
			fallbackAttempts = append(fallbackAttempts, at)
		}
	}
	return allowedAttempts, fallbackAttempts, normalAllowed, normalFallback
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
	eps := smartParseVodPlayURLToEpisodes(vodPlayURL)
	if len(eps) == 0 {
		return nil
	}
	matches := []smartCandidate{}
	for _, ep := range eps {
		if strings.TrimSpace(ep.URL) == "" {
			continue
		}
		texts := smartExtractEpisodeCandidateTexts(ep)
		if len(texts) == 0 || strings.TrimSpace(texts[0]) == "" {
			texts = []string{strings.TrimSpace(ep.Name)}
		}
		jsMatch, err := magic.MagicEpisodeExtractFromCandidates(texts, rawCleanRules, rawEpisodeRules)
		if err != nil {
			continue
		}
		rawSeason := jsMatch.Season
		match, keyNo, ok, _ := smartResolveEpisodeMappingForPlayback(tmdbSeasons, smartSeasonEpisode{Season: jsMatch.Season, Episode: jsMatch.Episode})
		seasonNo := match.Season
		epNo := match.Episode
		if !ok || epNo <= 0 {
			continue
		}
		if keyNo != want {
			continue
		}
		rawLower := smartBuildCandidateLowerText(texts)
		if rawLower == "" {
			rawLower = strings.ToLower(strings.TrimSpace(ep.Name))
		}
		cand := base
		cand.Ep = ep
		cand.RawLower = rawLower
		cand.MatchSeason = seasonNo
		cand.HasSeasonMarker = rawSeason > 0
		cand.MatchKeyword = smartComputePriorityMatch(rawLower, settings.KeywordTokensLower)
		matches = append(matches, cand)
	}
	if len(matches) == 0 {
		return nil
	}
	return smartPickBestMatchIgnorePanOrder(matches, tmdbHasMultiSeason, preferSeasonNo, settings)
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
	switch at.Provider {
	case "189":
		sc := strings.TrimSpace(at.MetaA)
		ac := strings.TrimSpace(at.MetaB)
		if sc == "" {
			sc2, ac2 := smartExtractTianyiMockMetaFromCandidate(base)
			sc = strings.TrimSpace(sc2)
			if ac == "" {
				ac = strings.TrimSpace(ac2)
			}
		}
		if strings.TrimSpace(ac) == "" && len(accessByShareID) > 0 {
			parts := strings.Split(strings.TrimSpace(base.Ep.URL), "*")
			if len(parts) >= 2 {
				shareID := strings.TrimSpace(parts[1])
				if shareID != "" {
					if v, ok := accessByShareID[shareID]; ok {
						ac = strings.TrimSpace(v)
					}
				}
			}
		}
		if u, _, _, _, err := netdisk.Tianyi189Play(database, strings.TrimSpace(base.Ep.URL), ac); err == nil && strings.TrimSpace(u) != "" {
			return &smartPickResult{Cand: base, PlayURL: strings.TrimSpace(u), Headers: map[string]string{}}
		}
		if sc == "" {
			return nil
		}
		flag := "天意-" + sc
		vod, _, _, err := netdisk.Tianyi189List(database, flag, ac)
		if err != nil {
			return nil
		}
		picked := smartResolvePanMockCandidateFromVod(base, vod, want, tmdbSeasons, tmdbHasMultiSeason, preferSeasonNo, settings, rawCleanRules, rawEpisodeRules)
		if picked == nil || strings.TrimSpace(picked.Ep.URL) == "" {
			return nil
		}
		if strings.TrimSpace(ac) == "" && len(accessByShareID) > 0 {
			parts := strings.Split(strings.TrimSpace(picked.Ep.URL), "*")
			if len(parts) >= 2 {
				shareID := strings.TrimSpace(parts[1])
				if shareID != "" {
					if v, ok := accessByShareID[shareID]; ok {
						ac = strings.TrimSpace(v)
					}
				}
			}
		}
		u, _, _, _, err := netdisk.Tianyi189Play(database, picked.Ep.URL, ac)
		if err != nil || strings.TrimSpace(u) == "" {
			return nil
		}
		return &smartPickResult{Cand: *picked, PlayURL: u, Headers: map[string]string{}}
	case "quark":
		if strings.TrimSpace(at.MetaA) == "" {
			u, header, err := netdisk.QuarkPlayWithTVUser(database, strings.TrimSpace(base.Ep.URL), "", tvUser)
			if err == nil && strings.TrimSpace(u) != "" {
				if header == nil {
					header = map[string]string{}
				}
				return &smartPickResult{Cand: base, PlayURL: strings.TrimSpace(u), Headers: header}
			}
		}
		vod, _, err := netdisk.QuarkList(database, strings.TrimSpace(base.PanLabel), strings.TrimSpace(at.MetaA))
		if err != nil {
			return nil
		}
		picked := smartResolvePanMockCandidateFromVod(base, vod, want, tmdbSeasons, tmdbHasMultiSeason, preferSeasonNo, settings, rawCleanRules, rawEpisodeRules)
		if picked == nil || strings.TrimSpace(picked.Ep.URL) == "" {
			return nil
		}
		u, header, err := netdisk.QuarkPlayWithTVUser(database, picked.Ep.URL, "", tvUser)
		if err != nil || strings.TrimSpace(u) == "" {
			return nil
		}
		if header == nil {
			header = map[string]string{}
		}
		return &smartPickResult{Cand: *picked, PlayURL: u, Headers: header}
	case "uc":
		if strings.TrimSpace(at.MetaA) == "" {
			u, header, err := netdisk.UCPlayWithTVUser(database, strings.TrimSpace(base.Ep.URL), "", tvUser)
			if err == nil && strings.TrimSpace(u) != "" {
				if header == nil {
					header = map[string]string{}
				}
				return &smartPickResult{Cand: base, PlayURL: strings.TrimSpace(u), Headers: header}
			}
		}
		vod, _, err := netdisk.UCList(database, strings.TrimSpace(base.PanLabel), strings.TrimSpace(at.MetaA))
		if err != nil {
			return nil
		}
		picked := smartResolvePanMockCandidateFromVod(base, vod, want, tmdbSeasons, tmdbHasMultiSeason, preferSeasonNo, settings, rawCleanRules, rawEpisodeRules)
		if picked == nil || strings.TrimSpace(picked.Ep.URL) == "" {
			return nil
		}
		u, header, err := netdisk.UCPlayWithTVUser(database, picked.Ep.URL, "", tvUser)
		if err != nil || strings.TrimSpace(u) == "" {
			return nil
		}
		if header == nil {
			header = map[string]string{}
		}
		return &smartPickResult{Cand: *picked, PlayURL: u, Headers: header}
	case "139":
		{
			downloadURL, playURL, err := netdisk.Yun139Play(database, strings.TrimSpace(base.PanLabel), strings.TrimSpace(base.Ep.URL))
			u := strings.TrimSpace(downloadURL)
			if u == "" {
				u = strings.TrimSpace(playURL)
			}
			if err == nil && u != "" {
				return &smartPickResult{Cand: base, PlayURL: u, Headers: map[string]string{}}
			}
		}
		vod, _, err := netdisk.Yun139List(database, strings.TrimSpace(base.PanLabel), "")
		if err != nil {
			return nil
		}
		picked := smartResolvePanMockCandidateFromVod(base, vod, want, tmdbSeasons, tmdbHasMultiSeason, preferSeasonNo, settings, rawCleanRules, rawEpisodeRules)
		if picked == nil || strings.TrimSpace(picked.Ep.URL) == "" {
			return nil
		}
		downloadURL, playURL, err := netdisk.Yun139Play(database, strings.TrimSpace(base.PanLabel), picked.Ep.URL)
		u := strings.TrimSpace(downloadURL)
		if u == "" {
			u = strings.TrimSpace(playURL)
		}
		if err != nil || u == "" {
			return nil
		}
		return &smartPickResult{Cand: *picked, PlayURL: u, Headers: map[string]string{}}
	case "baidu":
		if strings.TrimSpace(at.MetaA) == "" {
			u, header, err := netdisk.BaiduPlay(database, strings.TrimSpace(base.PanLabel), strings.TrimSpace(base.Ep.URL), "/MeowFilm")
			if err == nil && strings.TrimSpace(u) != "" {
				if header == nil {
					header = map[string]string{}
				}
				return &smartPickResult{Cand: base, PlayURL: strings.TrimSpace(u), Headers: header}
			}
		}
		vod, _, err := netdisk.BaiduList(database, strings.TrimSpace(base.PanLabel), strings.TrimSpace(at.MetaA))
		if err != nil {
			return nil
		}
		picked := smartResolvePanMockCandidateFromVod(base, vod, want, tmdbSeasons, tmdbHasMultiSeason, preferSeasonNo, settings, rawCleanRules, rawEpisodeRules)
		if picked == nil || strings.TrimSpace(picked.Ep.URL) == "" {
			return nil
		}
		u, header, err := netdisk.BaiduPlay(database, strings.TrimSpace(base.PanLabel), picked.Ep.URL, "/MeowFilm")
		if err != nil || strings.TrimSpace(u) == "" {
			return nil
		}
		if header == nil {
			header = map[string]string{}
		}
		return &smartPickResult{Cand: *picked, PlayURL: strings.TrimSpace(u), Headers: header}
	default:
		return nil
	}
}
