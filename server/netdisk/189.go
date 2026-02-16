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
	re189Flag = regexp.MustCompile(`^天意-([A-Za-z0-9]{6,64})$`)

	pan189LoginMu sync.Mutex
)

type tianyiEncryptConfResp struct {
	Result int `json:"result"`
	Data   struct {
		PubKey string `json:"pubKey"`
		Pre    string `json:"pre"`
	} `json:"data"`
}

type tianyiAppConfResp struct {
	Result int    `json:"result"`
	Msg    string `json:"msg"`
	Data   struct {
		AccountType string `json:"accountType"`
		MailSuffix  string `json:"mailSuffix"`
		ClientType  int    `json:"clientType"`
		IsOauth2    bool   `json:"isOauth2"`
		ReturnURL   string `json:"returnUrl"`
		ParamID     string `json:"paramId"`
	} `json:"data"`
}

type tianyiLoginSubmitResp struct {
	Result int    `json:"result"`
	Msg    string `json:"msg"`
	ToURL  string `json:"toUrl"`
}

type tianyiShareInfoResp struct {
	ResCode    any    `json:"res_code"`
	ResMessage string `json:"res_message"`
	NeedAccess any    `json:"needAccessCode"`
	ShareId    any    `json:"shareId"`
	FileId     any    `json:"fileId"`
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
	} `json:"fileListAO"`
}

type tianyiGetDownloadURLResp struct {
	ResCode    any    `json:"res_code"`
	ResMessage string `json:"res_message"`
	Data       struct {
		FileDownloadURL string `json:"fileDownloadUrl"`
		DownloadURL     string `json:"downloadUrl"`
		URL             string `json:"url"`
	} `json:"data"`
}

