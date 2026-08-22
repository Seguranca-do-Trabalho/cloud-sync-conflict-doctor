#!/usr/bin/env python3
"""Card t_957718d4: gera a arvore sintetica do fixture report v1 (§10.1 do schema-report-v1.md).

A arvore e gravada em fixtures/report-v1-tree/ e este script fica FORA dela,
para que um scan do diretorio capture exatamente os arquivos do fixture.
"""
import os

FIXTURES = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.join(FIXTURES, "report-v1-tree")

def c1(i):
    return (i * 7 + 3) % 256

def main():
    c1_bytes = bytes(c1(i) for i in range(96))
    x_base = bytes(i % 251 for i in range(262144))
    x1 = bytearray(x_base)
    x2 = bytearray(x_base)
    for i in range(131072, 131088):
        x1[i] = 0xAA
        x2[i] = 0xBB
    n1 = b"notas da reuniao de planejamento - versao A\n"
    n2 = b"notas da reuniao de planejamento - versao B\n"
    l = b"Arquivo unico - sem duplicatas.\n"

    files = {
        "docs/foto-reuniao.jpg": c1_bytes,
        "docs/backup/foto-reuniao.jpg": c1_bytes,
        "fotos/foto-reuniao.jpg": c1_bytes,
        "projetos/orcamento.xlsx": bytes(x1),
        "projetos/orcamento-DESKTOP-ABC123 (conflicted copy).xlsx": bytes(x2),
        "notas/reuniao.txt": n1,
        "notas/arquivo morto/reuniao.txt": n2,
        "leiame.txt": l,
    }

    # placeholders simulados: apenas diretorios, sem conteudo versionado
    placeholder_dirs = [
        "arquivos grandes",
        "arquivo morto",
    ]

    existing_files = []
    for base, _dirs, fnames in os.walk(ROOT):
        for fn in fnames:
            existing_files.append(os.path.join(base, fn))
    for p in existing_files:
        os.unlink(p)

    for rel, data in sorted(files.items()):
        p = os.path.join(ROOT, rel.replace("/", os.sep))
        os.makedirs(os.path.dirname(p), exist_ok=True)
        with open(p, "wb") as f:
            f.write(data)

    for rel in placeholder_dirs:
        d = os.path.join(ROOT, rel.replace("/", os.sep))
        os.makedirs(d, exist_ok=True)

    print("Arvore gerada em", ROOT)
    total_bytes = 0
    for rel in sorted(files):
        print(len(files[rel]), rel)
        total_bytes += len(files[rel])
    print("total_conteudo_gravado =", total_bytes)

if __name__ == "__main__":
    main()
