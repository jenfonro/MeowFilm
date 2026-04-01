package netdisk

import (
	"bytes"
	"compress/gzip"
	"crypto/md5"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"io"
	"log"
	"net/http"
	"net/http/cookiejar"
	"net/url"
	"os/exec"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
)

type ucQRSession struct {
	ID        string
	CreatedAt time.Time
	ExpiresAt time.Time

	Token    string
	ClientID string

	Image     []byte
	ImageType string

	Client *http.Client
	Jar    http.CookieJar

	Cookie     string
	LastStatus string
	LastErr    string
	mu         sync.Mutex
}

var ucQRSessions sync.Map // id -> *ucQRSession

const (
	ucQRClientID = "381"
	ucQRUA       = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36 Edg/121.0.0.0"
	ucReferer    = "https://drive.uc.cn/"
	ucSSOReferer = "https://api.open.uc.cn/cas/custom/login"
)

func cleanupUCQRSessions(now time.Time) {
	ucQRSessions.Range(func(key, value any) bool {
		s, ok := value.(*ucQRSession)
		if !ok || s == nil {
			ucQRSessions.Delete(key)
			return true
		}
		if now.After(s.ExpiresAt) {
			ucQRSessions.Delete(key)
		}
		return true
	})
}

func makeUCQRClient() (*http.Client, http.CookieJar, error) {
	jar, err := cookiejar.New(nil)
	if err != nil {
		return nil, nil, err
	}
	return &http.Client{
		Timeout:   12 * time.Second,
		Jar:       jar,
		Transport: netdiskHTTPTransport,
	}, jar, nil
}

func ucQRDoReq(client *http.Client, method string, urlStr string, body []byte, headers map[string]string) ([]byte, http.Header, error) {
	req, err := http.NewRequest(method, urlStr, bytes.NewReader(body))
	if err != nil {
		return nil, nil, err
	}
	for k, v := range headers {
		if strings.TrimSpace(k) == "" {
			continue
		}
		req.Header.Set(k, v)
	}
	resp, err := client.Do(req)
	if err != nil {
		return nil, nil, err
	}
	defer resp.Body.Close()

	buf, err := io.ReadAll(io.LimitReader(resp.Body, 8<<20))
	if err != nil {
		return nil, nil, err
	}
	if resp.StatusCode < 200 || resp.StatusCode >= 400 {
		msg := strings.TrimSpace(string(buf))
		if msg == "" {
			msg = resp.Status
		}
		return nil, resp.Header, errors.New("uc http " + strconv.Itoa(resp.StatusCode) + ": " + msg)
	}
	return buf, resp.Header, nil
}

// --- Share list/play (direct cloud-drive API) ---

const (
	ucShareAPIBase      = "https://pc-api.uc.cn/1/clouddrive"
	ucShareReferer      = "https://drive.uc.cn"
	ucShareUA           = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) uc-cloud-drive/2.5.20 Chrome/100.0.4896.160 Electron/18.3.5.4-b478491100 Safari/537.36 Channel/pckk_other_ch"
	ucShareAPIQueryBase = ucShareAPIBase + "?pr=UCBrowser&fr=pc"
)

// UCTV (open-api-drive): ported from catpawrunner `panUc.js`.
const (
	ucTVAPIBase      = "https://open-api-drive.uc.cn"
	ucTVCodeAPIBase  = "http://api.extscreen.com/ucdrive"
	ucTVClientID     = "5acf882d27b74502b7040b0c65519aa7"
	ucTVSignKey      = "l3srvtd7p42l0d0x1u8d7yc8ye9kki4d"
	ucTVAppVer       = "1.7.2.2"
	ucTVChannel      = "UCTVOFFICIALWEB"
	ucTVUA           = "Mozilla/5.0 (Linux; U; Android 13; zh-cn; M2004J7AC Build/UKQ1.231108.001) AppleWebKit/533.1 (KHTML, like Gecko) Mobile Safari/533.1"
	ucTVTokenSkewMs  = int64(60_000)
	ucTVBrand        = "Xiaomi"
	ucTVPlatform     = "tv"
	ucTVDeviceName   = "M2004J7AC"
	ucTVDeviceModel  = "M2004J7AC"
	ucTVBuildDevice  = "M2004J7AC"
	ucTVBuildProduct = "M2004J7AC"
	ucTVDeviceGPU    = "Adreno (TM) 550"
	ucTVActivityRect = "{}"
)

var ucTVTokenMu sync.Mutex

type ucPlayCacheEntry struct {
	ExpAt   time.Time
	URL     string
	Headers map[string]string
}

const ucPlayCacheTTL = 3 * time.Minute

var (
	ucPlayCacheMu sync.Mutex
	ucPlayCache   = map[string]ucPlayCacheEntry{} // key -> entry
)

func getUCPlayCache(key string) (string, map[string]string, bool) {
	k := strings.TrimSpace(key)
	if k == "" {
		return "", nil, false
	}
	now := time.Now()
	ucPlayCacheMu.Lock()
	defer ucPlayCacheMu.Unlock()
	e, ok := ucPlayCache[k]
	if !ok {
		return "", nil, false
	}
	if now.After(e.ExpAt) {
		delete(ucPlayCache, k)
		return "", nil, false
	}
	hdr := map[string]string{}
	for hk, hv := range e.Headers {
		hdr[hk] = hv
	}
	return e.URL, hdr, true
}

func setUCPlayCache(key string, urlStr string, headers map[string]string) {
	k := strings.TrimSpace(key)
	u := strings.TrimSpace(urlStr)
	if k == "" || u == "" {
		return
	}
	hdr := map[string]string{}
	for hk, hv := range headers {
		if strings.TrimSpace(hk) == "" {
			continue
		}
		hdr[hk] = hv
	}
	now := time.Now()
	ucPlayCacheMu.Lock()
	defer ucPlayCacheMu.Unlock()
	ucPlayCache[k] = ucPlayCacheEntry{ExpAt: now.Add(ucPlayCacheTTL), URL: u, Headers: hdr}
	if len(ucPlayCache) > 2000 {
		for ck, cv := range ucPlayCache {
			if now.After(cv.ExpAt) {
				delete(ucPlayCache, ck)
			}
		}
	}
}

func ucToFloat(v any) float64 {
	switch x := v.(type) {
	case nil:
		return 0
	case float64:
		return x
	case float32:
		return float64(x)
	case int:
		return float64(x)
	case int64:
		return float64(x)
	case uint64:
		return float64(x)
	case json.Number:
		f, _ := x.Float64()
		return f
	default:
		f, _ := strconv.ParseFloat(strings.TrimSpace(toString(v)), 64)
		return f
	}
}

func parseUCShareIDFromFlag(flag string) string {
	s := strings.TrimSpace(flag)
	if s == "" {
		return ""
	}
	if strings.HasPrefix(s, "优夕-") {
		return strings.TrimSpace(strings.TrimPrefix(s, "优夕-"))
	}
	if strings.HasPrefix(strings.ToLower(s), "uc-") {
		return strings.TrimSpace(s[3:])
	}
	if strings.Contains(strings.ToLower(s), "drive.uc.cn") {
		if id := parseUCShareIDFromURL(s); id != "" {
			return id
		}
	}
	return ""
}

func parseUCShareIDFromURL(urlStr string) string {
	raw := strings.TrimSpace(urlStr)
	if raw == "" {
		return ""
	}
	u, err := url.Parse(raw)
	if err != nil || u == nil {
		return ""
	}
	host := strings.ToLower(strings.TrimSpace(u.Hostname()))
	if host == "" || (!strings.HasSuffix(host, ".uc.cn") && host != "uc.cn") {
		return ""
	}
	m := strings.Split(strings.Trim(u.Path, "/"), "/")
	if len(m) >= 2 && m[0] == "s" {
		return strings.TrimSpace(m[1])
	}
	return ""
}

func buildUCShareHeaders(cookie string) http.Header {
	h := http.Header{}
	h.Set("User-Agent", ucShareUA)
	h.Set("Referer", ucShareReferer)
	h.Set("Accept", "application/json, text/plain, */*")
	h.Set("Content-Type", "application/json")
	if strings.TrimSpace(cookie) != "" {
		h.Set("Cookie", strings.TrimSpace(cookie))
	}
	return h
}

func ucShareDoJSON(method string, urlStr string, headers http.Header, body []byte, out any) error {
	client := &http.Client{Timeout: 18 * time.Second, Transport: netdiskHTTPTransport}
	req, err := http.NewRequest(method, urlStr, bytes.NewReader(body))
	if err != nil {
		return err
	}
	for k, vv := range headers {
		for _, v := range vv {
			req.Header.Add(k, v)
		}
	}
	resp, err := client.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	raw, _ := io.ReadAll(resp.Body)
	b := bytes.TrimSpace(raw)
	ce := strings.ToLower(strings.TrimSpace(resp.Header.Get("Content-Encoding")))
	if ce == "gzip" || (len(b) >= 2 && b[0] == 0x1f && b[1] == 0x8b) {
		gr, err := gzip.NewReader(bytes.NewReader(b))
		if err == nil {
			defer func() { _ = gr.Close() }()
			dec, _ := io.ReadAll(gr)
			b = bytes.TrimSpace(dec)
		}
	}
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return errors.New("uc http " + strconv.Itoa(resp.StatusCode) + ": " + strings.TrimSpace(string(b)))
	}
	return json.Unmarshal(b, out)
}

func ucShareDoJSONWithCookie(method string, urlStr string, cookie *string, headers http.Header, body []byte, out any) error {
	curCookie := ""
	if cookie != nil {
		curCookie = strings.TrimSpace(*cookie)
	}
	client := &http.Client{Timeout: 18 * time.Second, Transport: netdiskHTTPTransport}
	req, err := http.NewRequest(method, urlStr, bytes.NewReader(body))
	if err != nil {
		return err
	}
	for k, vv := range headers {
		for _, v := range vv {
			req.Header.Add(k, v)
		}
	}
	if curCookie != "" && req.Header.Get("Cookie") == "" {
		req.Header.Set("Cookie", curCookie)
	}
	resp, err := client.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	raw, _ := io.ReadAll(resp.Body)
	b := bytes.TrimSpace(raw)
	ce := strings.ToLower(strings.TrimSpace(resp.Header.Get("Content-Encoding")))
	if ce == "gzip" || (len(b) >= 2 && b[0] == 0x1f && b[1] == 0x8b) {
		gr, err := gzip.NewReader(bytes.NewReader(b))
		if err == nil {
			defer func() { _ = gr.Close() }()
			dec, _ := io.ReadAll(gr)
			b = bytes.TrimSpace(dec)
		}
	}
	if cookie != nil {
		sc := resp.Header.Values("Set-Cookie")
		if len(sc) > 0 {
			*cookie = mergeCookieFromSetCookie(curCookie, sc)
		}
	}
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return errors.New("uc http " + strconv.Itoa(resp.StatusCode) + ": " + strings.TrimSpace(string(b)))
	}
	return json.Unmarshal(b, out)
}

