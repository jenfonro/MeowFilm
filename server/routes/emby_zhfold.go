package routes

import "strings"

// embyToSimplifiedGuess performs a lightweight Traditional->Simplified fold for common characters.
// It is not a full OpenCC replacement; it is designed to make client-side "double search"
// (e.g. Infuse sending both 繁/简) converge to the same server-side cache/search in practice.
func embyToSimplifiedGuess(s string) string {
	in := strings.TrimSpace(s)
	if in == "" {
		return ""
	}

	// Minimal, high-frequency mapping for media titles.
	// Extend conservatively to avoid changing meaning unexpectedly.
	var tr2s = map[rune]rune{
		'來': '来',
		'劍': '剑',
		'國': '国',
		'畫': '画',
		'後': '后',
		'視': '视',
		'電': '电',
		'這': '这',
		'裡': '里',
		'眾': '众',
		'劇': '剧',
		'體': '体',
		'書': '书',
		'雲': '云',
		'龍': '龙',
		'鳳': '凤',
		'陰': '阴',
		'陽': '阳',
		'讓': '让',
		'說': '说',
		'當': '当',
		'時': '时',
		'長': '长',
		'車': '车',
		'轉': '转',
		'於': '于',
		'對': '对',
		'愛': '爱',
		'親': '亲',
		'發': '发',
		'髮': '发',
		'變': '变',
		'個': '个',
		'與': '与',
		'見': '见',
		'門': '门',
		'開': '开',
		'關': '关',
		'會': '会',
		'學': '学',
		'曉': '晓',
		'兒': '儿',
		'東': '东',
	}

	var b strings.Builder
	b.Grow(len(in))
	changed := false
	for _, r := range in {
		if rr, ok := tr2s[r]; ok {
			b.WriteRune(rr)
			changed = true
			continue
		}
		b.WriteRune(r)
	}
	out := strings.TrimSpace(b.String())
	if !changed {
		return in
	}
	return out
}

func embyCanonicalSearchTerm(term string) string {
	t := strings.TrimSpace(term)
	if t == "" {
		return ""
	}
	return embyToSimplifiedGuess(t)
}

