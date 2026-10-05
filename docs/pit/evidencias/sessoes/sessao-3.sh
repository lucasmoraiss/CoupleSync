#!/usr/bin/env bash
# Sessao 3 de teste exploratorio — CoupleSync: orcamento, rendas, notificacoes/alertas.
# Reexecutavel do zero: cada execucao cria usuarios/casais novos (e-mails unicos, prefixo s3-).
# Uso: bash sessao-3.sh   (grava a saida completa em sessao-3.log, ao lado do script)
set -u
BASE="${BASE:-http://localhost:5000/api/v1}"
PG="${PG:-couplesync-pit-postgres-1}"
DIR="$(cd "$(dirname "$0")" && pwd)"
LOG="$DIR/sessao-3.log"
TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT
cd "$TMP"
exec 3>&1 >"$LOG" 2>&1

RUN="$(date -u +%Y%m%d%H%M%S)-$RANDOM"
PASS='Senha@12345'
CUR="$(date -u +%Y-%m)"
PREV="$(date -u -d "$(date -u +%Y-%m-01) -1 month" +%Y-%m)"
NEXT="$(date -u -d "$(date -u +%Y-%m-01) +1 month" +%Y-%m)"
ZERO=00000000-0000-0000-0000-000000000000

echo "SESSAO 3 — CoupleSync — orcamento, rendas, notificacoes"
echo "Inicio (UTC): $(date -u +%Y-%m-%dT%H:%M:%SZ)   execucao: $RUN"
echo "Base: $BASE   mes atual: $CUR   anterior: $PREV   seguinte: $NEXT"

