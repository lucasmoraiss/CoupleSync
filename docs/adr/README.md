# Registros de Decisão de Arquitetura (ADRs)

Decisões de arquitetura do CoupleSync, em ordem cronológica. Cada registro descreve o contexto, a decisão e as consequências no momento em que foi tomada; registros antigos não são reescritos quando a decisão muda, apenas marcados como substituídos.

Os registros foram escritos em inglês durante o desenvolvimento e ficavam espalhados em pastas de sessão, com numeração repetida. Em outubro de 2026 foram reunidos aqui e renumerados; a coluna "Número original" preserva a referência antiga.

| ADR | Decisão | Data | Número original | Situação |
|---|---|---|---|---|
| [0001](0001-monolito-modular.md) | Monólito modular com módulos de domínio explícitos | abr/2026 | ADR-001 (bootstrap) | Vigente |
| [0002](0002-autorizacao-por-casal.md) | Autorização por casal no token, no serviço e na consulta | abr/2026 | ADR-002 (bootstrap) | Vigente |
| [0003](0003-jobs-em-processo.md) | Jobs em segundo plano dentro do processo da API | abr/2026 | ADR-003 (bootstrap) | Vigente em parte: a sincronização via Pluggy foi abandonada pelo ADR-0004 |
| [0004](0004-captura-de-notificacoes-android.md) | Captura de transações por notificações bancárias no Android | abr/2026 | ADR-004 (bootstrap) | Vigente |
| [0005](0005-stack-de-nuvem.md) | Escolha da stack de nuvem (Azure Container Apps + Neon) | abr/2026 | ADR-001 (v1.5) | Substituído: a API rodou em Azure App Service e depois foi migrada |
| [0006](0006-provedor-de-ocr.md) | Provedor de OCR | abr/2026 | ADR-002 (v1.5) | Vigente em parte: o padrão passou a ser o parser local do ADR-0007 |
| [0007](0007-parser-local-de-pdf.md) | Parser local de extratos em PDF (PdfPig + Strategy) | abr/2026 | ADR-001 (v1.6) | Vigente |
| [0008](0008-componente-de-toast.md) | Componente de toast próprio em vez de biblioteca | abr/2026 | ADR-002 (v1.6) | Vigente |
| [0009](0009-biblioteca-de-graficos.md) | Biblioteca de gráficos `react-native-gifted-charts` | abr/2026 | ADR-003 (v1.6) | Vigente |
| [0010](0010-categorizacao-automatica-por-ia.md) | Categorização automática por IA em lote, com isolamento de falha | abr/2026 | ADR-004 (v1.6) | Vigente |
| [0011](0011-renda-rapida-sem-plano.md) | Renda rápida cria o plano do mês quando ele não existe | abr/2026 | ADR-005 (v1.6) | Vigente |

Referências cruzadas dentro do texto de um ADR usam o número original da mesma série (bootstrap, v1.5 ou v1.6).
