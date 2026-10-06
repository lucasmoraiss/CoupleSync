# 8. Laudo de qualidade

## 8.1 Identificação

| | |
|---|---|
| **Sistema** | CoupleSync — API (.NET 8) e aplicativo Android (React Native) |
| **Versão avaliada** | commit `98e3f64` (início do ciclo) |
| **Versão após as correções** | API: commit `c3c58a2`, publicada em <https://couplesync-api.onrender.com>; aplicativo: commit `bfd8dce` |
| **Período** | 5 de outubro de 2026 |
| **Responsável** | Lucas Henrique Morais Salomão |
| **Ambiente** | API e PostgreSQL 16 em contêineres Docker locais; testes automatizados com .NET SDK e Node.js na máquina de desenvolvimento; verificação final na API publicada (Render e Neon) |

## 8.2 Método

A avaliação combinou verificação e validação, nesta ordem.

**1. Revisão de código (verificação).** Leitura completa do código do produto em cinco frentes: segurança da API; arquitetura e correção dos dados; qualidade dos testes; aplicativo; conteúdo do repositório e infraestrutura. Cada achado foi confirmado no ponto do código citado antes de ser registrado.

**2. Linha de base (verificação).** Compilação e execução de toda a suíte de testes automatizados, com medição de cobertura, antes de qualquer alteração.

**3. Sessões de teste exploratório (validação).** Cinco sessões de caixa-preta contra a API em execução, cada uma com um escopo, somando 642 casos. Cada sessão é um roteiro em shell com `curl`, reexecutável do zero, que grava o comando, o código de resposta e o corpo de cada caso. Além do caminho feliz, toda sessão testou entradas inválidas, limites e a tentativa de um grupo acessar os dados de outro.

| Sessão | Escopo | Casos |
|---|---|---|
| 1 | Cadastro, login, renovação de sessão e formação de grupo | 42 |
| 2 | Despesas, entrada de notificações bancárias e painel | 156 |
| 3 | Orçamento, fontes de renda e alertas | 105 |
| 4 | Metas, fluxo de caixa, relatórios e painel | 260 |
| 5 | Importação de extrato em PDF e assistente | 79 |

**4. Correção.** Cada erro selecionado seguiu o mesmo procedimento: escrever o teste automatizado que reproduz o erro, executá-lo e guardar a falha; corrigir; executar de novo e guardar o sucesso; executar a suíte inteira; registrar em um commit próprio.

**5. Reexecução (validação).** As cinco sessões foram executadas novamente, com os mesmos roteiros, contra a API corrigida.

**6. Verificação em produção.** Depois de publicada, a API foi conferida no ambiente real: saúde, conexão com o banco, autenticação e limite de tentativas. Essa etapa encontrou um erro que nenhum teste local revelava (A17).

**7. Execução do aplicativo.** O APK foi instalado em um emulador Android 14 e conectado à API publicada, com contas de demonstração e dados fictícios. Todas as telas foram percorridas por roteiro de toques, incluindo lançamento manual, troca de categoria, exclusão, importação de extrato com correção de valores e edição de meta. Essa etapa encontrou defeitos visuais que os testes de lógica não mostravam (A18).

A revisão de código e a execução dos roteiros foram feitas com apoio de ferramentas de IA para programação, sob condução do autor. Os arquivos de teste da importação de extrato são PDFs sintéticos, com dados inventados.

**Limite do método.** O aplicativo foi executado em emulador, não em um aparelho físico. A captura de notificações bancárias (A06, A09 e A16) não pôde ser exercitada ali, porque depende de notificações vindas do aplicativo de um banco: essas três correções foram verificadas por testes unitários e leitura do código. O roteiro para conferi-las em um aparelho está no anexo.

## 8.3 Resumo em números

| Indicador | Antes | Depois |
|---|---|---|
| Testes automatizados da API | 458 (456 com conteúdo) | 592 |
| Testes automatizados do aplicativo | 0 | 111 |
| Testes reprovados | 0 | 0 |
| Cobertura de linhas da API, sem migrations | 77,9% | 84,0% |
| Cobertura de ramos da API, sem migrations | 63,5% | 68,6% |
| Respostas de erro interno (500) nas cinco sessões | 34 | 0 |
| Segredos no repositório | 1 | 0 |

