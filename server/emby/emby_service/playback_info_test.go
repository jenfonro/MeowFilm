package emby_service

import (
	"net/http/httptest"
	"testing"
	"time"

	"github.com/jenfonro/meowfilm/server/catpawrunner"
	"github.com/jenfonro/meowfilm/server/smart"
)

func TestBuildPlaybackStreamTargetsPreserveFinalHeaders(t *testing.T) {
	headers := map[string]string{
		"User-Agent": "UA",
		"Referer":    "https://example.com",
	}
	picked := &smart.PlaybackPickedMeta{
		SiteKey:    "site_a",
		SiteDetail: "detail_a",
		PanFlag:    "flag_a",
		Provider:   "quark",
		RawName:    "episode01",
	}
	req := httptest.NewRequest("GET", "http://localhost/Items/1/PlaybackInfo", nil)

	tmdbTarget := buildTMDBPlaybackStreamTarget(nil, nil, &itemRef{RawID: "tmdb:1", Episode: 1}, playbackDisplayMeta{
		Path:      "/virtual/movie.mp4",
		Container: "mp4",
		Name:      "Movie",
		Runtime:   int64(time.Minute),
	}, picked, "https://example.com/video.m3u8", headers, req)
	if tmdbTarget == nil {
		t.Fatal("expected tmdb playback target")
	}
	if len(tmdbTarget.FinalHeaders) != 2 {
		t.Fatalf("expected tmdb target to preserve headers, got %#v", tmdbTarget.FinalHeaders)
	}

	siteTarget := buildSiteEpisodePlaybackStreamTarget(nil, nil, &itemRef{
		RawID:      "site:1",
		SiteKey:    "site_a",
		SiteDetail: "detail_a",
		SiteTitle:  "Site Title",
		Pan:        1,
		Episode:    1,
	}, resolvedSitePan{RawLabel: "flag_a"}, catpawrunner.Episode{Name: "Episode 1", URL: "https://example.com/raw"}, picked, "https://example.com/video.m3u8", headers, req)
	if siteTarget == nil {
		t.Fatal("expected site playback target")
	}
	if len(siteTarget.FinalHeaders) != 2 {
		t.Fatalf("expected site target to preserve headers, got %#v", siteTarget.FinalHeaders)
	}
}
