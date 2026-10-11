package emby_service

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"path/filepath"
	"reflect"
	"sync"
	"testing"

	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/server/catpawrunner"
	"github.com/jenfonro/meowfilm/server/smart"
)

const detailRoutingSpiderAPI = "/0123456789/spider/test/3"
const detailRoutingPlayURL = "https://media.example/runner.mkv"

func detailRoutingFixture(t *testing.T) (*db.DB, func() []map[string]any) {
	t.Helper()
	t.Setenv("MEOWFILM_DB_FILE", filepath.Join(t.TempDir(), "test.db"))
	database, err := db.Open()
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = database.Close() })
	var mu sync.Mutex
	var requests []map[string]any
	runner := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/play" {
			t.Errorf("unexpected list/detail request: %s", r.URL.Path)
		}
		var payload map[string]any
		if err := json.NewDecoder(r.Body).Decode(&payload); err != nil {
			t.Error(err)
		}
		mu.Lock()
		requests = append(requests, payload)
		mu.Unlock()
		w.Header().Set("Content-Type", "application/json")
		_ = json.NewEncoder(w).Encode(map[string]any{"url": detailRoutingPlayURL})
	}))
	t.Cleanup(runner.Close)
	if err := database.ReplacecatpawrunnerServers([]db.CatpawrunnerServer{{Name: "test", APIBase: runner.URL}}); err != nil {
		t.Fatal(err)
	}
	if err := database.ReplaceVideoSourceSites([]db.VideoSourceSite{{Key: "site", Name: "Site", API: detailRoutingSpiderAPI}}); err != nil {
		t.Fatal(err)
	}
	return database, func() []map[string]any {
		mu.Lock()
		defer mu.Unlock()
		return append([]map[string]any(nil), requests...)
	}
}

func detailRoutingOffers(t *testing.T, database *db.DB, pan resolvedSitePan) []smart.PlaybackOffer {
	t.Helper()
	ref := &itemRef{RawID: "tmdb_movie_1", NumericID: 1}
	req := smart.PlaybackRequest{Kind: "movie"}
	manual, ok := buildManualDetailOfferFromResolvedPan(database, ref, req, db.SmartManualItem{}, "site", "Site", detailRoutingSpiderAPI, "film", pan)
	if !ok {
		t.Fatal("manual detail did not produce a candidate")
	}
	history, ok := buildHistoryDetailOfferFromResolvedPan(database, ref, req, db.PlayHistoryRow{}, "site", detailRoutingSpiderAPI, "film", pan)
	if !ok {
		t.Fatal("history detail did not produce a candidate")
	}
	return []smart.PlaybackOffer{manual, history}
}

func TestDetailOffersKeepRunnerOwnership(t *testing.T) {
	database, requests := detailRoutingFixture(t)
	for _, flag := range []string{"百度-shareA", "夸克-shareA", "UC-shareA", "天翼-shareA-abcd", "移动-shareA", "光鸭原画", "蓝光HDR"} {
		for _, panMock := range []bool{false, true} {
			// A complete/opaque detail list has not been resolved locally,
			// including unclaimed native branches in a pan_mock response.
			const id = "opaque-script-id***Movie.mkv"
			records := smart.BuildDetailSourceRecords(flag, "File$"+id, panMock, "site", "Site", detailRoutingSpiderAPI, "film", "")
			pans := buildResolvedSitePans(database, smart.ResolvedRecordsToPans(records))
			if len(pans) != 1 || pans[0].PanMock {
				t.Fatalf("native detail not retained: %+v", pans)
			}
			for _, offer := range detailRoutingOffers(t, database, pans[0]) {
				before := len(requests())
				url, _, _, err := smart.TryPlaybackOffers(database, &smart.User{ID: "1", Username: "fixture"}, []smart.PlaybackOffer{offer})
				if err != nil || url != detailRoutingPlayURL || len(requests()) != before+1 {
					t.Errorf("%s/%s/mode=%v lost detail owner: url=%q err=%v", offer.Cand.Stage, flag, panMock, url, err)
					continue
				}
				want := map[string]any{"flag": flag, "id": id, "siteApi": detailRoutingSpiderAPI, "siteId": "0123456789"}
				if !reflect.DeepEqual(requests()[before], want) {
					t.Errorf("original play protocol changed: %+v", requests()[before])
				}
			}
		}
	}
	// A complete Runner-generated ID must also stay with its originating
	// resolver rather than borrowing MeowFilm's account/token context.
	pan := resolvedSitePan{RawLabel: "夸克-shareA", Episodes: []catpawrunner.Episode{{
		Flag: "夸克-shareA", Name: "Movie", URL: "shareA*runner-token*fid*file-token***Movie.mkv",
	}}}
	for _, offer := range detailRoutingOffers(t, database, pan) {
		before := len(requests())
		url, _, _, err := smart.TryPlaybackOffers(database, &smart.User{ID: "1"}, []smart.PlaybackOffer{offer})
		if err != nil || url != detailRoutingPlayURL || len(requests()) != before+1 {
			t.Errorf("%s sent Runner's complete ID to the local resolver: %q %v", offer.Cand.Stage, url, err)
		}
	}
}

