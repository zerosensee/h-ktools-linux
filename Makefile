# hktools for Linux — установка
#   sudo make install                 # в /usr/local
#   make install PREFIX=$HOME/.local  # для текущего пользователя (без root)
#   make install DESTDIR=$pkgdir PREFIX=/usr   # для PKGBUILD

PREFIX  ?= /usr/local
DESTDIR ?=
APP_ID  := io.github.zerosensee.HKTools
LIBDIR  := $(PREFIX)/lib/hktools
DATADIR := $(PREFIX)/share/hktools
PYTHON  ?= python3

.PHONY: all install uninstall test run check

all:
	@echo "Цели: install, uninstall, test, run, check"

install:
	install -d "$(DESTDIR)$(LIBDIR)"
	cp -r hktools "$(DESTDIR)$(LIBDIR)/"
	find "$(DESTDIR)$(LIBDIR)" -name '__pycache__' -prune -exec rm -rf {} +
	install -d "$(DESTDIR)$(PREFIX)/bin"
	printf '#!/bin/sh\nexport HKTOOLS_DATA_DIR="%s"\nexport PYTHONPATH="%s$${PYTHONPATH:+:$$PYTHONPATH}"\nexec %s -m hktools "$$@"\n' \
		"$(DATADIR)" "$(LIBDIR)" "$(PYTHON)" > "$(DESTDIR)$(PREFIX)/bin/hktools"
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

test:
	$(PYTHON) -m pytest -q tests/test_core.py

run:
	$(PYTHON) -m hktools

check:
	$(PYTHON) -m hktools deps
