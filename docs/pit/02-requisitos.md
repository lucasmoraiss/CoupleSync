# 2. Requisitos

Os requisitos estão divididos em funcionais (o que o sistema faz), regras de negócio (as condições que ele impõe) e não funcionais (as qualidades que ele deve ter). Cada requisito funcional aponta a rota da API e a tela do aplicativo que o atendem, para que possa ser rastreado até o código e, no [plano de testes](07-plano-de-testes.md), até os testes.

Situação: **Atendido** (implementado e coberto por teste), **Parcial** (implementado com limitação descrita) ou **Planejado** (fora desta versão).

## 2.1 Requisitos funcionais

### Conta e grupo

| ID | Requisito | API | Tela | Situação |
|---|---|---|---|---|
| RF01 | O usuário cria uma conta com nome, e-mail e senha | `POST /api/v1/auth/register` | Criar conta | Atendido |
| RF02 | O usuário entra com e-mail e senha | `POST /api/v1/auth/login` | Entrar | Atendido |
| RF03 | A sessão é renovada sem pedir a senha de novo enquanto o usuário usa o aplicativo | `POST /api/v1/auth/refresh` | (automático) | Atendido |
| RF04 | O usuário cria um grupo e recebe um código de convite | `POST /api/v1/couples` | Grupo | Atendido |
| RF05 | O usuário entra em um grupo existente informando o código | `POST /api/v1/couples/join` | Grupo | Atendido |
| RF06 | O usuário consulta o grupo, o código e os membros | `GET /api/v1/couples/me` | Grupo | Parcial: o aplicativo só mostra o código no momento da criação |
| RF07 | O usuário sai de um grupo, remove um membro ou troca o código | — | — | Planejado |
| RF08 | O usuário participa de mais de um grupo | — | — | Planejado |
| RF09 | O usuário recupera a senha esquecida | — | — | Planejado |

### Despesas

| ID | Requisito | API | Tela | Situação |
|---|---|---|---|---|
| RF10 | O usuário lança uma despesa à mão, com valor e categoria | `POST /api/v1/transactions` | Nova transação | Atendido |
| RF11 | O sistema registra uma despesa a partir de uma notificação bancária capturada no celular | `POST /api/v1/integrations/events` | (automático) | Atendido para cinco bancos; os padrões de texto não foram validados com notificações reais de todos eles |
| RF12 | O usuário envia um extrato em PDF e o sistema encontra os lançamentos | `POST /api/v1/ocr/upload`, `GET .../status`, `GET .../results` | Importar extrato | Parcial: validado com o formato do Inter; os leitores de outros bancos falham com várias linhas por página |
| RF13 | O usuário revisa os lançamentos do extrato, corrige descrição, valor e categoria, e escolhe quais importar | `POST /api/v1/ocr/{id}/confirm` | Revisar lançamentos | Atendido |
| RF14 | O usuário lista as despesas do grupo, com filtro por categoria e período e paginação | `GET /api/v1/transactions` | Transações | Parcial: o aplicativo mostra só os 20 lançamentos mais recentes |
| RF15 | O usuário troca a categoria de uma despesa | `PATCH /api/v1/transactions/{id}/category` | Transações | Atendido |
| RF16 | O usuário exclui uma despesa | `DELETE /api/v1/transactions/{id}` | Transações | Atendido |
| RF17 | O sistema sugere a categoria de uma despesa pelo nome do estabelecimento | (interno, na criação) | — | Atendido para despesas vindas de notificação |
| RF18 | O usuário corrige o valor, a descrição ou a data de uma despesa já gravada | — | — | Planejado |
| RF19 | O usuário consulta a situação da captura de notificações | `GET /api/v1/integrations/status` | — | Parcial: a contagem inclui lançamentos manuais; o aplicativo não consulta essa rota (mostra só se a permissão do Android está concedida) |

### Orçamento e renda

| ID | Requisito | API | Tela | Situação |
|---|---|---|---|---|
| RF20 | O grupo define a renda prevista do mês | `POST /api/v1/budgets`, `PATCH /api/v1/budgets/income` | — | Atendido na API; no aplicativo a renda é informada pelas fontes de renda (RF23) |
| RF21 | O grupo define quanto pretende gastar por categoria | `PUT /api/v1/budgets/{planId}/allocations` | — | Atendido na API; sem tela no aplicativo |
| RF22 | O grupo vê, por categoria, o gasto real contra o planejado | `GET /api/v1/budgets/current`, `GET /api/v1/budgets/{mês}` | — | Atendido na API; sem tela no aplicativo |
| RF23 | Cada membro registra suas fontes de renda, individuais ou compartilhadas | `POST`, `PUT`, `DELETE /api/v1/incomes` | Rendas | Atendido |
| RF24 | O grupo vê as rendas de todos os membros e o total | `GET /api/v1/incomes/current`, `GET /api/v1/incomes/{mês}` | Rendas | Atendido |

