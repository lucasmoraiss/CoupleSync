# Sessão de Teste Exploratório #1 — CoupleSync

- **Data/hora da execução:** 2026-10-05, 16:09 (horário local) / 2026-10-05T19:09:41Z (UTC)
- **API testada:** `http://localhost:5000` — commit `98e3f64`
- **Escopo:** `/api/v1/auth/*` (cadastro, login, refresh) e `/api/v1/couples/*` (criar, entrar, ver casal)
- **Total de casos:** 42 (numerados 1.01–1.43; o 1.26 foi descartado antes da execução). Os casos 1.19 (10 logins) e 1.37 (20 joins) são repetições em laço.
- **Artefatos:** `sessao-1.sh` (script reexecutável) e `sessao-1.log` (saída completa com comando, status e corpo de cada caso).
- **Observação:** nenhuma resposta 5xx foi observada em toda a sessão.

---

## O que testei e funcionou

- **1.01 / 1.02** — Cadastro de A e B retorna `201` com `user`, `accessToken` e `refreshToken`.
- **1.03** — Login de A retorna `200` com novo par de tokens.
- **1.04** — `refresh` retorna `200` e rotaciona o refresh token (novo valor diferente).
- **1.05** — A cria o casal: `201` com `coupleId`, `joinCode` (6 chars) e novo `accessToken` já com `couple_id`.
- **1.06** — B entra no casal com o código: `200`, lista os 2 membros corretos.
- **1.07 / 1.08** — `GET /couples/me` de A e de B: `200`, ambos veem o mesmo casal com os 2 membros.
- **1.09** — E-mail duplicado: `409 EMAIL_ALREADY_IN_USE`.
- **1.10** — E-mail malformado: `400` com erro de validação no campo `Email`.
- **1.11** — Senha de 7 caracteres: `400` (mínimo de 8 aplicado).
- **1.14 / 1.15 / 1.18** — Campos ausentes (password / email / password no login): `400` de validação.
- **1.16** — JSON malformado: `400` (não gera 5xx).
- **1.17** — Corpo vazio: `400` ("A non-empty request body is required.").
- **1.20** — Login com e-mail inexistente: `401 INVALID_CREDENTIALS` — mesma mensagem genérica do caso de senha errada (não revela se o e-mail existe).
- **1.21** — `refresh` com token inválido: `401 UNAUTHORIZED`.
- **1.22** — `refresh` reutilizando token já rotacionado (o anterior ao 1.04): `401 UNAUTHORIZED` — a rotação invalida o token antigo corretamente.
- **1.23 / 1.24 / 1.25** — Chamada autenticada sem token, com token adulterado na assinatura e com esquema `Basic`: todas `401`.
- **1.29** — `join` com código inexistente (6 chars válidos): `404 COUPLE_NOT_FOUND`.
- **1.30** — `join` com o código em minúsculas: `200` — a API normaliza para maiúsculas (comportamento razoável). *(Este caso expôs o defeito do 3º membro, relatado abaixo.)*
- **1.34** — Usuário que já tem casal tenta criar outro: `409 USER_ALREADY_IN_COUPLE`.
- **1.35** — Usuário que já tem casal tenta entrar em outro: `409 USER_ALREADY_IN_COUPLE`.
- **1.36** — Usuário sem casal chama `GET /api/v1/transactions`: `403 COUPLE_REQUIRED`, exatamente como esperado.
- **1.38 / 1.39** — `join` com código de 3 chars e vazio: `400` de validação de formato.
- **1.40** — Criar casal sem token: `401`.

---

## O que testei e não funcionou — o que deve ser corrigido

