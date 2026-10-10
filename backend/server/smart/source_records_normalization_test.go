package smart

import (
	"strings"
	"testing"
)

func TestDetailSourceRecordsUseShareIdentityNotDisplayName(t *testing.T) {
	src := smartSource{SiteKey: "test", SiteDetail: "film"}
	a := "https://pan.quark.cn/s/shareA?pwd=1234"
	b := "https://pan.quark.cn/s/shareB?pwd=1234"
	records := smartBuildDetailSourceRecords("夸克-shareA-1234$$$夸克-shareB-1234$$$蓝光HDR", a+"$$$"+b+"$$$第1集$opaque-id", true, src)
	if len(records) != 3 {
		t.Fatalf("got %d records", len(records))
	}
	for _, record := range records[:2] {
		if record.Status != smartDetailSourcePending || !record.Supported || record.Provider != "quark" {
			t.Fatalf("share not ready for existing list resolver: %+v", record)
		}
	}
	if records[0].GroupKey == records[1].GroupKey {
		t.Fatal("different shares collapsed because they have the same display name/password")
	}
	if records[2].Supported || records[2].Status != smartDetailSourceResolved || records[2].Episodes[0].URL != "opaque-id" {
		t.Fatalf("native source changed: %+v", records[2])
	}
}

func TestDetailSourceRecordsKeepCompleteRunnerAndNativeLists(t *testing.T) {
	src := smartSource{SiteKey: "test", SiteDetail: "film"}
	id := "shareA*stoken*fid*fileToken***S01E01.mkv"
	for _, mock := range []bool{false, true} {
		records := smartBuildDetailSourceRecords("夸克-shareA$$$光鸭原画", "第1集$"+id+"$$$第1集$private-id", mock, src)
		if len(records) != 2 {
			t.Fatalf("got %d records", len(records))
		}
		for _, record := range records {
			if record.Status != smartDetailSourceResolved || record.Supported || len(record.Episodes) != 1 {
				t.Fatalf("complete lists must not be converted into pending share placeholders: %+v", record)
			}
		}
		if records[0].Episodes[0].URL != id {
			t.Fatal("complete playback ID was changed")
		}
	}
}

func TestShareGroupKeyIgnoresURLTrackingButNotIdentityOrPassword(t *testing.T) {
	record := smartDetailSourceRecord{Provider: "baidu", PanFlag: "百度-shareA-1234", SourceValue: "https://pan.baidu.com/s/1shareA?pwd=1234"}
	key := smartBuildPanMockResolveGroupKey(record)
	if key == "" {
		t.Fatal("missing share key")
	}
	record.SourceValue += "&tracking=ignored"
	if got := smartBuildPanMockResolveGroupKey(record); got != key {
		t.Fatal("tracking parameter split the same share")
	}
	record.SourceValue = strings.ReplaceAll(record.SourceValue, "1234", "abcd")
	if got := smartBuildPanMockResolveGroupKey(record); got == key {
		t.Fatal("different credentials reused the same pending request")
	}
}

func TestTianyiShareCredentialsKeepLegacyAndReadCanonicalURL(t *testing.T) {
	for _, tc := range []struct{ flag, value, code, password string }{
		{"天翼-shareA-abcd", "123*456*S01E01.mkv", "shareA", "abcd"},
		{"天翼-shareA", "123*456*S01E01.mkv", "shareA", ""},
		{"天翼-shareA-abcd", "https://cloud.189.cn/t/shareA", "shareA", "abcd"},
		{"天翼-shareA-abcd", "https://cloud.189.cn/t/shareA?accessCode=1234", "shareA", "1234"},
		{"天翼-abcd", "123*456*S01E01.mkv", "", "abcd"},
	} {
		sc, ac := smartPanMock189CredentialsFromSourceValue(tc.flag, tc.value)
		if sc != tc.code || ac != tc.password {
			t.Fatalf("%s: got %s / %s", tc.flag, sc, ac)
		}
	}
	sc, ac := smartPanMock189CredentialsFromSourceValue("天翼-abcd", "https://cloud.189.cn/t/shareA?accessCode=abcd")
	if sc != "shareA" || ac != "abcd" {
		t.Fatalf("canonical credentials: %s / %s", sc, ac)
	}
	sc, ac = smartPanMock189CredentialsFromSourceValue("天意-shareA", "abcd")
	if sc != "shareA" || ac != "abcd" {
		t.Fatalf("legacy credentials: %s / %s", sc, ac)
	}
}
