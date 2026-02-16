package netdisk

import (
	"bytes"
	"crypto/rand"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"net/http/cookiejar"
	"net/url"
	"path"
	"regexp"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
)

type baiduQRSession struct {
	ID         string
	CreatedAt  time.Time
	ExpiresAt  time.Time
	GID        string
	Callback   string
	Sign       string
	Image      []byte
	ImageType  string
	Client     *http.Client
	Jar        http.CookieJar
	Cookie     string
	LastStatus string
	LastErr    string
	mu         sync.Mutex
}

var baiduQRSessions sync.Map // id -> *baiduQRSession

const (
	baiduQRUA      = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36"
	baiduQRBasePan = "https://pan.baidu.com/"
	baiduScriptWebUA = "Mozilla/5.0 (Linux; Android 12; V2238A) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/101.0.4951.40 Safari/537.36"
	baiduScriptNetdiskUA = "netdisk;12.11.9;V2238A;android-android;12;JSbridge4.4.0;jointBridge;1.1.0;"
	baiduPlayUA = "com.android.chrome/131.0.6778.200 (Linux;Android 10) AndroidXMedia3/1.5.1"
	baiduAppID  = "250528"
)

var (
	reFirstJSONObj = regexp.MustCompile(`\{[\s\S]*\}`)
)

func cleanupBaiduQRSessions(now time.Time) {
	baiduQRSessions.Range(func(key, value any) bool {
		s, ok := value.(*baiduQRSession)
		if !ok || s == nil {
			baiduQRSessions.Delete(key)
			return true
		}
		if now.After(s.ExpiresAt) {
			baiduQRSessions.Delete(key)
		}
		return true
	})
}

func randHex(n int) string {
	if n <= 0 {
		return ""
	}
	b := make([]byte, n)
	_, _ = rand.Read(b)
	return hex.EncodeToString(b)
}

func makeBaiduQRClient() (*http.Client, http.CookieJar, error) {
	jar, err := cookiejar.New(nil)
	if err != nil {
		return nil, nil, err
	}
	return &http.Client{
		Timeout: 12 * time.Second,
		Jar:     jar,
	}, jar, nil
}

func baiduQRGetQRCode(client *http.Client, gid string, cb string) (sign string, img []byte, imgType string, err error) {
	if client == nil {
		return "", nil, "", errors.New("missing http client")
	}
	now := time.Now().UnixMilli()

	_, _ = baiduQRDoReq(client, "GET", baiduQRBasePan, nil, map[string]string{
		"User-Agent": baiduQRUA,
		"Referer":    baiduQRBasePan,
	})

	qrURL := "https://passport.baidu.com/v2/api/getqrcode"
	q, _ := url.Parse(qrURL)
	qs := q.Query()
	qs.Set("lp", "pc")
	qs.Set("tt", strconvInt64(now))
	if strings.TrimSpace(gid) != "" {
		qs.Set("gid", gid)
	}
	if strings.TrimSpace(cb) != "" {
		qs.Set("callback", cb)
	}
	q.RawQuery = qs.Encode()

	body, _ := baiduQRDoReq(client, "GET", q.String(), nil, map[string]string{
		"User-Agent": baiduQRUA,
		"Referer":    baiduQRBasePan,
	})
	jsonText := extractJSONText(body)
	var resp struct {
		Errno  int    `json:"errno"`
		Sign   string `json:"sign"`
		ImgURL string `json:"imgurl"`
		Msg    string `json:"msg"`
	}
	if err := json.Unmarshal([]byte(jsonText), &resp); err != nil {
		return "", nil, "", err
	}
	if resp.Errno != 0 || strings.TrimSpace(resp.Sign) == "" || strings.TrimSpace(resp.ImgURL) == "" {
		if resp.Msg != "" {
			return "", nil, "", errors.New(resp.Msg)
		}
		return "", nil, "", errors.New("baidu getqrcode failed")
	}

	imgURL := strings.TrimSpace(resp.ImgURL)
	if strings.HasPrefix(imgURL, "//") {
		imgURL = "https:" + imgURL
	} else if strings.HasPrefix(imgURL, "http://") || strings.HasPrefix(imgURL, "https://") {
	} else {
		imgURL = "https://" + strings.TrimPrefix(imgURL, "/")
	}

	imgBuf, hdr, err := baiduQRDoReqWithHeaders(client, "GET", imgURL, nil, map[string]string{
		"User-Agent": baiduQRUA,
		"Referer":    baiduQRBasePan,
	})
	if err != nil {
		return "", nil, "", err
	}
	ct := strings.TrimSpace(hdr.Get("Content-Type"))
	if ct == "" {
		ct = http.DetectContentType(imgBuf)
	}
	return resp.Sign, imgBuf, ct, nil
}

func baiduQRPoll(client *http.Client, sign string, gid string, cb string) (status string, bduss string, err error) {
	if client == nil {
		return "", "", errors.New("missing http client")
	}
	if strings.TrimSpace(sign) == "" {
		return "", "", errors.New("missing sign")
	}

	u, _ := url.Parse("https://passport.baidu.com/channel/unicast")
	qs := u.Query()
	qs.Set("channel_id", sign)
	qs.Set("tpl", "netdisk")
	qs.Set("apiver", "v3")
	qs.Set("tt", strconvInt64(time.Now().UnixMilli()))
	if strings.TrimSpace(gid) != "" {
		qs.Set("gid", gid)
	}
		if strings.TrimSpace(cb) != "" {
			qs.Set("callback", cb)
		} else {
			qs.Set("callback", "bd__cbs__"+randHex(6))
		}
	u.RawQuery = qs.Encode()

	body, _ := baiduQRDoReq(client, "GET", u.String(), nil, map[string]string{
		"User-Agent": baiduQRUA,
		"Referer":    baiduQRBasePan,
	})
	jsonText := extractJSONText(body)
	var resp struct {
		Errno    int    `json:"errno"`
		ChannelV string `json:"channel_v"`
		Msg      string `json:"msg"`
	}
	if err := json.Unmarshal([]byte(jsonText), &resp); err != nil {
		return "", "", err
	}
	if resp.Errno != 0 {
		if resp.Errno == 1 {
			return "pending", "", nil
		}
		if resp.Msg != "" {
			return "error", "", errors.New(resp.Msg)
		}
		return "pending", "", nil
	}
	if strings.TrimSpace(resp.ChannelV) == "" {
		return "pending", "", nil
	}
	var cv struct {
		Status int    `json:"status"`
		V      string `json:"v"`
	}
		if err := json.Unmarshal([]byte(resp.ChannelV), &cv); err != nil {
			var tmp string
			if err2 := json.Unmarshal([]byte(strconvQuoteIfNeeded(resp.ChannelV)), &tmp); err2 == nil {
				_ = json.Unmarshal([]byte(tmp), &cv)
			}
		}
	switch cv.Status {
	case 0:
		if strings.TrimSpace(cv.V) == "" {
			return "error", "", errors.New("missing bduss")
		}
		return "confirmed", cv.V, nil
	case 1:
		return "scanned", "", nil
	default:
		return "pending", "", nil
	}
}

