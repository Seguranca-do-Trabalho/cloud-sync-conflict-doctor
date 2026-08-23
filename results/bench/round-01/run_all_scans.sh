#!/bin/bash
# Execucoes da rodada round-01 sobre /tmp/cd-1m (harness SS4).
# Ordem: 1 warm-up descartavel + 5 medidas (mediana). Saida em results/bench/round-01/.
set -euo pipefail

W=/home/ubuntu/Projetos/Software/cloud-sync-conflict-doctor/.worktrees/t_f3cb006a
OUT="$W/results/bench/round-01"
BIN="$W/src/Doctor.Cli/bin/Release/net8.0/conflictdoctor.dll"
RAIZ=/tmp/cd-1m

for i in warmup 1 2 3 4 5; do
  echo "[$(date -Is)] scan $i inicio" >> "$OUT/scans.log"
  python3 "$OUT/run_scan.py" "$BIN" "$RAIZ" "$OUT/run$i" >> "$OUT/scans.log" 2>&1
  echo "[$(date -Is)] scan $i fim" >> "$OUT/scans.log"
done

echo "[$(date -Is)] EXECUCOES COMPLETAS" >> "$OUT/scans.log"
