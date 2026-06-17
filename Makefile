SHELL := /bin/bash

BUNDLED_DOTNET := /Users/richardydani/Documents/Codex/2026-06-14/files-mentioned-by-the-user-implementation/work/dotnet-sdk/dotnet
ifneq ($(wildcard $(BUNDLED_DOTNET)),)
DOTNET ?= $(BUNDLED_DOTNET)
else
DOTNET ?= dotnet
endif

BUILD ?= dev
STAMP ?= $(shell date +%Y-%m-%d).$(BUILD)
CONFIG ?= Release
RID ?= win-x64

PROJECT := src/PoeAncientsPriceHelper/PoeAncientsPriceHelper.csproj
TEST_PROJECT := src/PoeAncientsPriceHelper.Tests/PoeAncientsPriceHelper.Tests.csproj
APP_EXE := NotAloneExile.exe
BUILD_INFO := src/PoeAncientsPriceHelper/BuildInfo.cs

OUT_ROOT ?= /Users/richardydani/Documents/Codex/2026-06-14/files-mentioned-by-the-user-implementation/work
PUBLISH_NAME := publish-win-x64-framework-$(BUILD)
PUBLISH_DIR := $(OUT_ROOT)/$(PUBLISH_NAME)

REMOTE_HOST ?= codex_ssh@192.168.0.16
SSH_KEY ?= $(HOME)/.ssh/codex_pc_ed25519
REMOTE_ROOT ?= /C:/Users/richa/Documents/codex/ssh-debug/runeshape-current
REMOTE_APP := $(REMOTE_ROOT)/$(PUBLISH_NAME)
REMOTE_CONTROL := $(REMOTE_ROOT)/control

.PHONY: help check-vars compile publish upload restart deploy stamp

help:
	@printf '%s\n' \
		'Targets:' \
		'  make deploy BUILD=64          Compile, publish, upload, and restart the app over SFTP.' \
		'  make stamp BUILD=64           Update BuildInfo.Stamp to today/build number.' \
		'  make publish BUILD=64         Create a framework-dependent win-x64 publish folder.' \
		'  make upload BUILD=64          Upload the publish folder to the Windows machine.' \
		'  make restart BUILD=64         Ask the Windows bridge to restart that uploaded build.' \
		'' \
		'Useful variables:' \
		'  BUILD=N                       Test build folder suffix, e.g. publish-win-x64-framework-N.' \
		'  DOTNET=/path/to/dotnet        Override the SDK.' \
		'  REMOTE_HOST=user@host         Override SSH/SFTP target.' \
		'  SSH_KEY=/path/to/key          Override private key.'

check-vars:
	@test "$(BUILD)" != "dev" || { echo 'Set BUILD=N, for example: make deploy BUILD=64'; exit 2; }
	@test -x "$(DOTNET)" || command -v "$(DOTNET)" >/dev/null || { echo 'DOTNET not found: $(DOTNET)'; exit 2; }
	@test -f "$(SSH_KEY)" || { echo 'SSH key not found: $(SSH_KEY)'; exit 2; }

compile:
	"$(DOTNET)" build "$(TEST_PROJECT)" -p:EnableWindowsTargeting=true

publish: check-vars compile
	rm -rf "$(PUBLISH_DIR)"
	"$(DOTNET)" publish "$(PROJECT)" \
		-c "$(CONFIG)" \
		-r "$(RID)" \
		--self-contained false \
		-p:EnableWindowsTargeting=true \
		-o "$(PUBLISH_DIR)"
	@echo "Published $(PUBLISH_DIR)"

