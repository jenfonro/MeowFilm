package emby_service

import (
	"fmt"
	"testing"

	"github.com/jenfonro/meowfilm/server/catpawrunner"
)

func TestMediaMetadataEpisodeDisplayAndPlaybackRange(t *testing.T) {
	rules := []string{`{"pattern":".*?([Ss]\\d{1,2})?(?:第\\s*(\\d{1,4})\\s*(?:集|话)|[Ee][Pp]?\\s*(\\d{1,4})(?:$|\\D)).*?.*","replace":"$1E$2$3","flags":"i"}`}
	var episodes []catpawrunner.Episode
	for index := 1; index <= 25; index++ {
		video := "video"
		if index == 4 {
			video = "tmPB4oe65DE"
		}
		ep := catpawrunner.Episode{
			Name: fmt.Sprintf("节目.S01E%02d", index),
			URL:  "https://image.example/cover*Author*1:26:05****watch?v=" + video + "&list=RDWq_VrOPe5uQ",
			Flag: "arbitrary-site",
		}
		episodes = append(episodes, ep)
		display := siteEpisodeDisplayName(ep, ep.Flag, false, "节目", index)
		if display != ep.Name {
			t.Fatalf("episode %d display = %q", index, display)
		}
		if file := siteEpisodeFileName(ep, "", index); file != ep.Name {
			t.Fatalf("episode %d file = %q", index, file)
		}
		if !siteHistoryEpisodeFileMatches(ep.Name, ep, index) {
			t.Fatalf("episode %d cannot resume from its title", index)
		}
	}
	if got := playbackSourceMaxExtractedEpisode(episodes, nil, rules); got != 25 {
		t.Fatalf("playback range = %d, want 25 (not locator's 65)", got)
	}
}

func TestPanEpisodeDisplayStillUsesFilename(t *testing.T) {
	for _, sample := range []struct{ flag, id string }{
		{"夸父-share", "share*token*fid*fidToken***第十二集"},
		{"优夕-share", "share*token*fid*fidToken***第十二集"},
		{"逸动-share", "content*link***第十二集"},
		{"百度原画-share", "eyJyZWFsTmFtZSI6IuesrOWNgeS6jOmbhiJ9|||第十二集"},
		{"天意-share", "file*share*第十二集"},
	} {
		ep := catpawrunner.Episode{Name: "@4K/作品/第2季", URL: sample.id, Flag: sample.flag}
		display := siteEpisodeDisplayName(ep, ep.Flag, true, "作品", 12)
		if display != "第十二集" {
			t.Fatalf("%s display = %q", sample.flag, display)
		}
		if file := siteEpisodeFileName(ep, "", 12); file != "第十二集" {
			t.Fatalf("%s file = %q", sample.flag, file)
		}
	}
}
