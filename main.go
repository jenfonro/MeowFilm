package main

import (
	"context"
	"flag"
	"log"
	"net/http"
	"os"
	"os/signal"
	"syscall"
	"time"

	"github.com/jenfonro/meowfilm/server"
	"github.com/jenfonro/meowfilm/server/static"
)

func main() {
	var addr string
	flag.StringVar(&addr, "addr", envDefault("MEOWFILM_ADDR", ":8080"), "listen address")
	flag.Parse()

	log.Printf("meowfilm version : %s", static.ServerVersion())

	s, err := server.New(server.Config{
		Addr:       addr,
		TrustProxy: os.Getenv("MEOWFILM_TRUST_PROXY") == "1",
	})
	if err != nil {
		log.Fatalf("init server: %v", err)
	}

	httpServer := &http.Server{
		Addr:              s.Addr(),
		Handler:           s.Handler(),
		ReadHeaderTimeout: 10 * time.Second,
	}

	go func() {
		log.Printf("meowfilm listening on %s", httpServer.Addr)
		if err := httpServer.ListenAndServe(); err != nil && err != http.ErrServerClosed {
			log.Fatalf("listen: %v", err)
		}
	}()

	stop := make(chan os.Signal, 1)
	signal.Notify(stop, syscall.SIGINT, syscall.SIGTERM)
	<-stop

	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	_ = httpServer.Shutdown(ctx)
	_ = s.Close()
}

func envDefault(key, def string) string {
	if v := os.Getenv(key); v != "" {
		return v
	}
	return def
}
