#!/usr/bin/env python3
"""Determinismo de conteudo das execucoes: compara os JSON sem o bloco
generated_from (timestamps vivos permitidos pelo ADR-0003).

Uso: python3 compare_determinism.py <dir-round-01>
"""
import hashlib
import json
import sys
from pathlib import Path


def main() -> int:
    d = Path(sys.argv[1])
    saida = {}
    for nome in ["runwarmup", "run1", "run2", "run3", "run4", "run5"]:
        doc = json.loads((d / nome).read_text())
        doc.pop("generated_from", None)
        canon = json.dumps(doc, sort_keys=True, ensure_ascii=False).encode("utf-8")
        saida[nome] = hashlib.sha256(canon).hexdigest()
    identicos = len(set(saida.values())) == 1
    resultado = {
        "sha256_sem_generated_from": saida,
        "conteudo_deterministico": identicos,
    }
    (d / "determinism_check.json").write_text(json.dumps(resultado, indent=2) + "\n")
    print(json.dumps(resultado, indent=2))
    return 0 if identicos else 1


if __name__ == "__main__":
    raise SystemExit(main())
