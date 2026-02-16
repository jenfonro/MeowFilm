package routes

import (
	"net/url"
	"path"
	"strings"

	"github.com/jenfonro/meowfilm/server/catpawopen"
)

func embyDetectContainerFromURL(originURL string) (container string, containerList string) {
	u := strings.TrimSpace(originURL)

	if u0, err := url.Parse(u); err == nil {
		ext := strings.ToLower(strings.TrimSpace(path.Ext(u0.Path)))
		if strings.HasPrefix(ext, ".") && len(ext) > 1 {
			container = ext[1:]
		}
	}
	if container == "" && catpawopen.IsProbablyM3U8(u) {
		container = "m3u8"
	}
	if container == "" {
		container = "mp4"
	}

	containerList = container
	switch container {
	case "mkv", "webm":
		containerList = "mkv,webm"
	case "mp4", "m4v":
		containerList = "mp4,m4v"
	}

	return container, containerList
}

func embyBuildDefaultMediaStreams() []map[string]any {
	return []map[string]any{
		{
			"Codec":                  "h264",
			"Index":                  0,
			"Type":                   "Video",
			"IsDefault":              true,
			"IsForced":               false,
			"IsExternal":             false,
			"IsInterlaced":           false,
			"RefFrames":              1,
			"AverageFrameRate":       0,
			"RealFrameRate":          0,
			"CodecTimeBase":          "",
			"VideoRange":             "",
			"Language":               "",
			"DisplayTitle":           "",
			"Height":                 0,
			"Width":                  0,
			"BitRate":                0,
			"Profile":                "",
			"Level":                  0,
			"PixelFormat":            "",
			"AspectRatio":            "",
			"IsTextSubtitleStream":   false,
			"SupportsExternalStream": false,
			"TimeBase":               "1/1000",
		},
		{
			"Codec":                  "aac",
			"Index":                  1,
			"Type":                   "Audio",
			"IsDefault":              true,
			"IsForced":               false,
			"IsExternal":             false,
			"IsInterlaced":           false,
			"Language":               "",
			"DisplayTitle":           "",
			"Channels":               2,
			"SampleRate":             0,
			"BitRate":                0,
			"CodecTimeBase":          "",
			"SupportsExternalStream": false,
			"IsTextSubtitleStream":   false,
			"TimeBase":               "1/1000",
			"Level":                  0,
		},
	}
}

func embyBuildPlaybackInfoResponse(embyID string, container string, containerList string, mediaSourceID string, playSessionID string) map[string]any {
	etag := mediaSourceID
	return map[string]any{
		"MediaSources": []map[string]any{
			{
				"MediaAttachments": []any{},
				"RunTimeTicks":     0,
				"RequiresLooping":  false,
				"MediaStreams":     embyBuildDefaultMediaStreams(),
				"RequiresOpening":  false,

				"Path":          embyBuildMediaPath(embyID, container),
				"ETag":          etag,
				"Name":          embyID,
				"Id":            mediaSourceID,
				"MediaSourceId": mediaSourceID,
				"Type":          "Default",
				"Size":          0,
				"Bitrate":       0,

				"SupportsDirectPlay":   true,
				"SupportsDirectStream": true,
				"SupportsProbing":      true,
				"SupportsTranscoding":  true,

				"RequiresClosing":            false,
				"Formats":                    []any{},
				"RequiredHttpHeaders":        map[string]string{},
				"IsRemote":                   false,
				"IgnoreIndex":                false,
				"IsInfiniteStream":           false,
				"IgnoreDts":                  false,
				"Container":                  containerList,
				"VideoType":                  "VideoFile",
				"DefaultAudioStreamIndex":    1,
				"DefaultSubtitleStreamIndex": -1,
				"GenPtsInput":                false,
				"ReadAtNativeFramerate":      false,
				"Protocol":                   "File",
			},
		},
		"PlaySessionId": playSessionID,
	}
}
