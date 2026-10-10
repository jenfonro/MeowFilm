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

func TestNavigationUsesExistingMatchAndPlayBeforeRequestingNextShare(t *testing.T) {
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
			list = []any{node("group", "resources", false)}
		case "group":
			if r.URL.Path != spider+"/resources" {
				t.Error("script navigation action was changed to detail")
			}
			list = []any{node("otherEpisode", "detail", true), node("badShare", "detail", true),
				node("goodShare", "detail", true), node("unneededShare", "detail", true)}
		default:
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
	if strings.Join(requested, ",") != "movie,group,otherEpisode,badShare,goodShare" {
		t.Fatalf("navigation was eagerly expanded or a failed share aborted the detail: %v", requested)
	}
	if len(played) != 2 || !strings.HasPrefix(played[0], "badShare*") || !strings.HasPrefix(played[1], "goodShare*") {
		t.Fatalf("a nonmatching episode was played or play failure was treated as success: %v", played)
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

func TestStreamingNavigationWaitsForExistingOfferFeedback(t *testing.T) {
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	var stopped atomic.Bool
	var mu sync.Mutex
	var requested []string
	var items []any
	for _, id := range []string{"noMatch", "badShare", "goodShare", "unneededShare"} {
		items = append(items, map[string]any{"vod_id": id, "vod_navigation": map[string]any{"action": "detail", "payload": map[string]any{"id": id}}})
	}
	ready := make(chan string, 2)
	feedback := make(chan bool)
	done := make(chan error, 1)
	go func() {
		done <- smartWalkNavigationOffers(ctx, map[string]any{"pan_mock": false, "list": items}, smartPlaybackSettings{}, stopped.Load,
			func(_ string, payload map[string]any) (map[string]any, error) {
				id := payload["id"].(string)
				mu.Lock()
				requested = append(requested, id)
				mu.Unlock()
				return map[string]any{"list": []any{map[string]any{"vod_play_from": "蓝光HDR", "vod_play_url": id}}}, nil
			}, func(leaf map[string]any, emit func(smartCandidateOffer)) {
				id := leaf["list"].([]any)[0].(map[string]any)["vod_play_url"].(string)
				if id != "noMatch" {
					emit(smartCandidateOffer{Cand: smartCandidate{RawName: id}})
				}
			}, func(ctx context.Context, offers []PlaybackOffer) {
				ready <- offers[0].Cand.RawName
				select {
				case success := <-feedback:
					stopped.Store(success)
				case <-ctx.Done():
				}
			})
	}()
	for i, id := range []string{"badShare", "goodShare"} {
		select {
		case got := <-ready:
			if got != id {
				t.Fatalf("got %s want %s", got, id)
			}
		case <-ctx.Done():
			t.Fatal("navigation did not reach feedback")
		}
		mu.Lock()
		count := len(requested)
		mu.Unlock()
		if count != i+2 {
			t.Fatalf("next share dispatched before feedback: %d", count)
		}
		feedback <- i == 1
	}
	select {
	case err := <-done:
		if err != nil {
			t.Fatal(err)
		}
	case <-ctx.Done():
		t.Fatal("navigation did not stop after playback succeeded")
	}
	mu.Lock()
	defer mu.Unlock()
	if strings.Join(requested, ",") != "noMatch,badShare,goodShare" {
		t.Fatalf("unneeded navigation: %v", requested)
	}
}
