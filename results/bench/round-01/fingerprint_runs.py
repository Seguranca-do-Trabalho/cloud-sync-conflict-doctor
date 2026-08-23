#!/usr/bin/env python3
"""Fingerprints SHA-256 dos stdout das execucoes da rodada (evidencia de determinismo).

Uso: python3 fingerprint_runs.py <dir-round-01>
"""
import hashlib
import json
import sys
from pathlib import Path


def main() -> int:
    d = Path(sys.argv[1])
    saida = {}
    for nome in ["runwarmup", "run1", "run2", "run3", "run4", "run5"]:
        p = d / nome
        h = hashlib.sha256(p.read_bytes()).hexdigest()
        saida[nome] = {"sha256": h, "bytes": p.stat().st_size}
    identicos = len({v["sha256"] for v in saida.values()}) == 1
    resultado = {"runs": saida, "stdout_byte_identicos": identicos}
    (d / "fingerprints.json").write_text(json.dumps(resultado, indent=2) + "\n")
    print(json.dumps(resultado, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
