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

	// Best-effort warm-up: ensure BAIDUID etc.
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
	// callback is optional; use JSON when possible.
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
		// ok
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
	// Baidu often wraps JSON in callback, keep callback always set.
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
		// Some responses use another layer of escaping; retry.
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

	// Ensure cookies for pan.baidu.com are present.
	_, _ = baiduQRDoReq(client, "GET", baiduQRBasePan, nil, map[string]string{
		"User-Agent": baiduQRUA,
		"Referer":    baiduQRBasePan,
	})

	// Build a cookie string for pan.baidu.com requests.
	panURL, _ := url.Parse(baiduQRBasePan)
	cookies := client.Jar.Cookies(panURL)
	cookieStr := formatCookieHeader(cookies)
	if !strings.Contains(cookieStr, "BDUSS=") {
		// Some cookies are attached to baidu.com; merge them.
		passURL, _ := url.Parse("https://passport.baidu.com/")
		more := client.Jar.Cookies(passURL)
		cookieStr = formatCookieHeader(append(cookies, more...))
	}
	if !strings.Contains(cookieStr, "BDUSS=") {
		return "", errors.New("cookie missing BDUSS")
	}
	return cookieStr, nil
}

// --- Share list (direct web share API) ---

var reBaiduSurl = regexp.MustCompile(`百度[^-]*-([^#]+)`)

func parseBaiduSurlFromFlag(flag string) string {
	raw := strings.TrimSpace(flag)
	if raw == "" {
		return ""
	}
	if m := reBaiduSurl.FindStringSubmatch(raw); len(m) == 2 {
		return strings.TrimSpace(m[1])
	}
	parts := strings.SplitN(raw, "-", 2)
	if len(parts) == 2 {
		return strings.TrimSpace(strings.SplitN(parts[1], "#", 2)[0])
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
	m, _ := obj.(map[string]any)
	if m == nil {
		return nil
	}
	if errno, ok := m["errno"]; ok && toString(errno) != "" && toString(errno) != "0" {
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
	_, data, shareID, uk, err := baiduShareListRoot(surl, pwd, baseCookie)
	if err != nil {
		return "", surl, err
	}
	list := baiduGetShareListArray(data)
	parts := []string{}
	for _, it := range list {
		if it == nil {
			continue
		}
		if toString(it["isdir"]) == "1" {
			continue
		}
		name := strings.TrimSpace(toString(it["server_filename"]))
		if name == "" {
			name = strings.TrimSpace(toString(it["name"]))
		}
		fsid := strings.TrimSpace(toString(it["fs_id"]))
		if fsid == "" {
			fsid = strings.TrimSpace(toString(it["fsid"]))
		}
		if name == "" || fsid == "" {
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
		id := base64.StdEncoding.EncodeToString(b) + "|||" + name
		parts = append(parts, name+"$"+id)
	}
	return strings.Join(parts, "#"), surl, nil
}

func HandleAPIBaiduList(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	var body struct {
		Flag string `json:"flag"`
		Pwd  string `json:"pwd"`
		Pass string `json:"pass"`
	}
	_ = readJSONLoose(r, &body)
	flag := strings.TrimSpace(body.Flag)
	pwd := strings.TrimSpace(body.Pwd)
	if pwd == "" {
		pwd = strings.TrimSpace(body.Pass)
	}
	vod, surl, err := BaiduList(database, flag, pwd)
	if err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": err.Error()})
		return
	}
	writeJSON(w, 200, map[string]any{"ok": true, "flag": flag, "surl": surl, "vod_play_url": vod})
}

func HandleAPIBaiduPlay(w http.ResponseWriter, r *http.Request, database *db.DB) {
	_ = database
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	writeJSON(w, http.StatusNotImplemented, map[string]any{"ok": false, "message": "baidu play not implemented yet (need transfer/mediainfo flow)"})
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
	// jsonp: callback({...})
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

	// Persist into pan_login_settings for admin to review/edit later.
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
