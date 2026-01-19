package server

import (
	"errors"
	"net/http"
	"os"
	"strings"

	"github.com/jenfonro/TV_Server/internal/auth"
	"github.com/jenfonro/TV_Server/internal/db"
	"github.com/jenfonro/TV_Server/server/routes"
	"github.com/jenfonro/TV_Server/server/static"
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
}

func New(cfg Config) (*Server, error) {
	if strings.TrimSpace(cfg.Addr) == "" {
		return nil, errors.New("addr is required")
	}

	database, err := db.Open()
	if err != nil {
		return nil, err
	}

	authMw := auth.New(database, auth.Options{
		TrustProxy:   cfg.TrustProxy,
		CookieSecure: os.Getenv("TV_SERVER_COOKIE_SECURE") == "1",
	})

	mux := http.NewServeMux()

	mux.Handle("/api/", routes.APIHandler(database, authMw))
	dashboardAPI := routes.DashboardHandler(database, authMw)
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
	handler := static.NoStoreForHTMLCSSJS(root)

	return &Server{addr: cfg.Addr, db: database, mux: mux, h: handler}, nil
}

func (s *Server) Addr() string { return s.addr }
func (s *Server) Handler() http.Handler { return s.h }

func (s *Server) Close() error {
	if s == nil || s.db == nil {
		return nil
	}
	return s.db.Close()
}
