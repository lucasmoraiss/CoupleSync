# Evidências do laudo de qualidade

Arquivos citados no [laudo de qualidade](../08-laudo-de-qualidade.md). Todos foram gerados em 05/10/2026.

## `correcoes/` — testes antes e depois de cada correção

Para cada erro corrigido há um par de arquivos com a saída do teste automatizado que o reproduz: `<ID>-antes.txt`, executado antes da correção (falhando), e `<ID>-depois.txt`, executado depois (passando). O A04 tem dois pares, um para a API e outro para o aplicativo.

| Arquivos | Erro |
|---|---|
| `A01-*` | Chave de assinatura no repositório |
| `A02-A14-*` | Confirmação da importação de extrato (linhas repetidas, reimportação, índice inexistente) |
| `A03-*` | Renovação de sessão no aplicativo |
| `A04-api-*`, `A04-app-*` | Correções de valor e descrição na revisão do extrato |
| `A05-*` | Limite de tentativas |
| `A06-*` | Leitura de notificações bancárias |
| `A07-*` | Entrada inválida devolvendo erro interno |
| `A08-*` | PDF de banco não suportado |
| `A09-*` | Texto da notificação enviado ao servidor |
| `A11-*` | Atualização do valor guardado de uma meta |
| `A12-*` | Alertas sem preferências gravadas |
| `A13-*` | Total de rendas em grupo de três |
| `A16-*` | Nome de banco recusado pela API |
| `A17-*` | Limite de tentativas atrás de CDN; `A17-producao.log` é a verificação na API publicada |
| `A18-*` | Rótulos do aplicativo: mês do painel, nome de categoria e de membro |

## `sessoes/` — sessões de teste exploratório

| Arquivo | Conteúdo |
|---|---|
| `sessao-1.sh` a `sessao-5.sh` | Roteiros reexecutáveis (shell + `curl`). Cada um cria seus próprios usuários e grava, para cada caso, o comando, o código de resposta e o corpo |
| `relatorio-sessao-1.md` a `relatorio-sessao-5.md` | Relatório de cada sessão sobre a versão avaliada (commit `98e3f64`): o que funcionou, o que não funcionou e o que não foi testado |
| `limite-de-tentativas.log` | Saída da verificação do limite de tentativas na versão corrigida |
| `sessao-5-arquivos/` | Programas que geram os PDFs de teste da sessão 5, com dados inventados |

Para executar uma sessão: suba a API com `docker compose up -d --build` na raiz do repositório e rode `bash sessao-N.sh` nesta pasta. As sessões 3 e 5 também consultam o banco, em modo de leitura, pelo contêiner do PostgreSQL; o nome do contêiner está no início de cada roteiro. Com o limite de tentativas padrão (5 por minuto), os roteiros que criam muitos usuários precisam que a API seja iniciada com `RateLimiting__Auth__PermitLimit` e `RateLimiting__CoupleJoin__PermitLimit` maiores.

Os registros completos de execução (saída bruta de cada roteiro, antes e depois das correções) não estão versionados por conterem tokens de sessão de teste.
