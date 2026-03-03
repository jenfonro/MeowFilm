package netdisk

import (
	"bytes"
	"compress/gzip"
	"crypto/md5"
	"crypto/rand"
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

type quarkQRSession struct {
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

var quarkQRSessions sync.Map // id -> *quarkQRSession

const (
	quarkQRClientID = "532"
	quarkQRUA       = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36 Edg/121.0.0.0"
	quarkReferer    = "https://pan.quark.cn/"
	quarkSSOReferer = "https://uop.quark.cn/cas/custom/login"
)

func cleanupQuarkQRSessions(now time.Time) {
	quarkQRSessions.Range(func(key, value any) bool {
		s, ok := value.(*quarkQRSession)
		if !ok || s == nil {
			quarkQRSessions.Delete(key)
			return true
		}
		if now.After(s.ExpiresAt) {
			quarkQRSessions.Delete(key)
		}
		return true
	})
}

func randHexN(n int) string {
	if n <= 0 {
		return ""
	}
	b := make([]byte, n)
	_, _ = rand.Read(b)
	return hex.EncodeToString(b)
}

func uuidV4() string {
	b := make([]byte, 16)
	_, _ = rand.Read(b)
	b[6] = (b[6] & 0x0f) | 0x40
	b[8] = (b[8] & 0x3f) | 0x80
	hexStr := hex.EncodeToString(b)
	if len(hexStr) != 32 {
		return randHexN(16)
	}
	return hexStr[0:8] + "-" + hexStr[8:12] + "-" + hexStr[12:16] + "-" + hexStr[16:20] + "-" + hexStr[20:]
}

func quarkMakeDT(nowMs int64) string {
	n := nowMs % 9000
	if n < 0 {
		n = -n
	}
	return strconv.FormatInt(1000+n, 10)
}

func makeQuarkQRClient() (*http.Client, http.CookieJar, error) {
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

func quarkQRDoReq(client *http.Client, method string, urlStr string, body []byte, headers map[string]string) ([]byte, http.Header, error) {
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
		return nil, resp.Header, errors.New("quark http " + strconv.Itoa(resp.StatusCode) + ": " + msg)
	}
	return buf, resp.Header, nil
}

// --- Share list/play (direct cloud-drive API) ---

const (
	quarkShareAPIBase = "https://drive.quark.cn"
	quarkShareReferer = "https://pan.quark.cn/"
	quarkShareUA      = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) quark-cloud-drive/2.5.20 Chrome/100.0.4896.160 Electron/18.3.5.4-b478491100 Safari/537.36 Channel/pckk_other_ch"
)

// QuarkTV (open-api-drive): ported from catpawrunner `panQuark.js`.
const (
	quarkTVAPIBase      = "https://open-api-drive.quark.cn"
	quarkTVCodeAPIBase  = "http://api.extscreen.com/quarkdrive"
	quarkTVClientID     = "d3194e61504e493eb6222857bccfed94"
	quarkTVSignKey      = "kw2dvtd7p4t3pjl2d9ed9yc8yej8kw2d"
	quarkTVAppVer       = "1.8.2.2"
	quarkTVChannel      = "GENERAL"
	quarkTVUA           = "Mozilla/5.0 (Linux; U; Android 13; zh-cn; M2004J7AC Build/UKQ1.231108.001) AppleWebKit/533.1 (KHTML, like Gecko) Mobile Safari/533.1"
	quarkTVTokenSkewMs  = int64(60_000)
	quarkTVBrand        = "Xiaomi"
	quarkTVPlatform     = "tv"
	quarkTVDeviceName   = "M2004J7AC"
	quarkTVDeviceModel  = "M2004J7AC"
	quarkTVBuildDevice  = "M2004J7AC"
	quarkTVBuildProduct = "M2004J7AC"
	quarkTVDeviceGPU    = "Adreno (TM) 550"
	quarkTVActivityRect = "{}"
)

var quarkTVTokenMu sync.Mutex

type quarkPlayCacheEntry struct {
	ExpAt   time.Time
	URL     string
	Headers map[string]string
}

const quarkPlayCacheTTL = 3 * time.Minute

var (
	quarkPlayCacheMu sync.Mutex
	quarkPlayCache   = map[string]quarkPlayCacheEntry{} // key -> entry
)

func getQuarkPlayCache(key string) (string, map[string]string, bool) {
	k := strings.TrimSpace(key)
	if k == "" {
		return "", nil, false
	}
	now := time.Now()
	quarkPlayCacheMu.Lock()
	defer quarkPlayCacheMu.Unlock()
	e, ok := quarkPlayCache[k]
	if !ok {
		return "", nil, false
	}
	if now.After(e.ExpAt) {
		delete(quarkPlayCache, k)
		return "", nil, false
	}
	hdr := map[string]string{}
	for hk, hv := range e.Headers {
		hdr[hk] = hv
	}
	return e.URL, hdr, true
}

func setQuarkPlayCache(key string, urlStr string, headers map[string]string) {
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
	quarkPlayCacheMu.Lock()
	defer quarkPlayCacheMu.Unlock()
	quarkPlayCache[k] = quarkPlayCacheEntry{ExpAt: now.Add(quarkPlayCacheTTL), URL: u, Headers: hdr}
	if len(quarkPlayCache) > 2000 {
		for ck, cv := range quarkPlayCache {
			if now.After(cv.ExpAt) {
				delete(quarkPlayCache, ck)
			}
		}
	}
}

func parseQuarkShareIDFromFlag(flag string) string {
	s := strings.TrimSpace(flag)
	if s == "" {
		return ""
	}
	// Accept raw share urls too.
	if strings.Contains(s, "pan.quark.cn") {
		if u, err := url.Parse(s); err == nil && u != nil {
			parts := strings.Split(strings.Trim(u.Path, "/"), "/")
			if len(parts) >= 2 && parts[0] == "s" && strings.TrimSpace(parts[1]) != "" {
				return strings.TrimSpace(parts[1])
			}
		}
	}
	if strings.HasPrefix(s, "夸父-") {
		return strings.TrimSpace(strings.TrimPrefix(s, "夸父-"))
	}
	if strings.HasPrefix(strings.ToLower(s), "quark-") {
		return strings.TrimSpace(s[6:])
	}
	return ""
}

func sanitizeVodPlayName(value string) string {
	s := strings.ReplaceAll(value, "\r\n", " ")
	s = strings.ReplaceAll(s, "\n", " ")
	s = strings.ReplaceAll(s, "\r", " ")
	s = strings.ReplaceAll(s, "<", "")
	s = strings.ReplaceAll(s, ">", "")
	s = strings.ReplaceAll(s, "《", "")
	s = strings.ReplaceAll(s, "》", "")
	s = strings.ReplaceAll(s, "$", " ")
	s = strings.ReplaceAll(s, "#", " ")
	s = strings.TrimSpace(s)
	if s == "" {
		return ""
	}
	return strings.Join(strings.Fields(s), " ")
}

func sanitizePathSegmentForDisplay(value string) string {
	raw := strings.TrimSpace(value)
	if raw == "" {
		return ""
	}
	raw = strings.ReplaceAll(raw, "/", "_")
	raw = strings.ReplaceAll(raw, "\\", "_")
	return sanitizeVodPlayName(raw)
}

func encodeURIComponent(value string) string {
	if strings.TrimSpace(value) == "" {
		return ""
	}
	enc := url.QueryEscape(value)
	enc = strings.ReplaceAll(enc, "+", "%20")
	enc = strings.ReplaceAll(enc, "*", "%2A")
	return enc
}

func sanitizeQuarkFolderName(value string) string {
	raw := strings.TrimSpace(value)
	if raw == "" || raw == "." || raw == ".." {
		return ""
	}
	cleaned := strings.ReplaceAll(raw, "/", "_")
	cleaned = strings.ReplaceAll(cleaned, "\\", "_")
	cleaned = strings.ReplaceAll(cleaned, "\x00", "")
	cleaned = strings.TrimSpace(cleaned)
	if cleaned == "" || cleaned == "." || cleaned == ".." {
		return ""
	}
	if len(cleaned) > 120 {
		cleaned = cleaned[:120]
	}
	return cleaned
}

func getTVUserFromRequest(r *http.Request) string {
	if r == nil {
		return ""
	}
	return sanitizeQuarkFolderName(strings.TrimSpace(r.Header.Get("X-TV-User")))
}

func buildQuarkShareHeaders(cookie string) http.Header {
	h := http.Header{}
	h.Set("User-Agent", quarkShareUA)
	h.Set("Referer", quarkShareReferer)
	h.Set("Accept", "application/json, text/plain, */*")
	h.Set("Content-Type", "application/json")
	if strings.TrimSpace(cookie) != "" {
		h.Set("Cookie", strings.TrimSpace(cookie))
	}
	return h
}

func quarkShareDoJSON(method string, urlStr string, headers http.Header, body []byte, out any) error {
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
	// If Accept-Encoding is set manually, Go won't auto-decompress gzip responses.
	// Detect gzip payload by header or magic bytes (0x1f 0x8b).
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
		return errors.New("quark http " + strconv.Itoa(resp.StatusCode) + ": " + strings.TrimSpace(string(b)))
	}
	return json.Unmarshal(b, out)
}