type ucShareTokenResp struct {
	Code    int    `json:"code"`
	Message string `json:"message"`
	Data    struct {
		Stoken string `json:"stoken"`
	} `json:"data"`
}

func ucShareGetStoken(shareID string, passcode string, cookie string) (string, error) {
	pwdID := strings.TrimSpace(shareID)
	if pwdID == "" {
		return "", errors.New("missing shareId")
	}
	u := ucShareAPIBase + "/share/sharepage/token?pr=UCBrowser&fr=pc"
	body := map[string]any{"pwd_id": pwdID}
	pc := strings.TrimSpace(passcode)
	if pc != "" {
		body["passcode"] = pc
	}
	b, _ := json.Marshal(body)
	var resp ucShareTokenResp
	if err := ucShareDoJSON(http.MethodPost, u, buildUCShareHeaders(cookie), b, &resp); err != nil {
		return "", err
	}
	if resp.Code != 0 && resp.Code != 200 {
		msg := strings.TrimSpace(resp.Message)
		if msg == "" {
			msg = "share token failed"
		}
		return "", errors.New(msg)
	}
	st := strings.TrimSpace(resp.Data.Stoken)
	if st == "" {
		return "", errors.New("stoken not found")
	}
	return st, nil
}

func ucTryGetStoken(shareID string, passcode string, cookie *string) (string, error) {
	pwdID := strings.TrimSpace(shareID)
	if pwdID == "" {
		return "", errors.New("missing shareId")
	}
	pc := strings.TrimSpace(passcode)

	var lastErr error

	tryParseStoken := func(raw any) string {
		return strings.TrimSpace(ucExtractFirstStringByKey(raw, "stoken"))
	}
	tryRespOK := func(raw map[string]any) bool {
		code := int(ucToFloat(raw["code"]))
		return code == 0 || code == 200
	}

	// 1) Token endpoint (preferred).
	{
		u := ucShareAPIBase + "/share/sharepage/token?pr=UCBrowser&fr=pc"
		body := map[string]any{"pwd_id": pwdID}
		if pc != "" {
			body["passcode"] = pc
		}
		b, _ := json.Marshal(body)
		var out map[string]any
		err := ucShareDoJSONWithCookie(http.MethodPost, u, cookie, buildUCShareHeaders(""), b, &out)
		if err == nil {
			if st := tryParseStoken(out); st != "" {
				return st, nil
			}
			if !tryRespOK(out) {
				msg := strings.TrimSpace(toString(out["message"]))
				if msg == "" {
					msg = "share token failed"
				}
				lastErr = errors.New(msg)
			} else {
				lastErr = errors.New("stoken not found")
			}
		} else {
			lastErr = err
		}
	}

	// 2) Detail GET with pwd_id only (best-effort, some responses include stoken).
	{
		u, _ := url.Parse(ucShareAPIBase + "/share/sharepage/detail?pr=UCBrowser&fr=pc")
		q := u.Query()
		q.Set("pwd_id", pwdID)
		u.RawQuery = q.Encode()
		var out map[string]any
		h := buildUCShareHeaders("")
		h.Del("Content-Type")
		err := ucShareDoJSONWithCookie(http.MethodGet, u.String(), cookie, h, nil, &out)
		if err == nil {
			if st := tryParseStoken(out); st != "" {
				return st, nil
			}
		} else if lastErr == nil {
			lastErr = err
		}
	}

	// 3) Detail POST with pdir_fid=0.
	{
		u := ucShareAPIBase + "/share/sharepage/detail?pr=UCBrowser&fr=pc"
		body := map[string]any{"pwd_id": pwdID, "pdir_fid": "0"}
		if pc != "" {
			body["passcode"] = pc
		}
		b, _ := json.Marshal(body)
		var out map[string]any
		err := ucShareDoJSONWithCookie(http.MethodPost, u, cookie, buildUCShareHeaders(""), b, &out)
		if err == nil {
			if st := tryParseStoken(out); st != "" {
				return st, nil
			}
		} else if lastErr == nil {
			lastErr = err
		}
	}

	if lastErr != nil {
		return "", lastErr
	}
	return "", errors.New("stoken not found")
}

type ucShareDetailResp struct {
	Code    int    `json:"code"`
	Message string `json:"message"`
	Data    struct {
		List []map[string]any `json:"list"`
	} `json:"data"`
}

func ucShareDetail(shareID string, stoken string, pdirFid string, page int, size int, cookie string) (ucShareDetailResp, error) {
	pwdID := strings.TrimSpace(shareID)
	sToken := strings.TrimSpace(stoken)
	if pwdID == "" || sToken == "" {
		return ucShareDetailResp{}, errors.New("missing uc share parameters")
	}
	pdir := strings.TrimSpace(pdirFid)
	if pdir == "" {
		pdir = "0"
	}
	if page <= 0 {
		page = 1
	}
	if size <= 0 || size > 200 {
		size = 200
	}
	u, _ := url.Parse(ucShareAPIBase + "/share/sharepage/detail?pr=UCBrowser&fr=pc")
	q := u.Query()
	q.Set("pwd_id", pwdID)
	q.Set("stoken", sToken)
	q.Set("pdir_fid", pdir)
	q.Set("force", "0")
	q.Set("_page", strconv.Itoa(page))
	q.Set("_size", strconv.Itoa(size))
	q.Set("_sort", "file_type:asc,file_name:asc")
	u.RawQuery = q.Encode()

	var resp ucShareDetailResp
	h := buildUCShareHeaders(cookie)
	h.Del("Content-Type")
	if err := ucShareDoJSON(http.MethodGet, u.String(), h, nil, &resp); err != nil {
		return ucShareDetailResp{}, err
	}
	if resp.Code != 0 && resp.Code != 200 {
		msg := strings.TrimSpace(resp.Message)
		if msg == "" {
			msg = "share detail failed"
		}
		return ucShareDetailResp{}, errors.New(msg)
	}
	return resp, nil
}

type ucShareDetailPage struct {
	List     []map[string]any
	Total    int
	HasMore  bool
	NextPage int
}

func ucShareDetailPageFetch(shareID string, stoken string, pdirFid string, page int, size int, cookie *string) (ucShareDetailPage, error) {
	pwdID := strings.TrimSpace(shareID)
	sToken := strings.TrimSpace(stoken)
	if pwdID == "" || sToken == "" {
		return ucShareDetailPage{}, errors.New("missing uc share parameters")
	}
	pdir := strings.TrimSpace(pdirFid)
	if pdir == "" {
		pdir = "0"
	}
	if page <= 0 {
		page = 1
	}
	if size <= 0 || size > 200 {
		size = 200
	}
	u, _ := url.Parse(ucShareAPIBase + "/share/sharepage/detail?pr=UCBrowser&fr=pc")
	q := u.Query()
	q.Set("pwd_id", pwdID)
	q.Set("stoken", sToken)
	q.Set("pdir_fid", pdir)
	q.Set("force", "0")
	q.Set("_page", strconv.Itoa(page))
	q.Set("_size", strconv.Itoa(size))
	q.Set("_sort", "file_type:asc,file_name:asc")
	u.RawQuery = q.Encode()

	var out map[string]any
	h := buildUCShareHeaders("")
	h.Del("Content-Type")
	if err := ucShareDoJSONWithCookie(http.MethodGet, u.String(), cookie, h, nil, &out); err != nil {
		return ucShareDetailPage{}, err
	}
	code := int(ucToFloat(out["code"]))
	if code != 0 && code != 200 {
		msg := strings.TrimSpace(toString(out["message"]))
		if msg == "" {
			msg = "share detail failed"
		}
		return ucShareDetailPage{}, errors.New(msg)
	}
	data, _ := out["data"].(map[string]any)
	if data == nil {
		data = map[string]any{}
	}
	listAny, _ := data["list"].([]any)
	list := make([]map[string]any, 0, len(listAny))
	for _, v := range listAny {
		m, _ := v.(map[string]any)
		if m != nil {
			list = append(list, m)
		}
	}
	total := int(ucToFloat(data["total"]))
	if total <= 0 {
		total = int(ucToFloat(data["_total"]))
	}
	hasMore := false
	if b, ok := data["has_more"].(bool); ok {
		hasMore = b
	} else if b, ok := data["hasMore"].(bool); ok {
		hasMore = b
	}
	nextPage := int(ucToFloat(data["next_page"]))
	if nextPage <= 0 {
		nextPage = int(ucToFloat(data["nextPage"]))
	}
	return ucShareDetailPage{List: list, Total: total, HasMore: hasMore, NextPage: nextPage}, nil
}

func ucShareIsDirItem(it map[string]any) bool {
	if it == nil {
		return false
	}
	if v, ok := it["dir"].(bool); ok && v {
		return true
	}
	if v, ok := it["file"].(bool); ok && !v {
		return true
	}
	if ft, ok := it["file_type"].(float64); ok && int(ft) == 0 {
		return true
	}
	return false
}

func ucShareItemFid(it map[string]any) string {
	if it == nil {
		return ""
	}
	if v := strings.TrimSpace(toString(it["fid"])); v != "" {
		return v
	}
	return strings.TrimSpace(toString(it["file_id"]))
}

func ucShareItemFidToken(it map[string]any) string {
	if it == nil {
		return ""
	}
	if v := strings.TrimSpace(toString(it["share_fid_token"])); v != "" {
		return v
	}
	if v := strings.TrimSpace(toString(it["fid_token"])); v != "" {
		return v
	}
	return strings.TrimSpace(toString(it["token"]))
}

func ucShareItemName(it map[string]any) string {
	if it == nil {
		return ""
	}
	if v := strings.TrimSpace(toString(it["file_name"])); v != "" {
		return v
	}
	return strings.TrimSpace(toString(it["name"]))
}

