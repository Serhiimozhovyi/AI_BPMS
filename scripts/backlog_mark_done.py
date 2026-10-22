#!/usr/bin/env python3
"""Ставить задачі статус `done` у BACKLOG.md.

Використання: backlog_mark_done.py <гілка>
Гілка agent/<ID>-<слаг> (напр. agent/F1-07-core-nodes) → задача <ID> отримує `done`.
Гілки без ID задачі (напр. agent/backlog-...) пропускаються без змін.
"""
import re
import sys
from pathlib import Path

BACKLOG = Path("BACKLOG.md")


def main() -> int:
    branch = sys.argv[1] if len(sys.argv) > 1 else ""
    match = re.match(r"^agent/([A-Z][A-Z0-9]*-\d+)(?:-|$)", branch)
    if not match:
        print(f"Гілка «{branch}» не містить ID задачі — пропускаю.")
        return 0

    task_id = match.group(1)
    lines = BACKLOG.read_text(encoding="utf-8").splitlines(keepends=True)
    for i, line in enumerate(lines):
        if line.startswith(f"### {task_id} "):
            for j in range(i + 1, min(i + 4, len(lines))):
                if "status: `" in lines[j]:
                    new = re.sub(r"status: `[^`]*`", "status: `done`", lines[j], count=1)
                    if new == lines[j]:
                        print(f"{task_id} уже done.")
                        return 0
                    lines[j] = new
                    BACKLOG.write_text("".join(lines), encoding="utf-8")
                    print(f"{task_id} → done")
                    return 0
            print(f"У {task_id} не знайдено рядка зі статусом.", file=sys.stderr)
            return 1

    print(f"Задачі {task_id} немає в BACKLOG.md — пропускаю.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