func baiduQRFinalize(client *http.Client, bduss string) (string, error) {
	if client == nil {
		return "", errors.New("missing http client")
	}
	b := strings.TrimSpace(bduss)
	if b == "" {
		return "", errors.New("missing bduss")
	}

	u, _ := url.Parse("https://passport.baidu.com/v3/login/main/qrbdusslogin")
	qs := u.Query()
	qs.Set("bduss", b)
	qs.Set("u", baiduQRBasePan)
	qs.Set("tpl", "netdisk")
	qs.Set("apiver", "v3")
	qs.Set("tt", strconvInt64(time.Now().UnixMilli()))
	u.RawQuery = qs.Encode()

	_, _, _ = baiduQRDoReqWithHeaders(client, "GET", u.String(), nil, map[string]string{
		"User-Agent": baiduQRUA,
		"Referer":    baiduQRBasePan,
	})

		_, _ = baiduQRDoReq(client, "GET", baiduQRBasePan, nil, map[string]string{
			"User-Agent": baiduQRUA,
			"Referer":    baiduQRBasePan,
		})

		panURL, _ := url.Parse(baiduQRBasePan)
		cookies := client.Jar.Cookies(panURL)
		cookieStr := formatCookieHeader(cookies)
		if !strings.Contains(cookieStr, "BDUSS=") {
			passURL, _ := url.Parse("https://passport.baidu.com/")
			more := client.Jar.Cookies(passURL)
			cookieStr = formatCookieHeader(append(cookies, more...))
		}
	if !strings.Contains(cookieStr, "BDUSS=") {
		return "", errors.New("cookie missing BDUSS")
	}
	return cookieStr, nil
}

var reBaiduSurl = regexp.MustCompile(`百度[^-]*-([^#]+)`)
var reBaiduSurlKey = regexp.MustCompile(`^1[0-9a-zA-Z_-]+$`)

func parseBaiduSurlFromFlag(flag string) string {
	raw := strings.TrimSpace(flag)
	if raw == "" {
		return ""
	}
	if m := reBaiduSurl.FindStringSubmatch(raw); len(m) == 2 {
		out := strings.TrimSpace(m[1])
		if reBaiduSurlKey.MatchString(out) && len(out) > 1 {
			return out[1:]
		}
		return out
	}
	parts := strings.SplitN(raw, "-", 2)
	if len(parts) == 2 {
		out := strings.TrimSpace(strings.SplitN(parts[1], "#", 2)[0])
		if reBaiduSurlKey.MatchString(out) && len(out) > 1 {
			return out[1:]
		}
		return out
	}
	return ""
}

func parseCookieToMap(cookie string) map[string]string {
	out := map[string]string{}
	for _, part := range strings.Split(cookie, ";") {
		p := strings.TrimSpace(part)
		if p == "" || !strings.Contains(p, "=") {
			continue
		}
		kv := strings.SplitN(p, "=", 2)
		k := strings.TrimSpace(kv[0])
		v := ""
		if len(kv) == 2 {
			v = strings.TrimSpace(kv[1])
		}
		if k != "" {
			out[k] = v
		}
	}
	return out
}

func mergeCookieFromSetCookie(baseCookie string, setCookie []string) string {
	m := parseCookieToMap(baseCookie)
	for _, sc := range setCookie {
		s := strings.TrimSpace(sc)
		if s == "" {
			continue
		}
		first := strings.SplitN(s, ";", 2)[0]
		if !strings.Contains(first, "=") {
			continue
		}
		kv := strings.SplitN(first, "=", 2)
		k := strings.TrimSpace(kv[0])
		v := ""
		if len(kv) == 2 {
			v = strings.TrimSpace(kv[1])
		}
		if k != "" {
			m[k] = v
		}
	}
	keys := make([]string, 0, len(m))
	for k := range m {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	var sb strings.Builder
	for i, k := range keys {
		if i > 0 {
			sb.WriteString("; ")
		}
		sb.WriteString(k)
		sb.WriteString("=")
		sb.WriteString(m[k])
	}
	return sb.String()
}

func baiduFetchJSON(method string, urlStr string, cookie string, body []byte) (any, []string, error) {
	client := &http.Client{Timeout: 18 * time.Second}
	req, err := http.NewRequest(method, urlStr, bytes.NewReader(body))
	if err != nil {
		return nil, nil, err
	}
	req.Header.Set("User-Agent", baiduQRUA)
	req.Header.Set("Referer", baiduQRBasePan)
	req.Header.Set("Origin", "https://pan.baidu.com")
	req.Header.Set("Accept", "application/json, text/plain, */*")
	if strings.TrimSpace(cookie) != "" {
		req.Header.Set("Cookie", strings.TrimSpace(cookie))
	}
	if method == http.MethodPost {
		req.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	}
	resp, err := client.Do(req)
	if err != nil {
		return nil, nil, err
	}
	defer resp.Body.Close()
	b, _ := io.ReadAll(resp.Body)
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return nil, resp.Header.Values("Set-Cookie"), errors.New("baidu http " + strconv.Itoa(resp.StatusCode) + ": " + strings.TrimSpace(string(b)))
	}
	var obj any
	if err := json.Unmarshal(bytes.TrimSpace(b), &obj); err != nil {
		return nil, resp.Header.Values("Set-Cookie"), err
	}
	return obj, resp.Header.Values("Set-Cookie"), nil
}

func baiduErrnoOk(obj any) error {
	return baiduErrnoOkAllow(obj, nil)
}

func baiduErrnoOkAllow(obj any, allow map[string]struct{}) error {
	m, _ := obj.(map[string]any)
	if m == nil {
		return nil
	}
	if errno, ok := m["errno"]; ok && toString(errno) != "" && toString(errno) != "0" {
		if allow != nil {
			if _, ok := allow[toString(errno)]; ok {
				return nil
			}
		}
		return errors.New("baidu errno " + toString(errno))
	}
	return nil
}

