package emby

import (
	"encoding/json"
	"html"
	"net/http"
	"os"
	"strings"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/magic"
)

func regexDebugWantsHTML(r *http.Request) bool {
	if r == nil {
		return false
	}
	if strings.EqualFold(strings.TrimSpace(r.URL.Query().Get("format")), "html") {
		return true
	}
	if strings.TrimSpace(r.URL.Query().Get("html")) == "1" {
		return true
	}
	accept := r.Header.Get("Accept")
	return strings.Contains(accept, "text/html")
}

func regexDebugWriteHTML(w http.ResponseWriter, title string, data any) {
	raw, err := json.MarshalIndent(data, "", "  ")
	if err != nil {
		writeJSON(w, 500, map[string]any{"ok": false, "error": err.Error()})
		return
	}

	lines := strings.Split(string(raw), "\n")
	var b strings.Builder
	b.Grow(len(raw) + 4096)

	b.WriteString("<!doctype html><html><head><meta charset=\"utf-8\">")
	b.WriteString("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
	b.WriteString("<title>")
	b.WriteString(html.EscapeString(title))
	b.WriteString("</title>")
	b.WriteString("<style>")
	b.WriteString("body{font-family:ui-monospace,SFMono-Regular,Menlo,Monaco,Consolas,monospace;margin:16px;background:#0b0d10;color:#e6edf3}")
	b.WriteString("a{color:#79c0ff;text-decoration:none} a:hover{text-decoration:underline}")
	b.WriteString(".bar{display:flex;gap:12px;align-items:center;margin-bottom:12px}")
	b.WriteString(".hint{opacity:.75;font-size:12px}")
	b.WriteString("pre{white-space:pre;overflow:auto;padding:12px;border:1px solid #30363d;border-radius:8px;background:#0f1318;line-height:1.35}")
	b.WriteString(".err{color:#ff7b72;background:rgba(255,123,114,.12);display:block}")
	b.WriteString("</style></head><body>")
	b.WriteString("<div class=\"bar\">")
	b.WriteString("<strong>")
	b.WriteString(html.EscapeString(title))
	b.WriteString("</strong>")
	b.WriteString("<span class=\"hint\">?format=html / ?html=1</span>")
	b.WriteString("</div>")

	b.WriteString("<pre>")
	for _, line := range lines {
		esc := html.EscapeString(line)
		// Highlight any line that contains an "error" field (QuickJS compile/exec errors).
		trim := strings.TrimSpace(line)
		if strings.Contains(trim, "\"error\"") || strings.Contains(trim, "\"jsError\"") || strings.Contains(trim, "\"jsMagicEpisodeError\"") {
			b.WriteString("<span class=\"err\">")
			b.WriteString(esc)
			b.WriteString("</span>\n")
			continue
		}
		b.WriteString(esc)
		b.WriteString("\n")
	}
	b.WriteString("</pre>")
	b.WriteString("</body></html>")

	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	_, _ = w.Write([]byte(b.String()))
}

func regexDebugExtractSE(jsMagic any) (season int, episode int, ok bool) {
	m, okMap := jsMagic.(map[string]any)
	if !okMap {
		return 0, 0, false
	}
	extracted, okExtracted := m["extracted"].(map[string]any)
	if !okExtracted {
		return 0, 0, false
	}
	seasonF, okS := extracted["season"].(float64)
	episodeF, okE := extracted["episode"].(float64)
	if !okS || !okE {
		return 0, 0, false
	}
	return int(seasonF), int(episodeF), true
}

func RegexListDebugHandler(database *db.DB) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if strings.TrimSpace(os.Getenv("MEOWFILM_DEBUG")) != "1" {
			embyNotFound(w)
			return
		}
		if r.Method != http.MethodGet {
			embyMethodNotAllowed(w)
			return
		}

		rawEpisodeRules, _ := database.ListMagicEpisodeRules()
		rawCleanRules, _ := database.ListMagicEpisodeCleanRegexRules()
		rawMovieRules, _ := database.ListMagicMovieRules()
		rawAggRules, _ := database.ListMagicAggregateRegexRules()

		type ruleInfo struct {
			Raw     string `json:"raw"`
			Pattern string `json:"pattern"`
			Flags   string `json:"flags"`
			Used    any    `json:"used"`
			Replace string `json:"replace"`
		}

		decodeEpisodeRule := func(raw string, isClean bool) ruleInfo {
			pat, rep, flags := magic.DecodeEpisodeRule(raw)
			if strings.TrimSpace(flags) == "" {
				flags = "i"
			}
			used := magic.DecodeRule(raw, isClean)
			// Expose the final rule that will be executed by QuickJS.
			usedMap := map[string]any{
				"pattern":  strings.TrimSpace(used.Pattern),
				"flags":    strings.TrimSpace(used.Flags),
				"replace":  strings.TrimSpace(used.Replace),
				"isClean":  isClean,
				"rawInput": raw,
			}
			// Keep `replace` in the top-level for quick scan (mirrors legacy debug output).
			if !isClean {
				rep = magic.NormalizeReplaceTemplate(rep)
			}
			return ruleInfo{
				Raw:     raw,
				Pattern: strings.TrimSpace(pat),
				Flags:   strings.TrimSpace(flags),
				Used:    usedMap,
				Replace: strings.TrimSpace(rep),
			}
		}

		episodeRules := make([]ruleInfo, 0, len(rawEpisodeRules))
		for _, row := range rawEpisodeRules {
			episodeRules = append(episodeRules, decodeEpisodeRule(row, false))
		}
		cleanRules := make([]ruleInfo, 0, len(rawCleanRules))
		for _, row := range rawCleanRules {
			cleanRules = append(cleanRules, decodeEpisodeRule(row, true))
		}

		decodePlain := func(raw string) ruleInfo {
			p := strings.TrimSpace(raw)
			return ruleInfo{
				Raw:     raw,
				Pattern: p,
				Flags:   "i",
				Used: map[string]any{
					"pattern":  p,
					"flags":    "i",
					"replace":  "",
					"isClean":  false,
					"rawInput": raw,
				},
				Replace: "",
			}
		}

		movieRules := make([]ruleInfo, 0, len(rawMovieRules))
		for _, row := range rawMovieRules {
			movieRules = append(movieRules, decodePlain(row))
		}
		aggregateRegexRules := make([]ruleInfo, 0, len(rawAggRules))
		for _, row := range rawAggRules {
			aggregateRegexRules = append(aggregateRegexRules, decodePlain(row))
		}

		seasonSuffixPatterns := smartSeasonSuffixRegexPatterns()
		embySeasonSuffix := []ruleInfo{
			{Raw: "smartReCNSeasonSuffix", Pattern: seasonSuffixPatterns[0]},
			{Raw: "smartReENSeasonSuffix", Pattern: seasonSuffixPatterns[1]},
			{Raw: "smartReSSeasonSuffix", Pattern: seasonSuffixPatterns[2]},
		}

		resp := map[string]any{
			"ok": true,
			"rules": map[string]any{
				"magic_episode_rules":             episodeRules,
				"magic_episode_clean_regex_rules": cleanRules,
				"magic_movie_rules":               movieRules,
				"magic_aggregate_regex_rules":     aggregateRegexRules,
				"emby_season_suffix_rules":        embySeasonSuffix,
			},
		}

		q := strings.TrimSpace(r.URL.Query().Get("q"))
		if q == "" {
			q = strings.TrimSpace(r.URL.Query().Get("text"))
		}
		if q == "" {
			q = strings.TrimSpace(r.URL.Query().Get("name"))
		}
		if q == "" {
			q = strings.TrimSpace(r.URL.Query().Get("filename"))
		}
		if q != "" {
			resp["q"] = q
			resp["embyTitleNormalize"] = map[string]any{
				"tv":    embyNormalizeTitleForTMDB("tv", q),
				"movie": embyNormalizeTitleForTMDB("movie", q),
			}
		}

		if magic.RegexAvailable() {
			if jsInfo, err := magic.CompileRulesDebug(database); err == nil && jsInfo != nil {
				resp["js"] = jsInfo
			} else if err != nil {
				resp["jsError"] = err.Error()
			}
			if q != "" {
				if jsRes, err := magic.MagicEpisodeDebug(q, rawCleanRules, rawEpisodeRules); err == nil && jsRes != nil {
					resp["jsMagicEpisode"] = jsRes
					if s, e, ok := regexDebugExtractSE(jsRes); ok {
						resp["extracted"] = map[string]any{"season": s, "episode": e}
					}
				} else if err != nil {
					resp["jsMagicEpisodeError"] = err.Error()
				}
			}
		}

		if regexDebugWantsHTML(r) {
			regexDebugWriteHTML(w, "listdebug", resp)
			return
		}
		writeJSON(w, 200, resp)
	})
}

