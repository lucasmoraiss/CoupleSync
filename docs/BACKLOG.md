# CoupleSync — Backlog Futuro

Lista viva de itens identificados durante a estabilização pós-lançamento do piloto.
Última atualização: **30/Abr/2026** (após execução autônoma de backlog).

---

## ✅ Concluído em 30/Abr/2026

Os itens abaixo foram **resolvidos** na sessão `.agents-work/2026-04-30_backlog-execution/`:

- ✅ **CRÍTICO — Ícones desaparecem no build EAS** → adicionado `expo-font` ao `app.json` plugins
- ✅ **Dashboard "Total de gastos" não atualiza após OCR** → invalidação de queries `dashboard`/`reports`/`budget` no `confirmMutation.onSuccess`
- ✅ **OCR — mensagens de erro genéricas** → `OcrBackgroundJob` agora tem `catch(OcrException)` que preserva o código real (`PDF_ENCRYPTED`, `PDF_TOO_SHORT`, `NO_TRANSACTIONS_FOUND`, `BANK_FORMAT_UNKNOWN`); mobile traduz cada código em mensagem pt-BR específica
- ✅ **OCR — IngestEventId Guid.Empty FK violation** → `ImportJobService` agora cria `TransactionEventIngest` sintético por candidato confirmado (paralelo ao fluxo manual)
- ✅ **AlertPolicyService não disparava em transação manual nem OCR** → `IAlertPolicyService` injetado em `CreateManualTransactionCommandHandler` e `ImportJobService.ConfirmCandidatesAsync`, com try/catch + `LogWarning` (fire-and-forget seguro)
- ✅ **Alerta de orçamento aos 80%** → nova regra `BudgetWarning` em `AlertPolicyService` com mensagem pt-BR
- ✅ **Notificação "parceiro entrou no casal"** → `JoinCoupleCommandHandler` emite `NotificationEvent` PartnerJoined (try/catch para não quebrar o join)
- ✅ **DELETE de transação** → endpoint `DELETE /api/v1/transactions/{id}` com isolamento de casal (`ForbiddenException` se for de outro casal); mobile com long-press + confirmação Alert
- ✅ **Acessibilidade — `couple-setup` e `settings`** → todos os `TouchableOpacity` com `accessibilityLabel` + `accessibilityRole="button"`; `textMuted` ajustado de `#94A3B8` para `#B8C2D1` (WCAG AA 4.5:1 contra surface)
- ✅ **OCR telemetry** → `Stopwatch` por job; logs estruturados com `IngestId`, `ElapsedMs`, `TransactionCount`
- ✅ **OCR UI — status mais granular durante polling** → mensagens pt-BR diferenciadas para `Pending` ("Aguardando processamento...") e `Processing` ("Processando extrato...")
- ✅ **CI/CD — upload de resultados de teste como artefatos** → `actions/upload-artifact@v4` com `if: always()` para `unit-test-results`, `integration-test-results`, `e2e-test-results`; `expo-doctor` step (informativo)

**Verificação**: 257/257 testes unitários passam, build .NET limpo, TypeScript compila sem erros.

---

## 🔴 CRÍTICO

> Atualmente sem itens críticos abertos. O bloqueador EAS foi resolvido.

---

## Alta prioridade

### PDF protegido por senha — campo de senha como fallback (P0 deferido)
- **Status**: Mensagem pt-BR clara já é exibida ("Por enquanto, exporte o extrato sem senha"). O fluxo completo (campo senha + retry com senha) foi adiado pois propagar a senha do mobile até o `PdfPigTextExtractor` sem persistir requer refatoração não trivial do pipeline de background job.
- **Ações pendentes**:
  - Backend: aceitar `password` opcional no upload OCR (form field), passar via parâmetro até `PdfPigTextExtractor` que abre o PDF com `new ParsingOptions { Password = password }`. Não persistir a senha — deve fluir apenas em memória durante o request.
  - Tratar `PdfDocumentEncryptedException` quando senha incorreta → `OcrException("PDF_PASSWORD_INCORRECT", "Senha incorreta. Tente novamente.")`.
  - Mobile: quando `errorCode === 'PDF_ENCRYPTED'`, exibir `TextInput` inline com botão "Tentar novamente com senha"; se `errorCode === 'PDF_PASSWORD_INCORRECT'`, exibir mensagem inline próxima ao input.
  - Decidir: manter o background job assíncrono (com senha em memória encriptada) OU mudar para upload síncrono quando há senha (mais simples).

