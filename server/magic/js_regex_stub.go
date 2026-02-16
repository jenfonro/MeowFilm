//go:build !(cgo && (linux || darwin))

package magic

import (
	"errors"
	"regexp"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
)

func RegexAvailable() bool { return true }

type goRule struct {
	Raw     string
	Pattern string
	Flags   string
	Replace string
	Re      *regexp.Regexp
	Err     error
}

func goDecodeRule(raw string, forClean bool) (pattern string, replace string, flags string) {
	pat, rep, fl := DecodeEpisodeRule(raw)
	pat = strings.TrimSpace(pat)
	fl = strings.TrimSpace(fl)
	rep = strings.TrimSpace(NormalizeReplaceTemplate(rep))
	if fl == "" {
		fl = "i"
	}
	if forClean {
		rep = ""
	}
	return pat, rep, fl
}

func goCompileRegexp(pat string, flags string) (*regexp.Regexp, error) {
	p := strings.TrimSpace(pat)
	if p == "" {
		return nil, errors.New("empty pattern")
	}
	f := strings.TrimSpace(flags)
	prefix := ""
	if strings.Contains(f, "i") {
		prefix += "(?i)"
	}
	if strings.Contains(f, "m") {
		prefix += "(?m)"
	}
	if strings.Contains(f, "s") {
		prefix += "(?s)"
	}
	return regexp.Compile(prefix + p)
}

var goSpace = regexp.MustCompile(`\s+`)
var goSE = regexp.MustCompile(`(?i)(?:S(\d{1,2}))?\s*E(\d{1,5})`)

func goApplyClean(text string, cleanRaw []string) (cleaned string, steps []map[string]any) {
	out := strings.TrimSpace(text)
	steps = make([]map[string]any, 0, len(cleanRaw))
	for i := 0; i < len(cleanRaw); i++ {
		row := cleanRaw[i]
		pat, rep, fl := goDecodeRule(row, true)
		before := out
		after := before
		errMsg := ""
		if strings.TrimSpace(pat) != "" {
			re, err := goCompileRegexp(pat, fl)
			if err != nil {
				errMsg = err.Error()
			} else {
				after = re.ReplaceAllString(before, rep)
			}
		}
		out = after
		steps = append(steps, map[string]any{
			"index":   float64(i),
			"pattern": pat,
			"flags":   fl,
			"replace": rep,
			"before":  before,
			"after":   after,
			"error":   errMsg,
		})
	}
	out = strings.TrimSpace(goSpace.ReplaceAllString(out, " "))
	return out, steps
}

func goExtractSE(cleaned string, episodeRaw []string) (season int, episode int, normalized string, ruleIndex int) {
	text := strings.TrimSpace(cleaned)
	for i := 0; i < len(episodeRaw); i++ {
		row := episodeRaw[i]
		pat, rep, fl := goDecodeRule(row, false)
		// For debug/exec stability, ignore global semantics.
		fl = strings.ReplaceAll(fl, "g", "")
		re, err := goCompileRegexp(pat, fl)
		if err != nil {
			continue
		}
		if !re.MatchString(text) {
			continue
		}
		norm := text
		if strings.TrimSpace(rep) != "" {
			norm = re.ReplaceAllString(text, rep)
		}
		if m := goSE.FindStringSubmatch(norm); len(m) >= 3 && strings.TrimSpace(m[2]) != "" {
			sn := 0
			if strings.TrimSpace(m[1]) != "" {
				sn = intFromDigits(m[1])
			}
			en := intFromDigits(m[2])
			if en > 0 {
				return sn, en, norm, i
			}
		}
	}
	return 0, 0, "", -1
}

func MagicEpisodeExtractFromCandidates(candidates []string, cleanRaw []string, episodeRaw []string) (SeasonEpisode, error) {
	if len(candidates) == 0 {
		return SeasonEpisode{Season: 0, Episode: 0}, nil
	}
	for i := 0; i < len(candidates); i++ {
		q := strings.TrimSpace(candidates[i])
		if q == "" {
			continue
		}
		cleaned, _ := goApplyClean(q, cleanRaw)
		sn, en, _, _ := goExtractSE(cleaned, episodeRaw)
		if en > 0 {
			return SeasonEpisode{Season: sn, Episode: en}, nil
		}
	}
	return SeasonEpisode{Season: 0, Episode: 0}, nil
}