func baiduVerifySharePwd(surl string, pwd string, cookie *string) error {
	if strings.TrimSpace(pwd) == "" {
		return nil
	}
	u, _ := url.Parse("https://pan.baidu.com/share/verify")
	q := u.Query()
	q.Set("t", strconvInt64(time.Now().UnixMilli()))
	q.Set("surl", strings.TrimSpace(surl))
	u.RawQuery = q.Encode()
	form := url.Values{}
	form.Set("pwd", strings.TrimSpace(pwd))
	obj, setCookie, err := baiduFetchJSON(http.MethodPost, u.String(), *cookie, []byte(form.Encode()))
	if err != nil {
		return err
	}
	if setCookie != nil && len(setCookie) > 0 {
		*cookie = mergeCookieFromSetCookie(*cookie, setCookie)
	}
	_ = obj
	return nil
}

func baiduShareListRoot(surl string, pwd string, baseCookie string) (cookie string, data map[string]any, shareID string, uk string, err error) {
	shorturl := strings.TrimSpace(surl)
	if shorturl == "" {
		return "", nil, "", "", errors.New("missing surl")
	}
	cookie = strings.TrimSpace(baseCookie)
	if cookie == "" {
		return "", nil, "", "", errors.New("missing baidu cookie")
	}
	if strings.TrimSpace(pwd) != "" {
		if err := baiduVerifySharePwd(shorturl, pwd, &cookie); err != nil {
			return cookie, nil, "", "", err
		}
	}
	u, _ := url.Parse("https://pan.baidu.com/share/list")
	q := u.Query()
	q.Set("desc", "1")
	q.Set("showempty", "0")
	q.Set("page", "1")
	q.Set("num", "10000")
	q.Set("order", "time")
	q.Set("shorturl", shorturl)
	q.Set("root", "1")
	u.RawQuery = q.Encode()

	objAny, setCookie, err := baiduFetchJSON(http.MethodGet, u.String(), cookie, nil)
	if err != nil {
		return cookie, nil, "", "", err
	}
	if setCookie != nil && len(setCookie) > 0 {
		cookie = mergeCookieFromSetCookie(cookie, setCookie)
	}
	if err := baiduErrnoOk(objAny); err != nil {
		return cookie, nil, "", "", err
	}
	root, _ := objAny.(map[string]any)
	if root == nil {
		root = map[string]any{}
	}
	shareID = strings.TrimSpace(toString(root["shareid"]))
	if shareID == "" {
		shareID = strings.TrimSpace(toString(root["share_id"]))
	}
	uk = strings.TrimSpace(toString(root["uk"]))
	if uk == "" {
		uk = strings.TrimSpace(toString(root["share_uk"]))
	}
	return cookie, root, shareID, uk, nil
}

func baiduShareListDir(shorturl string, dirPath string, cookie string) (data map[string]any, err error) {
	u, _ := url.Parse("https://pan.baidu.com/share/list")
	q := u.Query()
	q.Set("desc", "1")
	q.Set("showempty", "0")
	q.Set("page", "1")
	q.Set("num", "10000")
	q.Set("order", "other")
	q.Set("shorturl", strings.TrimSpace(shorturl))
	q.Set("root", "0")
	q.Set("dir", strings.TrimSpace(dirPath))
	q.Set("t", strconvInt64(time.Now().UnixNano()/1e3))
	u.RawQuery = q.Encode()

	objAny, setCookie, err := baiduFetchJSON(http.MethodGet, u.String(), cookie, nil)
	if err != nil {
		return nil, err
	}
	_ = setCookie // cookie can be merged by caller if needed
	if err := baiduErrnoOk(objAny); err != nil {
		return nil, err
	}
	root, _ := objAny.(map[string]any)
	if root == nil {
		root = map[string]any{}
	}
	return root, nil
}

func baiduGetShareListArray(data map[string]any) []map[string]any {
	if data == nil {
		return nil
	}
	if arrAny, ok := data["list"]; ok {
		if arr, ok := arrAny.([]any); ok {
			out := []map[string]any{}
			for _, it := range arr {
				m, _ := it.(map[string]any)
				if m != nil {
					out = append(out, m)
				}
			}
			return out
		}
	}
	if innerAny, ok := data["data"]; ok {
		inner, _ := innerAny.(map[string]any)
		if inner != nil {
			return baiduGetShareListArray(inner)
		}
	}
	return nil
}

func baiduIsDirItem(it map[string]any) bool {
	if it == nil {
		return false
	}
	if toString(it["isdir"]) == "1" {
		return true
	}
	if v := strings.TrimSpace(toString(it["isdir"])); v == "true" {
		return true
	}
	return false
}

func baiduItemPath(it map[string]any) string {
	if it == nil {
		return ""
	}
	if v := strings.TrimSpace(toString(it["path"])); v != "" {
		return v
	}
	return strings.TrimSpace(toString(it["server_path"]))
}