### Confirmação de e-mail no cadastro
- **Status**: Aguardando definição de infraestrutura de e-mail (item separado abaixo).
- **Por que é importante**: pré-requisito para "Esqueci a senha" e prevenção de cadastros com e-mail inválido.

### Esqueci a senha
- **Status**: Aguardando infraestrutura de e-mail.
- Link "Esqueci minha senha" na tela de login + fluxo: e-mail → token → tela de nova senha.

### OCR — fallback de edição manual antes de salvar
- **Status**: Tela de revisão (`OcrReviewScreen`) já permite confirmar/cancelar candidatos. Falta permitir editar **descrição, valor e categoria** dos candidatos antes de confirmar (hoje só categoria).
- **Ações**: adicionar campos editáveis na linha do candidato; validar localmente; enviar valores editados no `confirmMutation`.

---

## Média prioridade

### Edição completa de transação (PATCH amount/description/merchant)
- Hoje o backend só aceita `PATCH /transactions/{id}/category` e `PATCH /transactions/{id}/goal`. Não permite corrigir valor, descrição ou estabelecimento.
- **Ações**: novo endpoint `PATCH /transactions/{id}` com validação FluentValidation; mobile com tela/modal de edição.
- **Auditoria**: considerar `AuditLog` para registrar edições (quem, quando, valor antigo).

### IngestEventId nullable (refatoração definitiva)
- **Status**: O fix de Abr/30 cria `TransactionEventIngest` sintético para OCR (paralelo ao fluxo manual). Funciona, mas continua poluindo a tabela com registros sintéticos.
- **Evolução**: migration EF Core tornando `IngestEventId` nullable, atualizar `Transaction.Create` para aceitar `Guid?`, remover criação sintética em ambos os fluxos (OCR e manual). Adicionar índice parcial.

### Logging com correlation ID propagado do mobile
- Hoje cada request gera logs no backend mas não há correlação com a sessão mobile.
- **Ações**: mobile gera `X-Correlation-Id` por request (UUID v4); backend lê e propaga em todos os logs estruturados via `LogContext.PushProperty` ou similar.

### Multi-membros no grupo (família, trisal)
- **Status**: Bloqueio em `JoinCoupleCommandHandler.cs` linha 42 (`if (couple.Members.Count >= 2) throw COUPLE_FULL`).
- **Escopo**: arquitetural — afeta domain (`Couple` → `Group`/`Household`?), todos os filtros por `CoupleId`, UI de compartilhamento.
- **Sugestão**: spike de investigação antes de implementar. Decidir se mantém o nome `Couple` (com membros 1..N) ou renomeia.

### Participação em múltiplos grupos
- Hoje um usuário tem `User.CoupleId` único. Permitir N:N exigiria mudança de schema.
- **Sugestão**: depende da decisão acima (Couple → Group). Implementar junto.

---

## Qualidade e DX

### Testes E2E mobile (Maestro)
- **Status**: Hoje só `mobile/tests/e2e/manual-walkthrough.md` (10 cenários manuais). Sem Detox nem Maestro.
- **Ações**: instalar Maestro (mais leve que Detox para Expo managed); fluxo mínimo: login → criar casal → adicionar manual → ver dashboard atualizar; integrar em CI (job separado, talvez Maestro Cloud).
- **Estimativa**: sprint completo.

### Acessibilidade — auditoria completa
- **Status**: Telas `couple-setup` e `settings` cobertas em Abr/30. Faltam: `login`, `register`, `transactions/index`, `transactions/new`, `dashboard (tabs)/index`, `goals`, `chat`.
- **Ações**: passar em cada `TouchableOpacity` adicionando `accessibilityLabel` + `accessibilityRole`; revisar `TextInput` com `accessibilityHint`; testar com TalkBack.
- **Suporte a font scaling**: testar Android com tamanho de fonte máximo, ajustar layouts que quebrem.

### Observabilidade backend
- Application Insights ou OpenTelemetry: métricas por endpoint, latência por handler, erros por categoria.
- Structured logging com correlation ID (ver item acima).

