# Copies the Claude Code session transcripts for this project into transcripts/<day>/, one folder per
# local day a session started. Only adds or refreshes copies, never deletes, so a transcript Claude Code
# cleans up keeps its copy here. Run by hand or from the SessionEnd hook: python -I tools/save_transcripts.py
import json, shutil
from datetime import datetime
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SRC = Path.home() / ".claude" / "projects" / "e--Projects-Flocks"
DEST = ROOT / "transcripts"

def start_day(path):
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            try:
                ts = json.loads(line).get("timestamp")
            except ValueError:
                continue
            if ts:
                return datetime.fromisoformat(ts.replace("Z", "+00:00")).astimezone().strftime("%Y-%m-%d")
    return datetime.fromtimestamp(path.stat().st_mtime).strftime("%Y-%m-%d")

def newer(src, dst):
    return not dst.exists() or src.stat().st_size != dst.stat().st_size or src.stat().st_mtime > dst.stat().st_mtime

copied = 0
for log in sorted(SRC.glob("*.jsonl")):
    day = DEST / start_day(log)
    day.mkdir(parents=True, exist_ok=True)
    pairs = [(log, day / log.name)]
    extra = SRC / log.stem  # tool output too long for the log itself
    if extra.is_dir():
        pairs += [(p, day / log.stem / p.relative_to(extra)) for p in extra.rglob("*") if p.is_file()]
    for src, dst in pairs:
        if newer(src, dst):
            dst.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(src, dst)
            copied += 1
print(f"{copied} file(s) saved to {DEST}")
