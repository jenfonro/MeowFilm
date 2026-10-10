package catpawrunner

import (
	"crypto/sha256"
	"encoding/json"
	"errors"
	"fmt"
	"math"
	"regexp"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"time"
)

// Navigation describes only how to continue the current script. It is not a
// playback/cache protocol and never requires decoding a script-private ID.
type Navigation struct {
	Action    string
	Payload   map[string]any
	Provider  string
	ShareURL  string
	ShareFlag string
}

var navigationActionPattern = regexp.MustCompile(`(?i)^[a-z][a-z0-9_-]*$`)

func DetailNavigation(item map[string]any) (Navigation, bool) {
	if item == nil || anyToString(item["vod_id"]) == "" ||
		anyToString(item["vod_play_from"]) != "" || anyToString(item["vod_play_url"]) != "" {
		return Navigation{}, false
	}
	desc, _ := item["vod_navigation"].(map[string]any)
	action := anyToString(desc["action"])
	if action == "" && anyToString(item["vod_tag"]) == "folder" {
		action = "category"
	}
	if !navigationActionPattern.MatchString(action) {
		return Navigation{}, false
	}
	args, ok := desc["payload"].(map[string]any)
	payload := make(map[string]any)
	if ok {
		for key, value := range args {
			payload[key] = value
		}
	} else {
		payload["id"] = item["vod_id"]
		if action == "category" {
			payload["page"] = 1
		}
	}
	return Navigation{action, payload, anyToString(desc["provider"]),
		anyToString(desc["share_url"]), anyToString(desc["share_flag"])}, true
}

func HasDetailNavigation(raw map[string]any) bool {
	list, _ := raw["list"].([]any)
	for _, value := range list {
		item, _ := value.(map[string]any)
		if _, ok := DetailNavigation(item); ok || item["vod_navigation"] != nil {
			return true
		}
	}
	return false
}

func navigationKey(action string, payload map[string]any) string {
	data, _ := json.Marshal(payload) // encoding/json sorts map keys.
	return action + ":" + string(data)
}

type NavigationOptions struct {
	// Browsing can fetch independent branches together. Smart playback can
	// request the next branch only after its existing matching/play callback.
	Sequential bool
	ShouldStop func() bool
	Order      func([]any) []any
}

type navigationWalkContext struct {
	Navigation
	PreviousPage int64
	PageLists    map[[32]byte]bool
}