func ucListUncached(database *db.DB, flag string, passcode string) (string, string, error) {
	shareID := parseUCShareIDFromFlag(flag)
	if shareID == "" {
		return "", "", errors.New("missing/invalid flag (expected: 优夕-<shareId>)")
	}
	store := readPanLoginSettings(database)
	cookieStr := getPanField(store, "uc", "cookie")
	if cookieStr == "" {
		return "", "", errors.New("missing uc cookie (pan_login_settings[\"uc\"].cookie)")
	}
	cookie := cookieStr
	stoken, err := ucTryGetStoken(shareID, passcode, &cookie)
	if err != nil {
		return "", shareID, err
	}
	if strings.TrimSpace(cookie) != "" && strings.TrimSpace(cookie) != strings.TrimSpace(cookieStr) {
		store2 := readPanLoginSettings(database)
		setPanField(store2, "uc", "cookie", strings.TrimSpace(cookie))
		_ = writePanLoginSettings(database, store2)
	}

	const (
		ucListPageSize      = 200
		ucMaxDepth          = 12
		ucMaxDirs           = 800
		ucMaxFiles          = 5000
		ucMaxPagesPerDir    = 100
		ucMaxVirtualRootHop = 5
	)

	rootFid := "0"
	rootPrefixSegs := []string{}
	for i := 0; i < ucMaxVirtualRootHop; i++ {
		detail, err := ucShareDetailPageFetch(shareID, stoken, rootFid, 1, ucListPageSize, &cookie)
		if err != nil {
			return "", shareID, err
		}
		dirs := []map[string]any{}
		files := []map[string]any{}
		for _, it := range detail.List {
			if ucShareIsDirItem(it) {
				dirs = append(dirs, it)
			} else {
				if it != nil && isSupportedVideoFilename(strings.TrimSpace(ucShareItemName(it))) {
					files = append(files, it)
				}
			}
		}
		if len(files) == 0 && len(dirs) == 1 && len(detail.List) < ucListPageSize {
			next := ucShareItemFid(dirs[0])
			if next == "" || next == rootFid {
				break
			}
			if n := strings.TrimSpace(ucShareItemName(dirs[0])); n != "" {
				rootPrefixSegs = append(rootPrefixSegs, n)
			}
			rootFid = next
			continue
		}
		break
	}
	rootPrefix := "根目录"
	if len(rootPrefixSegs) > 0 {
		rootPrefix = strings.Join(rootPrefixSegs, "/")
	}

	type dirItem struct {
		fid      string
		pathSegs []string
		depth    int
	}

	queue := []dirItem{{fid: rootFid, pathSegs: []string{}, depth: 0}}
	seenDir := map[string]struct{}{rootFid: {}}
	seenFile := map[string]struct{}{}
	dirCount := 0
	fileCount := 0
	parts := []string{}

	for len(queue) > 0 {
		cur := queue[0]
		queue = queue[1:]
		dirCount++
		if dirCount > ucMaxDirs {
			return strings.Join(parts, "#"), shareID, errors.New("uc share too large (exceeded max dirs)")
		}

		expectedTotal := 0
		fetched := 0
		for page := 1; page <= ucMaxPagesPerDir; page++ {
			detail, err := ucShareDetailPageFetch(shareID, stoken, cur.fid, page, ucListPageSize, &cookie)
			if err != nil {
				return strings.Join(parts, "#"), shareID, err
			}
			if expectedTotal == 0 && detail.Total > 0 {
				expectedTotal = detail.Total
			}
			if len(detail.List) == 0 {
				break
			}

			newCount := 0
			for _, it := range detail.List {
				if it == nil {
					continue
				}
				if ucShareIsDirItem(it) {
					if cur.depth+1 > ucMaxDepth {
						continue
					}
					fid := ucShareItemFid(it)
					name := ucShareItemName(it)
					if fid == "" || name == "" {
						continue
					}
					if _, ok := seenDir[fid]; ok {
						continue
					}
					seenDir[fid] = struct{}{}
					nextSegs := append(append([]string{}, cur.pathSegs...), name)
					queue = append(queue, dirItem{fid: fid, pathSegs: nextSegs, depth: cur.depth + 1})
					newCount++
					continue
				}

				fid := ucShareItemFid(it)
				fidToken := ucShareItemFidToken(it)
				name := ucShareItemName(it)
				if fid == "" || fidToken == "" || name == "" || !isSupportedVideoFilename(name) {
					continue
				}
				if _, ok := seenFile[fid]; ok {
					continue
				}
				seenFile[fid] = struct{}{}
				fileCount++
				if fileCount > ucMaxFiles {
					return strings.Join(parts, "#"), shareID, errors.New("uc share too large (exceeded max files)")
				}

				dirPath := "/"
				if len(cur.pathSegs) > 0 {
					dirPath = "/" + strings.Join(cur.pathSegs, "/")
				}
				dirPath = prefixRootDirDisplay(dirPath, rootPrefix)
				finalDisplay := buildPanDisplayName(dirPath, it)
				id := shareID + "*" + stoken + "*" + fid + "*" + fidToken + "***" + name
				parts = append(parts, finalDisplay+"$"+id)
				newCount++
			}

			fetched += len(detail.List)
			if len(detail.List) < ucListPageSize {
				break
			}
			if newCount == 0 {
				break
			}
			if expectedTotal > 0 && fetched >= expectedTotal {
				break
			}
		}
	}

	if strings.TrimSpace(cookie) != "" && strings.TrimSpace(cookie) != strings.TrimSpace(cookieStr) {
		store2 := readPanLoginSettings(database)
		setPanField(store2, "uc", "cookie", strings.TrimSpace(cookie))
		_ = writePanLoginSettings(database, store2)
	}
	return strings.Join(parts, "#"), shareID, nil
}

func UCList(database *db.DB, flag string, passcode string) (string, string, error) {
	vod, shareID, _, err := UCListWithCacheHit(database, flag, passcode)
	return vod, shareID, err
}

func UCListWithCacheHit(database *db.DB, flag string, passcode string) (vod string, shareID string, fromCache bool, err error) {
	key := listCacheKey("uc_list", flag, listCacheCredentialPart(passcode))
	got, hit, err := ucListCacheTwoTier.Do(key, func() (listCache2, error) {
		vod, shareID, e := ucListUncached(database, flag, passcode)
		if e != nil {
			return listCache2{}, e
		}
		return listCache2{Vod: vod, ShareID: shareID}, nil
	})
	return strings.TrimSpace(got.Vod), strings.TrimSpace(got.ShareID), hit, err
}

type ucDownloadResp struct {
	Data any `json:"data"`
}

func normalizeUCWant(want string) string {
	w := strings.ToLower(strings.TrimSpace(want))
	switch w {
	case "", "streaming", "stream", "play", "play_url", "playurl":
		return "play_url"
	case "downloadurl", "download", "download_url":
		return "download_url"
	default:
		return ""
	}
}

func ucDirectDownload(fid string, fidToken string, cookie string, want string) (string, error) {
	fID := strings.TrimSpace(fid)
	if fID == "" {
		return "", errors.New("missing fid")
	}
	wantMode := strings.TrimSpace(want)
	if wantMode == "" {
		wantMode = "download_url"
	}
	u := ucShareAPIBase + "/file/download?pr=UCBrowser&fr=pc"
	body := map[string]any{"fid": fID, "fids": []any{fID}}
	if strings.TrimSpace(fidToken) != "" {
		body["fid_token"] = strings.TrimSpace(fidToken)
		body["fid_token_list"] = []any{strings.TrimSpace(fidToken)}
	}
	b, _ := json.Marshal(body)
	var resp ucDownloadResp
	if err := ucShareDoJSON(http.MethodPost, u, buildUCShareHeaders(cookie), b, &resp); err != nil {
		return "", err
	}
	out := ucExtractFirstStringByKeys(resp.Data, []string{wantMode, "download_url", "play_url", "url"})
	if strings.TrimSpace(out) == "" {
		return "", errors.New("direct download url not found")
	}
	return strings.TrimSpace(out), nil
}

func ucMD5Hex(s string) string {
	sum := md5.Sum([]byte(s))
	return hex.EncodeToString(sum[:])
}

func ucSHA256Hex(s string) string {
	sum := sha256.Sum256([]byte(s))
	return hex.EncodeToString(sum[:])
}

func ucTVGenerateReqSign(method string, pathname string, deviceID string) (tm string, xPanToken string, reqID string) {
	m := strings.ToUpper(strings.TrimSpace(method))
	if m == "" {
		m = "GET"
	}
	p := strings.TrimSpace(pathname)
	if p == "" {
		p = "/"
	}
	ts := strconv.FormatInt(time.Now().UnixMilli(), 10)
	dev := strings.TrimSpace(deviceID)
	reqID = ucMD5Hex(dev + ts)
	tokenData := m + "&" + p + "&" + ts + "&" + ucTVSignKey
	xPanToken = ucSHA256Hex(tokenData)
	return ts, xPanToken, reqID
}

type ucTVRefreshResp struct {
	Code int `json:"code"`
	Data struct {
		AccessToken  string `json:"access_token"`
		RefreshToken string `json:"refresh_token"`
		ExpiresIn    int64  `json:"expires_in"`
	} `json:"data"`
	Message string `json:"message"`
	Msg     string `json:"msg"`
}

