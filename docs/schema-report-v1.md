# Schema do Relatório v1 — Definição Normativa

| Campo | Valor |
|---|---|
| Produto | Cloud Sync Conflict Doctor |
| Card | t_957718d4 (T03 — Deterministic report schema v1) |
| Data | 2026-08-22 |
| Revisão | 1.0 |
| Responsável | André Santo (forg3) — backend/architect |
| Status | Aceito |
| Estende | ADR-0003 (Schema do relatório v1 e regra de determinismo) |
| Normativo para | Doctor.Core (emissão), Doctor.Cli (`--json`), Doctor.Gui (consumo), Doctor.Tests (fixtures byte-a-byte) |

Este documento fecha o schema do relatório v1. Onde este documento e o ADR-0003 divergem em detalhe, este documento prevalece; nenhuma decisão aqui reverte o ADR-0003, apenas o torna operacional.

---

## 1. Convenções fundamentais

### 1.1 Codificação

- O documento é UTF-8, **sem BOM**.
- Quebras de linha: apenas `LF` (`0x0A`).
- O arquivo termina com exatamente um `\n`.

### 1.2 Caminhos

- Todo caminho dentro do relatório é **relativo a `generated_from.root_path`**, nunca absoluto.
- Separador sempre `/`, em todas as plataformas. Nunca `\`.
- Sem prefixo `./`, sem segmento vazio, sem `..`.
- O nome do arquivo é reproduzido **exatamente como existe no filesystem**: nenhuma normalização Unicode (NFC/NFD) é aplicada a nomes. Comparação e ordenação usam os bytes originais.
- `generated_from.root_path` é o eco literal do argumento recebido pelo CLI, sem canonização. Duas grafias diferentes da mesma árvore produzem relatórios diferentes — isso é variação de invocação, não violação de determinismo. O teste de determinismo usa a mesma string de invocação.

### 1.3 Ordenação canônica (normativa)

Ordem de bytes UTF-8 bruto (semântica de `memcmp`; equivale à ordem de pontos de código Unicode). Proibido usar collation, locale, `String.Compare` cultural ou normalização antes de ordenar.

Como UTF-8 preserva a ordem de pontos de código, comparar os bytes é igual a comparar pontos de código — a regra é independente de plataforma e locale por construção.

Regras por estrutura:

| Estrutura | Chave de ordenação |
|---|---|
| `groups` | `(normalized_base_name` em bytes, depois `size_bytes` ascendente`)` |
| `groups[].members` | `path` em bytes |
| `identical_duplicates` | menor `path` do conjunto, em bytes; empate impossível (ver §1.4) |
| `identical_duplicates[].files` | `path` em bytes |
| `real_conflicts` | menor `path` do conjunto, em bytes |
| `real_conflicts[].files` | `path` em bytes |
| `placeholders` | `path` em bytes |
| `placeholders[].kinds` | posição na ordem canônica declarada: `reparse_point` → `recall_on_data_access` → `recall_on_open` → `offline` |

Dentro de qualquer lista, a ordem é sempre por caminho quando a entrada tem caminho. Nenhuma lista é ordenada por descoberta, thread, tamanho isolado ou hash.

### 1.4 Totalidade das ordens

Caminhos são únicos dentro de um scan (um caminho identifica exatamente uma entrada), portanto a ordenação por caminho é total: **nenhum desempate adicional é necessário nem definido**. A regra `mtime → size → path` da especificação (§17) governa **decisões de resolução** executadas sobre conjuntos ordenados, não a ordenação do relatório. Não confundir os dois papéis.

### 1.5 Campos de tempo

- Os únicos campos de tempo de parede do documento são `generated_from.scan_started_utc` e `generated_from.scan_finished_utc`.
- **Nenhuma lista contém campo de tempo.** Em particular, `mtime` de arquivo **não aparece** no relatório v1: mtime varia com operações na árvore e sua presença em listas quebraria o teste byte-a-byte para estado de árvore estável.
- Formato dos dois campos: RFC 3339, UTC, largura fixa `YYYY-MM-DDTHH:MM:SS.mmmZ`, com `Z` maiúsculo e exatamente 3 dígitos de milissegundo.

### 1.6 Máscara para o teste de determinismo (§20 da SPEC)

Os dois campos de §1.5 são os únicos não determinísticos do documento. O teste canônico de determinismo procede assim:

1. Gerar os relatórios dos N scans na mesma árvore;
2. Em cada relatório, substituir o valor de `scan_started_utc` e de `scan_finished_utc` pela string literal `MASKED-FOR-DETERMINISM-TEST`;
3. Exigir igualdade **byte a byte** dos documentos mascarados.

Sem a máscara, nenhum par de scans reais pode ser byte-idêntico; a máscara faz parte da definição de pronto do teste, não uma concessão. Toda a demais estrutura — inclusive todos os contadores de telemetria, que são contagens e não tempos — deve coincidir sem máscara.

---

## 2. Formato exato de serialização

O formato é fixado aqui e **faz parte do schema**: alterar qualquer item desta seção exige bump de `report_schema_version`.

| Aspecto | Valor exato |
|---|---|
| Codificação | UTF-8 sem BOM |
| Fim de linha | `LF` (`\n`), inclusive o último |
| Indentação | 2 espaços por nível; nenhum tab |
| Separador | `": "` após chave (dois-pontos + um espaço); `,` sem espaço à direita |
| Chaves vazias | Objeto vazio imprime `{}`; array vazio imprime `[]`, ambos inline |
| Ordem das chaves | **Declarada** — a sequência de cada objeto é a das tabelas deste documento, e é proibido reordenar |
| Escapamento de strings | Mínimo RFC 8259: `\"`, `\\`, `\b`, `\f`, `\n`, `\r`, `\t` e `\u00XX` para demais caracteres de controle `< 0x20`; barra `/` **não** escapada; caracteres não-ASCII emitidos **literalmente** em UTF-8 (sem `\uXXXX`) |
| Números | Somente inteiros; notação decimal simples; sem sinal `+`, zeros à esquerda, expoentes ou casas decimais |
| Estrito | JSON estrito RFC 8259: sem comentários, sem vírgula pendente, sem `NaN` |