type quarkShareTokenResp struct {
	Code    int    `json:"code"`
	Message string `json:"message"`
	Data    struct {
		Stoken string `json:"stoken"`
	} `json:"data"`
}

func quarkShareGetStoken(shareID string, passcode string, cookie string) (string, error) {
	pwdID := strings.TrimSpace(shareID)
	if pwdID == "" {
		return "", errors.New("missing shareId")
	}
	u := quarkShareAPIBase + "/1/clouddrive/share/sharepage/token?pr=ucpro&fr=pc"
	body := map[string]any{"pwd_id": pwdID}
	pc := strings.TrimSpace(passcode)
	if pc != "" {
		body["passcode"] = pc
	}
	b, _ := json.Marshal(body)
	var resp quarkShareTokenResp
	if err := quarkShareDoJSON(http.MethodPost, u, buildQuarkShareHeaders(cookie), b, &resp); err != nil {
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

type quarkShareDetailResp struct {
	Code    int    `json:"code"`
	Message string `json:"message"`
	Data    struct {
		List     []map[string]any `json:"list"`
		Total    int              `json:"total"`
		HasMore  bool             `json:"has_more"`
		NextPage int              `json:"next_page"`
	} `json:"data"`
}

type quarkListDirResp struct {
	Code    int    `json:"code"`
	Message string `json:"message"`
	Data    struct {
		List []map[string]any `json:"list"`
	} `json:"data"`
}

type quarkDeleteFilesResp struct {
	Code    int    `json:"code"`
	Message string `json:"message"`
	Data    any    `json:"data"`
}

func quarkShareDetail(shareID string, stoken string, pdirFid string, page int, size int, cookie string) (quarkShareDetailResp, error) {
	pwdID := strings.TrimSpace(shareID)
	sToken := strings.TrimSpace(stoken)
	if pwdID == "" || sToken == "" {
		return quarkShareDetailResp{}, errors.New("missing quark share parameters")
	}
	pdir := strings.TrimSpace(pdirFid)
	if pdir == "" {
		pdir = "0"
	}
	if page <= 0 {
		page = 1
	}
	if size <= 0 || size > 500 {
		size = 200
	}
	u, _ := url.Parse(quarkShareAPIBase + "/1/clouddrive/share/sharepage/detail?pr=ucpro&fr=pc")
	q := u.Query()
	q.Set("pwd_id", pwdID)
	q.Set("stoken", sToken)
	q.Set("pdir_fid", pdir)
	q.Set("force", "0")
	q.Set("_page", strconv.Itoa(page))
	q.Set("_size", strconv.Itoa(size))
	q.Set("_sort", "file_type:asc,file_name:asc")
	u.RawQuery = q.Encode()

	var resp quarkShareDetailResp
	h := buildQuarkShareHeaders(cookie)
	h.Del("Content-Type")
	if err := quarkShareDoJSON(http.MethodGet, u.String(), h, nil, &resp); err != nil {
		return quarkShareDetailResp{}, err
	}
	if resp.Code != 0 && resp.Code != 200 {
		msg := strings.TrimSpace(resp.Message)
		if msg == "" {
			msg = "share detail failed"
		}
		return quarkShareDetailResp{}, errors.New(msg)
	}
	return resp, nil
}

func quarkListDir(pdirFid string, cookie string, size int) (quarkListDirResp, error) {
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
	u, _ := url.Parse(quarkShareAPIBase + "/1/clouddrive/file/sort?pr=ucpro&fr=pc")
	q := u.Query()
	q.Set("pdir_fid", fid)
	q.Set("_fetch_total", "1")
	q.Set("_size", strconv.Itoa(sz))
	q.Set("_sort", "file_type:asc,file_name:asc")
	u.RawQuery = q.Encode()
	var resp quarkListDirResp
	h := buildQuarkShareHeaders(cookie)
	h.Del("Content-Type")
	if err := quarkShareDoJSON(http.MethodGet, u.String(), h, nil, &resp); err != nil {
		return quarkListDirResp{}, err
	}
	if resp.Code != 0 && resp.Code != 200 {
		msg := strings.TrimSpace(resp.Message)
		if msg == "" {
			msg = "list dir failed"
		}
		return quarkListDirResp{}, errors.New(msg)
	}
	return resp, nil
}

func quarkDeleteFiles(fids []string, cookie string) error {
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
	u := quarkShareAPIBase + "/1/clouddrive/file/delete?pr=ucpro&fr=pc"
	body := map[string]any{"action_type": 2, "filelist": list, "exclude_fids": []any{}}
	b, _ := json.Marshal(body)
	var resp quarkDeleteFilesResp
	if err := quarkShareDoJSON(http.MethodPost, u, buildQuarkShareHeaders(cookie), b, &resp); err != nil {
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

func quarkClearDir(pdirFid string, cookie string) error {
	fid := strings.TrimSpace(pdirFid)
	if fid == "" || fid == "0" {
		return errors.New("refuse to clear root (pdir_fid=0)")
	}
	sortResp, err := quarkListDir(fid, cookie, 500)
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
	return quarkDeleteFiles(fids, cookie)
}

func quarkEnsureFolderFid(name string, cookie string, parentFid string) (string, error) {
	folderName := strings.TrimSpace(name)
	if folderName == "" {
		return "", errors.New("missing folder name")
	}
	parent := strings.TrimSpace(parentFid)
	if parent == "" {
		parent = "0"
	}
	sortResp, err := quarkListDir(parent, cookie, 500)
	if err == nil {
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
				return fid, nil
			}
		}
	}
	createURL := quarkShareAPIBase + "/1/clouddrive/file?pr=ucpro&fr=pc"
	body := map[string]any{
		"pdir_fid":      parent,
		"file_name":     folderName,
		"dir_path":      "",
		"dir_init_lock": false,
	}
	b, _ := json.Marshal(body)
	var out map[string]any
	if err := quarkShareDoJSON(http.MethodPost, createURL, buildQuarkShareHeaders(cookie), b, &out); err != nil {
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

func quarkShareSave(shareID string, stoken string, fid string, fidToken string, toPdirFid string, cookie string) (savedFid string, err error) {
	pwdID := strings.TrimSpace(shareID)
	sToken := strings.TrimSpace(stoken)
	fID := strings.TrimSpace(fid)
	fToken := strings.TrimSpace(fidToken)
	toPdir := strings.TrimSpace(toPdirFid)
	if pwdID == "" || sToken == "" || fID == "" || fToken == "" {
		return "", errors.New("missing quark share parameters")
	}
	if toPdir == "" || toPdir == "0" {
		return "", errors.New("missing to_pdir_fid")
	}
	saveURL := quarkShareAPIBase + "/1/clouddrive/share/sharepage/save?pr=ucpro&fr=pc"
	taskURLBase := quarkShareAPIBase + "/1/clouddrive/task?pr=ucpro&fr=pc"
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
	if err := quarkShareDoJSON(http.MethodPost, saveURL, buildQuarkShareHeaders(cookie), b, &saveResp); err != nil {
		return "", err
	}
	// Quark APIs usually return HTTP 200 even on application-level errors.
	// If we don't surface their code/message, callers only see "task_id not found" which is misleading.
	if saveResp != nil {
		code := strings.TrimSpace(toString(saveResp["code"]))
		if code != "" && code != "0" && code != "200" {
			msg := strings.TrimSpace(toString(saveResp["message"]))
			if msg == "" {
				msg = strings.TrimSpace(toString(saveResp["msg"]))
			}
			if msg == "" {
				msg = "quark save failed"
			}
			return "", errors.New(msg + " (code=" + code + ")")
		}
	}
	taskID := strings.TrimSpace(quarkExtractFirstStringByKeys(saveResp, []string{"task_id", "taskid"}))
	if taskID == "" {
		// Best-effort fallback: include api message if present.
		msg := ""
		if saveResp != nil {
			msg = strings.TrimSpace(toString(saveResp["message"]))
			if msg == "" {
				msg = strings.TrimSpace(toString(saveResp["msg"]))
			}
		}
		if msg != "" {
			return "", errors.New("quark save failed: " + msg)
		}
		return "", errors.New("quark save: task_id not found")
	}

	deadline := time.Now().Add(30 * time.Second)
	var lastTask map[string]any
	for time.Now().Before(deadline) {
		u, _ := url.Parse(taskURLBase)
		q := u.Query()
		q.Set("task_id", taskID)
		u.RawQuery = q.Encode()
		var taskResp map[string]any
		h := buildQuarkShareHeaders(cookie)
		h.Del("Content-Type")
		if err := quarkShareDoJSON(http.MethodGet, u.String(), h, nil, &taskResp); err != nil {
			lastTask = taskResp
			break
		}
		lastTask = taskResp
		if taskResp != nil {
			code := strings.TrimSpace(toString(taskResp["code"]))
			if code != "" && code != "0" && code != "200" {
				msg := strings.TrimSpace(toString(taskResp["message"]))
				if msg == "" {
					msg = strings.TrimSpace(toString(taskResp["msg"]))
				}
				if msg == "" {
					msg = "quark task query failed"
				}
				return "", errors.New(msg + " (code=" + code + ")")
			}
		}
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
		if td, _ := lastTask["data"].(map[string]any); td != nil {
			if sa, _ := td["save_as"].(map[string]any); sa != nil {
				if arr, ok := sa["save_as_top_fids"].([]any); ok && len(arr) > 0 {
					savedFid = strings.TrimSpace(toString(arr[0]))
				} else if arr, ok := sa["save_as_top_fid"].([]any); ok && len(arr) > 0 {
					savedFid = strings.TrimSpace(toString(arr[0]))
				} else if v := strings.TrimSpace(toString(sa["save_as_top_fid"])); v != "" {
					savedFid = v
				}
			}
		}
	}
	if savedFid == "" {
		return "", errors.New("quark save: saved fid not found")
	}
	return savedFid, nil
}

func quarkShareIsDirItem(it map[string]any) bool {
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
	kind := strings.ToLower(strings.TrimSpace(toString(it["type"])))
	return kind == "folder" || kind == "dir" || kind == "directory"
}

func quarkShareItemFid(it map[string]any) string {
	if it == nil {
		return ""
	}
	if v := strings.TrimSpace(toString(it["fid"])); v != "" {
		return v
	}
	return strings.TrimSpace(toString(it["file_id"]))
}

func quarkShareItemFidToken(it map[string]any) string {
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

func quarkShareItemName(it map[string]any) string {
	if it == nil {
		return ""
	}
	if v := strings.TrimSpace(toString(it["file_name"])); v != "" {
		return v
	}
	return strings.TrimSpace(toString(it["name"]))
}

func quarkShareListDirAllPages(shareID string, stoken string, pdirFid string, cookie string, maxPages int, pageSize int) ([]map[string]any, error) {
	pwdID := strings.TrimSpace(shareID)
	sToken := strings.TrimSpace(stoken)
	if pwdID == "" || sToken == "" {
		return nil, errors.New("missing quark share parameters")
	}
	maxP := maxPages
	if maxP <= 0 || maxP > 500 {
		maxP = 200
	}
	sz := pageSize
	if sz <= 0 || sz > 500 {
		sz = 200
	}

	out := make([]map[string]any, 0, sz)
	seen := map[string]struct{}{}
	total := -1
	page := 1
	for page <= maxP {
		resp, err := quarkShareDetail(pwdID, sToken, pdirFid, page, sz, cookie)
		if err != nil {
			return nil, err
		}
		list := resp.Data.List
		if len(list) == 0 {
			break
		}
		if total < 0 && resp.Data.Total > 0 {
			total = resp.Data.Total
		}
		for _, it := range list {
			if it == nil {
				continue
			}
			fid := quarkShareItemFid(it)
			if fid == "" {
				continue
			}
			if _, ok := seen[fid]; ok {
				continue
			}
			seen[fid] = struct{}{}
			out = append(out, it)
		}
		if total > 0 && len(out) >= total {
			break
		}
		if resp.Data.NextPage > page {
			page = resp.Data.NextPage
			continue
		}
		if resp.Data.HasMore {
			page++
			continue
		}
		if len(list) >= sz {
			page++
			continue
		}
		break
	}
	return out, nil
}

func quarkListUncached(database *db.DB, flag string, passcode string) (string, string, error) {
	shareID := parseQuarkShareIDFromFlag(flag)
	if shareID == "" {
		return "", "", errors.New("missing/invalid flag (expected: 夸父-<shareId>)")
	}
	store := readPanLoginSettings(database)
	cookie := getPanField(store, "quark", "cookie")
	if cookie == "" {
		return "", "", errors.New("missing quark cookie (pan_login_settings[\"quark\"].cookie)")
	}
	stoken, err := quarkShareGetStoken(shareID, passcode, cookie)
	if err != nil {
		return "", shareID, err
	}

	const maxItems = 20000
	const pageSize = 200
	const maxDepth = 20

	startFid := "0"
	rootPrefix := "根目录"
	if rootItems, e := quarkShareListDirAllPages(shareID, stoken, "0", cookie, 2, pageSize); e == nil {
		rootDirs := []map[string]any{}
		rootFileCount := 0
		for _, it := range rootItems {
			if it == nil {
				continue
			}
			if quarkShareIsDirItem(it) {
				if fid := quarkShareItemFid(it); fid != "" {
					rootDirs = append(rootDirs, it)
				}
				continue
			}
			fid := quarkShareItemFid(it)
			fidToken := quarkShareItemFidToken(it)
			name := strings.TrimSpace(quarkShareItemName(it))
			if fid != "" && fidToken != "" && isSupportedVideoFilename(name) {
				rootFileCount++
			}
		}
		if rootFileCount == 0 && len(rootDirs) == 1 {
			if fid := quarkShareItemFid(rootDirs[0]); fid != "" {
				startFid = fid
				if n := strings.TrimSpace(quarkShareItemName(rootDirs[0])); n != "" {
					rootPrefix = n
				}
			}
		}
	}

	parts := []string{}
	visited := map[string]struct{}{}

	var walk func(pdir string, depth int, pathSegs []string) error
	walk = func(pdir string, depth int, pathSegs []string) error {
		if len(parts) >= maxItems {
			return nil
		}
		if depth > maxDepth {
			return nil
		}
		key := strings.TrimSpace(pdir)
		if key == "" {
			key = "0"
		}
		if _, ok := visited[key]; ok {
			return nil
		}
		visited[key] = struct{}{}

		items, err := quarkShareListDirAllPages(shareID, stoken, key, cookie, 200, pageSize)
		if err != nil {
			return err
		}

		for _, it := range items {
			if it == nil {
				continue
			}
			if quarkShareIsDirItem(it) {
				fid := quarkShareItemFid(it)
				if fid == "" {
					continue
				}
				dirName := quarkShareItemName(it)
				seg := sanitizePathSegmentForDisplay(dirName)
				if seg == "" {
					seg = strings.TrimSpace(dirName)
				}
				if seg == "" {
					seg = fid
				}
				if err := walk(fid, depth+1, append(pathSegs, seg)); err != nil {
					return err
				}
				continue
			}

			fid := quarkShareItemFid(it)
			fidToken := quarkShareItemFidToken(it)
			name := quarkShareItemName(it)
			if fid == "" || fidToken == "" || name == "" || !isSupportedVideoFilename(name) {
				continue
			}
			display := "/"
			if len(pathSegs) > 0 {
				display = "/" + strings.Join(pathSegs, "/")
			}
			display = prefixRootDirDisplay(display, rootPrefix)
			id := shareID + "*" + stoken + "*" + fid + "*" + fidToken
			if strings.TrimSpace(name) != "" {
				id = id + "***" + strings.TrimSpace(name)
			}
			parts = append(parts, sanitizeVodPlayName(display)+"$"+id)
			if len(parts) >= maxItems {
				break
			}
		}
		return nil
	}

	if err := walk(startFid, 0, []string{}); err != nil {
		return "", shareID, err
	}
	return strings.Join(parts, "#"), shareID, nil
}

func QuarkList(database *db.DB, flag string, passcode string) (string, string, error) {
	vod, shareID, _, err := QuarkListWithCacheHit(database, flag, passcode)
	return vod, shareID, err
}

func QuarkListWithCacheHit(database *db.DB, flag string, passcode string) (vod string, shareID string, fromCache bool, err error) {
	key := listCacheKey("quark_list", flag, passcode)
	got, hit, err := quarkListCacheTwoTier.Do(key, func() (listCache2, error) {
		vod, shareID, e := quarkListUncached(database, flag, passcode)
		if e != nil {
			return listCache2{}, e
		}
		return listCache2{Vod: vod, ShareID: shareID}, nil
	})
	return strings.TrimSpace(got.Vod), strings.TrimSpace(got.ShareID), hit, err
}

func parseQuarkPlayID(id string) (shareID string, stoken string, fid string, fidToken string, fileName string) {
	raw := strings.TrimSpace(id)
	if raw == "" {
		return "", "", "", "", ""
	}
	p := strings.SplitN(raw, "***", 2)
	left := p[0]
	if len(p) == 2 {
		fileName = strings.TrimSpace(p[1])
	}
	parts := strings.Split(left, "*")
	if len(parts) >= 1 {
		shareID = strings.TrimSpace(parts[0])
	}
	if len(parts) >= 2 {
		stoken = strings.TrimSpace(parts[1])
	}
	if len(parts) >= 3 {
		fid = strings.TrimSpace(parts[2])
	}
	if len(parts) >= 4 {
		fidToken = strings.TrimSpace(parts[3])
	}
	return
}

type quarkDownloadResp struct {
	Data any `json:"data"`
}

func normalizeQuarkWant(want string) string {
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

func quarkDirectDownload(fid string, fidToken string, cookie string, want string) (string, error) {
	fID := strings.TrimSpace(fid)
	if fID == "" {
		return "", errors.New("missing fid")
	}
	wantMode := strings.TrimSpace(want)
	if wantMode == "" {
		wantMode = "download_url"
	}
	u := quarkShareAPIBase + "/1/clouddrive/file/download?pr=ucpro&fr=pc"
	body := map[string]any{"fid": fID, "fids": []any{fID}}
	if strings.TrimSpace(fidToken) != "" {
		body["fid_token"] = strings.TrimSpace(fidToken)
		body["fid_token_list"] = []any{strings.TrimSpace(fidToken)}
	}
	b, _ := json.Marshal(body)
	var resp quarkDownloadResp
	if err := quarkShareDoJSON(http.MethodPost, u, buildQuarkShareHeaders(cookie), b, &resp); err != nil {
		return "", err
	}
	out := quarkExtractFirstStringByKeys(resp.Data, []string{wantMode, "download_url", "play_url", "url"})
	if strings.TrimSpace(out) == "" {
		return "", errors.New("direct download url not found")
	}
	return strings.TrimSpace(out), nil
}

func quarkMD5Hex(s string) string {
	sum := md5.Sum([]byte(s))
	return hex.EncodeToString(sum[:])
}

func quarkSHA256Hex(s string) string {
	sum := sha256.Sum256([]byte(s))
	return hex.EncodeToString(sum[:])
}

func quarkTVGenerateReqSign(method string, pathname string, deviceID string) (tm string, xPanToken string, reqID string) {
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
	reqID = quarkMD5Hex(dev + ts)
	tokenData := m + "&" + p + "&" + ts + "&" + quarkTVSignKey
	xPanToken = quarkSHA256Hex(tokenData)
	return ts, xPanToken, reqID
}

type quarkTVRefreshResp struct {
	Code int `json:"code"`
	Data struct {
		AccessToken  string `json:"access_token"`
		RefreshToken string `json:"refresh_token"`
		ExpiresIn    int64  `json:"expires_in"`
	} `json:"data"`
	Message string `json:"message"`
	Msg     string `json:"msg"`
}

func quarkTVRefreshAccessToken(refreshToken string, deviceID string) (accessToken string, nextRefresh string, expAtMs int64, err error) {
	rt := strings.TrimSpace(refreshToken)
	dev := strings.TrimSpace(deviceID)
	if rt == "" {
		return "", "", 0, errors.New("missing quark_tv refresh_token")
	}
	if dev == "" {
		return "", "", 0, errors.New("missing quark_tv device_id")
	}
	_, _, reqID := quarkTVGenerateReqSign(http.MethodPost, "/token", dev)
	u := strings.TrimSpace(quarkTVCodeAPIBase + "/token")
	payload := map[string]any{
		"req_id":        reqID,
		"app_ver":       quarkTVAppVer,
		"device_id":     dev,
		"device_brand":  quarkTVBrand,
		"platform":      quarkTVPlatform,
		"device_name":   quarkTVDeviceName,
		"device_model":  quarkTVDeviceModel,
		"build_device":  quarkTVBuildDevice,
		"build_product": quarkTVBuildProduct,
		"device_gpu":    quarkTVDeviceGPU,
		"activity_rect": quarkTVActivityRect,
		"channel":       quarkTVChannel,
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
	req.Header.Set("User-Agent", quarkTVUA)
	resp, err := client.Do(req)
	if err != nil {
		return "", "", 0, err
	}
	defer resp.Body.Close()
	body, _ := io.ReadAll(resp.Body)
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		msg := strings.TrimSpace(string(body))
		return "", "", 0, errors.New("quark_tv http " + strconv.Itoa(resp.StatusCode) + ": " + msg)
	}
	var out quarkTVRefreshResp
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
		return "", "", 0, errors.New("quark_tv refresh failed: " + msg)
	}
	at := strings.TrimSpace(out.Data.AccessToken)
	if at == "" {
		return "", "", 0, errors.New("quark_tv refresh failed: empty access_token")
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

type quarkTVFileResp struct {
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

func quarkTVIsAccessTokenInvalid(resp quarkTVFileResp) bool {
	if resp.Status == -1 && resp.Errno == 10001 {
		return true
	}
	msg := strings.ToLower(strings.TrimSpace(resp.ErrorInfo))
	if msg == "" {
		msg = strings.ToLower(strings.TrimSpace(resp.Message))
	}
	if msg == "" {
		return false
	}
	return strings.Contains(msg, "access token") ||
		strings.Contains(msg, "access_token") ||
		strings.Contains(msg, "token无效") ||
		strings.Contains(msg, "token 无效")
}

func quarkTVLinkByFid(fid string, accessToken string, deviceID string, method string) (string, quarkTVFileResp, error) {
	fid2 := strings.TrimSpace(fid)
	if fid2 == "" {
		return "", quarkTVFileResp{}, errors.New("missing fid")
	}
	at := strings.TrimSpace(accessToken)
	dev := strings.TrimSpace(deviceID)
	if at == "" {
		return "", quarkTVFileResp{}, errors.New("missing quark_tv access_token")
	}
	if dev == "" {
		return "", quarkTVFileResp{}, errors.New("missing quark_tv device_id")
	}
	m := strings.ToLower(strings.TrimSpace(method))
	apiMethod := "streaming"
	if m == "download" {
		apiMethod = "download"
	}
	tm, xPanToken, reqID := quarkTVGenerateReqSign(http.MethodGet, "/file", dev)

	u, _ := url.Parse(quarkTVAPIBase + "/file")
	q := u.Query()
	q.Set("req_id", reqID)
	q.Set("access_token", at)
	q.Set("app_ver", quarkTVAppVer)
	q.Set("device_id", dev)
	q.Set("device_brand", quarkTVBrand)
	q.Set("platform", quarkTVPlatform)
	q.Set("device_name", quarkTVDeviceName)
	q.Set("device_model", quarkTVDeviceModel)
	q.Set("build_device", quarkTVBuildDevice)
	q.Set("build_product", quarkTVBuildProduct)
	q.Set("device_gpu", quarkTVDeviceGPU)
	q.Set("activity_rect", quarkTVActivityRect)
	q.Set("channel", quarkTVChannel)
	q.Set("method", apiMethod)
	q.Set("group_by", "source")
	q.Set("fid", fid2)
	q.Set("resolution", "low,normal,high,super,2k,4k")
	q.Set("support", "dolby_vision")
	u.RawQuery = q.Encode()

	client := &http.Client{Timeout: 18 * time.Second, Transport: netdiskHTTPTransport}
	req, err := http.NewRequest(http.MethodGet, u.String(), nil)
	if err != nil {
		return "", quarkTVFileResp{}, err
	}
	req.Header.Set("Accept", "application/json, text/plain, */*")
	req.Header.Set("User-Agent", quarkTVUA)
	req.Header.Set("x-pan-tm", tm)
	req.Header.Set("x-pan-token", xPanToken)
	req.Header.Set("x-pan-client-id", quarkTVClientID)
	resp, err := client.Do(req)
	if err != nil {
		return "", quarkTVFileResp{}, err
	}
	defer resp.Body.Close()
	body, _ := io.ReadAll(resp.Body)
	var out quarkTVFileResp
	if err := json.Unmarshal(bytes.TrimSpace(body), &out); err != nil {
		if resp.StatusCode < 200 || resp.StatusCode >= 300 {
			return "", quarkTVFileResp{}, errors.New("quark_tv http " + strconv.Itoa(resp.StatusCode) + ": " + strings.TrimSpace(string(body)))
		}
		return "", quarkTVFileResp{}, err
	}
	// Even when HTTP is 4xx, QuarkTV may return a structured JSON body with errno/status.
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
			return "", out, errors.New("quark_tv errno=" + strconv.Itoa(out.Errno) + ": " + msg)
		}
		return "", out, errors.New("quark_tv http " + strconv.Itoa(resp.StatusCode) + ": " + msg)
	}
	if out.Errno != 0 {
		msg := strings.TrimSpace(out.ErrorInfo)
		if msg == "" {
			msg = strings.TrimSpace(out.Message)
		}
		if msg == "" {
			msg = "request failed"
		}
		return "", out, errors.New("quark_tv errno=" + strconv.Itoa(out.Errno) + ": " + msg)
	}
	if apiMethod == "download" {
		dl := strings.TrimSpace(out.Data.DownloadURL)
		if dl == "" {
			return "", out, errors.New("quark_tv download_url not found")
		}
		return dl, out, nil
	}
	for _, it := range out.Data.VideoInfo {
		if u := strings.TrimSpace(it.URL); u != "" {
			return u, out, nil
		}
	}
	return "", out, errors.New("quark_tv streaming url not found")
}

func ensureQuarkTVAccessToken(database *db.DB) (accessToken string, deviceID string, err error) {
	store := readPanLoginSettings(database)
	tvKey := pickQuarkTVStoreKey(store)
	rt := getPanField(store, tvKey, "refresh_token")
	dev := getPanField(store, tvKey, "device_id")
	at := getPanField(store, tvKey, "access_token")
	expAtRaw := getPanField(store, tvKey, "access_token_exp_at")
	expAtMs := parsePanInt64(expAtRaw)

	if at != "" && expAtMs > 0 {
		if time.Now().UnixMilli()+quarkTVTokenSkewMs < expAtMs {
			return at, dev, nil
		}
	}
	if at != "" && expAtMs == 0 {
		return at, dev, nil
	}
	if rt == "" || dev == "" {
		return "", "", errors.New("missing quark_tv credentials (refresh_token + device_id)")
	}

	quarkTVTokenMu.Lock()
	defer quarkTVTokenMu.Unlock()

	store = readPanLoginSettings(database)
	tvKey = pickQuarkTVStoreKey(store)
	rt = getPanField(store, tvKey, "refresh_token")
	dev = getPanField(store, tvKey, "device_id")
	at = getPanField(store, tvKey, "access_token")
	expAtRaw = getPanField(store, tvKey, "access_token_exp_at")
	expAtMs = parsePanInt64(expAtRaw)
	if at != "" && expAtMs > 0 {
		if time.Now().UnixMilli()+quarkTVTokenSkewMs < expAtMs {
			return at, dev, nil
		}
	}
	if at != "" && expAtMs == 0 {
		return at, dev, nil
	}

	newAT, newRT, newExpAt, err := quarkTVRefreshAccessToken(rt, dev)
	if err != nil {
		return "", "", err
	}
	setPanField(store, tvKey, "access_token", newAT)
	if newExpAt > 0 {
		setPanField(store, tvKey, "access_token_exp_at", strconv.FormatInt(newExpAt, 10))
	} else {
		setPanField(store, tvKey, "access_token_exp_at", "")
	}
	if newRT != "" && newRT != rt {
		setPanField(store, tvKey, "refresh_token", newRT)
	}
	if err := writePanLoginSettings(database, store); err != nil {
		log.Printf("[quark_tv] save access_token failed: %v", err)
	}
	return newAT, dev, nil
}

func pickQuarkTVStoreKey(store panLoginSettingsStore) string {
	candidates := []string{"quark_tv", "quarktv"}
	for _, k := range candidates {
		if strings.TrimSpace(getPanField(store, k, "refresh_token")) != "" && strings.TrimSpace(getPanField(store, k, "device_id")) != "" {
			return k
		}
	}
	for _, k := range candidates {
		if strings.TrimSpace(getPanField(store, k, "access_token")) != "" || strings.TrimSpace(getPanField(store, k, "device_id")) != "" {
			return k
		}
	}
	return "quark_tv"
}

func parsePanInt64(value string) int64 {
	s := strings.TrimSpace(value)
	if s == "" {
		return 0
	}
	if n, err := strconv.ParseInt(s, 10, 64); err == nil {
		return n
	}
	// Settings may be stored as JSON numbers, which can round-trip through float formatting.
	if f, err := strconv.ParseFloat(s, 64); err == nil && f > 0 {
		return int64(f)
	}
	return 0
}

func refreshQuarkTVAccessToken(database *db.DB, tvKey string, refreshToken string, deviceID string) (string, string, error) {
	rt := strings.TrimSpace(refreshToken)
	dev := strings.TrimSpace(deviceID)
	if rt == "" || dev == "" {
		return "", "", errors.New("missing quark_tv refresh_token/device_id")
	}
	newAT, newRT, newExpAt, err := quarkTVRefreshAccessToken(rt, dev)
	if err != nil {
		return "", "", err
	}
	store := readPanLoginSettings(database)
	key := strings.TrimSpace(tvKey)
	if key == "" {
		key = pickQuarkTVStoreKey(store)
	}
	setPanField(store, key, "access_token", strings.TrimSpace(newAT))
	if newExpAt > 0 {
		setPanField(store, key, "access_token_exp_at", strconv.FormatInt(newExpAt, 10))
	} else {
		setPanField(store, key, "access_token_exp_at", "")
	}
	if strings.TrimSpace(newRT) != "" && strings.TrimSpace(newRT) != rt {
		setPanField(store, key, "refresh_token", strings.TrimSpace(newRT))
	}
	if err := writePanLoginSettings(database, store); err != nil {
		log.Printf("[quark_tv] save access_token failed: %v", err)
	}
	return strings.TrimSpace(newAT), dev, nil
}

func quarkEnsurePlayDirFid(cookie string, tvUser string) (string, error) {
	rootFid, err := quarkEnsureFolderFid("MeowFilm", cookie, "0")
	if err != nil {
		return "", err
	}
	user := sanitizeQuarkFolderName(tvUser)
	if user == "" {
		return rootFid, nil
	}
	return quarkEnsureFolderFid(user, cookie, rootFid)
}

func quarkPickFirstFileInDir(pdirFid string, cookie string) (fid string, fidToken string, err error) {
	sortResp, err := quarkListDir(pdirFid, cookie, 200)
	if err != nil {
		return "", "", err
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
		if isDir {
			continue
		}
		id := strings.TrimSpace(toString(it["fid"]))
		if id == "" {
			id = strings.TrimSpace(toString(it["file_id"]))
		}
		if id == "" {
			id = strings.TrimSpace(toString(it["id"]))
		}
		if id == "" {
			continue
		}
		tok := strings.TrimSpace(toString(it["share_fid_token"]))
		if tok == "" {
			tok = strings.TrimSpace(toString(it["fid_token"]))
		}
		if tok == "" {
			tok = strings.TrimSpace(toString(it["fidToken"]))
		}
		if tok == "" {
			tok = strings.TrimSpace(toString(it["token"]))
		}
		return id, tok, nil
	}
	return "", "", errors.New("destination folder is empty")
}

func quarkPickFileInDirPrefer(pdirFid string, cookie string, preferredFid string) (fid string, fidToken string, err error) {
	pref := strings.TrimSpace(preferredFid)
	if pref == "" {
		return quarkPickFirstFileInDir(pdirFid, cookie)
	}
	sortResp, err := quarkListDir(pdirFid, cookie, 200)
	if err != nil {
		return "", "", err
	}
	var firstFid string
	var firstTok string
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
		if isDir {
			continue
		}
		id := strings.TrimSpace(toString(it["fid"]))
		if id == "" {
			id = strings.TrimSpace(toString(it["file_id"]))
		}
		if id == "" {
			id = strings.TrimSpace(toString(it["id"]))
		}
		if id == "" {
			continue
		}
		tok := strings.TrimSpace(toString(it["share_fid_token"]))
		if tok == "" {
			tok = strings.TrimSpace(toString(it["fid_token"]))
		}
		if tok == "" {
			tok = strings.TrimSpace(toString(it["fidToken"]))
		}
		if tok == "" {
			tok = strings.TrimSpace(toString(it["token"]))
		}
		if firstFid == "" {
			firstFid = id
			firstTok = tok
		}
		if id == pref {
			return id, tok, nil
		}
	}
	if firstFid != "" {
		return firstFid, firstTok, nil
	}
	return "", "", errors.New("destination folder is empty")
}

func quarkPlayImpl(database *db.DB, id string, want string, tvUser string) (string, map[string]string, error) {
	rawID := strings.TrimSpace(id)
	shareID, stoken, fid, fidToken, _ := parseQuarkPlayID(rawID)
	if shareID == "" || stoken == "" || fid == "" || fidToken == "" {
		return "", nil, errors.New("invalid id")
	}

	store := readPanLoginSettings(database)
	cookie := strings.TrimSpace(getPanField(store, "quark", "cookie"))
	if cookie == "" {
		return "", nil, errors.New("missing quark cookie (pan_login_settings[\"quark\"].cookie)")
	}

	wantMode := normalizeQuarkWant(want)
	if wantMode == "" {
		wantMode = "download_url"
	}

	user := sanitizeQuarkFolderName(tvUser)
	if strings.TrimSpace(tvUser) != "" && user == "" {
		return "", nil, errors.New("missing X-TV-User")
	}

	cacheKey := rawID + "|" + wantMode
	if user != "" {
		cacheKey = "v2|" + user + "|" + cacheKey
	}
	if u, h, ok := getQuarkPlayCache(cacheKey); ok && u != "" {
		if h != nil {
			if strings.TrimSpace(h["Cookie"]) == "" {
				h = map[string]string{}
			}
		}
		return u, h, nil
	}

	toPdir, err := quarkEnsurePlayDirFid(cookie, user)
	if err != nil {
		return "", nil, err
	}

	if err := quarkClearDir(toPdir, cookie); err != nil {
		return "", nil, err
	}

	savedFid, err := quarkShareSave(shareID, stoken, fid, fidToken, toPdir, cookie)
	if err != nil {
		return "", nil, err
	}

	pickedFid := strings.TrimSpace(savedFid)
	pickedToken := ""
	if pickedFid == "" {
		pickedFid, pickedToken, err = quarkPickFirstFileInDir(toPdir, cookie)
		if err != nil {
			return "", nil, err
		}
	}

	playURL := ""
	downloadURL := ""
	headerPlay := map[string]string{}
	headerDownload := map[string]string{}

	tvKey := pickQuarkTVStoreKey(store)
	hasTV := strings.TrimSpace(getPanField(store, tvKey, "refresh_token")) != "" && strings.TrimSpace(getPanField(store, tvKey, "device_id")) != ""
	if hasTV {
		rt := strings.TrimSpace(getPanField(store, tvKey, "refresh_token"))
		devCfg := strings.TrimSpace(getPanField(store, tvKey, "device_id"))
		at, dev, e := ensureQuarkTVAccessToken(database)
		if e == nil && at != "" && dev != "" {
			if u1, resp1, e1 := quarkTVLinkByFid(pickedFid, at, dev, "streaming"); e1 == nil {
				playURL = strings.TrimSpace(u1)
			} else if quarkTVIsAccessTokenInvalid(resp1) && rt != "" && devCfg != "" {
				if at2, dev2, eR := refreshQuarkTVAccessToken(database, tvKey, rt, devCfg); eR == nil && at2 != "" && dev2 != "" {
					if u1b, _, e1b := quarkTVLinkByFid(pickedFid, at2, dev2, "streaming"); e1b == nil {
						playURL = strings.TrimSpace(u1b)
					}
				}
			}

			if u2, resp2, e2 := quarkTVLinkByFid(pickedFid, at, dev, "download"); e2 == nil {
				downloadURL = strings.TrimSpace(u2)
			} else if quarkTVIsAccessTokenInvalid(resp2) && rt != "" && devCfg != "" {
				if at2, dev2, eR := refreshQuarkTVAccessToken(database, tvKey, rt, devCfg); eR == nil && at2 != "" && dev2 != "" {
					if u2b, _, e2b := quarkTVLinkByFid(pickedFid, at2, dev2, "download"); e2b == nil {
						downloadURL = strings.TrimSpace(u2b)
					}
				}
			}
		} else if rt != "" && devCfg != "" {
			// Missing access_token or expired -> refresh.
			if at2, dev2, eR := refreshQuarkTVAccessToken(database, tvKey, rt, devCfg); eR == nil && at2 != "" && dev2 != "" {
				if u1, _, e1 := quarkTVLinkByFid(pickedFid, at2, dev2, "streaming"); e1 == nil {
					playURL = strings.TrimSpace(u1)
				}
				if u2, _, e2 := quarkTVLinkByFid(pickedFid, at2, dev2, "download"); e2 == nil {
					downloadURL = strings.TrimSpace(u2)
				}
			}
		}
	}

	if playURL == "" {
		u, e := quarkDirectDownload(pickedFid, pickedToken, cookie, "play_url")
		if e != nil && pickedToken == "" {
			if fid2, tok2, e2 := quarkPickFileInDirPrefer(toPdir, cookie, pickedFid); e2 == nil && fid2 != "" {
				pickedFid = fid2
				pickedToken = tok2
			}
			u, e = quarkDirectDownload(pickedFid, pickedToken, cookie, "play_url")
		}
		if e != nil {
			return "", nil, e
		}
		playURL = strings.TrimSpace(u)
		headerPlay = map[string]string{"Cookie": cookie, "Referer": quarkShareReferer, "User-Agent": quarkShareUA}
	}
	if downloadURL == "" {
		u, e := quarkDirectDownload(pickedFid, pickedToken, cookie, "download_url")
		if e != nil && pickedToken == "" {
			if fid2, tok2, e2 := quarkPickFileInDirPrefer(toPdir, cookie, pickedFid); e2 == nil && fid2 != "" {
				pickedFid = fid2
				pickedToken = tok2
			}
			u, e = quarkDirectDownload(pickedFid, pickedToken, cookie, "download_url")
		}
		if e != nil {
			return "", nil, e
		}
		downloadURL = strings.TrimSpace(u)
		headerDownload = map[string]string{"Cookie": cookie, "Referer": quarkShareReferer, "User-Agent": quarkShareUA}
	}

	selectedURL := playURL
	selectedHeaders := headerPlay
	if wantMode == "download_url" {
		if downloadURL != "" {
			selectedURL = downloadURL
			selectedHeaders = headerDownload
		} else if playURL != "" {
			selectedURL = playURL
			selectedHeaders = headerPlay
		}
	}

	if strings.TrimSpace(selectedURL) == "" {
		return "", nil, errors.New("empty url")
	}
	if strings.TrimSpace(selectedHeaders["Cookie"]) == "" {
		selectedHeaders = map[string]string{}
	}
	setQuarkPlayCache(cacheKey, selectedURL, selectedHeaders)
	return selectedURL, selectedHeaders, nil
}

func QuarkPlay(database *db.DB, id string, want string) (string, map[string]string, error) {
	return quarkPlayImpl(database, id, want, "")
}

func QuarkPlayWithTVUser(database *db.DB, id string, want string, tvUser string) (string, map[string]string, error) {
	return quarkPlayImpl(database, id, want, tvUser)
}

func HandleAPIQuarkTVRefresh(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	store := readPanLoginSettings(database)
	tvKey := pickQuarkTVStoreKey(store)
	rt := strings.TrimSpace(getPanField(store, tvKey, "refresh_token"))
	dev := strings.TrimSpace(getPanField(store, tvKey, "device_id"))
	if rt == "" || dev == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": "missing quark_tv refresh_token/device_id"})
		return
	}
	at, newRT, expAt, err := quarkTVRefreshAccessToken(rt, dev)
	if err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": err.Error()})
		return
	}
	store2 := readPanLoginSettings(database)
	tvKey2 := pickQuarkTVStoreKey(store2)
	setPanField(store2, tvKey2, "access_token", strings.TrimSpace(at))
	if expAt > 0 {
		setPanField(store2, tvKey2, "access_token_exp_at", strconv.FormatInt(expAt, 10))
	} else {
		setPanField(store2, tvKey2, "access_token_exp_at", "")
	}
	if strings.TrimSpace(newRT) != "" && strings.TrimSpace(newRT) != rt {
		setPanField(store2, tvKey2, "refresh_token", strings.TrimSpace(newRT))
	}
	if err := writePanLoginSettings(database, store2); err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": "save pan_login_settings failed: " + err.Error()})
		return
	}
	writeJSON(w, 200, map[string]any{
		"ok":                  true,
		"device_id":           dev,
		"access_token_exp_at": strings.TrimSpace(getPanField(store2, tvKey2, "access_token_exp_at")),
	})
}

func HandleAPIQuarkList(w http.ResponseWriter, r *http.Request, database *db.DB) {
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
	val, fromCache, err := quarkListCache.Do(key, func() (quarkListAPIValue, error) {
		vod, shareID, err := QuarkList(database, flag, passcode)
		if err != nil {
			return quarkListAPIValue{}, err
		}
		return quarkListAPIValue{Vod: vod, ShareID: shareID}, nil
	})
	if err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": err.Error()})
		return
	}
	writeJSON(w, 200, map[string]any{"ok": true, "flag": flag, "shareId": val.ShareID, "vod_play_url": val.Vod, "cache": fromCache})
}

func HandleAPIQuarkStatus(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodGet {
		methodNotAllowed(w)
		return
	}
	store := readPanLoginSettings(database)
	hasCookie := strings.TrimSpace(getPanField(store, "quark", "cookie")) != ""
	tvKey := pickQuarkTVStoreKey(store)
	hasTV := strings.TrimSpace(getPanField(store, tvKey, "refresh_token")) != "" && strings.TrimSpace(getPanField(store, tvKey, "device_id")) != ""
	hasAT := strings.TrimSpace(getPanField(store, tvKey, "access_token")) != ""
	expAt := strings.TrimSpace(getPanField(store, tvKey, "access_token_exp_at"))
	writeJSON(w, 200, map[string]any{
		"ok":               true,
		"hasCookie":        hasCookie,
		"hasQuarkTV":       hasTV,
		"hasAccessToken":   hasAT,
		"accessTokenExpAt": expAt,
	})
}

func HandleAPIQuarkPlay(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	tvUser := getTVUserFromRequest(r)
	if tvUser == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": "missing X-TV-User"})
		return
	}
	var body struct {
		Flag string `json:"flag"`
		ID   string `json:"id"`
		Want string `json:"want"`
	}
	_ = readJSONLoose(r, &body)
	id := strings.TrimSpace(body.ID)
	if id == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": "missing id"})
		return
	}
	want := strings.TrimSpace(body.Want)

	cacheKey := buildPlayCacheKey("quark", tvUser, id, want)
	if u, header, ok := getPlayCache(cacheKey); ok {
		resp := map[string]any{"ok": true, "parse": 0, "url": u}
		if len(header) > 0 {
			resp["header"] = header
		}
		writeJSON(w, 200, resp)
		return
	}
	u, header, err := quarkPlayImpl(database, id, want, tvUser)
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
	writeJSON(w, 200, resp)
}

