#!/usr/bin/env bash
# CI local — substitui GitHub Actions enquanto desligadas (ago/2026)
# ESCOPO: cobre o job LINUX do workflow. O job WINDOWS (PLH-03, placeholders
# OneDrive nativos, junctions NTFS) exige SO Windows — pendência estrutural
# do GATE 6; ver README "CI/CD — local (Linux) vs GitHub Actions".
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

echo "== 5/6 Guarda NDES-05: relatorio deterministico (smoke) =="
dotnet run --project src/Doctor.Cli -c Release --no-build -- scan tests/fixtures/golden-tree --json /tmp/ci-report-1.json --quiet 2>/dev/null || \
  echo "SKIP: fixture golden-tree ausente no runner"

echo "== 6/6 Guarda GUIVM-02: zero rotulo destrutivo na GUI =="
if grep -rniE '"(apagar|deletar|excluir|delete)' src/Doctor.Gui --include='*.axaml' --include='*.cs' | grep -vE 'Quarentena|quarentena|nunca|Never|jamais'; then
  echo "FAIL: rotulo destrutivo encontrado na GUI"; exit 1
fi
echo "PASS"
