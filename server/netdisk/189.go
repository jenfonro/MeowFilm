package netdisk

import (
	"bytes"
	"crypto/rand"
	"crypto/rsa"
	"crypto/x509"
	"encoding/hex"
	"encoding/json"
	"encoding/pem"
	"errors"
	"io"
	"math/big"
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

const (
	tianyiAuthBase = "https://open.e.189.cn"
	tianyiAPIBase  = "https://cloud.189.cn"
	tianyiUA       = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36"
)

var (
	re189Flag = regexp.MustCompile(`^(?:天翼|天意)-([A-Za-z0-9]{6,64})$`)

	pan189LoginMu sync.Mutex
)

type tianyiEncryptConfResp struct {
	Result any `json:"result"`
	Data   struct {
		PubKey string `json:"pubKey"`
		Pre    string `json:"pre"`
	} `json:"data"`
}

type tianyiAppConfResp struct {
	Result any    `json:"result"`
	Msg    string `json:"msg"`
	Data   struct {
		AccountType string `json:"accountType"`
		MailSuffix  string `json:"mailSuffix"`
		ClientType  any    `json:"clientType"`
		IsOauth2    any    `json:"isOauth2"`
		ReturnURL   string `json:"returnUrl"`
		ParamID     string `json:"paramId"`
	} `json:"data"`
}

type tianyiLoginSubmitResp struct {
	Result any    `json:"result"`
	Msg    string `json:"msg"`
	ToURL  string `json:"toUrl"`
}

type tianyiShareInfoResp struct {
	ResCode    any    `json:"res_code"`
	ResMessage string `json:"res_message"`
	NeedAccess any    `json:"needAccessCode"`
	ShareId    any    `json:"shareId"`
	FileId     any    `json:"fileId"`
	FileName   string `json:"fileName"`
	IsFolder   any    `json:"isFolder"`
	ShareMode  any    `json:"shareMode"`
}

type tianyiListShareDirResp struct {
	ResCode    any    `json:"res_code"`
	ResMessage string `json:"res_message"`
	FileListAO struct {
		Count    int `json:"count"`
		FileList []struct {
			ID         any    `json:"id"`
			Name       string `json:"name"`
			IsFolder   any    `json:"isFolder"`
			FileCata   any    `json:"fileCata"`
			Size       any    `json:"size"`
			LastOpTime string `json:"lastOpTime"`
		} `json:"fileList"`
		FolderList []struct {
			ID         any    `json:"id"`
			Name       string `json:"name"`
			ParentID   any    `json:"parentId"`
			FileCata   any    `json:"fileCata"`
			Size       any    `json:"fileListSize"`
			LastOpTime string `json:"lastOpTime"`
		} `json:"folderList"`
	} `json:"fileListAO"`
}

type tianyiGetDownloadURLResp struct {
	ResCode    any    `json:"res_code"`
	ResMessage string `json:"res_message"`
	Data any `json:"data"`
}

func parse189ShareCodeLike(flagOrURL string) string {
	s := strings.TrimSpace(flagOrURL)
	if s == "" {
		return ""
	}
	if m := re189Flag.FindStringSubmatch(s); len(m) == 2 {
		return strings.TrimSpace(m[1])
	}
	if m := regexp.MustCompile(`(?i)^https?://cloud\.189\.cn/t/([A-Za-z0-9]{6,64})(?:\b|/|$)`).FindStringSubmatch(s); len(m) == 2 {
		return strings.TrimSpace(m[1])
	}
	if strings.HasPrefix(strings.ToLower(s), "http://") || strings.HasPrefix(strings.ToLower(s), "https://") {
		u, err := url.Parse(strings.ReplaceAll(s, "#", ""))
		if err == nil && u != nil {
			sc := strings.TrimSpace(u.Query().Get("shareCode"))
			if sc != "" {
				return sc
			}
		}
	}
	if regexp.MustCompile(`^[A-Za-z0-9]{6,64}$`).MatchString(s) {
		return s
	}
	return ""
}

func newTianyiClient() (*http.Client, error) {
	jar, err := cookiejar.New(nil)
	if err != nil {
		return nil, err
	}
	return &http.Client{
		Timeout: 18 * time.Second,
		Jar:     jar,
	}, nil
}

func httpDoText(client *http.Client, method string, urlStr string, body []byte, headers map[string]string) (status int, hdr http.Header, text string, finalURL string, err error) {
	req, err := http.NewRequest(method, urlStr, bytes.NewReader(body))
	if err != nil {
		return 0, nil, "", "", err
	}
	for k, v := range headers {
		if strings.TrimSpace(k) == "" {
			continue
		}
		req.Header.Set(k, v)
	}
	resp, err := client.Do(req)
	if err != nil {
		return 0, nil, "", "", err
	}
	defer resp.Body.Close()
	b, _ := io.ReadAll(resp.Body)
	return resp.StatusCode, resp.Header.Clone(), string(b), resp.Request.URL.String(), nil
}

func cookieHeaderFromJar(client *http.Client, urlStr string) string {
	if client == nil || client.Jar == nil {
		return ""
	}
	u, err := url.Parse(urlStr)
	if err != nil {
		return ""
	}
	cookies := client.Jar.Cookies(u)
	if len(cookies) == 0 {
		return ""
	}
	type kv struct{ k, v string }
	pairs := []kv{}
	seen := map[string]struct{}{}
	for _, c := range cookies {
		if c == nil {
			continue
		}
		k := strings.TrimSpace(c.Name)
		if k == "" {
			continue
		}
		if _, ok := seen[k]; ok {
			continue
		}
		seen[k] = struct{}{}
		pairs = append(pairs, kv{k: k, v: c.Value})
	}
	sort.Slice(pairs, func(i, j int) bool { return pairs[i].k < pairs[j].k })
	sb := strings.Builder{}
	for i, p := range pairs {
		if i > 0 {
			sb.WriteString("; ")
		}
		sb.WriteString(p.k)
		sb.WriteString("=")
		sb.WriteString(p.v)
	}
	return sb.String()
}

func wrapPublicKeyPem(rawKey string) string {
	k := strings.TrimSpace(rawKey)
	if k == "" {
		return ""
	}
	if strings.Contains(k, "BEGIN PUBLIC KEY") {
		return k
	}
	return "-----BEGIN PUBLIC KEY-----\n" + k + "\n-----END PUBLIC KEY-----"
}

func rsaEncryptToHexUpper(publicKey string, plainText string) (string, error) {
	pemText := wrapPublicKeyPem(publicKey)
	if pemText == "" {
		return "", errors.New("missing rsa public key")
	}
	block, _ := pem.Decode([]byte(pemText))
	if block == nil {
		return "", errors.New("invalid rsa public key pem")
	}
	pubAny, err := x509.ParsePKIXPublicKey(block.Bytes)
	if err != nil {
		return "", err
	}
	pub, ok := pubAny.(*rsa.PublicKey)
	if !ok || pub == nil {
		return "", errors.New("invalid rsa public key")
	}
	enc, err := rsa.EncryptPKCS1v15(rand.Reader, pub, []byte(plainText))
	if err != nil {
		return "", err
	}
	return strings.ToUpper(hex.EncodeToString(enc)), nil
}

func tianyiFetchEncryptConf(client *http.Client) (rsaKey string, pre string, err error) {
	u := tianyiAuthBase + "/api/logbox/config/encryptConf.do?appId=8025431004&timeStamp=" + url.QueryEscape(strconv.FormatInt(time.Now().UnixMilli(), 10))
	status, _, text, _, err := httpDoText(client, http.MethodGet, u, nil, map[string]string{
		"User-Agent":      tianyiUA,
		"Referer":         tianyiAuthBase,
		"Accept":          "application/json, text/plain, */*",
		"Accept-Encoding": "identity",
	})
	if err != nil {
		return "", "", err
	}
	if status < 200 || status >= 300 {
		return "", "", errors.New("tianyi encryptConf failed")
	}
	var resp tianyiEncryptConfResp
	if err := json.Unmarshal([]byte(strings.TrimSpace(text)), &resp); err != nil {
		return "", "", err
	}
	if !tianyiResultOk(resp.Result) {
		return "", "", errors.New("tianyi encryptConf failed")
	}
	return strings.TrimSpace(resp.Data.PubKey), strings.TrimSpace(resp.Data.Pre), nil
}

func parseLtReqIdFromURL(urlStr string) (lt string, reqID string, appID string) {
	u, err := url.Parse(urlStr)
	if err != nil {
		return "", "", ""
	}
	q := u.Query()
	lt = strings.TrimSpace(q.Get("lt"))
	reqID = strings.TrimSpace(q.Get("reqId"))
	appID = strings.TrimSpace(q.Get("appId"))
	return
}

func tianyiFetchLtReqID(client *http.Client) (lt string, reqID string, appID string, err error) {
	loginURL := "https://cloud.189.cn/api/portal/loginUrl.action?redirectURL=" + url.QueryEscape("https://cloud.189.cn/main.action")
	status, _, _, finalURL, err := httpDoText(client, http.MethodGet, loginURL, nil, map[string]string{
		"User-Agent":      tianyiUA,
		"Referer":         "https://cloud.189.cn/",
		"Accept-Encoding": "identity",
	})
	if err != nil {
		return "", "", "", err
	}
	if status < 200 || status >= 400 {
		return "", "", "", errors.New("tianyi loginUrl failed")
	}
	lt, reqID, appID = parseLtReqIdFromURL(finalURL)
	if strings.TrimSpace(appID) == "" {
		appID = "cloud"
	}
	return lt, reqID, appID, nil
}

func tianyiFetchAppConf(client *http.Client, lt string, reqID string, appID string) (conf tianyiAppConfResp, err error) {
	form := url.Values{}
	form.Set("version", "2.0")
	form.Set("appKey", strings.TrimSpace(appID))
	status, _, text, _, err := httpDoText(client, http.MethodPost, tianyiAuthBase+"/api/logbox/oauth2/appConf.do", []byte(form.Encode()), map[string]string{
		"User-Agent":      tianyiUA,
		"Referer":         tianyiAuthBase,
		"Content-Type":    "application/x-www-form-urlencoded",
		"Accept-Encoding": "identity",
		"Lt":              strings.TrimSpace(lt),
		"Reqid":           strings.TrimSpace(reqID),
	})
	if err != nil {
		return tianyiAppConfResp{}, err
	}
	if status < 200 || status >= 300 {
		return tianyiAppConfResp{}, errors.New("tianyi appConf http error")
	}
	if err := json.Unmarshal([]byte(strings.TrimSpace(text)), &conf); err != nil {
		return tianyiAppConfResp{}, err
	}
	if !tianyiResultOk(conf.Result) {
		if strings.TrimSpace(conf.Msg) != "" {
			return tianyiAppConfResp{}, errors.New("tianyi appConf failed: " + strings.TrimSpace(conf.Msg))
		}
		return tianyiAppConfResp{}, errors.New("tianyi appConf failed")
	}
	return conf, nil
}

func tianyiLoginWithPassword(username string, password string) (cookie string, err error) {
	user := strings.TrimSpace(username)
	pass := password
	if user == "" || strings.TrimSpace(pass) == "" {
		return "", errors.New("missing 189 username/password")
	}

	client, err := newTianyiClient()
	if err != nil {
		return "", err
	}

	rsaKey, pre, err := tianyiFetchEncryptConf(client)
	if err != nil {
		return "", err
	}
	if strings.TrimSpace(rsaKey) == "" {
		return "", errors.New("tianyi login: rsa key not found")
	}

	lt, reqID, appID, err := tianyiFetchLtReqID(client)
	if err != nil {
		return "", err
	}
	if strings.TrimSpace(lt) == "" || strings.TrimSpace(reqID) == "" {
		return "", errors.New("tianyi login: missing lt/reqId")
	}

	conf, err := tianyiFetchAppConf(client, lt, reqID, appID)
	if err != nil {
		return "", err
	}
	returnURL := strings.TrimSpace(conf.Data.ReturnURL)
	paramID := strings.TrimSpace(conf.Data.ParamID)
	if returnURL == "" || paramID == "" {
		return "", errors.New("tianyi login: missing paramId/returnUrl")
	}

	encUserHex, err := rsaEncryptToHexUpper(rsaKey, user)
	if err != nil {
		return "", err
	}
	encPassHex, err := rsaEncryptToHexUpper(rsaKey, pass)
	if err != nil {
		return "", err
	}
	prefix := strings.TrimSpace(pre)
	if prefix == "" {
		prefix = "{RSA}"
	}
	encUser := prefix + encUserHex
	encPass := prefix + encPassHex

	form := url.Values{}
	form.Set("appKey", strings.TrimSpace(appID))
	form.Set("version", "v2.0")
	form.Set("apToken", "")
	accountType := strings.TrimSpace(conf.Data.AccountType)
	if accountType == "" {
		accountType = "01"
	}
	form.Set("accountType", accountType)
	form.Set("userName", encUser)
	form.Set("password", encPass)
	form.Set("validateCode", "")
	form.Set("captchaToken", "")
	form.Set("returnUrl", returnURL)
	mailSuffix := strings.TrimSpace(conf.Data.MailSuffix)
	if mailSuffix == "" {
		mailSuffix = "@189.cn"
	}
	form.Set("mailSuffix", mailSuffix)
	form.Set("paramId", paramID)
	form.Set("dynamicCheck", "FALSE")
	clientType := conf.Data.ClientType
	form.Set("clientType", strconv.Itoa(tianyiToInt(clientType, 10020)))
	form.Set("cb_SaveName", "3")
	if tianyiToBool(conf.Data.IsOauth2, false) {
		form.Set("isOauth2", "true")
	} else {
		form.Set("isOauth2", "false")
	}
	form.Set("state", "")

	status, _, text, _, err := httpDoText(client, http.MethodPost, tianyiAuthBase+"/api/logbox/oauth2/loginSubmit.do", []byte(form.Encode()), map[string]string{
		"User-Agent":      tianyiUA,
		"Referer":         tianyiAuthBase,
		"Content-Type":    "application/x-www-form-urlencoded",
		"Accept-Encoding": "identity",
		"Lt":              strings.TrimSpace(lt),
		"Reqid":           strings.TrimSpace(reqID),
	})
	if err != nil {
		return "", err
	}
	if status < 200 || status >= 300 {
		return "", errors.New("tianyi loginSubmit failed")
	}

	var submit tianyiLoginSubmitResp
	if err := json.Unmarshal([]byte(strings.TrimSpace(text)), &submit); err != nil {
		return "", err
	}
	if !tianyiResultOk(submit.Result) {
		msg := strings.TrimSpace(submit.Msg)
		if msg == "" {
			msg = "login failed"
		}
		return "", errors.New("tianyi login failed: " + msg)
	}
	toURL := strings.TrimSpace(submit.ToURL)
	if toURL == "" {
		return "", errors.New("tianyi login failed: empty toUrl")
	}

	// Follow redirects to set final cookies.
	_, _, _, _, err = httpDoText(client, http.MethodGet, toURL, nil, map[string]string{
		"User-Agent":      tianyiUA,
		"Accept-Encoding": "identity",
	})
	if err != nil {
		return "", err
	}

	// Prefer cloud.189.cn cookies.
	c := cookieHeaderFromJar(client, "https://cloud.189.cn/")
	if strings.TrimSpace(c) == "" {
		// Fallback to open.e.189.cn cookies.
		c = cookieHeaderFromJar(client, "https://open.e.189.cn/")
	}
	if strings.TrimSpace(c) == "" {
		return "", errors.New("tianyi login failed: empty cookie")
	}
	return c, nil
}

func ensure189Cookie(database *db.DB, forceRefresh bool) (cookie string, refreshed bool, err error) {
	// MeowFilm DB stores Tianyi(189) credentials under a single key.
	// Keep it strict to avoid ambiguous state.
	const storeKey = "tianyi"

	store := readPanLoginSettings(database)
	existing := getPanField(store, storeKey, "cookie")
	if !forceRefresh && existing != "" {
		return existing, false, nil
	}
	username := getPanField(store, storeKey, "username")
	password := getPanField(store, storeKey, "password")
	if username == "" || password == "" {
		if existing != "" {
			return existing, false, nil
		}
		return "", false, errors.New("missing 189 cookie; set pan_login_settings[\"tianyi\"].cookie or username/password")
	}

	pan189LoginMu.Lock()
	defer pan189LoginMu.Unlock()

	// Re-check after lock.
	store = readPanLoginSettings(database)
	existing = getPanField(store, storeKey, "cookie")
	if !forceRefresh && existing != "" {
		return existing, false, nil
	}

	c, err := tianyiLoginWithPassword(username, password)
	if err != nil {
		return "", false, err
	}
	setPanField(store, storeKey, "cookie", c)
	_ = writePanLoginSettings(database, store)
	return c, true, nil
}

func tianyiJSONGet(urlStr string, cookie string, referer string, out any) error {
	client := &http.Client{Timeout: 18 * time.Second}
	req, err := http.NewRequest(http.MethodGet, urlStr, nil)
	if err != nil {
		return err
	}
	req.Header.Set("User-Agent", tianyiUA)
	req.Header.Set("Accept", "application/json;charset=UTF-8")
	req.Header.Set("Accept-Encoding", "identity")
	if strings.TrimSpace(referer) != "" {
		req.Header.Set("Referer", referer)
	}
	if strings.TrimSpace(cookie) != "" {
		req.Header.Set("Cookie", cookie)
	}
	resp, err := client.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	b, _ := io.ReadAll(resp.Body)
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return errors.New("tianyi http " + strconv.Itoa(resp.StatusCode) + ": " + strings.TrimSpace(string(b)))
	}
	dec := json.NewDecoder(bytes.NewReader(bytes.TrimSpace(b)))
	dec.UseNumber()
	return dec.Decode(out)
}

