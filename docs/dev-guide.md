# CoupleSync — Guia de desenvolvimento e roteiro de teste manual

Como subir o ambiente local do zero e conferir, pelo app, os fluxos principais.

---

## 1. Pré-requisitos

| Ferramenta | Versão | Conferir |
|---|---|---|
| .NET SDK | 8.0 | `dotnet --version` |
| PostgreSQL | 16 (a versão usada no CI) | `psql --version` |
| Node.js | 20 | `node --version` |
| Android Studio + emulador | — | AVD Manager |
| Git | — | `git --version` |

O Expo é usado via `npx`; não é preciso instalar nada global.

---

## 2. Back-end

### 2.1 Banco de dados

Crie um banco vazio:

```powershell
psql -U postgres -c "CREATE DATABASE couplesync;"
```

Não é preciso aplicar migrações à mão: a API aplica as pendentes ao iniciar (hoje são 13) e carrega as regras de categorização.

### 2.2 Configuração

O `backend/src/CoupleSync.Api/appsettings.json` versionado traz a chave JWT e a conexão **vazias**. A API não sobe sem as duas. Defina por variável de ambiente:

```powershell
# PowerShell 7 — gera uma chave de 64 caracteres
$env:JWT__SECRET = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$env:DATABASE_URL = "Host=localhost;Port=5432;Database=couplesync;Username=postgres;Password=<sua-senha>"
```

Ou crie `backend/src/CoupleSync.Api/appsettings.Development.json` (ignorado pelo Git):

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Port=5432;Database=couplesync;Username=postgres;Password=<sua-senha>"
  },
  "Jwt": {
    "Secret": "<chave-de-32-ou-mais-caracteres>"
  }
}
```

A chave precisa ter pelo menos 32 caracteres. Push (`Fcm:*`) e IA (`AI_CHAT_ENABLED`, `GEMINI_API_KEY`) podem ficar sem configurar em desenvolvimento: o envio de push é pulado com um aviso no log e o assistente responde como desabilitado.

A lista completa de variáveis está em [backend/README.md](../backend/README.md#variáveis-de-ambiente).

### 2.3 Iniciar a API

```powershell
dotnet run --project backend/src/CoupleSync.Api --launch-profile http
```

A API sobe em **http://localhost:5000** (escutando em todas as interfaces, o que permite o acesso do emulador e de aparelhos na mesma rede).

### 2.4 Conferir

```
GET http://localhost:5000/health/live   → 200
GET http://localhost:5000/health/ready  → 200 (banco respondendo)
```

### 2.5 Testes

```powershell
$env:DATABASE_URL = "Host=localhost;Port=5432;Database=couplesync_test;Username=postgres;Password=postgres"
dotnet test backend/CoupleSync.sln
```

São 588 testes (411 de unidade, 176 de integração, 1 de ponta a ponta).

### 2.6 Alternativa: Docker Compose

Na raiz, copie `.env.example` para `.env`, defina `JWT__SECRET` e rode `docker compose up --build`. A API fica em `http://localhost:5000` e o PostgreSQL em `localhost:5432`.

---

## 3. App

### 3.1 Instalar

```powershell
cd mobile
npm install
```

### 3.2 Apontar para a API

Crie `mobile/.env`:

```
EXPO_PUBLIC_API_BASE_URL=http://10.0.2.2:5000
```

`10.0.2.2` é o endereço pelo qual o emulador Android alcança o `localhost` da máquina. Em aparelho físico na mesma rede Wi-Fi, use o IP da máquina: `http://192.168.x.x:5000`. O valor é a raiz da API, sem `/api/v1`.

### 3.3 Iniciar

```powershell
npx expo start --android
```

A captura de notificações bancárias usa código nativo e não funciona no Expo Go. Para testá-la:

```powershell
npx expo prebuild --platform android --clean
npx expo run:android
```

### 3.4 Testes e checagem de tipos

```powershell
npm test            # Jest: 102 testes de lógica pura
npx tsc --noEmit    # checagem de tipos, a mesma do CI
```

`npm run lint` não funciona: não há configuração do ESLint no projeto.

### 3.5 Telas

```
(auth)/login.tsx               Login
(auth)/register.tsx            Cadastro
(auth)/couple-setup.tsx        Criar casal ou entrar com código
(main)/index.tsx               Dashboard
(main)/transactions/index.tsx  Transações
(main)/transactions/new.tsx    Nova transação
(main)/ocr-upload.tsx          Importar extrato em PDF
(main)/ocr-review.tsx          Revisão da importação
(main)/goals/index.tsx         Metas
(main)/cashflow/index.tsx      Fluxo de caixa
(main)/budget/index.tsx        Fontes de renda (aba "Rendas")
(main)/reports/index.tsx       Relatórios
(main)/chat/index.tsx          Chat IA (aba visível só com EXPO_PUBLIC_AI_CHAT_ENABLED=true)
(main)/settings/index.tsx      Configurações
(main)/settings/alerts.tsx     Alertas
```

Sem sessão, qualquer rota de `(main)` redireciona para o login.

---

## 4. Roteiro de teste manual

Use contas fictícias (os endereços `@example.com` abaixo não existem) e uma senha de 8 caracteres ou mais. Anote PASSOU ou FALHOU em cada item.