### CI/CD — gates de qualidade
- **Coverage gate**: enforcar mínimo (ex. 80%) com `coverlet` + threshold; falhar PR se cair.
- **Mobile EAS build em PR**: com label `build-apk` para opt-in (não em todo PR para economizar quota Expo).
- **Lint mobile**: ESLint + Prettier check em CI.

---

## Investigações / dívidas

### 🔴 Infraestrutura de envio de e-mail — DECIDIR PRIMEIRO
- **Bloqueia**: confirmação de e-mail, esqueci a senha.
- **Opções a avaliar**:
  - **Resend** (plano gratuito 100/dia, API moderna, fácil integração com .NET via HttpClient)
  - **SendGrid** (gratuito 100/dia, integração via NuGet `SendGrid` package)
  - **Azure Communication Services Email** (pay-as-you-go, integração nativa com Azure App Service)
  - **Mailgun** (gratuito limitado)
- **Critérios**: custo zero/baixo no piloto (≤10 usuários, ~50 e-mails/mês), facilidade .NET 8, configuração via env vars (sem SMTP).
- **Recomendação técnica**: **Resend** ou **Azure Communication Services** — ambos REST puros, sem dependência de SMTP server.

### Refresh token automático após mudança de claims
- **Status**: Funciona — `CreateCoupleCommandHandler` e `JoinCoupleCommandHandler` já re-emitem JWT completo. Não é bug.
- **Refatoração opcional**: mover emissão de token para o mobile (chamar `POST /auth/refresh` após qualquer mudança de claim sensível). Reduz acoplamento entre auth e domínio.
- **Prioridade**: baixa, só vale quando adicionar novas claims (ex.: role).

### Expo SDK 53 upgrade
- **Status**: SDK 52 estável e funcional. Adiar até libs críticas (charts, secure-store, reanimated) terem versões SDK 53 estáveis.
- **Quando fazer**: Q3/Q4 2026 ou quando Expo SDK 52 entrar em deprecated.

### N+1 query em `ImportJobService.ConfirmCandidatesAsync`
- **Detalhe**: hoje `SaveChangesAsync` é chamado dentro do loop sobre `created` para persistir alert events. Negligível em V1 (lotes pequenos), otimizar quando crescer.
- **Fix**: `AddRange` + um único `SaveChangesAsync` ao final do loop.

### IDOR teórico em DELETE transaction
- **Status**: Hoje `DeleteTransactionCommandHandler` usa `GetByIdRawAsync` (sem filtro de casal) e diferencia 404 vs 403. Permite a um atacante autenticado provar se um UUID pertence a outro casal (mas não vê dados).
- **Risco real**: nenhum no piloto (UUID v4 ~2^122 espaço). Em produção considerar retornar sempre 404 (fail-safe default).

### Padronização de error codes
- Backend hoje tem códigos em algumas exceptions (`COUPLE_FULL`, `EMAIL_ALREADY_IN_USE`, `INVALID_CREDENTIALS`, `PDF_ENCRYPTED`...) e o mobile mapeia caso a caso.
- **Ações**: criar enum/constantes no backend para evitar typos; documentar todos os códigos em `docs/api-error-codes.md`.

### Internacionalização (i18n)
- Hoje todas as strings pt-BR estão hardcoded no JSX/handlers. Se houver expansão para mercados de outras línguas, todo o app precisa ser refatorado.
- **Custo**: alto. **Adiar**: só quando houver decisão de produto sobre internacionalização.

### Métricas de produto / analytics
- Hoje sem nenhum tracking de uso (quantas transações/casal/mês, taxa de uso de OCR vs manual, retenção D1/D7/D30).
- **Ações**: integrar Mixpanel/Amplitude/PostHog (free tier) com eventos chave: app_opened, transaction_created (manual/ocr), couple_created, couple_joined, ocr_failed (com errorCode).

### Backup automático do banco PostgreSQL
- Verificar se Neon (DB atual) faz backup point-in-time. Se não, configurar backup diário para Azure Storage.

### Rate limiting
- Hoje endpoints públicos (`/auth/login`, `/auth/register`) não têm rate limiting. Vulnerável a brute force / spam de cadastros.
- **Ações**: middleware ASP.NET Core Rate Limiter (built-in no .NET 8) com 5 tentativas/min por IP em `/auth/*`.
