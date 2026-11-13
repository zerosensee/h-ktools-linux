# hktools (C# / .NET) — сборка и установка
#   make                               # сборка в build/hktools
#   sudo make install                  # в /usr/local
#   make install PREFIX=$HOME/.local   # для текущего пользователя (без root)
#   make install DESTDIR=$pkgdir PREFIX=/usr   # для PKGBUILD
#
# Нужен .NET SDK 8 (Arch: dotnet-sdk-8.0). Пакеты NuGet не используются — сборка работает без сети.

PREFIX   ?= /usr/local
DESTDIR  ?=
APP_ID   := io.github.zerosensee.HKTools
LIBDIR   := $(PREFIX)/lib/hktools
DATADIR  := $(PREFIX)/share/hktools
DOTNET   ?= dotnet
CONFIG   ?= Release
BUILDDIR := build/hktools
export DOTNET_CLI_TELEMETRY_OPTOUT := 1
export DOTNET_NOLOGO := 1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE := 1

.PHONY: all build install uninstall test smoke run check clean

all: build

build:
	$(DOTNET) publish src/HKTools.App/HKTools.App.csproj -c $(CONFIG) -o $(BUILDDIR) \
		-p:UseAppHost=false -p:DebugType=none -p:GenerateDocumentationFile=false

test:
	$(DOTNET) run --project tests/HKTools.SelfTest -c $(CONFIG)

# Дымовой тест интерфейса под Xvfb (снимки в build/smoke)
smoke: build
	HKTOOLS_DATA_DIR="$(CURDIR)/data" HKTOOLS_SMOKE="$(CURDIR)/build/smoke" GSK_RENDERER=cairo \
		xvfb-run -a -s "-screen 0 1400x900x24" $(DOTNET) $(BUILDDIR)/hktools.dll

install:
	@test -f $(BUILDDIR)/hktools.dll || { echo "Сначала выполните: make"; exit 1; }
	install -d "$(DESTDIR)$(LIBDIR)"
	cp -r $(BUILDDIR)/. "$(DESTDIR)$(LIBDIR)/"
	install -d "$(DESTDIR)$(PREFIX)/bin"
	printf '#!/bin/sh\nexport HKTOOLS_DATA_DIR="%s"\nexport DOTNET_CLI_TELEMETRY_OPTOUT=1\nexec dotnet "%s/hktools.dll" "$$@"\n' \
		"$(DATADIR)" "$(LIBDIR)" > "$(DESTDIR)$(PREFIX)/bin/hktools"
	chmod 755 "$(DESTDIR)$(PREFIX)/bin/hktools"
	install -Dm644 data/$(APP_ID).desktop "$(DESTDIR)$(PREFIX)/share/applications/$(APP_ID).desktop"
	install -Dm644 data/$(APP_ID).metainfo.xml "$(DESTDIR)$(PREFIX)/share/metainfo/$(APP_ID).metainfo.xml"
	for s in 16 32 48 64 128 256 512; do \
		install -Dm644 data/icons/hktools-$$s.png "$(DESTDIR)$(PREFIX)/share/icons/hicolor/$${s}x$${s}/apps/$(APP_ID).png"; \
	done
	install -Dm644 data/icons/hktools-256.png "$(DESTDIR)$(DATADIR)/icons/hktools-256.png"
	install -Dm644 data/hyprland/hktools.conf "$(DESTDIR)$(DATADIR)/hyprland/hktools.conf"
	install -Dm644 data/hyprland/hktools.lua "$(DESTDIR)$(DATADIR)/hyprland/hktools.lua"
	install -Dm644 data/waybar/hktools.jsonc "$(DESTDIR)$(DATADIR)/waybar/hktools.jsonc"
	install -Dm644 data/waybar/hktools.css "$(DESTDIR)$(DATADIR)/waybar/hktools.css"
	install -Dm644 data/filemanagers/hktools-dolphin.desktop "$(DESTDIR)$(PREFIX)/share/kio/servicemenus/hktools.desktop"
	install -Dm644 data/filemanagers/thunar-uca.xml "$(DESTDIR)$(DATADIR)/filemanagers/thunar-uca.xml"
	-[ -z "$(DESTDIR)" ] && gtk-update-icon-cache -q -t "$(PREFIX)/share/icons/hicolor" 2>/dev/null || true
	-[ -z "$(DESTDIR)" ] && update-desktop-database -q "$(PREFIX)/share/applications" 2>/dev/null || true
	@echo "Готово. Запуск: hktools   (проверка зависимостей: hktools deps)"

uninstall:
	rm -rf "$(DESTDIR)$(LIBDIR)" "$(DESTDIR)$(DATADIR)"
	rm -f "$(DESTDIR)$(PREFIX)/bin/hktools" "$(DESTDIR)$(PREFIX)/share/applications/$(APP_ID).desktop" \
	      "$(DESTDIR)$(PREFIX)/share/metainfo/$(APP_ID).metainfo.xml" "$(DESTDIR)$(PREFIX)/share/kio/servicemenus/hktools.desktop"
	for s in 16 32 48 64 128 256 512; do rm -f "$(DESTDIR)$(PREFIX)/share/icons/hicolor/$${s}x$${s}/apps/$(APP_ID).png"; done

run: build
	HKTOOLS_DATA_DIR="$(CURDIR)/data" $(DOTNET) $(BUILDDIR)/hktools.dll

check: build
	$(DOTNET) $(BUILDDIR)/hktools.dll deps

clean:
	rm -rf build src/*/bin src/*/obj tests/*/bin tests/*/obj
