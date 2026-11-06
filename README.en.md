# ScreenRest

[简体中文](README.md) | English

**A one-keystroke fake screen-off tool** — press the hotkey and every display goes pitch black with the mouse hidden, looking exactly like the monitor was turned off. The system, however, stays fully awake: downloads keep running, builds keep compiling, background jobs keep going — no auto screen-off, no sleep, no lock. Press the hotkey again and you are back instantly.

## 🎬 When would you use this

- 🌙 **Overnight jobs** — leave an AI task running after hours and head home: the screen sits dark like the PC is off, while the job keeps churning in the dark. Collect the results tomorrow morning.
- 🚶 **Step away safely** — grabbing coffee, in a meeting, or chatting by the water cooler? One hotkey blacks out everything on screen while your computer keeps working; come back and toggle it right back on.

## ✨ Why you'll like it

- ⚡ **Fast** — instant blackout, instant restore, no animation, no waiting
- 🪶 **Tiny** — a single 10 KB exe, fully portable, no installation
- 🎈 **Simple** — double-click to start, one hotkey to toggle; no UI, no settings, nothing to learn
- 💤 **Light** — idles at ~25–30 MB of RAM, pretty slim for a GUI app
- 🖥️ **Multi-monitor** — one black window per display
- 🤫 **Low-profile** — a plain dark-gray tray icon with no tooltip

If you find it handy, a **Star ⭐** would make the author's day!
Ideas, suggestions and bug reports are always welcome in [Issues](../../issues) — every one of them gets read. (=^･ω･^=)

## Features

- All displays go pitch black with the mouse cursor hidden — looks like the monitor is off
- The system stays awake: no auto screen-off, no sleep, no lock while covered
- Instant hotkey toggle: `Ctrl+Alt+M` to blackout, press again to restore
- Accident-proof: keyboard input and Alt+F4 are swallowed; the cover never shows up in Alt+Tab or the taskbar
- Multi-monitor support: one black window per display
- RGB blackout: if [OpenRGB](https://openrgb.org) is installed, all LEDs are turned off together with the screen; skipped silently otherwise (toggle in the tray menu)
- Low-profile tray: a plain dark-gray icon with no tooltip; right-click menu to toggle or exit

## Getting started

1. Download `ScreenRest.exe` (about 10 KB, no installation needed)
2. Double-click to run — a dark-gray square appears in the tray
3. Press `Ctrl+Alt+M` to fake screen-off, press again to restore
4. To exit: right-click the tray icon → Exit

## Custom hotkey

The default hotkey is `Ctrl+Alt+M`. To change it, edit the `HotKeyModifiers` / `HotKeyVk` constants at the top of `ScreenRest.cs` (virtual-key codes, e.g. `0x4D` = M) and rebuild.

## Building from source

Double-click `build.cmd`. It uses the C# compiler (`csc.exe`) that ships with Windows (.NET Framework), so no development tools need to be installed. The output is a single `ScreenRest.exe` — copy that one file to any other PC and it just works.

## How it works

- A borderless, topmost, pure-black window (`WS_EX_TOOLWINDOW`, hidden from Alt+Tab) is created for each display, and the cursor is hidden
- `SetThreadExecutionState(ES_CONTINUOUS | ES_DISPLAY_REQUIRED | ES_SYSTEM_REQUIRED)` continuously holds the system idle timers, so Windows never turns off the display, sleeps or locks
- A system-wide hotkey is registered via `RegisterHotKey`, which keeps working while the screen is covered

## FAQ

**Antivirus flags the exe?**
Unsigned executables occasionally trigger false positives. Add it to the whitelist or rename it and rebuild.

**What happens if someone presses the Win key while covered?**
The blackout is only a visual cover — system-level keys (like Win) still pop up system UI. Use your own judgment about the situation, and lock sensitive content before leaving your desk.

**Forgot the hotkey / the app misbehaves?**
Press `Ctrl+Alt+Del`, open Task Manager and kill the ScreenRest process.

**How do I turn the RGB LEDs back on?**
The OpenRGB CLI is stateless — it cannot faithfully restore your previous lighting (mode parameters are often unreadable, and vendor RGB software may overwrite settings anyway). Recommended: save your favorite lighting as a profile in the OpenRGB GUI and load it when needed, or use your motherboard/vendor utility. If LED kill doesn't work, run OpenRGB once as administrator so it can detect your devices.

## License

[MIT](LICENSE)