Implementação de referência em C#: `System.Text.Json.JsonSerializer` com `WriteIndented = true`, `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` (produz exatamente o escapamento acima) e POCOs cuja ordem de declaração das propriedades replica as tabelas abaixo — a ordem de declaração é a ordem de emissão. Anexar manualmente `\n` final.

---

## 3. Estrutura raiz

Todos os campos de todos os objetos são **obrigatórios e sempre presentes**. O v1 não possui campo opcional; listas podem ser vazias, objetos nunca. Consumidores podem indexar diretamente, sem navegação defensiva.

Chaves da raiz, na ordem declarada:

| # | Chave | Tipo | Descrição |
|---|---|---|---|
| 1 | `report_schema_version` | inteiro ≥ 1 | Constante `1` neste formato. Política de bump: §7. |
| 2 | `algorithm` | string | Constante `"BLAKE3"`. Nome do algoritmo de hash completo. |
| 3 | `hash_version` | inteiro ≥ 1 | Versão da definição de hash (algoritmo + receita). Constante `1`. |
| 4 | `normalization_rules_version` | inteiro ≥ 1 | Versão da lista fixa e ordenada de padrões de normalização de nomes de conflito aplicada neste scan. Constante `1`. |
| 5 | `generated_from` | objeto | Identificação da execução. Ver §4. |
| 6 | `telemetry` | objeto | Contadores do pipeline. Ver §5. |
| 7 | `groups` | array | Grupos candidatos do Level 1. Ver §6. |
| 8 | `identical_duplicates` | array | Classes de duplicatas idênticas (veredito final). Ver §6.1. |
| 9 | `real_conflicts` | array | Conjuntos com divergência real de conteúdo (veredito final). Ver §6.2. |
| 10 | `placeholders` | array | Entradas detectadas como placeholder/online-only e jamais abertas. Ver §6.3. |

`normalization_rules_version` foi acrescentado em relação ao exemplo do ADR-0003 porque o agrupamento (e logo o conteúdo de `groups`) depende dessa versão; é um terceiro eixo de versionamento independente (§7).

## 4. `generated_from`

Ordem declarada: `root_path`, `scan_started_utc`, `scan_finished_utc`.

| Chave | Tipo | Descrição |
|---|---|---|
| `root_path` | string | Eco literal do argumento do CLI (§1.2). |
| `scan_started_utc` | timestamp | Início do scan, formato de §1.5. |
| `scan_finished_utc` | timestamp | Fim do scan, formato de §1.5. Sempre ≥ `scan_started_utc`. |