| Caso | O que fiz | O que esperava | O que aconteceu (status + trecho) | Gravidade |
|---|---|---|---|---|
| 1.30–1.33 | Um terceiro usuário C entrou no mesmo casal (código do casal de A+B); depois `GET /couples/me` de A, B e C | Rejeição (um "casal" tem no máximo 2 pessoas), algo como `409 COUPLE_FULL` | C **foi aceito** (`200`). `GET /couples/me` de **A, B e C** passa a listar **3 membros**. Dados financeiros do casal ficam visíveis a um terceiro. `Couple.AddMember` não limita o número de membros. | **Alta** |
| 1.19 + 1.20 | 10 logins com senha errada (usuário existente) vs. 1 login com e-mail inexistente, comparando tempo | Tempo de resposta semelhante nos dois casos (resistência a enumeração por timing) | Senha errada em usuário existente: **~0,44–0,60 s** (verifica o hash). E-mail inexistente: **~0,23 s** (não verifica). A mensagem é idêntica, mas a diferença de tempo (~2x) permite **enumerar usuários por timing**. | Média |
| 1.19 | 10 tentativas de login com senha errada no mesmo e-mail, em sequência | Após N falhas, algum bloqueio/atraso/captcha | Todas as 10 retornaram `401` imediatamente, **sem bloqueio, atraso progressivo ou rate limit**. Sem proteção contra força bruta de senha. | Média |
| 1.37 | 20 `join` seguidos com códigos aleatórios (mesmo usuário) | Algum rate limit após várias tentativas de adivinhação de código | Todas as 20 retornaram `404` imediatamente, **sem bloqueio**. Códigos de 6 chars alfanuméricos podem ser enumerados sem limite. | Média |
| 1.10, 1.11, 1.14–1.18, 1.38, 1.39 (formato) + todos os erros de negócio | Comparei o corpo de cada tipo de erro | Um único formato de erro consistente | **Dois/três formatos coexistem:** (A) erros de negócio → `{code, message, traceId}`; (B) validação de modelo → ProblemDetails RFC9110 `{type, title, status, errors, traceId}`; (C) 401 de JWT e 405 → **corpo vazio**. Inconsistência para quem consome a API. | Baixa |
| Todos os erros | Verifiquei o idioma das mensagens | Mensagens em pt-BR (o app é claramente pt-BR — notificação "entrou no seu casal! 💕") | **Todas as mensagens de erro estão em inglês**: "Email is already in use.", "Invalid credentials.", "Couple was not found.", "You must be paired with a partner to access this resource.", e as do validador ("'Email' is not a valid email address."). | Baixa/Média |
| 1.16 | Enviei JSON malformado | Mensagem genérica de "JSON inválido" | A resposta **vaza detalhe interno do parser**: "Expected depth to be zero at the end of the JSON payload... Path: $.password \| LineNumber: 0 \| BytePositionInLine: 68." | Baixa |
| 1.12 | Cadastro com senha `12345678` | Possível rejeição de senha trivial/comum | **Aceita** (`201`). Só valida comprimento mínimo (8); não há checagem de senha fraca/comum. (Discutível; muitos sistemas também não checam.) | Baixa |
| 1.13 | Cadastro com senha de 200 caracteres | Possível limite máximo de comprimento | **Aceita** (`201`). Não há limite máximo de senha. | Baixa |

---

## Funcionalidade não testada (faltou ou não foi implementada)

- **Sair do casal / remover membro / trocar o código (casos 1.41–1.43):** não existem na API. `DELETE /couples/me` → `405`; `POST /couples/leave` → `404`; `POST /couples/code/rotate` → `404`. Confirmado também na leitura do `CouplesController` (só há `POST /couples`, `POST /couples/join` e `GET /couples/me`). Isso é especialmente grave combinado com o defeito 1.30: **não há como desfazer a entrada de um terceiro membro** nem rotacionar um código que tenha vazado.
- **Expiração do access token por tempo:** não testada — exigiria esperar o TTL do JWT ou manipular o relógio, fora do alcance de uma sessão de caixa-preta curta.
- **Expiração do refresh token:** não testada (depende do TTL `RefreshTokenTtlDays`); só foi validada a invalidação por rotação (1.22).
- **Concorrência real entre as 5 sessões paralelas:** não medida de forma controlada; a sessão usou usuários próprios com prefixo `s1-` e não observou interferência.
- **Confirmação de persistência/isolamento no banco:** teste caixa-preta; não inspecionei o banco diretamente (regra de não acessar fora da API).
- **Verificação do e-mail de cadastro / fluxo de recuperação de senha:** não há endpoint para isso no escopo; aparentemente não implementado.