func ucTVRefreshAccessToken(refreshToken string, deviceID string) (accessToken string, nextRefresh string, expAtMs int64, err error) {
	rt := strings.TrimSpace(refreshToken)
	dev := strings.TrimSpace(deviceID)
	if rt == "" {
		return "", "", 0, errors.New("missing uc_tv refresh_token")
	}
	if dev == "" {
		return "", "", 0, errors.New("missing uc_tv device_id")
	}
	_, _, reqID := ucTVGenerateReqSign(http.MethodPost, "/token", dev)
	u := strings.TrimSpace(ucTVCodeAPIBase + "/token")
	payload := map[string]any{
		"req_id":        reqID,
		"app_ver":       ucTVAppVer,
		"device_id":     dev,
		"device_brand":  ucTVBrand,
		"platform":      ucTVPlatform,
		"device_name":   ucTVDeviceName,
		"device_model":  ucTVDeviceModel,
		"build_device":  ucTVBuildDevice,
		"build_product": ucTVBuildProduct,
		"device_gpu":    ucTVDeviceGPU,
		"activity_rect": ucTVActivityRect,
		"channel":       ucTVChannel,
		"refresh_token": rt,
	}
	b, _ := json.Marshal(payload)
	client := &http.Client{Timeout: 12 * time.Second, Transport: netdiskHTTPTransport}
	req, err := http.NewRequest(http.MethodPost, u, bytes.NewReader(b))
	if err != nil {
		return "", "", 0, err
	}
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("Accept", "application/json, text/plain, */*")
	req.Header.Set("User-Agent", ucTVUA)
	resp, err := client.Do(req)
	if err != nil {
		return "", "", 0, err
	}
	defer resp.Body.Close()
	body, _ := io.ReadAll(resp.Body)
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		msg := strings.TrimSpace(string(body))
		return "", "", 0, errors.New("uc_tv http " + strconv.Itoa(resp.StatusCode) + ": " + msg)
	}
	var out ucTVRefreshResp
	if err := json.Unmarshal(bytes.TrimSpace(body), &out); err != nil {
		return "", "", 0, err
	}
	if out.Code != 200 {
		msg := strings.TrimSpace(out.Message)
		if msg == "" {
			msg = strings.TrimSpace(out.Msg)
		}
		if msg == "" {
			msg = "refresh failed"
		}
		return "", "", 0, errors.New("uc_tv refresh failed: " + msg)
	}
	at := strings.TrimSpace(out.Data.AccessToken)
	if at == "" {
		return "", "", 0, errors.New("uc_tv refresh failed: empty access_token")
	}
	nrt := strings.TrimSpace(out.Data.RefreshToken)
	if nrt == "" {
		nrt = rt
	}
	expAt := int64(0)
	if out.Data.ExpiresIn > 0 {
		expAt = time.Now().UnixMilli() + out.Data.ExpiresIn*1000
	}
	return at, nrt, expAt, nil
}

type ucTVFileResp struct {
	Status    int    `json:"status"`
	Errno     int    `json:"errno"`
	ErrorInfo string `json:"error_info"`
	Message   string `json:"message"`
	Data      struct {
		DownloadURL string `json:"download_url"`
		VideoInfo   []struct {
			URL string `json:"url"`
		} `json:"video_info"`
	} `json:"data"`
}

func ucTVIsAccessTokenInvalid(resp ucTVFileResp) bool {
	return resp.Status == -1 && (resp.Errno == 10001 || resp.Errno == 11001)
}

func ucTVLinkByFid(fid string, accessToken string, deviceID string, method string) (string, ucTVFileResp, error) {
	fid2 := strings.TrimSpace(fid)
	if fid2 == "" {
		return "", ucTVFileResp{}, errors.New("missing fid")
	}
	at := strings.TrimSpace(accessToken)
	dev := strings.TrimSpace(deviceID)
	if at == "" {
		return "", ucTVFileResp{}, errors.New("missing uc_tv access_token")
	}
	if dev == "" {
		return "", ucTVFileResp{}, errors.New("missing uc_tv device_id")
	}
	m := strings.ToLower(strings.TrimSpace(method))
	apiMethod := "streaming"
	if m == "download" {
		apiMethod = "download"
	}
	tm, xPanToken, reqID := ucTVGenerateReqSign(http.MethodGet, "/file", dev)

	u, _ := url.Parse(ucTVAPIBase + "/file")
	q := u.Query()
	q.Set("req_id", reqID)
	q.Set("access_token", at)
	q.Set("app_ver", ucTVAppVer)
	q.Set("device_id", dev)
	q.Set("device_brand", ucTVBrand)
	q.Set("platform", ucTVPlatform)
	q.Set("device_name", ucTVDeviceName)
	q.Set("device_model", ucTVDeviceModel)
	q.Set("build_device", ucTVBuildDevice)
	q.Set("build_product", ucTVBuildProduct)
	q.Set("device_gpu", ucTVDeviceGPU)
	q.Set("activity_rect", ucTVActivityRect)
	q.Set("channel", ucTVChannel)
	q.Set("method", apiMethod)
	q.Set("group_by", "source")
	q.Set("fid", fid2)
	q.Set("resolution", "low,normal,high,super,2k,4k")
	q.Set("support", "dolby_vision")
	u.RawQuery = q.Encode()

	client := &http.Client{Timeout: 18 * time.Second, Transport: netdiskHTTPTransport}
	req, err := http.NewRequest(http.MethodGet, u.String(), nil)
	if err != nil {
		return "", ucTVFileResp{}, err
	}
	req.Header.Set("Accept", "application/json, text/plain, */*")
	req.Header.Set("User-Agent", ucTVUA)
	req.Header.Set("x-pan-tm", tm)
	req.Header.Set("x-pan-token", xPanToken)
	req.Header.Set("x-pan-client-id", ucTVClientID)
	resp, err := client.Do(req)
	if err != nil {
		return "", ucTVFileResp{}, err
	}
	defer resp.Body.Close()
	body, _ := io.ReadAll(resp.Body)
	var out ucTVFileResp
	if err := json.Unmarshal(bytes.TrimSpace(body), &out); err != nil {
		if resp.StatusCode < 200 || resp.StatusCode >= 300 {
			return "", ucTVFileResp{}, errors.New("uc_tv http " + strconv.Itoa(resp.StatusCode) + ": " + strings.TrimSpace(string(body)))
		}
		return "", ucTVFileResp{}, err
	}
	// Even when HTTP is 4xx, UCTV may return a structured JSON body with errno/status.
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		msg := strings.TrimSpace(out.ErrorInfo)
		if msg == "" {
			msg = strings.TrimSpace(out.Message)
		}
		if msg == "" {
			msg = strings.TrimSpace(string(body))
		}
		if msg == "" {
			msg = "request failed"
		}
		if out.Errno != 0 {
			return "", out, errors.New("uc_tv errno=" + strconv.Itoa(out.Errno) + ": " + msg)
		}
		return "", out, errors.New("uc_tv http " + strconv.Itoa(resp.StatusCode) + ": " + msg)
	}
	if out.Errno != 0 {
		msg := strings.TrimSpace(out.ErrorInfo)
		if msg == "" {
			msg = strings.TrimSpace(out.Message)
		}
		if msg == "" {
			msg = "request failed"
		}
		return "", out, errors.New("uc_tv errno=" + strconv.Itoa(out.Errno) + ": " + msg)
	}
	if apiMethod == "download" {
		dl := strings.TrimSpace(out.Data.DownloadURL)
		if dl == "" {
			return "", out, errors.New("uc_tv download_url not found")
		}
		return dl, out, nil
	}
	for _, it := range out.Data.VideoInfo {
		if u := strings.TrimSpace(it.URL); u != "" {
			return u, out, nil
		}
	}
	return "", out, errors.New("uc_tv streaming url not found")
}

func ensureUCTVAccessToken(database *db.DB) (accessToken string, deviceID string, err error) {
	store := readPanLoginSettings(database)
	rt := getPanField(store, "uc_tv", "refresh_token")
	dev := getPanField(store, "uc_tv", "device_id")
	at := getPanField(store, "uc_tv", "access_token")
	expAtRaw := getPanField(store, "uc_tv", "access_token_exp_at")
	expAtMs, _ := strconv.ParseInt(strings.TrimSpace(expAtRaw), 10, 64)

	if at != "" && expAtMs > 0 {
		if time.Now().UnixMilli()+ucTVTokenSkewMs < expAtMs {
			return at, dev, nil
		}
	}
	if at != "" && expAtMs == 0 {
		return at, dev, nil
	}
	if rt == "" || dev == "" {
		return "", "", errors.New("missing uc_tv credentials (pan_login_settings[\"uc_tv\"].refresh_token + device_id)")
	}

	ucTVTokenMu.Lock()
	defer ucTVTokenMu.Unlock()

	store = readPanLoginSettings(database)
	at = getPanField(store, "uc_tv", "access_token")
	expAtRaw = getPanField(store, "uc_tv", "access_token_exp_at")
	expAtMs, _ = strconv.ParseInt(strings.TrimSpace(expAtRaw), 10, 64)
	if at != "" && expAtMs > 0 {
		if time.Now().UnixMilli()+ucTVTokenSkewMs < expAtMs {
			return at, dev, nil
		}
	}
	if at != "" && expAtMs == 0 {
		return at, dev, nil
	}

	newAT, newRT, newExpAt, err := ucTVRefreshAccessToken(rt, dev)
	if err != nil {
		return "", "", err
	}
	setPanField(store, "uc_tv", "access_token", newAT)
	if newExpAt > 0 {
		setPanField(store, "uc_tv", "access_token_exp_at", strconv.FormatInt(newExpAt, 10))
	} else {
		setPanField(store, "uc_tv", "access_token_exp_at", "")
	}
	if newRT != "" && newRT != rt {
		setPanField(store, "uc_tv", "refresh_token", newRT)
	}
	if err := writePanLoginSettings(database, store); err != nil {
		log.Printf("[uc_tv] save access_token failed: %v", err)
	}
	return newAT, dev, nil
}

type ucListDirResp struct {
	Code    int    `json:"code"`
	Message string `json:"message"`
	Data    struct {
		List []map[string]any `json:"list"`
	} `json:"data"`
}

func ucListDir(pdirFid string, cookie *string, size int) (ucListDirResp, error) {
	fid := strings.TrimSpace(pdirFid)
	if fid == "" {
		fid = "0"
	}
	sz := size
	if sz <= 0 {
		sz = 200
	}
	if sz > 500 {
		sz = 500
	}
	u, _ := url.Parse(ucShareAPIBase + "/file/sort?pr=UCBrowser&fr=pc")
	q := u.Query()
	q.Set("pdir_fid", fid)
	q.Set("_fetch_total", "1")
	q.Set("_size", strconv.Itoa(sz))
	q.Set("_sort", "file_type:asc,file_name:asc")
	u.RawQuery = q.Encode()
	var resp ucListDirResp
	h := buildUCShareHeaders("")
	h.Del("Content-Type")
	if err := ucShareDoJSONWithCookie(http.MethodGet, u.String(), cookie, h, nil, &resp); err != nil {
		return ucListDirResp{}, err
	}
	if resp.Code != 0 && resp.Code != 200 {
		msg := strings.TrimSpace(resp.Message)
		if msg == "" {
			msg = "list dir failed"
		}
		return ucListDirResp{}, errors.New(msg)
	}
	return resp, nil
}