func tianyiResCodeOk(code any) bool {
	switch v := code.(type) {
	case float64:
		return int(v) == 0
	case int:
		return v == 0
	case int64:
		return v == 0
	case json.Number:
		i, _ := v.Int64()
		return i == 0
	case string:
		return strings.TrimSpace(v) == "0"
	default:
		return false
	}
}

func tianyiNeedAccessCodeMessage(msg string) bool {
	m := strings.ToLower(strings.TrimSpace(msg))
	return strings.Contains(m, "accesscode") ||
		strings.Contains(m, "访问码") ||
		strings.Contains(m, "提取码") ||
		strings.Contains(m, "密码")
}

func tianyiResultOk(v any) bool {
	switch x := v.(type) {
	case nil:
		return false
	case int:
		return x == 0
	case int64:
		return x == 0
	case float64:
		return int(x) == 0
	case json.Number:
		i, err := x.Int64()
		return err == nil && i == 0
	case string:
		s := strings.TrimSpace(x)
		return s == "0" || s == "00" || s == "0000"
	default:
		s := strings.TrimSpace(toString(v))
		return s == "0" || s == "00" || s == "0000"
	}
}

func tianyiToBool(v any, def bool) bool {
	switch x := v.(type) {
	case nil:
		return def
	case bool:
		return x
	case string:
		s := strings.ToLower(strings.TrimSpace(x))
		if s == "true" || s == "1" || s == "yes" {
			return true
		}
		if s == "false" || s == "0" || s == "no" {
			return false
		}
		return def
	case float64:
		return int(x) != 0
	case int:
		return x != 0
	case int64:
		return x != 0
	case json.Number:
		i, err := x.Int64()
		if err != nil {
			return def
		}
		return i != 0
	default:
		s := strings.ToLower(strings.TrimSpace(toString(v)))
		if s == "true" || s == "1" || s == "yes" {
			return true
		}
		if s == "false" || s == "0" || s == "no" {
			return false
		}
		return def
	}
}

