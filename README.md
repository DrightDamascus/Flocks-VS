# Flocks

Vintage Story 1.22.7 mod (modid `flocks`). Client-side migrating bird flocks that signal the seasons. See DESIGN.md.

Status 0.2.1: test command `/flocks send [count] [species]` (or `.flocks send ...` client-side) sends a visual V of hawks over you (client-only, no entities).
The client also logs season, latitude and migration direction once per in-game day (`[Flocks]` in client-main.log).
No automatic migration yet. See CHANGELOG.md.

Build: `./build_zip.ps1 [-GamePath <folder with VintagestoryAPI.dll>]` -> `dist/flocks_<version>.zip`.
Default GamePath is the 1.22.7 reference DLLs at %USERPROFILE%\agent-tools\vsref1227.


