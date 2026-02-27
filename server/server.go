package server

import (
	"errors"
	"fmt"
	"net/http"
	"os"
	"strings"
	"sync"
	"time"

	"github.com/jenfonro/meowfilm/internal/auth"
	"github.com/jenfonro/meowfilm/internal/buildinfo"
	"github.com/jenfonro/meowfilm/internal/db"
	"github.com/jenfonro/meowfilm/internal/limit"
	"github.com/jenfonro/meowfilm/server/api"
	"github.com/jenfonro/meowfilm/server/dashboard"
	"github.com/jenfonro/meowfilm/server/emby"
	"github.com/jenfonro/meowfilm/server/static"
)

type Config struct {
	Addr       string
	TrustProxy bool
}

type Server struct {
	addr string
	db   *db.DB
	mux  *http.ServeMux
	h    http.Handler
	stop chan struct{}
	once sync.Once
}

func New(cfg Config) (*Server, error) {
	if strings.TrimSpace(cfg.Addr) == "" {
		return nil, errors.New("addr is required")
	}

	database, err := db.Open()
	if err != nil {
		return nil, err
	}

	if wm := buildinfo.WatermarkTrim(); wm != "" {
		_, _ = fmt.Fprintf(os.Stderr, "build=%s\n", wm)
	}

	authMw := auth.New(database, auth.Options{
		TrustProxy:   cfg.TrustProxy,
		CookieSecure: os.Getenv("MEOWFILM_COOKIE_SECURE") == "1",
	})

	mux := http.NewServeMux()

	mux.Handle("/api/", api.Handler(database, authMw))
	// Debug endpoints (enabled only when MEOWFILM_DEBUG=1).
	mux.Handle("/listdebug", emby.RegexListDebugHandler(database))
	mux.Handle("/searchdebug", emby.RegexSearchDebugHandler(database))
	embyAPI := emby.EmbyHandler(database)
	// Emby is the canonical API surface.
	mux.Handle("/emby/", embyAPI)
	dashboardAPI := dashboard.Handler(database, authMw)
	staticHandler := static.Handler(authMw)
	mux.Handle("/dashboard/", http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path == "/dashboard/" {
			staticHandler.ServeHTTP(w, r)
			return
		}
		dashboardAPI.ServeHTTP(w, r)
	}))
	mux.Handle("/", static.Handler(authMw))

	root := authMw.Middleware(mux)
	if wm := buildinfo.WatermarkTrim(); wm != "" {
		next := root
		root = http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			w.Header().Set(limit.HeaderWatermarkKey(), wm)
			next.ServeHTTP(w, r)
		})
	}
	handler := static.NoStoreForHTMLCSSJS(root)

	srv := &Server{addr: cfg.Addr, db: database, mux: mux, h: handler, stop: make(chan struct{})}
	srv.startSelfCheck()
	return srv, nil
}

func (s *Server) Addr() string          { return s.addr }
func (s *Server) Handler() http.Handler { return s.h }

func (s *Server) Close() error {
	if s == nil || s.db == nil {
		return nil
	}
	s.once.Do(func() {
		if s.stop != nil {
			close(s.stop)
		}
	})
	return s.db.Close()
}

func (s *Server) startSelfCheck() {
	if s == nil || s.db == nil || s.stop == nil {
		return
	}
	if !limit.Enabled() {
		return
	}
	go func() {
		t := time.NewTicker(30 * time.Second)
		defer t.Stop()
		for {
			select {
			case <-s.stop:
				return
			case <-t.C:
				ok, err := s.db.VerifyUsersTableShape()
				if err == nil && !ok {
					limit.Audit("ts", "u_shape")
				}
				ok2, err2 := s.db.VerifyUsersLimitTrigger()
				if err2 == nil && !ok2 {
					_ = s.db.RepairUsersLimitTrigger()
					limit.Audit("ts", "u_trg")
				}
			}
		}
	}()
}
