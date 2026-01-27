package routes

import (
	"bytes"
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
		Timeout: 12 * time.Second,
		Jar:     jar,
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

func handleDashboardUCQRStart(w http.ResponseWriter, r *http.Request, database *db.DB) {
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
		"imageUrl":  "/dashboard/pan/uc/qr/image?qid=" + url.QueryEscape(qid) + "&_t=" + url.QueryEscape(strconv.FormatInt(now.UnixMilli(), 10)),
	})
	_ = database
}

func handleDashboardUCQRImage(w http.ResponseWriter, r *http.Request) {
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

func handleDashboardUCQRCookie(w http.ResponseWriter, r *http.Request, database *db.DB) {
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

	store := parseJSONMap(database.GetSetting("pan_login_settings"))
	cur, _ := store["uc"].(map[string]any)
	if cur == nil {
		cur = map[string]any{}
	}
	cur["cookie"] = cookie
	store["uc"] = cur
	b, _ := json.Marshal(store)
	_ = database.SetSetting("pan_login_settings", string(b))

	writeJSON(w, 200, map[string]any{"success": true, "status": "confirmed", "cookie": cookie})
}
