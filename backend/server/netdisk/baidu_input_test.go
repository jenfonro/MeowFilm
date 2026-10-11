package netdisk

import "testing"

func TestBaiduListShareURLInput(t *testing.T) {
	for _, tc := range []struct{ flag, id string }{
		{"https://pan.baidu.com/s/1shareA?pwd=abcd", "shareA"},
		{"https://pan.baidu.com/s/11share-A_b", "1share-A_b"},
		{"https://pan.baidu.com/share/init?surl=1share-A_b&pwd=abcd", "1share-A_b"},
		{"百度原画-1shareA", "shareA"},
		{"https://pan.baidu.com.evil.example/s/1share-A_b", ""},
		{"https://user@pan.baidu.com/s/1share-A_b", ""},
	} {
		if got := parseBaiduSurlFromFlag(tc.flag); got != tc.id {
			t.Errorf("%s: got %q, want %q", tc.flag, got, tc.id)
		}
	}
}
