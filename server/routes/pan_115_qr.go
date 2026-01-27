package routes

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/url"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/jenfonro/TV_Server/internal/db"
)

type pan115QRSession struct {
	ID        string
	CreatedAt time.Time
	ExpiresAt time.Time

	UID  string
	Time int64
	Sign string

	Image     []byte
	ImageType string

	Client *http.Client

	Cookie     string
	LastStatus string
	LastErr    string
	mu         sync.Mutex
}

var pan115QRSessions sync.Map // id -> *pan115QRSession

const (
	pan115QRUA            = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36"
	pan115TokenURL        = "https://qrcodeapi.115.com/api/1.0/web/1.0/token/"
	pan115QRImageBaseURL  = "https://qrcodeapi.115.com/api/1.0/mac/1.0/qrcode"
	pan115QRStatusBaseURL = "https://qrcodeapi.115.com/get/status/"
	pan115QRLoginURL      = "https://passportapi.115.com/app/1.0/web/1.0/login/qrcode/"
)

func cleanup115QRSessions(now time.Time) {
	pan115QRSessions.Range(func(key, value any) bool {
		s, ok := value.(*pan115QRSession)
		if !ok || s == nil {
			pan115QRSessions.Delete(key)
			return true
		}
		if now.After(s.ExpiresAt) {
			pan115QRSessions.Delete(key)
		}
		return true
	})
}

func make115QRClient() *http.Client {
	return &http.Client{Timeout: 20 * time.Second}
}

func pan115DoReq(client *http.Client, method string, urlStr string, body []byte, headers map[string]string) ([]byte, http.Header, error) {
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
		return nil, resp.Header, errors.New("115 http " + strconv.Itoa(resp.StatusCode) + ": " + msg)
	}
	return buf, resp.Header, nil
}

