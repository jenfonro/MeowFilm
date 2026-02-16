package netdisk

import (
	"bytes"
	"crypto/md5"
	"crypto/sha256"
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

// --- Share list/play (direct cloud-drive API) ---

const (
	ucShareAPIBase      = "https://pc-api.uc.cn/1/clouddrive"
	ucShareReferer      = "https://drive.uc.cn"
	ucShareUA           = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) uc-cloud-drive/2.5.20 Chrome/100.0.4896.160 Electron/18.3.5.4-b478491100 Safari/537.36 Channel/pckk_other_ch"
	ucShareAPIQueryBase = ucShareAPIBase + "?pr=UCBrowser&fr=pc"
)

// UCTV (open-api-drive): ported from CatPawOpen `panUc.js`.
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
	return ""
}

func buildUCShareHeaders(cookie string) http.Header {
	h := http.Header{}
	h.Set("User-Agent", ucShareUA)
	h.Set("Referer", ucShareReferer)
	h.Set("Accept", "application/json, text/plain, */*")
	h.Set("Content-Type", "application/json")
	h.Set("Accept-Encoding", "gzip, deflate, br")
	if strings.TrimSpace(cookie) != "" {
		h.Set("Cookie", strings.TrimSpace(cookie))
	}
	return h
}

func ucShareDoJSON(method string, urlStr string, headers http.Header, body []byte, out any) error {
	client := &http.Client{Timeout: 18 * time.Second}
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
	b, _ := io.ReadAll(resp.Body)
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return errors.New("uc http " + strconv.Itoa(resp.StatusCode) + ": " + strings.TrimSpace(string(b)))
	}
	return json.Unmarshal(bytes.TrimSpace(b), out)
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

type ucShareDetailResp struct {
	Code    int    `json:"code"`
	Message string `json:"message"`
	Data    struct {
		List []map[string]any `json:"list"`
	} `json:"data"`
}

func ucShareDetail(shareID string, stoken string, cookie string) (ucShareDetailResp, error) {
	pwdID := strings.TrimSpace(shareID)
	sToken := strings.TrimSpace(stoken)
	if pwdID == "" || sToken == "" {
		return ucShareDetailResp{}, errors.New("missing uc share parameters")
	}
	u, _ := url.Parse(ucShareAPIBase + "/share/sharepage/detail?pr=UCBrowser&fr=pc")
	q := u.Query()
	q.Set("pwd_id", pwdID)
	q.Set("stoken", sToken)
	q.Set("pdir_fid", "0")
	q.Set("force", "0")
	q.Set("_page", "1")
	q.Set("_size", "200")
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

func UCList(database *db.DB, flag string, passcode string) (string, string, error) {
	shareID := parseUCShareIDFromFlag(flag)
	if shareID == "" {
		return "", "", errors.New("missing/invalid flag (expected: 优夕-<shareId>)")
	}
	store := readPanLoginSettings(database)
	cookie := getPanField(store, "uc", "cookie")
	if cookie == "" {
		return "", "", errors.New("missing uc cookie (pan_login_settings[\"uc\"].cookie)")
	}
	stoken, err := ucShareGetStoken(shareID, passcode, cookie)
	if err != nil {
		return "", shareID, err
	}
	detail, err := ucShareDetail(shareID, stoken, cookie)
	if err != nil {
		return "", shareID, err
	}
	parts := []string{}
	for _, it := range detail.Data.List {
		if ucShareIsDirItem(it) {
			continue
		}
		fid := ucShareItemFid(it)
		fidToken := ucShareItemFidToken(it)
		name := ucShareItemName(it)
		if fid == "" || fidToken == "" || name == "" {
			continue
		}
		id := shareID + "*" + stoken + "*" + fid + "*" + fidToken + "***" + name
		parts = append(parts, name+"$"+id)
	}
	return strings.Join(parts, "#"), shareID, nil
}

type ucDownloadResp struct {
	Data any `json:"data"`
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
	var out string
	if m, ok := resp.Data.(map[string]any); ok {
		if inner, ok := m["data"]; ok {
			switch x := inner.(type) {
			case []any:
				for _, it := range x {
					im, _ := it.(map[string]any)
					if im == nil {
						continue
					}
					if v, ok := im[wantMode].(string); ok && strings.TrimSpace(v) != "" {
						out = strings.TrimSpace(v)
						break
					}
					if v, ok := im["download_url"].(string); ok && strings.TrimSpace(v) != "" {
						out = strings.TrimSpace(v)
						break
					}
					if v, ok := im["play_url"].(string); ok && strings.TrimSpace(v) != "" {
						out = strings.TrimSpace(v)
						break
					}
				}
			case map[string]any:
				if v, ok := x[wantMode].(string); ok && strings.TrimSpace(v) != "" {
					out = strings.TrimSpace(v)
				} else if v, ok := x["download_url"].(string); ok && strings.TrimSpace(v) != "" {
					out = strings.TrimSpace(v)
				} else if v, ok := x["play_url"].(string); ok && strings.TrimSpace(v) != "" {
					out = strings.TrimSpace(v)
				}
			}
		}
	}
	if out == "" {
		return "", errors.New("direct download url not found")
	}
	return out, nil
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
	client := &http.Client{Timeout: 12 * time.Second}
	req, err := http.NewRequest(http.MethodPost, u, bytes.NewReader(b))
	if err != nil {
		return "", "", 0, err
	}
	req.Header.Set("Content-Type", "application/json")
	resp, err := client.Do(req)
	if err != nil {
		return "", "", 0, err
	}
	defer resp.Body.Close()
	body, _ := io.ReadAll(resp.Body)
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return "", "", 0, errors.New("uc_tv http " + strconv.Itoa(resp.StatusCode) + ": " + strings.TrimSpace(string(body)))
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
	return resp.Status == -1 && resp.Errno == 10001
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

	client := &http.Client{Timeout: 18 * time.Second}
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
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return "", ucTVFileResp{}, errors.New("uc_tv http " + strconv.Itoa(resp.StatusCode) + ": " + strings.TrimSpace(string(body)))
	}
	var out ucTVFileResp
	if err := json.Unmarshal(bytes.TrimSpace(body), &out); err != nil {
		return "", ucTVFileResp{}, err
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
	rt := getPanField(store, "uc", "refresh_token")
	dev := getPanField(store, "uc", "device_id")
	at := getPanField(store, "uc", "access_token")
	expAtRaw := getPanField(store, "uc", "access_token_exp_at")
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
		return "", "", errors.New("missing uc_tv credentials (pan_login_settings[\"uc\"].refresh_token + device_id)")
	}

	ucTVTokenMu.Lock()
	defer ucTVTokenMu.Unlock()

	store = readPanLoginSettings(database)
	at = getPanField(store, "uc", "access_token")
	expAtRaw = getPanField(store, "uc", "access_token_exp_at")
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
	setPanField(store, "uc", "access_token", newAT)
	if newExpAt > 0 {
		setPanField(store, "uc", "access_token_exp_at", strconv.FormatInt(newExpAt, 10))
	} else {
		setPanField(store, "uc", "access_token_exp_at", "")
	}
	if newRT != "" && newRT != rt {
		setPanField(store, "uc", "refresh_token", newRT)
	}
	_ = writePanLoginSettings(database, store)
	return newAT, dev, nil
}

