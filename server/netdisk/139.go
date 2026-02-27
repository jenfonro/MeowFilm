package netdisk

import (
	"bytes"
	"crypto/aes"
	"crypto/cipher"
	"crypto/md5"
	"crypto/rand"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"regexp"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
)

const (
	pan139OutlinkBase = "https://share-kd-njs.yun.139.com/yun-share/richlifeApp/devapp/IOutLink/"
	pan139UA          = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36"
	pan139DeviceInfo  = "||9|12.27.0|chrome|143.0.0.0|pda50460feabd10141fb59a3ba787afb||windows 10|1624X1305|zh-CN|||"
	// Matches config/0119.js anonymous OutLink list request header.
	pan139DeviceInfo0119 = "||3|12.27.0|chrome|131.0.0.0|5c7c68368f048245e1ce47f1c0f8f2d0||windows 10|1536X695|zh-CN|||"
	pan139KeyStr         = "PVGDwmcvfs1uV3d1"
)

var pan139SkipDirRe = regexp.MustCompile(`App|活动中心|免费|1T空间|免流`)
var pan139LinkIDByPrefixRe = regexp.MustCompile(`(?i)(?:逸动|yidong)[-_ ]*([a-zA-Z0-9]+)`)
var pan139LinkIDByURLRe = regexp.MustCompile(`(?i)(?:/w/i/|[?&]linkID=|/m/i[?]|/m/i/?|/shareweb/#.*?/w/i/)([A-Za-z0-9]+)`)
var pan139LinkIDByCaiyunMobileRe = regexp.MustCompile(`(?i)https://caiyun\\.139\\.com/m/i[?]([^&]+)`)

func parse139LinkIDFromFlag(flag string) string {
	s := strings.TrimSpace(flag)
	if s == "" {
		return ""
	}
	if m := pan139LinkIDByPrefixRe.FindStringSubmatch(s); len(m) >= 2 {
		return strings.TrimSpace(m[1])
	}
	// URL patterns:
	// - https://caiyun.139.com/w/i/<linkID>
	// - https://caiyun.139.com/m/i?<linkID>
	// - https://yun.139.com/shareweb/#/w/i/<linkID>
	// - ...?linkID=<linkID>
	if m := pan139LinkIDByURLRe.FindStringSubmatch(s); len(m) >= 2 {
		return strings.TrimSpace(m[1])
	}
	if m := pan139LinkIDByCaiyunMobileRe.FindStringSubmatch(s); len(m) >= 2 {
		return strings.TrimSpace(m[1])
	}
	return ""
}

func stripBasicPrefix(v string) string {
	s := strings.TrimSpace(v)
	if s == "" {
		return ""
	}
	// Only strip the leading "Basic " prefix (case-insensitive) without lowercasing the payload,
	// because base64 is case-sensitive.
	if strings.HasPrefix(s, "Basic ") {
		return strings.TrimSpace(s[len("Basic "):])
	}
	if strings.HasPrefix(s, "basic ") {
		return strings.TrimSpace(s[len("basic "):])
	}
	return s
}

func normalizeBase64(s string) string {
	t := strings.TrimSpace(s)
	if t == "" {
		return ""
	}
	t = strings.ReplaceAll(t, " ", "")
	t = strings.ReplaceAll(t, "\n", "")
	t = strings.ReplaceAll(t, "\r", "")
	t = strings.ReplaceAll(t, "\t", "")
	t = strings.ReplaceAll(t, "-", "+")
	t = strings.ReplaceAll(t, "_", "/")
	switch len(t) % 4 {
	case 2:
		t += "=="
	case 3:
		t += "="
	}
	return t
}

func decodeAccountFromAuthorization(auth string) string {
	raw := stripBasicPrefix(auth)
	if raw == "" {
		return ""
	}
	parseDecoded := func(decoded string) string {
		parts := strings.Split(decoded, ":")
		if len(parts) >= 3 {
			return strings.TrimSpace(parts[1])
		}
		return ""
	}
	// base64("xxx:<account>:<token...>")
	if b, err := base64.StdEncoding.DecodeString(normalizeBase64(raw)); err == nil {
		if acc := parseDecoded(string(b)); acc != "" {
			return acc
		}
	}
	return parseDecoded(raw)
}

func toInt64(v any) int64 {
	switch x := v.(type) {
	case nil:
		return 0
	case int:
		return int64(x)
	case int64:
		return x
	case uint64:
		if x > uint64(^uint64(0)>>1) {
			return 0
		}
		return int64(x)
	case float64:
		return int64(x)
	case float32:
		return int64(x)
	case json.Number:
		if n, err := x.Int64(); err == nil {
			return n
		}
		if f, err := x.Float64(); err == nil {
			return int64(f)
		}
		return 0
	case string:
		s := strings.TrimSpace(x)
		if s == "" {
			return 0
		}
		if n, err := strconv.ParseInt(s, 10, 64); err == nil {
			return n
		}
		if f, err := strconv.ParseFloat(s, 64); err == nil {
			return int64(f)
		}
		return 0
	default:
		s := strings.TrimSpace(toString(x))
		if s == "" {
			return 0
		}
		if n, err := strconv.ParseInt(s, 10, 64); err == nil {
			return n
		}
		if f, err := strconv.ParseFloat(s, 64); err == nil {
			return int64(f)
		}
		return 0
	}
}

func pkcs7Pad(b []byte, blockSize int) []byte {
	if blockSize <= 0 {
		return b
	}
	n := blockSize - (len(b) % blockSize)
	if n == 0 {
		n = blockSize
	}
	pad := bytes.Repeat([]byte{byte(n)}, n)
	return append(b, pad...)
}