func baiduShareListAllFiles(surl string, pwd string, baseCookie string) (cookie string, files []map[string]any, shareID string, uk string, err error) {
	cookie, rootData, shareID, uk, err := baiduShareListRoot(surl, pwd, baseCookie)
	if err != nil {
		return cookie, nil, "", "", err
	}
	initial := baiduGetShareListArray(rootData)

	const maxDirs = 2000
	const maxFiles = 50000
	const maxDepth = 50

	type qItem struct {
		dir   string
		depth int
	}

	sanitizeToken := func(s string) string {
		t := strings.NewReplacer("#", " ", "$", " ").Replace(strings.TrimSpace(s))
		return strings.Join(strings.Fields(t), " ")
	}
	normalizeDirDisplay := func(relDir string) string {
		d := strings.TrimSpace(relDir)
		if d == "" || d == "/" {
			return "/"
		}
		d = strings.TrimLeft(d, "/")
		return sanitizeToken(d)
	}
	stripDirPrefix := func(dirPath string, prefix string) string {
		d := strings.TrimSpace(dirPath)
		if d == "" || !strings.HasPrefix(d, "/") {
			return "/"
		}
		pre := strings.TrimSpace(prefix)
		if pre == "" || pre == "/" {
			return d
		}
		if d == pre {
			return "/"
		}
		pre2 := pre
		if !strings.HasSuffix(pre2, "/") {
			pre2 += "/"
		}
		if strings.HasPrefix(d, pre2) {
			out := "/" + strings.TrimLeft(d[len(pre2):], "/")
			if out == "/" {
				return "/"
			}
			return out
		}
		return d
	}

	getFsid := func(it map[string]any) string {
		fsid := strings.TrimSpace(toString(it["fs_id"]))
		if fsid == "" {
			fsid = strings.TrimSpace(toString(it["fsid"]))
		}
		return fsid
	}
	getName := func(it map[string]any) string {
		name := strings.TrimSpace(toString(it["server_filename"]))
		if name == "" {
			name = strings.TrimSpace(toString(it["name"]))
		}
		return name
	}

	joinDir := func(parent string, child string) string {
		base := strings.TrimSpace(parent)
		name := strings.Trim(child, "/")
		if name == "" {
			if base == "" {
				return ""
			}
			if strings.HasPrefix(base, "/") {
				return base
			}
			return "/" + base
		}
		if base == "" || base == "/" {
			return "/" + name
		}
		if !strings.HasPrefix(base, "/") {
			base = "/" + base
		}
		return strings.TrimRight(base, "/") + "/" + name
	}

	type baiduShareFile struct {
		Fsid    string
		RealName string
		DirDisplay string
	}

	out := make([]baiduShareFile, 0, 256)
	queue := make([]qItem, 0, 64)
	seenDir := map[string]struct{}{}
	effectiveRootPrefix := ""

	pushDir := func(p string, depth int) error {
		pp := strings.TrimSpace(p)
		if pp == "" || !strings.HasPrefix(pp, "/") {
			return nil
		}
		if depth > maxDepth {
			return nil
		}
		if _, ok := seenDir[pp]; ok {
			return nil
		}
		seenDir[pp] = struct{}{}
		if len(seenDir) > maxDirs {
			return errors.New("baidu share too large (exceeded max dirs)")
		}
		queue = append(queue, qItem{dir: pp, depth: depth})
		return nil
	}

	pushFile := func(it map[string]any, currentDir string) error {
		fsid := getFsid(it)
		name := getName(it)
		if fsid == "" || name == "" {
			return nil
		}
		dir := strings.TrimSpace(currentDir)
		if p := strings.TrimSpace(baiduItemPath(it)); strings.HasPrefix(p, "/") {
			dir = path.Dir(p)
		}
		if dir == "" || dir == "." {
			dir = "/"
		}
		if !strings.HasPrefix(dir, "/") {
			dir = "/" + dir
		}
		relDir := stripDirPrefix(dir, effectiveRootPrefix)
		dirDisplay := normalizeDirDisplay(relDir)
		out = append(out, baiduShareFile{Fsid: fsid, RealName: name, DirDisplay: dirDisplay})
		if len(out) > maxFiles {
			return errors.New("baidu share too large (exceeded max files)")
		}
		return nil
	}

	handleList := func(list []map[string]any, currentDir string, depth int) error {
		for _, it := range list {
			if it == nil {
				continue
			}
			if baiduIsDirItem(it) {
				dirPath := strings.TrimSpace(baiduItemPath(it))
				if dirPath == "" {
					dirPath = joinDir(currentDir, getName(it))
				}
				if err := pushDir(dirPath, depth+1); err != nil {
					return err
				}
				continue
			}
			if err := pushFile(it, currentDir); err != nil {
				return err
			}
		}
		return nil
	}

	rootDirs := make([]map[string]any, 0, 8)
	rootFiles := 0
	for _, it := range initial {
		if it == nil {
			continue
		}
		if baiduIsDirItem(it) {
			rootDirs = append(rootDirs, it)
		} else if getFsid(it) != "" {
			rootFiles++
		}
	}
	if rootFiles == 0 && len(rootDirs) == 1 {
		p := strings.TrimSpace(baiduItemPath(rootDirs[0]))
		if p == "" {
			n := strings.TrimSpace(getName(rootDirs[0]))
			if n != "" {
				p = "/" + strings.Trim(n, "/")
			}
		}
		if strings.HasPrefix(p, "/") {
			effectiveRootPrefix = p
			if err := pushDir(p, 0); err != nil {
				return cookie, nil, shareID, uk, err
			}
		} else {
			if err := handleList(initial, "/", 0); err != nil {
				return cookie, nil, shareID, uk, err
			}
		}
	} else {
		if err := handleList(initial, "/", 0); err != nil {
			return cookie, nil, shareID, uk, err
		}
	}

	for len(queue) > 0 {
		cur := queue[0]
		queue = queue[1:]
		data, err := baiduShareListDir(surl, cur.dir, cookie)
		if err != nil {
			return cookie, nil, shareID, uk, err
			}
			list := baiduGetShareListArray(data)
			if err := handleList(list, cur.dir, cur.depth); err != nil {
				return cookie, nil, shareID, uk, err
			}
		}

	parts := make([]map[string]any, 0, len(out))
	for _, f := range out {
		parts = append(parts, map[string]any{
			"fs_id":          f.Fsid,
			"server_filename": f.RealName,
			"__dir_display":   f.DirDisplay,
			"__suffix_name":   sanitizeToken(f.RealName),
		})
	}
	return cookie, parts, shareID, uk, nil
}

func BaiduList(database *db.DB, flag string, pwd string) (string, string, error) {
	surl := parseBaiduSurlFromFlag(flag)
	if surl == "" {
		return "", "", errors.New("missing/invalid flag (expected: 百度*-<surl>)")
	}
	store := readPanLoginSettings(database)
	baseCookie := getPanField(store, "baidu", "cookie")
	if baseCookie == "" {
		return "", surl, errors.New("missing baidu cookie (pan_login_settings[\"baidu\"].cookie)")
	}
	_, files, shareID, uk, err := baiduShareListAllFiles(surl, pwd, baseCookie)
	if err != nil {
		return "", surl, err
	}
	parts := []string{}
	for _, it := range files {
		if it == nil {
			continue
		}
		dirDisplay := strings.TrimSpace(toString(it["__dir_display"]))
		name := strings.TrimSpace(toString(it["server_filename"]))
		fsid := strings.TrimSpace(toString(it["fs_id"]))
		suffixName := strings.TrimSpace(toString(it["__suffix_name"]))
		if dirDisplay == "" {
			dirDisplay = "/"
		}
		if suffixName == "" {
			suffixName = name
		}
		if dirDisplay == "" || name == "" || fsid == "" {
			continue
		}
		playJSON := map[string]any{
			"shareid":  shareID,
			"uk":       uk,
			"fs_id":    fsid,
			"surl":     surl,
			"pwd":      strings.TrimSpace(pwd),
			"realName": name,
		}
		b, _ := json.Marshal(playJSON)
		id := base64.StdEncoding.EncodeToString(b) + "|||" + suffixName
		parts = append(parts, dirDisplay+"$"+id)
	}
	return strings.Join(parts, "#"), surl, nil
}

