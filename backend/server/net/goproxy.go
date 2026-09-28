package net

import (
	"encoding/json"
	"net/url"
	"strings"
)

type GoProxyServer struct {
	Name        string      `json:"name"`
	DisplayName string      `json:"displayName"`
	Base        string      `json:"base"`
	Pans        GoProxyPans `json:"pans"`
}

type GoProxyPans struct {
	Baidu bool `json:"baidu"`
	Quark bool `json:"quark"`
}

func NormalizeGoProxyServers(value string) []GoProxyServer {
	var list []any
	if err := json.Unmarshal([]byte(value), &list); err != nil {
		return []GoProxyServer{}
	}
	out := []GoProxyServer{}
	seen := map[string]struct{}{}
	for _, it := range list {
		var base string
		var name string
		var displayName string
		var pans map[string]any
		switch vv := it.(type) {
		case string:
			base = NormalizeHTTPBase(vv)
		case map[string]any:
			if n, ok := vv["name"].(string); ok {
				name = strings.TrimSpace(n)
			}
			if n, ok := vv["displayName"].(string); ok {
				displayName = strings.TrimSpace(n)
			}
			if b, ok := vv["base"].(string); ok {
				base = NormalizeHTTPBase(b)
			}
			if base == "" {
				if b, ok := vv["apiBase"].(string); ok {
					base = NormalizeHTTPBase(b)
				} else if b, ok := vv["api"].(string); ok {
					base = NormalizeHTTPBase(b)
				} else if b, ok := vv["url"].(string); ok {
					base = NormalizeHTTPBase(b)
				}
			}
			if p, ok := vv["pans"].(map[string]any); ok {
				pans = p
			}
		}
		if base == "" {
			continue
		}
		key := strings.ToLower(base)
		if _, ok := seen[key]; ok {
			continue
		}
		seen[key] = struct{}{}
		if name == "" {
			if u, err := url.Parse(base); err == nil {
				name = strings.TrimSpace(u.Hostname())
				if name == "" {
					name = strings.TrimSpace(u.Host)
				}
			}
		}
		if displayName == "" {
			displayName = name
		}
		baidu := true
		quark := true
		if pans != nil {
			if v, ok := pans["baidu"]; ok {
				baidu = ParseAnyBool(v, true)
			}
			if v, ok := pans["quark"]; ok {
				quark = ParseAnyBool(v, true)
			}
		}
		out = append(out, GoProxyServer{
			Name:        name,
			DisplayName: displayName,
			Base:        base,
			Pans:        GoProxyPans{Baidu: baidu, Quark: quark},
		})
	}
	return out
}