## 5. `telemetry`

Contadores inteiros ≥ 0, largura de 64 bits. Ordens declaradas conforme tabela.

| Chave | Definição precisa |
|---|---|
| `files_enumerated` | Total de entradas de arquivo vistas no Level 0 (diretórios nunca contam). Inclui placeholders. |
| `files_placeholder` | Entradas classificadas como placeholder (atributos offline/recall ou reparse point) e excluídas de todo acesso a conteúdo. Subconjunto de `files_enumerated`. |
| `files_skipped` | Entradas não-placeholder que não tiveram **nenhum** byte lido: membros de grupos unitários (fora de `groups`) e nada mais. |
| `files_partial_hashed` | Entradas não-placeholder que receberam hash parcial no Level 2 (janela inicial 64 KiB + janela final 64 KiB). |
| `files_full_hashed` | Entradas que sobreviveram ao Level 2 e receberam BLAKE3 completo no Level 3. Subconjunto de `files_partial_hashed`. |
| `bytes_read` | `bytes_read_partial + bytes_read_full`. |
| `bytes_read_partial` | Bytes lidos durante o passe de hash parcial. Arquivos ≤ 128 KiB têm o arquivo inteiro coberto pelas duas janelas; a leitura conta integralmente aqui. |
| `bytes_read_full` | Bytes lidos durante o passe de hash completo. |
| `placeholder_bytes_read` | Bytes lidos de placeholders. **Invariante absoluta: sempre `0`.** Valor ≠ 0 é falha de segurança, não dado. |

Invariantes verificáveis (obrigatórios nos testes):

```text
files_enumerated == files_placeholder + files_skipped + files_partial_hashed
files_full_hashed <= files_partial_hashed
bytes_read       == bytes_read_partial + bytes_read_full
placeholder_bytes_read == 0
```

## 6. Listas de resultado

### 6.0 `groups` — grupos candidatos (Level 1)

Inventário completo dos grupos formados por `normalized_base_name + size` com **2 ou mais membros**. Um grupo aparece aqui independentemente do veredito posterior: o consumidor identifica o veredito pela presença/ausência do conjunto nas listas finais — grupo presente em `groups` e ausente de ambas as listas finais foi eliminado no Level 2 (conteúdo provavelmente diferente). Isso é a trilha de auditoria do pipeline.

Ordem declarada de cada elemento:

| Chave | Tipo | Descrição |
|---|---|---|
| `normalized_base_name` | string | Nome base após normalização de conflitos, extensão preservada. Produzido pela versão `normalization_rules_version`. |
| `size_bytes` | inteiro ≥ 0 | Tamanho compartilhado por todos os membros (chave de agrupamento). |
| `members` | array de objeto | Membros do grupo, ordenados por caminho (§1.3). |

`members` é a única forma de entrada sem hash do documento, deliberadamente: no momento do agrupamento só existe caminho. Elemento de `members`, ordem declarada:

| Chave | Tipo |
|---|---|
| `path` | string |

### 6.1 `identical_duplicates`

Um elemento por classe de equivalência por conteúdo pleno (BLAKE3 igual), com 2 ou mais arquivos. Só se forma a partir de grupo cujos hashes completos são **todos iguais**.

Ordem declarada de cada elemento:

| Chave | Tipo | Descrição |
|---|---|---|
| `hash` | string | BLAKE3 completo, 64 caracteres hexdeciais **minúsculos**. Igual para todos os membros por definição. |
| `size_bytes` | inteiro ≥ 0 | Tamanho comum. |
| `files` | array de string | Caminhos relativos, ordenados por bytes (§1.3). |

### 6.2 `real_conflicts`

Um elemento por grupo candidato cujos hashes completos apresentam **ao menos dois valores distintos** após sobreviver ao Level 2. O elemento cobre **todos** os membros do grupo, incluindo subconjuntos internamente idênticos: os hashes por arquivo permitem ao consumidor reconstruir os subagrupamentos. Grupo nunca gera entrada simultânea em `identical_duplicates` e `real_conflicts` — a classificação por grupo é mutuamente exclusiva.

Ordem declarada de cada elemento:

