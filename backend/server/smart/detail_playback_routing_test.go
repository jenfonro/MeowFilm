package smart

import (
	"bytes"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"reflect"
	"sync/atomic"
	"testing"

	"github.com/jenfonro/meowfilm/server/catpawrunner"
)

func TestNormalDetailCandidatesKeepResolvedMode(t *testing.T) {
	seasons := []smartTMDBSeason{{Season: 1, EpisodeCount: 1}}
	movieRules := []string{`{"pattern":".+","flags":"i"}`}
	for _, provider := range []struct {
		flag, share, id string
	}{
		{"百度-shareA-1234", "https://pan.baidu.com/s/1shareA?pwd=1234", "baidu"},
		{"夸克-shareA", "https://pan.quark.cn/s/shareA", "quark"},
		{"UC-shareA", "https://drive.uc.cn/s/shareA", "uc"},
		{"天翼-shareA-abcd", "https://cloud.189.cn/t/shareA?accessCode=abcd", "189"},
		{"移动-shareA", "https://yun.139.com/shareweb/#/w/i/shareA", "139"},
		{"未命名来源", "https://pan.quark.cn/s/shareA", "quark"},
	} {
		for _, panMock := range []bool{false, true} {
			t.Run(provider.id+"/"+map[bool]string{false: "runner", true: "local"}[panMock], func(t *testing.T) {
				src := smartSource{SiteKey: t.Name(), SiteDetail: "film"}
				fileID := "share*stoken*fid*fileToken***Film.S01E01.mkv"
				sourceValue := "第1集$" + fileID
				if panMock {
					sourceValue = provider.share
				}
				records := smartBuildDetailSourceRecords(provider.flag, sourceValue, panMock, src)
				if len(records) != 1 {
					t.Fatalf("got %d records", len(records))
				}
				if panMock {
					if records[0].Status != smartDetailSourcePending {
						t.Fatalf("share must request list before becoming a candidate: %+v", records[0])
					}
					// Simulate the existing local list resolver's completed record.
					records[0].Status = smartDetailSourceResolved
					records[0].Episodes = []catpawrunner.Episode{{Name: "/", URL: fileID, Flag: records[0].PanFlag}}
				} else if records[0].Status != smartDetailSourceResolved {
					t.Fatal("Runner's complete list must be ready without another list request")
				}
				tv, loose := smartBuildEpisodeMapsFromResolvedRecords(src, records, seasons, nil, false, smartPlaybackSettings{}, nil, episodeNameRules, false, "tmdb")
				if len(tv[1]) != 1 || len(loose) != 0 {
					t.Fatalf("unexpected episode maps: %+v, %+v", tv, loose)
				}
				movies := smartBuildMovieCandidatesFromResolvedRecords(src, records, smartPlaybackSettings{}, nil, movieRules)
				if len(movies) != 1 {
					t.Fatalf("got %d movie candidates", len(movies))
				}
				wantProvider := ""
				if panMock {
					wantProvider = provider.id
				}
				for _, candidate := range []smartCandidate{tv[1][0], movies[0]} {
					if candidate.detailProvider == nil || *candidate.detailProvider != wantProvider {
						t.Fatal("detail resolution mode was lost while building the candidate")
					}
					if got := smartCandidateLocalPanProvider(nil, candidate); got != wantProvider {
						t.Fatalf("local provider = %q, want %q", got, wantProvider)
					}
					if candidate.Ep.URL != fileID {
						t.Fatal("complete playback ID changed")
					}
				}
			})
		}
	}
}

func TestNormalDetailDoesNotRelistOrCollapseResolvedFiles(t *testing.T) {
	local, runner := "quark", ""
	candidates := []smartCandidate{
		{PanFlag: "夸克-shareA-1234", detailProvider: &local, Ep: catpawrunner.Episode{URL: "shareA*token*fid*fileToken***S01E01.mkv"}},
		{PanFlag: "夸克-shareB-1234", detailProvider: &local, Ep: catpawrunner.Episode{URL: "shareB*token*fid*fileToken***S01E01.mkv"}},
		{PanFlag: "夸克-shareA", detailProvider: &runner, Ep: catpawrunner.Episode{URL: "complete-runner-id"}},
		{PanFlag: "蓝光HDR", detailProvider: &runner, Ep: catpawrunner.Episode{URL: "private-native-id"}},
	}
	settings := smartPlaybackSettings{PanTokenOrderLower: []string{"百度"}}
	attempts, fallbackAttempts, normal, fallback := smartBuildPanMockGroupAttempts(candidates, settings, nil, false, 1)
	if len(attempts) != 0 || len(fallbackAttempts) != 0 {
		t.Fatal("already resolved file lists were mistaken for share-list attempts")
	}
	if !reflect.DeepEqual(normal, candidates[:2]) || !reflect.DeepEqual(fallback, candidates[2:]) {
		t.Fatal("same-name shares were merged, or existing priority behavior changed")
	}

	// The independent replay path still uses its original share grouping.
	history := smartCandidate{Stage: smartCandidateStageHistoryList, PanFlag: "夸父-shareA", Ep: catpawrunner.Episode{URL: "1234"}}
	attempts, _, normal, _ = smartBuildPanMockGroupAttempts([]smartCandidate{history}, settings, nil, false, 1)
	if len(attempts) != 1 || attempts[0].Provider != "quark" || attempts[0].MetaA != "1234" || len(normal) != 0 {
		t.Fatal("history's name-based list acceleration was changed")
	}
}

