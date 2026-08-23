#!/usr/bin/env bash
# CI local — substitui GitHub Actions enquanto desligadas (ago/2026)
# Uso: ./scripts/ci/local-ci.sh
set -euo pipefail
cd "$(dirname "$0")/../.."
export PATH="$HOME/.dotnet:$PATH"

echo "== 1/4 Build Release =="
dotnet build CloudSyncConflictDoctor.sln -c Release --nologo -v q

echo "== 2/4 Testes (todas as suites) =="
dotnet test CloudSyncConflictDoctor.sln -c Release --no-build --nologo -v q

echo "== 3/4 Guarda estática: zero APIs destrutivas em src/ =="
if grep -rn 'File\.Delete\|Directory\.Delete' src/ --include='*.cs' | grep -v '///' | grep -v '// '; then
  echo "FAIL: API destrutiva encontrada em src/"; exit 1
fi
echo "PASS"

echo "== 4/4 Guarda estática: zero Process.Start em src/ =="
if grep -rn 'Process\.Start' src/ --include='*.cs'; then
  echo "FAIL: Process.Start encontrado em src/"; exit 1
fi
echo "PASS"

echo ""
echo "CI LOCAL VERDE ✓"