| Chave | Tipo | Descrição |
|---|---|---|
| `normalized_base_name` | string | Como em §6.0. |
| `size_bytes` | inteiro > 0 | Tamanho comum do grupo. |
| `files` | array de objeto | Todos os membros do grupo, ordenados por caminho. |

Elemento de `files`, ordem declarada:

| Chave | Tipo | Descrição |
|---|---|---|
| `path` | string | Caminho relativo. |
| `hash` | string | BLAKE3 completo individual, 64 hex minúsculos. |

### 6.3 `placeholders`

Entradas detectadas como placeholder/online-only antes de qualquer acesso a conteúdo. Nunca possuem hash — a ausência de hash é a prova do comportamento seguro.

Ordem declarada de cada elemento:

| Chave | Tipo | Descrição |
|---|---|---|
| `path` | string | Caminho relativo, ordenação global por bytes. |
| `kinds` | array de string | Rótulos detectados, sem duplicatas, na ordem canônica: `reparse_point`, `recall_on_data_access`, `recall_on_open`, `offline`. Valores permitidos: somente esses quatro. |
| `size_bytes` | inteiro ≥ 0 | Tamanho obtido por metadados de enumeração (Level 0), sem abrir o arquivo. |

---

## 7. Política de versionamento e bump

Três eixos independentes. Uma causa produz bump de **um** eixo, nunca de dois.

### 7.1 `report_schema_version`

Bump obrigatório (+1) quando mudar qualquer coisa que altere os **bytes possíveis** do documento:

- adicionar, remover, renomear ou aninhar campo;
- mudar tipo, obrigatoriedade, enumeração ou domínio de valores;
- mudar regra de ordenação, incluindo a ordem canônica de `kinds`;
- mudar qualquer item da §2 (indentação, escapamento, ordem de chaves, fim de linha);
- mudar a regra de caminhos relativos (§1.2) ou a máscara de determinismo (§1.6).

Política conservadora deliberada: **também há bump em mudança aditiva** (campo novo). Os consumidores são estritos e consomem exatamente uma versão; compatibilidade retroativa por tolerância a campo desconhecido não é objetivo do v1. Mudança de valor constante (`report_schema_version` de exemplo em docs) não é bump.

Consumidor que receber versão desconhecida deve recusar o documento com erro explícito, nunca tentar interpretar aproximadamente.

### 7.2 `hash_version`

Bump obrigatório quando mudar a **definição do hash exposto**:

- troca de algoritmo (`algorithm` muda junto — os dois campos são coerentes por definição);
- mudança no comprimento/truncamento do hash publicado;
- mudança na receita de hash parcial (janelas, composição) — mesmo que o hash completo não mude, pois invalida comparações históricas;
- introdução de hash chaveado.

Efeito colateral normativo: bump de `hash_version` **invalida o cache incremental** — linhas de cache cujo `hash_version` difere não podem ser reaproveitadas (SPEC §12: cache carrega `algorithm` e `hash_version`). Não causa bump de schema: os campos continuam existindo com o mesmo nome e tipo.

### 7.3 `normalization_rules_version`

Bump obrigatório quando mudar a lista fixa e ordenada de padrões de normalização de nomes: incluir, remover, reordenar padrões ou alterar a semântica de um padrão. Efeito: agrupamentos podem mudar (outro `groups` para a mesma árvore). **Não** causa bump de schema (estrutura intacta) **nem** de hash (definição de hash intacta). Relatórios gerados sob versões diferentes de normalização não são comparáveis entre si em `groups`, e sim em `identical_duplicates`/`real_conflicts` apenas quando os agrupamentos coincidirem — o consumidor deve tratar relatórios de versões distintas de normalização como séries separadas.

### 7.4 Matriz-resumo

| Causa | report_schema_version | hash_version | normalization_rules_version | Cache |
|---|---|---|---|---|
| Campo novo/removido/tipo/ordenação/serialização | bump | — | — | — |
| Algoritmo ou receita de hash muda | — | bump | — | invalidado |
| Padrão de normalização muda | — | — | bump | válido (hashes continuam corretos) |

---

## 8. Fórmulas derivadas para consumidores

Normativas para GUI e testes — implementações devem chegar aos mesmos valores:

```text
total_arquivos_duplicados_excedentes = Σ (len(files) - 1) sobre identical_duplicates
espaco_recuperavel_identico         = Σ ((len(files) - 1) * size_bytes) sobre identical_duplicates
conflitos_reais                     = len(real_conflicts)
arquivos_em_conflito                = Σ len(files) sobre real_conflicts
placeholders_ignorados              = len(placeholders)
```

