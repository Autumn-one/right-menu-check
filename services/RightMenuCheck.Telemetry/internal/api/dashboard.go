package api

import (
	_ "embed"
	"net/http"
)

//go:embed assets/dashboard.html
var dashboardHTML []byte

//go:embed assets/dashboard.css
var dashboardCSSContent []byte

//go:embed assets/dashboard.js
var dashboardJSContent []byte

//go:embed assets/RightMenuCheck.png
var dashboardLogoContent []byte

func (s *Server) dashboard(response http.ResponseWriter, request *http.Request) {
	if request.URL.Path != "/" {
		http.NotFound(response, request)
		return
	}
	serveDashboardAsset(response, request, "text/html; charset=utf-8", dashboardHTML)
}

func (s *Server) dashboardCSS(response http.ResponseWriter, request *http.Request) {
	serveDashboardAsset(response, request, "text/css; charset=utf-8", dashboardCSSContent)
}

func (s *Server) dashboardJS(response http.ResponseWriter, request *http.Request) {
	serveDashboardAsset(
		response, request, "text/javascript; charset=utf-8", dashboardJSContent)
}

func (s *Server) dashboardLogo(response http.ResponseWriter, request *http.Request) {
	serveDashboardAsset(response, request, "image/png", dashboardLogoContent)
}

func serveDashboardAsset(
	response http.ResponseWriter,
	request *http.Request,
	contentType string,
	content []byte,
) {
	if request.Method != http.MethodGet && request.Method != http.MethodHead {
		methodNotAllowed(response, http.MethodGet)
		return
	}
	response.Header().Set("Content-Type", contentType)
	response.WriteHeader(http.StatusOK)
	if request.Method == http.MethodGet {
		_, _ = response.Write(content)
	}
}