func TestDetailOffersKeepLocalListsLocalAndReplayListsUnchanged(t *testing.T) {
	database, requests := detailRoutingFixture(t)
	pan := resolvedSitePan{RawLabel: "夸克-shareA", PanMock: true, Episodes: []catpawrunner.Episode{{
		Flag: "夸克-shareA", Name: "Movie", URL: "shareA*local-token*fid*file-token***Movie.mkv",
	}}}
	offers := detailRoutingOffers(t, database, pan)
	// Independent replay candidates deliberately retain the original
	// name-based route, without a detail ownership override.
	offers = append(offers,
		smart.PlaybackOffer{Cand: smart.Candidate{Stage: "history_list", PanFlag: pan.RawLabel, Ep: pan.Episodes[0]}},
		smart.PlaybackOffer{Cand: smart.Candidate{Stage: "manual_list", PanFlag: pan.RawLabel, Ep: pan.Episodes[0]}},
	)
	for _, offer := range offers {
		// There is no configured local account in the isolated database.
		// Correct local playback fails before network; wrong Runner routing
		// would return the fixture's URL instead.
		url, _, _, _ := smart.TryPlaybackOffers(database, &smart.User{ID: "1"}, []smart.PlaybackOffer{offer})
		if url != "" || len(requests()) != 0 {
			t.Errorf("%s changed an existing local/replay list into Runner playback", offer.Cand.Stage)
		}
	}
}

func TestStandaloneEmbyNewNamesKeepOpaqueIDsNative(t *testing.T) {
	database, requests := detailRoutingFixture(t)
	for _, flag := range []string{"百度", "夸克", "夸克-shareA", "UC-shareA", "天翼-shareA", "移动-shareA", "蓝光HDR"} {
		const id = "opaque-script-id***Movie.mkv"
		ref := &itemRef{RawID: "site-episode", SiteKey: "site", SiteDetail: "film", SiteTitle: "Movie", Pan: 1, Episode: 1}
		ep := catpawrunner.Episode{Flag: flag, URL: id}
		before := len(requests())
		url, _, _, err := resolveSiteEpisodeDirectPlayback(database, &smart.User{ID: "1"}, ref, ep, flag)
		if err != nil || url != detailRoutingPlayURL || len(requests()) != before+1 {
			t.Errorf("%s acquired name-only local playback: url=%q err=%v", flag, url, err)
			continue
		}
		want := map[string]any{"flag": flag, "id": id, "siteApi": detailRoutingSpiderAPI, "siteId": "0123456789"}
		if !reflect.DeepEqual(requests()[before], want) {
			t.Errorf("standalone native parameters changed: %+v", requests()[before])
		}
	}
}

func TestStandaloneEmbyKeepsCompatibleNameBasedPlayback(t *testing.T) {
	database, requests := detailRoutingFixture(t)
	ref := &itemRef{RawID: "site-episode", SiteKey: "site", SiteDetail: "film", Pan: 1, Episode: 1}
	for _, flag := range []string{"夸父-shareA", "夸克-shareA"} {
		ep := catpawrunner.Episode{Flag: flag, URL: "shareA*token*fid*file-token***Movie.mkv"}
		url, _, _, _ := resolveSiteEpisodeDirectPlayback(database, &smart.User{ID: "1"}, ref, ep, flag)
		if url != "" || len(requests()) != 0 {
			t.Errorf("%s no longer uses its independent name-based local replay", flag)
		}
	}
}

func TestBarePanNamesKeepSiteBoundPlaybackIdentity(t *testing.T) {
	for _, flag := range []string{"百度", "夸克", "UC", "天翼", "移动", "蓝光HDR"} {
		a := &smart.PlaybackPickedMeta{SiteKey: "site-a", SiteDetail: "film-a", PanFlag: flag, RawName: "Movie.mkv"}
		b := &smart.PlaybackPickedMeta{SiteKey: "site-b", SiteDetail: "film-b", PanFlag: flag, RawName: "Movie.mkv"}
		if stablePlaybackIdentityFromPicked("play", "same-item", a) == stablePlaybackIdentityFromPicked("play", "same-item", b) {
			t.Errorf("%s without a share identity merged different sites", flag)
		}
		skip := playbackSwitchSkipItem{Site: "site-a", PanFlag: flag, EpisodeFile: "Movie.mkv"}
		if playbackSwitchSkipMatches(skip, "site-b", flag, "Movie.mkv") {
			t.Errorf("%s without a share identity skipped another site", flag)
		}
		if !playbackSwitchSkipMatches(skip, "site-a", flag, "Movie.mkv") {
			t.Errorf("%s no longer matches its original site", flag)
		}
	}
	// Actual provider-share flags keep the existing cross-site identity rule.
	// Name-based replay is tested separately and still accepts compatible IDs.
	for _, flag := range []string{"百度原画-shareA", "夸父-shareA", "百度-shareA", "夸克-shareA", "UC-shareA", "天翼-shareA", "移动-shareA"} {
		a := &smart.PlaybackPickedMeta{SiteKey: "site-a", SiteDetail: "film-a", PanFlag: flag, RawName: "Movie.mkv"}
		b := &smart.PlaybackPickedMeta{SiteKey: "site-b", SiteDetail: "film-b", PanFlag: flag, RawName: "Movie.mkv"}
		if stablePlaybackIdentityFromPicked("play", "same-item", a) != stablePlaybackIdentityFromPicked("play", "same-item", b) {
			t.Errorf("%s lost its cross-site share identity", flag)
		}
		skip := playbackSwitchSkipItem{Site: "site-a", PanFlag: flag, EpisodeFile: "Movie.mkv"}
		if !playbackSwitchSkipMatches(skip, "site-b", flag, "Movie.mkv") {
			t.Errorf("%s no longer skips the same share/file across sites", flag)
		}
	}
}