func pan115IsTimeoutErr(err error) bool {
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

func pan115Headers(extra map[string]string) map[string]string {
	h := map[string]string{
		"User-Agent":      pan115QRUA,
		"Accept":          "application/json, text/plain, */*",
		"Accept-Language": "zh-CN,zh;q=0.9,en;q=0.8",
		"Connection":      "keep-alive",
		"Origin":          "https://115.com",
		"Referer":         "https://115.com/",
	}
	for k, v := range extra {
		h[k] = v
	}
	return h
}

func pan115ExtractTokenFields(body []byte) (uid string, t int64, sign string, err error) {
	var raw any
	_ = json.Unmarshal(body, &raw)
	root, _ := raw.(map[string]any)
	data, _ := root["data"].(map[string]any)
	if data == nil {
		data = root
	}
	if s, ok := data["uid"].(string); ok {
		uid = strings.TrimSpace(s)
	} else if n, ok := data["uid"].(float64); ok {
		uid = strings.TrimSpace(strconv.FormatInt(int64(n), 10))
	} else if n, ok := data["uid"].(json.Number); ok {
		uid = strings.TrimSpace(n.String())
	}
	if s, ok := data["sign"].(string); ok {
		sign = strings.TrimSpace(s)
	}
	if n, ok := data["time"].(float64); ok {
		t = int64(n)
	} else if n, ok := data["time"].(json.Number); ok {
		if nn, e := n.Int64(); e == nil {
			t = nn
		}
	} else if s, ok := data["time"].(string); ok {
		if nn, e := strconv.ParseInt(strings.TrimSpace(s), 10, 64); e == nil {
			t = nn
		}
	}
	if uid == "" {
		uid = ucExtractFirstStringByKey(raw, "uid")
	}
	if sign == "" {
		sign = ucExtractFirstStringByKey(raw, "sign")
	}
	if t == 0 {
		if n, ok := ucExtractFirstNumberByKey(raw, "time"); ok {
			t = int64(n)
		}
	}
	if uid == "" || sign == "" || t == 0 {
		return "", 0, "", errors.New("115 token response missing fields")
	}
	return uid, t, sign, nil
}

func pan115GetQRCode(client *http.Client) (uid string, t int64, sign string, img []byte, imgType string, err error) {
	body, _, err := pan115DoReq(client, "GET", pan115TokenURL, nil, pan115Headers(nil))
	if err != nil {
		return "", 0, "", nil, "", err
	}
	uid, t, sign, err = pan115ExtractTokenFields(body)
	if err != nil {
		return "", 0, "", nil, "", err
	}
	u, _ := url.Parse(pan115QRImageBaseURL)
	qs := u.Query()
	qs.Set("uid", uid)
	u.RawQuery = qs.Encode()
	img, hdr, err := pan115DoReq(client, "GET", u.String(), nil, pan115Headers(map[string]string{
		"Accept": "image/avif,image/webp,image/apng,image/*,*/*;q=0.8",
	}))
	if err != nil {
		return "", 0, "", nil, "", err
	}
	ct := strings.TrimSpace(hdr.Get("Content-Type"))
	if ct == "" {
		ct = http.DetectContentType(img)
	}
	return uid, t, sign, img, ct, nil
}

func pan115PollStatus(client *http.Client, uid string, t int64, sign string) (int, error) {
	if strings.TrimSpace(uid) == "" || t == 0 || strings.TrimSpace(sign) == "" {
		return 0, errors.New("missing uid/time/sign")
	}
	u, _ := url.Parse(pan115QRStatusBaseURL)
	qs := u.Query()
	qs.Set("uid", strings.TrimSpace(uid))
	qs.Set("time", strconv.FormatInt(t, 10))
	qs.Set("sign", strings.TrimSpace(sign))
	u.RawQuery = qs.Encode()

	body, _, err := pan115DoReq(client, "GET", u.String(), nil, pan115Headers(nil))
	if err != nil {
		return 0, err
	}
	var raw any
	_ = json.Unmarshal(body, &raw)
	root, _ := raw.(map[string]any)
	data, _ := root["data"].(map[string]any)
	if data != nil {
		if n, ok := data["status"].(float64); ok {
			return int(n), nil
		}
		if n, ok := data["status"].(json.Number); ok {
			if nn, e := n.Int64(); e == nil {
				return int(nn), nil
			}
		}
		if s, ok := data["status"].(string); ok {
			if nn, e := strconv.Atoi(strings.TrimSpace(s)); e == nil {
				return nn, nil
			}
		}
	}
	if n, ok := ucExtractFirstNumberByKey(raw, "status"); ok {
		return int(n), nil
	}
	return 0, errors.New("115 status missing")
}

func pan115CookieStringFromMap(m map[string]any) string {
	if m == nil {
		return ""
	}
	preferred := []string{"UID", "CID", "SEID"}
	used := map[string]struct{}{}
	parts := make([]string, 0, len(m))
	appendKV := func(k string) {
		v, ok := m[k]
		if !ok {
			return
		}
		val := strings.TrimSpace(fmt.Sprint(v))
		if val == "" {
			return
		}
		parts = append(parts, k+"="+val)
		used[k] = struct{}{}
	}
	for _, k := range preferred {
		appendKV(k)
	}
	keys := make([]string, 0, len(m))
	for k := range m {
		if strings.TrimSpace(k) == "" {
			continue
		}
		if _, ok := used[k]; ok {
			continue
		}
		keys = append(keys, k)
	}
	sort.Strings(keys)
	for _, k := range keys {
		appendKV(k)
	}
	return strings.Join(parts, "; ")
}

func pan115LoginCookie(client *http.Client, uid string) (string, error) {
	form := url.Values{}
	form.Set("app", "web")
	form.Set("account", strings.TrimSpace(uid))
	body, _, err := pan115DoReq(client, "POST", pan115QRLoginURL, []byte(form.Encode()), pan115Headers(map[string]string{
		"Content-Type": "application/x-www-form-urlencoded; charset=UTF-8",
		"Origin":       "https://115.com",
		"Referer":      "https://115.com/",
	}))
	if err != nil {
		return "", err
	}
	var raw any
	_ = json.Unmarshal(body, &raw)
	root, _ := raw.(map[string]any)
	data, _ := root["data"].(map[string]any)
	if data == nil {
		data = root
	}
	cookieAny, ok := data["cookie"]
	if ok {
		switch c := cookieAny.(type) {
		case string:
			if strings.TrimSpace(c) != "" {
				return strings.TrimSpace(c), nil
			}
		case map[string]any:
			if s := pan115CookieStringFromMap(c); s != "" {
				return s, nil
			}
		}
	}
	if c := ucExtractFirstStringByKey(raw, "cookie"); strings.TrimSpace(c) != "" {
		return strings.TrimSpace(c), nil
	}
	return "", errors.New("115 cookie missing")
}

func handleDashboard115QRStart(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	now := time.Now()
	cleanup115QRSessions(now)

	client := make115QRClient()
	uid, t, sign, img, imgType, err := pan115GetQRCode(client)
	if err != nil {
		writeJSON(w, 500, map[string]any{"success": false, "message": err.Error()})
		return
	}

	qid := randHexN(12)
	s := &pan115QRSession{
		ID:        qid,
		CreatedAt: now,
		ExpiresAt: now.Add(3 * time.Minute),
		UID:       uid,
		Time:      t,
		Sign:      sign,
		Image:     img,
		ImageType: imgType,
		Client:    client,
	}
	pan115QRSessions.Store(qid, s)

	writeJSON(w, 200, map[string]any{
		"success":   true,
		"qid":       qid,
		"expiresAt": s.ExpiresAt.UnixMilli(),
		"imageUrl":  "/dashboard/pan/115/qr/image?qid=" + url.QueryEscape(qid),
	})
	_ = database
}

func handleDashboard115QRImage(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet {
		methodNotAllowed(w)
		return
	}
	qid := strings.TrimSpace(r.URL.Query().Get("qid"))
	if qid == "" {
		writeJSON(w, 400, map[string]any{"success": false, "message": "qid 不能为空"})
		return
	}
	v, ok := pan115QRSessions.Load(qid)
	if !ok {
		writeJSON(w, 404, map[string]any{"success": false, "message": "二维码已过期"})
		return
	}
	s, ok := v.(*pan115QRSession)
	if !ok || s == nil || time.Now().After(s.ExpiresAt) {
		pan115QRSessions.Delete(qid)
		writeJSON(w, 404, map[string]any{"success": false, "message": "二维码已过期"})
		return
	}
	ct := s.ImageType
	if strings.TrimSpace(ct) == "" {
		ct = "image/png"
	}
	w.Header().Set("Content-Type", ct)
	w.Header().Set("Cache-Control", "no-store")
	_, _ = w.Write(s.Image)
}

func handleDashboard115QRCookie(w http.ResponseWriter, r *http.Request, database *db.DB) {
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
	v, ok := pan115QRSessions.Load(qid)
	if !ok {
		writeJSON(w, 404, map[string]any{"success": false, "message": "二维码已过期"})
		return
	}
	s, ok := v.(*pan115QRSession)
	if !ok || s == nil || time.Now().After(s.ExpiresAt) {
		pan115QRSessions.Delete(qid)
		writeJSON(w, 404, map[string]any{"success": false, "message": "二维码已过期"})
		return
	}

	s.mu.Lock()
	defer s.mu.Unlock()

	if s.Cookie != "" {
		writeJSON(w, 200, map[string]any{"success": true, "status": "confirmed", "cookie": s.Cookie})
		return
	}

	st, err := pan115PollStatus(s.Client, s.UID, s.Time, s.Sign)
	if err != nil {
		if pan115IsTimeoutErr(err) {
			s.LastErr = err.Error()
			s.LastStatus = "pending"
			writeJSON(w, http.StatusConflict, map[string]any{"success": false, "status": "pending", "message": "请求超时，重试中..."})
			return
		}
		s.LastErr = err.Error()
		s.LastStatus = "error"
		writeJSON(w, 500, map[string]any{"success": false, "message": err.Error(), "status": "error"})
		return
	}
	switch st {
	case 0:
		s.LastStatus = "pending"
		writeJSON(w, http.StatusConflict, map[string]any{"success": false, "status": "pending", "message": "未确认登录"})
		return
	case 1:
		s.LastStatus = "scanned"
		writeJSON(w, http.StatusConflict, map[string]any{"success": false, "status": "scanned", "message": "未确认登录"})
		return
	case -1, -2:
		pan115QRSessions.Delete(qid)
		writeJSON(w, 404, map[string]any{"success": false, "message": "二维码已过期"})
		return
	case 2:
		// continue
	default:
		s.LastStatus = "pending"
		writeJSON(w, http.StatusConflict, map[string]any{"success": false, "status": "pending", "message": "未确认登录"})
		return
	}

	cookie, err := pan115LoginCookie(s.Client, s.UID)
	if err != nil {
		s.LastErr = err.Error()
		s.LastStatus = "error"
		writeJSON(w, 500, map[string]any{"success": false, "message": err.Error(), "status": "error"})
		return
	}
	s.Cookie = cookie
	s.LastStatus = "confirmed"

	store := parseJSONMap(database.GetSetting("pan_login_settings"))
	cur, _ := store["115"].(map[string]any)
	if cur == nil {
		cur = map[string]any{}
	}
	cur["cookie"] = cookie
	store["115"] = cur
	b, _ := json.Marshal(store)
	_ = database.SetSetting("pan_login_settings", string(b))

	writeJSON(w, 200, map[string]any{"success": true, "status": "confirmed", "cookie": cookie})
}
