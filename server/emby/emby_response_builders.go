package emby

func embyPagedItems(items any, startIndex int, total int) map[string]any {
	return map[string]any{
		"Items":            items,
		"StartIndex":       startIndex,
		"TotalRecordCount": total,
	}
}

func embyPagedEmpty(startIndex int) map[string]any {
	return embyPagedItems([]any{}, startIndex, 0)
}