**Achados por severidade**

| Severidade | Corrigidos neste ciclo | Pendentes |
|---|---|---|
| Crítica | 1 | 0 |
| Alta | 10 | 7 |
| Média | 7 | 13 |
| Baixa | 0 | 1 |
| **Total** | **18** | **21** |

Critério de severidade: **crítica**, falha de segurança explorável ou perda de dados; **alta**, função principal quebrada ou resultado errado apresentado como certo; **média**, função secundária, mensagem enganosa ou proteção ausente; **baixa**, cosmético ou de baixo impacto.

O que a avaliação encontrou de sólido: isolamento entre grupos correto em todos os módulos (nenhum acesso cruzado em 642 casos), valores monetários em tipo decimal, senhas em bcrypt, tokens de sessão guardados em armazenamento cifrado no aparelho, envio de arquivo com verificação de tipo e de tamanho, e uma suíte de testes sem nenhum teste desativado.

## 8.4 Erros corrigidos

| ID | Erro | Sev. | Como foi encontrado | Correção | Commit |
|---|---|---|---|---|---|
| A01 | Uma chave de assinatura de tokens estava no arquivo de configuração versionado, e a API a aceitava como válida. Quem lesse o repositório poderia fabricar tokens de qualquer usuário em um ambiente que usasse essa chave | Crítica | Revisão de código | O arquivo passou a ter o segredo vazio; a API recusa iniciar sem um segredo de pelo menos 32 caracteres vindo de variável de ambiente | `661de99` |
| A02 | Importação de extrato: confirmar um lote com duas linhas idênticas (duas corridas de R$ 12,90 no mesmo dia), ou reimportar um extrato já importado, devolvia erro interno e nada era gravado | Alta | Revisão de código; sessão 5 | Linhas repetidas no mesmo extrato são gravadas como lançamentos distintos; lançamentos que já existem são ignorados e contados na resposta | `57c1b94` |
| A03 | O aplicativo não renovava a sessão: 15 minutos depois de entrar, o usuário era devolvido à tela de entrada, sem aviso | Alta | Revisão de código | Ao receber "não autorizado", o aplicativo renova a sessão uma única vez e repete a requisição; só encerra a sessão, com aviso, se a renovação for recusada | `36fce2f` |
| A04 | Na revisão do extrato o usuário corrigia valor e descrição, via "importado com sucesso", e o valor original era gravado | Alta | Revisão de código; sessão 5 | O aplicativo envia as correções e a API as aplica; a tela valida os campos e reinicia a cada nova importação | `40a5b90` `dc31f5c` `9787a55` |
| A05 | Não havia limite de tentativas em login, cadastro e entrada em grupo: dez senhas erradas e vinte códigos de convite passaram sem bloqueio | Média | Revisão de código; sessão 1 | Limite de 5 por minuto, por endereço (login e cadastro) e por usuário (entrada em grupo), com resposta 429 | `820ce69` |
| A06 | Qualquer notificação bancária com um valor em reais virava despesa: "Pix recebido", "sua fatura vence amanhã", "limite disponível" | Alta | Revisão de código | Só notificações de compra, pagamento e transferência enviada são registradas; recebimentos e avisos são descartados no aparelho | `d968faf` |
| A07 | Entradas inválidas devolviam erro interno em vez de erro de validação: data com fuso de Brasília, texto acima do limite, valor com mais de 16 dígitos, evento sem moeda, nome de renda vazio, valor negativo em meta, entre outras | Alta | Revisão de código; sessões 2, 3, 4 e 5 | Validação na borda para os contratos que não tinham; datas convertidas para UTC; teto único para valores; erros de regra de domínio convertidos em 400 | `121924a` |
| A08 | Um PDF de banco não suportado era reprocessado quatro vezes e terminava, 22 segundos depois, com um erro genérico | Média | Revisão de código; sessão 5 | Falha na primeira tentativa, com o código `BANK_FORMAT_UNKNOWN` | `c7ed42b` |
| A09 | O texto integral da notificação bancária, que pode conter o nome de terceiros, era enviado ao servidor e armazenado; a documentação afirmava o contrário | Alta | Revisão de código | O aplicativo envia só banco, valor, moeda, data e estabelecimento | `1aaa8e6` |
| A10 | A tela de importação oferecia câmera e imagens, que o servidor rejeita | Média | Revisão de código; sessão 5 | A tela oferece apenas arquivo PDF | `c2cf02f` |
| A11 | Atualizar só o valor guardado de uma meta era recusado com "nenhum campo informado" | Alta | Sessão 4 | O valor guardado conta como campo; o prazo só é validado quando enviado | `1f0221a` |
| A12 | Quem nunca tinha aberto a tela de alertas não recebia nenhum alerta, embora a tela mostrasse todos ligados | Alta | Sessão 3 | A ausência de preferência gravada passa a significar o padrão, tudo ligado | `e481d4a` |
| A13 | Em grupo com três membros, o total de rendas mudava conforme quem consultava e omitia um dos membros | Alta | Revisão de código; sessão 3 | O total soma as rendas de todos os membros, e todos veem o mesmo valor | `ace3e23` |
| A14 | Confirmar uma importação com um índice inexistente a dava por concluída com zero despesas, e os lançamentos ficavam inacessíveis | Média | Revisão de código; sessão 5 | Índice inexistente devolve 422 e a importação continua disponível | `57c1b94` |
| A15 | Um arquivo de teste continha trechos de uma fatura real do autor | Média | Revisão de código | Substituídos por dados inventados | `a8f42da` |
| A16 | Toda notificação do Itaú e do C6 era recusada pelo servidor: o aplicativo enviava "Itaú" e "C6 Bank", e a API só aceitava "ITAU" e "C6" | Alta | Encontrado durante a correção do A09 | O aplicativo envia o nome que a API aceita | `27873ee` |
| A17 | Em produção, o limite de tentativas do A05 não disparava: atrás do Cloudflare, a API contava as tentativas pelo endereço do proxy, que muda a cada requisição | Média | Verificação em produção | A API identifica o cliente pelo cabeçalho que a CDN preenche com o endereço real, aceito só quando a origem é um proxy confiável | `c3c58a2` |
| A18 | Defeitos visuais no aplicativo em execução: o painel mostrava "setembro de 2026" durante outubro (o início do período, em UTC, era convertido para o fuso do aparelho); relatórios e fluxo de caixa mostravam a chave interna da categoria ("ALIMENTACAO"); o centro do gráfico ficava branco; rótulos das abas eram cortados; o outro membro aparecia como "Membro" | Média | Execução do aplicativo em emulador | Mês lido em UTC; nome da categoria e do membro em todas as telas; gráfico e abas ajustados | `a2b8555` `bfd8dce` |

