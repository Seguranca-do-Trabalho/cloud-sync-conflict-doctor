#!/usr/bin/env python3
"""Gerador de dataset sintetico para benchmarks do Cloud Sync Conflict Doctor.

Card t_2c4c7be7 (T04 - Benchmark harness design). Python3 puro, stdlib.

Gera uma arvore deterministica por seed com:
  - arquivos unicos;
  - grupos de duplicatas identicas exatas (mesmo conteudo, caminhos distintos);
  - divergencias reais: nomes que normalizam igual, conteudos diferentes
    (grupos de indice par usam o MESMO tamanho com conteudo distinto — pior caso
    para o filtro de tamanho do pipeline; impares usam tamanhos diferentes);
  - sufixos de conflito reais da SPEC §7: " (1)", " (conflicted copy)",
    "-DESKTOP-XXXX", "~$", "~", ".sb-<hex>";
  - placeholders simulados: stub de 4 KiB com marcador legivel + sidecar
    <arquivo>.placeholder-meta.json registrando atributos OFFLINE simulados
    (ext4 nao possui FILE_ATTRIBUTE_*; hook para Windows documentado no
    docs/benchmark-harness.md);
  - arvores profundas ate --depth niveis de diretorio.

Determinismo: toda aleatoriedade vem de random.Random(seed); mtime fixo em
2000-01-01T00:00:00Z; mesmo seed => mesma arvore byte a byte.

Uso:
    python3 generate_dataset.py --root /tmp/ds --files 500 --dup-groups 25 \
        --conflict-names 40 --placeholders 30 --depth 4 --seed 42

Saida (stdout): resumo JSON com parametros e contagens. --quiet suprime.
"""

from __future__ import annotations

import argparse
import json
import os
import random
import sys
import time
from pathlib import Path

PLACEHOLDER_ATTRIBUTES = (
    "FILE_ATTRIBUTE_OFFLINE",
    "FILE_ATTRIBUTE_RECALL_ON_OPEN",
    "FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS",
)

# Sufixos da lista fixa da SPEC §7. Cada entrada: (prefixo, sufixo) aplicados
# ao stem base. "~$" e prefixo; os demais sao sufixos.
_CONFLICT_SUFFIX_PATTERNS = (" (1)", " (2)", " (conflicted copy)", "-DESKTOP-{host}", "~", ".sb-{hex}")

_DIR_NAMES = (
    "Docs", "Fotos", "Planilhas", "Projetos", "Backup", "Trabalho",
    "Pessoal", "Downloads", "Notas", "Arquivo",
)
_EXTENSIONS = (".txt", ".docx", ".xlsx", ".pptx", ".md", ".csv", ".jpg", ".pdf")

_FIXED_MTIME = 946684800.0  # 2000-01-01T00:00:00Z
_CHUNK = 64 * 1024


class ParametrosInvalidos(ValueError):
    """Argumentos que nao produzem uma arvore valida."""


def normalize_conflict_stem(stem: str) -> str:
    """Normaliza stem removendo sufixos de conflito da SPEC §7.

    Funcao de referencia para testes e fixtures; a implementacao de producao
    (Doctor.Core) deve produzir o mesmo resultado sobre estes padroes.
    """
    s = stem
    if s.startswith("~$"):
        s = s[2:]
    if s.endswith("~"):
        s = s[:-1]
    # .sb-<hex> no fim do stem
    idx = s.rfind(".sb-")
    if idx != -1 and idx + 4 < len(s) and all(c in "0123456789abcdefABCDEF" for c in s[idx + 4:]):
        s = s[:idx]
    # -DESKTOP-<host>
    idx = s.find("-DESKTOP-")
    if idx != -1:
        s = s[:idx]
    # " (N)" e " (conflicted copy)" no fim
    for suf in (" (conflicted copy)",):
        if s.endswith(suf):
            s = s[: -len(suf)]
            break
    while True:
        if s.endswith(")") and " (" in s:
            prefixo, sep, num = s.rpartition(" (")
            if sep and num[:-1].isdigit() and num[:-1]:
                s = prefixo
                continue
        break
    return s


def _validate(files: int, dup_groups: int, conflict_names: int,
              placeholders: int, depth: int) -> None:
    if files < 1:
        raise ParametrosInvalidos("--files deve ser >= 1")
    if dup_groups < 0 or conflict_names < 0 or placeholders < 0:
        raise ParametrosInvalidos("contagens negativas nao sao permitidas")
    if depth < 0:
        raise ParametrosInvalidos("--depth deve ser >= 0")

    custo_dup = dup_groups * 2
    custo_conflict = conflict_names * 2
    custo_placeholder = placeholders * 1
    minimo = custo_dup + custo_conflict + custo_placeholder
    if minimo > files:
        raise ParametrosInvalidos(
            f"--files={files} insuficiente: {dup_groups} grupos de duplicata (2 "
            f"arquivos cada) + {conflict_names} grupos de conflito (2 arquivos "
            f"cada) + {placeholders} placeholders exigem no minimo {minimo} arquivos"
        )
    if files - minimo > 10_000_000:
        raise ParametrosInvalidos("--files acima de 10 milhoes nao e suportado")


