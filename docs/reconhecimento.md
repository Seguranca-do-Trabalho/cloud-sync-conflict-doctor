# Reconhecimento do repositório e do ambiente — T00

| | |
|---|---|
| **Card** | t_76be6462 — T00 Repository reconnaissance |
| **Data** | 2026-08-22 |
| **Revisão** | 1 |
| **Responsável** | André Santo (forg3) \<andre@junkyardgoodies.app\> |
| **Escopo** | Somente leitura e registro. Nenhum arquivo de código criado ou alterado. |

Fontes lidas na íntegra nesta sessão antes de qualquer conclusão: `README.md`,
`docs/SPEC.md` (1822 linhas), `docs/adr/ADR-0001.md`, `docs/adr/ADR-0002.md`,
`docs/adr/ADR-0003.md`. Todos os comandos citados foram executados de fato; as
saídas resumidas abaixo são as retornadas pelo shell.

---

## 1. Estado encontrado

### 1.1 Repositório

- Repositório git em `/home/ubuntu/Projetos/Software/cloud-sync-conflict-doctor`, branch `main`.
- Commit único no histórico: `d32effd` — "PHASE 0: baseline da especificacao +
  ADRs 0001-0003 (linguagem, quarentena, schema deterministico)".
- Branch de trabalho deste card: `wt/t_76be6462` (worktree em
  `.worktrees/t_76be6462`), atualmente no mesmo commit da `main`.
- **Nenhum remote configurado** (`git remote -v` vazio) — consistente com a
  instrução de não fazer push.
- Arquivos rastreados no HEAD (total: 5):

```text
README.md
docs/SPEC.md
docs/adr/ADR-0001.md
docs/adr/ADR-0002.md
docs/adr/ADR-0003.md
```

- Diretório não rastreado `.repowise/` presente no worktree (índice de
  ferramenta, não faz parte do produto). Não foi adicionado ao commit.

### 1.2 Esqueleto da solução .NET

**Não existe.** Verificado por `ls`: ausentes `CloudSyncConflictDoctor.sln`,
`src/` e `tests/`. Registrado conforme escopo do card — nenhum projeto foi
criado aqui.

### 1.3 Documentação

- `README.md`: visão de produto completa (problema, pipeline L0-L3,
  paralelismo por mídia, cache por file id, riscos, decisões travadas).
- `docs/SPEC.md`: especificação operacional completa, incluindo os princípios
  não negociáveis (§2 quarentena+restore, §3 determinismo byte-a-byte,
  §6 placeholders intocados com `placeholder_bytes_read == 0`, §4 BLAKE3
  explícito, §26 zero telemetria/cloud, §52 anti-overengineering, §51
  prioridades correctness > safety > determinism > data preservation >
  performance > UX), gates (§45), TDD obrigatório (§19/§46) e PHASE 0 (§53).
- ADRs 0001–0003 decididos: stack C#/.NET 8 com solução
  `CloudSyncConflictDoctor.sln` (`src/Doctor.Core`, `src/Doctor.Cli`,
  `src/Doctor.Gui`, `tests/Doctor.Tests`); quarentena+restore sem delete
  direto; schema JSON do relatório v1 com regras de determinismo.

---

## 2. Ambiente de build (comandos executados)

| Item | Comando | Resultado |
|---|---|---|
| SDK .NET | `dotnet --version` / `dotnet --list-sdks` | **8.0.424** em `/home/ubuntu/.dotnet/dotnet`; único SDK instalado: 8.0.424 |
| Runtimes | `dotnet --list-runtimes` | NETCore.App 8.0.30; AspNetCore.App 8.0.30 |
| Git | `git --version` | 2.43.0 |
| SO | `/etc/os-release` | Ubuntu 24.04.4 LTS, kernel 6.17.0-1018-oracle |
| CPU/RAM | `nproc`, `free -h` | 4 núcleos, 23 GiB total (~19 GiB disponíveis) |
| Disco | `df -h` | `/`: 173 GiB disponíveis (11% usado) |

Observações de ambiente:

- O SDK está em `~/.dotnet`; se o PATH da sessão não o incluir,
  `export PATH=$HOME/.dotnet:$PATH`.
- Cache NuGet local (`~/.nuget/packages`, 98 pacotes) já contém xunit completo
  (`xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk` via
  `microsoft.net.test.sdk`) e runtimes win-x64 — herança do SMB Speed Doctor.
  **Não contém** `Blake3` nem `Microsoft.Data.Sqlite`.
- Conectividade com NuGet verificada:
  `curl https://api.nuget.org/v3/index.json` → HTTP 200. Restore de pacotes
  novos funciona.

### 2.1 Git config

`git config user.name` e `git config user.email` retornaram **vazio**, tanto no
escopo global quanto no repositório/worktree. Sem configuração, commits saem
com identidade inválida ou herdam valor do sistema. Os cards de implementação
devem configurar por repositório antes do primeiro commit:

```bash
git config user.name "Andre Santo (forg3)"
git config user.email "andre@junkyardgoodies.app"
```