func TestReplayCandidatesKeepNameBasedResolution(t *testing.T) {
	for _, sample := range []struct{ flag, provider string }{
		{"百度-shareA-1234", "baidu"}, {"夸克-shareA-1234", "quark"}, {"UC-shareA", "uc"},
		{"天翼-shareA-abcd", "189"}, {"移动-shareA", "139"},
		{"百度-1234", "baidu"}, {"百度原画(无限)-share", "baidu"},
		{"夸克", "quark"}, {"夸父-share", "quark"},
		{"UC", "uc"}, {"优夕-share", "uc"},
		{"天翼-abcd", "189"}, {"天意-share", "189"},
		{"移动", "139"}, {"逸动-share", "139"},
		{"光鸭原画", ""}, {"蓝光HDR", ""}, {"百度原画(无限)", ""},
	} {
		for _, stage := range []smartCandidateStage{
			smartCandidateStageHistoryList, smartCandidateStageHistoryDetail,
			smartCandidateStageManualList, smartCandidateStageManualDetail,
		} {
			candidate := smartCandidate{Stage: stage, PanFlag: sample.flag}
			if got := smartCandidateLocalPanProvider(nil, candidate); got != sample.provider {
				t.Fatalf("%s/%s: provider %q, want %q", stage, sample.flag, got, sample.provider)
			}
		}
	}
}

func TestDetailCandidateRoutingIsInternal(t *testing.T) {
	candidate := smartCandidate{PanFlag: "夸克-shareA", Ep: catpawrunner.Episode{URL: "complete-id"}}
	before, err := json.Marshal(candidate)
	if err != nil {
		t.Fatal(err)
	}
	for _, mode := range []string{"", "quark"} {
		candidate.detailProvider = &mode
		after, err := json.Marshal(candidate)
		if err != nil || !bytes.Equal(before, after) {
			t.Fatal("internal detail routing must not introduce an external playback field")
		}
	}
}