type ucDeleteFilesResp struct {
	Code    int    `json:"code"`
	Message string `json:"message"`
}

func ucDeleteFiles(fids []string, cookie *string) error {
	list := []string{}
	for _, f := range fids {
		id := strings.TrimSpace(f)
		if id != "" && id != "0" {
			list = append(list, id)
		}
	}
	if len(list) == 0 {
		return nil
	}
	u := ucShareAPIBase + "/file/delete?pr=UCBrowser&fr=pc"
	call := func(actionType int) error {
		body := map[string]any{"action_type": actionType, "filelist": list, "exclude_fids": []any{}}
		b, _ := json.Marshal(body)
		var resp ucDeleteFilesResp
		if err := ucShareDoJSONWithCookie(http.MethodPost, u, cookie, buildUCShareHeaders(""), b, &resp); err != nil {
			return err
		}
		if resp.Code != 0 && resp.Code != 200 {
			msg := strings.TrimSpace(resp.Message)
			if msg == "" {
				msg = "delete failed"
			}
			return errors.New(msg)
		}
		return nil
	}
	if err := call(2); err != nil {
		return call(1)
	}
	return nil
}

func ucClearDir(pdirFid string, cookie *string) error {
	fid := strings.TrimSpace(pdirFid)
	if fid == "" || fid == "0" {
		return errors.New("refuse to clear root (pdir_fid=0)")
	}
	sortResp, err := ucListDir(fid, cookie, 500)
	if err != nil {
		return err
	}
	fids := []string{}
	for _, it := range sortResp.Data.List {
		if it == nil {
			continue
		}
		id := strings.TrimSpace(toString(it["fid"]))
		if id == "" {
			id = strings.TrimSpace(toString(it["file_id"]))
		}
		if id == "" {
			id = strings.TrimSpace(toString(it["id"]))
		}
		if id != "" && id != "0" {
			fids = append(fids, id)
		}
	}
	if len(fids) == 0 {
		return nil
	}
	return ucDeleteFiles(fids, cookie)
}

func ucFindFolderFid(name string, cookie *string, parentFid string) (string, bool, error) {
	folderName := strings.TrimSpace(name)
	if folderName == "" {
		return "", false, errors.New("missing folder name")
	}
	parent := strings.TrimSpace(parentFid)
	if parent == "" {
		parent = "0"
	}
	sortResp, err := ucListDir(parent, cookie, 500)
	if err != nil {
		return "", false, err
	}
	for _, it := range sortResp.Data.List {
		if it == nil {
			continue
		}
		isDir := false
		if v, ok := it["dir"].(bool); ok && v {
			isDir = true
		}
		if ft, ok := it["file_type"].(float64); ok && int(ft) == 0 {
			isDir = true
		}
		kind := strings.ToLower(strings.TrimSpace(toString(it["type"])))
		if kind == "folder" || kind == "dir" || kind == "directory" {
			isDir = true
		}
		if !isDir {
			continue
		}
		nm := strings.TrimSpace(toString(it["file_name"]))
		if nm == "" {
			nm = strings.TrimSpace(toString(it["name"]))
		}
		if nm != folderName {
			continue
		}
		fid := strings.TrimSpace(toString(it["fid"]))
		if fid == "" {
			fid = strings.TrimSpace(toString(it["file_id"]))
		}
		if fid == "" {
			fid = strings.TrimSpace(toString(it["id"]))
		}
		if fid != "" {
			return fid, true, nil
		}
	}
	return "", false, nil
}

func ucEnsureFolderFid(name string, cookie *string, parentFid string) (string, error) {
	folderName := strings.TrimSpace(name)
	if folderName == "" {
		return "", errors.New("missing folder name")
	}
	parent := strings.TrimSpace(parentFid)
	if parent == "" {
		parent = "0"
	}
	if fid, found, err := ucFindFolderFid(folderName, cookie, parent); err == nil && found {
		return fid, nil
	}
	createURL := ucShareAPIBase + "/file?pr=UCBrowser&fr=pc"
	body := map[string]any{
		"pdir_fid":      parent,
		"file_name":     folderName,
		"dir_path":      "",
		"dir_init_lock": false,
	}
	b, _ := json.Marshal(body)
	var out map[string]any
	if err := ucShareDoJSONWithCookie(http.MethodPost, createURL, cookie, buildUCShareHeaders(""), b, &out); err != nil {
		return "", err
	}
	data, _ := out["data"].(map[string]any)
	fid := ""
	if data != nil {
		fid = strings.TrimSpace(toString(data["fid"]))
		if fid == "" {
			fid = strings.TrimSpace(toString(data["file_id"]))
		}
		if fid == "" {
			fid = strings.TrimSpace(toString(data["id"]))
		}
	}
	if fid == "" {
		return "", errors.New("create folder: fid not found")
	}
	return fid, nil
}

func ucEnsureTransferCookie(shareID string, cookie *string) {
	pwdID := strings.TrimSpace(shareID)
	if pwdID == "" || cookie == nil {
		return
	}
	cur := strings.TrimSpace(*cookie)
	m := parseCookieToMap(cur)
	if _, ok := m["__puus"]; ok {
		if _, ok2 := m["Video-Auth"]; ok2 {
			return
		}
	}

	sharePageURL := "https://drive.uc.cn/s/" + url.PathEscape(pwdID) + "?platform=pc"
	req1, err := http.NewRequest(http.MethodGet, sharePageURL, nil)
	if err == nil {
		req1.Header.Set("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8")
		req1.Header.Set("User-Agent", ucShareUA)
		req1.Header.Set("Referer", ucShareReferer)
		req1.Header.Set("Cookie", strings.TrimSpace(*cookie))
		client := &http.Client{Timeout: 12 * time.Second, Transport: netdiskHTTPTransport}
		if resp, e2 := client.Do(req1); e2 == nil && resp != nil {
			_ = resp.Body.Close()
			*cookie = mergeCookieFromSetCookie(strings.TrimSpace(*cookie), resp.Header.Values("Set-Cookie"))
		}
	}

	uploadURL := ucShareAPIBase + "/transfer/upload/pdir?pr=UCBrowser&fr=pc"
	var out any
	h := buildUCShareHeaders("")
	_ = ucShareDoJSONWithCookie(http.MethodPost, uploadURL, cookie, h, []byte(`{}`), &out)
}

func parseUCPlayID(id string) (shareID string, stoken string, fid string, fidToken string, fileName string) {
	raw := strings.TrimSpace(id)
	if raw == "" {
		return "", "", "", "", ""
	}
	parts := strings.SplitN(raw, "***", 2)
	left := parts[0]
	if len(parts) == 2 {
		fileName = strings.TrimSpace(parts[1])
	}
	a := strings.Split(left, "*")
	if len(a) < 4 {
		return "", "", "", "", fileName
	}
	shareID = strings.TrimSpace(a[0])
	stoken = strings.TrimSpace(a[1])
	fid = strings.TrimSpace(a[2])
	fidToken = strings.TrimSpace(a[3])
	return shareID, stoken, fid, fidToken, fileName
}

func ucShareSave(shareID string, stoken string, fid string, fidToken string, toPdirFid string, cookie *string) (savedFid string, err error) {
	pwdID := strings.TrimSpace(shareID)
	sToken := strings.TrimSpace(stoken)
	fID := strings.TrimSpace(fid)
	fToken := strings.TrimSpace(fidToken)
	toPdir := strings.TrimSpace(toPdirFid)
	if pwdID == "" || sToken == "" || fID == "" || fToken == "" {
		return "", errors.New("missing uc share parameters")
	}
	if toPdir == "" || toPdir == "0" {
		return "", errors.New("missing to_pdir_fid")
	}
	ucEnsureTransferCookie(pwdID, cookie)

	saveURL := ucShareAPIBase + "/share/sharepage/save?pr=UCBrowser&fr=pc"
	taskURLBase := ucShareAPIBase + "/task?pr=UCBrowser&fr=pc"
	saveBody := map[string]any{
		"fid_list":       []any{fID},
		"fid_token_list": []any{fToken},
		"to_pdir_fid":    toPdir,
		"pwd_id":         pwdID,
		"stoken":         sToken,
		"pdir_fid":       "0",
		"scene":          "link",
		"share_id":       pwdID,
	}
	b, _ := json.Marshal(saveBody)
	var saveResp map[string]any
	if err := ucShareDoJSONWithCookie(http.MethodPost, saveURL, cookie, buildUCShareHeaders(""), b, &saveResp); err != nil {
		return "", err
	}
	if syncFlag, ok := saveResp["task_sync"].(bool); ok && syncFlag {
		if fid := extractPanSaveTopFid(saveResp); fid != "" {
			return fid, nil
		}
	}
	data, _ := saveResp["data"].(map[string]any)
	taskID := ""
	if data != nil {
		taskID = strings.TrimSpace(toString(data["task_id"]))
		if taskID == "" {
			taskID = strings.TrimSpace(toString(data["taskId"]))
		}
		if taskID == "" {
			taskID = strings.TrimSpace(toString(data["taskID"]))
		}
	}
	if taskID == "" {
		return "", errors.New("uc save: task_id not found")
	}
	deadline := time.Now().Add(30 * time.Second)
	var lastTask map[string]any
	for time.Now().Before(deadline) {
		u, _ := url.Parse(taskURLBase)
		q := u.Query()
		q.Set("task_id", taskID)
		u.RawQuery = q.Encode()
		var taskResp map[string]any
		h := buildUCShareHeaders("")
		h.Del("Content-Type")
		if err := ucShareDoJSONWithCookie(http.MethodGet, u.String(), cookie, h, nil, &taskResp); err != nil {
			lastTask = taskResp
			break
		}
		lastTask = taskResp
		td, _ := taskResp["data"].(map[string]any)
		state := -1
		if td != nil {
			if v := strings.TrimSpace(toString(td["state"])); v != "" {
				if n, e := strconv.Atoi(v); e == nil {
					state = n
				}
			} else if v := strings.TrimSpace(toString(td["status"])); v != "" {
				if n, e := strconv.Atoi(v); e == nil {
					state = n
				}
			}
			finished := state == 2 || state == 3 || state == 100 || strings.ToLower(strings.TrimSpace(toString(td["finished"]))) == "true" || strings.ToLower(strings.TrimSpace(toString(td["finish"]))) == "true" || strings.TrimSpace(toString(td["finish"])) == "1"
			if finished {
				break
			}
		}
		time.Sleep(300 * time.Millisecond)
	}
	if lastTask != nil {
		savedFid = extractPanSaveTopFid(lastTask)
	}
	if savedFid == "" {
		return "", errors.New("uc save: saved fid not found")
	}
	return savedFid, nil
}