func UCPlay(database *db.DB, id string, want string) (string, map[string]string, error) {
	_, _, fid, fidToken, _ := parseQuarkPlayID(id)
	if fid == "" {
		return "", nil, errors.New("invalid id (missing fid)")
	}
	store := readPanLoginSettings(database)
	// Prefer UCTV link when credentials exist (no cookie headers needed).
	if getPanField(store, "uc", "refresh_token") != "" && getPanField(store, "uc", "device_id") != "" {
		at, dev, err := ensureUCTVAccessToken(database)
		if err == nil && at != "" && dev != "" {
			u, resp, err2 := ucTVLinkByFid(fid, at, dev, "streaming")
			if err2 == nil && strings.TrimSpace(u) != "" {
				return u, map[string]string{}, nil
			}
			if err2 != nil && ucTVIsAccessTokenInvalid(resp) {
				store2 := readPanLoginSettings(database)
				rt := getPanField(store2, "uc", "refresh_token")
				dev2 := getPanField(store2, "uc", "device_id")
				if rt != "" && dev2 != "" {
					newAT, newRT, newExpAt, e3 := ucTVRefreshAccessToken(rt, dev2)
					if e3 == nil && strings.TrimSpace(newAT) != "" {
						setPanField(store2, "uc", "access_token", newAT)
						if newExpAt > 0 {
							setPanField(store2, "uc", "access_token_exp_at", strconv.FormatInt(newExpAt, 10))
						}
						if newRT != "" && newRT != rt {
							setPanField(store2, "uc", "refresh_token", newRT)
						}
						_ = writePanLoginSettings(database, store2)
						u2, _, e4 := ucTVLinkByFid(fid, newAT, dev2, "streaming")
						if e4 == nil && strings.TrimSpace(u2) != "" {
							return u2, map[string]string{}, nil
						}
					}
				}
			}
		}
	}

	cookie := getPanField(store, "uc", "cookie")
	if cookie == "" {
		return "", nil, errors.New("missing uc cookie (pan_login_settings[\"uc\"].cookie)")
	}
	u, err := ucDirectDownload(fid, fidToken, cookie, want)
	if err != nil {
		return "", nil, err
	}
	headers := map[string]string{
		"Cookie":     cookie,
		"Referer":    ucShareReferer,
		"User-Agent": ucShareUA,
	}
	return u, headers, nil
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
	vod, shareID, err := UCList(database, flag, passcode)
	if err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": err.Error()})
		return
	}
	writeJSON(w, 200, map[string]any{"ok": true, "flag": flag, "shareId": shareID, "vod_play_url": vod})
}

func HandleAPIUCPlay(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	var body struct {
		ID   string `json:"id"`
		Want string `json:"want"`
	}
	_ = readJSONLoose(r, &body)
	id := strings.TrimSpace(body.ID)
	if id == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": "missing id"})
		return
	}
	u, header, err := UCPlay(database, id, strings.TrimSpace(body.Want))
	if err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": err.Error()})
		return
	}
	writeJSON(w, 200, map[string]any{"ok": true, "parse": 0, "url": u, "headers": header})
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
