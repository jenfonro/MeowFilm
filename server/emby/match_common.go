package emby

import "github.com/jenfonro/meowfilm/server/smart"

// Align with frontend: pan_mock provider lookup only depends on label/flag.
func embyPanMockProviderFromLabel(label string) string {
	return smart.PanMockProviderFromLabel(label)
}

func embyScoreEpisodeDisplayName(name string, titleLower string) int {
	return smart.ScoreEpisodeDisplayName(name, titleLower)
}

func embyPickEpisodeDisplayName(displayName string, fileName string, titleLower string, preferFile bool) string {
	return smart.PickEpisodeDisplayName(displayName, fileName, titleLower, preferFile)
}
