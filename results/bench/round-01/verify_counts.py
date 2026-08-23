#!/usr/bin/env python3
"""Verificacao do dataset composto /tmp/cd-1m da rodada round-01.

Conferencia exigida pelo card T07: contagem total de arquivos == soma dos
files_created + sidecars_written dos resumos JSON por shard. Percorre a arvore
uma unica vez (os.walk) e emite JSON com contagens e bytes por shard.
"""
import json
import os
import sys
from pathlib import Path


def contar(raiz: Path) -> tuple[int, int]:
    n = 0
    b = 0
    for dirpath, _, files in os.walk(raiz):
        n += len(files)
        for f in files:
            try:
                b += os.stat(os.path.join(dirpath, f)).st_size
            except OSError:
                pass
    return n, b


def main() -> int:
    root = Path("/tmp/cd-1m")
    out = sys.argv[1]
    shards_dir = Path(sys.argv[2])

    esperado_total = 0
    bytes_gen = 0
    shards = []
    for i in range(10):
        resumo = json.loads((shards_dir / f"gen_shard_{i}.json").read_text())
        c = resumo["counts"]
        esp = c["files_created"] + c["sidecars_written"]
        esperado_total += esp
        bytes_gen += c["bytes_written"]
        shards.append({"shard": i, "seed": resumo["parameters"]["seed"], "esperado": esp})

    total = 0
    bytes_disco = 0
    for s in shards:
        n, b = contar(root / f"shard_{s['shard']}")
        s["encontrado"] = n
        s["bytes_disco"] = b
        total += n
        bytes_disco += b

    resultado = {
        "arquivos_encontrados": total,
        "arquivos_esperados": esperado_total,
        "divergencia": total - esperado_total,
        "bytes_em_disco": bytes_disco,
        "bytes_previstos_gerador": bytes_gen,
        "shards": shards,
    }
    Path(out).write_text(json.dumps(resultado, indent=2) + "\n")
    print(json.dumps(resultado, indent=2))
    return 0 if total == esperado_total else 1


if __name__ == "__main__":
    raise SystemExit(main())