(Este card configurou isso no próprio worktree para o commit do documento;
cards futuros precisam repetir no deles ou fixar uma vez no repo principal.)

---

## 3. Lacunas frente ao checklist PHASE 0 (SPEC §53)

| Dimensão (§53) | Estado atual | Lacuna |
|---|---|---|
| Linguagem | Decidida (ADR-0001): C#/.NET 8. SDK 8.0.424 disponível | Nenhuma na decisão; falta materializar projetos |
| Build system | Nenhum — sem `.sln`, sem `.csproj` | Criar solução e os 4 projetos (T01/T07) |
| Testes existentes | Nenhum — sem projeto de teste; harness xunit presente no cache NuGet | Criar `tests/Doctor.Tests` já com TDD desde o primeiro commit de código |
| CI | Inexistente — nenhum workflow/pipeline no repo | Definir CI mínimo (build + testes + teste de determinismo) — dependente da existir build |
| Estrutura de diretórios | Só `docs/`; sem `src/` nem `tests/` | Criar árvore exata do ADR-0001 |
| Documentação | README + SPEC + 3 ADRs completos | Nenhuma bloqueante; ADRs restantes do §25 (UI framework, scanner, hashing, SQLite, concorrência, cache, CLI, GUI, quarantine, comparator, packaging, updater, signing, licensing, telemetry, report schema) ainda pendentes nos cards T01+ |
| Dependências | Nenhuma declarada. Cache tem xunit; falta `Blake3` e `Microsoft.Data.Sqlite` (NuGet acessível, HTTP 200) | Declarar no primeiro restore; pinar versões explícitas |

Resumo: o repositório está na condição prevista pela SPEC para o início da
implementação — documentação pronta, código zero. A lacuna integral é a
materialização da solução esqueleto e sua infraestrutura (build, testes, CI).

---

## 4. Riscos de ambiente

1. **Host Linux desenvolvendo produto Windows-first.** Todo o requisito de
   Windows-native (FindFirstFileEx, FILE_ATTRIBUTE_OFFLINE /
   RECALL_ON_OPEN / RECALL_ON_DATA_ACCESS, reparse points, file IDs,
   IOCTL_STORAGE_QUERY_PROPERTY) é **intestável neste host**: atributos de
   placeholder NTFS e P/Invoke Win32 não existem aqui. Risco de falso "pronto"
   se os testes rodarem só no Linux. Mitigação: contratos isolados atrás de
   interfaces (já previsto no ADR-0001), testes de placeholder simulados por
   contrato + bateria dedicada em runner Windows (CI) antes dos gates 2 e 5.
2. **Git identity vazia por padrão.** Primeiro commit de cada worktree falha
   ou sai com autor errado se não configurado. Barato de corrigir, caro se
   esquecido (histórico sujo).
3. **Dependências nativas no cache ausente.** `Blake3` (binding nativo) e
   `Microsoft.Data.Sqlite` exigem download no primeiro restore. NuGet
   acessível hoje; risco apenas se o ambiente ficar offline.
4. **Um único SDK (8.0.424).** Qualquer card que fixar `net8.0` funciona; um
   upgrade acidental de TargetFramework para 9/10 quebraria o build aqui.
5. **`.repowise/` solto no worktree.** Artefato de ferramenta não rastreado;
   adicionar ao `.gitignore` no card que criar o esqueleto para não vazar
   para commits futuros.
6. **Referências externas quebradas no README.** `../CANAIS.md` e
   `../PRECIFICACAO.md` não existem no repo (verificado com `ls`). Ou são
   documentos irmãos fora do repositório ou pendentes; não bloqueiam T01-T07.

---

## 5. Recomendações imediatas para T01-T07

1. **T01 (architecture baseline)**: primeiro ato técnico = `dotnet new sln` +
   os 4 projetos do ADR-0001 + `.gitignore` cobrindo `bin/`, `obj/`,
   `.repowise/` + config de identidade git no repo principal. Solução deve
   compilar com `dotnet build` e `dotnet test` verdes (mesmo que com zero ou
   um teste canário) antes de qualquer lógica.
2. **Fixar versões explícitas** de `Blake3` e `Microsoft.Data.Sqlite` nos
   csproj no primeiro uso; nunca faixa flutuante — determinismo de build é
   extensão do determinismo do relatório.
3. **T05 (test strategy)**: definir desde já como os testes Windows-only serão
   marcados/filtrados (ex.: trait `[Trait("Platform","Windows")]`) para o CI
   Linux não mascarar cobertura falsa de placeholder/reparse.
4. **T03 (report schema)**: partir do JSON do ADR-0003; incluir
   `placeholder_bytes_read == 0` como invariante testada, não campo decorativo.
5. **T04 (benchmark harness)**: gerador versionado de dataset pode ser escrito
   e testado em Linux (geração de árvore sintética é portável); só as métricas
   de I/O Windows-native precisam de runner Windows.
6. **Ordem de desbloqueio confirmada**: nada no estado atual impede T01-T07 em
   paralelo após o esqueleto; a única precedência dura é esqueleto → todo o
   resto.
