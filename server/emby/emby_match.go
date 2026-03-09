package emby

func embyNormalizeAggKey(s string) string {
	return smartNormalizeAggKey(s)
}

func embyMatchScore(qKey string, candKey string) int {
	return smartMatchScore(qKey, candKey)
}
