#!/usr/bin/env python3
"""Wrapper de execucao da rodada benchmark T07 (docs/benchmark-harness.md SS3).

Captura por execucao: wall_clock (relogio monotonico), peak_RSS_kb e CPU% via
resource.getrusage(RUSAGE_CHILDREN) — /usr/bin/time (GNU time) ausente neste host.
Grava <out>.json (stdout do scanner) e <out>.meta.json (metricas do processo).

Uso: python3 run_scan.py <conflictdoctor.dll> <raiz-do-dataset> <saida-base>
"""
import json
import os
import resource
import subprocess
import sys
import time


def main() -> int:
    dll, root, out = sys.argv[1], sys.argv[2], sys.argv[3]

    # dotnet fora do PATH neste shell; SDK fixo em ~/.dotnet (driver do apphost).
    driver = os.environ.get("CD_DOTNET", "/home/ubuntu/.dotnet/dotnet")
    ru0 = resource.getrusage(resource.RUSAGE_CHILDREN)
    t0 = time.perf_counter()
    proc = subprocess.run(
        [driver, dll, "scan", root, "--json"],
        capture_output=True,
        text=True,
    )
    dt = time.perf_counter() - t0
    ru1 = resource.getrusage(resource.RUSAGE_CHILDREN)

    user_s = ru1.ru_utime - ru0.ru_utime
    sys_s = ru1.ru_stime - ru0.ru_stime
    meta = {
        "wall_clock_s": round(dt, 3),
        "peak_rss_kb": ru1.ru_maxrss,
        "cpu_user_s": round(user_s, 3),
        "cpu_sys_s": round(sys_s, 3),
        "cpu_pct": round((user_s + sys_s) / dt * 100.0, 1) if dt > 0 else None,
        "exit_code": proc.returncode,
        "stdout_bytes": len(proc.stdout.encode("utf-8")),
        "stderr_tail": proc.stderr[-2000:] if proc.stderr else "",
    }
    with open(out, "w", encoding="utf-8") as f:
        f.write(proc.stdout)
    with open(out + ".meta.json", "w", encoding="utf-8") as f:
        json.dump(meta, f, indent=2)
        f.write("\n")
    print(json.dumps(meta))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
