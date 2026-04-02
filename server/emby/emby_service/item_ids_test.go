package emby_service

import "testing"

func TestBuildAndParseSiteEpisodeIDV2(t *testing.T) {
	id := buildSiteEpisodeID("site_a", "/detail/123", 2, 5, "示例标题", "夸克-flag", "https://example.com/raw?id=1")
	if id == "" {
		t.Fatal("expected site episode id")
	}

	siteKey, siteDetail, pan, ep, title, flag, episodeURL, ok := parseSiteEpisodeID(id)
	if !ok {
		t.Fatal("expected site episode id to parse")
	}
	if siteKey != "site_a" || siteDetail != "/detail/123" {
		t.Fatalf("unexpected site identity: %q %q", siteKey, siteDetail)
	}
	if pan != 2 || ep != 5 {
		t.Fatalf("unexpected pan/episode: %d %d", pan, ep)
	}
	if title != "示例标题" {
		t.Fatalf("unexpected title: %q", title)
	}
	if flag != "夸克-flag" {
		t.Fatalf("unexpected flag: %q", flag)
	}
	if episodeURL != "https://example.com/raw?id=1" {
		t.Fatalf("unexpected episode url: %q", episodeURL)
	}
}

func TestParseSiteEpisodeIDRejectsLegacyFormat(t *testing.T) {
	legacy := siteEpisodePrefix + encodeSiteIDPart("site_a") + "." + encodeSiteIDPart("/detail/123") + ".1.2"
	if _, _, _, _, _, _, _, ok := parseSiteEpisodeID(legacy); ok {
		t.Fatal("expected legacy site episode id to be rejected")
	}
}

func TestBuildSiteEpisodeIDRequiresTitle(t *testing.T) {
	id := buildSiteEpisodeID("site_a", "/detail/123", 1, 1, "", "flag", "https://example.com/raw")
	if id != "" {
		t.Fatalf("expected empty id when title is missing, got %q", id)
	}
}
