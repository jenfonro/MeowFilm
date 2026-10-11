package catpawrunner

import (
	"encoding/base64"
	"encoding/json"
	"net/url"
	"regexp"
	"strings"
)

// PanShareInput keeps share identity separate from the canonical display flag.
// It uses existing URL/flag fields; it is not a new playback/cache protocol.
type PanShareInput struct {
	Provider string
	ShareID  string
	Passcode string
	URL      string
}

func (s PanShareInput) Key() string { return s.Provider + ":" + s.ShareID }

var panShareIDPattern = regexp.MustCompile(`^[A-Za-z0-9_-]{4,256}$`)
var panPlaceholderPattern = regexp.MustCompile(`(?i)^(?:root\d*|nopass|share)$`)
var panSharePathPattern = regexp.MustCompile(`^/s/([A-Za-z0-9_-]+)/?$`)
var panTianyiPathPattern = regexp.MustCompile(`^/t/([A-Za-z0-9_-]+)/?$`)
var panMobilePathPattern = regexp.MustCompile(`(?:^|#)/(?:w/i|m/i)/([A-Za-z0-9_-]+)(?:[/?]|$)`)
var panLegacyFlagPattern = regexp.MustCompile(`^(夸父|优夕|逸动|天意|百度原画)-([A-Za-z0-9_-]+)$`)

func validPanShareID(id string) bool {
	return panShareIDPattern.MatchString(id) && !panPlaceholderPattern.MatchString(id)
}

func ParsePanShareInput(flag, value string) (PanShareInput, bool) {
	label, raw := NormalizePanMockFlag(flag), strings.TrimSpace(value)
	for _, input := range []string{raw, label} {
		u, err := url.Parse(input)
		if err != nil || (u.Scheme != "https" && u.Scheme != "http") || u.User != nil {
			continue
		}
		q := u.Query()
		provider, id := "", ""
		match := panSharePathPattern.FindStringSubmatch(u.Path)
		switch strings.ToLower(u.Hostname()) {
		case "pan.baidu.com":
			provider = "baidu"
			if len(match) > 1 {
				id = strings.TrimPrefix(match[1], "1")
			} else {
				id = q.Get("surl")
			}
		case "pan.quark.cn":
			provider = "quark"
			if len(match) > 1 {
				id = match[1]
			}
		case "drive.uc.cn", "fast.uc.cn":
			provider = "uc"
			if len(match) > 1 {
				id = match[1]
			}
		case "cloud.189.cn", "h5.cloud.189.cn":
			provider = "189"
			if m := panTianyiPathPattern.FindStringSubmatch(u.Path); len(m) > 1 {
				id = m[1]
			} else {
				id = q.Get("code")
				if id == "" {
					id = q.Get("shareCode")
				}
			}
		case "caiyun.139.com", "yun.139.com":
			provider = "139"
			if m := panMobilePathPattern.FindStringSubmatch(u.Path + "#" + u.Fragment); len(m) > 1 {
				id = m[1]
			} else {
				id = q.Get("linkID")
				if id == "" {
					id = q.Get("linkId")
				}
				if id == "" && strings.TrimSuffix(u.Path, "/") == "/m/i" {
					id = strings.SplitN(u.RawQuery, "&", 2)[0]
				}
			}
		}
		if provider == "" || !validPanShareID(id) {
			continue
		}
		pass := ""
		for _, key := range []string{"pwd", "passcode", "accessCode", "password", "passwd"} {
			if pass = strings.TrimSpace(q.Get(key)); pass != "" {
				break
			}
		}
		if pass == "" {
			if pos := strings.Index(u.Fragment, "?"); pos >= 0 {
				hashQuery, _ := url.ParseQuery(u.Fragment[pos+1:])
				pass = hashQuery.Get("pwd")
				if pass == "" {
					pass = hashQuery.Get("passwd")
				}
			}
		}
		if pass == "" {
			names := map[string]string{"baidu": "百度", "quark": "夸克", "uc": "UC", "189": "天翼", "139": "移动"}
			// The URL confirms the whole share ID, including any "-" it contains.
			// Only a suffix following that exact identity can be a password.
			prefix := names[provider] + "-" + id + "-"
			if strings.HasPrefix(label, prefix) {
				pass = strings.TrimPrefix(label, prefix)
			}
		}
		return makePanShareInput(provider, id, pass), true
	}
	legacy := panLegacyFlagPattern.FindStringSubmatch(label)
	if len(legacy) != 3 || !validPanShareID(legacy[2]) || strings.ContainsAny(raw, "$*:/") {
		return PanShareInput{}, false
	}
	provider := PanMockProviderFromFlag(label)
	id := legacy[2]
	if provider == "baidu" {
		id = strings.TrimPrefix(id, "1")
	}
	if !validPanShareID(id) {
		return PanShareInput{}, false
	}
	pass := raw
	if strings.EqualFold(pass, "nopass") {
		pass = ""
	}
	return makePanShareInput(provider, id, pass), provider != ""
}