func tianyiToInt(v any, def int) int {
	switch x := v.(type) {
	case nil:
		return def
	case int:
		return x
	case int64:
		return int(x)
	case float64:
		return int(x)
	case json.Number:
		i, err := x.Int64()
		if err != nil {
			return def
		}
		return int(i)
	case string:
		i, err := strconv.Atoi(strings.TrimSpace(x))
		if err != nil {
			return def
		}
		return i
	default:
		i, err := strconv.Atoi(strings.TrimSpace(toString(v)))
		if err != nil {
			return def
		}
		return i
	}
}

func tianyiResCodeString(code any) string {
	switch v := code.(type) {
	case nil:
		return ""
	case string:
		return strings.TrimSpace(v)
	case json.Number:
		return strings.TrimSpace(v.String())
	default:
		return strings.TrimSpace(toString(v))
	}
}

func tianyiPickFirstString(m map[string]any, key string) string {
	if m == nil {
		return ""
	}
	v, ok := m[key]
	if !ok {
		return ""
	}
	s, ok := v.(string)
	if !ok {
		return ""
	}
	return strings.TrimSpace(s)
}

func tianyiSessionExpiredMessage(msg string) bool {
	m := strings.ToLower(strings.TrimSpace(msg))
	if m == "" {
		return false
	}
	return strings.Contains(m, "invalidsessionkey") ||
		strings.Contains(m, "usersessionbo is null") ||
		strings.Contains(m, "session expired") ||
		strings.Contains(m, "session失效") ||
		strings.Contains(m, "会话失效")
}

