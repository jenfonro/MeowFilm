//go:build cgo && (linux || darwin)
// +build cgo
// +build linux darwin

package magic

import (
	"crypto/sha1"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"strings"
	"sync"

	"github.com/buke/quickjs-go"
	"github.com/jenfonro/meowfilm/internal/db"
)

func RegexAvailable() bool { return true }

func jsEval(ctx *quickjs.Context, code string) (*quickjs.Value, error) {
	if ctx == nil {
		return nil, errors.New("quickjs ctx nil")
	}
	val := ctx.Eval(code)
	if val == nil {
		if ctx.HasException() {
			return nil, ctx.Exception()
		}
		return nil, nil
	}
	if val.IsException() {
		err := ctx.Exception()
		val.Free()
		return nil, err
	}
	return val, nil
}

func jsEvalVoid(ctx *quickjs.Context, code string) error {
	val, err := jsEval(ctx, code)
	if val != nil {
		val.Free()
	}
	return err
}

type jsMagicEngine struct {
	rt       *quickjs.Runtime
	ctx      *quickjs.Context
	rulesKey string
}

var jsMagicEnginePool = sync.Pool{
	New: func() any { return &jsMagicEngine{} },
}

func jsRulesKey(cleanRaw []string, episodeRaw []string) string {
	h := sha1.New()
	for _, s := range cleanRaw {
		h.Write([]byte(s))
		h.Write([]byte{0})
	}
	h.Write([]byte{1})
	for _, s := range episodeRaw {
		h.Write([]byte(s))
		h.Write([]byte{0})
	}
	return hex.EncodeToString(h.Sum(nil))
}

func (e *jsMagicEngine) close() {
	if e == nil {
		return
	}
	if e.ctx != nil {
		e.ctx.Close()
		e.ctx = nil
	}
	if e.rt != nil {
		e.rt.Close()
		e.rt = nil
	}
	e.rulesKey = ""
}

func (e *jsMagicEngine) ensureRules(cleanRaw []string, episodeRaw []string) error {
	if e == nil {
		return errors.New("engine nil")
	}
	key := jsRulesKey(cleanRaw, episodeRaw)
	if e.ctx != nil && e.rt != nil && e.rulesKey == key {
		return nil
	}
	e.close()

	e.rt = quickjs.NewRuntime()
	e.ctx = e.rt.NewContext()
	e.rulesKey = key

	cleanRules := make([]jsRule, 0, len(cleanRaw))
	for _, row := range cleanRaw {
		r := jsDecodeRule(row, true)
		if strings.TrimSpace(r.Pattern) == "" {
			continue
		}
		cleanRules = append(cleanRules, r)
	}
	episodeRules := make([]jsRule, 0, len(episodeRaw))
	for _, row := range episodeRaw {
		r := jsDecodeRule(row, false)
		if strings.TrimSpace(r.Pattern) == "" {
			continue
		}
		episodeRules = append(episodeRules, r)
	}

	if err := jsEvalVoid(e.ctx, `
			globalThis.__mf_set_rules = function(cleanRules, episodeRules) {
				globalThis.__mf_clean = [];
				for (let i=0;i<cleanRules.length;i++) {
					const r = cleanRules[i] || {};
				let flags = String(r.flags || "");
				if (!flags.includes("g")) flags += "g";
				__mf_clean.push({ re: new RegExp(String(r.pattern||""), flags), replace: String(r.replace||"") });
			}
			globalThis.__mf_ep = [];
			for (let i=0;i<episodeRules.length;i++) {
				const r = episodeRules[i] || {};
				let flags = String(r.flags || "");
				flags = flags.replace(/g/g, "");
				__mf_ep.push({ re: new RegExp(String(r.pattern||""), flags), replace: String(r.replace||"") });
			}
		};

		globalThis.__mf_clean_text = function(text) {
			let out = String(text || "");
			for (let i=0;i<__mf_clean.length;i++) {
				const r = __mf_clean[i];
				out = out.replace(r.re, r.replace);
			}
			return out.replace(/\s+/g, " ").trim();
		};

		globalThis.__mf_extract_one = function(text) {
			const cleaned = __mf_clean_text(text);
			for (let i=0;i<__mf_ep.length;i++) {
				const r = __mf_ep[i];
				const m = r.re.exec(cleaned);
				if (!m) continue;
				let normalized = cleaned;
				if (r.replace) normalized = normalized.replace(r.re, r.replace);
				const mm = /(?:S(\d{1,2}))?\s*E(\d{1,5})/i.exec(normalized);
				if (mm && mm[2]) {
					const season = mm[1] ? parseInt(mm[1], 10) : 0;
					const episode = parseInt(mm[2], 10);
					if (!Number.isNaN(episode)) return { season: season || 0, episode: episode || 0 };
				}
			}
			return { season: 0, episode: 0 };
		};

			globalThis.__mf_extract_from_candidates = function(cands) {
				for (let i=0;i<cands.length;i++) {
					const res = __mf_extract_one(cands[i]);
					if (res && res.episode > 0) return res;
				}
				return { season: 0, episode: 0 };
			};
		`); err != nil {
		e.close()
		return err
	}

	if err := jsEvalVoid(e.ctx, fmt.Sprintf(`__mf_set_rules(%s,%s)`, marshalJSON(cleanRules), marshalJSON(episodeRules))); err != nil {
		e.close()
		return err
	}
	return nil
}