func RegexSearchDebugHandler(database *db.DB) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if strings.TrimSpace(os.Getenv("MEOWFILM_DEBUG")) != "1" {
			embyNotFound(w)
			return
		}
		if r.Method != http.MethodGet {
			embyMethodNotAllowed(w)
			return
		}

		q := strings.TrimSpace(r.URL.Query().Get("q"))
		if q == "" {
			writeJSON(w, 200, map[string]any{"ok": true, "q": "", "message": "missing q"})
			return
		}

		resp := map[string]any{
			"ok": true,
			"q":  q,
			"embyTitleNormalize": map[string]any{
				"tv": embyNormalizeTitleForTMDB("tv", q),
			},
		}

		if magic.RegexAvailable() {
			rawCleanRules, _ := database.ListMagicEpisodeCleanRegexRules()
			rawEpisodeRules, _ := database.ListMagicEpisodeRules()
			if jsRes, err := magic.MagicEpisodeDebug(q, rawCleanRules, rawEpisodeRules); err == nil && jsRes != nil {
				resp["jsMagicEpisode"] = jsRes
				if s, e, ok := regexDebugExtractSE(jsRes); ok {
					resp["extracted"] = map[string]any{"season": s, "episode": e}
				}
			} else if err != nil {
				resp["jsMagicEpisodeError"] = err.Error()
			}
		}

		if regexDebugWantsHTML(r) {
			regexDebugWriteHTML(w, "searchdebug", resp)
			return
		}
		writeJSON(w, 200, resp)
	})
}