func tianyiIsSessionExpiredError(err error) bool {
	if err == nil {
		return false
	}
	return tianyiSessionExpiredMessage(err.Error())
}

func tianyiGetShareInfoByCode(shareCode string, accessCode string, cookie string) (tianyiShareInfoResp, error) {
	u, _ := url.Parse(tianyiAPIBase + "/api/open/share/getShareInfoByCodeV2.action")
	q := u.Query()
	q.Set("key", "noCache")
	q.Set("shareCode", strings.TrimSpace(shareCode))
	if strings.TrimSpace(accessCode) != "" {
		q.Set("accessCode", strings.TrimSpace(accessCode))
	}
	u.RawQuery = q.Encode()

	var resp tianyiShareInfoResp
	if err := tianyiJSONGet(u.String(), cookie, "https://cloud.189.cn/t/"+strings.TrimSpace(shareCode), &resp); err != nil {
		return tianyiShareInfoResp{}, err
	}
	if !tianyiResCodeOk(resp.ResCode) {
		msg := strings.TrimSpace(resp.ResMessage)
		if msg == "" {
			msg = "share info failed"
		}
		if tianyiNeedAccessCodeMessage(msg) {
			return tianyiShareInfoResp{}, errors.New("need accessCode: " + msg)
		}
		return tianyiShareInfoResp{}, errors.New(msg)
	}
	return resp, nil
}

