package catpawrunner

import "testing"

func TestPanShareInputStandardAndLegacy(t *testing.T) {
	tests := []struct{ flag, value, provider, id, password string }{
		{"百度-1234", "https://pan.baidu.com/s/1shareA?pwd=1234", "baidu", "shareA", "1234"},
		{"百度", "https://pan.baidu.com/share/init?surl=shareA", "baidu", "shareA", ""},
		{"夸克", "https://pan.quark.cn/s/shareA", "quark", "shareA", ""},
		{"UC-abcd", "https://drive.uc.cn/s/shareA?passcode=abcd", "uc", "shareA", "abcd"},
		{"天翼-abcd", "https://cloud.189.cn/web/share?code=shareA&accessCode=abcd", "189", "shareA", "abcd"},
		{"移动", "https://caiyun.139.com/m/i?shareA", "139", "shareA", ""},
		{"移动-abcd", "https://yun.139.com/shareweb/#/w/i/shareA?pwd=abcd", "139", "shareA", "abcd"},
		{"夸父-shareA", "", "quark", "shareA", ""},
		{"天意-shareA", "abcd", "189", "shareA", "abcd"},
		{"百度原画-1shareA", "nopass", "baidu", "shareA", ""},
	}
	for _, tc := range tests {
		t.Run(tc.flag+" "+tc.value, func(t *testing.T) {
			got, ok := ParsePanShareInput(tc.flag, tc.value)
			if !ok || got.Provider != tc.provider || got.ShareID != tc.id || got.Passcode != tc.password {
				t.Fatalf("unexpected share %+v (ok=%v)", got, ok)
			}
			roundTrip, ok := ParsePanShareInput("", got.URL)
			if !ok || roundTrip != got {
				t.Fatalf("canonical URL lost identity: %+v vs %+v", got, roundTrip)
			}
		})
	}
}

func TestPanShareInputDoesNotGuessFromCanonicalPassword(t *testing.T) {
	for _, tc := range [][2]string{
		{"百度-1234", ""}, {"夸克", ""}, {"光鸭", "File$private"},
		{"天翼-1234", "File$123*456*name"},
		{"夸父-shareA", "File$shareA*token*fid"},
		{"夸克", "https://pan.quark.cn.evil.test/s/shareA"},
		{"夸克", "https://user@pan.quark.cn/s/shareA"},
		{"移动", "https://caiyun.139.com/m/i?pwd=1234"},
		{"天意-root", ""},
	} {
		if got, ok := ParsePanShareInput(tc[0], tc[1]); ok {
			t.Fatalf("%q unexpectedly became a share: %+v", tc, got)
		}
	}
}

func TestPanNamesKeepHistoryCompatibility(t *testing.T) {
	for flag, want := range map[string]string{
		"百度": "baidu", "百度-1234": "baidu", "百度原画(无限)-shareA": "baidu",
		"夸克": "quark", "夸父-shareA": "quark",
		"UC": "uc", "uc-abcd": "uc", "优夕-shareA": "uc",
		"天翼": "189", "天意-shareA": "189", "移动": "139", "逸动-shareA": "139",
		"蓝光HDR": "", "光鸭": "", "迅雷": "",
	} {
		if got := PanMockProviderFromFlag(flag); got != want {
			t.Errorf("%s: got %q, want %q", flag, got, want)
		}
	}
}
