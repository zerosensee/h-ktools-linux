-- hktools — правила для Hyprland 0.55+ (Lua-конфиг, ~/.config/hypr/hyprland.lua)
-- Подключение: скопируйте файл в ~/.config/hypr/hktools.lua и добавьте
--   pcall(require, "hktools")
-- После правки проверьте:  hyprctl reload && hyprctl configerrors

-- Запуск по Super+Shift+K
hl.bind("SUPER + SHIFT + K", hl.dsp.exec_cmd("hktools"), { description = "hktools" })

-- Окна графика битрейта и синхронизации — плавающие, по центру
hl.window_rule({
  name = "hktools-tool-windows",
  match = {
    class = "^(io\\.github\\.zerosensee\\.HKTools)$",
    title = "^(Битрейт.*|Синхронизация по осциллограмме)$",
  },
  float = true,
  center = true,
  size = "1200 640",
})

-- Диалоги выбора файлов (portal)
hl.window_rule({
  name = "hktools-portal",
  match = { class = "^(xdg-desktop-portal-gtk|xdg-desktop-portal-gnome)$" },
  float = true,
  center = true,
  size = "1000 680",
})
