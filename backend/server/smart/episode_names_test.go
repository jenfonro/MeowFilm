package smart

import (
	"encoding/base64"
	"encoding/json"
	"fmt"
	"reflect"
	"strings"
	"testing"

	"github.com/jenfonro/meowfilm/server/catpawrunner"
	"github.com/jenfonro/meowfilm/server/magic"
)

var episodeNameRules = []string{
	`{"pattern":".*?([Ss]\\d{1,2})?(?:第\\s*(\\d{1,4})\\s*(?:集|话)|[Ee][Pp]?\\s*(\\d{1,4})(?:$|\\D)).*?.*","replace":"$1E$2$3","flags":"i"}`,
	`{"pattern":"^[\\s\\[\\]\\(\\){}【】._=-]*0*(\\d{1,4})[\\s\\[\\]\\(\\){}【】._=-]*(?:\\.[A-Za-z0-9]{1,6})?\\s*$","replace":"E$1","flags":"i"}`,
}

func TestMediaMetadataEpisodeCandidates(t *testing.T) {
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
		if names := smartExtractRawNamesFromEpisodeURL(ep.URL); len(names) != 0 {
			t.Fatalf("playback locator treated as filename: %v", names)
		}
		candidates := smartExtractEpisodeCandidateTexts(ep)
		if len(candidates) == 0 || candidates[0] != ep.Name {
			t.Fatalf("unexpected candidates: %v", candidates)
		}
		got, err := magic.MagicEpisodeExtractFromCandidates(candidates, nil, episodeNameRules)
		if err != nil || got.Season != 1 || got.Episode != index {
			t.Fatalf("episode %d: got %+v, err %v", index, got, err)
		}
	}
	for _, id := range []string{
		"HTTP://image.example/cover*author*1:20*likes*date*region*https://media.example/E65",
		"https://image.example/cover******opaque-E65",
	} {
		ep := catpawrunner.Episode{Name: "无集数标题", URL: id}
		got, err := magic.MagicEpisodeExtractFromCandidates(smartExtractEpisodeCandidateTexts(ep), nil, episodeNameRules)
		if err != nil || got.Episode != 0 {
			t.Fatalf("metadata produced episode %+v: %v", got, err)
		}
	}
}

func TestPanEpisodeNamesPreserved(t *testing.T) {
	providers := []struct {
		flag string
		id   func(string) string
	}{
		{"夸父-share", func(name string) string { return "shareId*stoken*fid*fidToken***" + name }},
		{"优夕-share", func(name string) string { return "shareId*stoken*fid*fidToken***" + name }},
		{"逸动-share", func(name string) string { return "contentId*linkID***" + name }},
		{"百度原画-share", func(name string) string {
			raw, _ := json.Marshal(map[string]string{"shareid": "123", "uk": "456", "fs_id": "789", "realName": name})
			return base64.StdEncoding.EncodeToString(raw) + "|||" + name
		}},
		{"天意-share", func(name string) string { return "fileId*shareId*" + name }},
	}
	samples := []struct {
		name, dir, quality string
		season, episode    int
	}{
		{"S01E01", "/", "", 1, 1},
		{"01", "/第2季", "", 2, 1},
		{"第十二集", "/第2季", "", 2, 12},
		{"剧名 S03E07.mkv", "/作品", "", 3, 7},
		{"EP09", "/第2季", "", 2, 9},
		{"E12", "@4K/作品/第2季", "4K", 2, 12},
		{"剧名 S01E01.1080p.mkv", "/", "1080P", 1, 1},
		{"S02E03.未知后缀", "/", "", 2, 3},
		{"宣传片", "/作品", "", 0, 0},
		{"第十三集", "/作品/第2季", "", 2, 13},
	}
	for _, provider := range providers {
		for _, sample := range samples {
			t.Run(provider.flag+"/"+sample.name, func(t *testing.T) {
				id := provider.id(sample.name)
				ep := catpawrunner.Episode{Name: sample.dir, URL: id, Flag: provider.flag}
				if names := smartExtractRawNamesFromEpisodeURL(id); !reflect.DeepEqual(names, []string{sample.name}) {
					t.Fatalf("filename changed: %v", names)
				}
				file, dir, parent := smartEpisodePathLayers(ep)
				if file != sample.name || ep.URL != id {
					t.Fatalf("filename or playback ID changed: %q, %q", file, ep.URL)
				}
				wantPath := strings.Trim(strings.TrimPrefix(sample.dir, "@4K"), "/")
				if got := strings.Trim(strings.Join([]string{parent, dir}, "/"), "/"); got != wantPath {
					t.Fatalf("directory changed: %q, want %q", got, wantPath)
				}
				quality, _ := smartGuessQualityByLayers(file, ep.Name, dir, parent)
				if quality != sample.quality {
					t.Fatalf("quality changed: %q", quality)
				}
				got, err := magic.MagicEpisodeExtractFromCandidates(smartExtractEpisodeCandidateTexts(ep), nil, episodeNameRules)
				if got.Episode > 0 && got.Season <= 0 {
					got.Season = smartEpisodePathSeasonHint(ep)
				}
				if err != nil || got.Season != sample.season || got.Episode != sample.episode {
					t.Fatalf("got %+v, want S%dE%d: %v", got, sample.season, sample.episode, err)
				}
			})
		}
	}
}

func TestLegacyEpisodeNameSuffixesPreserved(t *testing.T) {
	for _, id := range []string{
		"opaque***目录/S01E01", "opaque|||目录/S01E01", "a|b|c|目录/S01E01",
		"file*share*目录/S01E01", "https://media.example/video***目录/S01E01",
	} {
		if got := smartExtractRawNamesFromEpisodeURL(id); !reflect.DeepEqual(got, []string{"目录/S01E01"}) {
			t.Fatalf("%q: unexpected names %v", id, got)
		}
	}
}