func MagicEpisodeExtractFromCandidates(candidates []string, cleanRaw []string, episodeRaw []string) (SeasonEpisode, error) {
	if len(candidates) == 0 {
		return SeasonEpisode{Season: 0, Episode: 0}, nil
	}
	engine := jsMagicEnginePool.Get().(*jsMagicEngine)
	defer jsMagicEnginePool.Put(engine)

	if err := engine.ensureRules(cleanRaw, episodeRaw); err != nil {
		engine.close()
		return SeasonEpisode{Season: 0, Episode: 0}, err
	}

	val, err := jsEval(engine.ctx, fmt.Sprintf(`JSON.stringify(__mf_extract_from_candidates(%s))`, marshalJSON(candidates)))
	if err != nil {
		engine.close()
		return SeasonEpisode{Season: 0, Episode: 0}, err
	}
	defer val.Free()

	raw := strings.TrimSpace(val.String())
	if raw == "" {
		return SeasonEpisode{Season: 0, Episode: 0}, nil
	}
	var obj struct {
		Season  int `json:"season"`
		Episode int `json:"episode"`
	}
	if err := json.Unmarshal([]byte(raw), &obj); err != nil {
		return SeasonEpisode{Season: 0, Episode: 0}, err
	}
	return SeasonEpisode{Season: obj.Season, Episode: obj.Episode}, nil
}