Estas fórmulas respondem a primeira tela da GUI (SPEC §15). Por isso o v1 não traz bloco `summary`: toda soma derivável é proibida no documento, eliminando risco de inconsistência entre resumo e listas.

---

## 9. Fora de escopo do v1 (decisões explícitas de não-fazer)

- **Sugestão de qual arquivo manter** (`recommended_keep`): decisão de resolução pertence ao motor de resolução com estratégia escolhida pelo usuário (SPEC §17); o relatório é evidência, não política.
- **Bloco `summary`**: derivável por §8; redundância é risco de inconsistência.
- **`mtime` em entradas**: proibido em lista (§1.5).
- **Hash parcial no relatório**: a receita ainda será fechada pelo card de hashing; expô-la congelaria a receita antes da hora. A telemetria de bytes já audita o Level 2.
- **Correlação entre grupos de mesmo base name e tamanhos diferentes**: o agrupamento v1 é `base + size` (SPEC §7); variantes que divergiram em tamanho caem em grupos distintos e não são correlacionadas. Limitação conhecida, registrada; mudança aqui seria bump de schema em versão futura com card próprio.

---

## 10. Fixture oficial `fixtures/report-v1-exemplo.json`

### 10.1 Árvore sintética de referência

Raiz lógica: `report-v1-tree/` dentro de `fixtures/` (invocação simulada: `conflictdoctor scan fixtures/report-v1-tree --json`). Conteúdos são definidos por fórmula determinística e regeneráveis pelo script versionado `fixtures/generate-report-v1-tree.py` — que reside **fora** da árvore, para que um scan capture exatamente os arquivos do fixture. Os dois diretórios de placeholder existem vazios na árvore versionada (`arquivo morto`, `arquivos grandes`). Verificação permanente dos conteúdos: `sha256sum` sobre os 8 arquivos listados abaixo (§10.3).

| Caminho relativo | Conteúdo | Tamanho | Papel no fixture |
|---|---|---|---|
| `docs/foto-reuniao.jpg` | C1 | 96 B | Tríplice idêntica |
| `docs/backup/foto-reuniao.jpg` | C1 | 96 B | Tríplice idêntica |
| `fotos/foto-reuniao.jpg` | C1 | 96 B | Tríplice idêntica |
| `projetos/orcamento.xlsx` | X1 | 262144 B | Conflito real |
| `projetos/orcamento-DESKTOP-ABC123 (conflicted copy).xlsx` | X2 | 262144 B | Conflito real |
| `notas/reuniao.txt` | N1 | 44 B | Grupo eliminado no Level 2 |
| `notas/arquivo morto/reuniao.txt` | N2 | 44 B | Grupo eliminado no Level 2 |
| `leiame.txt` | L | 32 B | Arquivo único (`files_skipped`) |
| `arquivos grandes/video-aula.mp4` | — (não versionado) | ilustrativo | Placeholder simulado |
| `arquivo morto/relatorio antigo.docx` | — (não versionado) | ilustrativo | Placeholder simulado |

Definições de conteúdo (geração reproduzível pelo script):

```text
C1[i]           = (i * 7 + 3) mod 256,                i em [0, 96)
X1[i] = X2[i]   = i mod 251,                          i em [0, 262144)
X1[i]           = 0xAA para i em [131072, 131088)
X2[i]           = 0xBB para i em [131072, 131088)
N1              = "notas da reuniao de planejamento - versao A\n"   (44 bytes)
N2              = "notas da reuniao de planejamento - versao B\n"   (44 bytes)
L               = "Arquivo unico - sem duplicatas.\n"               (32 bytes)
```

X1 e X2 diferem apenas no intervalo de 16 bytes em `[131072, 131088)`, que está fora das janelas de hash parcial (primeiros 64 KiB = `[0, 65536)`; últimos 64 KiB = `[196608, 262144)`): hash parcial igual, hash completo diferente — é isto que caracteriza o conflito real no pipeline.

Os dois placeholders simulam metadados que só existem no Windows (`FILE_ATTRIBUTE_*`, reparse points) e por isso não têm arquivo versionado: seus `size_bytes` são ilustrativos e não verificáveis contra árvore Linux. Todos os demais campos do fixture são verificáveis contra os arquivos versionados.