func HandleAPIBaiduList(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	var body struct {
		Flag     string `json:"flag"`
		Pwd      string `json:"pwd"`
		Pass     string `json:"pass"`
		Password string `json:"password"`
	}
	_ = readJSONLoose(r, &body)
	flag := strings.TrimSpace(body.Flag)
	pwd := strings.TrimSpace(body.Pwd)
	if pwd == "" {
		pwd = strings.TrimSpace(body.Pass)
	}
	if pwd == "" {
		pwd = strings.TrimSpace(body.Password)
	}
	vod, _, err := BaiduList(database, flag, pwd)
	if err != nil {
		code := http.StatusBadRequest
		msg := err.Error()
		if strings.HasPrefix(msg, "baidu ") || strings.Contains(msg, " errno=") {
			code = http.StatusBadGateway
		}
		writeJSON(w, code, map[string]any{"ok": false, "message": msg})
		return
	}
	writeJSON(w, 200, map[string]any{"ok": true, "vod_play_url": vod})
}

func HandleAPIBaiduPlay(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	var body struct {
		Flag     string `json:"flag"`
		ID       string `json:"id"`
		DestName string `json:"destName"`
		DestPath string `json:"destPath"`
	}
	_ = readJSONLoose(r, &body)
	id := strings.TrimSpace(body.ID)
	flag := strings.TrimSpace(body.Flag)
	if id == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": "missing id"})
		return
	}
	dirPath := strings.TrimSpace(body.DestPath)
	if dirPath == "" && strings.TrimSpace(body.DestName) != "" {
		dirPath = "/" + strings.Trim(strings.TrimSpace(body.DestName), "/")
	}
	if dirPath == "" {
		dirPath = "/MeowFilm"
	}
	if !strings.HasPrefix(dirPath, "/") {
		dirPath = "/" + dirPath
	}
	u, headers, err := BaiduPlay(database, flag, id, dirPath)
	if err != nil {
		code := http.StatusBadRequest
		msg := err.Error()
		if strings.HasPrefix(msg, "baidu ") || strings.Contains(msg, " errno=") {
			code = http.StatusBadGateway
		}
		writeJSON(w, code, map[string]any{"ok": false, "message": msg})
		return
	}
	writeJSON(w, 200, map[string]any{
		"ok":     true,
		"url":    u,
		"header": headers,
	})
}

func formatCookieHeader(cookies []*http.Cookie) string {
	if len(cookies) == 0 {
		return ""
	}
	byName := map[string]string{}
	for _, c := range cookies {
		if c == nil {
			continue
		}
		name := strings.TrimSpace(c.Name)
		val := strings.TrimSpace(c.Value)
		if name == "" || val == "" {
			continue
		}
		byName[name] = val
	}
	if len(byName) == 0 {
		return ""
	}
	priority := []string{"BDUSS", "STOKEN", "PTOKEN", "BAIDUID", "BAIDUID_BFESS"}
	ordered := make([]string, 0, len(byName))
	seen := map[string]struct{}{}
	for _, n := range priority {
		if v, ok := byName[n]; ok {
			ordered = append(ordered, n+"="+v)
			seen[n] = struct{}{}
		}
	}
	rest := make([]string, 0, len(byName))
	for n, v := range byName {
		if _, ok := seen[n]; ok {
			continue
		}
		rest = append(rest, n+"="+v)
	}
	sort.Strings(rest)
	ordered = append(ordered, rest...)
	return strings.Join(ordered, "; ")
}

func extractJSONText(body []byte) string {
	s := strings.TrimSpace(string(body))
	if s == "" {
		return "{}"
	}
	if strings.Contains(s, "{") && strings.Contains(s, "}") {
		m := reFirstJSONObj.FindString(s)
		if m != "" {
			return m
		}
	}
	return s
}

func strconvInt64(v int64) string {
	return strconv.FormatInt(v, 10)
}

func strconvQuoteIfNeeded(s string) string {
	ss := strings.TrimSpace(s)
	if ss == "" {
		return `""`
	}
	if strings.HasPrefix(ss, "\"") && strings.HasSuffix(ss, "\"") {
		return ss
	}
	b, _ := json.Marshal(ss)
	return string(b)
}

func baiduQRDoReq(client *http.Client, method string, urlStr string, body []byte, headers map[string]string) ([]byte, error) {
	data, _, err := baiduQRDoReqWithHeaders(client, method, urlStr, body, headers)
	return data, err
}

func baiduQRDoReqWithHeaders(client *http.Client, method string, urlStr string, body []byte, headers map[string]string) ([]byte, http.Header, error) {
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
		return nil, resp.Header, errors.New("baidu http " + strconv.Itoa(resp.StatusCode) + ": " + msg)
	}
	return buf, resp.Header, nil
}

func baiduFetchJSONWithHeaders(method string, urlStr string, cookie string, body []byte, headers map[string]string) (any, []string, error) {
	client := &http.Client{Timeout: 25 * time.Second}
	req, err := http.NewRequest(method, urlStr, bytes.NewReader(body))
	if err != nil {
		return nil, nil, err
	}
	req.Header.Set("Accept", "application/json, text/plain, */*")
	req.Header.Set("Origin", "https://pan.baidu.com")
	req.Header.Set("Referer", baiduQRBasePan)
	if headers != nil {
		for k, v := range headers {
			if strings.TrimSpace(k) == "" {
				continue
			}
			req.Header.Set(k, v)
		}
	}
	if strings.TrimSpace(req.Header.Get("User-Agent")) == "" {
		req.Header.Set("User-Agent", baiduScriptWebUA)
	}
	if strings.TrimSpace(cookie) != "" {
		req.Header.Set("Cookie", strings.TrimSpace(cookie))
	}
	if method == http.MethodPost && strings.TrimSpace(req.Header.Get("Content-Type")) == "" {
		req.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	}
	resp, err := client.Do(req)
	if err != nil {
		return nil, nil, err
	}
	defer resp.Body.Close()
	b, _ := io.ReadAll(resp.Body)
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return nil, resp.Header.Values("Set-Cookie"), errors.New("baidu http " + strconv.Itoa(resp.StatusCode) + ": " + strings.TrimSpace(string(b)))
	}
	var obj any
	if err := json.Unmarshal(bytes.TrimSpace(b), &obj); err != nil {
		return nil, resp.Header.Values("Set-Cookie"), err
	}
	return obj, resp.Header.Values("Set-Cookie"), nil
}

