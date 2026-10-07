# CoupleSync — app Android

Aplicativo React Native (Expo SDK 52, Expo Router) do CoupleSync, para Android. Registra os gastos de um grupo ("casal") a partir de notificações bancárias, lançamentos manuais e extratos em PDF, e mostra painel, rendas, metas, fluxo de caixa e relatórios.

## O que o app faz

- **Cadastro e login**, com a sessão guardada no armazenamento seguro do aparelho e renovada automaticamente pelo refresh token.
- **Grupo**: uma pessoa cria o grupo e recebe um código de convite de 6 caracteres; as demais entram com o código.
- **Captura de notificações bancárias**: um `NotificationListenerService` lê as notificações dos apps de banco suportados, o app interpreta o texto no próprio aparelho e envia ao servidor só os dados estruturados da despesa.
- **Transações**: lista das mais recentes, lançamento manual, troca de categoria e exclusão.
- **Importação de extrato em PDF**, com tela de revisão antes de gravar.
- **Rendas, metas, fluxo de caixa (30 e 90 dias) e relatórios**.
- **Preferências de alerta** e, quando habilitado no build, a aba do **assistente de IA**.

## Requisitos

- Node.js 20 (a versão usada no CI) e npm
- Android Studio com emulador, ou aparelho Android
- A API do CoupleSync acessível (veja [backend/README.md](../backend/README.md))

## Como rodar

### 1. Instalar

```bash
cd mobile
npm install
```

### 2. Apontar para a API

Crie `mobile/.env` a partir de `.env.example`:

```
EXPO_PUBLIC_API_BASE_URL=http://10.0.2.2:5000
```

O valor é a **raiz** da API, sem `/api/v1` (o cliente HTTP acrescenta o caminho). `10.0.2.2` é o endereço pelo qual o emulador Android alcança o `localhost` da máquina; em aparelho físico na mesma rede, use o IP da máquina. Para a API hospedada, use `https://<seu-servico>.onrender.com`.

Se a variável não for definida, o app usa `http://10.0.2.2:5000`.

### 3. Iniciar

```bash
npx expo start --android
# para limpar o cache do Metro:
npx expo start --android --clear
```

A captura de notificações depende de código nativo próprio (Kotlin) e **não funciona no Expo Go**. Para testá-la, gere o projeto nativo e instale um build:

```bash
npx expo prebuild --platform android --clean
npx expo run:android
```

## Comandos

| Comando | O que faz |
|---|---|
| `npm start` | `expo start` |
| `npm run android` | `expo start --android` |
| `npm run prebuild` | `expo prebuild` (gera `android/`) |
| `npm test` | Testes de unidade com Jest (`ts-jest`, ambiente Node) |
| `npx tsc --noEmit` | Checagem de tipos (é o que o CI executa) |

O script `npm run lint` está declarado no `package.json`, mas **não funciona**: o projeto não tem arquivo de configuração do ESLint.

### Testes

`npm test` roda 111 testes em 5 arquivos, todos de lógica pura: interpretação de notificações, montagem do corpo enviado ao servidor, montagem da confirmação do extrato e renovação de sessão. As telas são testadas no CI, a cada PR, pelo workflow `App E2E` (fluxos Maestro em `tests/e2e/flows/`, num emulador Android); componentes isolados não têm teste. O que continua manual está no roteiro `tests/e2e/manual-walkthrough.md`.

## Variáveis de ambiente

Variáveis `EXPO_PUBLIC_*` são embutidas no pacote JavaScript no momento do build.

| Variável | Obrigatória | Padrão | Observação |
|---|---|---|---|
| `EXPO_PUBLIC_API_BASE_URL` | Não | `http://10.0.2.2:5000` | Raiz da API, sem `/api/v1`. Nos builds do EAS vem do bloco `env` do perfil em `eas.json`. |
| `EXPO_PUBLIC_AI_CHAT_ENABLED` | Não | desligado | `true` exibe a aba "Chat IA". A API também precisa estar com a IA habilitada. |

## Estrutura

```
mobile/
├── app/                              # Rotas (Expo Router)
│   ├── _layout.tsx                   # Layout raiz: sessão, fontes, React Query, toasts
│   ├── (auth)/
│   │   ├── login.tsx
│   │   ├── register.tsx
│   │   └── couple-setup.tsx          # Criar grupo ou entrar com código
│   └── (main)/
│       ├── _layout.tsx               # Abas e proteção de rota
│       ├── index.tsx                 # Dashboard
│       ├── transactions/index.tsx    # Lista de transações
│       ├── transactions/new.tsx      # Nova transação
│       ├── ocr-upload.tsx            # Importar extrato em PDF
│       ├── ocr-review.tsx            # Revisão da importação
│       ├── goals/index.tsx           # Metas
│       ├── cashflow/index.tsx        # Fluxo de caixa
│       ├── budget/index.tsx          # Fontes de renda (aba "Rendas")
│       ├── reports/index.tsx         # Relatórios
│       ├── chat/index.tsx            # Chat IA (aba oculta por padrão)
│       └── settings/                 # Configurações e alertas
├── src/
│   ├── components/                   # Estados de tela, ErrorBoundary, Toast
│   ├── modules/
│   │   ├── chat/                     # Tela, hook e chamada do assistente
│   │   ├── integrations/notification-capture/
│   │   │   ├── NotificationListenerBridge.ts   # Ponte com o serviço nativo
│   │   │   ├── notificationParser.ts           # Classifica e extrai valor/estabelecimento
│   │   │   ├── notification-patterns.json      # Padrões por banco
│   │   │   ├── ingestRequest.ts                # Monta o corpo enviado ao servidor
│   │   │   └── eventUploader.ts                # Envio com novas tentativas
│   │   ├── ocr/                      # Tela de revisão e montagem da confirmação
│   │   └── transactions/categories.ts
│   ├── services/
│   │   ├── apiClient.ts              # Cliente HTTP (axios) tipado
│   │   ├── authRefresh.ts            # Renovação de sessão no 401
│   │   └── pushTokenService.ts       # Registro do token de push
│   ├── state/                        # Stores Zustand (sessão, período do painel)
│   ├── theme/
│   └── types/api.ts                  # Contratos da API
├── android-native/                   # Código Kotlin versionado (serviço e ponte de notificações)
├── plugins/withNotificationListener.js   # Config plugin: manifesto + cópia do Kotlin no prebuild
├── tests/e2e/                        # Roteiro manual e fluxos em YAML
├── app.json
├── eas.json
├── jest.config.js
└── package.json
```

