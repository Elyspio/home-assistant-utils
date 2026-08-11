variable "TAG" {
  default = "2026.08.11.1"
}

group "default" {
  targets = ["app"]
}

target "app" {
  context    = ".."
  dockerfile = "./deploy/Dockerfile"
  platforms  = ["linux/amd64", "linux/arm64"]
  tags       = ["registry.elylan/elyspio/home-assistant-utils:${TAG}"]
}
