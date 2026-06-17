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