### Bloco A — Cadastro, casal e login

**A-1. Tela de login.** Abra o app sem sessão. Esperado: campos "E-mail" e "Senha", botão "Entrar" e o link "Não tem conta? Cadastre-se".

**A-2. Cadastro da primeira pessoa.** Toque em "Cadastre-se", preencha Nome `Ana`, E-mail `ana@example.com`, Senha e Confirmar senha, e toque em "Criar conta". Esperado: tela "Vamos configurar", com "Criar casal" e "Entrar em um casal".

**A-3. Criar casal.** Toque em "Criar casal". Esperado: tela "Casal criado!" com um código de convite de 6 caracteres e o botão "Copiar código". Anote o código (ele não é exibido de novo) e toque em "Ir para o Dashboard".

**A-4. Sair.** Aba "Config" > "Sair da conta" > confirme. Esperado: volta ao login.

**A-5. Cadastro da segunda pessoa.** Repita A-2 com `Bruno` e `bruno@example.com`.

**A-6. Entrar no casal.** Toque em "Entrar em um casal", digite o código de A-3 e toque em "Entrar". Esperado: vai para o Dashboard.

**A-7. Senha errada.** Saia, tente entrar com `ana@example.com` e uma senha incorreta. Esperado: alerta "E-mail ou senha incorretos.", sem navegação.

**A-8. Login correto.** Entre com a senha certa. Esperado: vai direto ao Dashboard, sem passar pela tela do casal.

**A-9. Limite de tentativas.** Erre a senha 6 vezes em menos de um minuto. Esperado: a sexta tentativa é recusada pela API com HTTP 429.

### Bloco B — Abas

**B-1. Abas visíveis.** Esperado: Dashboard, Transações, Metas, Fluxo, Rendas, Relatórios e Config. "Chat IA" só aparece com a variável de build ligada.

**B-2. Dashboard.** Esperado: mês corrente, cartão "Total de gastos" e os atalhos "Ver rendas" e "Ver todas as transações".

**B-3. Nova transação.** Em Transações, toque em "Nova", informe valor e categoria e toque em "Salvar". Esperado: a transação aparece na lista com o selo "Manual" e o total do Dashboard muda.

**B-4. Trocar categoria e excluir.** Toque na transação e escolha outra categoria; depois toque na lixeira e confirme. Esperado: a categoria muda; após excluir, aparece "Transação excluída".

**B-5. Metas.** Sem metas, a tela mostra "Nenhuma meta ativa". Crie uma meta e confira a barra de progresso.

**B-6. Fluxo.** Alterne entre "30 dias" e "90 dias".

**B-7. Rendas.** Toque em "Adicionar fonte de renda", salve e confira "Renda Total do Casal".

**B-8. Relatórios.** Alterne entre 3m, 6m e 12m.

**B-9. Config.** Esperado: "Notificações do sistema", "Alertas", "Código do casal" (inativo) e "Sair da conta".

### Bloco C — Sessão

**C-1. Proteção de rota.** Saia da conta e reabra o app. Esperado: tela de login.

**C-2. Sessão persistente.** Entre, feche o app por completo e reabra. Esperado: Dashboard direto.

**C-3. Renovação de sessão.** Com `JWT__ACCESSTOKENTTLMINUTES=1` na API, entre, aguarde dois minutos e navegue entre as abas. Esperado: os dados carregam sem pedir login de novo.

### Bloco D — Extrato e notificações (build nativo)

**D-1. Importar PDF.** Em Transações, toque no ícone de nuvem, escolha "Arquivo PDF" e selecione um extrato. Esperado: tela "Revisão de Importação"; após "Confirmar Importação", as transações aparecem com o selo "OCR".

**D-2. Permissão de notificações.** Sem a permissão, Transações mostra a faixa "Captura de notificações desativada". Toque em "Ativar" e conceda o acesso ao CoupleSync na tela do Android. Esperado: depois de reiniciar o app, a faixa não aparece mais (a permissão é conferida quando a tela é montada).

O roteiro em inglês `mobile/tests/e2e/manual-walkthrough.md` cobre cenários adicionais.

---

## 5. Depuração

### Back-end

```powershell
# inspecionar o banco
psql -U postgres -d couplesync
```

```sql
\dt
SELECT * FROM transactions LIMIT 10;
SELECT * FROM import_jobs LIMIT 10;
SELECT * FROM notification_events LIMIT 10;
SELECT * FROM device_tokens;
SELECT * FROM goals;
```

### App

```powershell
# limpar o cache do Metro
npx expo start --android --clear
```

Os logs do app aparecem no terminal do Metro. Em desenvolvimento, o cliente HTTP imprime a URL da API em uso (`[apiClient] BASE_URL = ...`).

### PostgreSQL não conecta

```powershell
Get-Service -Name postgresql*
```

### Porta 5000 ocupada

```powershell
netstat -ano | findstr :5000
taskkill /PID <PID> /F
```

---

## 6. Onde continuar

- Pendências e melhorias: [docs/BACKLOG.md](BACKLOG.md)
- Implantação: [docs/deployment/DEPLOY-GUIDE.md](deployment/DEPLOY-GUIDE.md)
- Decisões de arquitetura: [docs/adr/](adr/README.md)