func pkcs7Unpad(b []byte, blockSize int) ([]byte, error) {
	if len(b) == 0 || blockSize <= 0 || len(b)%blockSize != 0 {
		return nil, errors.New("invalid padding")
	}
	n := int(b[len(b)-1])
	if n <= 0 || n > blockSize || n > len(b) {
		return nil, errors.New("invalid padding")
	}
	for i := 0; i < n; i++ {
		if b[len(b)-1-i] != byte(n) {
			return nil, errors.New("invalid padding")
		}
	}
	return b[:len(b)-n], nil
}

func aesCbcEncryptBase64(key []byte, plainText string) (string, error) {
	block, err := aes.NewCipher(key)
	if err != nil {
		return "", err
	}
	iv := make([]byte, 16)
	if _, err := rand.Read(iv); err != nil {
		return "", err
	}
	pt := pkcs7Pad([]byte(plainText), block.BlockSize())
	ct := make([]byte, len(pt))
	cipher.NewCBCEncrypter(block, iv).CryptBlocks(ct, pt)
	out := append(iv, ct...)
	return base64.StdEncoding.EncodeToString(out), nil
}

func aesCbcDecryptBase64(key []byte, b64Text string) (string, error) {
	raw, err := base64.StdEncoding.DecodeString(normalizeBase64(b64Text))
	if err != nil {
		return "", err
	}
	if len(raw) < 17 {
		return "", errors.New("ciphertext too short")
	}
	iv := raw[:16]
	ct := raw[16:]
	block, err := aes.NewCipher(key)
	if err != nil {
		return "", err
	}
	if len(ct)%block.BlockSize() != 0 {
		return "", errors.New("ciphertext invalid")
	}
	pt := make([]byte, len(ct))
	cipher.NewCBCDecrypter(block, iv).CryptBlocks(pt, ct)
	pt, err = pkcs7Unpad(pt, block.BlockSize())
	if err != nil {
		return "", err
	}
	return string(pt), nil
}

func md5HexLower(s string) string {
	h := md5.Sum([]byte(s))
	return hex.EncodeToString(h[:])
}

func encodeURIComponent139(s string) string {
	// JS encodeURIComponent keeps: A-Z a-z 0-9 - _ . ! ~ * ' ( )
	const safe = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_.!~*'()"
	isSafe := func(b byte) bool {
		return strings.IndexByte(safe, b) >= 0
	}
	var out strings.Builder
	for i := 0; i < len(s); i++ {
		b := s[i]
		if isSafe(b) {
			out.WriteByte(b)
		} else {
			out.WriteString("%")
			out.WriteString(strings.ToUpper(hex.EncodeToString([]byte{b})))
		}
	}
	return out.String()
}

func calMcloudSign(plainJSONBody string, ts string, randStr string) string {
	encoded := encodeURIComponent139(plainJSONBody)
	chars := strings.Split(encoded, "")
	sort.Strings(chars)
	sorted := strings.Join(chars, "")
	bodyB64 := base64.StdEncoding.EncodeToString([]byte(sorted))
	res := md5HexLower(bodyB64) + md5HexLower(ts+":"+randStr)
	return strings.ToUpper(md5HexLower(res))
}

func randomAlphaNum(n int) string {
	const alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"
	if n <= 0 {
		return ""
	}
	b := make([]byte, n)
	_, _ = rand.Read(b)
	var out strings.Builder
	for i := 0; i < n; i++ {
		out.WriteByte(alphabet[int(b[i])%len(alphabet)])
	}
	return out.String()
}

func formatChinaTimestamp() string {
	cst := time.FixedZone("CST", 8*60*60)
	return time.Now().In(cst).Format("2006-01-02 15:04:05")
}

func buildMcloudHeaders(authorization string, bodyForSign string) map[string]string {
	ts := formatChinaTimestamp()
	randStr := randomAlphaNum(16)
	sign := calMcloudSign(bodyForSign, ts, randStr)
	return map[string]string{
		"Accept":           "application/json, text/plain, */*",
		"Accept-Encoding":  "gzip, deflate",
		"Accept-Language":  "zh-CN,zh;q=0.9,en;q=0.8",
		"Authorization":    "Basic " + stripBasicPrefix(authorization),
		"Content-Type":     "application/json;charset=UTF-8",
		"Hcy-Cool-Flag":    "1",
		"Mcloud-Sign":      ts + "," + randStr + "," + sign,
		"Origin":           "https://yun.139.com",
		"Referer":          "https://yun.139.com/",
		"User-Agent":       pan139UA,
		"X-Deviceinfo":     pan139DeviceInfo,
		"Accept-Charset":   "utf-8",
		"X-Requested-With": "XMLHttpRequest",
	}
}

func buildOutlinkAnonHeaders0119() map[string]string {
	return map[string]string{
		"User-Agent":    pan139UA,
		"Accept":        "application/json, text/plain, */*",
		"Content-Type":  "application/json",
		"hcy-cool-flag": "1",
		"x-deviceinfo":  pan139DeviceInfo0119,
	}
}

func buildOutlinkAnonHeaders0119WithReferer(linkID string) map[string]string {
	h := buildOutlinkAnonHeaders0119()
	lid := strings.TrimSpace(linkID)
	if lid != "" {
		h["Referer"] = "https://caiyun.139.com/w/i/" + lid
	}
	return h
}

