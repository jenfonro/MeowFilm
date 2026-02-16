package netdisk

import (
	"bytes"
	"crypto/md5"
	"crypto/rand"
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

// --- Share list/play (direct cloud-drive API) ---

const (
	quarkShareAPIBase = "https://drive.quark.cn"
	quarkShareReferer = "https://pan.quark.cn/"
	quarkShareUA      = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) quark-cloud-drive/2.5.20 Chrome/100.0.4896.160 Electron/18.3.5.4-b478491100 Safari/537.36 Channel/pckk_other_ch"
)

// QuarkTV (open-api-drive): ported from CatPawOpen `panQuark.js`.
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

func parseQuarkShareIDFromFlag(flag string) string {
	s := strings.TrimSpace(flag)
	if s == "" {
		return ""
	}
	if strings.HasPrefix(s, "夸父-") {
		return strings.TrimSpace(strings.TrimPrefix(s, "夸父-"))
	}
	if strings.HasPrefix(strings.ToLower(s), "quark-") {
		return strings.TrimSpace(s[6:])
	}
	return ""
}

func buildQuarkShareHeaders(cookie string) http.Header {
	h := http.Header{}
	h.Set("User-Agent", quarkShareUA)
	h.Set("Referer", quarkShareReferer)
	h.Set("Accept", "application/json, text/plain, */*")
	h.Set("Content-Type", "application/json")
	h.Set("Accept-Encoding", "gzip, deflate, br")
	if strings.TrimSpace(cookie) != "" {
		h.Set("Cookie", strings.TrimSpace(cookie))
	}
	return h
}

func quarkShareDoJSON(method string, urlStr string, headers http.Header, body []byte, out any) error {
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
		return errors.New("quark http " + strconv.Itoa(resp.StatusCode) + ": " + strings.TrimSpace(string(b)))
	}
	return json.Unmarshal(bytes.TrimSpace(b), out)
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
		List []map[string]any `json:"list"`
	} `json:"data"`
}

