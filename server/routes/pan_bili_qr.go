package routes

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"io"
	"net"
	"net/http"
	"net/http/cookiejar"
	"net/url"
	"os/exec"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
)

type biliQRSession struct {
	ID        string
	CreatedAt time.Time
	ExpiresAt time.Time

	Key string
	URL string

	Image     []byte
	ImageType string

	Client *http.Client
	Jar    http.CookieJar

	Cookie     string
	LastStatus string
	LastErr    string
	mu         sync.Mutex
}

var biliQRSessions sync.Map // id -> *biliQRSession

const (
	biliQRUA       = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36 Edg/121.0.0.0"
	biliReferer    = "https://www.bilibili.com/"
	biliOrigin     = "https://www.bilibili.com"
	biliHome       = "https://www.bilibili.com/"
	biliGenerate   = "https://passport.bilibili.com/x/passport-login/web/qrcode/generate"
	biliPoll       = "https://passport.bilibili.com/x/passport-login/web/qrcode/poll"
	biliSessionTTL = 3 * time.Minute
)

func cleanupBiliQRSessions(now time.Time) {
	biliQRSessions.Range(func(key, value any) bool {
		s, ok := value.(*biliQRSession)
		if !ok || s == nil {
			biliQRSessions.Delete(key)
			return true
		}
		if now.After(s.ExpiresAt) {
			biliQRSessions.Delete(key)
		}
		return true
	})
}

func makeBiliQRClient() (*http.Client, http.CookieJar, error) {
	jar, err := cookiejar.New(nil)
	if err != nil {
		return nil, nil, err
	}
	return &http.Client{
		Timeout: 20 * time.Second,
		Jar:     jar,
	}, jar, nil
}

func biliIsTimeoutErr(err error) bool {
	if err == nil {
		return false
	}
	if errors.Is(err, context.DeadlineExceeded) {
		return true
	}
	var ne net.Error
	if errors.As(err, &ne) && ne.Timeout() {
		return true
	}
	var ue *url.Error
	if errors.As(err, &ue) && ue != nil && ue.Timeout() {
		return true
	}
	return false
}

func biliQRDoReq(client *http.Client, method string, urlStr string, body []byte, headers map[string]string) ([]byte, http.Header, error) {
	if client == nil {
		return nil, nil, errors.New("missing http client")
	}
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
		return nil, resp.Header, errors.New("bili http " + strconv.Itoa(resp.StatusCode) + ": " + msg)
	}
	return buf, resp.Header, nil
}

func biliHeaders(extra map[string]string) map[string]string {
	h := map[string]string{
		"User-Agent":      biliQRUA,
		"Accept":          "application/json, text/plain, */*",
		"Accept-Language": "zh-CN,zh;q=0.9,en;q=0.8",
		"Connection":      "keep-alive",
		"Referer":         biliReferer,
		"Origin":          biliOrigin,
	}
	for k, v := range extra {
		h[k] = v
	}
	return h
}

func biliWarmupCookies(client *http.Client) {
	if client == nil {
		return
	}
	_, _, _ = biliQRDoReq(client, "GET", biliHome, nil, biliHeaders(map[string]string{
		"Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
	}))
}

func biliGenerateQR(client *http.Client) (qrURL string, qrKey string, err error) {
	body, _, err := biliQRDoReq(client, "GET", biliGenerate, nil, biliHeaders(nil))
	if err != nil {
		return "", "", err
	}
	var resp struct {
		Code    int    `json:"code"`
		Message string `json:"message"`
		Data    struct {
			URL string `json:"url"`
			Key string `json:"qrcode_key"`
		} `json:"data"`
	}
	if err := json.Unmarshal(body, &resp); err != nil {
		return "", "", err
	}
	if resp.Code != 0 || strings.TrimSpace(resp.Data.URL) == "" || strings.TrimSpace(resp.Data.Key) == "" {
		msg := strings.TrimSpace(resp.Message)
		if msg == "" {
			msg = "bili qrcode generate failed"
		}
		return "", "", errors.New(msg)
	}
	return strings.TrimSpace(resp.Data.URL), strings.TrimSpace(resp.Data.Key), nil
}