func outlinkPostEncrypted(path string, plainBody string, authorization string) (parsed map[string]any, rawText string, decrypted string, err error) {
	key := []byte(pan139KeyStr)
	encB64, err := aesCbcEncryptBase64(key, plainBody)
	if err != nil {
		return nil, "", "", err
	}
	bodyBytes, _ := json.Marshal(encB64)
	req, err := http.NewRequest(http.MethodPost, pan139OutlinkBase+path, bytes.NewReader(bodyBytes))
	if err != nil {
		return nil, "", "", err
	}
	h := buildMcloudHeaders(authorization, plainBody)
	for k, v := range h {
		req.Header.Set(k, v)
	}
	client := &http.Client{Timeout: 18 * time.Second}
	resp, err := client.Do(req)
	if err != nil {
		return nil, "", "", err
	}
	defer resp.Body.Close()
	b, _ := io.ReadAll(resp.Body)
	rawText = strings.TrimSpace(string(b))
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return nil, rawText, "", errors.New("139 http " + strconv.Itoa(resp.StatusCode))
	}
	// Response may be JSON string.
	respB64 := rawText
	if strings.HasPrefix(respB64, "\"") {
		var s string
		if err := json.Unmarshal([]byte(respB64), &s); err == nil {
			respB64 = s
		}
	}
	decrypted, err = aesCbcDecryptBase64(key, respB64)
	if err != nil {
		decrypted = rawText
	}
	var obj map[string]any
	_ = json.Unmarshal([]byte(strings.TrimSpace(decrypted)), &obj)
	return obj, rawText, decrypted, nil
}

func pickOutlinkCoList(parsed map[string]any) []map[string]any {
	if parsed == nil {
		return nil
	}
	data, _ := parsed["data"].(map[string]any)
	listAny := any(nil)
	if data != nil {
		if v, ok := data["coLst"]; ok {
			listAny = v
		} else if v, ok := data["co_list"]; ok {
			listAny = v
		} else if v, ok := data["list"]; ok {
			listAny = v
		}
	}
	if listAny == nil {
		if v, ok := parsed["coLst"]; ok {
			listAny = v
		} else if v, ok := parsed["list"]; ok {
			listAny = v
		}
	}
	if m, _ := listAny.(map[string]any); m != nil {
		if it, ok := m["item"]; ok {
			listAny = it
		}
	}
	arr, _ := listAny.([]any)
	out := []map[string]any{}
	for _, it := range arr {
		m, _ := it.(map[string]any)
		if m != nil {
			out = append(out, m)
		}
	}
	return out
}

func outlinkIsDirItem(it map[string]any) bool {
	if it == nil {
		return false
	}
	tryKeys := []string{"isFolder", "isDir", "isdir", "folder", "dir"}
	for _, k := range tryKeys {
		v := strings.ToLower(strings.TrimSpace(toString(it[k])))
		if v == "1" || v == "true" || v == "yes" || v == "on" {
			return true
		}
	}
	// Heuristic: some responses use `coType` or `type`.
	v := strings.ToLower(strings.TrimSpace(toString(it["coType"])))
	if v == "dir" || v == "folder" || v == "directory" {
		return true
	}
	// Another heuristic: if `fileType` indicates folder.
	ft := strings.ToLower(strings.TrimSpace(toString(it["fileType"])))
	if ft == "folder" || ft == "dir" {
		return true
	}
	return false
}

func outlinkDirID(it map[string]any) string {
	if it == nil {
		return ""
	}
	tryKeys := []string{"caID", "caId", "catalogID", "catalogId", "dirID", "dirId", "folderID", "folderId", "pCaID", "pCaId", "parentId", "id"}
	for _, k := range tryKeys {
		if v := strings.TrimSpace(toString(it[k])); v != "" {
			return v
		}
	}
	return ""
}

func pickRedrURL(parsed map[string]any) string {
	var b map[string]any
	if parsed == nil {
		return ""
	}
	if data, _ := parsed["data"].(map[string]any); data != nil {
		b = data
	} else {
		b = parsed
	}
	tryKeys := []string{"redrUrl", "redrUrlNew", "downloadUrl", "url", "dlUrl"}
	for _, k := range tryKeys {
		if v, ok := b[k].(string); ok {
			s := strings.TrimSpace(v)
			if strings.HasPrefix(s, "http") {
				return s
			}
		}
	}
	return ""
}

type pan139DirItem struct {
	Name string
	Path string
}

type pan139FileItem struct {
	Name string
	CoID string
	Size int64
}

type pan139ShareFile struct {
	Name    string
	CoID    string
	Size    int64
	DirPath string
}

type pan139Outlink0119CacheEntry struct {
	Data      map[string]any
	IsNil     bool
	ExpiresAt time.Time
}

var pan139Outlink0119Cache sync.Map // key: linkID-pCaID -> pan139Outlink0119CacheEntry

func outlinkPostEncryptedAnon0119(path string, plainBody string, linkID string) (parsed map[string]any, rawText string, decrypted string, err error) {
	key := []byte(pan139KeyStr)
	encB64, err := aesCbcEncryptBase64(key, plainBody)
	if err != nil {
		return nil, "", "", err
	}
	bodyBytes, _ := json.Marshal(encB64)
	req, err := http.NewRequest(http.MethodPost, pan139OutlinkBase+path, bytes.NewReader(bodyBytes))
	if err != nil {
		return nil, "", "", err
	}
	for k, v := range buildOutlinkAnonHeaders0119WithReferer(linkID) {
		req.Header.Set(k, v)
	}
	client := &http.Client{Timeout: 18 * time.Second}
	resp, err := client.Do(req)
	if err != nil {
		return nil, "", "", err
	}
	defer resp.Body.Close()
	b, _ := io.ReadAll(resp.Body)
	rawText = strings.TrimSpace(string(b))
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return nil, rawText, "", errors.New("139 http " + strconv.Itoa(resp.StatusCode))
	}
	// Response may be JSON string.
	respB64 := rawText
	if strings.HasPrefix(respB64, "\"") {
		var s string
		if err := json.Unmarshal([]byte(respB64), &s); err == nil {
			respB64 = s
		}
	}
	decrypted, err = aesCbcDecryptBase64(key, respB64)
	if err != nil {
		decrypted = rawText
	}
	var obj map[string]any
	_ = json.Unmarshal([]byte(strings.TrimSpace(decrypted)), &obj)
	return obj, rawText, decrypted, nil
}