func ucParseSubPathSegments(subPath string) []string {
	raw := strings.TrimSpace(subPath)
	if raw == "" {
		return nil
	}
	raw = strings.ReplaceAll(raw, "\\", "/")
	parts := strings.Split(raw, "/")
	out := make([]string, 0, len(parts))
	for _, p := range parts {
		seg := strings.TrimSpace(p)
		if seg == "" || seg == "." || seg == ".." {
			continue
		}
		seg = strings.ReplaceAll(seg, "/", "_")
		seg = strings.ReplaceAll(seg, "\\", "_")
		if seg != "" {
			out = append(out, seg)
		}
		if len(out) >= 12 {
			break
		}
	}
	return out
}

func ucEnsureDestDirFid(cookie *string, tvUser string, toPdirFid string, toPdirPath string) (string, error) {
	fidIn := strings.TrimSpace(toPdirFid)
	if fidIn != "" {
		return fidIn, nil
	}
	segs := ucParseSubPathSegments(toPdirPath)
	rootFid, err := ucEnsureFolderFid(panRootFolderName, cookie, "0")
	if err != nil {
		return "", err
	}
	cur := rootFid
	user := strings.TrimSpace(tvUser)
	if user != "" {
		user = strings.ReplaceAll(user, "/", "_")
		user = strings.ReplaceAll(user, "\\", "_")
		if user != "" {
			userFid, err := ucEnsureFolderFid(user, cookie, cur)
			if err != nil {
				return "", err
			}
			cur = userFid
		}
	}
	for _, seg := range segs {
		next, err := ucEnsureFolderFid(seg, cookie, cur)
		if err != nil {
			return "", err
		}
		cur = next
	}
	return cur, nil
}

func ucPickSavedFidInDir(pdirFid string, cookie *string, expectedName string) (string, error) {
	sortResp, err := ucListDir(pdirFid, cookie, 200)
	if err != nil {
		return "", err
	}
	want := strings.TrimSpace(expectedName)
	candidates := []map[string]any{}
	for _, it := range sortResp.Data.List {
		if it == nil {
			continue
		}
		isDir := false
		if v, ok := it["dir"].(bool); ok && v {
			isDir = true
		}
		if ft, ok := it["file_type"].(float64); ok && int(ft) == 0 {
			isDir = true
		}
		kind := strings.ToLower(strings.TrimSpace(toString(it["type"])))
		if kind == "folder" || kind == "dir" || kind == "directory" {
			isDir = true
		}
		if isDir {
			continue
		}
		candidates = append(candidates, it)
	}
	pick := func(it map[string]any) string {
		id := strings.TrimSpace(toString(it["fid"]))
		if id == "" {
			id = strings.TrimSpace(toString(it["file_id"]))
		}
		if id == "" {
			id = strings.TrimSpace(toString(it["id"]))
		}
		return id
	}
	if want != "" {
		for _, it := range candidates {
			nm := strings.TrimSpace(toString(it["file_name"]))
			if nm == "" {
				nm = strings.TrimSpace(toString(it["name"]))
			}
			if nm == want {
				if fid := pick(it); fid != "" {
					return fid, nil
				}
			}
		}
	}
	if len(candidates) > 0 {
		if fid := pick(candidates[0]); fid != "" {
			return fid, nil
		}
	}
	return "", errors.New("destination folder is empty")
}

func ucCookieResolveURL(savedFid string, pdirFid string, cookie *string, wantMode string) (string, map[string]string, error) {
	if cookie == nil {
		return "", nil, errors.New("missing cookie")
	}
	curCookie := strings.TrimSpace(*cookie)
	u, err := ucDirectDownload(savedFid, "", curCookie, wantMode)
	if err != nil {
		sortResp, e2 := ucListDir(pdirFid, cookie, 200)
		if e2 != nil {
			return "", nil, err
		}
		pickedToken := ""
		for _, it := range sortResp.Data.List {
			if it == nil {
				continue
			}
			id2 := strings.TrimSpace(toString(it["fid"]))
			if id2 == "" {
				id2 = strings.TrimSpace(toString(it["file_id"]))
			}
			if id2 == "" {
				id2 = strings.TrimSpace(toString(it["id"]))
			}
			if id2 != strings.TrimSpace(savedFid) {
				continue
			}
			pickedToken = strings.TrimSpace(toString(it["fid_token"]))
			if pickedToken == "" {
				pickedToken = strings.TrimSpace(toString(it["token"]))
			}
			break
		}
		u2, err2 := ucDirectDownload(savedFid, pickedToken, strings.TrimSpace(*cookie), wantMode)
		if err2 != nil {
			return "", nil, err
		}
		u = u2
	}
	h := map[string]string{
		"Cookie":     strings.TrimSpace(*cookie),
		"Referer":    ucShareReferer,
		"User-Agent": ucShareUA,
	}
	return u, h, nil
}

func ucPlayImpl(database *db.DB, flag string, id string, want string, tvUser string, toPdirPath string, toPdirFid string) (string, map[string]string, error) {
	shareID, stoken, fid, fidToken, expectedName := parseUCPlayID(id)
	if fid == "" || shareID == "" || stoken == "" {
		return "", nil, errors.New("invalid id (expected: <shareId>*<stoken>*<fid>*<fidToken>***<name>)")
	}
	if strings.Contains(strings.ToLower(flag), "drive.uc.cn") {
		if sid := parseUCShareIDFromURL(flag); sid != "" {
			shareID = sid
		}
	}
	wantMode := normalizeUCWant(want)
	if wantMode == "" {
		wantMode = "play_url"
	}
	cacheKey := strings.TrimSpace(id) + "|" + wantMode
	if u, h, ok := getUCPlayCache(cacheKey); ok && u != "" {
		if len(h) == 0 {
			markPanPlayActivity("uc", time.Now(), panPlayActiveTTL)
			return u, nil, nil
		}
		markPanPlayActivity("uc", time.Now(), panPlayActiveTTL)
		return u, h, nil
	}

	store := readPanLoginSettings(database)
	cookieStr := getPanField(store, "uc", "cookie")
	if cookieStr == "" {
		return "", nil, errors.New("missing uc cookie (pan_login_settings[\"uc\"].cookie)")
	}
	cookie := cookieStr

	destFid, err := ucEnsureDestDirFid(&cookie, tvUser, toPdirFid, toPdirPath)
	if err != nil {
		return "", nil, err
	}
	savedFid, err := ucShareSave(shareID, stoken, fid, fidToken, destFid, &cookie)
	if err != nil {
		return "", nil, err
	}
	if strings.TrimSpace(savedFid) == "" {
		savedFid, err = ucPickSavedFidInDir(destFid, &cookie, expectedName)
		if err != nil {
			return "", nil, err
		}
	}

	if strings.TrimSpace(cookie) != "" && strings.TrimSpace(cookie) != strings.TrimSpace(cookieStr) {
		store2 := readPanLoginSettings(database)
		setPanField(store2, "uc", "cookie", strings.TrimSpace(cookie))
		_ = writePanLoginSettings(database, store2)
	}

	hasTV := strings.TrimSpace(getPanField(store, "uc_tv", "refresh_token")) != "" && strings.TrimSpace(getPanField(store, "uc_tv", "device_id")) != ""

	playUrl := ""
	downloadUrl := ""
	var playHeader map[string]string
	var downloadHeader map[string]string
	playSource := ""
	downloadSource := ""

	if hasTV {
		if u, err := ucTVResolveLink(database, savedFid, "streaming"); err == nil && strings.TrimSpace(u) != "" {
			playUrl = strings.TrimSpace(u)
			playSource = "tv"
		}
		if u, err := ucTVResolveLink(database, savedFid, "download"); err == nil && strings.TrimSpace(u) != "" {
			downloadUrl = strings.TrimSpace(u)
			downloadSource = "tv"
		}
	}

	if strings.TrimSpace(playUrl) == "" {
		u, h, err := ucCookieResolveURL(savedFid, destFid, &cookie, "play_url")
		if err != nil {
			return "", nil, err
		}
		playUrl = strings.TrimSpace(u)
		playHeader = h
		playSource = "cookie"
	}
	if strings.TrimSpace(downloadUrl) == "" {
		u, h, err := ucCookieResolveURL(savedFid, destFid, &cookie, "download_url")
		if err != nil {
			return "", nil, err
		}
		downloadUrl = strings.TrimSpace(u)
		downloadHeader = h
		downloadSource = "cookie"
	}

	preferDownload := wantMode == "download_url"
	chosenUrl := ""
	chosenSource := ""
	var chosenHeader map[string]string
	if preferDownload {
		if strings.TrimSpace(downloadUrl) != "" {
			chosenUrl = downloadUrl
			chosenSource = downloadSource
			chosenHeader = downloadHeader
		} else {
			chosenUrl = playUrl
			chosenSource = playSource
			chosenHeader = playHeader
		}
	} else {
		if strings.TrimSpace(playUrl) != "" {
			chosenUrl = playUrl
			chosenSource = playSource
			chosenHeader = playHeader
		} else {
			chosenUrl = downloadUrl
			chosenSource = downloadSource
			chosenHeader = downloadHeader
		}
	}

	if strings.TrimSpace(chosenUrl) == "" {
		return "", nil, errors.New("empty url")
	}

	if strings.TrimSpace(cookie) != "" && strings.TrimSpace(cookie) != strings.TrimSpace(cookieStr) {
		store2 := readPanLoginSettings(database)
		setPanField(store2, "uc", "cookie", strings.TrimSpace(cookie))
		_ = writePanLoginSettings(database, store2)
	}

	if chosenSource == "cookie" && len(chosenHeader) > 0 {
		setUCPlayCache(cacheKey, chosenUrl, chosenHeader)
		markPanPlayActivity("uc", time.Now(), panPlayActiveTTL)
		return chosenUrl, chosenHeader, nil
	}
	setUCPlayCache(cacheKey, chosenUrl, map[string]string{})
	markPanPlayActivity("uc", time.Now(), panPlayActiveTTL)
	return chosenUrl, nil, nil
}

