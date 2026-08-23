# Cloud Sync Conflict Doctor

> "Você tem 1.847 arquivos duplicados e 23 divergências reais nesta pasta. 1.812 são cópias idênticas — posso colocar em quarentena agora."

![status](https://img.shields.io/badge/status-em%20desenvolvimento-orange) ![stack](https://img.shields.io/badge/C%23-.NET%208-blueviolet) ![segurança](https://img.shields.io/badge/delete-direto%20nunca-red) ![licença](https://img.shields.io/badge/v1-gratuita-green)

**Autor:** André Santo (forg3) | junkyardgoodies.app

## Para que serve

Quem usa OneDrive, Google Drive, Dropbox, Nextcloud ou iCloud conhece a rotina: `relatório (conflicted copy).docx`, `planilha-DESKTOP-A1B2C3.xlsx`, `foto (1).jpg`, `foto (2).jpg`… O provedor de nuvem cria essas cópias em silêncio, nunca avisa qual versão é a boa, e a pasta apodrece por anos porque o usuário tem medo de apagar a errada.

O **Cloud Sync Conflict Doctor** escaneia uma pasta sincronizada local e responde, com evidência auditável:

- quantas cópias são **idênticas byte a byte** (podem ir para quarentena sem risco);
- quais são **divergências reais** do mesmo documento (precisam de decisão humana);
- quais arquivos são *placeholders* online-only (ele nem toca — ver abaixo);
- quanto espaço pode ser recuperado com segurança.

É uma ferramenta de decisão confiável para limpeza de árvores de sincronização — não mais um deduplicador burro que não entende conflito.

## Como funciona

Pipeline determinístico em cascata — o desempenho vem de **não ler** o que não precisa:

```text
LEVEL 0  Enumeração (só metadados: caminho, tamanho, mtime, atributos, file id)
         → placeholders OFFLINE / RECALL_* / reparse points são MARCADOS E PULADOS.
           Nunca são abertos: tocar neles dispararia download da nuvem inteira.
LEVEL 1  Agrupamento por (nome-base normalizado + tamanho)
         → grupo de 1 elemento? Descartado sem ler 1 byte.
         → normalização limitada e versionada dos sufixos de conflito
           ((conflicted copy), -DESKTOP-XXXX, " (1)", ~$, .sb-hex…)
LEVEL 2  Hash parcial BLAKE3 (primeiros 64 KiB + últimos 64 KiB) só nos sobreviventes
         → mata falso positivo "mesmo nome, mesmo tamanho, conteúdo diferente".
LEVEL 3  Hash completo BLAKE3 só nas colisões do nível 2
         → hash igual = duplicata idêntica | hash diferente = divergência real.
```

Garantias de projeto (todas testadas, não declaradas):

- **Determinismo byte-a-byte:** a mesma árvore produz o mesmo relatório JSON sempre — ordenação por caminho em bytes UTF-8 (nunca locale), empate resolvido por `mtime → tamanho → caminho`, zero paralelismo na decisão (paralelismo só na leitura).
- **Segurança antes de conveniência:** nada é apagado. Jamais. Toda remoção vira **quarentena datada** com manifesto (`original_path`, hash BLAKE3, motivo, regra) e **restore verificado**, que nunca sobrescreve arquivo existente silenciosamente.
- **BLAKE3 versionado:** algoritmo explícito no schema do relatório (`algorithm/hash_version/report_schema_version`); trocar hash é bump de formato.
- **Cache incremental SQLite** chaveado por file ID/inode — renomear/mover não invalida cache; o segundo scan é quase instantâneo.
- **Local-first absoluto:** zero upload, zero telemetria obrigatória, zero conteúdo enviado para qualquer serviço.
- **Calibração por mídia:** SSD/NVMe → paralelismo alto na leitura; HDD com seek penalty → leitura serial (detectado via IOCTL, configurável, nunca fixo).

Distribuição: CLI (`conflictdoctor scan <pasta> --json`) desde o dia um, GUI de consumidor como produto principal (fluxo Pasta → Scan → Resumo → Duplicatas → Conflitos → Comparar → Quarentena → Confirmação).

## Stack

C#/.NET 8 · solução `CloudSyncConflictDoctor.sln`: `src/Doctor.Core` (motor), `src/Doctor.Cli`, `src/Doctor.Gui` (Avalonia — hipótese de trabalho), `tests/Doctor.Tests`. SQLite via `Microsoft.Data.Sqlite`, hashing via Blake3.

## Estado atual (agosto/2026)

Concluído e revisado (**GATE 1 — Architecture Ready fechado**):

- ADRs 0001–0011 (linguagem, quarentena, schema, scanner, hashing, cache, concorrência, CLI, GUI, quarentena detalhada, comparador);
- Threat model com 11 casos concretos de destruição de dados e mitigação mapeada;
- Schema do relatório v1 fechado com fixture real (hashes BLAKE3 calculados);
- Estratégia de testes completa (suítes DET/PLH/QDT mapeadas para os gates);
- Harness de benchmark + gerador de dataset sintético versionado;
- Protótipo Level 0 funcional (enumeração cross-platform + marcação de placeholder na origem);
- Esqueleto de GUI navegável com as 9 telas do fluxo e linguagem visual de segurança (ação destrutiva sempre rotulada "Mover para quarentena", nunca "apagar"), ViewModels com 49 testes verdes.

Em execução: motor do scan (pipeline Level 0/1), gate de placeholder com telemetria zero-bytes, state machine da GUI, contrato de telemetria (bytes evitados).

## O que falta (roadmap até o release)

| Marco | Conteúdo | Gate |
|---|---|---|
| Motor completo | Level 1–3, classificação duplicata vs divergência | GATE 2 (Scanner Correctness) |
| Segurança | Path traversal, TOCTOU, reparse attacks, fail-closed | GATE 5 |
| Resolução | keep-newest/largest/machine/manual + empate determinístico | GATE 3 (Resolution Safety) |
| Performance | Benchmark 1M arquivos, telemetria de bytes evitados | GATE 4 |
| CI | GitHub Actions ubuntu+windows, suítes determinismo/placeholder/no-delete | GATE 6 |
| Distribuição v1 (gratuita) | Build self-contained win-x64 via GitHub Releases (sem assinatura de código — adiada para a v2 paga) | GATE 6 |
| Auditoria final | *"Can I trust the delete button?"* — revisão adversarial | pré-RC |

> **Estratégia comercial:** v1 é **gratuita** para validação em campo. Empacotamento com instalador assinado (MSIX/winget/Store), licensing Ed25519 e precificação ficam para a **v2**, quando o produto estiver provado.

## Estado atual (2026-08-23, tarde)

- **522 testes verdes** na `main`; cobertura 83.4% line-rate (coverlet)
- **CI local**: `./scripts/ci/local-ci.sh` — build Release + suíte completa + guardas estáticas
  (anti-`File.Delete`/`Directory.Delete`, anti-`Process.Start`, GUIVM-02 anti-rótulo-destrutivo)
- **Hardening S11 concluído:** PathCanonical (S11-1), ReparsePolicy (S11-2 via T18),
  TOCTOU+cache poisoning (S11-3/4 via T19), fail-closed com `UnresolvedGroup` auditável (S11-5),
  auditoria GATE 5 consolidada com matriz SEG-01..23 (S11-6) e SEG-12 verde
  (exclusão estrutural de `ConflictDoctor/` na enumeração)
- **Comparadores v1 completos:** texto (LCS), binário, markdown e CSV (ADR-0011)
- Benchmark round-01 sobre dataset real de 1M arquivos registrado em `docs/bench/rounds/`
- GATEs fechados: 1 (Architecture), 2 (Scanner Correctness); GATE 4 baseline registrado

## Ideias e questões abertas

- **Diff semântico de Office** (parágrafos/células/fórmulas via Open XML): entra no v1 ou é o gancho do Pro? Análise em curso no board.
- **Escopo de providers no v1**: os cinco de uma vez ou Windows-first progressivo? (arquitetura já desacoplada de vendor.)
- **Naming**: "Conflict Doctor" descreve mas não vende — pesquisa de nome/trademark planejada.
- Comparação CSV orientada a linha/coluna e diff markdown no comparador v1.
- Modo MSP/RMM: execução headless agendada com relatório JSON consolidado (o EXE é o produto; wrapper PowerShell é só implantação).

## Desenvolvimento

Este projeto é executado via **Hermes Kanban** (board `conflict-doctor`): cards com Definition of Done, dependências explícitas, gates formais de revisão e handoffs auditáveis. A especificação completa está em [`docs/SPEC.md`](docs/SPEC.md) e as decisões de arquitetura em [`docs/adr/`](docs/adr/).

```bash
# build e testes (requer .NET 8 SDK)
dotnet build CloudSyncConflictDoctor.sln
dotnet test tests/Doctor.Tests
```

---

**Regra de ouro do produto:** o usuário precisa chegar ao ponto de dizer *"eu sei exatamente por que estes arquivos foram escolhidos e sei que posso desfazer."*