func isOutlinkSuccessCode(code any) bool {
	c := strings.TrimSpace(toString(code))
	if c == "" {
		return true
	}
	if c == "0" || strings.EqualFold(c, "success") || c == "200" {
		return true
	}
	return false
}

func pickOutlinkError(root map[string]any) (code string, desc string, ok bool) {
	if root == nil {
		return "", "", true
	}
	c := root["code"]
	if c == nil {
		c = root["resultCode"]
	}
	if c == nil {
		c = root["result_code"]
	}
	if isOutlinkSuccessCode(c) {
		return "", "", true
	}
	d := strings.TrimSpace(toString(root["desc"]))
	if d == "" {
		d = strings.TrimSpace(toString(root["message"]))
	}
	if d == "" {
		d = strings.TrimSpace(toString(root["msg"]))
	}
	cc := strings.TrimSpace(toString(c))
	if cc == "" {
		cc = "error"
	}
	if d == "" {
		d = "request failed"
	}
	return cc, d, false
}

func toFriendlyOutlinkErrorMessage(code string, desc string) string {
	d := strings.TrimSpace(desc)
	c := strings.TrimSpace(code)
	if c == "" {
		c = "error"
	}
	if regexp.MustCompile(`浏览次数.*上限|达到.*次数.*上限|次数.*上限`).MatchString(d) {
		return c + ": 分享已达到浏览次数上限"
	}
	if strings.Contains(d, "来晚了") {
		return c + ": " + d
	}
	if d != "" {
		return c + ": " + d
	}
	return c
}

func getOutLinkInfoV6_0119(linkID string, pCaID string, bNum int, eNum int, passcode string) (map[string]any, error) {
	lid := strings.TrimSpace(linkID)
	pid := strings.TrimSpace(pCaID)
	pw := strings.TrimSpace(passcode)
	if bNum <= 0 {
		bNum = 1
	}
	if eNum <= 0 {
		eNum = 200
	}
	passSig := ""
	if pw != "" {
		passSig = md5HexLower(pw)
	}
	key := lid + "-" + pid + "-" + strconv.Itoa(bNum) + "-" + strconv.Itoa(eNum) + "-" + passSig
	if v, ok := pan139Outlink0119Cache.Load(key); ok {
		if ent, _ := v.(pan139Outlink0119CacheEntry); !ent.ExpiresAt.IsZero() {
			if time.Now().Before(ent.ExpiresAt) {
				if ent.IsNil {
					return nil, nil
				}
				return ent.Data, nil
			}
			pan139Outlink0119Cache.Delete(key)
		}
	}

	payload := map[string]any{
		"getOutLinkInfoReq": map[string]any{
			"account": "",
			"linkID":  lid,
			"passwd":  pw,
			"caSrt":   0,
			"coSrt":   0,
			"srtDr":   1,
			"bNum":    bNum,
			"pCaID":   pid,
			"eNum":    eNum,
		},
		"commonAccountInfo": map[string]any{"account": "", "accountType": 1},
	}
	plainBytes, _ := json.Marshal(payload)
	plain := string(plainBytes)
	parsed, _, _, err := outlinkPostEncryptedAnon0119("getOutLinkInfoV6", plain, lid)
	if err != nil {
		return nil, err
	}
	if code, desc, ok := pickOutlinkError(parsed); !ok {
		if strings.TrimSpace(code) == "9188" && strings.TrimSpace(passcode) == "" {
			return nil, errors.New("该分享需要密码,但是上游未提供")
		}
		return nil, errors.New(toFriendlyOutlinkErrorMessage(code, desc))
	}
	data, _ := parsed["data"].(map[string]any)
	if data == nil {
		return nil, nil
	}
	pan139Outlink0119Cache.Store(key, pan139Outlink0119CacheEntry{
		Data:      data,
		IsNil:     false,
		ExpiresAt: time.Now().Add(10 * time.Minute),
	})
	return data, nil
}

func getShareFile_0119(linkID string, pCaID string, passcode string) ([]pan139DirItem, error) {
	if strings.TrimSpace(pCaID) == "" {
		return nil, nil
	}
	ca := strings.TrimSpace(pCaID)
	if strings.HasPrefix(ca, "http") {
		ca = "root"
	}
	o, err := getOutLinkInfoV6_0119(linkID, ca, 1, 200, passcode)
	if err != nil {
		return nil, err
	}
	if o == nil {
		return nil, nil
	}
	caLstAny, ok := o["caLst"]
	if !ok || caLstAny == nil {
		return nil, nil
	}
	caLstArr, _ := caLstAny.([]any)
	if len(caLstArr) == 0 {
		return nil, nil
	}
	u := make([]pan139DirItem, 0, len(caLstArr))
	paths := make([]string, 0, len(caLstArr))
	for _, raw := range caLstArr {
		m, _ := raw.(map[string]any)
		if m == nil {
			continue
		}
		name := strings.TrimSpace(toString(m["caName"]))
		path := strings.TrimSpace(toString(m["path"]))
		if path != "" {
			paths = append(paths, path)
		}
		if name == "" || path == "" {
			continue
		}
		if pan139SkipDirRe.MatchString(name) {
			continue
		}
		u = append(u, pan139DirItem{Name: name, Path: path})
	}

	if len(paths) == 0 {
		return u, nil
	}

	children := make([][]pan139DirItem, len(paths))
	var wg sync.WaitGroup
	wg.Add(len(paths))
	for idx, p := range paths {
		i := idx
		path := p
		go func() {
			defer wg.Done()
			ch, _ := getShareFile_0119(linkID, path, passcode)
			children[i] = ch
		}()
	}
	wg.Wait()

	out := make([]pan139DirItem, 0, len(u)+16)
	out = append(out, u...)
	for _, ch := range children {
		if len(ch) > 0 {
			out = append(out, ch...)
		}
	}
	return out, nil
}