func biliEncodePNG(text string) ([]byte, error) {
	if strings.TrimSpace(text) == "" {
		return nil, errors.New("empty qr text")
	}
	cmd := exec.Command("qrencode", "-o", "-", "-t", "PNG", "-s", "6", "-m", "2", "--", text)
	out, err := cmd.Output()
	if err != nil {
		return nil, err
	}
	if len(out) == 0 {
		return nil, errors.New("qrencode empty output")
	}
	return out, nil
}

func biliCookieFromJar(jar http.CookieJar) string {
	if jar == nil {
		return ""
	}
	base, _ := url.Parse("https://www.bilibili.com/")
	p, _ := url.Parse("https://passport.bilibili.com/")
	a, _ := url.Parse("https://api.bilibili.com/")
	cookies := append([]*http.Cookie{}, jar.Cookies(base)...)
	cookies = append(cookies, jar.Cookies(p)...)
	cookies = append(cookies, jar.Cookies(a)...)

	m := map[string]string{}
	for _, c := range cookies {
		if c == nil {
			continue
		}
		name := strings.TrimSpace(c.Name)
		val := strings.TrimSpace(c.Value)
		if name == "" || val == "" {
			continue
		}
		if _, ok := m[name]; ok {
			continue
		}
		m[name] = val
	}
	if len(m) == 0 {
		return ""
	}
	preferred := []string{"SESSDATA", "bili_jct", "DedeUserID", "DedeUserID__ckMd5", "sid", "buvid3", "buvid4", "_uuid"}
	used := map[string]struct{}{}
	parts := make([]string, 0, len(m))
	add := func(k string) {
		v, ok := m[k]
		if !ok || strings.TrimSpace(v) == "" {
			return
		}
		parts = append(parts, k+"="+v)
		used[k] = struct{}{}
	}
	for _, k := range preferred {
		add(k)
	}
	keys := make([]string, 0, len(m))
	for k := range m {
		if _, ok := used[k]; ok {
			continue
		}
		keys = append(keys, k)
	}
	sort.Strings(keys)
	for _, k := range keys {
		add(k)
	}
	return strings.Join(parts, "; ")
}

func biliPollStatus(client *http.Client, qrKey string) (status string, redirectURL string, err error) {
	u, _ := url.Parse(biliPoll)
	qs := u.Query()
	qs.Set("qrcode_key", strings.TrimSpace(qrKey))
	u.RawQuery = qs.Encode()

	body, _, err := biliQRDoReq(client, "GET", u.String(), nil, biliHeaders(nil))
	if err != nil {
		return "", "", err
	}
	var resp struct {
		Code    int    `json:"code"`
		Message string `json:"message"`
		Data    struct {
			Code    int    `json:"code"`
			Message string `json:"message"`
			URL     string `json:"url"`
		} `json:"data"`
	}
	if err := json.Unmarshal(body, &resp); err != nil {
		return "", "", err
	}
	if resp.Code != 0 {
		msg := strings.TrimSpace(resp.Message)
		if msg == "" {
			msg = "bili poll failed"
		}
		return "error", "", errors.New(msg)
	}
	switch resp.Data.Code {
	case 0:
		return "confirmed", strings.TrimSpace(resp.Data.URL), nil
	case 86101:
		return "pending", "", nil
	case 86090:
		return "scanned", "", nil
	case 86038:
		return "expired", "", nil
	default:
		return "pending", "", nil
	}
}

func handleDashboardBiliQRStart(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	now := time.Now()
	cleanupBiliQRSessions(now)

	client, jar, err := makeBiliQRClient()
	if err != nil {
		writeJSON(w, 500, map[string]any{"success": false, "message": "初始化失败"})
		return
	}
	biliWarmupCookies(client)

	qrURL, qrKey, err := biliGenerateQR(client)
	if err != nil {
		writeJSON(w, 500, map[string]any{"success": false, "message": err.Error()})
		return
	}
	img, err := biliEncodePNG(qrURL)
	if err != nil {
		writeJSON(w, 500, map[string]any{"success": false, "message": "二维码编码失败"})
		return
	}

	qid := randHexN(12)
	s := &biliQRSession{
		ID:        qid,
		CreatedAt: now,
		ExpiresAt: now.Add(biliSessionTTL),
		Key:       qrKey,
		URL:       qrURL,
		Image:     img,
		ImageType: "image/png",
		Client:    client,
		Jar:       jar,
	}
	biliQRSessions.Store(qid, s)

	writeJSON(w, 200, map[string]any{
		"success":   true,
		"qid":       qid,
		"expiresAt": s.ExpiresAt.UnixMilli(),
		"imageUrl":  "/dashboard/pan/bili/qr/image?qid=" + url.QueryEscape(qid) + "&_t=" + url.QueryEscape(strconv.FormatInt(now.UnixMilli(), 10)),
	})
	_ = database
}