def build_plan(files: int, dup_groups: int, conflict_names: int,
               placeholders: int, depth: int, seed: int) -> dict:
    """Constroi o plano completo da arvore SEM tocar o disco."""
    _validate(files, dup_groups, conflict_names, placeholders, depth)
    rng = random.Random(seed)

    n_dirs = max(1, min(depth, 8)) * 4 if depth > 0 else 0
    dirs = []
    for i in range(n_dirs):
        d = i % max(1, depth if depth > 0 else 1)
        partes = tuple(
            f"{_DIR_NAMES[(i * (k + 3) + k) % len(_DIR_NAMES)]}{k + 1}"
            for k in range(d + 1)
        )
        dirs.append(partes)

    def novo_caminho(ext: str | None = None) -> str:
        """Caminho relativo unico, deterministico, dentro da profundidade."""
        while True:
            if dirs and rng.random() < 0.85:
                partes_dir = rng.choice(dirs)
            else:
                partes_dir = ()
            nome_base = f"file_{rng.randrange(16**8):08x}"
            ext_final = ext if ext is not None else rng.choice(_EXTENSIONS)
            rel = "/".join((*partes_dir, nome_base + ext_final))
            if rel not in ocupados:
                ocupados.add(rel)
                return rel

    ocupados: set[str] = set()

    plan_dirs = sorted("/".join(p) for p in dirs)
    unique_files: list[str] = []
    duplicate_groups: list[list[str]] = []
    conflict_groups: list[tuple[str, str, bool, int, int]] = []
    placeholder_files: list[str] = []

    # --- Placeholders primeiro (reserva garantida) ---
    for _ in range(placeholders):
        placeholder_files.append(novo_caminho())

    # --- Grupos de conflito: 2 arquivos por grupo, nomes normalizando igual ---
    hosts = [f"-DESKTOP-{rng.randrange(0xFFFF):04X}" for _ in range(8)]
    for g in range(conflict_names):
        base = f"relatorio_g{g:03d}"
        ext = rng.choice((".docx", ".xlsx", ".csv"))
        mesmo_tamanho = g % 2 == 0
        if mesmo_tamanho:
            tamanho_a = tamanho_b = rng.randrange(_CHUNK // 2, _CHUNK * 2)
        else:
            tamanho_a = rng.randrange(_CHUNK, _CHUNK * 3)
            tamanho_b = tamanho_a + rng.randrange(1024, 8192)

        # variante A: stem limpo ou prefixo ~$; variante B: sempre sufixo de conflito
        if rng.random() < 0.25:
            caminho_a = f"~${base}{ext}"
        else:
            caminho_a = f"{base}{ext}"

        escolha_b = rng.choice((" (1)", " (conflicted copy)", "-DESKTOP-", "~$", "~", ".sb-"))
        if escolha_b == "~$":
            caminho_b = f"~${base}{ext}"
        elif escolha_b == "-DESKTOP-":
            caminho_b = f"{base}{rng.choice(hosts)}{ext}"
        elif escolha_b == ".sb-":
            caminho_b = f"{base}.sb-{rng.randrange(16**6):06x}{ext}"
        elif escolha_b == "~":
            caminho_b = f"{base}~{ext}"
        else:
            caminho_b = f"{base}{escolha_b}"
        if caminho_b == caminho_a:
            # colisao intra-grupo (ex.: A e B ambos "~$nome"): substitui B
            caminho_b = f"{base} (1){ext}"

        conflict_groups.append((caminho_a, caminho_b, mesmo_tamanho,
                                tamanho_a, tamanho_b))
        ocupados.add(caminho_a)
        ocupados.add(caminho_b)

    # --- Grupos de duplicata identica: 2+ arquivos com o MESMO conteudo ---
    for g in range(dup_groups):
        primario = novo_caminho()
        extra = novo_caminho()
        duplicate_groups.append([primario, extra])

    # --- Unicos preenchem o restante ---
    alocados = (sum(len(g) for g in duplicate_groups)
                + 2 * len(conflict_groups)  # cada grupo de conflito tem 2 arquivos
                + len(placeholder_files))
    for _ in range(files - alocados):
        unique_files.append(novo_caminho())

    return {
        "parameters": {
            "files": files, "dup_groups": dup_groups,
            "conflict_names": conflict_names, "placeholders": placeholders,
            "depth": depth, "seed": seed,
        },
        "dirs": plan_dirs,
        "unique_files": sorted(unique_files),
        "duplicate_groups": sorted(sorted(g) for g in duplicate_groups),
        "conflict_groups": sorted((g[0], g[1]) for g in conflict_groups),
        "conflict_sizes": sorted(conflict_groups),
        "placeholder_files": sorted(placeholder_files),
    }


def materialize(plan: dict, root: Path) -> dict:
    """Materializa o plano em root. Deterministico: mtimes fixos."""
    rng = random.Random(plan["parameters"]["seed"] ^ 0x5EED5EED)
    criados = 0
    bytes_totais = 0
    t_zero = time.time()

    def escrever(rel: str, tamanho: int, dados_fixos: bytes | None = None) -> int:
        nonlocal criados, bytes_totais
        alvo = root / Path(rel)
        alvo.parent.mkdir(parents=True, exist_ok=True)
        with open(alvo, "wb") as fh:
            if dados_fixos is not None:
                fh.write(dados_fixos)
                escritos = len(dados_fixos)
            else:
                escritos = 0
                while escritos < tamanho:
                    bloco = rng.randbytes(min(_CHUNK, tamanho - escritos))
                    fh.write(bloco)
                    escritos += len(bloco)
        os.utime(alvo, (_FIXED_MTIME, _FIXED_MTIME))
        criados += 1
        bytes_totais += escritos
        return escritos

    for d in plan["dirs"]:
        (root / Path(d)).mkdir(parents=True, exist_ok=True)

    sidecars = 0
    for rel in plan["placeholder_files"]:
        escrever(rel, 4096, b"PLACEHOLDER-SIMULADO - nao leia este arquivo.\n" + b"\x00" * (4096 - 44))
        meta = {
            "file": rel,
            "simulated_attributes": PLACEHOLDER_ATTRIBUTES[len(rel) % len(PLACEHOLDER_ATTRIBUTES)],
            "provider": ["onedrive", "gdrive", "dropbox"][len(rel) % 3],
            "original_size": 100_000_000 + len(rel) * 7919,
            "note": "stub local; atributos OFFLINE nao existem em ext4 - ver docs/benchmark-harness.md",
        }
        sc = root / Path(rel + ".placeholder-meta.json")
        sc.parent.mkdir(parents=True, exist_ok=True)
        sc.write_text(json.dumps(meta, indent=2, sort_keys=True), encoding="utf-8")
        os.utime(sc, (_FIXED_MTIME, _FIXED_MTIME))
        sidecars += 1

    for i, grupo in enumerate(plan["conflict_groups"]):
        _a, _b, _mesmo, tamanho_a, tamanho_b = plan["conflict_sizes"][i]
        escrever(grupo[0], tamanho_a)
        escrever(grupo[1], tamanho_b)

    conteudos_duplicatas: list[bytes] = []
    for grupo in plan["duplicate_groups"]:
        tamanho = rng.randrange(_CHUNK, _CHUNK * 4)
        conteudo = rng.randbytes(tamanho)
        conteudos_duplicatas.append(conteudo)
        for rel in grupo:
            escrever(rel, tamanho, dados_fixos=conteudo)

    for rel in plan["unique_files"]:
        escrever(rel, rng.randrange(256, _CHUNK * 2))

    counts = {
        "files_created": criados,
        "dirs_created": len(set(plan["dirs"])),
        "bytes_written": bytes_totais,
        "unique_files": len(plan["unique_files"]),
        "duplicate_groups": len(plan["duplicate_groups"]),
        "duplicate_files": sum(len(g) for g in plan["duplicate_groups"]),
        "conflict_groups": len(plan["conflict_groups"]),
        "conflict_files": sum(len(g) for g in plan["conflict_groups"]),
        "placeholders": len(plan["placeholder_files"]),
        "sidecars_written": sidecars,
    }
    return {"counts": counts, "files": {
        "unique": plan["unique_files"],
        "duplicates": plan["duplicate_groups"],
        "conflicts": plan["conflict_groups"],
        "placeholders": plan["placeholder_files"],
    }}


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(
        description="Gera dataset sintetico deterministico p/ benchmark do Conflict Doctor."
    )
    ap.add_argument("--root", required=True, help="diretorio raiz da arvore gerada")
    ap.add_argument("--files", type=int, default=500)
    ap.add_argument("--dup-groups", type=int, default=25, dest="dup_groups")
    ap.add_argument("--conflict-names", type=int, default=40, dest="conflict_names")
    ap.add_argument("--placeholders", type=int, default=30)
    ap.add_argument("--depth", type=int, default=4)
    ap.add_argument("--seed", type=int, default=42)
    ap.add_argument("--quiet", action="store_true", help="nao imprime resumo JSON")
    args = ap.parse_args(argv)

    try:
        plan = build_plan(args.files, args.dup_groups, args.conflict_names,
                          args.placeholders, args.depth, args.seed)
    except ParametrosInvalidos as exc:
        print(f"erro: {exc}", file=sys.stderr)
        return 2

    root = Path(args.root).resolve()
    root.mkdir(parents=True, exist_ok=True)
    resultado = materialize(plan, root)

    if not args.quiet:
        resumo = {
            "generator_version": 1,
            "parameters": plan["parameters"],
            "counts": resultado["counts"],
            "root": str(root),
        }
        print(json.dumps(resumo, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