func parse189ShareCodeFromFlag(flag string) string {
	s := strings.TrimSpace(flag)
	m := re189Flag.FindStringSubmatch(s)
	if len(m) == 2 {
		return strings.TrimSpace(m[1])
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
	if resp.Result != 0 {
		return "", "", errors.New("tianyi encryptConf result != 0")
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
	if conf.Result != 0 {
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
	if clientType == 0 {
		clientType = 10020
	}
	form.Set("clientType", strconv.Itoa(clientType))
	form.Set("cb_SaveName", "3")
	if conf.Data.IsOauth2 {
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
	if submit.Result != 0 {
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
	store := readPanLoginSettings(database)
	existing := getPanField(store, "189", "cookie")
	if !forceRefresh && existing != "" {
		return existing, false, nil
	}
	username := getPanField(store, "189", "username")
	password := getPanField(store, "189", "password")
	if username == "" || password == "" {
		if existing != "" {
			return existing, false, nil
		}
		return "", false, errors.New("missing 189 cookie; set pan_login_settings[\"189\"].cookie or username/password")
	}

	pan189LoginMu.Lock()
	defer pan189LoginMu.Unlock()

	// Re-check after lock.
	store = readPanLoginSettings(database)
	existing = getPanField(store, "189", "cookie")
	if !forceRefresh && existing != "" {
		return existing, false, nil
	}

	c, err := tianyiLoginWithPassword(username, password)
	if err != nil {
		return "", false, err
	}
	setPanField(store, "189", "cookie", c)
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
	return json.Unmarshal(bytes.TrimSpace(b), out)
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
	return strings.Contains(m, "accesscode") || strings.Contains(m, "访问码") || strings.Contains(m, "提取码") || strings.Contains(m, "密码")
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
		dt = "71"
	}
	q.Set("dt", strings.TrimSpace(dt))
	if strings.TrimSpace(accessCode) != "" {
		q.Set("accessCode", strings.TrimSpace(accessCode))
	}
	u.RawQuery = q.Encode()
	var resp tianyiGetDownloadURLResp
	if err := tianyiJSONGet(u.String(), cookie, "https://cloud.189.cn/", &resp); err != nil {
		return tianyiGetDownloadURLResp{}, err
	}
	if !tianyiResCodeOk(resp.ResCode) {
		msg := strings.TrimSpace(resp.ResMessage)
		if msg == "" {
			msg = "getFileDownloadUrl failed"
		}
		return tianyiGetDownloadURLResp{}, errors.New(msg)
	}
	return resp, nil
}

func pickTianyiDownloadURL(resp tianyiGetDownloadURLResp) string {
	if strings.TrimSpace(resp.Data.FileDownloadURL) != "" {
		return strings.TrimSpace(resp.Data.FileDownloadURL)
	}
	if strings.TrimSpace(resp.Data.DownloadURL) != "" {
		return strings.TrimSpace(resp.Data.DownloadURL)
	}
	return strings.TrimSpace(resp.Data.URL)
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
	sc := parse189ShareCodeFromFlag(flag)
	if sc == "" {
		return "", "", "", errors.New("missing/invalid flag (expected: 天意-<shareCode>)")
	}
	cookie, _, err := ensure189Cookie(database, false)
	if err != nil {
		return "", "", "", err
	}
	info, err := tianyiGetShareInfoByCode(sc, accessCode, cookie)
	if err != nil {
		return "", "", "", err
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
	list, err := tianyiListShareDir(shareID, rootFileID, sm, 1, 200, accessCode, cookie)
	if err != nil {
		return "", "", "", err
	}
	parts := []string{}
	for _, it := range list.FileListAO.FileList {
		name := strings.TrimSpace(it.Name)
		if name == "" {
			continue
		}
		idStr := strings.TrimSpace(toString(it.ID))
		if idStr == "" {
			continue
		}
		isFolder := false
		switch v := it.IsFolder.(type) {
		case bool:
			isFolder = v
		case string:
			isFolder = strings.TrimSpace(v) == "true" || strings.TrimSpace(v) == "1"
		case float64:
			isFolder = int(v) == 1
		}
		if isFolder {
			continue
		}
		id := idStr + "*" + shareID + "*" + name
		parts = append(parts, name+"$"+id)
	}
	return strings.Join(parts, "#"), shareID, sc, nil
}

func Tianyi189Play(database *db.DB, id string, accessCode string) (finalURL string, shareID string, fileID string, fileName string, err error) {
	fileID, shareID, fileName = parse189PlayID(id)
	if fileID == "" || shareID == "" {
		return "", "", "", "", errors.New("missing/invalid id (expected: <fileId>*<shareId>*<name?>)")
	}
	cookie, _, err := ensure189Cookie(database, false)
	if err != nil {
		return "", "", "", "", err
	}
	out, err := tianyiGetFileDownloadURL(shareID, fileID, "71", accessCode, cookie)
	if err != nil {
		return "", "", "", "", err
	}
	u := pickTianyiDownloadURL(out)
	if strings.TrimSpace(u) == "" {
		return "", "", "", "", errors.New("tianyi download url not found")
	}
	finalURL, err = resolveSingleRedirectLocation(u)
	if err != nil {
		return "", "", "", "", err
	}
	return finalURL, shareID, fileID, fileName, nil
}

func HandleAPI189List(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	var body struct {
		Flag       string `json:"flag"`
		AccessCode string `json:"accessCode"`
		Passcode   string `json:"passcode"`
		Pwd        string `json:"pwd"`
		Password   string `json:"password"`
	}
	_ = readJSONLoose(r, &body)
	flag := strings.TrimSpace(body.Flag)
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
	vod, shareID, shareCode, err := Tianyi189List(database, flag, accessCode)
	if err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": err.Error()})
		return
	}
	writeJSON(w, 200, map[string]any{"ok": true, "flag": flag, "shareCode": shareCode, "shareId": shareID, "vod_play_url": vod})
}

func HandleAPI189Play(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	var body struct {
		ID         string `json:"id"`
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
	u, shareID, fileID, fileName, err := Tianyi189Play(database, id, accessCode)
	if err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": err.Error()})
		return
	}
	writeJSON(w, 200, map[string]any{
		"ok":       true,
		"parse":    0,
		"url":      u,
		"shareId":  shareID,
		"fileId":   fileID,
		"fileName": fileName,
	})
}

func randFloat() float64 {
	// good enough for noCache
	n, _ := rand.Int(rand.Reader, bigIntFromInt64(1<<53))
	return float64(n.Int64()) / float64(1<<53)
}

func bigIntFromInt64(v int64) *big.Int {
	return big.NewInt(v)
}
