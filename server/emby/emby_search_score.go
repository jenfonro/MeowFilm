package emby

import (
	"github.com/jenfonro/meowfilm/server/magic"
)

func embyTitleLenForSort(title string) int {
	return len(magic.NormalizeForMatch(title))
}

func embyComputeMatchScore(query string, title string) int {
	n, err := magic.ComputeMatchScore(query, title)
	if err != nil {
		return 0
	}
	return n
}