### Metas

| ID | Requisito | API | Tela | Situação |
|---|---|---|---|---|
| RF30 | O grupo cria uma meta com título, valor-alvo e prazo | `POST /api/v1/goals` | Metas | Atendido |
| RF31 | O grupo edita uma meta e atualiza o valor já guardado | `PATCH /api/v1/goals/{id}` | Metas | Atendido |
| RF32 | O grupo arquiva ou exclui uma meta | `DELETE /api/v1/goals/{id}/archive`, `DELETE /api/v1/goals/{id}` | Metas | Atendido |
| RF33 | O grupo acompanha o progresso de cada meta | `GET /api/v1/goals`, `GET /api/v1/goals/{id}/progress`, `GET /api/v1/goals/progress-summary` | Metas, Relatórios | Parcial: o valor informado à mão e a soma das despesas vinculadas são calculados em consultas diferentes |
| RF34 | O usuário vincula uma despesa a uma meta | `PATCH /api/v1/transactions/{id}/goal` | — | Atendido na API; sem tela no aplicativo |

### Acompanhamento

| ID | Requisito | API | Tela | Situação |
|---|---|---|---|---|
| RF40 | O grupo vê o total gasto no período, por membro e por categoria | `GET /api/v1/dashboard` | Painel | Atendido |
| RF41 | O grupo vê a distribuição dos gastos por categoria nos últimos meses | `GET /api/v1/reports/spending-by-category` | Relatórios | Atendido |
| RF42 | O grupo vê a evolução mensal dos gastos | `GET /api/v1/reports/monthly-trends` | Relatórios | Parcial: a renda não entra no cálculo do saldo mensal |
| RF43 | O grupo vê o gasto médio diário e o total em 30 ou 90 dias | `GET /api/v1/cashflow` | Fluxo | Parcial: o valor projetado repete o total do período |

### Alertas

| ID | Requisito | API | Tela | Situação |
|---|---|---|---|---|
| RF50 | O sistema gera alerta quando o gasto de uma categoria chega a 80% do planejado e quando ultrapassa | (interno, após cada despesa) | Notificação push | Atendido |
| RF51 | O sistema gera alerta para uma despesa acima de R$ 500 | (interno) | Notificação push | Atendido |
| RF52 | O sistema gera alerta quando o gasto em 30 dias passa de R$ 3.000 | (interno) | Notificação push | Parcial: o alerta se repete a cada despesa seguinte |
| RF53 | O usuário liga e desliga cada tipo de alerta | `GET`, `PUT /api/v1/notifications/settings` | Alertas | Atendido |
| RF54 | O aparelho é registrado para receber notificações push | `POST /api/v1/devices/token` | (automático) | Atendido; a entrega depende de credenciais do Firebase no servidor |

### Assistente

| ID | Requisito | API | Tela | Situação |
|---|---|---|---|---|
| RF60 | O usuário faz perguntas sobre as finanças do grupo e recebe resposta em linguagem natural | `POST /api/v1/ai/chat` | Assistente | Atendido quando o recurso está habilitado; desligado por padrão |

## 2.2 Regras de negócio

| ID | Regra | Onde é imposta |
|---|---|---|
| RN01 | Os dados de um grupo só são visíveis e alteráveis por seus membros | Token, filtro `RequireCouple`, repositórios e filtro global do EF Core |
| RN02 | Um usuário sem grupo não acessa nenhum dado financeiro | Filtro `RequireCouple` (resposta 403) |
| RN03 | Um usuário pertence a no máximo um grupo | `JoinCoupleCommandHandler`, `CreateCoupleCommandHandler` |
| RN04 | Um grupo pode ter mais de dois membros | `Couple.AddMember` |
| RN05 | O e-mail identifica o usuário e não se repete | Índice único em `users.email` |
| RN06 | A senha tem no mínimo 8 caracteres e é guardada apenas como hash | Validador de cadastro; bcrypt |
| RN07 | A mesma despesa não é registrada duas vezes no grupo | Impressão digital única por grupo |
| RN08 | Uma notificação só vira despesa se for de compra, pagamento ou transferência enviada; recebimentos e avisos são ignorados | Leitor de notificações do aplicativo |
| RN09 | O valor de uma despesa é maior que zero e não passa de R$ 999.999.999.999,99 | Validadores; `MoneyRules` |
| RN10 | Linhas idênticas dentro de um mesmo extrato são lançamentos distintos; lançamentos que já existem no grupo são ignorados na confirmação | `ImportJobService` |
| RN11 | Uma importação de extrato só é confirmada uma vez | `ImportJob.MarkConfirmed` |
| RN12 | Cada grupo tem um único plano de orçamento por mês, com até 20 categorias sem repetição | Índice único `(couple_id, month)`; `BudgetService` |
| RN13 | Uma renda individual só é editada por seu dono; uma renda compartilhada, por qualquer membro | `IncomeSource.CanBeEditedBy` |
| RN14 | Uma meta tem valor-alvo maior que zero e prazo igual ou posterior a hoje | Validadores e entidade `Goal` |
| RN15 | Uma meta arquivada não pode ser editada | `Goal.Update` (resposta 409) |
| RN16 | Excluir uma meta não exclui as despesas vinculadas | Chave estrangeira com `SET NULL` |
| RN17 | Alertas são gerados conforme as preferências do usuário; sem preferência gravada, todos estão ligados | Casos de uso de criação de despesa |
| RN18 | O arquivo de extrato tem no máximo 10 MB e precisa ser um PDF com texto | `OcrController`; leitor de PDF |