func handleDashboardBiliQRImage(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet {
		methodNotAllowed(w)
		return
	}
	qid := strings.TrimSpace(r.URL.Query().Get("qid"))
	if qid == "" {
		writeJSON(w, 400, map[string]any{"success": false, "message": "qid 不能为空"})
		return
	}
	v, ok := biliQRSessions.Load(qid)
	if !ok {
		writeJSON(w, 404, map[string]any{"success": false, "message": "二维码已过期"})
		return
	}
	s, ok := v.(*biliQRSession)
	if !ok || s == nil || time.Now().After(s.ExpiresAt) {
		biliQRSessions.Delete(qid)
		writeJSON(w, 404, map[string]any{"success": false, "message": "二维码已过期"})
		return
	}
	w.Header().Set("Content-Type", s.ImageType)
	w.Header().Set("Cache-Control", "no-store")
	_, _ = w.Write(s.Image)
}

func handleDashboardBiliQRCookie(w http.ResponseWriter, r *http.Request, database *db.DB) {
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

	v, ok := biliQRSessions.Load(qid)
	if !ok {
		writeJSON(w, 404, map[string]any{"success": false, "message": "二维码已过期"})
		return
	}
	s, ok := v.(*biliQRSession)
	if !ok || s == nil || time.Now().After(s.ExpiresAt) {
		biliQRSessions.Delete(qid)
		writeJSON(w, 404, map[string]any{"success": false, "message": "二维码已过期"})
		return
	}

	s.mu.Lock()
	defer s.mu.Unlock()

	if s.Cookie != "" {
		writeJSON(w, 200, map[string]any{"success": true, "status": "confirmed", "cookie": s.Cookie})
		return
	}

	status, nextURL, err := biliPollStatus(s.Client, s.Key)
	s.LastStatus = status
	if err != nil {
		if biliIsTimeoutErr(err) {
			writeJSON(w, http.StatusConflict, map[string]any{"success": false, "status": "pending", "message": "请求超时，重试中..."})
			return
		}
		s.LastErr = err.Error()
		writeJSON(w, 500, map[string]any{"success": false, "message": err.Error(), "status": "error"})
		return
	}
	if status == "expired" {
		biliQRSessions.Delete(qid)
		writeJSON(w, 404, map[string]any{"success": false, "message": "二维码已过期"})
		return
	}
	if status != "confirmed" {
		writeJSON(w, http.StatusConflict, map[string]any{"success": false, "status": status, "message": "未确认登录"})
		return
	}

	if strings.TrimSpace(nextURL) != "" {
		_, _, _ = biliQRDoReq(s.Client, "GET", strings.TrimSpace(nextURL), nil, biliHeaders(map[string]string{
			"Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
		}))
	}
	cookie := biliCookieFromJar(s.Jar)
	if strings.TrimSpace(cookie) == "" {
		s.LastErr = "cookie missing"
		writeJSON(w, 500, map[string]any{"success": false, "message": "cookie 获取失败", "status": "error"})
		return
	}
	s.Cookie = cookie

	store := parseJSONMap(database.GetSetting("pan_login_settings"))
	cur, _ := store["bili"].(map[string]any)
	if cur == nil {
		cur = map[string]any{}
	}
	cur["cookie"] = cookie
	store["bili"] = cur
	b, _ := json.Marshal(store)
	_ = database.SetSetting("pan_login_settings", string(b))

	writeJSON(w, 200, map[string]any{"success": true, "status": "confirmed", "cookie": cookie})
}
