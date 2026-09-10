#!/usr/bin/env bash
# filter-repo.sh — Remove large binaries from git history
# Usage: ./filter-repo.sh [--dry-run]
#
# WARNING: This rewrites git history. All collaborators must re-clone.
# Backup branch created: backup-before-filter

set -euo pipefail

DRY_RUN=false
[[ "${1:-}" == "--dry-run" ]] && DRY_RUN=true

echo "=== Git History Cleanup ==="
echo "Target: remove ~127 MB of binaries"
echo ""

# Check git-filter-repo is installed
if ! command -v git-filter-repo &>/dev/null; then
  echo "ERROR: git-filter-repo not found. Install: pip3 install git-filter-repo"
  exit 1
fi

# Check we're in a git repo
if ! git rev-parse --is-inside-work-tree &>/dev/null; then
  echo "ERROR: not inside a git repository"
  exit 1
fi

# Check backup branch exists
if ! git rev-parse --verify backup-before-filter &>/dev/null; then
  echo "ERROR: backup branch 'backup-before-filter' not found"
  echo "Create it first: git branch backup-before-filter"
  exit 1
fi

echo "Files to remove from history:"
echo "  1. dist/rc1-win-x64/conflictdoctor.exe  (~66.8 MB)"
echo "  2. results/bench/round-01/run1           (~11.8 MB)"
echo "  3. results/bench/round-01/run2           (~11.8 MB)"
echo "  4. results/bench/round-01/run3           (~11.8 MB)"
echo "  5. results/bench/round-01/run4           (~11.8 MB)"
echo "  6. results/bench/round-01/run5           (~11.8 MB)"
echo "  7. results/bench/round-01/runwarmup      (~11.8 MB)"
echo "  TOTAL: ~136.8 MB"
echo ""

if $DRY_RUN; then
  echo "[DRY RUN] Would run: git filter-repo --invert-paths \\"
  echo "  --path dist/rc1-win-x64/conflictdoctor.exe \\"
  echo "  --path-glob 'results/bench/round-01/run*' \\"
  echo "  --force"
  echo ""
  echo "[DRY RUN] No changes made."
  exit 0
fi

echo "Running filter-repo..."

# Remove the Windows executable
# Remove all benchmark run binaries (run1-5, runwarmup)
# Remove any other files > 10 MB via callback
git filter-repo \
  --path dist/rc1-win-x64/conflictdoctor.exe \
  --path-glob 'results/bench/round-01/run*' \
  --force

echo ""
echo "=== Done ==="
echo "History rewritten. ~136.8 MB of binaries removed."
echo ""
echo "Next steps:"
echo "  1. Verify: git log --oneline | head -20"
echo "  2. Verify: git count-objects -vH"
echo "  3. Force push: git push --force --all origin"
echo "  4. Notify collaborators to re-clone"
echo "  5. Add dist/ and results/ to .gitignore"