func getShareUrl_0119(linkID string, pCaID string, passcode string) ([]pan139FileItem, error) {
	t, err := getOutLinkInfoV6_0119(linkID, pCaID, 1, 200, passcode)
	if err != nil {
		return nil, err
	}
	if t == nil {
		return nil, nil
	}
	_, hasCo := t["coLst"]
	if !hasCo {
		return nil, nil
	}
	o := t["coLst"]
	if o != nil {
		arr, _ := o.([]any)
		out := make([]pan139FileItem, 0, len(arr))
		for _, raw := range arr {
			m, _ := raw.(map[string]any)
			if m == nil {
				continue
			}
			if toInt64(m["coType"]) != 3 {
				continue
			}
			name := strings.TrimSpace(toString(m["coName"]))
			coID := strings.TrimSpace(toString(m["coID"]))
			size := toInt64(m["coSize"])
			if name == "" || coID == "" {
				continue
			}
			out = append(out, pan139FileItem{Name: name, CoID: coID, Size: size})
		}
		return out, nil
	}

	if t["caLst"] != nil {
		caLstArr, _ := t["caLst"].([]any)
		paths := make([]string, 0, len(caLstArr))
		for _, raw := range caLstArr {
			m, _ := raw.(map[string]any)
			if m == nil {
				continue
			}
			p := strings.TrimSpace(toString(m["path"]))
			if p != "" {
				paths = append(paths, p)
			}
		}
		if len(paths) == 0 {
			return nil, nil
		}
		children := make([][]pan139FileItem, len(paths))
		var wg sync.WaitGroup
		wg.Add(len(paths))
		for idx, p := range paths {
			i := idx
			path := p
			go func() {
				defer wg.Done()
				ch, _ := getShareUrl_0119(linkID, path, passcode)
				children[i] = ch
			}()
		}
		wg.Wait()
		out := make([]pan139FileItem, 0, 32)
		for _, ch := range children {
			if len(ch) > 0 {
				out = append(out, ch...)
			}
		}
		return out, nil
	}
	return nil, nil
}

func pickListAny(node map[string]any, key string) []any {
	if node == nil {
		return nil
	}
	v := node[key]
	if v == nil {
		return nil
	}
	// Sometimes list is wrapped as { item: [...] }.
	if m, _ := v.(map[string]any); m != nil {
		if it := m["item"]; it != nil {
			if arr, _ := it.([]any); arr != nil {
				return arr
			}
		}
	}
	if arr, _ := v.([]any); arr != nil {
		return arr
	}
	return nil
}

func outlinkPickCaChildren(data map[string]any) []pan139DirItem {
	arr := pickListAny(data, "caLst")
	if len(arr) == 0 {
		return nil
	}
	out := make([]pan139DirItem, 0, len(arr))
	for _, raw := range arr {
		m, _ := raw.(map[string]any)
		if m == nil {
			continue
		}
		name := strings.TrimSpace(toString(m["caName"]))
		path := strings.TrimSpace(toString(m["path"]))
		if path == "" {
			continue
		}
		out = append(out, pan139DirItem{Name: name, Path: path})
	}
	return out
}

func outlinkPickCoFiles(data map[string]any) []pan139FileItem {
	arr := pickListAny(data, "coLst")
	if len(arr) == 0 {
		return nil
	}
	out := make([]pan139FileItem, 0, len(arr))
	for _, raw := range arr {
		m, _ := raw.(map[string]any)
		if m == nil {
			continue
		}
		if toInt64(m["coType"]) != 3 {
			continue
		}
		name := strings.TrimSpace(toString(m["coName"]))
		coID := strings.TrimSpace(toString(m["coID"]))
		size := toInt64(m["coSize"])
		if name == "" || coID == "" {
			continue
		}
		out = append(out, pan139FileItem{Name: name, CoID: coID, Size: size})
	}
	return out
}

type pan139OutlinkPageResult struct {
	CA        []pan139DirItem
	CO        []pan139FileItem
	Pages     int
	Truncated bool
	Suspect   bool
}

