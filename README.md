# Cloud Sync Conflict Doctor

> "Você tem 1.847 arquivos duplicados e 23 divergências reais nesta pasta.
> 1.812 são cópias idênticas — posso apagar agora."

## Problema

OneDrive, Google Drive, Dropbox, Nextcloud e iCloud criam, silenciosamente:

- `arquivo (conflicted copy).docx`
- `arquivo-DESKTOP-A1B2C3.xlsx`
- `arquivo (1).pdf`, `arquivo (2).pdf`
- versões divergentes do mesmo documento em máquinas diferentes
- arquivos "online only" que quebram scripts e backups

O usuário nunca sabe qual é a versão boa. Então não apaga nenhuma. A pasta
apodrece por anos.

## Produto

Escaneia uma pasta sincronizada **local** e monta a árvore de versões:

1. **Agrupa** por nome-base, desfazendo os sufixos de conflito de cada fornecedor.
2. **Classifica** cada grupo por hash:
   - duplicata idêntica → lixo, apagar em lote com um clique;
   - divergência real → precisa de decisão humana.
3. **Compara** as divergências: diff de texto; para Office, diff do XML interno
   (parágrafos/células que mudaram, não bytes).
4. **Resolve** em lote: manter a mais recente, manter a maior, manter a de tal
   máquina, ou escolher item a item.

Sempre com "desfazer": nada é apagado, vai para uma quarentena datada.

## Especificação do scan — determinístico e de alto desempenho

### O que "determinístico" tem que significar aqui

Não é vago: **a mesma árvore de arquivos produz um relatório byte a byte
idêntico, em qualquer máquina, em qualquer ordem de disco.** Isso é testável
e é a garantia que permite o usuário confiar num botão que apaga arquivo.

Consequências de projeto, todas obrigatórias:

- Nenhuma decisão depende da ordem de enumeração do sistema de arquivos.
- Nenhuma ordem de iteração de `HashMap` vaza para a saída — ordenar por
  caminho (bytes, não locale) antes de emitir qualquer coisa.
- Hash fixo e versionado no relatório (BLAKE3). Trocar de hash é mudança de
  versão do formato, não detalhe interno.
- Empate resolvido por regra escrita e estável (mtime, depois tamanho, depois
  caminho), nunca por "o que apareceu primeiro".
- Zero paralelismo na *decisão*. O paralelismo fica só na leitura; a
  classificação acontece sobre um conjunto já ordenado.

### Pipeline em cascata — o desempenho vem de não ler

O erro clássico é hashear tudo. A pasta tem 400 GB; 99% dela é irrelevante.

**Nível 0 — enumeração (I/O de metadado apenas)**

Uma passada só, coletando `(caminho, tamanho, mtime, atributos, file id)`.
Tamanho e mtime **já vêm** na enumeração de diretório — não custa `stat`
adicional. No Windows, `FindFirstFileEx` com `FindExInfoBasic` +
`FIND_FIRST_EX_LARGE_FETCH` (dispensa o nome 8.3 e busca em lote).

**Pular obrigatoriamente**, antes de qualquer outra coisa:

- `FILE_ATTRIBUTE_OFFLINE`
- `FILE_ATTRIBUTE_RECALL_ON_OPEN`
- `FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS`
- reparse points em geral

Esses são os placeholders de "Arquivos sob Demanda". **Tocar neles dispara
download.** Um scan ingênuo baixa a nuvem inteira do usuário e estoura a
franquia dele. Isto não é otimização — é requisito de correção.

**Nível 1 — agrupamento (custo zero de I/O)**

Agrupar por `(nome-base normalizado, tamanho)`. Grupo com 1 elemento é
descartado na hora. Isto elimina a esmagadora maioria dos arquivos sem ler
um byte de conteúdo.

A normalização do nome-base é a **única** parte com conhecimento por
fornecedor: uma lista ordenada de ~8 expressões (`(conflicted copy)`,
`-DESKTOP-XXXX`, ` (1)`, `~$`, `.sb-<hex>`, etc.). Bounded e estável.

**Nível 2 — hash parcial (só nos sobreviventes)**

Primeiros 64 KiB + últimos 64 KiB. Mata quase todo falso positivo de
"mesmo nome, mesmo tamanho, conteúdo diferente" a duas leituras por arquivo.

**Nível 3 — hash completo (só nas colisões do nível 2)**

BLAKE3 no arquivo inteiro. Aqui sobra tão pouco que o custo some.

Resultado: o trabalho pesado acontece em ordem de grandeza menor que a pasta.

### Paralelismo — e o detalhe que a maioria erra