func buildQuarkHeaders(extra map[string]string) map[string]string {
	h := map[string]string{
		"User-Agent":      quarkQRUA,
		"Referer":         quarkReferer,
		"Origin":          "https://pan.quark.cn",
		"Accept":          "application/json, text/plain, */*",
		"Accept-Language": "zh-CN,zh;q=0.9,en;q=0.8",
		"Connection":      "keep-alive",
	}
	for k, v := range extra {
		h[k] = v
	}
	return h
}

func quarkExtractFirstStringByKey(root any, keyLower string) string {
	type item struct{ v any }
	q := []item{{v: root}}
	steps := 0
	for len(q) > 0 && steps < 5000 {
		steps++
		cur := q[0].v
		q = q[1:]
		if cur == nil {
			continue
		}
		m, ok := cur.(map[string]any)
		if ok {
			for k, v := range m {
				if strings.ToLower(strings.TrimSpace(k)) == keyLower {
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

func quarkExtractFirstStringByKeys(root any, keysLower []string) string {
	for _, k := range keysLower {
		v := quarkExtractFirstStringByKey(root, strings.ToLower(strings.TrimSpace(k)))
		if strings.TrimSpace(v) != "" {
			return strings.TrimSpace(v)
		}
	}
	return ""
}

func quarkExtractFirstNumberByKey(root any, keyLower string) (float64, bool) {
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

func quarkQRInitCookies(client *http.Client) {
	if client == nil {
		return
	}
	_, _, _ = quarkQRDoReq(client, "GET", "https://pan.quark.cn/", nil, buildQuarkHeaders(nil))
	loginURL := "https://uop.quark.cn/cas/custom/login?custom_login_type=mobile&client_id=" + url.QueryEscape(quarkQRClientID) + "&display=pc&v=1.2"
	_, _, _ = quarkQRDoReq(client, "GET", loginURL, nil, buildQuarkHeaders(map[string]string{
		"Referer": quarkSSOReferer,
		"Origin":  "https://uop.quark.cn",
	}))
}

func quarkQRGetToken(client *http.Client) (token string, qrURL string, err error) {
	u, _ := url.Parse("https://uop.quark.cn/cas/ajax/getTokenForQrcodeLogin")
	now := time.Now().UnixMilli()
	qs := u.Query()
	qs.Set("client_id", quarkQRClientID)
	qs.Set("v", "1.2")
	qs.Set("__dt", quarkMakeDT(now))
	qs.Set("__t", strconv.FormatInt(now, 10))
	qs.Set("request_id", uuidV4())
	u.RawQuery = qs.Encode()

	body, _, err := quarkQRDoReq(client, "GET", u.String(), nil, buildQuarkHeaders(map[string]string{
		"Referer": quarkSSOReferer,
		"Origin":  "https://uop.quark.cn",
	}))
	if err != nil {
		return "", "", err
	}
	var raw any
	_ = json.Unmarshal(body, &raw)
	token = quarkExtractFirstStringByKey(raw, "token")
	if token == "" {
		return "", "", errors.New("quark token missing")
	}
	qrURL = quarkExtractFirstStringByKeys(raw, []string{"qrcode_url", "qrcodeurl", "qr_url", "qrurl"})
	return token, qrURL, nil
}

func quarkQRBuildQRText(token string, qrURL string) string {
	raw := strings.TrimSpace(qrURL)
	if raw != "" && (strings.HasPrefix(raw, "http://") || strings.HasPrefix(raw, "https://")) {
		return raw
	}
	t := strings.TrimSpace(token)
	if t == "" {
		return ""
	}
	u, _ := url.Parse("https://su.quark.cn/4_eMHBJ")
	qs := u.Query()
	qs.Set("token", t)
	qs.Set("client_id", quarkQRClientID)
	qs.Set("ssb", "weblogin")
	qs.Set("uc_param_str", "")
	qs.Set("uc_biz_str", "S:custom|OPT:SAREA@0|OPT:IMMERSIVE@1|OPT:BACK_BTN_STYLE@0")
	u.RawQuery = qs.Encode()
	return u.String()
}

func quarkQREncodePNG(text string) ([]byte, error) {
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

func quarkQRPollServiceTicket(client *http.Client, token string) (status string, serviceTicket string, redirectURL string, err error) {
	t := strings.TrimSpace(token)
	if t == "" {
		return "", "", "", errors.New("missing token")
	}
	u, _ := url.Parse("https://uop.quark.cn/cas/ajax/getServiceTicketByQrcodeToken")
	now := time.Now().UnixMilli()
	qs := u.Query()
	qs.Set("__t", strconv.FormatInt(now, 10))
	qs.Set("__dt", quarkMakeDT(now))
	qs.Set("token", t)
	qs.Set("client_id", quarkQRClientID)
	qs.Set("v", "1.2")
	qs.Set("request_id", uuidV4())
	u.RawQuery = qs.Encode()

	body, _, err := quarkQRDoReq(client, "GET", u.String(), nil, buildQuarkHeaders(map[string]string{
		"Referer": quarkSSOReferer,
		"Origin":  "https://uop.quark.cn",
	}))
	if err != nil {
		return "error", "", "", err
	}

	var raw any
	_ = json.Unmarshal(body, &raw)
	msg := quarkExtractFirstStringByKey(raw, "message")
	if n, ok := quarkExtractFirstNumberByKey(raw, "status"); ok {
		if int64(n) == 2000000 {
			serviceTicket = quarkExtractFirstStringByKey(raw, "service_ticket")
			if serviceTicket == "" {
				return "error", "", "", errors.New("missing service_ticket")
			}
			redirectURL = quarkExtractFirstStringByKeys(raw, []string{"redirect_url", "redirecturl", "redirect_uri", "redirecturi"})
			return "confirmed", serviceTicket, redirectURL, nil
		}
	}

	serviceTicket = quarkExtractFirstStringByKey(raw, "service_ticket")
	if strings.TrimSpace(serviceTicket) != "" {
		redirectURL = quarkExtractFirstStringByKeys(raw, []string{"redirect_url", "redirecturl", "redirect_uri", "redirecturi"})
		return "confirmed", serviceTicket, redirectURL, nil
	}

	// Best-effort status mapping.
	if strings.Contains(msg, "扫码") || strings.Contains(msg, "scan") {
		return "scanned", "", "", nil
	}
	return "pending", "", "", nil
}

func quarkQRFinalizeCookies(client *http.Client, jar http.CookieJar, serviceTicket string, redirectURL string) (string, error) {
	if client == nil || jar == nil {
		return "", errors.New("missing client/jar")
	}
	st := strings.TrimSpace(serviceTicket)
	if st == "" {
		return "", errors.New("missing service_ticket")
	}

	tryValidate := func() error {
		validateURL := "https://drive.quark.cn/1/clouddrive/file/sort?pr=ucpro&fr=pc&pdir_fid=0&_fetch_total=1&_size=1&_sort=file_type:asc,file_name:asc"
		vBody, _, err := quarkQRDoReq(client, "GET", validateURL, nil, buildQuarkHeaders(map[string]string{
			"Referer": quarkReferer,
			"Origin":  "https://pan.quark.cn",
		}))
		if err == nil {
			var parsed map[string]any
			if err := json.Unmarshal(vBody, &parsed); err != nil {
				return errors.New("quark validate: invalid json")
			}
			code := int64(0)
			if n, ok := parsed["code"].(float64); ok {
				code = int64(n)
			}
			if code == 0 {
				return nil
			}
			msg := ""
			if s, ok := parsed["message"].(string); ok {
				msg = s
			}
			if msg == "" {
				msg = "validate failed"
			}
			return errors.New("quark validate: " + msg)
		}
		return err
	}

	candidates := make([]string, 0, 6)
	if ru := strings.TrimSpace(redirectURL); strings.HasPrefix(ru, "http://") || strings.HasPrefix(ru, "https://") {
		candidates = append(candidates, ru)
	}
	candidates = append(candidates,
		"https://drive.quark.cn/account/info?st="+url.QueryEscape(st)+"&fr=pc&platform=pc",
		"https://pan.quark.cn/account/info?st="+url.QueryEscape(st)+"&fr=pc&platform=pc",
		"https://drive-h.quark.cn/account/info?st="+url.QueryEscape(st)+"&fr=pc&platform=pc",
		"https://drive.quark.cn/?st="+url.QueryEscape(st),
		"https://pan.quark.cn/?st="+url.QueryEscape(st),
	)

	for _, u := range candidates {
		origin := ""
		referer := ""
		if pu, err := url.Parse(u); err == nil && pu != nil && pu.Host != "" {
			origin = pu.Scheme + "://" + pu.Host
			referer = origin + "/"
		}
		_, _, _ = quarkQRDoReq(client, "GET", u, nil, buildQuarkHeaders(map[string]string{
			"Referer": referer,
			"Origin":  origin,
			"Accept":  "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
		}))
		_, _, _ = quarkQRDoReq(client, "GET", "https://pan.quark.cn/", nil, buildQuarkHeaders(nil))
		_, _, _ = quarkQRDoReq(client, "GET", "https://drive.quark.cn/", nil, buildQuarkHeaders(map[string]string{
			"Referer": "https://drive.quark.cn/",
			"Origin":  "https://drive.quark.cn",
			"Accept":  "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
		}))
		if err := tryValidate(); err == nil {
			break
		}
	}

	if err := tryValidate(); err != nil {
		return "", err
	}

	panURL, _ := url.Parse("https://pan.quark.cn/")
	driveURL, _ := url.Parse("https://drive.quark.cn/")
	cookies := append([]*http.Cookie{}, jar.Cookies(panURL)...)
	cookies = append(cookies, jar.Cookies(driveURL)...)
	cookieStr := formatCookieHeader(cookies)
	if strings.TrimSpace(cookieStr) == "" {
		return "", errors.New("quark cookie empty")
	}
	return cookieStr, nil
}

func HandleDashboardQuarkStart(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	now := time.Now()
	cleanupQuarkQRSessions(now)

	client, jar, err := makeQuarkQRClient()
	if err != nil {
		writeJSON(w, 500, map[string]any{"success": false, "message": "初始化失败"})
		return
	}
	quarkQRInitCookies(client)
	token, qrURL, err := quarkQRGetToken(client)
	if err != nil {
		writeJSON(w, 500, map[string]any{"success": false, "message": err.Error()})
		return
	}
	qrText := quarkQRBuildQRText(token, qrURL)
	if qrText == "" {
		writeJSON(w, 500, map[string]any{"success": false, "message": "二维码生成失败"})
		return
	}
	img, err := quarkQREncodePNG(qrText)
	if err != nil {
		writeJSON(w, 500, map[string]any{"success": false, "message": "二维码编码失败"})
		return
	}

	qid := randHexN(12)
	s := &quarkQRSession{
		ID:        qid,
		CreatedAt: now,
		ExpiresAt: now.Add(3 * time.Minute),
		Token:     token,
		ClientID:  quarkQRClientID,
		Image:     img,
		ImageType: "image/png",
		Client:    client,
		Jar:       jar,
	}
	quarkQRSessions.Store(qid, s)

	writeJSON(w, 200, map[string]any{
		"success":   true,
		"qid":       qid,
		"expiresAt": s.ExpiresAt.UnixMilli(),
		"imageUrl":  "/dashboard/pan/quark/image?qid=" + url.QueryEscape(qid) + "&_t=" + url.QueryEscape(strconv.FormatInt(now.UnixMilli(), 10)),
	})
	_ = database
}

func HandleDashboardQuarkImage(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet {
		methodNotAllowed(w)
		return
	}
	qid := strings.TrimSpace(r.URL.Query().Get("qid"))
	if qid == "" {
		writeJSON(w, 400, map[string]any{"success": false, "message": "qid 不能为空"})
		return
	}
	v, ok := quarkQRSessions.Load(qid)
	if !ok {
		writeJSON(w, 404, map[string]any{"success": false, "message": "二维码已过期"})
		return
	}
	s, ok := v.(*quarkQRSession)
	if !ok || s == nil || time.Now().After(s.ExpiresAt) {
		quarkQRSessions.Delete(qid)
		writeJSON(w, 404, map[string]any{"success": false, "message": "二维码已过期"})
		return
	}
	w.Header().Set("Content-Type", s.ImageType)
	w.Header().Set("Cache-Control", "no-store")
	_, _ = w.Write(s.Image)
}

func HandleDashboardQuarkCookie(w http.ResponseWriter, r *http.Request, database *db.DB) {
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
	v, ok := quarkQRSessions.Load(qid)
	if !ok {
		writeJSON(w, 404, map[string]any{"success": false, "message": "二维码已过期"})
		return
	}
	s, ok := v.(*quarkQRSession)
	if !ok || s == nil || time.Now().After(s.ExpiresAt) {
		quarkQRSessions.Delete(qid)
		writeJSON(w, 404, map[string]any{"success": false, "message": "二维码已过期"})
		return
	}

	s.mu.Lock()
	defer s.mu.Unlock()
	if s.Cookie != "" {
		writeJSON(w, 200, map[string]any{"success": true, "status": "confirmed", "cookie": s.Cookie})
		return
	}

	status, st, redir, err := quarkQRPollServiceTicket(s.Client, s.Token)
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

	cookie, err := quarkQRFinalizeCookies(s.Client, s.Jar, st, redir)
	if err != nil {
		s.LastErr = err.Error()
		writeJSON(w, 500, map[string]any{"success": false, "message": err.Error(), "status": "error"})
		return
	}
	s.Cookie = cookie

	store := readPanLoginSettings(database)
	cur := store["quark"]
	if cur == nil {
		cur = map[string]any{}
	}
	cur["cookie"] = cookie
	store["quark"] = cur
	_ = writePanLoginSettings(database, store)

	writeJSON(w, 200, map[string]any{"success": true, "status": "confirmed", "cookie": cookie})
}
