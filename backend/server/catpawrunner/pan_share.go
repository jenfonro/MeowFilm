package catpawrunner

import (
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
