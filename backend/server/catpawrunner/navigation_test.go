package catpawrunner

import (
	"encoding/json"
	"fmt"
	"reflect"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"
)

func navTestItem(id, action string, extra map[string]any) map[string]any {
	desc := map[string]any{"action": action, "payload": map[string]any{"id": id}}
	for k, v := range extra {
		desc[k] = v
	}
	return map[string]any{"vod_id": id, "vod_name": id, "vod_navigation": desc}
}
func navTestDoc(items ...any) map[string]any { return map[string]any{"list": items, "pan_mock": false} }
func navTestLeaf(id string) map[string]any {
	return navTestDoc(map[string]any{"vod_play_from": "蓝光HDR", "vod_play_url": "E1$" + id})
}

func TestNavigationFollowsScriptActionsInParallel(t *testing.T) {
	root := navTestDoc(navTestItem("a", "category", nil), navTestItem("b", "resources",
		map[string]any{"payload": map[string]any{"cursor": "unchanged", "page": 1}}))
	before, _ := json.Marshal(root)
	started, release := make(chan string, 2), make(chan struct{})
	var leaves atomic.Int32
	done := make(chan error, 1)
	go func() {
		done <- WalkDetailNavigation(root, func(action string, payload map[string]any) (map[string]any, error) {
			if action == "resources" && payload["cursor"] != "unchanged" {
				t.Error("script payload changed")
			}
			started <- action
			<-release
			return navTestLeaf(action + "*private-id"), nil
		}, func(doc map[string]any) bool {
			from, url := ExtractDetailPlayFromURL(doc)
			if from != "蓝光HDR" || !strings.Contains(url, "*private-id") {
				t.Error("native playback fields changed")
			}
			leaves.Add(1)
			return false
		}, NavigationOptions{})
	}()
	actions := map[string]bool{}
	for i := 0; i < 2; i++ {
		select {
		case action := <-started:
			actions[action] = true
		case <-time.After(2 * time.Second):
			close(release)
			t.Fatal("navigation branches were unnecessarily serialized")
		}
	}
	close(release)
	if err := <-done; err != nil || leaves.Load() != 2 || !actions["resources"] || !actions["category"] {
		t.Fatalf("unexpected traversal: %v %v", actions, err)
	}
	after, _ := json.Marshal(root)
	if string(before) != string(after) {
		t.Fatal("raw navigation mutated")
	}
}

func TestNavigationShareOwnerAndDemandStopping(t *testing.T) {
	for _, mode := range []bool{false, true} {
		items := []any{}
		for _, id := range []string{"shareA", "shareB", "shareC"} {
			items = append(items, navTestItem(id, "detail", map[string]any{
				"provider": "quark", "share_flag": "夸克-" + id, "share_url": "https://pan.quark.cn/s/" + id,
			}))
		}
		root := navTestDoc(items...)
		root["pan_mock"] = mode
		calls, visits := []string{}, 0
		err := WalkDetailNavigation(root, func(action string, payload map[string]any) (map[string]any, error) {
			if mode {
				t.Fatal("local share invoked script private detail")
			}
			id := payload["id"].(string)
			calls = append(calls, id)
			return navTestLeaf(id), nil
		}, func(doc map[string]any) bool {
			visits++
			if mode {
				from, url := ExtractDetailPlayFromURL(doc)
				if !strings.HasPrefix(from, "夸克-share") || !strings.HasPrefix(url, "https://pan.quark.cn/") {
					t.Fatal("local list did not get the existing flag/URL representation")
				}
			}
			return visits == 2 // first leaf rejected; caller accepts the second
		}, NavigationOptions{Sequential: true})
		if err != nil || visits != 2 {
			t.Fatalf("demand traversal: visits=%d err=%v", visits, err)
		}
		if !mode && !reflect.DeepEqual(calls, []string{"shareA", "shareB"}) {
			t.Fatalf("unneeded share dispatched: %v", calls)
		}
	}
}

func TestNavigationMixedUnknownAndCyclesAreReported(t *testing.T) {
	node := navTestItem("same", "category", nil)
	root := navTestDoc(node, nil, map[string]any{"vod_id": "unknown", "custom": true},
		map[string]any{"vod_play_from": "光鸭", "vod_play_url": "E1$opaque"})
	var visits atomic.Int32
	err := WalkDetailNavigation(root, func(string, map[string]any) (map[string]any, error) {
		return navTestDoc(node), nil
	}, func(map[string]any) bool { visits.Add(1); return false }, NavigationOptions{})
	if err == nil || !strings.Contains(err.Error(), "循环") || !strings.Contains(err.Error(), "未支持") || visits.Load() != 1 {
		t.Fatalf("unknown data was hidden: visits=%d err=%v", visits.Load(), err)
	}
	if !HasDetailNavigation(root) {
		t.Fatal("mixed navigation not recognized")
	}
	if HasDetailNavigation(navTestDoc(map[string]any{"vod_id": "opaque", "style": map[string]any{"type": "list"}})) {
		t.Fatal("layout was guessed to be a script endpoint")
	}
}

