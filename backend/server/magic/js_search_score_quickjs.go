//go:build cgo
// +build cgo

package magic

import (
	"encoding/json"
	"errors"
	"fmt"
	"strings"
	"sync"

	"github.com/buke/quickjs-go"
)

type jsSearchScoreEngine struct {
	rt  *quickjs.Runtime
	ctx *quickjs.Context
}

var jsSearchScorePool = sync.Pool{
	New: func() any { return &jsSearchScoreEngine{} },
}

func (e *jsSearchScoreEngine) close() {
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
}

func (e *jsSearchScoreEngine) ensure() error {
	if e == nil {
		return errors.New("engine nil")
	}
	if e.rt != nil && e.ctx != nil {
		return nil
	}
	e.close()
	e.rt = quickjs.NewRuntime()
	e.ctx = e.rt.NewContext()

	// Ported from MeowFilm-Frontend/src/shared/searchClient.js:
	// - normalizeForMatch
	// - computeMatchScore
	return jsEvalVoid(e.ctx, `
		globalThis.__mf_normalize_for_match = function(s) {
			return String(s || "")
				.toLowerCase()
				.replace(/[\s\u200b\u200c\u200d\ufeff]+/g, "")
				.trim();
		};

		globalThis.__mf_compute_match_score = function(q, title) {
			const qRaw = String(q || "").trim();
			const qNorm = __mf_normalize_for_match(qRaw);
			const name = __mf_normalize_for_match(title);
			if (!qNorm || !name) return 0;
			if (name === qNorm) return 1000;
			if (name.startsWith(qNorm)) return 900;
			const idx = name.indexOf(qNorm);
			if (idx >= 0) {
				const posBoost = 60 - Math.min(60, idx);
				const lenBoost = 40 - Math.min(40, Math.max(0, name.length - qNorm.length));
				return 800 + posBoost + lenBoost;
			}
			const tokens = qRaw
				.toLowerCase()
				.split(/\s+/g)
				.map((t) => t.trim())
				.filter(Boolean);
			if (tokens.length >= 2) {
				let hit = 0;
				tokens.forEach((t) => {
					if (t && name.includes(t)) hit += 1;
				});
				if (hit) return 600 + hit * 20;
			}
			return 0;
		};
	`)
}

func ComputeMatchScore(q string, title string) (int, error) {
	engine := jsSearchScorePool.Get().(*jsSearchScoreEngine)
	defer jsSearchScorePool.Put(engine)

	if err := engine.ensure(); err != nil {
		engine.close()
		return 0, err
	}
	qq := strings.TrimSpace(q)
	if qq == "" {
		return 0, nil
	}

	val, err := jsEval(engine.ctx, fmt.Sprintf(`JSON.stringify(__mf_compute_match_score(%s,%s))`, marshalJSON(qq), marshalJSON(title)))
	if err != nil {
		engine.close()
		return 0, err
	}
	defer val.Free()

	raw := strings.TrimSpace(val.String())
	if raw == "" {
		return 0, nil
	}
	var n int
	if err := json.Unmarshal([]byte(raw), &n); err != nil {
		return 0, err
	}
	if n < 0 {
		n = 0
	}
	return n, nil
}
