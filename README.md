# Flocks

Vintage Story 1.22.7 mod (modid `flocks`). Client-side migrating bird flocks that signal the seasons. See DESIGN.md.

Status 0.4.1: flocks migrate on their own in spring and autumn (time-based, held back by storms); `/flocks schedule` shows today's plan. Test command `/flocks send [count] [goose|hawk]` (or `.flocks send ...` client-side) sends a visual V of greylag geese (default) or hawks over you (client-only, no entities).
The client logs each day's plan and every flock sent or skipped (`[Flocks]` in client-main.log).
Settings in ModConfig/flocks.json. See CHANGELOG.md.

Goose model: `python tools/make_greylag.py` regenerates its shape and animations into Flocks/assets/flocks/ (add `--texture` to repaint the texture, which overwrites hand edits). `tools/greylag_uv_guide.png` shows which texture area is which face; `python tools/preview_greylag.py` renders the bird from below, the side and above.

Build: `./build_zip.ps1 [-GamePath <folder with VintagestoryAPI.dll>]` -> `dist/flocks_<version>.zip`.
Default GamePath is the 1.22.7 reference DLLs at %USERPROFILE%\agent-tools\vsref1227.