func baiduDecodePlayIDToJSON(id string) (map[string]any, string) {
	raw := strings.TrimSpace(id)
	if raw == "" {
		return nil, ""
	}
	if idx := strings.LastIndex(raw, "$"); idx >= 0 {
		raw = raw[idx+1:]
	}
	if idx := strings.Index(raw, "|||"); idx >= 0 {
		raw = raw[:idx]
	}
	raw = strings.TrimSpace(raw)
	if raw == "" {
		return nil, ""
	}
	if u, err := url.QueryUnescape(raw); err == nil {
		raw = u
	}
	b, err := base64.StdEncoding.DecodeString(raw)
	if err != nil {
		alt := strings.NewReplacer("-", "+", "_", "/").Replace(raw)
		if m := len(alt) % 4; m != 0 {
			alt += strings.Repeat("=", 4-m)
		}
		b2, err2 := base64.StdEncoding.DecodeString(alt)
		if err2 != nil {
			return nil, ""
		}
		b = b2
	}
	var obj map[string]any
	if err := json.Unmarshal(b, &obj); err != nil {
		return nil, ""
	}
	return obj, raw
}

func baiduExtractNameFromID(id string) string {
	s := strings.TrimSpace(id)
	if s == "" {
		return ""
	}
	if idx := strings.Index(s, "|||"); idx >= 0 {
		return strings.TrimSpace(s[idx+3:])
	}
	return ""
}

func baiduGetBdstoken(cookie string) (string, string, error) {
	u, _ := url.Parse("https://pan.baidu.com/api/loginStatus")
	q := u.Query()
	q.Set("clienttype", "1")
	q.Set("web", "1")
	q.Set("channel", "web")
	q.Set("version", "0")
	u.RawQuery = q.Encode()
	objAny, setCookie, err := baiduFetchJSONWithHeaders(http.MethodGet, u.String(), cookie, nil, map[string]string{"User-Agent": baiduScriptWebUA})
	if err != nil {
		return "", cookie, err
	}
	if setCookie != nil && len(setCookie) > 0 {
		cookie = mergeCookieFromSetCookie(cookie, setCookie)
	}
	root, _ := objAny.(map[string]any)
	if root == nil {
		return "", cookie, errors.New("baidu loginStatus invalid response")
	}
	li, _ := root["login_info"].(map[string]any)
	if li == nil {
		return "", cookie, errors.New("baidu loginStatus missing login_info")
	}
	token := strings.TrimSpace(toString(li["bdstoken"]))
	if token == "" {
		return "", cookie, errors.New("baidu bdstoken not found")
	}
	return token, cookie, nil
}

func baiduToCreateAPIPath(dirPath string) string {
	p := strings.TrimSpace(dirPath)
	if p == "" {
		return ""
	}
	if strings.HasPrefix(p, "/") {
		return "/" + p // send `//Name` for root dirs
	}
	return "//" + p
}

func baiduAPIListRoot(cookie string) (map[string]any, string, error) {
	u, _ := url.Parse("https://pan.baidu.com/api/list")
	q := u.Query()
	q.Set("clienttype", "0")
	q.Set("app_id", baiduAppID)
	q.Set("web", "1")
	q.Set("order", "time")
	q.Set("desc", "1")
	q.Set("num", "9999")
	q.Set("page", "1")
	u.RawQuery = q.Encode()
	objAny, setCookie, err := baiduFetchJSONWithHeaders(http.MethodGet, u.String(), cookie, nil, map[string]string{"User-Agent": baiduScriptWebUA})
	if err != nil {
		return nil, cookie, err
	}
	if setCookie != nil && len(setCookie) > 0 {
		cookie = mergeCookieFromSetCookie(cookie, setCookie)
	}
	root, _ := objAny.(map[string]any)
	if root == nil {
		root = map[string]any{}
	}
	return root, cookie, nil
}

func baiduPickDirEntryFromAPIList(data map[string]any, dirPath string) map[string]any {
	wantPath := strings.TrimSpace(dirPath)
	if wantPath == "" || !strings.HasPrefix(wantPath, "/") || data == nil {
		return nil
	}
	wantName := ""
	parts := strings.Split(wantPath, "/")
	for i := len(parts) - 1; i >= 0; i-- {
		if strings.TrimSpace(parts[i]) != "" {
			wantName = strings.TrimSpace(parts[i])
			break
		}
	}
	arrAny, _ := data["list"].([]any)
	for _, it := range arrAny {
		m, _ := it.(map[string]any)
		if m == nil {
			continue
		}
		if toString(m["isdir"]) != "1" {
			continue
		}
		p := strings.TrimSpace(toString(m["path"]))
		if p == wantPath {
			return m
		}
		if wantName != "" {
			n := strings.TrimSpace(toString(m["server_filename"]))
			if n == "" {
				n = strings.TrimSpace(toString(m["name"]))
			}
			if n == wantName {
				return m
			}
		}
	}
	return nil
}

func baiduCreateDir(cookie string, dirPath string, bdstoken string) (map[string]any, string, error) {
	if strings.TrimSpace(bdstoken) == "" {
		var err error
		bdstoken, cookie, err = baiduGetBdstoken(cookie)
		if err != nil {
			return nil, cookie, err
		}
	}
	u, _ := url.Parse("https://pan.baidu.com/api/create")
	q := u.Query()
	q.Set("a", "commit")
	q.Set("bdstoken", strings.TrimSpace(bdstoken))
	q.Set("clienttype", "0")
	q.Set("web", "1")
	u.RawQuery = q.Encode()
	form := url.Values{}
	form.Set("path", baiduToCreateAPIPath(dirPath))
	form.Set("isdir", "1")
	form.Set("block_list", "[]")
	objAny, setCookie, err := baiduFetchJSONWithHeaders(http.MethodPost, u.String(), cookie, []byte(form.Encode()), map[string]string{
		"User-Agent":    baiduScriptWebUA,
		"Content-Type":  "application/x-www-form-urlencoded",
	})
	if err != nil {
		return nil, cookie, err
	}
	if setCookie != nil && len(setCookie) > 0 {
		cookie = mergeCookieFromSetCookie(cookie, setCookie)
	}
	allow := map[string]struct{}{"31066": {}, "-8": {}}
	if err := baiduErrnoOkAllow(objAny, allow); err != nil {
		return nil, cookie, err
	}
	root, _ := objAny.(map[string]any)
	if root == nil {
		root = map[string]any{}
	}
	return root, cookie, nil
}

