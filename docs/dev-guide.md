# CoupleSync — Dev Guide & Interface Test Plan

> Siga este guia para subir o ambiente de desenvolvimento do zero e validar todas as funcionalidades pelo app mobile (como um QA/usuário faria).

---

## 1. Pré-requisitos

| Ferramenta | Versão mínima | Verificar |
|---|---|---|
| .NET SDK | 8.0 | `dotnet --version` |
| PostgreSQL | 15+ | `psql --version` |
| Node.js | 20 LTS | `node --version` |
| npm | 10+ | `npm --version` |
| Expo CLI | (via npx) | `npx expo --version` |
| Android Studio + Emulator | API 31+ | AVD Manager |
| Git | qualquer | `git --version` |

---

## 2. Backend — Subindo o servidor

### 2.1 Banco de dados

Crie o banco e aplicar as 9 migrações:

```powershell
# no psql ou pgAdmin: criar banco
psql -U postgres -c "CREATE DATABASE couplesync;"

# na raiz do repo:
cd backend
dotnet ef database update --project src/CoupleSync.Infrastructure --startup-project src/CoupleSync.Api
```

Se o comando `dotnet-ef` não estiver instalado:
```powershell
dotnet tool install --global dotnet-ef
```

### 2.2 Variáveis de ambiente (Desenvolvimento)

O arquivo `backend/src/CoupleSync.Api/appsettings.json` já tem os valores padrão para desenvolvimento local:

```json
"ConnectionStrings": {
  "DefaultConnection": "Host=localhost;Port=5432;Database=couplesync;Username=postgres;Password=postgres"
},
"Jwt": {
  "Secret": "REPLACE_WITH_ENV_JWT_SECRET_32CHARS_MIN"
}
```

**⚠️ Antes de subir**, troque o `Secret` por uma string de no mínimo 32 caracteres. Pode usar `appsettings.Development.json` para isso (não commitado):

```json
{
  "Jwt": {
    "Secret": "dev-secret-local-pelo-menos-32-chars!!"
  }
}
```

> FCM (`Fcm.ProjectId` e `Fcm.CredentialJson`) pode ficar vazio em dev — o worker loga um aviso e pula o envio, sem quebrar nada.

### 2.3 Iniciar a API

```powershell
cd backend/src/CoupleSync.Api
dotnet run --launch-profile http
```

A API sobe em **http://localhost:5210**. Você verá:

```
info: Now listening on: http://localhost:5210
```

### 2.4 Verificar saúde

```
GET http://localhost:5210/health/live   → 200 Healthy
GET http://localhost:5210/health/ready  → 200 Healthy
```

---

## 3. Mobile — Subindo o app

### 3.1 Instalar dependências

```powershell
cd mobile
npm install
```

### 3.2 Configurar endpoint da API

Crie `mobile/.env`:

```
EXPO_PUBLIC_API_BASE_URL=http://10.0.2.2:5210
```

> `10.0.2.2` é o alias do emulador Android para `localhost` da máquina host.
> Se usar **dispositivo físico** na mesma rede Wi-Fi, troque pelo IP da sua máquina: `http://192.168.x.x:5210`

### 3.3 Iniciar o app

```powershell
cd mobile
npx expo start --android
```

O Metro Bundler abre e instala o app no emulador. Na primeira vez pode demorar ~2 minutos.

### 3.4 Estrutura de telas

```
(auth)/login.tsx           ← Tela de login (tela inicial sem token)
(auth)/register.tsx        ← Tela de registro (nome, email, senha)
(auth)/couple-setup.tsx    ← Criar casal ou entrar com código
(main)/index.tsx           ← Dashboard
(main)/goals/index.tsx     ← Metas
(main)/cashflow/index.tsx  ← Fluxo de Caixa
(main)/settings/index.tsx  ← Configurações (+ sair da conta)
```

> Auth guard: se não houver token, qualquer rota de `(main)` redireciona para login.

---

## 4. Plano de Testes — Via Tela do App

Abra o app no emulador/dispositivo e siga os passos. Para cada teste, anote **PASS / FAIL** e observações.

---

### BLOCO A — Registro e Login (AC-001)

#### A-1: Tela de login aparece PASS
1. Abra o app (primeira vez, sem dados salvos)
2. **Esperado:** Tela de login com fundo escuro, campos "Email" e "Senha", botão "Entrar", link "Criar conta"

#### A-2: Registro do Usuário 1 (Ana) PASS
1. Na tela de login, toque em **"Criar conta"**
2. **Esperado:** Tela de registro com campos Nome, Email, Senha, Confirmar senha
3. Preencha: `Ana` / `ana@teste.com` / `Senha123!` / `Senha123!`
4. Toque em **"Criar conta"**
5. **Esperado:** Navegação para tela de **Configuração do casal** (emoji 💑, botões "Criar casal" e "Entrar em um casal")

#### A-3: Criar casal (Ana) PASS - Não é possível copiar o código, o sistema também não gerou um 
1. Na tela de configuração do casal, toque em **"Criar casal"**
2. **Esperado:** Tela com emoji 🎉, "Casal criado!", e um **código de 6 caracteres** (ex: `A3B7K9`)
3. **Anote o código** — é o que o parceiro vai usar
4. Toque em **"Ir para o Dashboard"**
5. **Esperado:** Navegação para tela principal com abas (Dashboard, Metas, Fluxo, Config)

