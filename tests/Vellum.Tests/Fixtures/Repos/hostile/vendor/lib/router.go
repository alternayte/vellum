package lib

import "net/http"

func init() {
	http.HandleFunc("/from-vendor", nil)
}