N=0; STATUS=""; BODY=""
caso(){ N=$((N+1)); printf '\n### CASO 3.%02d — %s\n' "$N" "$1"; }
mask(){ sed -E 's/("(accessToken|refreshToken)":")[^"]*/\1<omitido>/g'; }
# req METODO CAMINHO QUEM [CORPO]   (QUEM = A|B|C|D|E|W|Z ou "-" = sem token)
req(){
  local m="$1" p="$2" who="$3" tok="" shown=""
  local args=(-s -X "$m" "$BASE$p" -o resp.txt -w '%{http_code}')
  if [ "$who" != "-" ]; then eval "tok=\${TOK_$who}"; args+=(-H "Authorization: Bearer $tok"); shown=" -H 'Authorization: Bearer <token de $who>'"; fi
  if [ $# -ge 4 ]; then
    printf '%s' "$4" > req.json
    args+=(-H 'Content-Type: application/json; charset=utf-8' --data-binary @req.json)
    local b="$4"; [ ${#b} -gt 400 ] && b="${b:0:120}...(${#4} caracteres no total)"
    shown="$shown -H 'Content-Type: application/json' -d '$b'"
  fi
  echo "\$ curl -X $m $BASE$p$shown"
  : > resp.txt
  STATUS="$(curl "${args[@]}")"
  BODY="$(cat resp.txt)"
  echo "HTTP $STATUS"
  if [ -n "$BODY" ]; then printf '%s\n' "$BODY" | mask; else echo "(corpo vazio)"; fi
}
jv(){ printf '%s' "$BODY" | grep -o "\"$1\":\"[^\"]*\"" | head -1 | cut -d'"' -f4; }
sql(){ echo "\$ psql (somente leitura): $1"; docker exec "$PG" psql -U couplesync -d couplesync -c "$1" 2>&1; }
tx(){ # tx QUEM VALOR CATEGORIA DESCRICAO
  req POST /transactions "$1" "{\"amount\":$2,\"currency\":\"BRL\",\"description\":\"$4 $RUN\",\"category\":\"$3\"}"
}
registrar(){ # registrar LETRA nome
  req POST /auth/register - "{\"email\":\"s3-$1-$RUN@teste.local\",\"name\":\"$2\",\"password\":\"$PASS\"}"
  eval "TOK_$1=\"\$(jv accessToken)\"; UID_$1=\"\$(jv id)\""
}

############################ PREPARACAO ############################
caso "Preparacao: registrar usuarios A, B, C, D e E (E fica sem casal)"
registrar A "Ana S3"; registrar B "Bruno S3"; registrar C "Carla S3"; registrar D "Davi S3"; registrar E "Edu S3"

caso "Preparacao: A cria o casal X"
req POST /couples A
COUPLE_X="$(jv coupleId)"; JOIN_X="$(jv joinCode)"; TOK_A="$(jv accessToken)"

caso "Preparacao: B entra no casal X com o codigo de convite"
req POST /couples/join B "{\"joinCode\":\"$JOIN_X\"}"
TOK_B="$(jv accessToken)"

caso "Preparacao: C cria o casal Y"
req POST /couples C
COUPLE_Y="$(jv coupleId)"; TOK_C="$(jv accessToken)"

caso "Preparacao: D (terceiro membro) entra no casal X com o mesmo codigo"
req POST /couples/join D "{\"joinCode\":\"$JOIN_X\"}"
TOK_D="$(jv accessToken)"
req GET /couples/me A
echo "casal X=$COUPLE_X  casal Y=$COUPLE_Y  A=$UID_A B=$UID_B C=$UID_C D=$UID_D E=$UID_E"

############################ ORCAMENTO ############################
caso "Orcamento: GET /budgets/current sem plano (casal X)"
req GET /budgets/current A

caso "Orcamento: GET /budgets/{mes atual} sem plano"
req GET "/budgets/$CUR" A

caso "Orcamento: PUT alocacoes em planId inexistente"
req PUT "/budgets/$ZERO/allocations" A '{"allocations":[{"category":"Alimentação","allocatedAmount":100,"currency":"BRL"}]}'

caso "Orcamento: renda rapida PATCH /budgets/income SEM plano existente (4000)"
req PATCH /budgets/income A '{"grossIncome":4000}'
req GET /budgets/current A

caso "Orcamento: criar/atualizar plano do mes atual (POST /budgets, renda 5000)"
req POST /budgets A "{\"month\":\"$CUR\",\"grossIncome\":5000,\"currency\":\"BRL\"}"
PLAN_X="$(jv id)"
echo "PLAN_X=$PLAN_X"

caso "Orcamento: renda rapida COM plano existente (5500, feita por B) e conferencia"
req PATCH /budgets/income B '{"grossIncome":5500,"currency":"BRL"}'
req GET /budgets/current A

caso "Orcamento: renda rapida com valor negativo"
req PATCH /budgets/income A '{"grossIncome":-100}'

caso "Orcamento: renda rapida com valor zero"
req PATCH /budgets/income A '{"grossIncome":0}'
req GET /budgets/current A

caso "Orcamento: renda rapida com valor gigante (1e20, 9999999999999999.99 e 1e40)"
req PATCH /budgets/income A '{"grossIncome":100000000000000000000}'
req PATCH /budgets/income A '{"grossIncome":9999999999999999.99}'
req PATCH /budgets/income A '{"grossIncome":1e40}'
req GET /budgets/current A

caso "Orcamento: renda rapida com corpo invalido (texto, ausente, moeda invalida, 3 casas decimais)"
req PATCH /budgets/income A '{"grossIncome":"abc"}'
req PATCH /budgets/income A '{}'
req PATCH /budgets/income A '{"grossIncome":5000,"currency":"XYZ"}'
req PATCH /budgets/income A '{"grossIncome":5000,"currency":"DOLAR"}'
req PATCH /budgets/income A '{"grossIncome":1234.567}'
req PATCH /budgets/income A '{"grossIncome":5000,"currency":"BRL"}'
req GET /budgets/current A

caso "Orcamento: POST /budgets com mes invalido (2026-13, 2026-00, abc, vazio, 2026-1)"
for m in 2026-13 2026-00 abc "" 2026-1; do
  req POST /budgets A "{\"month\":\"$m\",\"grossIncome\":1000,\"currency\":\"BRL\"}"
done

caso "Orcamento: POST /budgets com renda negativa, zero, moeda invalida, sem moeda, corpo vazio e JSON malformado"
req POST /budgets A "{\"month\":\"$PREV\",\"grossIncome\":-1,\"currency\":\"BRL\"}"
req POST /budgets A "{\"month\":\"$PREV\",\"grossIncome\":0,\"currency\":\"BRL\"}"
req POST /budgets A "{\"month\":\"$PREV\",\"grossIncome\":1000,\"currency\":\"XYZ\"}"
req POST /budgets A "{\"month\":\"$PREV\",\"grossIncome\":1000}"
req POST /budgets A '{}'
req POST /budgets A '{"month":'

caso "Orcamento: criar plano para mes passado ($PREV), mes futuro ($NEXT), 1900-01 e 9999-12"
req POST /budgets A "{\"month\":\"$PREV\",\"grossIncome\":3000,\"currency\":\"BRL\"}"
PLAN_PREV="$(jv id)"
req POST /budgets A "{\"month\":\"$NEXT\",\"grossIncome\":7000,\"currency\":\"BRL\"}"
req POST /budgets A '{"month":"1900-01","grossIncome":1,"currency":"BRL"}'
req POST /budgets A '{"month":"9999-12","grossIncome":1,"currency":"BRL"}'

caso "Orcamento: GET /budgets/{month} — mes atual, 2026-13, 2026-00, abc, mes passado, mes futuro, mes sem plano"
for m in "$CUR" 2026-13 2026-00 abc "$PREV" "$NEXT" 2020-01; do req GET "/budgets/$m" A; done

caso "Orcamento: definir alocacoes validas (Alimentação 100, Outros 200, Transporte 300)"
ALOC_OK='{"allocations":[{"category":"Alimentação","allocatedAmount":100,"currency":"BRL"},{"category":"Outros","allocatedAmount":200,"currency":"BRL"},{"category":"Transporte","allocatedAmount":300,"currency":"BRL"}]}'
req PUT "/budgets/$PLAN_X/allocations" A "$ALOC_OK"

caso "Orcamento: alocacoes cuja soma (6000) excede a renda (5000)"
req PUT "/budgets/$PLAN_X/allocations" A '{"allocations":[{"category":"Moradia","allocatedAmount":4000,"currency":"BRL"},{"category":"Lazer","allocatedAmount":2000,"currency":"BRL"}]}'
req GET /budgets/current A

caso "Orcamento: alocacao negativa"
req PUT "/budgets/$PLAN_X/allocations" A '{"allocations":[{"category":"Lazer","allocatedAmount":-50,"currency":"BRL"}]}'

caso "Orcamento: alocacao zero"
req PUT "/budgets/$PLAN_X/allocations" A '{"allocations":[{"category":"Lazer","allocatedAmount":0,"currency":"BRL"}]}'

caso "Orcamento: alocacao gigante (9999999999999999.99, 1e20 e 1e40)"
req PUT "/budgets/$PLAN_X/allocations" A '{"allocations":[{"category":"Lazer","allocatedAmount":9999999999999999.99,"currency":"BRL"}]}'
req PUT "/budgets/$PLAN_X/allocations" A '{"allocations":[{"category":"Lazer","allocatedAmount":100000000000000000000,"currency":"BRL"}]}'
req PUT "/budgets/$PLAN_X/allocations" A '{"allocations":[{"category":"Lazer","allocatedAmount":1e40,"currency":"BRL"}]}'
req GET /budgets/current A

caso "Orcamento: mesma categoria duas vezes na lista (identica e com caixa diferente)"
req PUT "/budgets/$PLAN_X/allocations" A '{"allocations":[{"category":"Lazer","allocatedAmount":100,"currency":"BRL"},{"category":"Lazer","allocatedAmount":200,"currency":"BRL"}]}'
req PUT "/budgets/$PLAN_X/allocations" A '{"allocations":[{"category":"Lazer","allocatedAmount":100,"currency":"BRL"},{"category":"LAZER","allocatedAmount":200,"currency":"BRL"}]}'
req GET /budgets/current A

caso "Orcamento: alocacoes — categoria vazia, so espacos, 65 caracteres, moeda invalida/diferente do plano/ausente"
req PUT "/budgets/$PLAN_X/allocations" A '{"allocations":[{"category":"","allocatedAmount":100,"currency":"BRL"}]}'
req PUT "/budgets/$PLAN_X/allocations" A '{"allocations":[{"category":"   ","allocatedAmount":100,"currency":"BRL"}]}'
C65="$(printf 'C%.0s' $(seq 1 65))"
req PUT "/budgets/$PLAN_X/allocations" A "{\"allocations\":[{\"category\":\"$C65\",\"allocatedAmount\":100,\"currency\":\"BRL\"}]}"
req PUT "/budgets/$PLAN_X/allocations" A '{"allocations":[{"category":"Lazer","allocatedAmount":100,"currency":"XYZ"}]}'
req PUT "/budgets/$PLAN_X/allocations" A '{"allocations":[{"category":"Lazer","allocatedAmount":100,"currency":"USD"}]}'
req PUT "/budgets/$PLAN_X/allocations" A '{"allocations":[{"category":"Lazer","allocatedAmount":100}]}'

caso "Orcamento: alocacoes — lista vazia, chave ausente, null, 21 itens, planId nao-GUID"
req PUT "/budgets/$PLAN_X/allocations" A '{"allocations":[]}'
req PUT "/budgets/$PLAN_X/allocations" A '{}'
req PUT "/budgets/$PLAN_X/allocations" A '{"allocations":null}'
L21=""; for i in $(seq 1 21); do L21="$L21{\"category\":\"Cat$i\",\"allocatedAmount\":1,\"currency\":\"BRL\"},"; done
req PUT "/budgets/$PLAN_X/allocations" A "{\"allocations\":[${L21%,}]}"
req PUT "/budgets/nao-e-guid/allocations" A "$ALOC_OK"

caso "Orcamento: restaurar alocacoes validas e conferir budgetGap (renda 5000 - 600 alocado)"
req PUT "/budgets/$PLAN_X/allocations" A "$ALOC_OK"
sql "SELECT category, allocated_amount, currency FROM budget_allocations WHERE budget_plan_id='$PLAN_X' ORDER BY category;"

############################ GASTO x ALOCACAO ############################
ver_orc(){ req GET /budgets/current A; }
eventos_x(){ sleep 2; sql "SELECT alert_type, title, body, status, user_id, created_at_utc FROM notification_events WHERE couple_id='$COUPLE_X' ORDER BY created_at_utc;"; }

caso "Gasto x alocacao: Alimentação=100, transacao de 50 (total 50 = 50%)"
tx A 50 "Alimentação" "mercado 1"; ver_orc; eventos_x

caso "Gasto x alocacao: +35 (total 85 = 85%, cruza 80%) — gasto no orcamento e alertas"
tx A 35 "Alimentação" "mercado 2"; ver_orc; eventos_x

caso "Gasto x alocacao: +35 (total 120 = 120%, cruza 100%) — gasto no orcamento e alertas"
tx B 35 "Alimentação" "mercado 3"; ver_orc; eventos_x

caso "Gasto x alocacao: transacao de 10 na categoria escrita 'ALIMENTACAO'"
tx A 10 "ALIMENTACAO" "variante 1"; ver_orc

caso "Gasto x alocacao: transacao de 10 na categoria escrita 'alimentação'"
tx A 10 "alimentação" "variante 2"; ver_orc

caso "Gasto x alocacao: transacao de 10 na categoria escrita 'Alimentacao'"
tx A 10 "Alimentacao" "variante 3"; ver_orc

caso "Gasto x alocacao: 'Outros' (20) x 'OUTROS' (30) com alocacao 'Outros'=200"
tx A 20 "Outros" "outros 1"; ver_orc
tx A 30 "OUTROS" "outros 2"; ver_orc
sql "SELECT category, count(*), sum(amount) FROM transactions WHERE couple_id='$COUPLE_X' GROUP BY category ORDER BY category;"

caso "Gasto x alocacao: transacao do terceiro membro D (40 em Transporte) entra no gasto do orcamento?"
tx D 40 "Transporte" "onibus D"; ver_orc

caso "Gasto x alocacao: transacao com data no mes passado nao deve contar no mes atual"
req POST /transactions A "{\"amount\":77,\"currency\":\"BRL\",\"description\":\"mes passado $RUN\",\"category\":\"Transporte\",\"eventTimestampUtc\":\"$PREV-15T12:00:00Z\"}"
ver_orc
req PUT "/budgets/$PLAN_PREV/allocations" A '{"allocations":[{"category":"Transporte","allocatedAmount":100,"currency":"BRL"}]}'
req GET "/budgets/$PREV" A

caso "Gasto x alocacao: alocacao gravada como 'ALIMENTACAO'/'OUTROS' — qual gasto aparece? (depois restaura)"
req PUT "/budgets/$PLAN_X/allocations" A '{"allocations":[{"category":"ALIMENTACAO","allocatedAmount":100,"currency":"BRL"},{"category":"OUTROS","allocatedAmount":200,"currency":"BRL"},{"category":"Transporte","allocatedAmount":300,"currency":"BRL"}]}'
ver_orc
req PUT "/budgets/$PLAN_X/allocations" A "$ALOC_OK"

caso "Gasto x alocacao: resposta do PUT de alocacoes x GET logo em seguida (actualSpent deveria ser igual)"
req PUT "/budgets/$PLAN_X/allocations" A "$ALOC_OK"
ver_orc

caso "Orcamento: renda rapida trocando a moeda do plano (USD) com alocacoes em BRL ja gravadas (depois restaura)"
req PATCH /budgets/income A '{"grossIncome":5000,"currency":"USD"}'
ver_orc
req PATCH /budgets/income A '{"grossIncome":5000,"currency":"BRL"}'

caso "Alertas: eventos do casal X apos cruzar 80% e 100% em Alimentação (A, B e D nunca abriram as configuracoes de notificacao)"
eventos_x
sql "SELECT count(*) AS linhas_settings_casal_x FROM notification_settings WHERE couple_id='$COUPLE_X';"

caso "Alertas: quem recebe? A abre as configuracoes (GET) e B lanca 210 em Transporte (40 -> 250 = 83% de 300)"
req GET /notifications/settings A
sql "SELECT user_id, low_balance_enabled, large_transaction_enabled, bill_reminder_enabled FROM notification_settings WHERE couple_id='$COUPLE_X';"
tx B 210 "Transporte" "transporte B"; ver_orc; eventos_x

caso "Alertas: quem recebe? B tambem abre as configuracoes e D lanca 60 em Transporte (250 -> 310 = 103% de 300)"
req GET /notifications/settings B
tx D 60 "Transporte" "transporte D"; ver_orc; eventos_x

caso "Alertas: quem recebe? A (configuracoes gravadas) lanca 600 em Outros (20 -> 620 de 200; transacao > 500) — para quais membros ha evento?"
tx A 600 "Outros" "outros grande A"; ver_orc; eventos_x
sql "SELECT e.alert_type, u.name AS destinatario, e.status FROM notification_events e JOIN users u ON u.id=e.user_id WHERE e.couple_id='$COUPLE_X' ORDER BY e.created_at_utc, e.alert_type;"

############################ ALERTAS DE ORCAMENTO (casal V, configuracoes ja gravadas) ############################
caso "Alertas orcamento: preparar casal V (usuario V sozinho), abrir configuracoes (GET) e alocar Lazer=100 e Alimentação=100"
registrar V "Vera S3"
req POST /couples V
COUPLE_V="$(jv coupleId)"; TOK_V="$(jv accessToken)"
sql "SELECT count(*) AS linhas_settings_antes_do_get FROM notification_settings WHERE couple_id='$COUPLE_V';"
req GET /notifications/settings V
sql "SELECT count(*) AS linhas_settings_depois_do_get FROM notification_settings WHERE couple_id='$COUPLE_V';"
req POST /budgets V "{\"month\":\"$CUR\",\"grossIncome\":5000,\"currency\":\"BRL\"}"
PLAN_V="$(jv id)"
req PUT "/budgets/$PLAN_V/allocations" V '{"allocations":[{"category":"Lazer","allocatedAmount":100,"currency":"BRL"},{"category":"Alimentação","allocatedAmount":100,"currency":"BRL"}]}'
eventos_v(){ sleep 2; sql "SELECT alert_type, title, body, status, created_at_utc FROM notification_events WHERE couple_id='$COUPLE_V' ORDER BY created_at_utc, alert_type;"; }

caso "Alertas orcamento: Lazer 50 de 100 (50%) — nenhum alerta esperado"
tx V 50 "Lazer" "lazer v1"; eventos_v

caso "Alertas orcamento: Lazer +35 (85%, cruza 80%) — esperado 1 alerta de aviso"
tx V 35 "Lazer" "lazer v2"; eventos_v

caso "Alertas orcamento: Lazer +5 (90%, continua entre 80% e 100%) — nao deveria repetir o aviso"
tx V 5 "Lazer" "lazer v3"; eventos_v

caso "Alertas orcamento: Lazer +30 (120%, cruza 100%) — esperado 1 alerta de estouro"
tx V 30 "Lazer" "lazer v4"; eventos_v

caso "Alertas orcamento: Lazer +10 (130%, ja estourado) — nao deveria repetir"
tx V 10 "Lazer" "lazer v5"; eventos_v

caso "Alertas orcamento: Alimentação (com acento) 0 -> 120 em uma unica transacao (pula 80% e 100%)"
tx V 120 "Alimentação" "alimentacao v1"; eventos_v
req GET /budgets/current V

############################ ALERTA DE TRANSACAO GRANDE (casal T) ############################
caso "Alertas transacao grande: casal T com configuracoes gravadas; transacoes de 100, 300, 499, 500, 501 e 600 (total 2500)"
registrar T "Tito S3"
req POST /couples T
COUPLE_T="$(jv coupleId)"; TOK_T="$(jv accessToken)"
req GET /notifications/settings T
for v in 100 300 499 500 501 600; do tx T "$v" "Lazer" "grande $v"; done
sleep 2
sql "SELECT alert_type, title, body, status FROM notification_events WHERE couple_id='$COUPLE_T' ORDER BY created_at_utc, alert_type;"

############################ ALERTA > R$ 3.000 EM 30 DIAS (casal W) ############################
caso "Alertas 3000: preparar casal W (usuario W sozinho), abrir configuracoes (GET), plano e alocacao Lazer=100000"
registrar W "Wal S3"
req POST /couples W
COUPLE_W="$(jv coupleId)"; TOK_W="$(jv accessToken)"
req GET /notifications/settings W
req POST /budgets W "{\"month\":\"$CUR\",\"grossIncome\":200000,\"currency\":\"BRL\"}"
PLAN_W="$(jv id)"
req PUT "/budgets/$PLAN_W/allocations" W '{"allocations":[{"category":"Lazer","allocatedAmount":100000,"currency":"BRL"}]}'
conta_w(){ sleep 2; sql "SELECT alert_type, title, body, status FROM notification_events WHERE couple_id='$COUPLE_W' ORDER BY created_at_utc, alert_type;"; sql "SELECT alert_type, count(*) AS qtd FROM notification_events WHERE couple_id='$COUPLE_W' GROUP BY 1 ORDER BY 1;"; }

caso "Alertas 3000: 10 transacoes de 290 (total 2900, abaixo de 3000)"
for i in 1 2 3 4 5 6 7 8 9 10; do tx W 290 "Lazer" "lazer $i"; done
conta_w

caso "Alertas 3000: +150 (total 3050, cruza 3000) — esperado 1 alerta"
tx W 150 "Lazer" "lazer cruza"; conta_w

caso "Alertas 3000: +10 (total 3060) — 1a transacao adicional (nao deveria gerar novo alerta)"
tx W 10 "Lazer" "adicional 1"; conta_w

caso "Alertas 3000: +10 (total 3070) — 2a transacao adicional"
tx W 10 "Lazer" "adicional 2"; conta_w

caso "Alertas 3000: +10 (total 3080) — 3a transacao adicional"
tx W 10 "Lazer" "adicional 3"; conta_w

caso "Alertas 3000: desligar as tres configuracoes de W e lancar +10 (total 3090) — nenhum alerta novo esperado"
req PUT /notifications/settings W '{"lowBalanceEnabled":false,"largeTransactionEnabled":false,"billReminderEnabled":false}'
tx W 10 "Lazer" "adicional 4 config desligada"; conta_w

caso "Alertas 3000: religar so largeTransaction e lancar 5000 — esperado so alerta de transacao grande"
req PUT /notifications/settings W '{"lowBalanceEnabled":false,"largeTransactionEnabled":true,"billReminderEnabled":false}'
tx W 5000 "Lazer" "grande"; conta_w

caso "Alertas 3000: situacao dos eventos de W alguns segundos depois (status de entrega)"
sleep 8
sql "SELECT alert_type, status, count(*) AS qtd, min(created_at_utc) AS primeiro, max(delivered_at_utc) AS entregue FROM notification_events WHERE couple_id='$COUPLE_W' GROUP BY 1,2 ORDER BY 1;"

############################ RENDAS ############################
caso "Rendas: GET /incomes (sem sufixo) e GET /incomes/current sem rendas"
req GET /incomes A
req GET /incomes/current A

caso "Rendas: A cria 'Salario A' 5000 (pessoal) no mes atual"
req POST /incomes A "{\"month\":\"$CUR\",\"name\":\"Salario A\",\"amount\":5000,\"currency\":\"BRL\",\"isShared\":false,\"isRecurring\":false}"
INC_A="$(jv id)"

caso "Rendas: B cria 'Salario B' 3000 (pessoal)"
req POST /incomes B "{\"month\":\"$CUR\",\"name\":\"Salario B\",\"amount\":3000,\"currency\":\"BRL\",\"isShared\":false,\"isRecurring\":false}"
INC_B="$(jv id)"

caso "Rendas: D (terceiro membro) cria 'Salario D' 2000 (pessoal)"
req POST /incomes D "{\"month\":\"$CUR\",\"name\":\"Salario D\",\"amount\":2000,\"currency\":\"BRL\",\"isShared\":false,\"isRecurring\":false}"
INC_D="$(jv id)"

caso "Rendas: A cria renda compartilhada 'Aluguel recebido' 1000"
req POST /incomes A "{\"month\":\"$CUR\",\"name\":\"Aluguel recebido\",\"amount\":1000,\"currency\":\"BRL\",\"isShared\":true,\"isRecurring\":false}"
INC_SH="$(jv id)"
sql "SELECT user_id, name, amount, is_shared, is_recurring, month FROM income_sources WHERE couple_id='$COUPLE_X' ORDER BY created_at_utc;"

caso "Rendas: o que A ve em GET /incomes/current (soma gravada no banco = 11000)"
req GET /incomes/current A
caso "Rendas: o que B ve em GET /incomes/current (soma gravada no banco = 11000)"
req GET /incomes/current B
caso "Rendas: o que D ve em GET /incomes/current (soma gravada no banco = 11000)"
req GET /incomes/current D

caso "Rendas: criar rendas altera a renda bruta (grossIncome) do plano de orcamento?"
req GET /budgets/current A

caso "Rendas: editar renda propria (nome e valor), so valor, e corpo {}"
req PUT "/incomes/$INC_A" A '{"name":"Salario A editado","amount":5200}'
req PUT "/incomes/$INC_A" A '{"amount":5000}'
req PUT "/incomes/$INC_A" A '{}'

caso "Rendas: PUT com nome vazio {\"name\":\"\"} e so espacos"
req PUT "/incomes/$INC_A" A '{"name":""}'
sql "SELECT id, '[' || name || ']' AS nome, amount FROM income_sources WHERE id='$INC_A';"
req PUT "/incomes/$INC_A" A '{"name":"   "}'
sql "SELECT id, '[' || name || ']' AS nome, amount FROM income_sources WHERE id='$INC_A';"
req PUT "/incomes/$INC_A" A '{"name":"Salario A"}'

caso "Rendas: PUT com valor negativo, zero, gigante, texto e nome de 65 caracteres"
req PUT "/incomes/$INC_A" A '{"amount":-1}'
req PUT "/incomes/$INC_A" A '{"amount":0}'
req PUT "/incomes/$INC_A" A '{"amount":9999999999999999.99}'
req PUT "/incomes/$INC_A" A '{"amount":100000000000000000000}'
req PUT "/incomes/$INC_A" A '{"amount":"mil"}'
N65="$(printf 'N%.0s' $(seq 1 65))"
req PUT "/incomes/$INC_A" A "{\"name\":\"$N65\"}"
req PUT "/incomes/$INC_A" A '{"name":"Salario A","amount":5000}'
sql "SELECT id, '[' || name || ']' AS nome, amount FROM income_sources WHERE id='$INC_A';"

caso "Rendas: B tenta editar e excluir a renda PESSOAL de A (mesmo casal)"
req PUT "/incomes/$INC_A" B '{"amount":1}'
req DELETE "/incomes/$INC_A" B

caso "Rendas: B e D editam a renda COMPARTILHADA criada por A"
req PUT "/incomes/$INC_SH" B '{"amount":1100}'
req PUT "/incomes/$INC_SH" D '{"amount":1000}'

caso "Rendas: POST com valor negativo, zero, gigante, 3 casas decimais"
req POST /incomes A "{\"month\":\"$CUR\",\"name\":\"Neg\",\"amount\":-10,\"currency\":\"BRL\",\"isShared\":false}"
req POST /incomes A "{\"month\":\"$CUR\",\"name\":\"Zero\",\"amount\":0,\"currency\":\"BRL\",\"isShared\":false}"
INC_ZERO="$(jv id)"
req POST /incomes A "{\"month\":\"$CUR\",\"name\":\"Gigante1\",\"amount\":9999999999999999.99,\"currency\":\"BRL\",\"isShared\":false}"
INC_G1="$(jv id)"
req POST /incomes A "{\"month\":\"$CUR\",\"name\":\"Gigante2\",\"amount\":100000000000000000000,\"currency\":\"BRL\",\"isShared\":false}"
req POST /incomes A "{\"month\":\"$CUR\",\"name\":\"Gigante3\",\"amount\":1e40,\"currency\":\"BRL\",\"isShared\":false}"
req POST /incomes A "{\"month\":\"$CUR\",\"name\":\"Decimais\",\"amount\":10.999,\"currency\":\"BRL\",\"isShared\":false}"
INC_DEC="$(jv id)"

caso "Rendas: POST com mes invalido (2026-13, 2026-00, abc, vazio, ausente)"
for m in 2026-13 2026-00 abc ""; do
  req POST /incomes A "{\"month\":\"$m\",\"name\":\"Mes invalido $m\",\"amount\":10,\"currency\":\"BRL\",\"isShared\":false}"
done
req POST /incomes A '{"name":"Sem mes","amount":10,"currency":"BRL","isShared":false}'

caso "Rendas: POST com nome vazio, ausente, 65 caracteres, duplicado; moeda invalida; corpo vazio"
req POST /incomes A "{\"month\":\"$CUR\",\"name\":\"\",\"amount\":10,\"currency\":\"BRL\",\"isShared\":false}"
req POST /incomes A "{\"month\":\"$CUR\",\"amount\":10,\"currency\":\"BRL\",\"isShared\":false}"
req POST /incomes A "{\"month\":\"$CUR\",\"name\":\"$N65\",\"amount\":10,\"currency\":\"BRL\",\"isShared\":false}"
req POST /incomes A "{\"month\":\"$CUR\",\"name\":\"Salario A\",\"amount\":10,\"currency\":\"BRL\",\"isShared\":false}"
req POST /incomes A "{\"month\":\"$CUR\",\"name\":\"salario a\",\"amount\":10,\"currency\":\"BRL\",\"isShared\":false}"
INC_DUPCASE="$(jv id)"
req POST /incomes A "{\"month\":\"$CUR\",\"name\":\"Moeda XYZ\",\"amount\":10,\"currency\":\"XYZ\",\"isShared\":false}"
INC_XYZ="$(jv id)"
req POST /incomes A "{\"month\":\"$CUR\",\"name\":\"Moeda USD\",\"amount\":10,\"currency\":\"USD\",\"isShared\":false}"
INC_USD="$(jv id)"
req POST /incomes A "{\"month\":\"$CUR\",\"name\":\"Sem moeda\",\"amount\":10,\"isShared\":false}"
req POST /incomes A '{}'

caso "Rendas: total do casal depois das tentativas invalidas (o que ficou gravado?)"
req GET /incomes/current A
sql "SELECT name, amount, currency, is_shared, month FROM income_sources WHERE couple_id='$COUPLE_X' ORDER BY created_at_utc;"

caso "Rendas: excluir rendas extras criadas pelas tentativas; excluir duas vezes; id inexistente; id nao-GUID"
for id in "$INC_ZERO" "$INC_G1" "$INC_DEC" "$INC_DUPCASE" "$INC_XYZ" "$INC_USD"; do
  if [ -n "$id" ]; then req DELETE "/incomes/$id" A; fi
done
if [ -n "$INC_DEC" ]; then req DELETE "/incomes/$INC_DEC" A; fi
req DELETE "/incomes/$ZERO" A
req DELETE "/incomes/nao-e-guid" A
sql "SELECT name, amount, currency, is_shared, month FROM income_sources WHERE couple_id='$COUPLE_X' ORDER BY created_at_utc;"

caso "Rendas: GET /incomes/{month} — mes atual, 2026-13, 2026-00, abc, passado, futuro"
for m in "$CUR" 2026-13 2026-00 abc "$PREV" "$NEXT"; do req GET "/incomes/$m" A; done

caso "Rendas: renda recorrente criada no mes atual aparece no mes seguinte?"
req POST /incomes A "{\"month\":\"$CUR\",\"name\":\"Bonus recorrente\",\"amount\":400,\"currency\":\"BRL\",\"isShared\":false,\"isRecurring\":true}"
INC_REC="$(jv id)"
req GET "/incomes/$NEXT" A
req GET "/incomes/$CUR" A

caso "Rendas: renda recorrente criada no mes passado aparece no mes atual?"
req POST /incomes A "{\"month\":\"$PREV\",\"name\":\"Freela recorrente\",\"amount\":250,\"currency\":\"BRL\",\"isShared\":false,\"isRecurring\":true}"
req GET "/incomes/$PREV" A
req GET /incomes/current A
sql "SELECT name, amount, month, is_recurring FROM income_sources WHERE couple_id='$COUPLE_X' AND is_recurring ORDER BY month;"

caso "Rendas: excluir renda propria e conferir listagem"
req DELETE "/incomes/$INC_REC" A
req GET /incomes/current A

############################ NOTIFICACOES ############################
caso "Notificacoes: GET /notifications/settings (padrao) para A e B"
req GET /notifications/settings A
req GET /notifications/settings B

caso "Notificacoes: PUT settings validos (tudo false) e conferir; B nao deve mudar"
req PUT /notifications/settings A '{"lowBalanceEnabled":false,"largeTransactionEnabled":false,"billReminderEnabled":false}'
req GET /notifications/settings A
req GET /notifications/settings B

caso "Notificacoes: PUT parcial (so um campo) e corpo {}"
req PUT /notifications/settings A '{"lowBalanceEnabled":true}'
req GET /notifications/settings A
req PUT /notifications/settings A '{}'
req GET /notifications/settings A

caso "Notificacoes: PUT settings com valores invalidos (texto, numero, null, campo desconhecido, JSON malformado, sem corpo, lista)"
req PUT /notifications/settings A '{"lowBalanceEnabled":"sim"}'
req PUT /notifications/settings A '{"lowBalanceEnabled":1}'
req PUT /notifications/settings A '{"lowBalanceEnabled":null,"largeTransactionEnabled":null,"billReminderEnabled":null}'
req PUT /notifications/settings A '{"campoInexistente":true}'
req PUT /notifications/settings A '{"lowBalanceEnabled":'
req PUT /notifications/settings A
req PUT /notifications/settings A '[]'
req GET /notifications/settings A

caso "Notificacoes: registrar token de dispositivo valido (POST /devices/token) e rota /notifications/devices/token"
TOKEN_OK="fcm-s3-$RUN-aaaaaaaaaaaaaaaaaaaaaaaaaaaa"
req POST /devices/token A "{\"token\":\"$TOKEN_OK\",\"platform\":\"android\"}"
req POST /notifications/devices/token A "{\"token\":\"$TOKEN_OK\",\"platform\":\"android\"}"
sql "SELECT user_id, left(token,30) AS token_ini, length(token) AS tam, platform FROM device_tokens WHERE couple_id='$COUPLE_X';"

caso "Notificacoes: token vazio, so espacos, ausente e null"
req POST /devices/token A '{"token":"","platform":"android"}'
req POST /devices/token A '{"token":"   ","platform":"android"}'
req POST /devices/token A '{"platform":"android"}'
req POST /devices/token A '{"token":null,"platform":"android"}'

caso "Notificacoes: token com 5000 caracteres, com 513 e com 512"
T5000="$(head -c 5000 /dev/zero | tr '\0' 'x')"
T513="$(head -c 513 /dev/zero | tr '\0' 'y')"
T512="$(head -c 512 /dev/zero | tr '\0' 'z')"
req POST /devices/token A "{\"token\":\"$T5000\",\"platform\":\"android\"}"
req POST /devices/token A "{\"token\":\"$T513\",\"platform\":\"android\"}"
req POST /devices/token A "{\"token\":\"$T512\",\"platform\":\"android\"}"
sql "SELECT user_id, left(token,30) AS token_ini, length(token) AS tam, platform FROM device_tokens WHERE couple_id='$COUPLE_X';"

caso "Notificacoes: token duplicado — mesmo usuario duas vezes e o mesmo token por B (mesmo casal) e por C (outro casal)"
req POST /devices/token A "{\"token\":\"$TOKEN_OK\",\"platform\":\"android\"}"
req POST /devices/token A "{\"token\":\"$TOKEN_OK\",\"platform\":\"android\"}"
req POST /devices/token B "{\"token\":\"$TOKEN_OK\",\"platform\":\"android\"}"
req POST /devices/token C "{\"token\":\"$TOKEN_OK\",\"platform\":\"android\"}"
sql "SELECT user_id, couple_id, left(token,30) AS token_ini, length(token) AS tam, platform FROM device_tokens WHERE token='$TOKEN_OK';"

caso "Notificacoes: plataforma invalida (ios, vazio, ausente) e ANDROID maiusculo"
req POST /devices/token A "{\"token\":\"$TOKEN_OK\",\"platform\":\"ios\"}"
req POST /devices/token A "{\"token\":\"$TOKEN_OK\",\"platform\":\"\"}"
req POST /devices/token A "{\"token\":\"$TOKEN_OK\"}"
req POST /devices/token A "{\"token\":\"$TOKEN_OK\",\"platform\":\"ANDROID\"}"
sql "SELECT user_id, left(token,30) AS token_ini, platform FROM device_tokens WHERE couple_id='$COUPLE_X';"

caso "Notificacoes: existe rota para listar notificacoes/alertas ou remover token? (GET /notifications, /notifications/events, /alerts, DELETE /devices/token)"
req GET /notifications A
req GET /notifications/events A
req GET /alerts A
req DELETE /devices/token A

############################ ISOLAMENTO ############################
caso "Isolamento: C (casal Y) le orcamento — /budgets/current, /budgets/{mes} e /budgets/{planId de X}"
req GET /budgets/current C
req GET "/budgets/$CUR" C
req GET "/budgets/$PLAN_X" C

caso "Isolamento: C tenta substituir as alocacoes do plano de X pelo planId"
req PUT "/budgets/$PLAN_X/allocations" C '{"allocations":[{"category":"INVASAO","allocatedAmount":1,"currency":"BRL"}]}'
req GET /budgets/current A

caso "Isolamento: C usa renda rapida — nao pode alterar a renda do plano de X"
req PATCH /budgets/income C '{"grossIncome":1}'
req GET /budgets/current A

caso "Isolamento: C le rendas — nao deve ver rendas de X"
req GET /incomes/current C
req GET "/incomes/$CUR" C

caso "Isolamento: C tenta editar e excluir rendas de X pelos ids (pessoal de A, de B e compartilhada)"
req PUT "/incomes/$INC_A" C '{"amount":1,"name":"invadido"}'
req PUT "/incomes/$INC_SH" C '{"amount":1}'
req DELETE "/incomes/$INC_B" C
req DELETE "/incomes/$INC_SH" C
sql "SELECT name, amount, is_shared FROM income_sources WHERE id IN ('$INC_A','$INC_B','$INC_SH') ORDER BY name;"

caso "Isolamento: configuracoes de notificacao de C nao refletem as de A"
req GET /notifications/settings C

caso "Isolamento: sem token (esperado 401) em orcamento, rendas e notificacoes"
req GET /budgets/current -
req GET /incomes/current -
req GET /notifications/settings -
req POST /devices/token - '{"token":"x","platform":"android"}'

caso "Isolamento: usuario E sem casal (esperado 403) em orcamento, rendas, notificacoes e token"
req GET /budgets/current E
req POST /budgets E "{\"month\":\"$CUR\",\"grossIncome\":1000,\"currency\":\"BRL\"}"
req GET /incomes/current E
req GET /notifications/settings E
req POST /devices/token E '{"token":"tok-e","platform":"android"}'

caso "Isolamento: token JWT adulterado"
TOK_Z="${TOK_A}x"
req GET /budgets/current Z

############################ FORMATO DE ERRO ############################
caso "Formato de erro: rota inexistente, metodo nao permitido, Content-Type errado, JSON malformado, transacao invalida"
req GET /budgets A
req DELETE "/budgets/$PLAN_X" A
req GET /rota-que-nao-existe A
echo "\$ curl -X POST $BASE/incomes -H 'Authorization: Bearer <token de A>' -H 'Content-Type: text/plain' -d 'texto'"
curl -s -X POST "$BASE/incomes" -H "Authorization: Bearer $TOK_A" -H 'Content-Type: text/plain' -d 'texto' -w '\nHTTP %{http_code}\n'
req POST /incomes A '{"month":"2026-10","name":"x","amount":}'
req POST /transactions A '{"amount":10,"category":""}'
req POST /transactions A '{"amount":-10,"category":"Outros"}'

############################ ESTADO FINAL ############################
caso "Estado final: orcamento de X, eventos de notificacao de X, W e Y, configuracoes"
req GET /budgets/current A
sql "SELECT alert_type, title, body, status, user_id, created_at_utc FROM notification_events WHERE couple_id='$COUPLE_X' ORDER BY created_at_utc;"
sql "SELECT alert_type, status, count(*) FROM notification_events WHERE couple_id IN ('$COUPLE_X','$COUPLE_W','$COUPLE_Y','$COUPLE_V','$COUPLE_T') GROUP BY 1,2 ORDER BY 1;"
sql "SELECT user_id, low_balance_enabled, large_transaction_enabled, bill_reminder_enabled FROM notification_settings WHERE couple_id IN ('$COUPLE_X','$COUPLE_W','$COUPLE_Y','$COUPLE_V','$COUPLE_T');"

echo
echo "TOTAL DE CASOS: $N"
echo "Fim (UTC): $(date -u +%Y-%m-%dT%H:%M:%SZ)"
echo "sessao-3: $N casos; log em $LOG" >&3
