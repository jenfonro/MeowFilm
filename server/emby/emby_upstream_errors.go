package emby

import (
	"errors"
	"net/http"
)

func embyBadGateway(w http.ResponseWriter, err error) {
	if w == nil {
		return
	}
	e := err
	if e == nil {
		e = errors.New("Bad Gateway")
	}
	embyWriteError(w, http.StatusBadGateway, e.Error())
}