func CompileRulesDebug(database *db.DB) (any, error) {
	if database == nil {
		return nil, errors.New("db nil")
	}
	rawEpisodeRules, _ := database.ListMagicEpisodeRules()
	rawCleanRules, _ := database.ListMagicEpisodeCleanRegexRules()
	rawMovieRules, _ := database.ListMagicMovieRules()
	rawAggRules, _ := database.ListMagicAggregateRegexRules()

	type jsCompileInfo struct {
		Raw     string `json:"raw"`
		Pattern string `json:"pattern"`
		Flags   string `json:"flags"`
		Error   string `json:"error,omitempty"`
	}

	rt := quickjs.NewRuntime()
	defer rt.Close()
	ctx := rt.NewContext()
	defer ctx.Close()

	// helper: compile regexp, return error string or empty.
	if err := jsEvalVoid(ctx, `
			globalThis.__mf_compile = function (pattern, flags) {
				try { new RegExp(pattern, flags); return ""; } catch (e) { return String(e); }
			};
		`); err != nil {
		return nil, err
	}

	callCompile := func(pat string, flags string) (string, error) {
		p := strings.TrimSpace(pat)
		f := strings.TrimSpace(flags)
		if f == "" {
			f = "i"
		}
		// invoke: __mf_compile(pattern, flags)
		val, err := jsEval(ctx, fmt.Sprintf(`__mf_compile(%s,%s)`, marshalJSON(p), marshalJSON(f)))
		if err != nil {
			return "", err
		}
		defer val.Free()
		return strings.TrimSpace(val.String()), nil
	}

	outEpisode := make([]jsCompileInfo, 0, len(rawEpisodeRules))
	for _, row := range rawEpisodeRules {
		r := jsDecodeRule(row, false)
		msg, e := callCompile(r.Pattern, r.Flags)
		if e != nil {
			return nil, e
		}
		it := jsCompileInfo{Raw: row, Pattern: r.Pattern, Flags: r.Flags}
		if msg != "" {
			it.Error = msg
		}
		outEpisode = append(outEpisode, it)
	}

	outClean := make([]jsCompileInfo, 0, len(rawCleanRules))
	for _, row := range rawCleanRules {
		r := jsDecodeRule(row, true)
		msg, e := callCompile(r.Pattern, r.Flags)
		if e != nil {
			return nil, e
		}
		it := jsCompileInfo{Raw: row, Pattern: r.Pattern, Flags: r.Flags}
		if msg != "" {
			it.Error = msg
		}
		outClean = append(outClean, it)
	}

	compilePlain := func(row string) (jsCompileInfo, error) {
		// Movie/aggregate rules are usually plain patterns, but may be stored as {"pattern":...}.
		pat, _, flags := DecodeEpisodeRule(row)
		if strings.TrimSpace(pat) == "" {
			pat = strings.TrimSpace(row)
		}
		if strings.TrimSpace(flags) == "" {
			flags = "i"
		}
		msg, e := callCompile(pat, flags)
		if e != nil {
			return jsCompileInfo{}, e
		}
		it := jsCompileInfo{Raw: row, Pattern: pat, Flags: flags}
		if msg != "" {
			it.Error = msg
		}
		return it, nil
	}

	outMovie := make([]jsCompileInfo, 0, len(rawMovieRules))
	for _, row := range rawMovieRules {
		it, e := compilePlain(row)
		if e != nil {
			return nil, e
		}
		outMovie = append(outMovie, it)
	}
	outAgg := make([]jsCompileInfo, 0, len(rawAggRules))
	for _, row := range rawAggRules {
		it, e := compilePlain(row)
		if e != nil {
			return nil, e
		}
		outAgg = append(outAgg, it)
	}

	return map[string]any{
		"engine": "quickjs",
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
	cleanRules := make([]jsRule, 0, len(cleanRaw))
	for _, row := range cleanRaw {
		r := jsDecodeRule(row, true)
		if strings.TrimSpace(r.Pattern) == "" {
			continue
		}
		cleanRules = append(cleanRules, r)
	}
	episodeRules := make([]jsRule, 0, len(episodeRaw))
	for _, row := range episodeRaw {
		r := jsDecodeRule(row, false)
		if strings.TrimSpace(r.Pattern) == "" {
			continue
		}
		// For debug exec, avoid global to keep exec() stable.
		r.Flags = strings.ReplaceAll(r.Flags, "g", "")
		episodeRules = append(episodeRules, r)
	}

	rt := quickjs.NewRuntime()
	defer rt.Close()
	ctx := rt.NewContext()
	defer ctx.Close()

	// Core helpers.
	if err := jsEvalVoid(ctx, `
			globalThis.__mf_apply_clean = function (text, rules) {
				let out = String(text || "");
				const steps = [];
				for (let i = 0; i < rules.length; i++) {
				const r = rules[i] || {};
				let flags = String(r.flags || "");
				if (!flags.includes("g")) flags += "g";
				let before = out;
				try {
					const re = new RegExp(String(r.pattern || ""), flags);
					out = out.replace(re, String(r.replace || ""));
					steps.push({ index: i, pattern: String(r.pattern || ""), flags, replace: String(r.replace || ""), before, after: out });
				} catch (e) {
					steps.push({ index: i, pattern: String(r.pattern || ""), flags, replace: String(r.replace || ""), before, after: before, error: String(e) });
				}
			}
			out = out.replace(/\s+/g, " ").trim();
			return { cleaned: out, steps };
		};

		globalThis.__mf_rule_matches = function (text, rules) {
			const out = [];
			for (let i = 0; i < rules.length; i++) {
				const r = rules[i] || {};
				const pattern = String(r.pattern || "");
				const flags = String(r.flags || "");
				const replace = String(r.replace || "");
				let matched = false;
				let submatch = null;
				let after = String(text || "");
				try {
					const re = new RegExp(pattern, flags);
					const m = re.exec(String(text || ""));
					if (m) {
						matched = true;
						submatch = Array.from(m);
						if (replace) after = String(text || "").replace(re, replace);
					}
					out.push({ index: i, pattern, flags, replace, matched, submatch, before: String(text || ""), after });
				} catch (e) {
					out.push({ index: i, pattern, flags, replace, matched: false, submatch: null, before: String(text || ""), after: String(text || ""), error: String(e) });
				}
			}
			return out;
		};

		globalThis.__mf_extract_se = function (cleaned, rules) {
			for (let i = 0; i < rules.length; i++) {
				const r = rules[i] || {};
				const pattern = String(r.pattern || "");
				const flags = String(r.flags || "");
				const replace = String(r.replace || "");
				try {
					const re = new RegExp(pattern, flags);
					const m = re.exec(String(cleaned || ""));
					if (!m) continue;
					let normalized = String(cleaned || "");
					if (replace) normalized = normalized.replace(re, replace);
					const mm = /(?:S(\d{1,2}))?\s*E(\d{1,5})/i.exec(normalized);
					if (mm && mm[2]) {
						const season = mm[1] ? parseInt(mm[1], 10) : 0;
						const episode = parseInt(mm[2], 10);
						if (!Number.isNaN(episode)) return { season: season || 0, episode, normalized, ruleIndex: i };
					}
				} catch (_e) {}
			}
				return { season: 0, episode: 0 };
			};
		`); err != nil {
		return nil, err
	}

	// Inject data.
	if err := jsEvalVoid(ctx, fmt.Sprintf("globalThis.__mf_text=%s;", marshalJSON(text))); err != nil {
		return nil, err
	}
	if err := jsEvalVoid(ctx, fmt.Sprintf("globalThis.__mf_clean_rules=%s;", marshalJSON(cleanRules))); err != nil {
		return nil, err
	}
	if err := jsEvalVoid(ctx, fmt.Sprintf("globalThis.__mf_episode_rules=%s;", marshalJSON(episodeRules))); err != nil {
		return nil, err
	}

	// Run and stringify results to avoid depending on quickjs-go value marshaling helpers.
	applyVal, err := jsEval(ctx, `JSON.stringify(__mf_apply_clean(__mf_text, __mf_clean_rules))`)
	if err != nil {
		return nil, err
	}
	defer applyVal.Free()
	applyJSON := []byte(applyVal.String())

	// Extract cleaned string by quick parse in Go too (safe).
	type applied struct {
		Cleaned string `json:"cleaned"`
	}
	var ap applied
	_ = json.Unmarshal(applyJSON, &ap)

	matchesVal, err := jsEval(ctx, `JSON.stringify(__mf_rule_matches(__mf_apply_clean(__mf_text, __mf_clean_rules).cleaned, __mf_episode_rules))`)
	if err != nil {
		return nil, err
	}
	defer matchesVal.Free()
	matchesJSON := []byte(matchesVal.String())

	extractVal, err := jsEval(ctx, `JSON.stringify(__mf_extract_se(__mf_apply_clean(__mf_text, __mf_clean_rules).cleaned, __mf_episode_rules))`)
	if err != nil {
		return nil, err
	}
	defer extractVal.Free()
	extractJSON := []byte(extractVal.String())

	return map[string]any{
		"engine":  "quickjs",
		"cleaned": ap.Cleaned,
		"cleanSteps": func() any {
			var v any
			_ = json.Unmarshal(applyJSON, &v)
			if m, ok := v.(map[string]any); ok {
				return m["steps"]
			}
			return nil
		}(),
		"ruleMatches": func() any {
			var v any
			_ = json.Unmarshal(matchesJSON, &v)
			return v
		}(),
		"extracted": func() any {
			var v any
			_ = json.Unmarshal(extractJSON, &v)
			return v
		}(),
	}, nil
}
