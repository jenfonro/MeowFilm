package catpawopen

import (
	"bytes"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"net/url"
	"strings"
	"time"
)

func RequestSpider(apiBase string, spiderAPI string, action string, payload any) (map[string]any, error) {
	return RequestSpiderWithTimeout(apiBase, spiderAPI, action, payload, 12*time.Second)
}

func RequestSpiderWithTimeout(apiBase string, spiderAPI string, action string, payload any, timeout time.Duration) (map[string]any, error) {
	base := NormalizeAPIBase(apiBase)
	if base == "" {
		return nil, errors.New("CatPawOpen 接口地址未设置")
	}
	act := strings.TrimSpace(action)
	sp := strings.TrimSpace(spiderAPI)
	if act == "" || sp == "" {
		return nil, errors.New("invalid args")
	}

	// spiderAPI is typically like "/spider/xxx" or "/<id>/spider/xxx"
	spiderPath := strings.TrimSuffix(sp, "/")
	target, err := url.Parse(base)
	if err != nil {
		return nil, errors.New("CatPawOpen base invalid")
	}
	target, _ = target.Parse(strings.TrimPrefix(spiderPath, "/") + "/" + url.PathEscape(act))

	body := payload
	if body == nil {
		body = map[string]any{}
	}
	b, _ := json.Marshal(body)
	req, _ := http.NewRequest(http.MethodPost, target.String(), bytes.NewReader(b))
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("Accept", "application/json")

	if timeout <= 0 {
		timeout = 12 * time.Second
	}
	client := &http.Client{Timeout: timeout}
	resp, err := client.Do(req)
	if err != nil {
		return nil, err
	}
	defer func() { _ = resp.Body.Close() }()
	var out map[string]any
	_ = json.NewDecoder(resp.Body).Decode(&out)
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		msg := "请求失败"
		if out != nil {
			if m, ok := out["message"].(string); ok && strings.TrimSpace(m) != "" {
				msg = strings.TrimSpace(m)
			}
		}
		return nil, fmt.Errorf("%s (http %d)", msg, resp.StatusCode)
	}
	if out == nil {
		out = map[string]any{}
	}
	return out, nil
}

func RequestPlay(apiBase string, tvUser string, payload any) (map[string]any, error) {
	base := NormalizeAPIBase(apiBase)
	if base == "" {
		return nil, errors.New("CatPawOpen 接口地址未设置")
	}
	target, _ := url.Parse(base)
	target, _ = target.Parse("play")

	body := payload
	if body == nil {
		body = map[string]any{}
	}
	b, _ := json.Marshal(body)
	req, _ := http.NewRequest(http.MethodPost, target.String(), bytes.NewReader(b))
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("Accept", "application/json")
	if strings.TrimSpace(tvUser) != "" {
		req.Header.Set("X-TV-User", strings.TrimSpace(tvUser))
	}

	client := &http.Client{Timeout: 15 * time.Second}
	resp, err := client.Do(req)
	if err != nil {
		return nil, err
	}
	defer func() { _ = resp.Body.Close() }()
	var out map[string]any
	_ = json.NewDecoder(resp.Body).Decode(&out)
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		msg := "请求失败"
		if out != nil {
			if m, ok := out["message"].(string); ok && strings.TrimSpace(m) != "" {
				msg = strings.TrimSpace(m)
			}
		}
		return nil, fmt.Errorf("%s (http %d)", msg, resp.StatusCode)
	}
	if out == nil {
		out = map[string]any{}
	}
	return out, nil
}