func ucTVResolveLink(database *db.DB, fid string, method string) (string, error) {
	at, dev, err := ensureUCTVAccessToken(database)
	if err != nil || strings.TrimSpace(at) == "" || strings.TrimSpace(dev) == "" {
		if err == nil {
			err = errors.New("missing access token/device id")
		}
		return "", err
	}
	u, resp, err := ucTVLinkByFid(fid, at, dev, method)
	if err == nil && strings.TrimSpace(u) != "" {
		return strings.TrimSpace(u), nil
	}
	if !ucTVIsAccessTokenInvalid(resp) {
		if err != nil {
			return "", err
		}
		return "", errors.New("empty url")
	}

	store := readPanLoginSettings(database)
	rt := getPanField(store, "uc_tv", "refresh_token")
	dev2 := getPanField(store, "uc_tv", "device_id")
	if strings.TrimSpace(rt) == "" || strings.TrimSpace(dev2) == "" {
		return "", errors.New("missing uc_tv credentials (pan_login_settings[\"uc_tv\"].refresh_token + device_id)")
	}
	newAT, newRT, newExpAt, e2 := ucTVRefreshAccessToken(rt, dev2)
	if e2 != nil || strings.TrimSpace(newAT) == "" {
		if e2 != nil {
			return "", e2
		}
		return "", errors.New("uc_tv refresh failed: empty access_token")
	}
	store2 := readPanLoginSettings(database)
	setPanField(store2, "uc_tv", "access_token", newAT)
	if newExpAt > 0 {
		setPanField(store2, "uc_tv", "access_token_exp_at", strconv.FormatInt(newExpAt, 10))
	} else {
		setPanField(store2, "uc_tv", "access_token_exp_at", "")
	}
	if strings.TrimSpace(newRT) != "" && strings.TrimSpace(newRT) != strings.TrimSpace(rt) {
		setPanField(store2, "uc_tv", "refresh_token", newRT)
	}
	_ = writePanLoginSettings(database, store2)

	u2, _, e3 := ucTVLinkByFid(fid, newAT, dev2, method)
	if e3 != nil {
		return "", e3
	}
	if strings.TrimSpace(u2) == "" {
		return "", errors.New("empty url")
	}
	return strings.TrimSpace(u2), nil
}

func UCPlay(database *db.DB, id string, want string) (string, map[string]string, error) {
	return ucPlayImpl(database, "", id, want, "", "", "")
}

func UCPlayWithTVUser(database *db.DB, id string, want string, tvUser string) (string, map[string]string, error) {
	return ucPlayImpl(database, "", id, want, tvUser, "", "")
}

func HandleAPIUCList(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	var body struct {
		Flag     string `json:"flag"`
		Passcode string `json:"passcode"`
		Pwd      string `json:"pwd"`
	}
	_ = readJSONLoose(r, &body)
	flag := strings.TrimSpace(body.Flag)
	passcode := strings.TrimSpace(body.Passcode)
	if passcode == "" {
		passcode = strings.TrimSpace(body.Pwd)
	}
	key := flag + "|" + passcode
	val, fromCache, err := ucListCache.Do(key, func() (ucListAPIValue, error) {
		vod, shareID, err := UCList(database, flag, passcode)
		if err != nil {
			return ucListAPIValue{}, err
		}
		return ucListAPIValue{Vod: vod, ShareID: shareID}, nil
	})
	if err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": err.Error()})
		return
	}
	writeJSON(w, 200, map[string]any{"ok": true, "flag": flag, "shareId": val.ShareID, "vod_play_url": val.Vod, "cache": fromCache})
}

func HandleAPIUCPlay(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	var body struct {
		Flag       string `json:"flag"`
		ID         string `json:"id"`
		Want       string `json:"want"`
		ToPdirPath string `json:"toPdirPath"`
		ToPdirFid  string `json:"toPdirFid"`
	}
	_ = readJSONLoose(r, &body)
	id := strings.TrimSpace(body.ID)
	if id == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": "missing id"})
		return
	}
	tvUser := getTVUserFromRequest(r)
	flag := strings.TrimSpace(body.Flag)
	want := strings.TrimSpace(body.Want)
	toPdirPath := strings.TrimSpace(body.ToPdirPath)
	toPdirFid := strings.TrimSpace(body.ToPdirFid)

	cacheKey := buildPlayCacheKey("uc", tvUser, flag, id, want, toPdirPath, toPdirFid)
	if u, header, ok := getPlayCache(cacheKey); ok {
		resp := map[string]any{"ok": true, "parse": 0, "url": u}
		if len(header) > 0 {
			resp["header"] = header
		}
		writeJSON(w, 200, attachRelayToken(r, resp))
		return
	}

	u, header, err := ucPlayImpl(database, flag, id, want, tvUser, toPdirPath, toPdirFid)
	if err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": err.Error()})
		return
	}
	resp := map[string]any{"ok": true, "parse": 0, "url": u}
	if len(header) > 0 {
		resp["header"] = header
	}
	if strings.TrimSpace(u) != "" {
		setPlayCache(cacheKey, u, header)
	}
	writeJSON(w, 200, attachRelayToken(r, resp))
}

func HandleAPIUCTVRefresh(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	store := readPanLoginSettings(database)
	rt := strings.TrimSpace(getPanField(store, "uc_tv", "refresh_token"))
	dev := strings.TrimSpace(getPanField(store, "uc_tv", "device_id"))
	if rt == "" || dev == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": "missing uc_tv refresh_token/device_id in pan_login_settings[\"uc_tv\"]"})
		return
	}
	at, newRT, expAt, err := ucTVRefreshAccessToken(rt, dev)
	if err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": err.Error()})
		return
	}
	store2 := readPanLoginSettings(database)
	setPanField(store2, "uc_tv", "access_token", strings.TrimSpace(at))
	if expAt > 0 {
		setPanField(store2, "uc_tv", "access_token_exp_at", strconv.FormatInt(expAt, 10))
	} else {
		setPanField(store2, "uc_tv", "access_token_exp_at", "")
	}
	if strings.TrimSpace(newRT) != "" && strings.TrimSpace(newRT) != rt {
		setPanField(store2, "uc_tv", "refresh_token", strings.TrimSpace(newRT))
	}
	if err := writePanLoginSettings(database, store2); err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": "save pan_login_settings failed: " + err.Error()})
		return
	}
	writeJSON(w, 200, map[string]any{
		"ok":                  true,
		"device_id":           dev,
		"access_token_exp_at": strings.TrimSpace(getPanField(store2, "uc_tv", "access_token_exp_at")),
	})
}

func buildUCHeaders(extra map[string]string) map[string]string {
	h := map[string]string{
		"User-Agent":      ucQRUA,
		"Referer":         ucReferer,
		"Origin":          "https://drive.uc.cn",
		"Accept":          "application/json, text/plain, */*",
		"Accept-Language": "zh-CN,zh;q=0.9,en;q=0.8",
		"Connection":      "keep-alive",
	}
	for k, v := range extra {
		h[k] = v
	}
	return h
}

func ucExtractFirstStringByKey(root any, keyLower string) string {
	type item struct{ v any }
	q := []item{{v: root}}
	steps := 0
	kl := strings.ToLower(strings.TrimSpace(keyLower))
	for len(q) > 0 && steps < 5000 {
		steps++
		cur := q[0].v
		q = q[1:]
		if cur == nil {
			continue
		}
		if m, ok := cur.(map[string]any); ok {
			for k, v := range m {
				if strings.ToLower(strings.TrimSpace(k)) == kl {
					if s, ok := v.(string); ok && strings.TrimSpace(s) != "" {
						return strings.TrimSpace(s)
					}
				}
				q = append(q, item{v: v})
			}
			continue
		}
		if arr, ok := cur.([]any); ok {
			for _, v := range arr {
				q = append(q, item{v: v})
			}
			continue
		}
	}
	return ""
}

func ucExtractFirstStringByKeys(root any, keysLower []string) string {
	for _, k := range keysLower {
		kl := strings.ToLower(strings.TrimSpace(k))
		if kl == "" {
			continue
		}
		if v := ucExtractFirstStringByKey(root, kl); strings.TrimSpace(v) != "" {
			return strings.TrimSpace(v)
		}
	}
	return ""
}

func ucExtractFirstNumberByKey(root any, keyLower string) (float64, bool) {
	type item struct{ v any }
	q := []item{{v: root}}
	steps := 0
	kl := strings.ToLower(strings.TrimSpace(keyLower))
	for len(q) > 0 && steps < 5000 {
		steps++
		cur := q[0].v
		q = q[1:]
		if cur == nil {
			continue
		}
		if m, ok := cur.(map[string]any); ok {
			for k, v := range m {
				if strings.ToLower(strings.TrimSpace(k)) == kl {
					switch n := v.(type) {
					case float64:
						return n, true
					case int:
						return float64(n), true
					case int64:
						return float64(n), true
					case json.Number:
						f, err := n.Float64()
						if err == nil {
							return f, true
						}
					}
				}
				q = append(q, item{v: v})
			}
			continue
		}
		if arr, ok := cur.([]any); ok {
			for _, v := range arr {
				q = append(q, item{v: v})
			}
			continue
		}
	}
	return 0, false
}

func ucQRInitCookies(client *http.Client) {
	if client == nil {
		return
	}
	_, _, _ = ucQRDoReq(client, "GET", "https://drive.uc.cn/", nil, buildUCHeaders(map[string]string{
		"Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
	}))
	loginURL := "https://api.open.uc.cn/cas/custom/login?custom_login_type=mobile&client_id=" + url.QueryEscape(ucQRClientID) + "&display=pc&v=1.2"
	_, _, _ = ucQRDoReq(client, "GET", loginURL, nil, buildUCHeaders(map[string]string{
		"Referer": ucSSOReferer,
		"Origin":  "https://api.open.uc.cn",
		"Accept":  "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
	}))
}