A pasta `android/` é gerada pelo `expo prebuild` e não é versionada.

## Captura de notificações bancárias

Com o acesso às notificações concedido nas configurações do Android:

1. O **`NotificationCaptureService`** (Kotlin) recebe as notificações e repassa ao JavaScript apenas as dos pacotes de banco suportados.
2. O **`notificationParser.ts`** decide, no aparelho, se a notificação é uma despesa, usando os padrões de cada banco:
   - compras, pagamentos e Pix **enviados** são reconhecidos, e o valor e o estabelecimento são extraídos;
   - dinheiro que entra (Pix recebido, transferência recebida, estorno, depósito), compras negadas, propaganda e qualquer texto que não case com um padrão de despesa do banco são **descartados** — nada é enviado.
3. O **`ingestRequest.ts`** monta o corpo da requisição só com dados estruturados (veja abaixo).
4. O **`eventUploader.ts`** envia para `POST /api/v1/integrations/events` e, se falhar, tenta de novo algumas vezes, com a fila mantida em memória.
5. A **API** valida, descarta duplicatas e cria a transação.

Bancos com padrões cadastrados: **Nubank, Itaú, Inter, C6 Bank e Bradesco**. A lista está em `src/modules/integrations/notification-capture/notification-patterns.json`.

As notificações só são interpretadas e enviadas com o JavaScript do app carregado. Enquanto ele não está pronto, o serviço nativo guarda em memória até 50 notificações e as entrega quando o app abre; se o processo for encerrado pelo sistema antes disso, o que estava nessa fila se perde.

### O que é enviado ao servidor

Para cada despesa reconhecida, o app envia exatamente estes campos:

| Campo | Exemplo | Observação |
|---|---|---|
| `bank` | `"Nubank"` | obtido do nome do pacote Android; um de `Nubank`, `Itau`, `Inter`, `C6`, `Bradesco` (nomes aceitos pela API) |
| `amount` | `45.9` | extraído da notificação |
| `currency` | `"BRL"` | sempre BRL |
| `eventTimestamp` | `"2026-10-05T14:30:00.000Z"` | momento em que a notificação foi publicada |
| `merchant` | `"PADARIA DO ZE"` | omitido quando não é possível extrair; em Pix enviado, é o nome do destinatário |

O título e o corpo da notificação são usados só em memória, para o reconhecimento, e **não** são enviados nem armazenados (os campos opcionais `rawNotificationText` e `description` do contrato da API não são usados pelo app).

## Push (alertas)

Ao entrar, o app pede permissão de notificação, obtém o token de push do aparelho com `expo-notifications` e o registra em `POST /api/v1/devices/token`. Falhas nesse passo são ignoradas: o app funciona sem push.

O `app.json` versionado não referencia um `google-services.json`, e esse arquivo é ignorado pelo Git. Para o push funcionar em um build, o projeto Firebase precisa ser configurado nesse build e a API precisa das credenciais `Fcm__*`.

## Gerar o APK

### EAS Build

```bash
npm install -g eas-cli
eas login
eas build --platform android --profile preview
```

Os perfis `preview` e `production` de `eas.json` geram APK e definem `EXPO_PUBLIC_API_BASE_URL`. **Antes de gerar, confira esse valor**: ele deve ser a URL da API em uso (`https://<seu-servico>.onrender.com`).

### Build local

```bash
npx expo prebuild --platform android --clean
cd android
./gradlew assembleDebug
# saída: android/app/build/outputs/apk/debug/app-debug.apk
```

Mais detalhes em [docs/architecture/apk-generation-guide.md](../docs/architecture/apk-generation-guide.md).

## Problemas comuns

**Login ou cadastro mostram "Servidor indisponível" ou "Sem conexão".**
Confira se a API responde (`curl http://localhost:5000/health/ready`) e se `EXPO_PUBLIC_API_BASE_URL` aponta para ela. No emulador, use `10.0.2.2` em vez de `localhost`. Depois de mudar o `.env`, reinicie o Metro com `--clear`.

**A API hospedada demora na primeira requisição.**
No plano gratuito do Render o serviço é suspenso após um período sem tráfego e leva cerca de um minuto para voltar. O cliente HTTP do app desiste após 30 segundos; tente de novo.

**A faixa "Captura de notificações desativada" aparece na tela de transações.**
Toque em "Ativar" (ou em Config > "Notificações do sistema") e conceda o acesso ao CoupleSync na tela do Android. Em Expo Go a captura não está disponível.

**Notificações de banco não viram transação.**
Só compras, pagamentos e Pix enviados dos bancos listados são reconhecidos, e o texto precisa casar com um dos padrões de `notification-patterns.json`. Mensagens em formato diferente são descartadas.

## Documentação relacionada

- [Guia de uso](../docs/guia-de-uso.md)
- [Guia de implantação](../docs/deployment/DEPLOY-GUIDE.md)
- [Decisões de arquitetura](../docs/adr/README.md)
