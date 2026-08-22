#!/usr/bin/env python3
"""Testes do gerador de dataset sintetico (card t_2c4c7be7).

Execucao:
    python3 -m unittest discover -s scripts/bench -p 'test_*.py'

Somente stdlib. Cada teste gera arvores em diretorios temporarios.
"""

from __future__ import annotations

import hashlib
import importlib.util
import io
import json
import contextlib
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

_SCRIPT = Path(__file__).resolve().parent / "generate_dataset.py"

_spec = importlib.util.spec_from_file_location("generate_dataset", _SCRIPT)
assert _spec is not None and _spec.loader is not None  # arquivo fixo existente
gen = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(gen)


def _run(tmp: str, **kw):
    """Constroi plano + materializa em tmp; devolve (plan, result)."""
    plan = gen.build_plan(
        files=kw["files"],
        dup_groups=kw["dup_groups"],
        conflict_names=kw["conflict_names"],
        placeholders=kw["placeholders"],
        depth=kw["depth"],
        seed=kw["seed"],
    )
    result = gen.materialize(plan, Path(tmp))
    return plan, result


def _todos_arquivos(res: dict) -> list[str]:
    """Catalogo plano de caminhos relativos criados (sem sidecars).

    Conflitos chegam como pares (a, b); duplicatas como listas de 2+.
    """
    saida: list[str] = []
    for item in res["files"]["unique"]:
        saida.append(item)
    for item in res["files"]["conflicts"]:
        if isinstance(item, str):
            saida.append(item)
        else:
            saida.extend(item)
    for grupo in res["files"]["duplicates"]:
        saida.extend(grupo)
    for item in res["files"]["placeholders"]:
        saida.append(item)
    return saida


class TestContagens(unittest.TestCase):
    def test_contagens_conferem_com_parametros(self):
        with tempfile.TemporaryDirectory() as tmp:
            _, res = _run(
                tmp,
                files=60,
                dup_groups=5,
                conflict_names=8,
                placeholders=4,
                depth=2,
                seed=1,
            )
            c = res["counts"]
            self.assertEqual(c["files_created"], 60)
            self.assertEqual(c["placeholders"], 4)
            self.assertEqual(c["duplicate_groups"], 5)
            self.assertEqual(c["conflict_groups"], 8)
            self.assertEqual(c["conflict_files"], 16)  # 2 arquivos por grupo
            # categorias particionam o total
            self.assertEqual(
                c["unique_files"]
                + c["duplicate_files"]
                + c["conflict_files"]
                + c["placeholders"],
                60,
            )

    def test_arvore_nao_excede_profundidade(self):
        with tempfile.TemporaryDirectory() as tmp:
            _, res = _run(
                tmp,
                files=80,
                dup_groups=4,
                conflict_names=6,
                placeholders=2,
                depth=3,
                seed=5,
            )
            for rel in _todos_arquivos(res):
                profundidade = len(Path(rel).parts) - 1  # partes de diretorio
                self.assertLessEqual(profundidade, 3, rel)
            self.assertGreater(res["counts"]["dirs_created"], 0)

    def test_todo_arquivo_criado_esta_catalogado(self):
        """Nenhum arquivo alem dos catalogados e dos sidecars pode existir."""
        with tempfile.TemporaryDirectory() as tmp:
            _, res = _run(
                tmp,
                files=50,
                dup_groups=4,
                conflict_names=6,
                placeholders=3,
                depth=2,
                seed=9,
            )
            catalogados = set(_todos_arquivos(res))
            no_disco = set()
            for raiz, _dirs, arqs in os.walk(tmp):
                for a in arqs:
                    p = Path(raiz, a)
                    no_disco.add(p.relative_to(tmp).as_posix())
            esperado = catalogados | {
                s + ".placeholder-meta.json" for s in res["files"]["placeholders"]
            }
            self.assertEqual(no_disco, esperado)

    def test_catalogo_sem_caminhos_repetidos(self):
        """Colisao intra-grupo sobrescreveria arquivo em silencio (bug 500/525
        encontrado na fumaca); catalogo precisa ser livre de duplicatas e a
        arvore criada precisa ter exatamente files_created itens + sidecars."""
        with tempfile.TemporaryDirectory() as tmp:
            _, res = _run(
                tmp,
                files=120,
                dup_groups=10,
                conflict_names=20,
                placeholders=8,
                depth=3,
                seed=42,
            )
            catalogo = _todos_arquivos(res)
            self.assertEqual(len(catalogo), len(set(catalogo)))
            no_disco = sum(
                len(arqs)
                for _raiz, _dirs, arqs in os.walk(tmp)
            )
            self.assertEqual(
                no_disco,
                len(catalogo) + res["counts"]["sidecars_written"],
            )