Fora da tabela, por não serem erros de funcionamento: remoção de um framework de terceiros do repositório e reunião dos registros de decisão (`98e3f64`); remoção do fluxo de implantação que apontava para um serviço desativado (`ea9df05`); remoção de arquivos de modelo sem uso (`ba83f0c`); correção da documentação que descrevia funções inexistentes.

## 8.5 Evidências

Para cada correção há dois arquivos: a execução do teste antes (falhando) e depois (passando). Eles estão em [`evidencias/correcoes`](evidencias/README.md), e os roteiros e relatórios das sessões em `evidencias/sessoes`. Abaixo, o resumo de cada par e, para os erros que apareceram nas sessões, a resposta da API antes e depois.

### Testes automatizados: antes e depois de cada correção

| ID | Antes da correção | Depois da correção |
|---|---|---|
| A01 | 2 reprovados, 10 aprovados | 12 aprovados |
| A02 + A14 | 14 reprovados, 2 aprovados | 16 aprovados |
| A03 | 9 reprovados, 3 aprovados | 12 aprovados |
| A04 (API) | 15 reprovados, 2 aprovados | 17 aprovados |
| A04 (aplicativo) | 8 reprovados, 5 aprovados | 13 aprovados |
| A05 | 6 reprovados, 2 aprovados | 8 aprovados |
| A06 | 58 reprovados, 10 aprovados | 68 aprovados |
| A07 | 46 reprovados, 9 aprovados | 55 aprovados |
| A08 | 2 reprovados, 1 aprovado | 3 aprovados |
| A09 | 4 reprovados | 4 aprovados |
| A11 | 6 reprovados, 4 aprovados | 10 aprovados |
| A12 | 3 reprovados, 2 aprovados | 5 aprovados |
| A13 | 4 reprovados, 2 aprovados | 6 aprovados |
| A16 | 2 reprovados, 3 aprovados | 5 aprovados |
| A17 | 3 reprovados, 1 aprovado | 4 aprovados |
| A18 | 5 reprovados, 4 aprovados | 9 aprovados |

