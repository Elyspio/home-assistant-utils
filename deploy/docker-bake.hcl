group "default" {
	targets = [
		"app"
	]
}

target "app" {
	context    = ".."
	dockerfile = "./deploy/Dockerfile"
	platforms  = [
		"linux/amd64", "linux/arm64"
	]
	tags       = [
		"elyspio/home-assistant-utils"
	]
}
