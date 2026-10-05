# Guia de geração do APK — CoupleSync

Como produzir um APK Android instalável do app. Há dois caminhos: **EAS Build** (na nuvem da Expo) e **build local com Gradle**.

Este guia usa marcadores no lugar de identificadores reais. A URL da API é `https://<seu-servico>.onrender.com`.

---

## Pré-requisitos

| Ferramenta | Observação |
|---|---|
| Node.js 20 | A versão usada no CI. |
| EAS CLI | `npm install -g eas-cli` |
| Conta na Expo | Necessária para o EAS Build. |
| Android Studio (SDK e JDK) | Só para o build local. |

---

## A URL da API vai dentro do APK

O app lê `process.env.EXPO_PUBLIC_API_BASE_URL` (em `mobile/src/services/apiClient.ts`). O valor é fixado no pacote JavaScript no momento do build e deve ser a **raiz** da API, sem `/api/v1`:

```
EXPO_PUBLIC_API_BASE_URL=https://<seu-servico>.onrender.com
```

- No **EAS Build**, o valor vem do bloco `env` do perfil em `mobile/eas.json`.
- No **build local** e em desenvolvimento, vem de `mobile/.env`.
- Sem a variável, o app usa `http://10.0.2.2:5000` (o `localhost` da máquina visto pelo emulador), o que não funciona em um aparelho real.

Para exibir a aba do assistente, defina também `EXPO_PUBLIC_AI_CHAT_ENABLED=true`.

---

## Caminho 1 — EAS Build

### 1. Entrar na Expo

```bash
eas login
```

### 2. Conferir `mobile/eas.json`

O arquivo já existe no repositório, com três perfis:

| Perfil | O que gera |
|---|---|
| `development` | Build com cliente de desenvolvimento, distribuição interna. |
| `preview` | APK, canal `preview`, distribuição interna. |
| `production` | APK, canal `production`, com incremento automático de versão. |

Os perfis `preview` e `production` definem `EXPO_PUBLIC_API_BASE_URL`. **Confira esse valor antes de cada build** e ajuste para a URL da API em uso:

```json
"env": {
  "EXPO_PUBLIC_API_BASE_URL": "https://<seu-servico>.onrender.com"
}
```

### 3. Conferir `mobile/app.json`

Já configurado com o pacote Android `com.couplesync.app`, os plugins (`expo-router`, `./plugins/withNotificationListener`, `expo-notifications`, `expo-asset`, `expo-font`) e o projeto EAS em `extra.eas.projectId`. Para usar a sua própria conta Expo, rode `eas project:init` dentro de `mobile/`; o comando grava o seu `<id-do-projeto-expo>` no `app.json` (o endereço de atualizações em `updates.url` deve apontar para o mesmo projeto).

A versão do código (`versionCode`) é controlada pelo EAS (`"appVersionSource": "remote"` em `eas.json`).

### 4. Gerar o APK

```bash
cd mobile
eas build --platform android --profile preview --non-interactive
```

O EAS empacota o JavaScript, executa o `expo prebuild` (que aplica o plugin de captura de notificações e copia o código Kotlin de `android-native/`), compila e assina o APK com uma keystore gerenciada pela Expo.

### 5. Baixar e instalar

Ao terminar, o comando imprime o link do APK. Também dá para consultar:

```bash
eas build:list --platform android --limit 1
```

No aparelho, permita a instalação de apps de fontes desconhecidas para o aplicativo usado para abrir o arquivo e instale o `.apk`.

---

## Caminho 2 — Build local com Gradle

Para quando o EAS não estiver disponível.

```bash
cd mobile
npx expo prebuild --platform android --clean   # gera mobile/android/
cd android
./gradlew assembleDebug
# saída: mobile/android/app/build/outputs/apk/debug/app-debug.apk
```

Instale por ADB:

```bash
adb install mobile/android/app/build/outputs/apk/debug/app-debug.apk
```

Um APK de release (`assembleRelease`) exige uma keystore de assinatura própria, que o repositório não traz.

A pasta `mobile/android/` é gerada e não é versionada; o código nativo próprio fica em `mobile/android-native/`.

---

## GitHub Actions

O repositório tem dois workflows para o app:

| Workflow | Arquivo | O que faz |
|---|---|---|
| APK | `.github/workflows/mobile-apk.yml` | Roda o EAS Build com o perfil `production` e publica o APK como artefato do workflow. Pode ser disparado manualmente ou por tag `v*`. |
| Atualização OTA | `.github/workflows/mobile-update.yml` | Publica uma atualização JavaScript pelo EAS Update no branch `production`. Pode ser disparado manualmente. |

Segredos do repositório usados por eles:

| Segredo | Valor |
|---|---|
| `EXPO_TOKEN` | Token de acesso da conta Expo. |
| `EXPO_PUBLIC_API_BASE_URL` | URL da API, usada pela atualização OTA. |

Uma atualização OTA troca só o JavaScript. Mudanças em código nativo, plugins ou permissões exigem um APK novo.

---

## Distribuição

O APK é distribuído diretamente a quem vai testar (link do EAS ou arquivo). O projeto não usa hoje o Firebase App Distribution nem a Play Store.

---

## Push e Firebase

O `app.json` versionado não referencia um `google-services.json`, e esse arquivo é ignorado pelo Git. Sem a configuração do Firebase no build, o app funciona, mas o registro do token de push falha em silêncio e os alertas não chegam ao aparelho. Para habilitar o push, é preciso configurar o projeto Firebase no build do app e as variáveis `Fcm__ProjectId` e `Fcm__CredentialJson` na API.

---

## Problemas comuns

| Sintoma | Causa provável | O que fazer |
|---|---|---|
| `eas build` reclama de credenciais | Keystore ainda não criada | Rode `eas credentials` e deixe a Expo gerenciar a keystore. |
| O app instala, mas não entra nem cadastra | URL da API errada no build | Confira `EXPO_PUBLIC_API_BASE_URL` no perfil do `eas.json` e gere outro APK. |
| A primeira requisição demora ou falha | API suspensa no plano gratuito do Render | Tente de novo após cerca de um minuto. |
| O Android bloqueia a instalação | Fontes desconhecidas não permitidas | Autorize a instalação para o app que abriu o arquivo. |
| A captura de notificações não aparece | Rodando no Expo Go | Use um build gerado por EAS ou por `expo prebuild`. |