func getOutLinkInfoV6AllPages(linkID string, pCaID string, eNum int, maxPages int, passcode string) (pan139OutlinkPageResult, error) {
	if eNum <= 0 {
		eNum = 200
	}
	if maxPages <= 0 {
		maxPages = 50
	}

	seenCo := map[string]struct{}{}
	seenCa := map[string]struct{}{}
	lastSig := ""

	out := pan139OutlinkPageResult{CA: []pan139DirItem{}, CO: []pan139FileItem{}, Pages: 0}
	for bn := 1; bn <= maxPages; bn++ {
		out.Pages = bn
		data, err := getOutLinkInfoV6_0119(linkID, pCaID, bn, eNum, passcode)
		if err != nil {
			return pan139OutlinkPageResult{}, err
		}
		if data == nil {
			break
		}
		ca := outlinkPickCaChildren(data)
		co := outlinkPickCoFiles(data)
		if len(ca) == 0 && len(co) == 0 {
			break
		}

		newItems := 0
		sigParts := make([]string, 0, 16)
		for _, it := range co {
			if it.CoID == "" {
				continue
			}
			if _, ok := seenCo[it.CoID]; !ok {
				seenCo[it.CoID] = struct{}{}
				newItems++
			}
			if len(sigParts) < 16 {
				sigParts = append(sigParts, "co:"+it.CoID)
			}
		}
		for _, it := range ca {
			if it.Path == "" {
				continue
			}
			if _, ok := seenCa[it.Path]; !ok {
				seenCa[it.Path] = struct{}{}
				newItems++
			}
			if len(sigParts) < 16 {
				sigParts = append(sigParts, "ca:"+it.Path)
			}
		}
		sig := strings.Join(sigParts, "|")
		if bn > 1 {
			if newItems == 0 {
				out.Suspect = true
				break
			}
			if sig != "" && sig == lastSig {
				out.Suspect = true
				break
			}
		}
		lastSig = sig

		out.CA = append(out.CA, ca...)
		out.CO = append(out.CO, co...)

		maybeHasNext := len(ca) >= eNum || len(co) >= eNum
		if !maybeHasNext {
			break
		}
		if bn == maxPages {
			out.Truncated = true
		}
	}
	return out, nil
}

func resolveLogicalRoot(linkID string, pCaID string, eNum int, maxPages int, passcode string) (string, []string, error) {
	cur := strings.TrimSpace(pCaID)
	if cur == "" {
		cur = "root"
	}
	if cur != "root" {
		return cur, nil, nil
	}
	rootPrefixSegs := []string{}
	for i := 0; i < 10; i++ {
		page, err := getOutLinkInfoV6AllPages(linkID, cur, eNum, maxPages, passcode)
		if err != nil {
			return "", nil, err
		}
		files := []pan139FileItem{}
		for _, f := range page.CO {
			if isSupportedVideoFilename(strings.TrimSpace(f.Name)) {
				files = append(files, f)
			}
		}
		dirs := page.CA
		if len(files) == 0 && len(dirs) == 1 {
			if n := strings.TrimSpace(dirs[0].Name); n != "" {
				rootPrefixSegs = append(rootPrefixSegs, n)
			} else if p := strings.TrimSpace(dirs[0].Path); p != "" {
				if seg := strings.Trim(p, "/"); seg != "" {
					rootPrefixSegs = append(rootPrefixSegs, seg)
				}
			}
			next := strings.TrimSpace(dirs[0].Path)
			if next == "" || next == cur {
				break
			}
			cur = next
			continue
		}
		break
	}
	return cur, rootPrefixSegs, nil
}

func collectShareFilesRecursive(linkID string, pCaID string, dirParts []string, eNum int, maxPages int, passcode string) ([]pan139ShareFile, error) {
	caID := strings.TrimSpace(pCaID)
	if caID == "" {
		caID = "root"
	}
	page, err := getOutLinkInfoV6AllPages(linkID, caID, eNum, maxPages, passcode)
	if err != nil {
		return nil, err
	}

	dirPath := "/"
	if len(dirParts) > 0 {
		tmp := make([]string, 0, len(dirParts))
		for _, p := range dirParts {
			if s := strings.TrimSpace(p); s != "" {
				tmp = append(tmp, s)
			}
		}
		if len(tmp) > 0 {
			dirPath = strings.Join(tmp, "/")
		}
	}

	out := make([]pan139ShareFile, 0, len(page.CO)+16)
	for _, it := range page.CO {
		if !isSupportedVideoFilename(strings.TrimSpace(it.Name)) {
			continue
		}
		out = append(out, pan139ShareFile{
			Name:    it.Name,
			CoID:    it.CoID,
			Size:    it.Size,
			DirPath: dirPath,
		})
	}

	children := page.CA
	if len(children) == 0 {
		return out, nil
	}
	nested := make([][]pan139ShareFile, len(children))
	var wg sync.WaitGroup
	var firstErr error
	var mu sync.Mutex
	wg.Add(len(children))
	for idx, c := range children {
		i := idx
		child := c
		go func() {
			defer wg.Done()
			name := strings.TrimSpace(child.Name)
			if name == "" {
				name = strings.TrimSpace(child.Path)
			}
			nextParts := append(append([]string{}, dirParts...), name)
			files, err := collectShareFilesRecursive(linkID, child.Path, nextParts, eNum, maxPages, passcode)
			if err != nil {
				mu.Lock()
				if firstErr == nil {
					firstErr = err
				}
				mu.Unlock()
				return
			}
			nested[i] = files
		}()
	}
	wg.Wait()
	if firstErr != nil {
		return nil, firstErr
	}
	for _, arr := range nested {
		if len(arr) > 0 {
			out = append(out, arr...)
		}
	}
	return out, nil
}

func yun139ListUncached(_ *db.DB, flag string, passcode string) (string, string, error) {
	linkID := parse139LinkIDFromFlag(flag)
	if linkID == "" {
		return "", "", errors.New("missing/invalid flag (expected: 逸动-<linkID>)")
	}
	start, rootPrefixSegs, err := resolveLogicalRoot(linkID, "root", 200, 50, passcode)
	if err != nil {
		return "", linkID, err
	}
	rootPrefix := "根目录"
	if len(rootPrefixSegs) > 0 {
		rootPrefix = strings.Join(rootPrefixSegs, "/")
	}
	files, err := collectShareFilesRecursive(linkID, start, []string{}, 200, 50, passcode)
	if err != nil {
		return "", linkID, err
	}

	parts := make([]string, 0, len(files))
	for _, f := range files {
		if strings.TrimSpace(f.Name) == "" || strings.TrimSpace(f.CoID) == "" {
			continue
		}
		dirPath := strings.TrimSpace(f.DirPath)
		if dirPath == "" {
			dirPath = "/"
		}
		parts = append(parts, prefixRootDirDisplay(dirPath, rootPrefix)+"$"+f.CoID+"*"+linkID+"***"+strings.TrimSpace(f.Name))
	}
	return strings.Join(parts, "#"), linkID, nil
}

