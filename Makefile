SHELL := /bin/bash

# Prefer the bundled .NET SDK if present, else whatever `dotnet` is on PATH.
BUNDLED_DOTNET := /Users/richardydani/Documents/Codex/2026-06-14/files-mentioned-by-the-user-implementation/work/dotnet-sdk/dotnet
ifneq ($(wildcard $(BUNDLED_DOTNET)),)
DOTNET ?= $(BUNDLED_DOTNET)
else
DOTNET ?= dotnet
endif

CONFIG ?= Release
RID ?= win-x64

SIDECAR_PROJ := src/PoeAncientsSidecar/PoeAncientsSidecar.csproj
SHELL_DIR    := poc/overlay-shell
SIDECAR_OUT  := build/sidecar
DIST         := dist

.PHONY: help test sidecar installer release deploy clean

help:
	@printf '%s\n' \
		'Not Alone, Exile — Electron + C# sidecar.' \
		'' \
		'  make test                     Mac checks: detector tests, sidecar/test builds, JS syntax/tests.' \
		'  make sidecar                  Publish the C# sidecar (win-x64) into build/sidecar.' \
		'  make installer                Build the Windows NSIS installer into dist/ (no upload).' \
		'  make release                  Build + publish the installer to GitHub Releases.' \
		'                                 Needs GH_TOKEN; the app auto-updates from there.' \
		'  make deploy BUILD=67          SFTP the unpacked build to the Windows box + bridge-restart.' \
		'  make clean                    Remove build/ and dist/.' \
		'' \
		'Variables:' \
		'  DOTNET=/path/to/dotnet        Override the .NET SDK.' \
		'  GH_TOKEN=xxx                  Required by `make release`.'

# ── Mac verification (no Windows needed) ──────────────────────────────────
test:
	"$(DOTNET)" test src/PoeAncientsPriceHelper.Core.Tests
	"$(DOTNET)" build "$(SIDECAR_PROJ)" -p:EnableWindowsTargeting=true -p:BuildStamp=$(BUILD)
	"$(DOTNET)" build src/PoeAncientsPriceHelper.Tests -p:EnableWindowsTargeting=true -p:BuildStamp=$(BUILD)
	@cd "$(SHELL_DIR)" && for f in *.js; do node --check "$$f" && echo "ok $$f"; done
	@cd "$(SHELL_DIR)" && npm test

# ── Sidecar (framework-dependent win-x64) ────────────────────────────────
sidecar:
	"$(DOTNET)" publish "$(SIDECAR_PROJ)" -c $(CONFIG) -r $(RID) \
		--self-contained false -p:EnableWindowsTargeting=true -p:BuildStamp=$(BUILD) -o "$(SIDECAR_OUT)"
	@echo "==> Sidecar published to $(SIDECAR_OUT)"

# ── Electron packaging ────────────────────────────────────────────────────
# electron-builder reads package.json "build" and bundles build/sidecar -> resources/sidecar.
installer: sidecar
	cd "$(SHELL_DIR)" && npx electron-builder --win nsis
	@echo "==> Installer + latest.yml in $(DIST)"

# Publish to GitHub Releases. electron-updater reads latest.yml from the latest release, so every
# installed copy (yours + friends') auto-downloads the next version on launch.
release: sidecar
	cd "$(SHELL_DIR)" && npx electron-builder --win nsis -p always
	@echo "==> Published to GitHub Releases"

# ── Windows live-test deploy over the SSH/SFTP bridge ─────────────────────
REMOTE_HOST ?= codex_ssh@192.168.0.16
SSH_KEY ?= $(HOME)/.ssh/codex_pc_ed25519
REMOTE_ROOT ?= /C:/Users/richa/Documents/codex/ssh-debug/runeshape-current
REMOTE_CONTROL := $(REMOTE_ROOT)/control
BUILD ?= dev
DEPLOY_DIR := $(DIST)/nae-$(BUILD)
REMOTE_DEPLOY := $(REMOTE_ROOT)/nae-$(BUILD)
DEPLOY_EXE := not-alone-exile.exe

# @electron/packager (not electron-builder) is the proven transport to this box. The packaged exe
# name comes from package.json `name` (no spaces → bridge path-friendly). The sidecar is copied
# into resources/sidecar/ so main.js spawns it via process.resourcesPath.
deploy:
	@echo "==> Packaging app (win32-x64) via @electron/packager"
	rm -rf "$(DEPLOY_DIR)" /tmp/nae-pkg-$(BUILD)
	cd "$(SHELL_DIR)" && npx --yes @electron/packager@latest . \
		--platform=win32 --arch=x64 --out="/tmp/nae-pkg-$(BUILD)" --overwrite --prune --asar
	mkdir -p "$(DEPLOY_DIR)"
	cp -R /tmp/nae-pkg-$(BUILD)/*-win32-x64/. "$(DEPLOY_DIR)/"
	rm -rf /tmp/nae-pkg-$(BUILD)
	@echo "==> Publishing sidecar (win-x64) into resources/sidecar"
	"$(DOTNET)" publish "$(SIDECAR_PROJ)" -c $(CONFIG) -r $(RID) \
		--self-contained false -p:EnableWindowsTargeting=true -p:BuildStamp=$(BUILD) -o "$(DEPLOY_DIR)/resources/sidecar"
	@echo "==> Uploading $(DEPLOY_DIR) to $(REMOTE_DEPLOY)"
	@batch="$$(mktemp -t nae-upload.XXXXXX)"; \
	log="$$(mktemp -t nae-upload-log.XXXXXX)"; \
	{ \
		printf -- '-mkdir "%s"\n' "$(REMOTE_DEPLOY)"; \
		find "$(DEPLOY_DIR)" -type d | sort | while IFS= read -r dir; do \
			rel="$${dir#$(DEPLOY_DIR)}"; rel="$${rel#/}"; \
			[ -n "$$rel" ] && printf -- '-mkdir "%s/%s"\n' "$(REMOTE_DEPLOY)" "$$rel"; \
		done; \
		find "$(DEPLOY_DIR)" -type f | sort | while IFS= read -r file; do \
			rel="$${file#$(DEPLOY_DIR)/}"; \
			printf 'put -p "%s" "%s/%s"\n' "$$file" "$(REMOTE_DEPLOY)" "$$rel"; \
		done; \
	} > "$$batch"; \
	sftp -i "$(SSH_KEY)" -o IdentitiesOnly=yes -o BatchMode=yes -b "$$batch" "$(REMOTE_HOST)" 2>&1 | tee "$$log"; \
	rm -f "$$batch" "$$log"
	@echo "==> Requesting launch via bridge"
	@tmp="$$(mktemp -d -t nae-restart.XXXXXX)"; \
	cf="$$(date -u +%Y%m%dT%H%M%SZ)-restart-nae-$(BUILD).json"; \
	printf '{\n  "action": "restart",\n  "executable": "nae-$(BUILD)\\\\$(DEPLOY_EXE)"\n}\n' > "$$tmp/r.json"; \
	sftp -i "$(SSH_KEY)" -o IdentitiesOnly=yes -o BatchMode=yes "$(REMOTE_HOST)" <<< "put \"$$tmp/r.json\" \"$(REMOTE_CONTROL)/$$cf\""; \
	rm -rf "$$tmp"
	@echo "==> Launch requested: nae-$(BUILD)/$(DEPLOY_EXE) (no args — Electron exits if the bridge passes argv)"

clean:
	rm -rf "$(SIDECAR_OUT)" "$(DIST)"