upload: check-vars publish
	@batch="$$(mktemp -t runeshape-upload.XXXXXX)"; \
	log="$$(mktemp -t runeshape-upload-log.XXXXXX)"; \
	{ \
		printf -- '-mkdir "%s"\n' "$(REMOTE_APP)"; \
		find "$(PUBLISH_DIR)" -type d | sort | while IFS= read -r dir; do \
			rel="$${dir#$(PUBLISH_DIR)}"; \
			rel="$${rel#/}"; \
			if [ -n "$$rel" ]; then \
				printf -- '-mkdir "%s/%s"\n' "$(REMOTE_APP)" "$$rel"; \
			fi; \
		done; \
		find "$(PUBLISH_DIR)" -type f | sort | while IFS= read -r file; do \
			rel="$${file#$(PUBLISH_DIR)/}"; \
			printf 'put -p "%s" "%s/%s"\n' "$$file" "$(REMOTE_APP)" "$$rel"; \
		done; \
	} > "$$batch"; \
	sftp -i "$(SSH_KEY)" -o IdentitiesOnly=yes -o BatchMode=yes -b "$$batch" "$(REMOTE_HOST)" 2>&1 | tee "$$log"; \
	if grep -E 'dest open .*: Failure|File .* not found|Connection closed|Permission denied' "$$log" >/dev/null; then \
		rm -f "$$batch" "$$log"; \
		echo 'Upload failed. Use a fresh BUILD number if the target build is currently running.'; \
		exit 1; \
	fi; \
	rm -f "$$batch" "$$log"
	@echo "Uploaded $(REMOTE_APP)"

restart: check-vars
	@tmp="$$(mktemp -d -t runeshape-restart.XXXXXX)"; \
	control_file="$$(date -u +%Y%m%dT%H%M%SZ)-restart-runeshape-$(BUILD).json"; \
	printf '{\n  "action": "restart",\n  "executable": "%s",\n  "arguments": ["--debug"]\n}\n' "$(PUBLISH_NAME)\\\\$(APP_EXE)" > "$$tmp/restart.json"; \
	sftp -i "$(SSH_KEY)" -o IdentitiesOnly=yes -o BatchMode=yes "$(REMOTE_HOST)" <<< "put \"$$tmp/restart.json\" \"$(REMOTE_CONTROL)/$$control_file\""; \
	rm -rf "$$tmp"
	@echo "Restart requested for $(PUBLISH_NAME)"

deploy: upload restart

stamp: check-vars
	perl -0pi -e 's/public const string Stamp = "[^"]+";/public const string Stamp = "$(STAMP)";/' "$(BUILD_INFO)"
	@echo "Stamped $(BUILD_INFO) as $(STAMP)"

# ─────────────────────────────────────────────────────────────────────────
# POC: Electron overlay shell + C# sidecar (new architecture).
# Same SSH/SFTP bridge as the WPF deploy. The packaged Electron app bundles
# its own Chromium+Node, and the sidecar is a framework-dependent win-x64
# .exe (the Windows box already runs the .NET 8 Desktop runtime for the WPF
# app, which includes the base runtime the sidecar needs) — so there is
# NOTHING to install on the target. Folders are versioned so a running build
# never locks files mid-upload (use a fresh BUILD=N each deploy).
# ─────────────────────────────────────────────────────────────────────────
POC_APP_NAME     := runeshape-overlay-shell
POC_SIDECAR_PROJ := src/PoeAncientsSidecar/PoeAncientsSidecar.csproj
POC_SHELL_DIR    := poc/overlay-shell
POC_PACKAGE_NAME := $(POC_APP_NAME)-win32-x64-$(BUILD)
POC_DIST         := $(OUT_ROOT)/$(POC_PACKAGE_NAME)
POC_REMOTE_APP   := $(REMOTE_ROOT)/$(POC_PACKAGE_NAME)
POC_EXE          := $(POC_APP_NAME).exe
POC_SIDECAR_DIR  := $(POC_DIST)/resources/sidecar

.PHONY: poc-help poc-package poc-upload poc-restart poc-deploy