class TestDuplicatasEConflitos(unittest.TestCase):
    def test_grupo_de_duplicata_tem_conteudo_identico(self):
        with tempfile.TemporaryDirectory() as tmp:
            _, res = _run(
                tmp,
                files=70,
                dup_groups=6,
                conflict_names=4,
                placeholders=2,
                depth=2,
                seed=11,
            )
            self.assertTrue(res["files"]["duplicates"])
            for grupo in res["files"]["duplicates"]:
                self.assertGreaterEqual(len(grupo), 2)
                hashes = {
                    hashlib.blake2b(Path(tmp, g).read_bytes()).hexdigest()
                    for g in grupo
                }
                self.assertEqual(len(hashes), 1, grupo)

    def test_conflito_real_mesmo_nome_normalizado_conteudo_diferente(self):
        with tempfile.TemporaryDirectory() as tmp:
            _, res = _run(
                tmp,
                files=70,
                dup_groups=4,
                conflict_names=9,
                placeholders=2,
                depth=2,
                seed=13,
            )
            self.assertEqual(res["counts"]["conflict_groups"], len(res["files"]["conflicts"]))
            for i, grupo in enumerate(res["files"]["conflicts"]):
                self.assertGreaterEqual(len(grupo), 2)
                normados = {gen.normalize_conflict_stem(Path(g).stem) for g in grupo}
                self.assertEqual(len(normados), 1, grupo)
                hashes = {
                    hashlib.blake2b(Path(tmp, g).read_bytes()).hexdigest()
                    for g in grupo
                }
                self.assertEqual(len(hashes), len(grupo), grupo)

    def test_par_de_conflito_mesmo_tamanho_alternado(self):
        """Grupos com numero par no identificador (relatorio_gNNN) trazem
        par mesmo-tamanho/conteudo-distinto (pior caso para o filtro de
        tamanho do pipeline); impares, tamanhos diferentes."""
        import re

        with tempfile.TemporaryDirectory() as tmp:
            _, res = _run(
                tmp,
                files=80,
                dup_groups=4,
                conflict_names=10,
                placeholders=2,
                depth=2,
                seed=17,
            )
            for grupo in res["files"]["conflicts"]:
                numero = int(re.search(r"_g(\d+)", grupo[0]).group(1))
                mesmo_tamanho = any(
                    Path(tmp, a).stat().st_size == Path(tmp, b).stat().st_size
                    for ia, a in enumerate(grupo)
                    for b in grupo[ia + 1 :]
                )
                tamanhos_diferentes = not mesmo_tamanho
                if numero % 2 == 0:
                    self.assertTrue(mesmo_tamanho, grupo)
                else:
                    self.assertTrue(tamanhos_diferentes, grupo)


class TestPlaceholders(unittest.TestCase):
    def test_sidecar_registro_completo(self):
        with tempfile.TemporaryDirectory() as tmp:
            _, res = _run(
                tmp,
                files=40,
                dup_groups=3,
                conflict_names=4,
                placeholders=5,
                depth=2,
                seed=19,
            )
            self.assertEqual(len(res["files"]["placeholders"]), 5)
            for rel in res["files"]["placeholders"]:
                sc = Path(tmp, rel + ".placeholder-meta.json")
                self.assertTrue(sc.is_file(), rel)
                meta = json.loads(sc.read_text(encoding="utf-8"))
                for chave in (
                    "file",
                    "simulated_attributes",
                    "provider",
                    "original_size",
                    "note",
                ):
                    self.assertIn(chave, meta)
                self.assertIn(
                    meta["simulated_attributes"],
                    gen.PLACEHOLDER_ATTRIBUTES,
                )

    def test_stub_placeholder_tem_marcador_legivel(self):
        """Se o scanner abrir placeholder por engano, le marcador (bytes > 0)
        e a violacao fica detectavel pelo benchmark."""
        with tempfile.TemporaryDirectory() as tmp:
            _, res = _run(
                tmp,
                files=30,
                dup_groups=2,
                conflict_names=2,
                placeholders=2,
                depth=1,
                seed=23,
            )
            for rel in res["files"]["placeholders"]:
                dados = Path(tmp, rel).read_bytes()
                self.assertIn(b"PLACEHOLDER", dados)


