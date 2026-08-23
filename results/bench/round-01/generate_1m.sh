#!/bin/bash
# Geracao do dataset 1M da rodada round-01 (docs/benchmark-harness.md SS5).
# 10 shards de 100k arquivos, seeds 20260822..20260831, depth 6, em /tmp/cd-1m.
set -euo pipefail

W=/home/ubuntu/Projetos/Software/cloud-sync-conflict-doctor/.worktrees/t_f3cb006a
GEN="$W/scripts/bench/generate_dataset.py"
OUT="$W/results/bench/round-01"

mkdir -p /tmp/cd-1m

for i in $(seq 0 9); do
  echo "[$(date -Is)] shard_$i inicio" >> "$OUT/generation.log"
  python3 "$GEN" \
    --root "/tmp/cd-1m/shard_$i" \
    --files 100000 \
    --dup-groups 5000 \
    --conflict-names 5000 \
    --placeholders 6000 \
    --depth 6 \
    --seed $((20260822 + i)) \
    > "$OUT/gen_shard_$i.json"
  echo "[$(date -Is)] shard_$i fim" >> "$OUT/generation.log"
done

echo "[$(date -Is)] GERACAO COMPLETA" >> "$OUT/generation.log"
