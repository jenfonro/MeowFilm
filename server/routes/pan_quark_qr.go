package routes

import (
	"bytes"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"net/http/cookiejar"
	"net/url"
	"os/exec"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/jenfonro/TV_Server/internal/db"
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
		Timeout: 12 * time.Second,
		Jar:     jar,
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

func handleDashboardQuarkQRStart(w http.ResponseWriter, r *http.Request, database *db.DB) {
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
		"imageUrl":  "/dashboard/pan/quark/qr/image?qid=" + url.QueryEscape(qid) + "&_t=" + url.QueryEscape(strconv.FormatInt(now.UnixMilli(), 10)),
	})
	_ = database
}

func handleDashboardQuarkQRImage(w http.ResponseWriter, r *http.Request) {
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

func handleDashboardQuarkQRCookie(w http.ResponseWriter, r *http.Request, database *db.DB) {
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

	store := parseJSONMap(database.GetSetting("pan_login_settings"))
	cur, _ := store["quark"].(map[string]any)
	if cur == nil {
		cur = map[string]any{}
	}
	cur["cookie"] = cookie
	store["quark"] = cur
	b, _ := json.Marshal(store)
	_ = database.SetSetting("pan_login_settings", string(b))

	writeJSON(w, 200, map[string]any{"success": true, "status": "confirmed", "cookie": cookie})
}