func TestRunnerDetailCandidatePlaysViaRunner(t *testing.T) {
	for _, flag := range []string{"百度-shareA-1234", "夸克-shareA", "UC-shareA", "天翼-shareA-abcd", "移动-shareA", "光鸭原画", "蓝光HDR"} {
		for _, panMock := range []bool{false, true} {
			t.Run(flag+"/"+map[bool]string{false: "runner", true: "mock-with-complete-list"}[panMock], func(t *testing.T) {
				spiderAPI := "/0123456789/spider/test/4"
				fileID := "opaque-script-id***Film.S01E01.mkv"
				playURL := "https://media.example/video.mkv"
				var detailCalls, playCalls atomic.Int32
				server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
					w.Header().Set("Content-Type", "application/json")
					switch r.URL.Path {
					case spiderAPI + "/detail":
						detailCalls.Add(1)
						_ = json.NewEncoder(w).Encode(map[string]any{
							"pan_mock": panMock,
							"list": []any{map[string]any{
								"vod_id": "film", "vod_play_from": flag, "vod_play_url": "第1集$" + fileID,
							}},
						})
					case "/play":
						playCalls.Add(1)
						var payload map[string]any
						if err := json.NewDecoder(r.Body).Decode(&payload); err != nil {
							t.Error(err)
						}
						want := map[string]any{"flag": flag, "id": fileID, "siteApi": spiderAPI, "siteId": "0123456789"}
						if !reflect.DeepEqual(payload, want) || r.Header.Get("X-TV-User") != "test-user" {
							t.Errorf("play protocol/identity changed: %+v", payload)
						}
						_ = json.NewEncoder(w).Encode(map[string]any{"url": playURL, "header": map[string]string{"Referer": "https://media.example/"}})
					default:
						t.Errorf("unexpected list or other request: %s", r.URL.Path)
						http.Error(w, "unexpected request", http.StatusBadRequest)
					}
				}))
				defer server.Close()
				src := smartSource{SiteKey: t.Name(), SiteDetail: "film", SpiderAPI: spiderAPI}
				seasons := []smartTMDBSeason{{Season: 1, EpisodeCount: 1}}
				cleanRules := []string{`{"pattern":"^$","replace":"","flags":"g"}`}
				settings := smartPlaybackSettings{}
				key := smartBuildDetailCacheKey(src, seasons, nil, false, "tmdb")
				t.Cleanup(func() {
					smartDetailCache.Lock()
					delete(smartDetailCache.M, key)
					smartDetailCache.Unlock()
				})
				entry := smartLoadOrBuildDetailCache(nil, server.URL, src, seasons, nil, false, settings, cleanRules, episodeNameRules, false, "tmdb")
				if entry == nil || !entry.OK || len(entry.EpisodeMap[1]) != 1 {
					t.Fatalf("detail did not produce a ready candidate: %+v", entry)
				}
				candidate := entry.EpisodeMap[1][0]
				if candidate.detailProvider == nil || *candidate.detailProvider != "" {
					t.Fatal("a complete detail list was mistaken for a locally resolved share")
				}
				result := smartTryPlayPickedCandidate(0, nil, server.URL, "test-user", candidate, nil)
				if result == nil || result.PlayURL != playURL || result.Headers["Referer"] != "https://media.example/" {
					t.Fatalf("normal smart play did not use Runner: %+v", result)
				}
				result = smartFetchDetailAndPickAndPlay(nil, server.URL, "test-user", src, seasons, nil, false, 1, 1, settings, cleanRules, episodeNameRules, false, false, "tmdb", nil)
				if result == nil || result.PlayURL != playURL {
					t.Fatalf("cached detail play did not use Runner: %+v", result)
				}
				if detailCalls.Load() != 1 || playCalls.Load() != 2 {
					t.Fatalf("unexpected requests: detail=%d, play=%d", detailCalls.Load(), playCalls.Load())
				}
			})
		}
	}
}

func TestResolvedLocalDetailDoesNotReturnToRunner(t *testing.T) {
	var calls atomic.Int32
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		calls.Add(1)
		_ = json.NewEncoder(w).Encode(map[string]any{"url": "https://media.example/should-not-be-used"})
	}))
	defer server.Close()
	src := smartSource{SiteKey: t.Name(), SiteDetail: "film", SpiderAPI: "/0123456789/spider/test/4"}
	records := smartBuildDetailSourceRecords("夸克-shareA", "https://pan.quark.cn/s/shareA", true, src)
	records[0].Status = smartDetailSourceResolved
	records[0].Episodes = []catpawrunner.Episode{{Name: "/", URL: "shareA*token*fid*fileToken***Film.S01E01.mkv", Flag: "夸克-shareA"}}
	seasons := []smartTMDBSeason{{Season: 1, EpisodeCount: 1}}
	settings := smartPlaybackSettings{}
	episodes, loose := smartBuildEpisodeMapsFromResolvedRecords(src, records, seasons, nil, false, settings, nil, episodeNameRules, false, "tmdb")
	if len(episodes[1]) != 1 {
		t.Fatal("missing resolved local candidate")
	}
	key := smartBuildDetailCacheKey(src, seasons, nil, false, "tmdb")
	smartDetailCache.Lock()
	smartDetailCache.M[key] = &smartDetailCacheEntry{
		OK: true, Source: src, PanMockEnabled: true, SourceRecords: records,
		EpisodeMap: episodes, EpisodeMapLoose: loose,
	}
	smartDetailCache.Unlock()
	t.Cleanup(func() {
		smartDetailCache.Lock()
		delete(smartDetailCache.M, key)
		smartDetailCache.Unlock()
	})
	// With no local account, existing QuarkPlay fails before any network call.
	// A wrong Runner fallback would instead return the test server's URL.
	if result := smartTryPlayPickedCandidate(0, nil, server.URL, "test-user", episodes[1][0], nil); result != nil {
		t.Fatal("local detail unexpectedly fell back to Runner")
	}
	if result := smartFetchDetailAndPickAndPlay(nil, server.URL, "test-user", src, seasons, nil, false, 1, 1, settings, nil, episodeNameRules, false, false, "tmdb", nil); result != nil {
		t.Fatal("cached local detail unexpectedly fell back to Runner")
	}
	if calls.Load() != 0 {
		t.Fatal("a locally resolved list was sent back to Runner")
	}
	if records[0].Episodes[0].URL != "shareA*token*fid*fileToken***Film.S01E01.mkv" {
		t.Fatal("playback ID was rewritten")
	}
}