func Yun139List(database *db.DB, flag string, passcode string) (string, string, error) {
	vod, linkID, _, err := Yun139ListWithCacheHit(database, flag, passcode)
	return vod, linkID, err
}

func Yun139ListWithCacheHit(database *db.DB, flag string, passcode string) (vod string, linkID string, fromCache bool, err error) {
	key := listCacheKey("139_list", flag, passcode)
	got, hit, err := y139ListCacheTwoTier.Do(key, func() (listCache2, error) {
		vod, linkID, e := yun139ListUncached(database, flag, passcode)
		if e != nil {
			return listCache2{}, e
		}
		return listCache2{Vod: vod, ShareID: linkID}, nil
	})
	return strings.TrimSpace(got.Vod), strings.TrimSpace(got.ShareID), hit, err
}

func parse139PlayID(id string) (linkID string, contentID string, coID string) {
	raw := strings.TrimSpace(id)
	if raw == "" {
		return "", "", ""
	}
	// 0119.js-style: "<coID>*<linkID>"
	if strings.Contains(raw, "*") && !strings.Contains(raw, "|") {
		parts := strings.Split(raw, "*")
		if len(parts) >= 1 {
			contentID = strings.TrimSpace(parts[0]) // treated as coID by caller
		}
		if len(parts) >= 2 {
			linkID = strings.TrimSpace(parts[1])
		}
		return linkID, contentID, ""
	}
	parts := strings.Split(raw, "|")
	if len(parts) >= 1 {
		linkID = strings.TrimSpace(parts[0])
	}
	if len(parts) >= 2 {
		contentID = strings.TrimSpace(parts[1])
	}
	if len(parts) >= 3 {
		coID = strings.TrimSpace(parts[2])
	}
	return
}

func getContentInfoFromOutLinkPresentURL(linkID string, coID string) (string, error) {
	payload := map[string]any{
		"getContentInfoFromOutLinkReq": map[string]any{"contentId": strings.TrimSpace(coID), "linkID": strings.TrimSpace(linkID), "account": ""},
		"commonAccountInfo":            map[string]any{"account": "", "accountType": 1},
	}
	bodyBytes, _ := json.Marshal(payload)
	req, err := http.NewRequest(http.MethodPost, pan139OutlinkBase+"getContentInfoFromOutLink", bytes.NewReader(bodyBytes))
	if err != nil {
		return "", err
	}
	req.Header.Set("User-Agent", pan139UA)
	req.Header.Set("Accept", "application/json, text/plain, */*")
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("Accept-Encoding", "identity")

	client := &http.Client{Timeout: 18 * time.Second}
	resp, err := client.Do(req)
	if err != nil {
		return "", err
	}
	defer resp.Body.Close()
	b, _ := io.ReadAll(resp.Body)
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return "", errors.New("139 http " + strconv.Itoa(resp.StatusCode))
	}
	var obj map[string]any
	if err := json.Unmarshal(b, &obj); err != nil {
		return "", err
	}
	data, _ := obj["data"].(map[string]any)
	if data == nil {
		return "", nil
	}
	contentInfo, _ := data["contentInfo"].(map[string]any)
	if contentInfo == nil {
		return "", nil
	}
	u := strings.TrimSpace(toString(contentInfo["presentURL"]))
	if u == "" {
		u = strings.TrimSpace(toString(contentInfo["presentUrl"]))
	}
	return u, nil
}

func yun139DownloadURL(database *db.DB, flag string, id string) (string, error) {
	linkID, contentID, coID := parse139PlayID(id)
	isStarID := strings.Contains(id, "*") && !strings.Contains(id, "|")
	if isStarID && strings.TrimSpace(coID) == "" && strings.TrimSpace(contentID) != "" {
		coID = contentID
		contentID = ""
	}
	if linkID == "" {
		linkID = parse139LinkIDFromFlag(flag)
	}
	if linkID == "" {
		return "", errors.New("missing linkID (from id/flag)")
	}
	if contentID == "" && coID == "" {
		return "", errors.New("missing contentId/coID (from id)")
	}
	store := readPanLoginSettings(database)
	auth := getPanField(store, "139", "authorization")
	if auth == "" {
		return "", errors.New("missing 139 authorization (pan_login_settings[\"139\"].authorization)")
	}
	account := decodeAccountFromAuthorization(auth)
	if account == "" {
		return "", errors.New("authorization invalid (missing account)")
	}
	tryOnce := func(useCo bool) (string, map[string]any, error) {
		var payload map[string]any
		if useCo && coID != "" {
			payload = map[string]any{
				"dlFromOutLinkReqV3": map[string]any{"account": account, "linkID": linkID, "coIDLst": map[string]any{"item": []any{coID}}},
				"commonAccountInfo":  map[string]any{"account": account, "accountType": 1},
			}
		} else {
			payload = map[string]any{
				"dlFromOutLinkReq":  map[string]any{"contentId": contentID, "linkID": linkID, "account": account},
				"commonAccountInfo": map[string]any{"account": account, "accountType": 1},
			}
		}
		plainBytes, _ := json.Marshal(payload)
		plain := string(plainBytes)
		parsed, _, _, err := outlinkPostEncrypted("dlFromOutLinkV3", plain, auth)
		if err != nil {
			return "", nil, err
		}
		return pickRedrURL(parsed), parsed, nil
	}

	u, parsed, err := tryOnce(true)
	if err != nil {
		return "", err
	}
	if u != "" {
		return u, nil
	}
	code := toString(parsed["code"])
	if code == "" {
		code = toString(parsed["resultCode"])
	}
	if strings.TrimSpace(code) == "9530" {
		u2, _, err := tryOnce(false)
		if err != nil {
			return "", err
		}
		if u2 != "" {
			return u2, nil
		}
	}
	desc := toString(parsed["desc"])
	if desc == "" {
		desc = toString(parsed["message"])
	}
	if strings.TrimSpace(desc) == "" {
		desc = "failed"
	}
	return "", errors.New(strings.TrimSpace(code) + ": " + strings.TrimSpace(desc))
}

