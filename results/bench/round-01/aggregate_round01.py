#!/usr/bin/env python3
"""Agrega as execucoes da rodada round-01 (mediana de 5 + delta vs warm-up).

Uso: python3 aggregate_round01.py <dir-round-01> <commit-before>
Le run{1..5}(.meta).json do diretorio e imprime a tabela-modelo do harness SS6.
"""
import json
import statistics
import sys
from pathlib import Path

CAMPOS = (
    "files_enumerated",
    "files_skipped",
    "files_placeholder",
    "files_partial_hashed",
    "files_full_hashed",
    "bytes_read",
    "bytes_read_partial",
    "bytes_read_full",
    "placeholder_bytes_read",
)


def main() -> int:
    d = Path(sys.argv[1])
    before = sys.argv[2] if len(sys.argv) > 2 else "?"

    metas = []
    tels = []
    for i in range(1, 6):
        meta = json.loads((d / f"run{i}.meta.json").read_text())
        doc = json.loads((d / f"run{i}").read_text())
        metas.append(meta)
        tels.append(doc["report"]["telemetry"])

    print("== metricas de processo (por execucao)")
    for i, m in enumerate(metas, 1):
        print(
            f"run{i}: wall={m['wall_clock_s']}s rss={m['peak_rss_kb']}kB "
            f"cpu={m['cpu_pct']}% exit={m['exit_code']}"
        )

    wall = statistics.median(m["wall_clock_s"] for m in metas)
    rss = statistics.median(m["peak_rss_kb"] for m in metas)
    cpu = statistics.median(m["cpu_pct"] for m in metas)

    print("\n== medianas (n=5)")
    print(f"wall_clock_s: {wall:.3f}")
    print(f"peak_RSS_kb:  {rss}")
    print(f"CPU%:         {cpu}")

    print("\n== telemetria (por execucao)")
    for i, t in enumerate(tels, 1):
        vals = " ".join(f"{k.replace('files_', 'f_').replace('bytes_', 'b_').replace('placeholder_bytes_read', 'GATE')}={t[k]}" for k in CAMPOS)
        print(f"run{i}: {vals}")

    print("\n== medianas de telemetria")
    for k in CAMPOS:
        med = statistics.median(t[k] for t in tels)
        iguais = all(t[k] == tels[0][k] for t in tels)
        print(f"{k}: {med:g}{'' if iguais else '  (VARIA entre execucoes)'}")

    gate = max(t["placeholder_bytes_read"] for t in tels)
    print(f"\ngate placeholder_bytes_read == 0: {'PASS' if gate == 0 else f'FAIL ({gate})'}")
    print(f"before_commit: {before}")
    return 0 if gate == 0 else 2


if __name__ == "__main__":
    raise SystemExit(main())