- **Enumeração: uma thread por volume.** Listar diretório é metadado; várias
  threads brigando pela mesma MFT deixam mais lento, não mais rápido.
- **Hashing: pool limitado.** E o limite depende da mídia:
  - NVMe/SSD → paralelismo alto ajuda (fila profunda);
  - HDD → paralelismo **destrói** o throughput (seek thrashing). Serializar.
  - Detectar com `IOCTL_STORAGE_QUERY_PROPERTY` /
    `StorageDeviceSeekPenaltyProperty`. Tem penalidade de seek = disco
    girante = uma thread de leitura.

Isto é uma calibração de hardware real, não um número escolhido no papel:
deixar o valor configurável e medido, nunca fixo no código.

### Cache incremental — o segundo scan é instantâneo

SQLite local: `(file id, tamanho, mtime) → hash`. Rescan só re-lê o que mudou.
Chave é o **file id** (`FileIndex`/inode), não o caminho — assim renomear e
mover não invalida nada, que é justamente o que a sincronização faz o tempo
todo.

### Como medir (antes de otimizar qualquer coisa)

Alvo declarado e verificável, não estimativa de marketing:

- Árvore sintética de 1.000.000 de arquivos, gerada por script versionado.
- Métricas: tempo de parede, arquivos lidos de fato, bytes lidos de fato,
  pico de RSS.
- **Teste de determinismo no CI:** rodar o scan 3x na mesma árvore, com ordem
  de enumeração embaralhada artificialmente, e exigir saída idêntica. Se
  falhar, é bug de correção, não de desempenho.
- **Teste de placeholder:** árvore com arquivos marcados como offline; o scan
  tem que terminar com **zero** bytes lidos deles.

Os dois últimos testes valem mais que qualquer benchmark.

## Por que passa no filtro "baixa manutenção + lucro alto"

- **A menor manutenção das três ideias.** Os padrões de nome de conflito são
  determinísticos e mudam quase nunca.
- **Não depende de API de fornecedor nenhum no v1.** Só do sistema de arquivos.
  Isso mata de uma vez: OAuth, quota, rate limit, mudança de API, revisão de app.
- **Infra: US$ 0/mês.** Local-first, licença + site estático.
- **Base gigantesca:** qualquer pessoa com nuvem sincronizada. Concorrência
  praticamente nula — as ferramentas existentes são de-duplicadores burros que
  não entendem o conceito de conflito de sincronização.

## Riscos

- **A dor é chata, não urgente.** É o oposto do Printer Rescue. Ninguém acorda
  querendo resolver isso. A conversão depende de um scan grátis que mostre o
  estrago em números — o susto é o gatilho da compra.
- Apagar arquivo do usuário é a operação mais perigosa que existe. Quarentena
  obrigatória, nunca delete direto, nem em modo "limpar tudo".
- Arquivos "online only" (placeholders) não têm conteúdo local para hashear.
  Precisa detectar o atributo de reparse point e tratar separadamente, sem
  disparar download de 400 GB sem querer.

## Decisões travadas

- **Interface: GUI de verdade.** É o único dos quatro cujo comprador é usuário
  final. UI/UX decente não é enfeite aqui — é o produto. Um CLI mataria a
  venda.
  *Ainda assim, expor um `--json` para o scan:* custa pouco e mantém a porta
  aberta para o kit de técnico.
- **Preço: US$ 9,90, venda única, updates gratuitos.** É o produto de volume.
- **Scan e relatório completos de graça.** O susto é o gatilho da compra;
  cobrar pelo diagnóstico mata o funil.
- **Microsoft Store: sim, e é o único dos quatro em que ela realmente serve.**
  Público consumidor, zero atrito de política (não toca driver nem serviço),
  0% de comissão usando Paddle.

## A decidir

- [ ] Cobrar por licença vitalícia (compra única) ou assinatura?
- [ ] O diff semântico de Office entra no v1 ou é o gancho do Pro?
- [ ] Escopo do v1: só OneDrive + Google Drive, ou os cinco de uma vez?
- [ ] Nome. "Conflict Doctor" descreve, mas não vende.

## Fonte

`grok.md` §2.8 e `ideias_apps_problemas_cronicos_ti_refinada.md` §2.8
(5/5 automação, 5/5 valor, 5º lugar geral). Subiu na nossa lista por ser o de
menor custo de manutenção do conjunto todo.

---

**Distribuição e venda:** ver [`../CANAIS.md`](../CANAIS.md) — canal MSP, RMM, Microsoft Store e recebimento.

**Preço e modelo:** ver [`../PRECIFICACAO.md`](../PRECIFICACAO.md).