func Yun139Play(database *db.DB, flag string, id string) (downloadURL string, playURL string, err error) {
	linkID, contentID, coID := parse139PlayID(id)
	isStarID := strings.Contains(id, "*") && !strings.Contains(id, "|")
	if isStarID && strings.TrimSpace(coID) == "" && strings.TrimSpace(contentID) != "" {
		coID = contentID
		contentID = ""
	}
	if linkID == "" {
		linkID = parse139LinkIDFromFlag(flag)
	}
	if linkID == "" {
		return "", "", errors.New("missing linkID (from id/flag)")
	}
	if contentID == "" && coID == "" {
		return "", "", errors.New("missing contentId/coID (from id)")
	}
	if strings.TrimSpace(coID) != "" {
		playURL, _ = getContentInfoFromOutLinkPresentURL(linkID, coID)
	}
	u, derr := yun139DownloadURL(database, flag, id)
	if derr == nil && strings.TrimSpace(u) != "" {
		downloadURL = u
	}
	if strings.TrimSpace(playURL) != "" {
		return downloadURL, playURL, nil
	}
	if derr != nil {
		return "", "", derr
	}
	if strings.TrimSpace(downloadURL) != "" {
		return downloadURL, "", nil
	}
	return "", "", errors.New("url unavailable")
}

func HandleAPI139List(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	var body struct {
		Flag     string `json:"flag"`
		Passcode string `json:"passcode"`
	}
	_ = readJSONLoose(r, &body)
	flag := strings.TrimSpace(body.Flag)
	passcode := strings.TrimSpace(body.Passcode)
	linkID := parse139LinkIDFromFlag(flag)
	if linkID == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": "missing/invalid flag (expected: 逸动-<linkID>)"})
		return
	}
	normalizedFlag := "逸动-" + linkID
	key := linkID + "|" + passcode
	val, fromCache, err := yun139ListCache.Do(key, func() (yun139ListAPIValue, error) {
		vod, _, err := Yun139List(database, normalizedFlag, passcode)
		if err != nil {
			return yun139ListAPIValue{}, err
		}
		return yun139ListAPIValue{Vod: vod}, nil
	})
	if err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": err.Error()})
		return
	}
	writeJSON(w, 200, map[string]any{"ok": true, "vod_play_url": val.Vod, "cache": fromCache})
}

func HandleAPI139Play(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	var body struct {
		Flag string `json:"flag"`
		ID   string `json:"id"`
		Want string `json:"want"`
	}
	_ = readJSONLoose(r, &body)
	flag := strings.TrimSpace(body.Flag)
	id := strings.TrimSpace(body.ID)
	want := strings.ToLower(strings.TrimSpace(body.Want))
	if want == "" {
		want = "download_url"
	}
	if want != "download_url" && want != "play_url" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": "invalid want (expected: download_url|play_url)"})
		return
	}
	if id == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": "missing id"})
		return
	}

	cacheKey := buildPlayCacheKey("139", flag, id, want)
	if u, _, ok := getPlayCache(cacheKey); ok {
		writeJSON(w, 200, map[string]any{"ok": true, "url": u})
		return
	}

	linkID, contentID, coID := parse139PlayID(id)
	isStarID := strings.Contains(id, "*") && !strings.Contains(id, "|")
	if isStarID && strings.TrimSpace(coID) == "" && strings.TrimSpace(contentID) != "" {
		coID = contentID
		contentID = ""
	}
	if linkID == "" {
		linkID = parse139LinkIDFromFlag(flag)
	}
	if linkID == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": "missing linkID (from id/flag)"})
		return
	}
	if want == "play_url" {
		if strings.TrimSpace(coID) == "" {
			writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": "want=play_url requires contentId (expected id: <coID>*<linkID>)"})
			return
		}
		u, err := getContentInfoFromOutLinkPresentURL(linkID, coID)
		if err != nil || strings.TrimSpace(u) == "" {
			writeJSON(w, http.StatusBadGateway, map[string]any{"ok": false, "message": "play url unavailable"})
			return
		}
		setPlayCache(cacheKey, u, nil)
		writeJSON(w, 200, map[string]any{"ok": true, "url": strings.TrimSpace(u)})
		return
	}
	u, err := yun139DownloadURL(database, flag, id)
	if err != nil || strings.TrimSpace(u) == "" {
		if err != nil {
			writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": err.Error()})
		} else {
			writeJSON(w, http.StatusBadGateway, map[string]any{"ok": false, "message": "download url unavailable"})
		}
		return
	}
	setPlayCache(cacheKey, u, nil)
	writeJSON(w, 200, map[string]any{"ok": true, "url": strings.TrimSpace(u)})
}
