.PHONY: help build build-release test clean pack install uninstall reinstall publish run ui web web-dev setup all

VERSION   := $(shell cat version)
NUPKG_DIR := ./nupkgs
DIST_DIR  := ./dist
TOOL_NAME := sfx
PKG_ID    := sfx
SOLUTION  := Sfx.slnx
CLI_PROJ  := src/Sfx/Sfx.csproj
RID       ?= $(shell dotnet --info | awk '/^ *RID:/ {print $$2; exit}')

help: ## Show this help message
	@echo 'Usage: make [target]'
	@echo ''
	@echo 'Available targets:'
	@grep -E '^[a-zA-Z_-]+:.*?## .*$$' $(MAKEFILE_LIST) | awk 'BEGIN {FS = ":.*?## "}; {printf "  %-18s %s\n", $$1, $$2}'

build: ## Build the solution in Debug mode
	dotnet build $(SOLUTION) -c Debug

build-release: ## Build the solution in Release mode
	dotnet build $(SOLUTION) -c Release

test: ## Run tests (set SFX_TEST_CLUSTER=<endpoint> to include the cluster tests)
	dotnet test $(SOLUTION)

run: ## Run sfx from source; pass arguments with ARGS="..."
	dotnet run --project $(CLI_PROJ) -- $(ARGS)

ui: ## Run sfx ui from source on port 7080, rebuilding the web UI first; pass arguments with ARGS="..."
	cd web && bun install && bun run build
	dotnet run --project $(CLI_PROJ) -- ui --port 7080 $(ARGS)

web: ## Build the web UI (web/dist), which the next dotnet build embeds
	cd web && bun install && bun run build

web-dev: ## Serve the web UI with hot reload, against a running `make ui`
	cd web && bun install && bun run dev

clean: ## Clean build artifacts
	dotnet clean $(SOLUTION) || true
	rm -rf $(NUPKG_DIR) $(DIST_DIR) web/dist
	rm -rf */*/bin */*/obj

pack: ## Pack sfx as a dotnet tool package
	dotnet pack $(CLI_PROJ) -c Release -o $(NUPKG_DIR)
	@echo ""
	@echo "Package created: $(NUPKG_DIR)/$(PKG_ID).$(VERSION).nupkg"

publish: ## Publish a self-contained single-file binary for RID (default: this machine)
	dotnet publish $(CLI_PROJ) -c Release -r $(RID) --self-contained \
		-p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none \
		-o $(DIST_DIR)/$(RID)
	@echo ""
	@echo "Binary: $(DIST_DIR)/$(RID)/$(TOOL_NAME)"

install: pack ## Install the tool globally
	dotnet tool uninstall -g $(PKG_ID) 2>/dev/null || true
	dotnet tool install --global --add-source $(NUPKG_DIR) $(PKG_ID) --version $(VERSION)
	@echo ""
	@echo "Installed! Use: $(TOOL_NAME)"
	@echo "If not found, add ~/.dotnet/tools to your PATH:"
	@echo "  echo 'export PATH=\"\$$PATH:\$$HOME/.dotnet/tools\"' >> ~/.zprofile && source ~/.zprofile"

uninstall: ## Uninstall the global tool
	dotnet tool uninstall -g $(PKG_ID)

reinstall: uninstall install ## Reinstall the tool (clean install)

setup: ## Restore packages
	dotnet restore $(SOLUTION)
	cd web && bun install

all: clean build test ## Clean, build, and test