func ucQRGetToken(client *http.Client) (token string, err error) {
	u, _ := url.Parse("https://api.open.uc.cn/cas/ajax/getTokenForQrcodeLogin")
	now := time.Now().UnixMilli()
	qs := u.Query()
	qs.Set("client_id", ucQRClientID)
	qs.Set("v", "1.2")
	qs.Set("__dt", quarkMakeDT(now))
	qs.Set("__t", strconv.FormatInt(now, 10))
	qs.Set("request_id", uuidV4())
	u.RawQuery = qs.Encode()

	body, _, err := ucQRDoReq(client, "GET", u.String(), nil, buildUCHeaders(map[string]string{
		"Referer": ucSSOReferer,
		"Origin":  "https://api.open.uc.cn",
	}))
	if err != nil {
		return "", err
	}
	var raw any
	_ = json.Unmarshal(body, &raw)
	token = ucExtractFirstStringByKey(raw, "token")
	if token == "" {
		return "", errors.New("uc token missing")
	}
	return token, nil
}

func ucQRBuildQRText(token string) string {
	t := strings.TrimSpace(token)
	if t == "" {
		return ""
	}
	u, _ := url.Parse("https://su.uc.cn/1_n0ZCv")
	qs := u.Query()
	qs.Set("uc_param_str", "dsdnfrpfbivesscpgimibtbmnijblauputogpintnwktprchmt")
	qs.Set("token", t)
	qs.Set("client_id", ucQRClientID)
	qs.Set("uc_biz_str", "S:custom|C:titlebar_fix")
	u.RawQuery = qs.Encode()
	return u.String()
}

func ucQREncodePNG(text string) ([]byte, error) {
	if strings.TrimSpace(text) == "" {
		return nil, errors.New("empty qr text")
	}
	cmd := exec.Command("qrencode", "-o", "-", "-t", "PNG", "-s", "6", "-m", "2", "--", text)
	out, err := cmd.Output()
	if err != nil {
		return nil, err
	}
	if len(out) < 64 {
		return nil, errors.New("qrencode output too small")
	}
	return out, nil
}

func ucQRPollServiceTicket(client *http.Client, token string) (status string, serviceTicket string, err error) {
	t := strings.TrimSpace(token)
	if t == "" {
		return "", "", errors.New("missing token")
	}
	u, _ := url.Parse("https://api.open.uc.cn/cas/ajax/getServiceTicketByQrcodeToken")
	now := time.Now().UnixMilli()
	qs := u.Query()
	qs.Set("__t", strconv.FormatInt(now, 10))
	qs.Set("__dt", quarkMakeDT(now))
	qs.Set("token", t)
	qs.Set("client_id", ucQRClientID)
	qs.Set("v", "1.2")
	qs.Set("request_id", uuidV4())
	u.RawQuery = qs.Encode()

	body, _, err := ucQRDoReq(client, "GET", u.String(), nil, buildUCHeaders(map[string]string{
		"Referer": ucSSOReferer,
		"Origin":  "https://api.open.uc.cn",
	}))
	if err != nil {
		return "error", "", err
	}

	var raw any
	_ = json.Unmarshal(body, &raw)

	if n, ok := ucExtractFirstNumberByKey(raw, "status"); ok && int64(n) == 2000000 {
		serviceTicket = ucExtractFirstStringByKey(raw, "service_ticket")
		if serviceTicket == "" {
			return "error", "", errors.New("missing service_ticket")
		}
		return "confirmed", serviceTicket, nil
	}

	serviceTicket = ucExtractFirstStringByKey(raw, "service_ticket")
	if strings.TrimSpace(serviceTicket) != "" {
		return "confirmed", serviceTicket, nil
	}

	msg := ucExtractFirstStringByKey(raw, "message")
	if strings.Contains(msg, "扫码") || strings.Contains(msg, "scan") {
		return "scanned", "", nil
	}
	return "pending", "", nil
}

func ucQRFinalizeCookies(client *http.Client, jar http.CookieJar, serviceTicket string) (string, error) {
	if client == nil || jar == nil {
		return "", errors.New("missing client/jar")
	}
	st := strings.TrimSpace(serviceTicket)
	if st == "" {
		return "", errors.New("missing service_ticket")
	}

	openURL, _ := url.Parse("https://api.open.uc.cn/")
	driveURL, _ := url.Parse("https://drive.uc.cn/")
	pcApiURL, _ := url.Parse("https://pc-api.uc.cn/")

	openCookies := jar.Cookies(openURL)
	openCookieStr := formatCookieHeader(openCookies)

	infoURL := "https://drive.uc.cn/account/info?st=" + url.QueryEscape(st) + "&fr=pc&platform=pc"
	_, _, _ = ucQRDoReq(client, "GET", infoURL, nil, buildUCHeaders(map[string]string{
		"Referer": "https://drive.uc.cn/",
		"Origin":  "https://drive.uc.cn",
		"Cookie":  openCookieStr,
		"Accept":  "application/json, text/plain, */*",
	}))

	combined := append([]*http.Cookie{}, openCookies...)
	combined = append(combined, jar.Cookies(driveURL)...)
	combinedCookieStr := formatCookieHeader(combined)
	uploadURL := "https://pc-api.uc.cn/1/clouddrive/transfer/upload/pdir?pr=UCBrowser&fr=pc"
	_, _, _ = ucQRDoReq(client, "POST", uploadURL, []byte(`{}`), buildUCHeaders(map[string]string{
		"Referer":      "https://drive.uc.cn/",
		"Origin":       "https://pc-api.uc.cn",
		"Cookie":       combinedCookieStr,
		"Content-Type": "application/json",
	}))

	finalCookies := append([]*http.Cookie{}, openCookies...)
	finalCookies = append(finalCookies, jar.Cookies(driveURL)...)
	finalCookies = append(finalCookies, jar.Cookies(pcApiURL)...)
	finalCookieStr := formatCookieHeader(finalCookies)
	if strings.TrimSpace(finalCookieStr) == "" {
		return "", errors.New("uc cookie empty")
	}
	up := strings.ToUpper(finalCookieStr)
	if !strings.Contains(up, "PUUS=") && !strings.Contains(up, "PUS=") {
		return "", errors.New("uc cookie incomplete")
	}
	return finalCookieStr, nil
}

func HandleDashboardUCStart(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	now := time.Now()
	cleanupUCQRSessions(now)

	client, jar, err := makeUCQRClient()
	if err != nil {
		writeJSON(w, 500, map[string]any{"success": false, "message": "初始化失败"})
		return
	}
	ucQRInitCookies(client)
	token, err := ucQRGetToken(client)
	if err != nil {
		writeJSON(w, 500, map[string]any{"success": false, "message": err.Error()})
		return
	}
	qrText := ucQRBuildQRText(token)
	if qrText == "" {
		writeJSON(w, 500, map[string]any{"success": false, "message": "二维码生成失败"})
		return
	}
	img, err := ucQREncodePNG(qrText)
	if err != nil {
		writeJSON(w, 500, map[string]any{"success": false, "message": "二维码编码失败"})
		return
	}

	qid := randHexN(12)
	s := &ucQRSession{
		ID:        qid,
		CreatedAt: now,
		ExpiresAt: now.Add(3 * time.Minute),
		Token:     token,
		ClientID:  ucQRClientID,
		Image:     img,
		ImageType: "image/png",
		Client:    client,
		Jar:       jar,
	}
	ucQRSessions.Store(qid, s)

	writeJSON(w, 200, map[string]any{
		"success":   true,
		"qid":       qid,
		"expiresAt": s.ExpiresAt.UnixMilli(),
		"imageUrl":  "/dashboard/pan/uc/image?qid=" + url.QueryEscape(qid) + "&_t=" + url.QueryEscape(strconv.FormatInt(now.UnixMilli(), 10)),
	})
	_ = database
}

func HandleDashboardUCImage(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet {
		methodNotAllowed(w)
		return
	}
	qid := strings.TrimSpace(r.URL.Query().Get("qid"))
	if qid == "" {
		writeJSON(w, 400, map[string]any{"success": false, "message": "qid 不能为空"})
		return
	}
	v, ok := ucQRSessions.Load(qid)
	if !ok {
		writeJSON(w, 404, map[string]any{"success": false, "message": "二维码已过期"})
		return
	}
	s, ok := v.(*ucQRSession)
	if !ok || s == nil || time.Now().After(s.ExpiresAt) {
		ucQRSessions.Delete(qid)
		writeJSON(w, 404, map[string]any{"success": false, "message": "二维码已过期"})
		return
	}
	w.Header().Set("Content-Type", s.ImageType)
	w.Header().Set("Cache-Control", "no-store")
	_, _ = w.Write(s.Image)
}

func HandleDashboardUCCookie(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	var body struct {
		QID string `json:"qid"`
	}
	_ = readJSONLoose(r, &body)
	qid := strings.TrimSpace(body.QID)
	if qid == "" {
		_ = r.ParseForm()
		qid = strings.TrimSpace(r.FormValue("qid"))
	}
	if qid == "" {
		writeJSON(w, 400, map[string]any{"success": false, "message": "qid 不能为空"})
		return
	}
	v, ok := ucQRSessions.Load(qid)
	if !ok {
		writeJSON(w, 404, map[string]any{"success": false, "message": "二维码已过期"})
		return
	}
	s, ok := v.(*ucQRSession)
	if !ok || s == nil || time.Now().After(s.ExpiresAt) {
		ucQRSessions.Delete(qid)
		writeJSON(w, 404, map[string]any{"success": false, "message": "二维码已过期"})
		return
	}

	s.mu.Lock()
	defer s.mu.Unlock()
	if s.Cookie != "" {
		writeJSON(w, 200, map[string]any{"success": true, "status": "confirmed", "cookie": s.Cookie})
		return
	}

	status, st, err := ucQRPollServiceTicket(s.Client, s.Token)
	s.LastStatus = status
	if err != nil {
		s.LastErr = err.Error()
		writeJSON(w, 500, map[string]any{"success": false, "message": err.Error(), "status": "error"})
		return
	}
	if status != "confirmed" {
		writeJSON(w, http.StatusConflict, map[string]any{"success": false, "status": status, "message": "未确认登录"})
		return
	}

	cookie, err := ucQRFinalizeCookies(s.Client, s.Jar, st)
	if err != nil {
		s.LastErr = err.Error()
		writeJSON(w, 500, map[string]any{"success": false, "message": err.Error(), "status": "error"})
		return
	}
	s.Cookie = cookie

	store := readPanLoginSettings(database)
	cur := store["uc"]
	if cur == nil {
		cur = map[string]any{}
	}
	cur["cookie"] = cookie
	store["uc"] = cur
	_ = writePanLoginSettings(database, store)

	writeJSON(w, 200, map[string]any{"success": true, "status": "confirmed", "cookie": cookie})
}
