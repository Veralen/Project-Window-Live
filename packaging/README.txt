========================================
 ScreenTranslator  —  Chinese to English
========================================

What it does
------------
Press a keyboard shortcut, drag a box around Chinese text anywhere on your
screen, and a moment later the English translation appears next to it.
Everything runs on your own PC — no internet, no accounts, nothing is sent
anywhere.


First-time setup (one step, done once)
--------------------------------------
ScreenTranslator needs Windows' Chinese text-recognition pack. To install it:

  1. Right-click "install-ocr-language.ps1" (in this folder).
  2. Choose "Run with PowerShell".
  3. Click "Yes" if Windows asks for permission.
  4. Wait for it to say Done, then close the window.

(If you skip this and only English recognition is installed, the app will pop
up a note telling you the Chinese pack is missing.)


How to use it
-------------
  1. Double-click  ScreenTranslator.exe .
     It doesn't open a window — it lives in the system tray (the little icons
     near the clock, bottom-right). Look for the blue "T" icon.

     If you don't see the blue "T" icon, click the  ^  arrow near the clock
     (the "hidden icons" arrow) — new tray icons often start out tucked in
     there. You can drag the icon out onto the taskbar tray to keep it visible.

     When it starts, a little pop-up near the clock confirms it's running and
     shows your shortcut.

  2. Press  Ctrl + Shift + L  (or left-click the tray icon).
     The screen dims. Drag a box around the Chinese text you want translated.

  3. Let go. The English translation appears in dark labels near the text.

  4. Press  Esc , click an empty area, or click the small  ✕  to close it.
     The app keeps running in the tray, ready for the next time.

Only one copy runs at a time. If you double-click ScreenTranslator.exe again
while it's already running, it just shows a short message pointing you back to
the tray icon — it won't start a second copy.


Change the keyboard shortcut
----------------------------
  Right-click the tray icon  ->  Settings...
  Click the box, press the keys you want (for example Ctrl + Shift + L),
  then click Save. It takes effect immediately — no restart.


Turn it off
-----------
  Right-click the tray icon  ->  Exit.


Good to know
------------
  * Works best on clear, printed text: menus, subtitles, buttons, captions.
  * Long dense paragraphs translate more roughly than short phrases — this is
    a small offline model, traded for privacy and speed. Handwriting is not
    supported.
  * 100% offline. Your screen never leaves your computer.

  Requires 64-bit Windows 10 or 11.


Something not working? (for support)
------------------------------------
ScreenTranslator writes a small log file that helps diagnose problems. You can
find it here (paste this into the File Explorer address bar):

  %LOCALAPPDATA%\ScreenTranslator\logs

Open the newest  app-YYYYMMDD.log  file, or send it along when asking for help.