class TestDeterminismo(unittest.TestCase):
    def test_mesmo_seed_mesma_arvore(self):
        with tempfile.TemporaryDirectory() as t1, tempfile.TemporaryDirectory() as t2:
            _, r1 = _run(t1, files=60, dup_groups=5, conflict_names=8,
                         placeholders=3, depth=3, seed=42)
            _, r2 = _run(t2, files=60, dup_groups=5, conflict_names=8,
                         placeholders=3, depth=3, seed=42)
            self.assertEqual(r1["counts"], r2["counts"])

            def pegadas(base, res):
                saida = []
                for rel in _todos_arquivos(res):
                    st = Path(base, rel).stat()
                    h = hashlib.blake2b(
                        Path(base, rel).read_bytes(), digest_size=16
                    ).hexdigest()
                    saida.append((rel, h, st.st_size, st.st_mtime_ns))
                return sorted(saida)

            self.assertEqual(pegadas(t1, r1), pegadas(t2, r2))

    def test_seeds_diferentes_geram_arvores_diferentes(self):
        with tempfile.TemporaryDirectory() as t1, tempfile.TemporaryDirectory() as t2:
            _, r1 = _run(t1, files=40, dup_groups=3, conflict_names=4,
                         placeholders=2, depth=2, seed=100)
            _, r2 = _run(t2, files=40, dup_groups=3, conflict_names=4,
                         placeholders=2, depth=2, seed=101)

            def conteudos(base, res):
                return sorted(
                    hashlib.blake2b(Path(base, rel).read_bytes(),
                                    digest_size=16).hexdigest()
                    for rel in _todos_arquivos(res)
                )

            self.assertNotEqual(conteudos(t1, r1), conteudos(t2, r2))


class TestNormalizacao(unittest.TestCase):
    def test_padroes_da_spec_normalizam_igual(self):
        casos = [
            ("Relatorio", "Relatorio (1)"),
            ("Relatorio", "Relatorio (2)"),
            ("Relatorio", "Relatorio (conflicted copy)"),
            ("Relatorio", "Relatorio-DESKTOP-ABCD"),
            ("Relatorio", "$~".replace("$~", "~$") + "Relatorio"),
            ("Relatorio", "Relatorio.sb-1a2b3c"),
            ("Relatorio", "Relatorio~"),
        ]
        for esperado, bruto in casos:
            self.assertEqual(esperado, gen.normalize_conflict_stem(bruto), bruto)


class TestCli(unittest.TestCase):
    def test_cli_emite_json_e_codigo_zero(self):
        with tempfile.TemporaryDirectory() as tmp:
            proc = subprocess.run(
                [
                    sys.executable,
                    str(_SCRIPT),
                    "--root",
                    tmp,
                    "--files",
                    "24",
                    "--dup-groups",
                    "3",
                    "--conflict-names",
                    "4",
                    "--placeholders",
                    "2",
                    "--depth",
                    "2",
                    "--seed",
                    "7",
                ],
                capture_output=True,
                text=True,
            )
            self.assertEqual(proc.returncode, 0, proc.stderr)
            resumo = json.loads(proc.stdout)
            self.assertEqual(resumo["counts"]["files_created"], 24)
            self.assertEqual(resumo["parameters"]["seed"], 7)

    def test_parametros_inviaveis_rejeitados(self):
        with tempfile.TemporaryDirectory() as tmp:
            proc = subprocess.run(
                [
                    sys.executable,
                    str(_SCRIPT),
                    "--root",
                    tmp,
                    "--files",
                    "3",
                    "--dup-groups",
                    "2",
                    "--seed",
                    "7",
                ],
                capture_output=True,
                text=True,
            )
            self.assertEqual(proc.returncode, 2)
            self.assertTrue(proc.stderr.strip())

    def test_main_retorna_resumo_sem_tocar_stdout_quiet(self):
        with tempfile.TemporaryDirectory() as tmp:
            buffer = io.StringIO()
            with contextlib.redirect_stdout(buffer):
                codigo = gen.main(
                    [
                        "--root",
                        tmp,
                        "--files",
                        "20",
                        "--dup-groups",
                        "2",
                        "--conflict-names",
                        "2",
                        "--placeholders",
                        "1",
                        "--depth",
                        "1",
                        "--seed",
                        "3",
                        "--quiet",
                    ]
                )
            self.assertEqual(codigo, 0)
            self.assertEqual(buffer.getvalue(), "")


if __name__ == "__main__":
    unittest.main()
