# 1. Visão e escopo

## 1.1 O problema

Quem divide a vida financeira com outra pessoa costuma ter o dinheiro espalhado: cada um com seus bancos, seus cartões e seu jeito de anotar. As perguntas simples ficam sem resposta rápida. Quanto já gastamos este mês? Ainda cabe um jantar fora no orçamento? Estamos perto da meta da viagem?

Os aplicativos de finanças mais conhecidos foram desenhados para uma pessoa só. Para um casal ou uma família, sobram três saídas ruins: compartilhar a senha de uma conta, manter uma planilha que alguém esquece de atualizar, ou conversar sobre dinheiro só quando o problema já apareceu.

## 1.2 A proposta

O **CoupleSync** é um aplicativo Android de gestão financeira compartilhada. Duas ou mais pessoas formam um grupo e passam a enxergar, no mesmo lugar e ao mesmo tempo, os gastos, o orçamento do mês e as metas de todos.

O que o diferencia de uma planilha compartilhada é o esforço de registro, que é o ponto em que o controle financeiro costuma ser abandonado. O aplicativo oferece três formas de registrar uma despesa, da mais automática à mais manual:

1. **Captura de notificações bancárias.** Quando o banco avisa de uma compra, o aplicativo lê a notificação no próprio celular e registra a despesa.
2. **Importação de extrato.** O usuário envia o PDF do extrato ou da fatura e revisa os lançamentos encontrados antes de confirmar.
3. **Lançamento manual.** Para o que não passou pelo banco.

## 1.3 Público-alvo

- Casais que dividem despesas, morando juntos ou não.
- Famílias pequenas em que mais de uma pessoa tem renda ou gasta do orçamento comum.
- Pessoas que usam bancos brasileiros com aplicativo no Android (os bancos suportados na captura de notificações são Nubank, Itaú, Inter, C6 e Bradesco).

O projeto foi dimensionado para um piloto de até dez usuários. Alta disponibilidade e escala não são objetivos desta versão.

## 1.4 Objetivos

| # | Objetivo | Como se verifica |
|---|---|---|
| O1 | Todos os membros de um grupo veem os mesmos dados financeiros | O que um membro lança aparece para os demais na consulta seguinte |
| O2 | Nenhum dado de um grupo é visível a outro | Testes automatizados de isolamento em todos os módulos |
| O3 | Registrar uma despesa exige o mínimo de digitação | Três formas de entrada; duas delas sem digitar valor |
| O4 | O grupo sabe, a qualquer momento, quanto gastou no mês, por membro e por categoria | Painel do mês; a API também calcula o gasto contra o orçamento planejado por categoria |
| O5 | O grupo acompanha metas de economia | Tela de metas com progresso |
| O6 | O sistema roda sem custo mensal durante o piloto | Hospedagem e banco em planos gratuitos |

## 1.5 Escopo desta versão

**Incluído**

- Cadastro, login e sessão com renovação automática.
- Criação de grupo e entrada por código de convite, com dois ou mais membros.
- Despesas: lançamento manual, captura de notificações bancárias, importação de extrato em PDF; listagem com filtros; recategorização; exclusão.
- Classificação automática de despesas por regras de palavra-chave.
- Orçamento mensal por categoria, com gasto real contra o planejado (disponível na API; ainda sem tela no aplicativo).
- Fontes de renda por membro, individuais ou compartilhadas.
- Metas de economia; o vínculo de despesas a uma meta está disponível na API, ainda sem tela no aplicativo.
- Painel do mês, gastos por categoria, tendência mensal e fluxo de caixa.
- Alertas: orçamento a 80% e estourado, transação grande, gasto elevado em 30 dias.
- Assistente de perguntas sobre as finanças do grupo (opcional; depende de chave de serviço de IA).

**Fora do escopo**

- iOS e versão web.
- Integração direta com bancos (Open Finance).
- Receitas e saldo de contas: o sistema registra despesas e rendas declaradas, não saldo bancário.
- Leitura de extrato por foto (somente PDF com texto).
- Divisão de uma despesa entre categorias ou entre pessoas.
- Publicação na Google Play.

**Planejado para versões seguintes**

- Um usuário participar de mais de um grupo.
- Sair de um grupo, remover um membro e trocar o código de convite.
- Recuperação de senha e confirmação de e-mail.
- Tela de consentimento e política de privacidade para a captura de notificações.

## 1.6 Premissas e restrições

| Tipo | Descrição |
|---|---|
| Premissa | Os usuários têm Android e recebem notificações de compra do banco |
| Premissa | O piloto usa dados de teste ou de voluntários cientes do que o aplicativo coleta |
| Restrição | Custo mensal zero: todos os serviços em planos gratuitos |
| Restrição | Um único desenvolvedor |
| Restrição | A captura de notificações só enxerga compras feitas depois da instalação e da permissão; não há histórico retroativo |
| Restrição | O plano gratuito de hospedagem suspende a API após 15 minutos sem uso; a primeira requisição seguinte leva cerca de um minuto |

## 1.7 Evolução em relação ao planejamento

O projeto começou com um documento de requisitos de produto ([`.github/PRD.md`](../../.github/PRD.md)) escrito antes do desenvolvimento. A tabela registra o que mudou entre aquele planejamento e a versão entregue, e por quê.

| Planejado | Entregue | Motivo |
|---|---|---|
| Entidade `Account` (conta bancária com saldo) | Não existe; as despesas pertencem ao grupo | Sem integração bancária não há saldo confiável para guardar |
| Captura de notificações como única entrada automática | Captura de notificações e importação de extrato em PDF | A captura não traz histórico; o extrato cobre o período anterior à instalação |
| Gráfico de patrimônio líquido | Gastos por categoria e tendência mensal | Patrimônio depende de saldo, que saiu do escopo |
| Divisão de transações | Não implementado | Prioridade menor que orçamento e metas |
| Membros adicionais com níveis de acesso | Grupo com vários membros, todos com o mesmo acesso | Níveis de acesso exigem um modelo de permissões que o piloto não justifica |
| Hospedagem em PaaS simples (Railway, Render ou similar) | Azure App Service durante o desenvolvimento; Render na entrega | O serviço no Azure foi desativado para evitar cobrança; ver [ADR-0005](../adr/0005-stack-de-nuvem.md) |
| Categorização por IA como item futuro | Classificação por regras, com IA opcional | Regras resolvem os casos comuns sem custo e sem enviar dados a terceiros |