var panCanonicalListFlagPattern = regexp.MustCompile(`(?i)^(百度|夸克|UC|天翼|移动)-([A-Za-z0-9_-]{6,256})$`)
var panBaiduListPasswordPattern = regexp.MustCompile(`^([A-Za-z0-9_-]{22})-([A-Za-z0-9]{4})$`)
var panCanonicalListPasswordPattern = regexp.MustCompile(`^([A-Za-z0-9_]{6,256})(?:-([A-Za-z0-9]{1,16}))?$`)
var panLegacyBaiduListFlagPattern = regexp.MustCompile(`^[^-]*百度[^-]*-([A-Za-z0-9_-]+)(?:#.*)?$`)

// ParsePanListInput is for an explicit history/manual list request, not for
// deciding whether an ordinary detail source has been handed over to us.
func ParsePanListInput(flag, value string) (PanShareInput, bool) {
	if share, ok := ParsePanShareInput(flag, value); ok {
		return share, true
	}
	label := NormalizePanMockFlag(flag)
	// Preserve decorated saved flags accepted by the existing Baidu list API,
	// without widening ordinary-detail ownership checks.
	if legacy := panLegacyBaiduListFlagPattern.FindStringSubmatch(label); len(legacy) == 2 &&
		strings.TrimSpace(strings.SplitN(label, "-", 2)[0]) != "百度" {
		return ParsePanShareInput("百度原画-"+legacy[1], value)
	}
	if strings.TrimSpace(value) != "" {
		return PanShareInput{}, false
	}
	match := panCanonicalListFlagPattern.FindStringSubmatch(label)
	if len(match) != 3 {
		return PanShareInput{}, false
	}
	provider := PanMockProviderFromFlag(flag)
	id, pass := match[2], ""
	if provider == "baidu" {
		// Only the current 22-character short-code + four-character password
		// shape has a known boundary. Other hyphens remain part of the ID.
		if coded := panBaiduListPasswordPattern.FindStringSubmatch(id); len(coded) == 3 {
			id, pass = coded[1], coded[2]
		}
	} else {
		coded := panCanonicalListPasswordPattern.FindStringSubmatch(id)
		if len(coded) != 3 {
			return PanShareInput{}, false
		}
		id, pass = coded[1], coded[2]
	}
	if provider == "" || !validPanShareID(id) {
		return PanShareInput{}, false
	}
	return makePanShareInput(provider, id, pass), true
}

var panFileTokenPattern = regexp.MustCompile(`^[A-Za-z0-9_-]+$`)
var panFileNumberPattern = regexp.MustCompile(`^\d+$`)

// IsBuiltinPanPlayID recognizes the existing list file formats, not arbitrary
// script IDs. It does not infer ownership from a provider's display name.
func IsBuiltinPanPlayID(provider, value string) bool {
	id := strings.TrimSpace(value)
	if provider == "baidu" {
		encoded := strings.SplitN(strings.SplitN(id, "|||", 2)[0], "***", 2)[0]
		if decoded, err := url.PathUnescape(encoded); err == nil {
			encoded = decoded
		}
		encoded = strings.TrimRight(strings.NewReplacer("-", "+", "_", "/").Replace(encoded), "=")
		raw, err := base64.RawStdEncoding.DecodeString(encoded)
		if err != nil {
			return false
		}
		var data map[string]any
		if json.Unmarshal(raw, &data) != nil {
			return false
		}
		return anyToString(data["surl"]) != "" && anyToString(data["shareid"]) != "" &&
			anyToString(data["uk"]) != "" && anyToString(data["fs_id"]) != "" && anyToString(data["providerId"]) == ""
	}
	parts := strings.Split(id, "*")
	switch provider {
	case "quark", "uc":
		return len(parts) >= 4 && panFileTokenPattern.MatchString(parts[0]) && parts[1] != "" &&
			panFileTokenPattern.MatchString(parts[2]) && parts[3] != ""
	case "139":
		return len(parts) >= 3 && panFileTokenPattern.MatchString(parts[0]) && panFileTokenPattern.MatchString(parts[1])
	case "189":
		return len(parts) >= 3 && panFileNumberPattern.MatchString(parts[0]) && panFileNumberPattern.MatchString(parts[1])
	}
	return false
}

func makePanShareInput(provider, id, pass string) PanShareInput {
	bases := map[string]string{
		"baidu": "https://pan.baidu.com/s/1", "quark": "https://pan.quark.cn/s/",
		"uc": "https://drive.uc.cn/s/", "189": "https://cloud.189.cn/t/", "139": "https://caiyun.139.com/m/i?",
	}
	value := bases[provider] + id
	if pass != "" {
		separator, key := "?", "pwd"
		if provider == "139" {
			separator = "&"
		} else if provider == "189" {
			key = "accessCode"
		}
		value += separator + key + "=" + url.QueryEscape(pass)
	}
	return PanShareInput{Provider: provider, ShareID: id, Passcode: pass, URL: value}
}