func tianyiListShareDir(shareID string, fileID string, shareMode string, pageNum int, pageSize int, accessCode string, cookie string) (tianyiListShareDirResp, error) {
	u, _ := url.Parse(tianyiAPIBase + "/api/open/share/listShareDir.action")
	q := u.Query()
	q.Set("key", "noCache")
	q.Set("noCache", strconv.FormatFloat(randFloat(), 'f', -1, 64))
	q.Set("shareId", strings.TrimSpace(shareID))
	fid := strings.TrimSpace(fileID)
	if fid == "" {
		fid = "0"
	}
	q.Set("fileId", fid)
	q.Set("shareDirFileId", fid)
	q.Set("isFolder", "true")
	q.Set("iconOption", "5")
	sm := strings.TrimSpace(shareMode)
	if sm == "" {
		sm = "3"
	}
	q.Set("shareMode", sm)
	if pageNum <= 0 {
		pageNum = 1
	}
	if pageSize <= 0 {
		pageSize = 200
	}
	q.Set("pageNum", strconv.Itoa(pageNum))
	q.Set("pageSize", strconv.Itoa(pageSize))
	q.Set("orderBy", "lastOpTime")
	q.Set("descending", "true")
	if strings.TrimSpace(accessCode) != "" {
		q.Set("accessCode", strings.TrimSpace(accessCode))
	}
	u.RawQuery = q.Encode()

	var resp tianyiListShareDirResp
	if err := tianyiJSONGet(u.String(), cookie, "https://cloud.189.cn/", &resp); err != nil {
		return tianyiListShareDirResp{}, err
	}
	if !tianyiResCodeOk(resp.ResCode) {
		msg := strings.TrimSpace(resp.ResMessage)
		if msg == "" {
			msg = "listShareDir failed"
		}
		if tianyiNeedAccessCodeMessage(msg) {
			return tianyiListShareDirResp{}, errors.New("need accessCode: " + msg)
		}
		return tianyiListShareDirResp{}, errors.New(msg)
	}
	return resp, nil
}

func tianyiGetFileDownloadURL(shareID string, fileID string, dt string, accessCode string, cookie string) (tianyiGetDownloadURLResp, error) {
	u, _ := url.Parse(tianyiAPIBase + "/api/open/file/getFileDownloadUrl.action")
	q := u.Query()
	q.Set("shareId", strings.TrimSpace(shareID))
	q.Set("fileId", strings.TrimSpace(fileID))
	if strings.TrimSpace(dt) == "" {
		dt = "1"
	}
	q.Set("dt", strings.TrimSpace(dt))
	if strings.TrimSpace(accessCode) != "" {
		q.Set("accessCode", strings.TrimSpace(accessCode))
	}
	u.RawQuery = q.Encode()

	var root map[string]any
	if err := tianyiJSONGet(u.String(), cookie, "https://cloud.189.cn/", &root); err != nil {
		return tianyiGetDownloadURLResp{}, err
	}
	code := root["res_code"]
	if code == nil {
		code = root["resCode"]
	}
	msg := strings.TrimSpace(toString(root["res_message"]))
	if msg == "" {
		msg = strings.TrimSpace(toString(root["resMessage"]))
	}
	if !tianyiResCodeOk(code) {
		if msg == "" {
			msg = "getFileDownloadUrl failed"
		}
		if tianyiNeedAccessCodeMessage(msg) {
			return tianyiGetDownloadURLResp{}, errors.New("need accessCode: " + msg)
		}
		return tianyiGetDownloadURLResp{}, errors.New(msg)
	}
	resp := tianyiGetDownloadURLResp{ResCode: code, ResMessage: msg, Data: root}
	return resp, nil
}