## 2.3 Requisitos não funcionais

| ID | Categoria | Requisito | Como é verificado | Situação |
|---|---|---|---|---|
| RNF01 | Segurança | Toda comunicação entre aplicativo e API usa HTTPS | Configuração da hospedagem e do build do aplicativo | Atendido na hospedagem; a API não força o redirecionamento por conta própria |
| RNF02 | Segurança | Nenhum segredo no repositório; a API não inicia sem o segredo de assinatura | Testes de inicialização; varredura na integração contínua | Atendido |
| RNF03 | Segurança | Acesso a recurso de outro grupo é recusado em todos os módulos | Testes de integração de isolamento | Atendido |
| RNF04 | Segurança | No máximo 5 tentativas por minuto de login, de cadastro (por endereço) e de entrada em grupo (por usuário) | Testes de integração do limite | Atendido |
| RNF05 | Segurança | O token de acesso vale 15 minutos; o de renovação vale 7 dias, é trocado a cada uso e guardado só como hash | Testes de autenticação | Atendido |
| RNF06 | Confiabilidade | Entrada inválida produz erro de cliente (4xx) com mensagem, nunca erro interno | Testes de validação; sessões de teste exploratório | Atendido para os casos encontrados |
| RNF07 | Confiabilidade | Falha no envio de um alerta não desfaz a despesa que o originou | Código dos casos de uso | Atendido |
| RNF08 | Desempenho | Consultas do painel respondem em menos de 1 segundo com o volume do piloto | Índices compostos por grupo nas tabelas consultadas | Não medido formalmente; nenhuma lentidão observada nas sessões de teste em ambiente local |
| RNF09 | Disponibilidade | O sistema fica disponível sem custo mensal | Hospedagem em plano gratuito | Atendido, com a limitação de suspender após 15 minutos sem uso |
| RNF10 | Usabilidade | Uma despesa manual é lançada com um único campo obrigatório | Tela de nova transação | Atendido |
| RNF11 | Usabilidade | Interface e mensagens ao usuário em português do Brasil | Revisão das telas | Parcial: mensagens de erro vindas do servidor estão em inglês |
| RNF12 | Acessibilidade | Texto com contraste mínimo de 4,5:1 e elementos com rótulo para leitor de tela | Revisão do tema e das telas | Parcial (ver [IHC e UX](06-ihc-ux.md)) |
| RNF13 | Manutenibilidade | Toda regra de negócio tem teste automatizado executado a cada alteração | Integração contínua | Atendido: 592 testes na API e 111 no aplicativo |
| RNF14 | Manutenibilidade | Toda mudança no banco é uma migration versionada | Diretório de migrations; verificação de modelo pendente | Atendido |
| RNF15 | Portabilidade | A API roda em qualquer ambiente com Docker | `backend/Dockerfile`, `docker-compose.yml` | Atendido |
| RNF16 | Privacidade | O aplicativo envia ao servidor só os dados necessários para registrar a despesa | Teste do corpo enviado | Atendido para a captura de notificações |
| RNF17 | Privacidade | O usuário é informado do que é coletado antes de autorizar, e pode exportar e apagar seus dados | — | Planejado |

## 2.4 Validação dos requisitos

O material da disciplina define que um requisito válido descreve corretamente a necessidade, tem um só significado, pode ser modificado e pode ser ligado ao código e aos testes. Foi assim que estes requisitos foram validados:

- **Necessidade.** Cada requisito funcional responde a uma das perguntas do usuário descritas em [Visão e escopo](01-visao-e-escopo.md).
- **Significado único.** Cada requisito aponta uma rota e uma tela; quando o comportamento tem limite numérico, o número está escrito (80%, R$ 500, 10 MB, 5 tentativas por minuto).
- **Rastreabilidade.** A matriz do [plano de testes](07-plano-de-testes.md) liga cada requisito aos testes automatizados que o verificam.
- **Confronto com o sistema em execução.** As cinco sessões de teste descritas no [laudo de qualidade](08-laudo-de-qualidade.md) exercitaram os requisitos contra a API real. Foi esse confronto que revelou os requisitos marcados como "Parcial" e corrigiu a descrição de vários deles em relação ao planejamento original.