### 10.2 Valores esperados

- `groups`: 3 grupos — `foto-reuniao.jpg` (3 membros), `orcamento.xlsx` (2 membros), `reuniao.txt` (2 membros), nesta ordem por `(base, size)` em bytes: `foto-reuniao.jpg` < `orcamento.xlsx` < `reuniao.txt`.
- `identical_duplicates`: 1 entrada (tríplice de `foto-reuniao.jpg`, menor caminho `docs/backup/foto-reuniao.jpg`).
- `real_conflicts`: 1 entrada (par `orcamento`).
- Grupo `reuniao.txt` presente em `groups` e ausente das duas listas finais: eliminado no Level 2.
- `placeholders`: 2 entradas, `arquivo morto/relatorio antigo.docx` antes de `arquivos grandes/video-aula.mp4` (ordem de bytes: `arquivo` < `arquivos`, porque o espaço `0x20` precede `s`).
- Telemetria: `files_enumerated=10`, `files_placeholder=2`, `files_skipped=1`, `files_partial_hashed=7`, `files_full_hashed=5`, `bytes_read_partial=262632` (288 + 2×131072 + 88), `bytes_read_full=524864` (288 + 2×262144), `bytes_read=787496`, `placeholder_bytes_read=0`.
- Os hashes BLAKE3 do fixture foram calculados com a biblioteca oficial (`Blake3`, NuGet) sobre exatamente os conteúdos de §10.1; não são ilustrativos.

### 10.3 Reprodução e verificação

Artefatos de verificação versionados neste repositório:

- `fixtures/generate-report-v1-tree.py` — regenera a árvore sintética exata da §10.1;
- `tools/report-v1-fixturegen/` — projeto C#/.NET 8 (pacote oficial `Blake3`, NuGet) que emite o JSON do fixture no formato exato da §2;
- `scripts/validate_report_v1.py` — validador estrutural do relatório v1 (ordem declarada das chaves, ordenação canônica por bytes UTF-8, invariantes de telemetria, formato dos hashes, ordem canônica de `kinds`); exit code 0 = conforme, 1 = violações listadas.

```text
python3 fixtures/generate-report-v1-tree.py
dotnet run --project tools/report-v1-fixturegen -- <raiz-do-repositorio>
python3 scripts/validate_report_v1.py fixtures/report-v1-exemplo.json
cd fixtures/report-v1-tree
sha256sum "docs/backup/foto-reuniao.jpg" "docs/foto-reuniao.jpg" "fotos/foto-reuniao.jpg" \
          "leiame.txt" "notas/arquivo morto/reuniao.txt" "notas/reuniao.txt" \
          "projetos/orcamento-DESKTOP-ABC123 (conflicted copy).xlsx" "projetos/orcamento.xlsx"
```

sha256 de referência (registrado na emissão deste documento):

```text
c9f1a5f79d7bea01a54f4edb41673722f627ee2e82dda324946b63cf4b9b16af  docs/backup/foto-reuniao.jpg
c9f1a5f79d7bea01a54f4edb41673722f627ee2e82dda324946b63cf4b9b16af  docs/foto-reuniao.jpg
c9f1a5f79d7bea01a54f4edb41673722f627ee2e82dda324946b63cf4b9b16af  fotos/foto-reuniao.jpg
8fc2d4ad5de8aad5d7c58bba230f634bbf35a6cda881b19b6d76a35b25f8f414  leiame.txt
f1aa8dbb2cc2b3fff8c8842ac17c2d2b5c1ae1feac32eb957bf3886f5358355b  notas/arquivo morto/reuniao.txt
1197d0e5fa85136614d806540720970c786340c6b4ad6fab3afe360259309ba4  notas/reuniao.txt
6384a8ea4e63a28461f4a83328e9ab1c1b13bf3c59b7fc4f7a1267c83a8b4c0d  projetos/orcamento-DESKTOP-ABC123 (conflicted copy).xlsx
1d73b17db393481a4bc4d5294de368995b2c5e4f2512b542433bbd90789cc56e  projetos/orcamento.xlsx
```

Qualquer alteração futura nestes conteúdos invalida o fixture e exige regeneração completa (conteúdos, hashes e telemetria juntos), mantendo a auto-consistência exigida por este documento.