func pickTianyiDownloadURL(resp tianyiGetDownloadURLResp) string {
	root, _ := resp.Data.(map[string]any)
	if root == nil {
		return ""
	}
	dataAny, ok := root["data"]
	if !ok {
		dataAny = root["Data"]
	}
	dataObj, _ := dataAny.(map[string]any)
	target := root
	if dataObj != nil {
		target = dataObj
	}
	if s := tianyiPickFirstString(target, "fileDownloadUrl"); s != "" {
		return s
	}
	if s := tianyiPickFirstString(target, "downloadUrl"); s != "" {
		return s
	}
	if s := tianyiPickFirstString(target, "url"); s != "" {
		return s
	}
	if s := tianyiPickFirstString(target, "fileDownloadUrlHttp"); s != "" {
		return s
	}
	if s := tianyiPickFirstString(target, "fileDownloadUrlHttps"); s != "" {
		return s
	}
	return ""
}

func resolveSingleRedirectLocation(urlStr string) (string, error) {
	client := &http.Client{
		Timeout: 18 * time.Second,
		CheckRedirect: func(req *http.Request, via []*http.Request) error {
			return http.ErrUseLastResponse
		},
	}
	req, err := http.NewRequest(http.MethodGet, urlStr, nil)
	if err != nil {
		return "", err
	}
	req.Header.Set("User-Agent", tianyiUA)
	req.Header.Set("Range", "bytes=0-0")
	resp, err := client.Do(req)
	if err != nil {
		return "", err
	}
	defer resp.Body.Close()
	if resp.StatusCode >= 300 && resp.StatusCode < 400 {
		loc := strings.TrimSpace(resp.Header.Get("Location"))
		if loc == "" {
			return urlStr, nil
		}
		u, err := url.Parse(urlStr)
		if err == nil {
			if next, err2 := u.Parse(loc); err2 == nil {
				return next.String(), nil
			}
		}
		return loc, nil
	}
	return urlStr, nil
}

func resolveFinalRedirectURL(urlStr string, maxRedirects int) (string, error) {
	cur := strings.TrimSpace(urlStr)
	if cur == "" {
		return "", errors.New("empty url")
	}
	limit := maxRedirects
	if limit <= 0 {
		limit = 8
	}
	if limit > 20 {
		limit = 20
	}
	client := &http.Client{
		Timeout: 18 * time.Second,
		CheckRedirect: func(req *http.Request, via []*http.Request) error {
			return http.ErrUseLastResponse
		},
	}
	for i := 0; i <= limit; i++ {
		req, err := http.NewRequest(http.MethodGet, cur, nil)
		if err != nil {
			return "", err
		}
		req.Header.Set("User-Agent", tianyiUA)
		req.Header.Set("Accept-Encoding", "identity")
		req.Header.Set("Range", "bytes=0-0")
		resp, err := client.Do(req)
		if err != nil {
			return "", err
		}
		_ = resp.Body.Close()
		if resp.StatusCode >= 300 && resp.StatusCode < 400 {
			loc := strings.TrimSpace(resp.Header.Get("Location"))
			if loc == "" {
				return cur, nil
			}
			u, err := url.Parse(cur)
			if err == nil && u != nil {
				if next, err2 := u.Parse(loc); err2 == nil && next != nil {
					cur = next.String()
					continue
				}
			}
			cur = loc
			continue
		}
		return cur, nil
	}
	return cur, nil
}

func parse189PlayID(id string) (fileID string, shareID string, fileName string) {
	raw := strings.TrimSpace(id)
	if raw == "" {
		return "", "", ""
	}
	parts := strings.Split(raw, "*")
	if len(parts) < 2 {
		return "", "", ""
	}
	fileID = strings.TrimSpace(parts[0])
	shareID = strings.TrimSpace(parts[1])
	if len(parts) >= 3 {
		fileName = strings.TrimSpace(strings.Join(parts[2:], "*"))
	}
	return
}