Os testes que já passavam antes são os casos de controle de cada conjunto (por exemplo, no A06, as compras que já eram reconhecidas). A10 e A15 não têm teste próprio: o primeiro é mudança só de tela e o segundo é troca de dados de teste.

### A01 — chave no repositório

Antes:

```text
$ grep -n "Secret" backend/src/CoupleSync.Api/appsettings.json
7:        "Secret": "397dac…",          (valor truncado)
```

Depois:

```text
7:        "Secret": "",
```

Com o segredo vazio, a API encerra na inicialização se a variável `JWT__SECRET` não estiver definida. A chave antiga permanece no histórico do repositório e por isso é tratada como comprometida: nenhum ambiente deve usá-la.

### A02 — extrato com duas linhas idênticas (sessão 5)

Antes:

```text
POST /api/v1/ocr/{id}/confirm    selectedIndices: todos
HTTP 500  {"code":"INTERNAL_SERVER_ERROR","message":"An unexpected error occurred."}
log da API: 23505: duplicate key value violates unique constraint
            "IX_transactions_couple_id_fingerprint"
despesas gravadas desse extrato: 0
```

Depois:

```text
POST /api/v1/ocr/{id}/confirm    selectedIndices: todos
HTTP 200  {"transactionsCreated":7,"duplicatesSkipped":0}
despesas gravadas desse extrato: 7      (as duas corridas de R$ 12,90 incluídas)
nova tentativa de confirmar: HTTP 409 OCR_JOB_ALREADY_CONFIRMED
```

### A05 — limite de tentativas

Antes (sessão 1): dez logins seguidos com senha errada, todos respondidos com 401, sem bloqueio.

Depois:

```text
tentativa 1 -> HTTP 401  {"code":"INVALID_CREDENTIALS", ...}
...
tentativa 5 -> HTTP 401  {"code":"INVALID_CREDENTIALS", ...}
tentativa 6 -> HTTP 429  Retry-After: 60  {"code":"RATE_LIMIT_EXCEEDED", ...}
tentativa 7 -> HTTP 429  Retry-After: 60  {"code":"RATE_LIMIT_EXCEEDED", ...}
```

### A17 — limite de tentativas na API publicada

O A05 funcionava no ambiente local e não em produção. Sete logins seguidos com senha errada em `https://couplesync-api.onrender.com`:

```text
Antes (commit aad8f84):   401 401 401 401 401 401 401

Depois (commit c3c58a2):
tentativa 1 -> HTTP 401 {"code":"INVALID_CREDENTIALS", ...}
...
tentativa 5 -> HTTP 401 {"code":"INVALID_CREDENTIALS", ...}
tentativa 6 -> HTTP 429 retry-after: 60 {"code":"RATE_LIMIT_EXCEEDED", ...}
tentativa 7 -> HTTP 429 retry-after: 60 {"code":"RATE_LIMIT_EXCEEDED", ...}
```

O teste automatizado que reproduz o cenário mantém o mesmo cliente e muda o endereço do proxy a cada requisição.

### A18 — mês do painel no aplicativo em execução

```text
Data do aparelho: 5 de outubro de 2026, fuso de Brasília.  Início do período devolvido pela API: 2026-10-01T00:00:00Z

Antes:   rótulo do painel "setembro de 2026"
Depois:  rótulo do painel "outubro de 2026"
```

