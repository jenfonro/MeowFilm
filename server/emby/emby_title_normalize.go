package emby

func embyNormalizeTitleForTMDB(kind string, title string) string {
	return smartNormalizeTitleForTMDB(kind, title)
}

// embyNormalizeTitleForTMDBCandidates returns a small list of normalized queries to try, in order.
// This keeps the original normalized title first, and appends conservative fallbacks that remove
// common suffix noise (e.g. "xxx2" -> "xxx", "xxx电影" -> "xxx") when safe.
func embyNormalizeTitleForTMDBCandidates(kind string, title string) []string {
	return smartNormalizeTitleForTMDBCandidates(kind, title)
}

func embyToASCIIDigits(s string) string {
	return smartToASCIIDigits(s)
}
