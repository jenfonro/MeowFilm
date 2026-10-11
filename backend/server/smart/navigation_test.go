package smart

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"
)

func TestNavigationResolvesGroupSharesConcurrentlyAndUsesExistingMatchAndPlay(t *testing.T) {
	spider := "/0123456789/spider/navigation/3"
	node := func(id, action string, share bool) map[string]any {
		desc := map[string]any{"action": action, "payload": map[string]any{"id": id}, "provider": "quark"}
		if share {
			desc["share_flag"], desc["share_url"] = "夸克-"+id, "https://pan.quark.cn/s/"+id
		}
		return map[string]any{"vod_id": id, "vod_name": id, "vod_navigation": desc}
	}
	var mu sync.Mutex
	var requested, played []string
	var shareRequests atomic.Int32
	allShares := make(chan struct{})
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		var body map[string]any
		_ = json.NewDecoder(r.Body).Decode(&body)
		id, _ := body["id"].(string)
		if r.URL.Path == "/play" {
			mu.Lock()
			played = append(played, id)
			mu.Unlock()
			if body["siteApi"] != spider {
				t.Error("original site API was not preserved for play")
			}
			url := ""
			if strings.HasPrefix(id, "goodShare*") {
				url = "https://media.example/good.mkv"
			}
			_ = json.NewEncoder(w).Encode(map[string]any{"url": url})
			return
		}
		mu.Lock()
		requested = append(requested, id)
		mu.Unlock()
		var list []any
		switch id {
		case "movie":
			list = []any{node("group", "resources", false), node("nextGroup", "category", false)}
		case "group":
			if r.URL.Path != spider+"/resources" {
				t.Error("script navigation action was changed to detail")
			}
			list = []any{node("otherEpisode", "detail", true), node("badShare", "detail", true),
				node("goodShare", "detail", true), node("unneededShare", "detail", true)}
		default:
			if id == "nextGroup" {
				t.Error("requested the next category after successful playback")
				break
			}
			if shareRequests.Add(1) == 4 {
				close(allShares)
			}
			select {
			case <-allShares:
			case <-time.After(2 * time.Second):
				t.Error("same-group share details were serialized")
			}
			episode := "01"
			if id == "otherEpisode" {
				episode = "02"
			}
			list = []any{map[string]any{"vod_play_from": "夸克-" + id,
				"vod_play_url": "File$" + id + "*token*fid*ftoken***Film.S01E" + episode + ".mkv"}}
		}
		_ = json.NewEncoder(w).Encode(map[string]any{"pan_mock": false, "list": list})
	}))
	defer server.Close()
	src := smartSource{SiteKey: t.Name(), SiteDetail: "movie", SpiderAPI: spider}
	seasons := []smartTMDBSeason{{Season: 1, EpisodeCount: 2}}
	clean := []string{`{"pattern":"^$","replace":"","flags":"g"}`}
	key := smartBuildDetailCacheKey(src, seasons, nil, false, "tmdb")
	t.Cleanup(func() {
		smartDetailCache.Lock()
		delete(smartDetailCache.M, key)
		smartDetailCache.Unlock()
	})
	result := smartFetchDetailAndPickAndPlay(nil, server.URL, "test-user", src, seasons, nil,
		false, 1, 1, smartPlaybackSettings{}, clean, episodeNameRules, false, false, "tmdb", nil)
	if result == nil || result.PlayURL != "https://media.example/good.mkv" {
		t.Fatalf("navigation did not use normal matching/play: %+v", result)
	}
	mu.Lock()
	defer mu.Unlock()
	if len(requested) != 6 || requested[0] != "movie" || requested[1] != "group" {
		t.Fatalf("did not resolve all shares in the selected category: %v", requested)
	}
	good := false
	for _, id := range played {
		if strings.HasPrefix(id, "otherEpisode*") {
			t.Fatalf("original episode matching was bypassed: %v", played)
		}
		good = good || strings.HasPrefix(id, "goodShare*")
	}
	if !good {
		t.Fatalf("no matching playable share was selected: %v", played)
	}
	if result.Cand.SiteDetail != "movie" {
		t.Fatal("navigation replaced the original movie identity")
	}
}

func TestNavigationWrapperRetainsPreRequestArgumentChecks(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		t.Error("invalid request reached the Runner")
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte("{}"))
	}))
	defer server.Close()
	for _, src := range []smartSource{
		{SpiderAPI: "/spider/test/3", SiteDetail: "movie"},
		{SiteKey: "site", SiteDetail: "movie"},
		{SiteKey: "site", SpiderAPI: "/spider/test/3"},
	} {
		if got := smartFetchDetailAndPickAndPlay(nil, server.URL, "", src, nil, nil, false, 1, 1, smartPlaybackSettings{}, nil, nil, false, false, "tmdb", nil); got != nil {
			t.Fatal("invalid input accepted")
		}
	}
	src := smartSource{SiteKey: "site", SpiderAPI: "/spider/test/3", SiteDetail: "movie"}
	if got := smartFetchDetailAndPickAndPlay(nil, server.URL, "", src, nil, nil, false, 1, 0, smartPlaybackSettings{}, nil, nil, false, false, "tmdb", nil); got != nil {
		t.Fatal("invalid episode accepted")
	}
}

func TestStreamingNavigationDoesNotWaitForPlaybackFeedback(t *testing.T) {
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	var mu sync.Mutex
	var requested []string
	var items []any
	for _, id := range []string{"groupA", "groupB"} {
		items = append(items, map[string]any{"vod_id": id, "vod_navigation": map[string]any{"action": "category", "payload": map[string]any{"id": id}}})
	}
	offers := make(chan string, 2)
	done := make(chan error, 1)
	go func() {
		done <- smartWalkNavigationOffers(ctx, map[string]any{"pan_mock": false, "list": items}, smartPlaybackSettings{}, nil,
			func(_ string, payload map[string]any) (map[string]any, error) {
				id := payload["id"].(string)
				mu.Lock()
				requested = append(requested, id)
				mu.Unlock()
				return map[string]any{"list": []any{map[string]any{"vod_play_from": "蓝光HDR", "vod_play_url": id}}}, nil
			}, func(leaf map[string]any) {
				id := leaf["list"].([]any)[0].(map[string]any)["vod_play_url"].(string)
				// Like the original scheduler, accepting an offer does not wait
				// for the separate playback consumer to finish.
				offers <- id
			})
	}()
	select {
	case err := <-done:
		if err != nil {
			t.Fatal(err)
		}
	case <-ctx.Done():
		t.Fatal("navigation added a playback feedback wait")
	}
	mu.Lock()
	defer mu.Unlock()
	if strings.Join(requested, ",") != "groupA,groupB" || len(offers) != 2 {
		t.Fatalf("navigation did not return to the existing offer flow: %v", requested)
	}
}
