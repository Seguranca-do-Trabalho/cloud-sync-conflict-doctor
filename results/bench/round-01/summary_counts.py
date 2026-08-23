#!/usr/bin/env python3
"""Resumo de anomalias do relatorio da rodada (contagens por tipo).

Uso: python3 summary_counts.py <run1> <saida.json>
"""
import json
import sys


def main() -> int:
    src, dst = sys.argv[1], sys.argv[2]
    doc = json.loads(open(src, encoding="utf-8").read())
    resumo = {
        "grupos_candidatos": len(doc.get("groups", [])),
        "duplicatas_identicas": len(doc.get("identical_duplicates", [])),
        "conflitos_reais": len(doc.get("real_conflicts", [])),
        "arquivos_em_duplicatas": sum(
            len(g.get("files", [])) for g in doc.get("identical_duplicates", [])
        ),
        "arquivos_em_conflitos": sum(
            len(c.get("files", [])) for c in doc.get("real_conflicts", [])
        ),
        "placeholders_listados": len(doc.get("placeholders", [])),
        "erros_acesso": len(doc.get("errors", [])),
        "chaves_raiz": sorted(doc.keys()),
    }
    open(dst, "w", encoding="utf-8").write(json.dumps(resumo, indent=2) + "\n")
    print(json.dumps(resumo, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
