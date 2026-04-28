# GitHub Actions MCP Server

Servidor MCP local que permite aos agentes do GitHub Copilot interagir com o GitHub Actions do repositório.

## Tools disponíveis

| Tool | Descrição |
|---|---|
| `list_workflows` | Lista todos os workflows do repositório |
| `list_workflow_runs` | Lista execuções recentes de um workflow (com filtros) |
| `get_workflow_run` | Detalhes de uma execução específica |
| `list_workflow_jobs` | Lista jobs e steps de uma execução |
| `get_job_logs` | Baixa logs de um job (para debug de falhas) |
| `trigger_workflow` | Dispara `workflow_dispatch` em um workflow |
| `cancel_workflow_run` | Cancela uma execução em andamento |
| `rerun_workflow` | Re-executa um workflow (todos os jobs ou apenas os que falharam) |
| `get_workflow_file` | Lê o conteúdo YAML de um workflow do repositório |
| `validate_workflow_file` | Valida sintaxe YAML e estrutura de um workflow |
| `get_workflow_usage` | Informações de uso/billing de um workflow |
| `list_run_artifacts` | Lista artefatos produzidos por uma execução |

## Setup

### 1. Criar o GitHub Personal Access Token (Fine-grained)

1. Acesse https://github.com/settings/tokens?type=beta
2. Clique **"Generate new token"**
3. Configure:
   - **Token name**: `CoupleSync MCP` (ou qualquer nome descritivo)
   - **Expiration**: escolha o período desejado (90 dias recomendado)
   - **Resource owner**: sua conta (`lucasmoraiss`)
   - **Repository access**: selecione **"Only select repositories"** → escolha `CoupleSync`
   - **Permissions** (em "Repository permissions"):
     - **Actions**: `Read and write` ← necessário para listar, disparar, cancelar e re-executar workflows
     - **Contents**: `Read-only` ← necessário para ler arquivos de workflow do repositório
     - **Metadata**: `Read-only` ← concedido automaticamente
4. Clique **"Generate token"**
5. **Copie o token** (começa com `github_pat_...`) — ele não será mostrado novamente

### 2. Build do servidor

```bash
cd mcp-github-actions
npm install
npm run build
```

### 3. Uso no VS Code

O servidor já está configurado em `.vscode/mcp.json`. Ao iniciar o Copilot Chat com o servidor ativo, o VS Code pedirá as 3 variáveis:

| Variável | Valor | Onde obter |
|---|---|---|
| `GITHUB_PAT` | `github_pat_...` | Token criado no passo 1 |
| `GITHUB_OWNER` | `lucasmoraiss` | Seu username do GitHub |
| `GITHUB_REPO` | `CoupleSync` | Nome do repositório |

### 4. Verificar funcionamento

No Copilot Chat, peça ao agente:
> "Liste os workflows do meu repositório usando o MCP de GitHub Actions"

Ele deve retornar os 3 workflows: **CI**, **Deploy** e **Mobile APK**.

## Permissões do token — resumo

| Permissão | Escopo | Por quê |
|---|---|---|
| **Actions** | Read and write | Listar runs, disparar workflows, cancelar, re-executar, ver logs e artefatos |
| **Contents** | Read-only | Ler arquivos `.github/workflows/*.yml` do repositório |
| **Metadata** | Read-only | Acesso básico ao repositório (concedido automaticamente) |

> **Segurança**: O token é do tipo Fine-grained (escopo mínimo), restrito apenas ao repositório `CoupleSync`, e é passado via `promptString` com `password: true` — nunca fica salvo em arquivos do projeto.