func quarkShareDetail(shareID string, stoken string, cookie string) (quarkShareDetailResp, error) {
	pwdID := strings.TrimSpace(shareID)
	sToken := strings.TrimSpace(stoken)
	if pwdID == "" || sToken == "" {
		return quarkShareDetailResp{}, errors.New("missing quark share parameters")
	}
	u, _ := url.Parse(quarkShareAPIBase + "/1/clouddrive/share/sharepage/detail?pr=ucpro&fr=pc")
	q := u.Query()
	q.Set("pwd_id", pwdID)
	q.Set("stoken", sToken)
	q.Set("pdir_fid", "0")
	q.Set("force", "0")
	q.Set("_page", "1")
	q.Set("_size", "200")
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

func QuarkList(database *db.DB, flag string, passcode string) (string, string, error) {
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
	detail, err := quarkShareDetail(shareID, stoken, cookie)
	if err != nil {
		return "", shareID, err
	}
	parts := []string{}
	for _, it := range detail.Data.List {
		if quarkShareIsDirItem(it) {
			continue
		}
		fid := quarkShareItemFid(it)
		fidToken := quarkShareItemFidToken(it)
		name := quarkShareItemName(it)
		if fid == "" || fidToken == "" || name == "" {
			continue
		}
		id := shareID + "*" + stoken + "*" + fid + "*" + fidToken + "***" + name
		parts = append(parts, name+"$"+id)
	}
	return strings.Join(parts, "#"), shareID, nil
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
		return "", "", 0, errors.New("quark_tv http " + strconv.Itoa(resp.StatusCode) + ": " + strings.TrimSpace(string(body)))
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
	return resp.Status == -1 && resp.Errno == 10001
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

	client := &http.Client{Timeout: 18 * time.Second}
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
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return "", quarkTVFileResp{}, errors.New("quark_tv http " + strconv.Itoa(resp.StatusCode) + ": " + strings.TrimSpace(string(body)))
	}
	var out quarkTVFileResp
	if err := json.Unmarshal(bytes.TrimSpace(body), &out); err != nil {
		return "", quarkTVFileResp{}, err
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
	rt := getPanField(store, "quark", "refresh_token")
	dev := getPanField(store, "quark", "device_id")
	at := getPanField(store, "quark", "access_token")
	expAtRaw := getPanField(store, "quark", "access_token_exp_at")
	expAtMs, _ := strconv.ParseInt(strings.TrimSpace(expAtRaw), 10, 64)

	if at != "" && expAtMs > 0 {
		if time.Now().UnixMilli()+quarkTVTokenSkewMs < expAtMs {
			return at, dev, nil
		}
	}
	if at != "" && expAtMs == 0 {
		return at, dev, nil
	}
	if rt == "" || dev == "" {
		return "", "", errors.New("missing quark_tv credentials (pan_login_settings[\"quark\"].refresh_token + device_id)")
	}

	quarkTVTokenMu.Lock()
	defer quarkTVTokenMu.Unlock()

	store = readPanLoginSettings(database)
	at = getPanField(store, "quark", "access_token")
	expAtRaw = getPanField(store, "quark", "access_token_exp_at")
	expAtMs, _ = strconv.ParseInt(strings.TrimSpace(expAtRaw), 10, 64)
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
	setPanField(store, "quark", "access_token", newAT)
	if newExpAt > 0 {
		setPanField(store, "quark", "access_token_exp_at", strconv.FormatInt(newExpAt, 10))
	} else {
		setPanField(store, "quark", "access_token_exp_at", "")
	}
	if newRT != "" && newRT != rt {
		setPanField(store, "quark", "refresh_token", newRT)
	}
	_ = writePanLoginSettings(database, store)
	return newAT, dev, nil
}

func QuarkPlay(database *db.DB, id string, want string) (string, map[string]string, error) {
	_, _, fid, fidToken, _ := parseQuarkPlayID(id)
	if fid == "" {
		return "", nil, errors.New("invalid id (missing fid)")
	}
	store := readPanLoginSettings(database)
	// Prefer QuarkTV link when credentials exist (no cookie headers needed).
	if getPanField(store, "quark", "refresh_token") != "" && getPanField(store, "quark", "device_id") != "" {
		at, dev, err := ensureQuarkTVAccessToken(database)
		if err == nil && at != "" && dev != "" {
			u, resp, err2 := quarkTVLinkByFid(fid, at, dev, "streaming")
			if err2 == nil && strings.TrimSpace(u) != "" {
				return u, map[string]string{}, nil
			}
			if err2 != nil && quarkTVIsAccessTokenInvalid(resp) {
				store2 := readPanLoginSettings(database)
				rt := getPanField(store2, "quark", "refresh_token")
				dev2 := getPanField(store2, "quark", "device_id")
				if rt != "" && dev2 != "" {
					newAT, newRT, newExpAt, e3 := quarkTVRefreshAccessToken(rt, dev2)
					if e3 == nil && strings.TrimSpace(newAT) != "" {
						setPanField(store2, "quark", "access_token", newAT)
						if newExpAt > 0 {
							setPanField(store2, "quark", "access_token_exp_at", strconv.FormatInt(newExpAt, 10))
						}
						if newRT != "" && newRT != rt {
							setPanField(store2, "quark", "refresh_token", newRT)
						}
						_ = writePanLoginSettings(database, store2)
						u2, _, e4 := quarkTVLinkByFid(fid, newAT, dev2, "streaming")
						if e4 == nil && strings.TrimSpace(u2) != "" {
							return u2, map[string]string{}, nil
						}
					}
				}
			}
		}
	}
	cookie := getPanField(store, "quark", "cookie")
	if cookie == "" {
		return "", nil, errors.New("missing quark cookie (pan_login_settings[\"quark\"].cookie)")
	}
	u, err := quarkDirectDownload(fid, fidToken, cookie, want)
	if err != nil {
		return "", nil, err
	}
	headers := map[string]string{
		"Cookie":     cookie,
		"Referer":    quarkShareReferer,
		"User-Agent": quarkShareUA,
	}
	return u, headers, nil
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
	vod, shareID, err := QuarkList(database, flag, passcode)
	if err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": err.Error()})
		return
	}
	writeJSON(w, 200, map[string]any{"ok": true, "flag": flag, "shareId": shareID, "vod_play_url": vod})
}

func HandleAPIQuarkPlay(w http.ResponseWriter, r *http.Request, database *db.DB) {
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
	u, header, err := QuarkPlay(database, id, strings.TrimSpace(body.Want))
	if err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": err.Error()})
		return
	}
	writeJSON(w, 200, map[string]any{"ok": true, "parse": 0, "url": u, "headers": header})
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
