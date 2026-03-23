package smart

import "testing"

func TestAggKeyWithRules_StripsSeasonVariants(t *testing.T) {
	base := AggKeyWithRules("咒术回战", nil)
	cases := []string{
		"咒术回战 第三季",
		"咒术回战 三季",
		"咒术回战 Season 3",
		"咒术回战 S03",
		"咒术回战 年番二",
	}
	for _, input := range cases {
		if got := AggKeyWithRules(input, nil); got != base {
			t.Fatalf("input=%q got=%q want=%q", input, got, base)
		}
	}
}

func TestAggKeyWithRules_DoesNotRewriteNormalTitles(t *testing.T) {
	got := AggKeyWithRules("一人之下", nil)
	want := smartNormalizeAggKey("一人之下")
	if got != want {
		t.Fatalf("got=%q want=%q", got, want)
	}
}