func Tianyi189List(database *db.DB, flag string, accessCode string) (vodPlayURL string, shareID string, shareCode string, err error) {
	sc := parse189ShareCodeLike(flag)
	if sc == "" {
		return "", "", "", errors.New("missing/invalid flag/shareCode (expected: 天翼-<shareCode> or https://cloud.189.cn/t/<shareCode>)")
	}
	cookie, _, err := ensure189Cookie(database, false)
	if err != nil {
		return "", "", "", err
	}
	info, err := tianyiGetShareInfoByCode(sc, accessCode, cookie)
	if err != nil {
		if !tianyiIsSessionExpiredError(err) {
			return "", "", "", err
		}
		cookie2, _, err2 := ensure189Cookie(database, true)
		if err2 != nil || strings.TrimSpace(cookie2) == "" {
			return "", "", "", err
		}
		info2, err3 := tianyiGetShareInfoByCode(sc, accessCode, cookie2)
		if err3 != nil {
			return "", "", "", err
		}
		info = info2
		cookie = cookie2
	}
	shareID = strings.TrimSpace(toString(info.ShareId))
	rootFileID := strings.TrimSpace(toString(info.FileId))
	sm := strings.TrimSpace(toString(info.ShareMode))
	if shareID == "" {
		return "", "", "", errors.New("tianyi share info missing shareId")
	}
	if rootFileID == "" {
		rootFileID = "0"
	}

	const maxItems = 20000
	const pageSize = 200

		isFolderValue := func(v any) bool {
			switch x := v.(type) {
			case bool:
				return x
			case string:
				s := strings.TrimSpace(x)
				return s == "true" || s == "1"
			case float64:
				return int(x) == 1
			case json.Number:
				i, _ := x.Int64()
				return int(i) == 1
			default:
				return false
			}
		}

		type dirItem struct {
			fileID string
			path   string
		}
		ensureLeadingSlash := func(p string) string {
			p = strings.TrimSpace(strings.ReplaceAll(p, "\\", "/"))
			p = strings.ReplaceAll(p, "//", "/")
			if p == "" {
				return "/"
			}
			if strings.HasPrefix(p, "/") {
				return p
			}
			return "/" + p
		}
		joinPath := func(a, b string) string {
			aa := ensureLeadingSlash(a)
			bb := strings.TrimSpace(b)
			if bb == "" {
				return aa
			}
			if aa == "/" {
				return "/" + bb
			}
			return strings.TrimRight(aa, "/") + "/" + bb
		}

		// Root listing: determine effective root and handle single-file shares.
		rootFolders := []dirItem{}
		rootFiles := 0
		rootTotal := 0
		for page := 1; page <= 500; page++ {
			list, err := tianyiListShareDir(shareID, rootFileID, sm, page, pageSize, accessCode, cookie)
			if err != nil {
				if !tianyiIsSessionExpiredError(err) {
					return "", "", "", err
				}
				cookie2, _, err2 := ensure189Cookie(database, true)
				if err2 != nil || strings.TrimSpace(cookie2) == "" {
					return "", "", "", err
				}
				list2, err3 := tianyiListShareDir(shareID, rootFileID, sm, page, pageSize, accessCode, cookie2)
				if err3 != nil {
					return "", "", "", err
				}
				list = list2
				cookie = cookie2
			}
			itemsFile := list.FileListAO.FileList
			itemsFolder := list.FileListAO.FolderList
			if len(itemsFile) == 0 && len(itemsFolder) == 0 {
				break
			}
			if list.FileListAO.Count > 0 && list.FileListAO.Count > rootTotal {
				rootTotal = list.FileListAO.Count
			}
			for _, it := range itemsFolder {
				name := strings.TrimSpace(it.Name)
				idStr := strings.TrimSpace(toString(it.ID))
				if idStr == "" || name == "" {
					continue
				}
				rootFolders = append(rootFolders, dirItem{fileID: idStr, path: joinPath("/", name)})
			}
			for _, it := range itemsFile {
				idStr := strings.TrimSpace(toString(it.ID))
				name := strings.TrimSpace(it.Name)
				if idStr == "" || name == "" {
					continue
				}
				// fileList may include folders too; handle by isFolderValue.
				if isFolderValue(it.IsFolder) {
					rootFolders = append(rootFolders, dirItem{fileID: idStr, path: joinPath("/", name)})
					continue
				}
				rootFiles += 1
			}
			if len(itemsFile)+len(itemsFolder) < pageSize {
				break
			}
			if list.FileListAO.Count > 0 && page*pageSize >= list.FileListAO.Count {
				break
			}
		}
		if rootFiles == 0 && len(rootFolders) == 0 {
			// Single-file share (no listable dir content); fall back to shareInfo fields.
			fileName := strings.TrimSpace(info.FileName)
			if fileName == "" {
				fileName = "file"
			}
			id := rootFileID + "*" + shareID + "*" + fileName
			return "/$" + id, shareID, sc, nil
		}
		effectiveRootID := rootFileID
		if rootFiles == 0 && len(rootFolders) == 1 && rootTotal > 0 && rootTotal <= len(rootFolders) {
			effectiveRootID = rootFolders[0].fileID
		}

		queue := []dirItem{{fileID: effectiveRootID, path: "/"}}
		seenDir := map[string]struct{}{effectiveRootID: {}}

		parts := []string{}
		for len(queue) > 0 {
			cur := queue[0]
			queue = queue[1:]

			for page := 1; page <= 500; page++ {
				list, err := tianyiListShareDir(shareID, cur.fileID, sm, page, pageSize, accessCode, cookie)
				if err != nil {
					if !tianyiIsSessionExpiredError(err) {
						return "", "", "", err
					}
					cookie2, _, err2 := ensure189Cookie(database, true)
					if err2 != nil || strings.TrimSpace(cookie2) == "" {
						return "", "", "", err
					}
					list2, err3 := tianyiListShareDir(shareID, cur.fileID, sm, page, pageSize, accessCode, cookie2)
					if err3 != nil {
						return "", "", "", err
					}
					list = list2
					cookie = cookie2
				}
				files1 := list.FileListAO.FileList
				folders1 := list.FileListAO.FolderList
				if len(files1) == 0 && len(folders1) == 0 {
					break
				}
				// folderList is authoritative folders.
				for _, it := range folders1 {
					name := strings.TrimSpace(it.Name)
					idStr := strings.TrimSpace(toString(it.ID))
					if idStr == "" || name == "" {
						continue
					}
					if _, ok := seenDir[idStr]; ok {
						continue
					}
					seenDir[idStr] = struct{}{}
					queue = append(queue, dirItem{fileID: idStr, path: joinPath(cur.path, name)})
				}
				for _, it := range files1 {
					name := strings.TrimSpace(it.Name)
					idStr := strings.TrimSpace(toString(it.ID))
					if idStr == "" || name == "" {
						continue
					}
					if isFolderValue(it.IsFolder) {
						if _, ok := seenDir[idStr]; ok {
							continue
						}
						seenDir[idStr] = struct{}{}
						queue = append(queue, dirItem{fileID: idStr, path: joinPath(cur.path, name)})
						continue
					}
					id := idStr + "*" + shareID + "*" + name
					display := ensureLeadingSlash(cur.path)
					parts = append(parts, display+"$"+id)
					if len(parts) >= maxItems {
						return strings.Join(parts, "#"), shareID, sc, errors.New("tianyi share too large (exceeded max items)")
					}
				}
				// Stop paging when the page is not full, or when count is reached.
				if len(files1)+len(folders1) < pageSize {
					break
				}
				if list.FileListAO.Count > 0 && page*pageSize >= list.FileListAO.Count {
					break
				}
			}
		}
		return strings.Join(parts, "#"), shareID, sc, nil
	}

