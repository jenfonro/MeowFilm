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

func TestNavigationKeepsEnrichedShareCredentials(t *testing.T) {
	for _, mode := range []bool{true, false} {
		for _, sameID := range []bool{true, false} {
			// The local parser uses the share data, even if a script keeps its
			// opaque card ID unchanged while supplying the missing password.
			if sameID && !mode {
				continue
			}
			t.Run(fmt.Sprintf("mock=%v/sameID=%v", mode, sameID), func(t *testing.T) {
				card := func(id, pass string) map[string]any {
					flag, url := "夸克-protectedShare", "https://pan.quark.cn/s/protectedShare"
					if pass != "" {
						flag, url = flag+"-"+pass, url+"?pwd="+pass
					}
					if sameID {
						id = "same-card"
					}
					return navTestItem(id, "detail", map[string]any{
						"provider": "quark", "share_flag": flag, "share_url": url,
					})
				}
				doc := func(items ...any) map[string]any { return map[string]any{"pan_mock": mode, "list": items} }
				root := doc(navTestItem("groupA", "category", nil), navTestItem("groupB", "category", nil),
					navTestItem("groupC", "category", nil))
				var attempted []string
				resolved := false
				_ = WalkDetailNavigation(root, func(_ string, payload map[string]any) (map[string]any, error) {
					id := payload["id"].(string)
					switch id {
					case "groupA":
						return doc(card("without-pass", "")), nil
					case "groupB":
						return doc(card("with-pass", "1234")), nil
					case "groupC":
						return doc(card("duplicate-pass", "1234")), nil
					}
					attempted = append(attempted, id)
					if id == "without-pass" {
						return nil, fmt.Errorf("password required")
					}
					return doc(map[string]any{"vod_play_from": "夸克-protectedShare-1234", "vod_play_url": "E1$complete"}), nil
				}, func(leaf map[string]any) bool {
					if !mode {
						resolved = true
						return false
					}
					from, url := ExtractDetailPlayFromURL(leaf)
					share, _ := ParsePanShareInput(from, url)
					attempted = append(attempted, share.Passcode)
					resolved = resolved || share.Passcode == "1234"
					return false
				}, NavigationOptions{Sequential: true})
				want := []string{"without-pass", "with-pass"}
				if mode {
					want = []string{"", "1234"}
				}
				if !resolved || !reflect.DeepEqual(attempted, want) {
					t.Fatalf("later credentials lost or identical input repeated: resolved=%v attempted=%q", resolved, attempted)
				}
			})
		}
	}
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

func TestNavigationSerializesCategoriesButNotShareResolution(t *testing.T) {
	for _, mode := range []bool{false, true} {
		t.Run(fmt.Sprint(mode), func(t *testing.T) {
			root := navTestDoc(navTestItem("groupA", "category", nil), navTestItem("groupB", "category", nil))
			root["pan_mock"] = mode
			var mu sync.Mutex
			var calls []string
			release, started := make(chan struct{}), make(chan string, 4)
			var once sync.Once
			unblock := func() { once.Do(func() { close(release) }) }
			defer unblock()
			record := func(id string) {
				mu.Lock()
				calls = append(calls, id)
				mu.Unlock()
			}
			resolveShare := func(id string) {
				record(id)
				started <- id
				if id == "shareA" {
					<-release
				}
			}
			done := make(chan error, 1)
			go func() {
				done <- WalkDetailNavigation(root, func(action string, payload map[string]any) (map[string]any, error) {
					id := payload["id"].(string)
					if action == "category" {
						record(id)
						if id == "groupB" {
							doc := navTestLeaf("native")
							doc["pan_mock"] = mode
							return doc, nil
						}
						items := []any{}
						for _, shareID := range []string{"shareA", "shareB"} {
							items = append(items, navTestItem(shareID, "detail", map[string]any{
								"provider": "quark", "share_flag": "夸克-" + shareID, "share_url": "https://pan.quark.cn/s/" + shareID,
							}))
						}
						doc := navTestDoc(items...)
						doc["pan_mock"] = mode
						return doc, nil
					}
					if mode {
						t.Error("a local share called script detail")
					}
					resolveShare(id)
					return navTestLeaf(id), nil
				}, func(doc map[string]any) bool {
					if mode {
						_, url := ExtractDetailPlayFromURL(doc)
						if strings.HasPrefix(url, "https://pan.quark.cn/s/") {
							resolveShare(strings.TrimPrefix(url, "https://pan.quark.cn/s/"))
						}
					}
					return false
				}, NavigationOptions{Sequential: true})
			}()
			got := map[string]bool{}
			for len(got) < 2 {
				select {
				case id := <-started:
					got[id] = true
				case <-time.After(2 * time.Second):
					unblock()
					<-done
					t.Fatal("same-provider shares were serialized under category navigation")
				}
			}
			mu.Lock()
			before := append([]string(nil), calls...)
			mu.Unlock()
			if strings.Contains(strings.Join(before, ","), "groupB") {
				t.Errorf("site advanced to another category before this group's lists completed: %v", before)
			}
			unblock()
			if err := <-done; err != nil {
				t.Fatal(err)
			}
			mu.Lock()
			defer mu.Unlock()
			if len(calls) != 4 || calls[0] != "groupA" || calls[3] != "groupB" {
				t.Fatalf("unexpected category/share dispatch: %v", calls)
			}
		})
	}
}

func TestNavigationStopsBeforeTheNextCategory(t *testing.T) {
	for _, mode := range []bool{false, true} {
		items := []any{}
		for _, id := range []string{"shareA", "shareB", "shareC"} {
			items = append(items, navTestItem("group-"+id, "category", nil))
		}
		root := navTestDoc(items...)
		root["pan_mock"] = mode
		calls, visits := []string{}, 0
		err := WalkDetailNavigation(root, func(action string, payload map[string]any) (map[string]any, error) {
			id := payload["id"].(string)
			calls = append(calls, id)
			if action == "category" {
				id = strings.TrimPrefix(id, "group-")
				doc := navTestDoc(navTestItem(id, "detail", map[string]any{
					"provider": "quark", "share_flag": "夸克-" + id, "share_url": "https://pan.quark.cn/s/" + id,
				}))
				doc["pan_mock"] = mode
				return doc, nil
			}
			if mode {
				t.Error("local share invoked script private detail")
			}
			return navTestLeaf(id), nil
		}, func(doc map[string]any) bool {
			visits++
			if mode {
				from, url := ExtractDetailPlayFromURL(doc)
				if !strings.HasPrefix(from, "夸克-share") || !strings.HasPrefix(url, "https://pan.quark.cn/") {
					t.Error("local list did not get the existing flag/URL representation")
				}
			}
			return visits == 2 // first leaf rejected; caller accepts the second
		}, NavigationOptions{Sequential: true})
		if err != nil || visits != 2 {
			t.Fatalf("demand traversal: visits=%d err=%v", visits, err)
		}
		want := []string{"group-shareA", "shareA", "group-shareB", "shareB"}
		if mode {
			want = []string{"group-shareA", "group-shareB"}
		}
		if !reflect.DeepEqual(calls, want) {
			t.Fatalf("unneeded category dispatched: %v", calls)
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