#### A-4: Sair da conta (Ana)
1. Na aba **"Config"**, toque em **"Sair da conta"**
2. Confirme no diálogo
3. **Esperado:** Volta para tela de login

#### A-5: Registro do Usuário 2 (Bruno)
1. Toque em **"Criar conta"**
2. Preencha: `Bruno` / `bruno@teste.com` / `Senha123!` / `Senha123!`
3. Toque em **"Criar conta"**
4. **Esperado:** Tela de configuração do casal

#### A-6: Entrar no casal (Bruno)
1. Toque em **"Entrar em um casal"**
2. **Esperado:** Tela com campo para digitar o código e botão "Entrar"
3. Cole o código anotado no A-3
4. Toque em **"Entrar"**
5. **Esperado:** Navegação para o Dashboard — agora Bruno e Ana estão no mesmo casal

#### A-7: Login com senha errada
1. Saia da conta (Config → Sair da conta)
2. Na tela de login, digite: `ana@teste.com` / `senhaerrada`
3. Toque em **"Entrar"**
4. **Esperado:** Alerta de erro (email ou senha incorretos), sem navegação

#### A-8: Login correto
1. Digite: `ana@teste.com` / `Senha123!`
2. Toque em **"Entrar"**
3. **Esperado:** Navegação direto para o Dashboard (pula tela de casal porque já está em um)

---

### BLOCO B — Navegação e Telas Principais

#### B-1: Dashboard
1. Após login, confirme que a aba **Dashboard** está selecionada
2. **Esperado:** Tela com "Olá! 👋", card de saldo total com "—", cards de receitas e despesas

#### B-2: Metas
1. Toque na aba **Metas**
2. **Esperado:** Tela com título "Metas", estado vazio com emoji 🎯 e texto "Nenhuma meta ainda"

#### B-3: Fluxo de Caixa
1. Toque na aba **Fluxo**
2. **Esperado:** Tela com título "Fluxo de Caixa", estado vazio com emoji 📊

#### B-4: Configurações
1. Toque na aba **Config**
2. **Esperado:** Menu com "Notificações", "Código do casal", "Sair da conta" (em vermelho)

#### B-5: Tab bar
1. Verifique que a tab bar no fundo tem fundo escuro e a aba ativa está destacada em roxo/índigo
2. **Esperado:** Navegação fluida entre todas as abas

---

### BLOCO C — Proteção de Rotas

#### C-1: Guard de autenticação
1. Saia da conta
2. Tente abrir o app novamente
3. **Esperado:** Tela de login aparece (não o Dashboard)

#### C-2: Persistência de sessão
1. Faça login
2. Feche completamente o app (swipe up no recents)
3. Reabra o app
4. **Esperado:** Dashboard aparece diretamente (token persiste via SecureStore)

---

## 5. Coleta de Evidências

| Teste | Status | Observações |
|---|---|---|
| A-1: Login aparece | ⬜ | |
| A-2: Registro Ana | ⬜ | |
| A-3: Criar casal | ⬜ | |
| A-4: Sair da conta | ⬜ | |
| A-5: Registro Bruno | ⬜ | |
| A-6: Entrar no casal | ⬜ | |
| A-7: Senha errada | ⬜ | |
| A-8: Login correto | ⬜ | |
| B-1: Dashboard | ⬜ | |
| B-2: Metas | ⬜ | |
| B-3: Fluxo de Caixa | ⬜ | |
| B-4: Configurações | ⬜ | |
| B-5: Tab bar | ⬜ | |
| C-1: Guard auth | ⬜ | |
| C-2: Persistência | ⬜ | |

---

## 6. Dicas de debug

### Backend

```powershell
# Ver logs detalhados
dotnet run --launch-profile http --verbosity detailed

# Conectar ao banco para inspecionar dados
psql -U postgres -d couplesync

# Tabelas úteis
SELECT * FROM transactions ORDER BY created_at DESC LIMIT 10;
SELECT * FROM notification_events ORDER BY created_at DESC LIMIT 10;
SELECT * FROM device_tokens;
SELECT * FROM notification_settings;
SELECT * FROM goals;
```

### Mobile

```powershell
# Limpar cache do Metro quando houver erros estranhos
npx expo start --android --clear

# Ver logs do React Native
npx expo start --android  # os logs aparecem no terminal do Metro

# Inspecionar store do Zustand em runtime
# Adicione temporariamente ao _layout.tsx:
# console.log('session:', useSessionStore.getState())
```

### PostgreSQL não conecta
Verifique se o serviço está rodando:
```powershell
Get-Service -Name postgresql*
# ou no Linux/Mac:
# pg_lscluster
```

### Porta 5210 já em uso
```powershell
netstat -ano | findstr :5210
taskkill /PID <PID> /F
```

---

## 7. Próximos passos (após validação)

Quando todos os testes A-C estiverem **PASS**, retomamos o fluxo autônomo com:

- **T-023** — Expo Config Plugin + Kotlin `NotificationCaptureService` + parser de bancos
- **T-024** — Checkpoint do foundation mobile
- **T-UI-POLISH** — Refinamento UI/UX premium em todas as telas


# Mobile Alpha Manual Test Plan
- [ ] AC-001: Login and join-code onboarding pass on Android test device
- [ ] AC-002: Permission enable/disable flow is visible and recoverable
- [ ] AC-009: Offline capture and retry upload behavior is observable
- [ ] AC-010: Key setup actions are reachable in three taps
