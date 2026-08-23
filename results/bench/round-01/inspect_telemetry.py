#!/usr/bin/env python3
"""Inspeciona telemetria de uma execucao (run.json do scanner).

Uso: python3 inspect_telemetry.py <arquivo.json> [mais.json ...]
Imprime os contadores do bloco report.telemetry + identificacao do relatorio.
"""
import json
import sys


def main() -> int:
    for path in sys.argv[1:]:
        with open(path, encoding="utf-8") as f:
            d = json.load(f)
        tel = d.get("report", {}).get("telemetry", d.get("telemetry"))
        print(f"== {path}")
        if tel is None:
            print("   SEM bloco telemetry. chaves raiz:", sorted(d.keys()))
            rep = d.get("report")
            if isinstance(rep, dict):
                print("   chaves report:", sorted(rep.keys()))
            continue
        for k in (
            "files_enumerated",
            "files_skipped",
            "files_placeholder",
            "files_partial_hashed",
            "files_full_hashed",
            "bytes_read",
            "bytes_read_partial",
            "bytes_read_full",
            "placeholder_bytes_read",
        ):
            if k in tel:
                print(f"   {k} = {tel[k]}")
        meta = {
            k: d.get("report", {}).get(k)
            for k in ("report_schema_version", "root", "generated_from")
            if isinstance(d.get("report"), dict)
        }
        top_meta = {k: d[k] for k in ("report_schema_version", "root", "generated_from") if k in d}
        print(f"   ident: {meta or top_meta}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
