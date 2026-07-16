#!/usr/bin/env bash
# Remove empty (0-byte) UNTRACKED files from the working tree.
#
# These are junk left by stray shell redirects: a command containing an unescaped `>` next to a
# code fragment creates a file named after the next token, so names like "_success",
# "t.ClosedAt" or "r.Connection.Name" keep appearing. They have reached commits more than once.
#
# Safe by construction: only files git does NOT track AND that are exactly 0 bytes are removed.
# Tracked files, non-empty files, and ignored build output (bin/obj) are never touched.
# Run from anywhere inside the repo; run before every commit.
set -euo pipefail

root="$(git rev-parse --show-toplevel)"
cd "$root"

removed=0
while IFS= read -r -d '' f; do
  if [ -f "$f" ] && [ ! -s "$f" ]; then
    rm -f -- "$f"
    echo "removed empty file: $f"
    removed=$((removed + 1))
  fi
done < <(git ls-files --others --exclude-standard -z)

echo "clean-empty-files: removed $removed empty untracked file(s)"