func baiduEnsureDir(cookie string, dirPath string) (string, string, error) {
	token, cookie2, err := baiduGetBdstoken(cookie)
	if err != nil {
		return "", cookie, err
	}
	if list, cookie3, err2 := baiduAPIListRoot(cookie2); err2 == nil {
		if found := baiduPickDirEntryFromAPIList(list, dirPath); found != nil {
			p := strings.TrimSpace(toString(found["path"]))
			if p == "" {
				p = dirPath
			}
			return p, cookie3, nil
		}
		cookie2 = cookie3
	}
	created, cookie4, err := baiduCreateDir(cookie2, dirPath, token)
	if err != nil {
		return "", cookie4, err
	}
	_ = created
	return dirPath, cookie4, nil
}

func baiduShareTransferToDir(cookie string, shareID string, uk string, surl string, pwd string, fsid string, destPath string) (string, error) {
	if strings.TrimSpace(pwd) != "" {
		if err := baiduVerifySharePwd(surl, pwd, &cookie); err != nil {
			return cookie, err
		}
	}
	u, _ := url.Parse("https://pan.baidu.com/share/transfer")
	q := u.Query()
	q.Set("shareid", strings.TrimSpace(shareID))
	q.Set("from", strings.TrimSpace(uk))
	q.Set("ondup", "newcopy")
	u.RawQuery = q.Encode()
	var fs any = strings.TrimSpace(fsid)
	if n, err := strconv.ParseInt(strings.TrimSpace(fsid), 10, 64); err == nil {
		fs = n
	}
	fsidList, _ := json.Marshal([]any{fs})
	form := url.Values{}
	form.Set("fsidlist", string(fsidList))
	form.Set("path", strings.TrimSpace(destPath))
	objAny, setCookie, err := baiduFetchJSONWithHeaders(http.MethodPost, u.String(), cookie, []byte(form.Encode()), map[string]string{
		"User-Agent":    baiduScriptWebUA,
		"Content-Type":  "application/x-www-form-urlencoded",
	})
	if err != nil {
		return cookie, err
	}
	if setCookie != nil && len(setCookie) > 0 {
		cookie = mergeCookieFromSetCookie(cookie, setCookie)
	}
	if err := baiduErrnoOk(objAny); err != nil {
		return cookie, err
	}
	return cookie, nil
}

func baiduMediaInfo(cookie string, path string) (string, error) {
	p := strings.TrimSpace(path)
	if p == "" || !strings.HasPrefix(p, "/") {
		return "", errors.New("invalid path")
	}
	u, _ := url.Parse("https://pan.baidu.com/api/mediainfo")
	q := u.Query()
	q.Set("type", "M3U8_FLV_264_480")
	q.Set("path", p)
	q.Set("origin", "dlna")
	q.Set("check_blue", "1")
	q.Set("app_id", baiduAppID)
	q.Set("devuid", "kx1cK7VGweDrdrLiQpQRZduW5KTFvBHU|YyLyiRidC")
	q.Set("clienttype", "80")
	q.Set("channel", "android_12_V2238A_bd-netdisk_1024266g")
	q.Set("network_type", "wifi")
	q.Set("version", "12.11.9")
	u.RawQuery = q.Encode()
	objAny, _, err := baiduFetchJSONWithHeaders(http.MethodGet, u.String(), cookie, nil, map[string]string{
		"User-Agent":   baiduScriptNetdiskUA,
		"Content-Type": "application/x-www-form-urlencoded",
	})
	if err != nil {
		return "", err
	}
	if err := baiduErrnoOk(objAny); err != nil {
		return "", err
	}
	root, _ := objAny.(map[string]any)
	info, _ := root["info"].(map[string]any)
	dlink := ""
	if info != nil {
		dlink = strings.TrimSpace(toString(info["dlink"]))
	}
	if dlink == "" {
		return "", errors.New("baidu mediainfo missing dlink")
	}
	return dlink, nil
}

func baiduResolveFinalURLFromDlink(dlink string) (string, error) {
	cur := strings.TrimSpace(dlink)
	if cur == "" {
		return "", errors.New("missing dlink")
	}
	client := &http.Client{
		Timeout: 20 * time.Second,
		CheckRedirect: func(req *http.Request, via []*http.Request) error {
			return http.ErrUseLastResponse
		},
	}
	for i := 0; i < 5; i++ {
		req, _ := http.NewRequest(http.MethodGet, cur, nil)
		req.Header.Set("User-Agent", baiduPlayUA)
		req.Header.Set("Range", "bytes=0-0")
		resp, err := client.Do(req)
		if err != nil {
			return "", err
		}
		_ = resp.Body.Close()
		status := resp.StatusCode
		loc := strings.TrimSpace(resp.Header.Get("Location"))
		if status >= 300 && status < 400 && loc != "" {
			ref, err := url.Parse(loc)
			if err != nil {
				cur = loc
				continue
			}
			if ref.IsAbs() {
				cur = ref.String()
				continue
			}
			base, err := url.Parse(cur)
			if err != nil || base == nil {
				cur = loc
				continue
			}
			cur = base.ResolveReference(ref).String()
			continue
		}
		if status >= 200 && status < 300 {
			return cur, nil
		}
		return "", errors.New("baidu dlink resolve http " + strconv.Itoa(status))
	}
	return cur, nil
}

