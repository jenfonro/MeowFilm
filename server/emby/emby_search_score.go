package emby

func embyTitleLenForSort(title string) int {
	return smartTitleLenForSort(title)
}

func embyComputeMatchScore(query string, title string) int {
	return smartComputeMatchScore(query, title)
}
