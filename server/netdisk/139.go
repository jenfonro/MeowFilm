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
	"sort"
	"strconv"
	"strings"
	"time"

	"github.com/jenfonro/meowfilm/internal/db"
)

const (
	pan139OutlinkBase = "https://share-kd-njs.yun.139.com/yun-share/richlifeApp/devapp/IOutLink/"
	pan139UA          = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36"
	pan139DeviceInfo  = "||9|12.27.0|chrome|143.0.0.0|pda50460feabd10141fb59a3ba787afb||windows 10|1624X1305|zh-CN|||"
	pan139KeyStr      = "PVGDwmcvfs1uV3d1"
)


func parse139LinkIDFromFlag(flag string) string {
	s := strings.TrimSpace(flag)
	if s == "" {
		return ""
	}
	if strings.HasPrefix(s, "逸动-") {
		return strings.TrimSpace(strings.TrimPrefix(s, "逸动-"))
	}
	return ""
}

func stripBasicPrefix(v string) string {
	s := strings.TrimSpace(v)
	s = strings.TrimPrefix(strings.ToLower(s), "basic ")
	// keep original after trim "Basic " in any case
	if strings.HasPrefix(strings.TrimSpace(v), "Basic ") || strings.HasPrefix(strings.TrimSpace(v), "basic ") {
		return strings.TrimSpace(strings.TrimSpace(v)[len("Basic "):])
	}
	return strings.TrimSpace(s)
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

func encodeURIComponent(s string) string {
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
	encoded := encodeURIComponent(plainJSONBody)
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

func Yun139List(database *db.DB, flag string) (string, string, error) {
	linkID := parse139LinkIDFromFlag(flag)
	if linkID == "" {
		return "", "", errors.New("missing/invalid flag (expected: 逸动-<linkID>)")
	}
	store := readPanLoginSettings(database)
	auth := getPanField(store, "139", "authorization")
	if auth == "" {
		return "", "", errors.New("missing 139 authorization (pan_login_settings[\"139\"].authorization)")
	}
	account := decodeAccountFromAuthorization(auth)
	if account == "" {
		return "", "", errors.New("authorization invalid (missing account)")
	}
	payload := map[string]any{
		"getOutLinkInfoReq":  map[string]any{"account": account, "linkID": linkID, "pCaID": ""},
		"commonAccountInfo":  map[string]any{"account": account, "accountType": 1},
	}
	plainBytes, _ := json.Marshal(payload)
	plain := string(plainBytes)
	parsed, _, _, err := outlinkPostEncrypted("getOutLinkInfoV6", plain, auth)
	if err != nil {
		return "", "", err
	}
	code := ""
	if v, ok := parsed["code"]; ok {
		code = toString(v)
	} else if v, ok := parsed["resultCode"]; ok {
		code = toString(v)
	}
	if strings.TrimSpace(code) != "" && strings.TrimSpace(code) != "0" {
		desc := toString(parsed["desc"])
		if desc == "" {
			desc = toString(parsed["message"])
		}
		return "", linkID, errors.New(code + ": " + strings.TrimSpace(desc))
	}

	parts := []string{}
	for _, it := range pickOutlinkCoList(parsed) {
		name := strings.TrimSpace(toString(it["coName"]))
		if name == "" {
			name = strings.TrimSpace(toString(it["name"]))
		}
		if name == "" {
			name = strings.TrimSpace(toString(it["fileName"]))
		}
		coID := strings.TrimSpace(toString(it["coID"]))
		if coID == "" {
			coID = strings.TrimSpace(toString(it["coId"]))
		}
		if coID == "" {
			coID = strings.TrimSpace(toString(it["id"]))
		}
		if name == "" || coID == "" {
			continue
		}
		id := linkID + "||" + coID + "|" + name
		parts = append(parts, name+"$"+id)
	}
	return strings.Join(parts, "#"), linkID, nil
}

func parse139PlayID(id string) (linkID string, contentID string, coID string) {
	raw := strings.TrimSpace(id)
	if raw == "" {
		return "", "", ""
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

func Yun139Play(database *db.DB, flag string, id string) (string, error) {
	linkID, contentID, coID := parse139PlayID(id)
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
				"commonAccountInfo": map[string]any{"account": account, "accountType": 1},
			}
		} else {
			payload = map[string]any{
				"dlFromOutLinkReq":   map[string]any{"contentId": contentID, "linkID": linkID, "account": account},
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

func HandleAPI139List(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	var body struct {
		Flag string `json:"flag"`
	}
	_ = readJSONLoose(r, &body)
	flag := strings.TrimSpace(body.Flag)
	vod, linkID, err := Yun139List(database, flag)
	if err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": err.Error()})
		return
	}
	writeJSON(w, 200, map[string]any{"ok": true, "flag": flag, "linkId": linkID, "vod_play_url": vod})
}

func HandleAPI139Play(w http.ResponseWriter, r *http.Request, database *db.DB) {
	if r.Method != http.MethodPost {
		methodNotAllowed(w)
		return
	}
	var body struct {
		Flag string `json:"flag"`
		ID   string `json:"id"`
	}
	_ = readJSONLoose(r, &body)
	flag := strings.TrimSpace(body.Flag)
	id := strings.TrimSpace(body.ID)
	if id == "" {
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": "missing id"})
		return
	}
	u, err := Yun139Play(database, flag, id)
	if err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"ok": false, "message": err.Error()})
		return
	}
	writeJSON(w, 200, map[string]any{"ok": true, "parse": 0, "url": u})
}
