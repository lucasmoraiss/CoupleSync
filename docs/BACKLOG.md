# CoupleSync — Backlog Futuro

Lista viva de itens identificados durante a estabilização pós-lançamento do piloto.
Priorizados para iterações seguintes. Fora do escopo da correção imediata de 22-Abr.

## Alta prioridade

### OCR / Import de extratos — robustez
- **Problema observado**: usuários relataram inconsistências ao importar extratos
  via OCR. Hoje é o único caminho automatizado de entrada.
- **Ações**:
  - Reforçar validação do payload Gemini antes de persistir (campos obrigatórios,
    datas, valores, duplicidade por fingerprint).
  - Telemetria: logar `ingestId`, tempo total, taxa de parsing bem-sucedido.
  - UI: mostrar status granular (upload → OCR → parsing → persistência) com
    recuperação explícita em falha.
  - Fallback: permitir edição manual do resultado do OCR antes de salvar.

### Refresh token após mudança de casal
- **Hoje**: após `create couple` / `join couple`, o backend re-emite um JWT
  completo. Funciona, mas mistura fluxo de auth com fluxo de domínio.
- **Evolução**: implementar refresh token real (`POST /auth/refresh`) e chamar
  após qualquer mudança de claims sensíveis (couple_id, role).
- Benefício: simplifica handlers e reduz superfície de mudança quando novas
  claims forem adicionadas.

### IngestEventId nullable em Transaction
- **Hoje**: transações manuais criam um `TransactionEventIngest` sintético com
  `bank = "MANUAL"` para satisfazer FK obrigatória.
- **Evolução**: migração EF Core tornando `IngestEventId` nullable + índice
  parcial. Remove dados sintéticos e melhora relatórios por fonte.

## Média prioridade

### Notificações push para parceiro
- Enviar push via FCM quando o parceiro entra no casal, quando uma transação
  grande é registrada e quando o orçamento mensal passa de um limite.
- Hoje FCM está configurado mas sem handlers de domínio.

### Orçamento mensal / alertas
- Modelo de orçamento por categoria (mensal) + comparação em tempo real nos
  Reports. Alerta proativo quando passar de 80 %.

### Edição / remoção de transação
- Hoje transações são somente-leitura após criadas. Precisamos permitir corrigir
  categoria, descrição e valor (manuais); para OCR, ao menos categoria.
- Auditoria: guardar quem editou e quando (`AuditLog`).

## Qualidade e DX

### Acessibilidade mobile
- Auditoria de contraste (WCAG AA) no tema escuro.
- Suporte a font scaling do Android sem quebrar o layout.
- Labels em todos os `TouchableOpacity` (parcial hoje).

### Testes end-to-end mobile
- Detox ou Maestro em pipeline para cobrir: login → criar casal → importar OCR →
  adicionar manual → ver relatório.
- Hoje só temos testes de unidade no backend.

### Observabilidade backend
- Application Insights ou equivalente (métrica por endpoint, erro por handler,
  latência Neon).
- Structured logging com correlation ID propagado do mobile.

### CI/CD
- GitHub Actions: build backend + testes + build EAS mobile em cada PR.
- Deploy contínuo do backend via `az webapp deploy` em push na `main`.

## Investigações / dívidas

### Expo SDK 53 upgrade
- SDK 52 é o target atual. Avaliar upgrade quando o ecosystem de libs (charts,
  secure-store) estiver estável em 53.