func BaiduPlay(database *db.DB, flag string, id string, dirPath string) (string, map[string]string, error) {
	decoded, _ := baiduDecodePlayIDToJSON(id)
	if decoded == nil {
		return "", nil, errors.New("invalid id")
	}
	shareID := strings.TrimSpace(toString(decoded["shareid"]))
	if shareID == "" {
		shareID = strings.TrimSpace(toString(decoded["share_id"]))
	}
	uk := strings.TrimSpace(toString(decoded["uk"]))
	if uk == "" {
		uk = strings.TrimSpace(toString(decoded["share_uk"]))
	}
	fsid := strings.TrimSpace(toString(decoded["fs_id"]))
	if fsid == "" {
		fsid = strings.TrimSpace(toString(decoded["fsid"]))
	}
	surl := strings.TrimSpace(toString(decoded["surl"]))
	if surl == "" {
		surl = parseBaiduSurlFromFlag(flag)
	}
	pwd := strings.TrimSpace(toString(decoded["pwd"]))
	fileName := strings.TrimSpace(toString(decoded["realName"]))
	if fileName == "" {
		fileName = baiduExtractNameFromID(id)
	}
	if shareID == "" || uk == "" || fsid == "" {
		return "", nil, errors.New("missing shareid/uk/fsid")
	}
	if fileName == "" {
		return "", nil, errors.New("missing filename")
	}
	if surl == "" {
		return "", nil, errors.New("missing surl")
	}

	store := readPanLoginSettings(database)
	cookie := getPanField(store, "baidu", "cookie")
	if strings.TrimSpace(cookie) == "" {
		return "", nil, errors.New("missing baidu cookie (pan_login_settings[\"baidu\"].cookie)")
	}

	ensuredPath, cookie2, err := baiduEnsureDir(cookie, dirPath)
	if err != nil {
		return "", nil, err
	}
	cookie3, err := baiduShareTransferToDir(cookie2, shareID, uk, surl, pwd, fsid, ensuredPath)
	if err != nil {
		return "", nil, err
	}
	safeName := path.Base(strings.ReplaceAll(fileName, "\\", "/"))
	safeName = strings.TrimLeft(safeName, "/")
	fullPath := strings.ReplaceAll(strings.TrimRight(ensuredPath, "/")+"/"+safeName, "//", "/")
	dlink, err := baiduMediaInfo(cookie3, fullPath)
	if err != nil {
		return "", nil, err
	}
	finalURL, err := baiduResolveFinalURLFromDlink(dlink)
	if err != nil {
		return "", nil, err
	}
	headers := map[string]string{"User-Agent": baiduPlayUA}
	return finalURL, headers, nil
}

func HandleDashboardBaiduStart(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}

	now := time.Now()
	cleanupBaiduQRSessions(now)

	client, jar, err := makeBaiduQRClient()
	if err != nil {
		writeJSON(w, 500, map[string]any{"success": false, "message": "初始化失败"})
		return
	}

	qid := randHex(12)
	gid := strings.ToUpper(randHex(16))
	cb := "bd__cbs__" + randHex(6)

	sign, img, imgType, err := baiduQRGetQRCode(client, gid, cb)
	if err != nil {
		writeJSON(w, 500, map[string]any{"success": false, "message": err.Error()})
		return
	}

	s := &baiduQRSession{
		ID:        qid,
		CreatedAt: now,
		ExpiresAt: now.Add(3 * time.Minute),
		GID:       gid,
		Callback:  cb,
		Sign:      sign,
		Image:     img,
		ImageType: imgType,
		Client:    client,
		Jar:       jar,
	}
	baiduQRSessions.Store(qid, s)

	writeJSON(w, 200, map[string]any{
		"success":   true,
		"qid":       qid,
		"expiresAt": s.ExpiresAt.UnixMilli(),
		"imageUrl":  "/dashboard/pan/baidu/image?qid=" + url.QueryEscape(qid),
	})
}

func HandleDashboardBaiduImage(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet {
		methodNotAllowed(w)
		return
	}
	qid := strings.TrimSpace(r.URL.Query().Get("qid"))
	if qid == "" {
		writeJSON(w, 400, map[string]any{"success": false, "message": "qid 不能为空"})
		return
	}
	v, ok := baiduQRSessions.Load(qid)
	if !ok {
		writeJSON(w, 404, map[string]any{"success": false, "message": "二维码已过期"})
		return
	}
	s, ok := v.(*baiduQRSession)
	if !ok || s == nil || time.Now().After(s.ExpiresAt) {
		baiduQRSessions.Delete(qid)
		writeJSON(w, 404, map[string]any{"success": false, "message": "二维码已过期"})
		return
	}

	ct := s.ImageType
	if ct == "" {
		ct = "image/png"
	}
	w.Header().Set("Content-Type", ct)
	w.Header().Set("Cache-Control", "no-store")
	_, _ = w.Write(s.Image)
}

func HandleDashboardBaiduCookie(w http.ResponseWriter, r *http.Request, database *db.DB) {
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

	v, ok := baiduQRSessions.Load(qid)
	if !ok {
		writeJSON(w, 404, map[string]any{"success": false, "message": "二维码已过期"})
		return
	}
	s, ok := v.(*baiduQRSession)
	if !ok || s == nil || time.Now().After(s.ExpiresAt) {
		baiduQRSessions.Delete(qid)
		writeJSON(w, 404, map[string]any{"success": false, "message": "二维码已过期"})
		return
	}

	s.mu.Lock()
	defer s.mu.Unlock()

	if s.Cookie != "" {
		writeJSON(w, 200, map[string]any{"success": true, "status": "confirmed", "cookie": s.Cookie})
		return
	}

	status, bduss, err := baiduQRPoll(s.Client, s.Sign, s.GID, s.Callback)
	s.LastStatus = status
	if err != nil {
		s.LastErr = err.Error()
		writeJSON(w, 500, map[string]any{"success": false, "message": err.Error(), "status": "error"})
		return
	}
	if status != "confirmed" {
		writeJSON(w, 409, map[string]any{"success": false, "status": status, "message": "未确认登录"})
		return
	}

	cookie, err := baiduQRFinalize(s.Client, bduss)
	if err != nil {
		s.LastErr = err.Error()
		writeJSON(w, 500, map[string]any{"success": false, "message": err.Error(), "status": "error"})
		return
	}
	s.Cookie = cookie

	store := parseJSONMap(database.GetSetting("pan_login_settings"))
	cur, _ := store["baidu"].(map[string]any)
	if cur == nil {
		cur = map[string]any{}
	}
	cur["cookie"] = cookie
	store["baidu"] = cur
	b, _ := json.Marshal(store)
	_ = database.SetSetting("pan_login_settings", string(b))

	writeJSON(w, 200, map[string]any{"success": true, "status": "confirmed", "cookie": cookie})
}