poc-help:
	@printf '%s\n' \
		'POC targets (new architecture: Electron + C# sidecar):' \
		'  make poc-deploy BUILD=67    Package, upload, and launch on Windows.' \
		'  make poc-package BUILD=67   Build the win-x64 package locally (no upload).' \
		'  make poc-upload BUILD=67    Upload a built package.' \
		'  make poc-restart BUILD=67   Tell the bridge to launch the POC.'

poc-package:
	@echo "==> Packaging Electron shell for win32-x64 into $(POC_DIST)"
	rm -rf "$(POC_DIST)" /tmp/poc-pkg-$(BUILD)
	mkdir -p "$(POC_DIST)"
	cd "$(POC_SHELL_DIR)" && npx --yes @electron/packager@latest . "$(POC_APP_NAME)" \
		--platform=win32 --arch=x64 \
		--out="/tmp/poc-pkg-$(BUILD)" --overwrite --prune --asar
	cp -R "/tmp/poc-pkg-$(BUILD)/$(POC_APP_NAME)-win32-x64/." "$(POC_DIST)/"
	rm -rf "/tmp/poc-pkg-$(BUILD)"
	@echo "==> Publishing sidecar (win-x64) into $(POC_SIDECAR_DIR)"
	"$(DOTNET)" publish "$(POC_SIDECAR_PROJ)" -c "$(CONFIG)" -r "$(RID)" \
		--self-contained false -p:EnableWindowsTargeting=true -o "$(POC_SIDECAR_DIR)"
	@echo "==> Package ready: $(POC_DIST)/$(POC_EXE)"

poc-upload: check-vars poc-package
	@echo "==> Uploading POC to $(POC_REMOTE_APP)"
	@batch="$$(mktemp -t poc-upload.XXXXXX)"; \
	log="$$(mktemp -t poc-upload-log.XXXXXX)"; \
	{ \
		printf -- '-mkdir "%s"\n' "$(POC_REMOTE_APP)"; \
		find "$(POC_DIST)" -type d | sort | while IFS= read -r dir; do \
			rel="$${dir#$(POC_DIST)}"; rel="$${rel#/}"; \
			[ -n "$$rel" ] && printf -- '-mkdir "%s/%s"\n' "$(POC_REMOTE_APP)" "$$rel"; \
		done; \
		find "$(POC_DIST)" -type f | sort | while IFS= read -r file; do \
			rel="$${file#$(POC_DIST)/}"; \
			printf 'put -p "%s" "%s/%s"\n' "$$file" "$(POC_REMOTE_APP)" "$$rel"; \
		done; \
	} > "$$batch"; \
	sftp -i "$(SSH_KEY)" -o IdentitiesOnly=yes -o BatchMode=yes -b "$$batch" "$(REMOTE_HOST)" 2>&1 | tee "$$log"; \
	if grep -E 'dest open .*: Failure|File .* not found|Connection closed|Permission denied' "$$log" >/dev/null; then \
		rm -f "$$batch" "$$log"; echo 'Upload failed. Use a fresh BUILD number if the POC is currently running.'; exit 1; \
	fi; \
	rm -f "$$batch" "$$log"
	@echo "==> Uploaded $(POC_REMOTE_APP)"

poc-restart: check-vars
	@tmp="$$(mktemp -d -t poc-restart.XXXXXX)"; \
	control_file="$$(date -u +%Y%m%dT%H%M%SZ)-restart-poc-$(BUILD).json"; \
	printf '{\n  "action": "restart",\n  "executable": "%s\\\\$(POC_EXE)",\n  "arguments": []\n}\n' "$(POC_PACKAGE_NAME)" > "$$tmp/restart.json"; \
	sftp -i "$(SSH_KEY)" -o IdentitiesOnly=yes -o BatchMode=yes "$(REMOTE_HOST)" <<< "put \"$$tmp/restart.json\" \"$(REMOTE_CONTROL)/$$control_file\""; \
	rm -rf "$$tmp"
	@echo "==> Restart requested for $(POC_PACKAGE_NAME)"

poc-deploy: poc-upload poc-restart