func tianyi189PlayWithDT(database *db.DB, id string, accessCode string, dt string) (finalURL string, shareID string, fileID string, fileName string, err error) {
	fileID, shareID, fileName = parse189PlayID(id)
	if fileID == "" || shareID == "" {
		return "", "", "", "", errors.New("missing/invalid id (expected: <fileId>*<shareId>*<name?>)")
	}
	cookie, _, err := ensure189Cookie(database, false)
	if err != nil {
		return "", "", "", "", err
	}
	out, err := tianyiGetFileDownloadURL(shareID, fileID, dt, accessCode, cookie)
	if err != nil {
		if !tianyiIsSessionExpiredError(err) {
			return "", "", "", "", err
		}
		cookie2, _, err2 := ensure189Cookie(database, true)
		if err2 != nil || strings.TrimSpace(cookie2) == "" {
			return "", "", "", "", err
		}
		out2, err3 := tianyiGetFileDownloadURL(shareID, fileID, dt, accessCode, cookie2)
		if err3 != nil {
			return "", "", "", "", err
		}
		out = out2
	}
	u := pickTianyiDownloadURL(out)
	if strings.TrimSpace(u) == "" {
		raw, _ := json.Marshal(out.Data)
		s := strings.TrimSpace(string(raw))
		if len(s) > 800 {
			s = s[:800]
		}
		if s == "" || s == "null" {
			return "", "", "", "", errors.New("tianyi download url not found (empty data)")
		}
		return "", "", "", "", errors.New("tianyi download url not found: data=" + s)
	}
	finalURL, err = resolveFinalRedirectURL(u, 8)
	if err != nil {
		return "", "", "", "", err
	}
	return finalURL, shareID, fileID, fileName, nil
}

func Tianyi189Play(database *db.DB, id string, accessCode string) (finalURL string, shareID string, fileID string, fileName string, err error) {
	return tianyi189PlayWithDT(database, id, accessCode, "")
}

func HandleAPI189List(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	var body struct {
		Flag       string `json:"flag"`
		ShareCode  string `json:"shareCode"`
		AccessCode string `json:"accessCode"`
	}
	_ = readJSONLoose(r, &body)
	flag := strings.TrimSpace(body.Flag)
	shareCode := strings.TrimSpace(body.ShareCode)
	accessCode := strings.TrimSpace(body.AccessCode)
	if shareCode != "" {
		flag = shareCode
	}
	vod, _, _, err := Tianyi189List(database, flag, accessCode)
	if err != nil {
		if tianyiNeedAccessCodeMessage(err.Error()) {
			writeJSON(w, http.StatusUnauthorized, map[string]any{"ok": false, "needAccessCode": true, "message": "tianyi share requires accessCode"})
			return
		}
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": err.Error()})
		return
	}
	writeJSON(w, 200, map[string]any{"ok": true, "vod_play_url": vod})
}

func HandleAPI189Play(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	var body struct {
		Flag       string `json:"flag"`
		ID         string `json:"id"`
		DT         string `json:"dt"`
		AccessCode string `json:"accessCode"`
		Passcode   string `json:"passcode"`
		Pwd        string `json:"pwd"`
		Password   string `json:"password"`
	}
	_ = readJSONLoose(r, &body)
	id := strings.TrimSpace(body.ID)
	if id == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": "missing id"})
		return
	}
	accessCode := strings.TrimSpace(body.AccessCode)
	if accessCode == "" {
		accessCode = strings.TrimSpace(body.Passcode)
	}
	if accessCode == "" {
		accessCode = strings.TrimSpace(body.Pwd)
	}
	if accessCode == "" {
		accessCode = strings.TrimSpace(body.Password)
	}
	dt := strings.TrimSpace(body.DT)
	u, shareID, fileID, fileName, err := tianyi189PlayWithDT(database, id, accessCode, dt)
	if err != nil {
		if tianyiNeedAccessCodeMessage(err.Error()) {
			writeJSON(w, http.StatusUnauthorized, map[string]any{"ok": false, "needAccessCode": true, "message": "tianyi share requires accessCode"})
			return
		}
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": err.Error()})
		return
	}
	_, _, _ = shareID, fileID, fileName
	writeJSON(w, 200, map[string]any{"ok": true, "url": u})
}

func randFloat() float64 {
	// good enough for noCache
	n, _ := rand.Int(rand.Reader, bigIntFromInt64(1<<53))
	return float64(n.Int64()) / float64(1<<53)
}

func bigIntFromInt64(v int64) *big.Int {
	return big.NewInt(v)
}