func TestNavigationDoesNotTruncateAt64AndKeepsPaginationArguments(t *testing.T) {
	items := make([]any, 75)
	for i := range items {
		items[i] = navTestItem(string(rune('a'+i)), "resources", nil)
	}
	var count atomic.Int32
	if err := WalkDetailNavigation(navTestDoc(items...), func(string, map[string]any) (map[string]any, error) {
		return navTestLeaf("native"), nil
	}, func(map[string]any) bool { count.Add(1); return false }, NavigationOptions{}); err != nil || count.Load() != 75 {
		t.Fatalf("truncated: %d %v", count.Load(), err)
	}
	var mu sync.Mutex
	pages := []int{}
	err := WalkDetailNavigation(navTestDoc(navTestItem("pages", "resources",
		map[string]any{"payload": map[string]any{"cursor": "kept", "page": 1}})), func(action string, payload map[string]any) (map[string]any, error) {
		if action != "resources" || payload["cursor"] != "kept" {
			t.Fatal("pagination lost original request")
		}
		page := int(navigationPage(payload["page"]))
		mu.Lock()
		pages = append(pages, page)
		mu.Unlock()
		doc := navTestLeaf(fmt.Sprintf("page-%d", page))
		doc["page"], doc["pagecount"] = page, 3
		return doc, nil
	}, func(map[string]any) bool { return false }, NavigationOptions{})
	if err != nil || !reflect.DeepEqual(pages, []int{1, 2, 3}) {
		t.Fatalf("pagination: %v %v", pages, err)
	}
}

func TestNavigationPaginationStringsSentinelsAndPageKey(t *testing.T) {
	var pages []int64
	root := navTestDoc(navTestItem("pages", "resources", map[string]any{"payload": map[string]any{"id": "pages", "pg": "1"}}))
	err := WalkDetailNavigation(root, func(action string, payload map[string]any) (map[string]any, error) {
		if _, exists := payload["page"]; exists {
			t.Fatal("added unrelated page argument")
		}
		page := navigationPage(payload["pg"])
		pages = append(pages, page)
		doc := navTestLeaf(fmt.Sprintf("page-%d", page))
		doc["page"], doc["pagecount"] = fmt.Sprint(page), "2147483647"
		if page == 3 {
			doc["list"] = []any{}
		}
		return doc, nil
	}, func(map[string]any) bool { return false }, NavigationOptions{})
	if err != nil || !reflect.DeepEqual(pages, []int64{1, 2, 3}) {
		t.Fatalf("pagination: %v %v", pages, err)
	}
}

func TestNavigationPaginationReportsLackOfProgress(t *testing.T) {
	for _, kind := range []string{"number", "data"} {
		var calls, visits int
		err := WalkDetailNavigation(navTestDoc(navTestItem("pages", "category", map[string]any{"payload": map[string]any{"id": "pages", "page": 1}})),
			func(_ string, payload map[string]any) (map[string]any, error) {
				calls++
				doc := navTestLeaf("repeated")
				doc["page"], doc["pagecount"] = payload["page"], 2147483647
				if kind == "number" {
					doc["page"] = 1
				}
				return doc, nil
			}, func(map[string]any) bool { visits++; return false }, NavigationOptions{})
		if calls != 2 || visits != 1 || err == nil || !strings.Contains(err.Error(), "分页") {
			t.Fatalf("%s: %d %d %v", kind, calls, visits, err)
		}
	}
}

func TestNavigationModeChangesAreNotMixed(t *testing.T) {
	err := WalkDetailNavigation(navTestDoc(navTestItem("group", "category", nil)), func(string, map[string]any) (map[string]any, error) {
		doc := navTestLeaf("native")
		doc["pan_mock"] = true
		return doc, nil
	}, func(map[string]any) bool { t.Error("mode-changed leaf consumed"); return false }, NavigationOptions{})
	if err == nil || !strings.Contains(err.Error(), "模式已变更") {
		t.Fatalf("missing refresh diagnostic: %v", err)
	}
}

func TestNavigationBrowsePreservesUnknownRawWhenNoLeafIsSupported(t *testing.T) {
	raw := navTestDoc(map[string]any{"vod_id": "unknown", "vod_navigation": map[string]any{"action": "??"}, "custom": true})
	out, err := ResolveDetailNavigation("", "", raw, time.Second)
	if err == nil || !reflect.DeepEqual(out["list"], raw["list"]) {
		t.Fatalf("unknown structure erased: %v %v", out, err)
	}
}