// WalkDetailNavigation passes each leaf to existing consumers. A true return
// from visit stops new dispatch; the walker never invents a success criterion.
func WalkDetailNavigation(raw map[string]any,
	request func(string, map[string]any) (map[string]any, error),
	visit func(map[string]any) bool, options NavigationOptions,
) error {
	var mu sync.Mutex
	seen, shares := map[string]bool{}, map[string]bool{}
	var failures []error
	var done atomic.Bool
	stopped := func() bool { return done.Load() || (options.ShouldStop != nil && options.ShouldStop()) }
	report := func(item any, message string) {
		name := ""
		if m, ok := item.(map[string]any); ok {
			name = anyToString(m["vod_name"])
		}
		mu.Lock()
		failures = append(failures, fmt.Errorf("%s%s", func() string {
			if name != "" {
				return name + ": "
			}
			return ""
		}(), message))
		mu.Unlock()
	}
	claim := func(set map[string]bool, key string) bool {
		mu.Lock()
		defer mu.Unlock()
		if set[key] {
			return false
		}
		set[key] = true
		return true
	}
	var walk func(map[string]any, map[string]bool, bool, *navigationWalkContext)
	walk = func(doc map[string]any, ancestors map[string]bool, inherited bool, context *navigationWalkContext) {
		if stopped() {
			return
		}
		mode, ok := doc["pan_mock"].(bool)
		if !ok {
			mode = inherited
		}
		message := strings.TrimSpace(anyToString(doc["message"]))
		if message == "" {
			message = strings.TrimSpace(anyToString(doc["msg"]))
		}
		if failure, ok := doc["ok"].(bool); ok && !failure {
			if message == "" {
				message = "导航请求失败"
			}
			report(doc, message)
			return
		}
		if context != nil && mode != inherited {
			report(doc, "网盘解析模式已变更，请刷新详情")
			return
		}
		list, ok := doc["list"].([]any)
		if !ok {
			report(doc, "未支持的详情结构")
			return
		}
		if message != "" {
			report(doc, message)
		}
		page, count := navigationPage(doc["page"]), navigationPage(doc["pagecount"])
		if context != nil && (context.PreviousPage > 0 || (page > 0 && count > page)) {
			if context.PreviousPage > 0 && page <= context.PreviousPage {
				report(doc, "导航分页页码未前进")
				return
			}
			if len(list) > 0 {
				encoded, _ := json.Marshal(list)
				key := sha256.Sum256(encoded)
				if context.PageLists[key] {
					report(doc, "导航分页重复返回相同数据")
					return
				}
				context.PageLists[key] = true
			}
		}
		if options.Order != nil {
			list = options.Order(append([]any(nil), list...))
		}
		handle := func(value any) {
			if stopped() {
				return
			}
			item, ok := value.(map[string]any)
			if !ok {
				report(value, "未支持的详情项")
				return
			}
			nav, ok := DetailNavigation(item)
			if !ok {
				if anyToString(item["vod_play_from"]) == "" && anyToString(item["vod_play_url"]) == "" {
					report(item, "未支持的详情结构")
					return
				}
				if visit(map[string]any{"list": []any{item}, "pan_mock": mode}) {
					done.Store(true)
				}
				return
			}
			key := navigationKey(nav.Action, nav.Payload)
			if ancestors[key] {
				report(item, "详情导航循环引用")
				return
			}
			if !claim(seen, key) {
				return
			}
			lineage := make(map[string]bool, len(ancestors)+1)
			for k, v := range ancestors {
				lineage[k] = v
			}
			lineage[key] = true
			if share, supported := ParsePanShareInput(nav.ShareFlag, nav.ShareURL); supported {
				if !claim(shares, share.Key()) {
					return
				}
				if mode {
					leaf := make(map[string]any, len(item)+2)
					for k, v := range item {
						if k != "vod_navigation" {
							leaf[k] = v
						}
					}
					leaf["vod_play_from"], leaf["vod_play_url"] = nav.ShareFlag, nav.ShareURL
					if visit(map[string]any{"list": []any{leaf}, "pan_mock": true}) {
						done.Store(true)
					}
					return
				}
			}
			if stopped() {
				return
			}
			child, err := request(nav.Action, nav.Payload)
			if err != nil {
				report(item, err.Error())
				return
			}
			walk(child, lineage, mode, &navigationWalkContext{Navigation: nav, PageLists: map[[32]byte]bool{}})
		}
		if options.Sequential {
			for _, item := range list {
				handle(item)
				if stopped() {
					break
				}
			}
		} else {
			var wg sync.WaitGroup
			for _, item := range list {
				wg.Add(1)
				go func(value any) { defer wg.Done(); handle(value) }(item)
			}
			wg.Wait()
		}
		// Page sequentially, preserving the script operation and parameters.
		// Never fan out a possibly sentinel-sized pagecount into goroutines.
		if context != nil && !stopped() && len(list) > 0 && page > 0 && count > page {
			next := *context
			next.Payload = make(map[string]any, len(context.Payload)+1)
			for k, v := range context.Payload {
				next.Payload[k] = v
			}
			next.PreviousPage = page
			_, hasPage := next.Payload["page"]
			_, hasPG := next.Payload["pg"]
			if hasPG {
				next.Payload["pg"] = page + 1
			}
			if hasPage || !hasPG {
				next.Payload["page"] = page + 1
			}
			key := navigationKey(next.Action, next.Payload)
			if claim(seen, key) {
				child, err := request(next.Action, next.Payload)
				if err != nil {
					report(doc, err.Error())
				} else {
					lineage := make(map[string]bool, len(ancestors)+1)
					for k, v := range ancestors {
						lineage[k] = v
					}
					lineage[key] = true
					walk(child, lineage, mode, &next)
				}
			}
		}
	}
	walk(raw, map[string]bool{}, false, nil)
	return errors.Join(failures...)
}

func navigationPage(value any) int64 {
	var number float64
	switch v := value.(type) {
	case float64:
		number = v
	case int:
		number = float64(v)
	case int64:
		number = float64(v)
	case json.Number:
		number, _ = v.Float64()
	case string:
		number, _ = strconv.ParseFloat(strings.TrimSpace(v), 64)
	}
	// Match JSON/JavaScript safe integers, not an arbitrary traversal limit.
	if number > 0 && number <= 9007199254740991 && math.Trunc(number) == number {
		return int64(number)
	}
	return 0
}

// ResolveDetailNavigation is a browse edge adapter, not a new cached wire
// response. Its complete leaves feed the existing play-field consumer.
func ResolveDetailNavigation(apiBase, spiderAPI string, raw map[string]any, timeout time.Duration) (map[string]any, error) {
	if !HasDetailNavigation(raw) {
		return raw, nil
	}
	var mu sync.Mutex
	var froms, urls []string
	err := WalkDetailNavigation(raw, func(action string, payload map[string]any) (map[string]any, error) {
		return RequestSpiderWithTimeout(apiBase, spiderAPI, action, payload, timeout)
	}, func(leaf map[string]any) bool {
		from, url := ExtractDetailPlayFromURL(leaf)
		if from != "" || url != "" {
			mu.Lock()
			froms, urls = append(froms, from), append(urls, url)
			mu.Unlock()
		}
		return false
	}, NavigationOptions{})
	out := make(map[string]any, len(raw)+1)
	for k, v := range raw {
		out[k] = v
	}
	if len(froms) > 0 || len(urls) > 0 {
		out["list"] = []any{map[string]any{"vod_play_from": strings.Join(froms, "$$$"), "vod_play_url": strings.Join(urls, "$$$")}}
	}
	if err != nil {
		out["message"] = err.Error()
	}
	return out, err
}
