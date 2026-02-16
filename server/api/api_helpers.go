package api

import (
	"strings"

	mfnet "github.com/jenfonro/meowfilm/server/net"
)

type goProxyServer struct {
	Name        string      `json:"name"`
	DisplayName string      `json:"displayName"`
	Base        string      `json:"base"`
	Pans        goProxyPans `json:"pans"`
}

type goProxyPans struct {
	Baidu bool `json:"baidu"`
	Quark bool `json:"quark"`
}

func normalizeGoProxyServers(value string) []goProxyServer {
	servers := mfnet.NormalizeGoProxyServers(value)
	out := make([]goProxyServer, 0, len(servers))
	for _, s := range servers {
		out = append(out, goProxyServer{
			Name:        s.Name,
			DisplayName: s.DisplayName,
			Base:        s.Base,
			Pans:        goProxyPans{Baidu: s.Pans.Baidu, Quark: s.Pans.Quark},
		})
	}
	return out
}

func normalizeContentKey(s string) string {
	return strings.TrimSpace(strings.ToLower(strings.Join(strings.Fields(s), "")))
}

func defaultString(v, def string) string {
	return mfnet.DefaultString(v, def)
}

func normalizeHTTPBase(value string) string {
	return mfnet.NormalizeHTTPBase(value)
}