As telas depois da correção estão nas [capturas do documento de interface](06-ihc-ux.md#capturas-de-tela).

### A07 — entrada inválida (sessões 2, 3 e 5)

| Caso | Requisição | Antes | Depois |
|---|---|---|---|
| 2.73 | Despesa com data `2026-10-03T10:00:00-03:00` | 500 | 201 |
| 2.79 | Despesa com descrição de 513 caracteres | 500 | 400 |
| 2.85 | Despesa com categoria de 65 caracteres | 500 | 400 |
| 2.62 | Despesa de valor 99999999999999999999 | 500 | 400 |
| 2.133 | Evento de notificação com data sem fuso | 500 | 201 |
| 2.144 | Evento de notificação sem moeda | 500 | 400 |
| 2.147 | Evento de notificação vazio | 500 | 400 |
| 2.55 | Listagem na página 2147483647 | 500 | 400 |
| 3.72 | Renda com nome vazio | 500 | 400 |
| 3.14 | Renda mensal de 1e20 | 500 | 400 |
| 5.29 | Confirmação com índice de categoria repetido | 500 | 400 |
| 5.30 | Confirmação com categoria de 65 caracteres | 500 | 400 |

Total de respostas 500 nas cinco sessões: 34 antes, 0 depois (sessão 2: 12 → 0; sessão 3: 6 → 0; sessão 4: 10 → 0; sessão 5: 6 → 0).

### A08 — banco não suportado (sessão 5)

```text
Antes:   status Failed após 22,2 s    errorCode: "processing_error"
Depois:  status Failed na 1ª tentativa errorCode: "BANK_FORMAT_UNKNOWN"
```

### A11 — meta: atualizar só o valor guardado (sessão 4)

```text
PATCH /api/v1/goals/{id}    {"currentAmount":1500}

Antes:   400  "At least one field must be provided for update."
Depois:  200  {"title":"Viagem Japao 2027","targetAmount":12000.00,"currentAmount":1500, ...}
```

### A12 — alertas sem preferências gravadas (sessão 3)

Cenário: orçamento de R$ 100 em Alimentação; despesas somando 50, 85 e 120, lançadas por um usuário que nunca abriu a tela de alertas.

```text
Antes:   0 alertas registrados
Depois:  BudgetWarning   "Atenção: você usou 80% do orçamento de Alimentação este mês."
         BudgetExceeded  "Spent 120.00 BRL of 100.00 BRL Alimentação budget."
```

### A13 — rendas em grupo de três (sessão 3)

Cenário: A com R$ 5.000, B com R$ 3.000, D com R$ 2.000 e uma renda compartilhada de R$ 1.000. Total real: R$ 11.000.

| Quem consulta | Total antes | Total depois |
|---|---|---|
| A | 9.000,00 | 11.000,00 |
| B | 9.000,00 | 11.000,00 |
| D | 8.000,00 | 11.000,00 |

### A14 — índice inexistente (sessão 5)

```text
POST /api/v1/ocr/{id}/confirm    {"selectedIndices":[99]}

Antes:   200 {"transactionsCreated":0}; importação marcada como concluída
Depois:  422 INVALID_SELECTION; importação continua disponível
```

### Suíte completa ao final

```text
Passed!  - Failed: 0, Passed: 411, Skipped: 0, Total: 411 - CoupleSync.UnitTests.dll
Passed!  - Failed: 0, Passed:   1, Skipped: 0, Total:   1 - CoupleSync.E2ETests.dll
Passed!  - Failed: 0, Passed: 180, Skipped: 0, Total: 180 - CoupleSync.IntegrationTests.dll

Aplicativo:  Test Suites: 6 passed, 6 total    Tests: 111 passed, 111 total
             tsc --noEmit: sem erros
```

## 8.6 Pendências conhecidas

Erros e limitações encontrados e não corrigidos neste ciclo, por prioridade ou por exigirem decisão de produto.

### Severidade alta

| ID | Pendência | Origem |
|---|---|---|
| C01 | Não existe sair do grupo, remover um membro nem trocar o código de convite. O código tem seis caracteres e não expira: quem o viu uma vez pode entrar e ver as finanças do grupo, e não há como desfazer | Revisão; sessão 1 |
| C03 | Os leitores de extrato de Nubank, Banco do Brasil e Itaú devolvem um único lançamento errado quando há várias linhas na mesma página. Observado com PDFs sintéticos; só o formato do Inter foi validado | Sessão 5 |
| C04 | Fatura do Inter: uma compra com "Parcela 03/10" some da leitura, e "02/06" é interpretado como data | Sessão 5 |
| C05 | O progresso de uma meta diverge entre consultas: uma usa o valor informado à mão, outra a soma das despesas vinculadas | Revisão; sessão 4 |
| C06 | Os relatórios mensais devolvem renda zero, e a "projeção" do fluxo de caixa repete o total do período | Revisão; sessão 4 |
| C08 | Categoria é texto livre: "Alimentação", "ALIMENTACAO" e "Outros"/"OUTROS" não se somam no orçamento nem no painel | Revisão; sessões 2 e 3 |
| C18 | Os testes de integração rodam em SQLite e a produção usa PostgreSQL: as migrations e o SQL específico do PostgreSQL não têm teste automatizado. Os controladores de rendas e de relatórios têm pouca cobertura pela camada HTTP | Revisão |

### Severidade média

| ID | Pendência | Origem |
|---|---|---|
| C02 | Um usuário só pode pertencer a um grupo; participar de vários é um requisito planejado | Decisão de produto |
| C07 | O mês é calculado em UTC: uma despesa das 21h às 24h do último dia, no horário de Brasília, cai no mês seguinte | Revisão; sessão 4 |
| C09 | O alerta de gasto elevado se repete a cada nova despesa; parte dos textos de alerta está em inglês; os alertas vão só para quem lançou a despesa | Revisão; sessão 3 |
| C10 | Moedas diferentes do real são aceitas e somadas sem conversão; valores com três casas decimais são arredondados sem aviso | Sessões 2 e 3 |
| C11 | A consulta de situação da captura de notificações conta lançamentos manuais como notificações aceitas | Revisão; sessão 2 |
| C12 | As mensagens de erro da API estão em inglês, em mais de um formato, e algumas expõem nomes internos | Revisão; sessões 1 a 5 |
| C13 | Não é possível corrigir valor, descrição ou data de uma despesa já gravada | Sessão 2 |
| C14 | Não há encerramento de sessão no servidor, troca ou recuperação de senha, nem confirmação de e-mail | Revisão |
| C16 | Importação: uma tarefa interrompida por reinício fica "em processamento"; uma confirmação parcial impede confirmar o restante; créditos somem da lista sem aviso | Revisão; sessão 5 |
| C17 | Uma renda marcada como recorrente não se repete no mês seguinte; o mesmo identificador de aparelho é aceito em dois grupos | Sessão 3 |
| C19 | A camada de aplicação depende do Entity Framework Core; várias relações com o grupo não têm chave estrangeira no banco | Revisão |
| C20 | Aplicativo: a lista mostra só as 20 despesas mais recentes; o código do grupo não reaparece depois da criação; sair da conta não limpa os dados em cache; rótulos e contraste incompletos para acessibilidade | Revisão |
| C21 | Dependências do aplicativo com vulnerabilidade conhecida, quase todas em ferramentas de build | Revisão |

### Severidade baixa

| ID | Pendência | Origem |
|---|---|---|
| C15 | O login responde mais rápido para um e-mail inexistente do que para senha errada, o que permite descobrir quais e-mails têm conta; senhas fracas como "12345678" são aceitas | Revisão; sessão 1 |

### Limitações de ambiente e de privacidade

- **Hospedagem gratuita.** A API suspende após 15 minutos sem uso; a primeira requisição seguinte leva cerca de um minuto. Um monitor externo chama a API a cada 5 minutos para mantê-la ativa; é um contorno que o provedor não garante. O disco é apagado a cada implantação, o que não afeta o sistema porque o PDF do extrato é excluído após a leitura.
- **Privacidade.** O aplicativo não tem tela de consentimento, política de privacidade, interruptor da captura de notificações nem exportação e exclusão de dados. São exigências da LGPD e da Google Play para distribuição a público real; o projeto é um piloto com dados de teste.
- **Dados já armazenados.** O aplicativo deixou de enviar o texto das notificações, mas textos enviados por versões anteriores permanecem no banco até serem apagados.
- **Chave antiga.** A chave removida no A01 continua no histórico do repositório. Ambientes novos usam um segredo gerado pela hospedagem.
- **Validação com dados reais.** Os padrões de leitura de notificações e de extratos foram testados com textos e arquivos sintéticos.

## 8.7 Parecer

O sistema atende ao que se propõe para um piloto: formar um grupo, registrar despesas por três caminhos, acompanhar rendas e metas, e manter os dados de cada grupo isolados. A base é sólida no que é mais difícil de consertar depois: isolamento entre grupos, tratamento de valores monetários, autenticação e uma suíte de testes que cobre 84,0% das linhas da API.

A avaliação encontrou 39 problemas. Dezoito foram corrigidos, entre eles o único crítico (uma chave no repositório) e dez de severidade alta, todos com teste automatizado que falhava antes e passa depois. Dois deles só apareceram com o sistema publicado e o aplicativo em execução, o que mostra o valor de verificar no ambiente real e não só no de desenvolvimento. As cinco sessões de teste, repetidas contra a versão corrigida, não produziram nenhum erro interno, contra 34 na versão avaliada.

Restam 21 pendências. As que mais limitam o uso real são a impossibilidade de sair de um grupo ou trocar o código de convite, a leitura de extratos restrita a um banco, e a ausência dos itens de privacidade exigidos para distribuição pública. Nenhuma delas impede a demonstração do sistema, e todas estão documentadas.

**Conclusão: apto para demonstração e para uso em piloto com dados de teste; não apto para distribuição a público real enquanto as pendências C01 e de privacidade não forem resolvidas.**

## Anexo — Roteiro de verificação no aparelho

Para conferir no celular as correções do aplicativo. Pré-requisitos: API em execução, aplicativo instalado por APK (não pelo Expo Go, por causa da captura de notificações) e um usuário com grupo criado.

### A03 — Sessão renovada

1. Para não esperar 15 minutos, reduza temporariamente a validade do token de acesso para 1 minuto na configuração local da API e reinicie-a.
2. Entre no aplicativo e abra o Painel.
3. Espere pouco mais de um minuto e puxe a tela para atualizar.
   - **Esperado:** os dados carregam; o aplicativo não volta à tela de entrada. No log da API aparece uma chamada a `/api/v1/auth/refresh`.
   - **Antes da correção:** o aplicativo voltava à tela de entrada.

### A04 — Correções na revisão do extrato

1. Em Transações, toque em importar extrato e escolha um PDF.
2. Na revisão, altere a descrição de uma linha e o valor de outra; desmarque uma terceira.
3. Confirme.
   - **Esperado:** em Transações, a primeira linha aparece com a descrição nova, a segunda com o valor novo, e a terceira não aparece.
   - **Antes da correção:** todas apareciam com os dados originais.
4. Importe de novo, apague a descrição de uma linha e confirme.
   - **Esperado:** a linha é destacada com "Informe uma descrição." e nada é enviado.
5. Sem fechar o aplicativo, importe um segundo PDF, diferente.
   - **Esperado:** a revisão mostra só as linhas do segundo PDF.

### A06 e A16 — Só compras viram despesa

1. Com a captura ativada, faça uma compra pequena ou um Pix em um dos bancos suportados.
   - **Esperado:** a despesa aparece em Transações, com valor e estabelecimento.
2. Receba um Pix nesse banco.
   - **Esperado:** nenhuma despesa nova. Antes da correção, o recebimento virava despesa.
3. Se a compra do passo 1 não aparecer, anote o texto exato da notificação: o padrão daquele banco precisa de ajuste.

### A09 — Texto da notificação não sai do aparelho

1. Depois da compra acima, consulte no banco a linha mais recente de `transaction_event_ingests`.
   - **Esperado:** a coluna `raw_notification_text_redacted` está vazia na linha nova.

### A10 — Só PDF

1. Abra a tela de importar extrato.
   - **Esperado:** uma única opção, "Arquivo PDF"; não há opção de câmera.
