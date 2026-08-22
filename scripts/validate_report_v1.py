#!/usr/bin/env python3
"""Card t_957718d4 - Validador do schema report v1 (docs/schema-report-v1.md).

Uso: python3 validate_report_v1.py <arquivo.json>

Verifica estruturalmente um relatorio report v1:
  - ordem declarada das chaves (raiz e objetos aninhados)
  - ordenacao canonica por bytes UTF-8 em todas as listas
  - invariantes de telemetria da secao 5
  - formato dos hashes BLAKE3 (64 hex minusculos)
  - ordem canonica de placeholders.kinds
Saida: OK ou lista de violacoes; exit code 0/1.
"""
import json
import re
import sys

ROOT_KEYS = [
    "report_schema_version", "algorithm", "hash_version",
    "normalization_rules_version", "generated_from", "telemetry",
    "groups", "identical_duplicates", "real_conflicts", "placeholders",
]
GEN_KEYS = ["root_path", "scan_started_utc", "scan_finished_utc"]
TEL_KEYS = [
    "files_enumerated", "files_placeholder", "files_skipped",
    "files_partial_hashed", "files_full_hashed", "bytes_read",
    "bytes_read_partial", "bytes_read_full", "placeholder_bytes_read",
]
GROUP_KEYS = ["normalized_base_name", "size_bytes", "members"]
MEMBER_KEYS = ["path"]
DUP_KEYS = ["hash", "size_bytes", "files"]
CONFLICT_KEYS = ["normalized_base_name", "size_bytes", "files"]
CFILE_KEYS = ["path", "hash"]
PLACEHOLDER_KEYS = ["path", "kinds", "size_bytes"]
KIND_ORDER = ["reparse_point", "recall_on_data_access", "recall_on_open", "offline"]

violations = []


def err(msg):
    violations.append(msg)


def cmp_utf8(a, b):
    ba, bb = a.encode("utf-8"), b.encode("utf-8")
    return (ba > bb) - (ba < bb)


def check_keys(obj, expected, where):
    keys = list(obj.keys())
    if keys != expected:
        err(f"{where}: ordem/chaves {keys} != esperado {expected}")


def check_sorted(paths, where):
    for i in range(1, len(paths)):
        if cmp_utf8(paths[i - 1], paths[i]) >= 0:
            err(f"{where}: violacao de ordem em posicao {i}: {paths[i-1]!r} !< {paths[i]!r}")


def check_hash(h, where):
    if not re.fullmatch(r"[0-9a-f]{64}", h):
        err(f"{where}: hash fora do formato (64 hex minusculos): {h!r}")


def main():
    if len(sys.argv) != 2:
        print(__doc__)
        sys.exit(2)
    raw = open(sys.argv[1], "rb").read()
    if raw.startswith(b"\xef\xbb\xbf"):
        err("BOM detectado")
    if not raw.endswith(b"\n") or raw.endswith(b"\n\n"):
        err("newline final incorreto")
    if b"\r" in raw:
        err("CR detectado")

    r = json.loads(raw.decode("utf-8"))

    check_keys(r, ROOT_KEYS, "raiz")
    if r["report_schema_version"] != 1:
        err("report_schema_version != 1")
    if r["algorithm"] != "BLAKE3":
        err("algorithm != BLAKE3")
    if r["hash_version"] < 1 or r["normalization_rules_version"] < 1:
        err("versao menor que 1")

    check_keys(r["generated_from"], GEN_KEYS, "generated_from")
    ts = r["generated_from"]["scan_started_utc"]
    tf = r["generated_from"]["scan_finished_utc"]
    pat = re.compile(r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$")
    if not pat.fullmatch(ts):
        err("scan_started_utc fora do formato fixo")
    if not pat.fullmatch(tf):
        err("scan_finished_utc fora do formato fixo")

    t = r["telemetry"]
    check_keys(t, TEL_KEYS, "telemetry")
    if t["placeholder_bytes_read"] != 0:
        err("INVARIANTE DE SEGURANCA: placeholder_bytes_read != 0")
    if t["files_enumerated"] != t["files_placeholder"] + t["files_skipped"] + t["files_partial_hashed"]:
        err("invariante files_enumerated quebrada")
    if t["files_full_hashed"] > t["files_partial_hashed"]:
        err("invariante files_full_hashed <= files_partial_hashed quebrada")
    if t["bytes_read"] != t["bytes_read_partial"] + t["bytes_read_full"]:
        err("invariante bytes_read quebrada")

    group_keys = []
    for i, g in enumerate(r["groups"]):
        check_keys(g, GROUP_KEYS, f"groups[{i}]")
        group_keys.append((g["normalized_base_name"], g["size_bytes"]))
        check_sorted([m["path"] for m in g["members"]], f"groups[{i}].members")
        for m in g["members"]:
            check_keys(m, MEMBER_KEYS, f"groups[{i}].members[].path={m['path']!r}")
    sk = [(b.encode("utf-8"), s) for b, s in group_keys]
    if sk != sorted(sk):
        err("groups fora da ordem canonica (base bytes, size)")

    dup_first_paths = []
    for i, d in enumerate(r["identical_duplicates"]):
        check_keys(d, DUP_KEYS, f"identical_duplicates[{i}]")
        check_hash(d["hash"], f"identical_duplicates[{i}].hash")
        check_sorted(d["files"], f"identical_duplicates[{i}].files")
        if len(d["files"]) < 2:
            err(f"identical_duplicates[{i}] com menos de 2 arquivos")
        dup_first_paths.append(min(d["files"], key=lambda p: p.encode("utf-8")))
    check_sorted(dup_first_paths, "identical_duplicates")

    rc_first_paths = []
    for i, c in enumerate(r["real_conflicts"]):
        check_keys(c, CONFLICT_KEYS, f"real_conflicts[{i}]")
        hashes = [f["hash"] for f in c["files"]]
        if len(set(hashes)) < 2:
            err(f"real_conflicts[{i}] sem ao menos dois hashes distintos")
        check_sorted([f["path"] for f in c["files"]], f"real_conflicts[{i}].files por path")
        for j, f in enumerate(c["files"]):
            check_keys(f, CFILE_KEYS, f"real_conflicts[{i}].files[{j}]")
            check_hash(f["hash"], f"real_conflicts[{i}].files[{j}].hash")
        rc_first_paths.append(min((f["path"] for f in c["files"]), key=lambda p: p.encode("utf-8")))
    check_sorted(rc_first_paths, "real_conflicts")

    ph_paths = []
    for i, p in enumerate(r["placeholders"]):
        check_keys(p, PLACEHOLDER_KEYS, f"placeholders[{i}] (path={p.get('path')!r})")
        kinds = p["kinds"]
        if len(kinds) != len(set(kinds)):
            err(f"placeholders[{i}].kinds com duplicatas")
        idx = [KIND_ORDER.index(k) for k in kinds if k in KIND_ORDER]
        if any(k not in KIND_ORDER for k in kinds):
            err(f"placeholders[{i}].kinds com valor desconhecido: {kinds}")
        elif idx != sorted(idx):
            err(f"placeholders[{i}].kinds fora da ordem canonica: {kinds}")
        if "hash" in p:
            err(f"placeholders[{i}] possui hash (proibido)")
        ph_paths.append(p["path"])
    check_sorted(ph_paths, "placeholders")

    if violations:
        print("VIOLACOES:")
        for v in violations:
            print(" -", v)
        sys.exit(1)
    print(f"OK: {sys.argv[1]} conforme docs/schema-report-v1.md")


if __name__ == "__main__":
    main()
