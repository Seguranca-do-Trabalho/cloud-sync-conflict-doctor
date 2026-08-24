# Diretrizes de Código Inspiradas em Karpathy

Diretrizes comportamentais para reduzir erros comuns de LLM em código. Adaptadas de
[multica-ai/andrej-karpathy-skills](https://github.com/multica-ai/andrej-karpathy-skills)
(licença MIT), derivadas das observações de Andrej Karpathy sobre falhas típicas de
agentes de código. Mesclar com as instruções específicas do projeto conforme necessário.

**Trade-off:** estas diretrizes favorecem cautela em vez de velocidade. Em tarefas
triviais, use julgamento.

## 1. Pensar Antes de Codificar

**Não presuma. Não esconda confusão. Exponha trade-offs.**

Antes de implementar:
- Explicite suas premissas. Se houver incerteza, pergunte.
- Se existirem múltiplas interpretações, apresente-as — não escolha em silêncio.
- Se existir abordagem mais simples, diga. Empurre para trás quando couber.
- Se algo estiver obscuro, pare. Nomeie o que está confuso. Pergunte.

## 2. Simplicidade Primeiro

**Código mínimo que resolve o problema. Nada especulativo.**

- Nenhuma feature além do que foi pedido.
- Nenhuma abstração para código de uso único.
- Nenhuma "flexibilidade" ou configurabilidade que não foi solicitada.
- Nenhum tratamento de erro para cenários impossíveis.
- Se escreveu 200 linhas e caberia em 50, reescreva.

Pergunte-se: "Um engenheiro sênior diria que isso está complicado demais?" Se sim,
simplifique.

## 3. Mudanças Cirúrgicas

**Toque apenas no necessário. Limpe apenas a sua própria bagunça.**

Ao editar código existente:
- Não "melhore" código adjacente, comentários ou formatação.
- Não refatore o que não está quebrado.
- Siga o estilo existente, mesmo que faria diferente.
- Se notar código morto não relacionado, mencione — não delete.

Quando suas mudanças criarem órfãos:
- Remova imports/variáveis/funções que AS SUAS mudanças tornaram não utilizados.
- Não remova código morto pré-existente sem pedido.

O teste: cada linha alterada deve rastrear diretamente ao pedido do usuário.

## 4. Execução Orientada a Objetivo

**Defina critérios de sucesso. Itere até verificar.**

Transforme tarefas em objetivos verificáveis:
- "Adicionar validação" → "Escrever testes para entradas inválidas, depois fazê-los passar"
- "Corrigir o bug" → "Escrever um teste que o reproduz, depois fazê-lo passar"
- "Refatorar X" → "Garantir testes passando antes e depois"

Para tarefas de múltiplas etapas, declare um plano breve:

```
1. [Etapa] -> verificação: [check]
2. [Etapa] -> verificação: [check]
3. [Etapa] -> verificação: [check]
```

Critérios fortes de sucesso permitem iterar de forma autônoma. Critérios fracos
("fazer funcionar") exigem clarificação constante.

---

**Estas diretrizes estão funcionando se:** houver menos mudanças desnecessárias nos
diffs, menos reescritas por complicação excessiva, e perguntas de esclarecimento
vierem antes da implementação, não depois dos erros.
