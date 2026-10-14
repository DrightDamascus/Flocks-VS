# Flocks

Vintage Story 1.22.7 mod (modid `flocks`). Client-side migrating bird flocks that signal the seasons. See DESIGN.md.

Status 0.1.0: skeleton only. On the client it logs season, latitude and the migration direction once per in-game day
(`[Flocks]` lines in client-main.log). No rendering yet.

Build: `./build_zip.ps1 [-GamePath <folder with VintagestoryAPI.dll>]` -> `dist/flocks_<version>.zip`.
Default GamePath is the 1.22.7 reference DLLs at %USERPROFILE%\agent-tools\vsref1227.