func CompileRulesDebug(database *db.DB) (any, error) {
	if database == nil {
		return nil, errors.New("db nil")
	}
	rawEpisodeRules := parseJSONStringArray(database.GetSetting("magic_episode_rules"))
	rawCleanRules := parseJSONStringArray(database.GetSetting("magic_episode_clean_regex_rules"))
	rawMovieRules := parseJSONStringArray(database.GetSetting("magic_movie_rules"))
	rawAggRules := parseJSONStringArray(database.GetSetting("magic_aggregate_regex_rules"))

	type info struct {
		Raw     string `json:"raw"`
		Pattern string `json:"pattern"`
		Flags   string `json:"flags"`
		Error   string `json:"error,omitempty"`
	}

	compileRule := func(row string, forClean bool) info {
		pat, _, fl := goDecodeRule(row, forClean)
		msg := ""
		if strings.TrimSpace(pat) != "" {
			if _, err := goCompileRegexp(pat, fl); err != nil {
				msg = err.Error()
			}
		}
		it := info{Raw: row, Pattern: pat, Flags: fl}
		if msg != "" {
			it.Error = msg
		}
		return it
	}

	compilePlain := func(row string) info {
		pat, _, fl := DecodeEpisodeRule(row)
		pat = strings.TrimSpace(pat)
		fl = strings.TrimSpace(fl)
		if pat == "" {
			pat = strings.TrimSpace(row)
		}
		if fl == "" {
			fl = "i"
		}
		msg := ""
		if pat != "" {
			if _, err := goCompileRegexp(pat, fl); err != nil {
				msg = err.Error()
			}
		}
		it := info{Raw: row, Pattern: pat, Flags: fl}
		if msg != "" {
			it.Error = msg
		}
		return it
	}

	outEpisode := make([]info, 0, len(rawEpisodeRules))
	for _, row := range rawEpisodeRules {
		outEpisode = append(outEpisode, compileRule(row, false))
	}
	outClean := make([]info, 0, len(rawCleanRules))
	for _, row := range rawCleanRules {
		outClean = append(outClean, compileRule(row, true))
	}
	outMovie := make([]info, 0, len(rawMovieRules))
	for _, row := range rawMovieRules {
		outMovie = append(outMovie, compilePlain(row))
	}
	outAgg := make([]info, 0, len(rawAggRules))
	for _, row := range rawAggRules {
		outAgg = append(outAgg, compilePlain(row))
	}

	return map[string]any{
		"engine": "go-regex",
		"compile": map[string]any{
			"magic_episode_rules":             outEpisode,
			"magic_episode_clean_regex_rules": outClean,
			"magic_movie_rules":               outMovie,
			"magic_aggregate_regex_rules":     outAgg,
		},
	}, nil
}

func MagicEpisodeDebug(q string, cleanRaw []string, episodeRaw []string) (any, error) {
	text := strings.TrimSpace(q)
	if text == "" {
		return map[string]any{"q": "", "message": "missing q"}, nil
	}
	cleaned, cleanSteps := goApplyClean(text, cleanRaw)
	sn, en, norm, ruleIdx := goExtractSE(cleaned, episodeRaw)

	ruleMatches := make([]map[string]any, 0, len(episodeRaw))
	for i := 0; i < len(episodeRaw); i++ {
		row := episodeRaw[i]
		pat, rep, fl := goDecodeRule(row, false)
		fl = strings.ReplaceAll(fl, "g", "")
		before := cleaned
		after := before
		matched := false
		var submatch any = nil
		errMsg := ""
		re, err := goCompileRegexp(pat, fl)
		if err != nil {
			errMsg = err.Error()
		} else {
			m := re.FindStringSubmatch(before)
			if len(m) > 0 {
				matched = true
				// Cast to []any for JSON friendliness.
				tmp := make([]any, 0, len(m))
				for _, s := range m {
					tmp = append(tmp, s)
				}
				submatch = tmp
				if strings.TrimSpace(rep) != "" {
					after = re.ReplaceAllString(before, rep)
				}
			}
		}
		ruleMatches = append(ruleMatches, map[string]any{
			"index":    float64(i),
			"pattern":  pat,
			"flags":    fl,
			"replace":  rep,
			"matched":  matched,
			"submatch": submatch,
			"before":   before,
			"after":    after,
			"error":    errMsg,
		})
	}

	extracted := map[string]any{"season": float64(0), "episode": float64(0)}
	if en > 0 {
		extracted = map[string]any{
			"season":     float64(sn),
			"episode":    float64(en),
			"normalized": norm,
			"ruleIndex":  float64(ruleIdx),
		}
	}

	return map[string]any{
		"engine":      "go-regex",
		"cleaned":     cleaned,
		"cleanSteps":  cleanSteps,
		"ruleMatches": ruleMatches,
		"extracted":   extracted,
	}, nil
}
