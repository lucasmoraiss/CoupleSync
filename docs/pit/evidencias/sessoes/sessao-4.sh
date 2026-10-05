#!/usr/bin/env bash
# Sessao 4 de teste exploratorio (caixa-preta) - CoupleSync
# Escopo: metas (/goals), fluxo de caixa (/cashflow), relatorios (/reports), dashboard (/dashboard)
# Uso:  bash sessao-4.sh > sessao-4.log 2>&1
# Reexecutavel do zero: cada execucao cria usuarios com e-mails unicos (prefixo s4-).
# Requisitos: bash, curl, sed, GNU date. Nao usa jq.

BASE="${BASE:-http://localhost:5000/api/v1}"
RUN="$(date -u +%Y%m%d%H%M%S)-$RANDOM"
PASS='Senha@12345'
N=0
declare -A TOK
BODY=""; STATUS=""

# ---------- datas (dinamicas, em UTC) ----------
NOW_ISO=$(date -u +%Y-%m-%dT%H:%M:%SZ)
TODAY=$(date -u +%Y-%m-%d)
CM=$(date -u +%Y-%m)                                   # mes corrente
PM=$(date -u -d "$CM-01 -1 month" +%Y-%m)              # mes anterior
PM2=$(date -u -d "$CM-01 -2 month" +%Y-%m)             # dois meses atras
FUT=$(date -u -d "+1 year" +%Y-%m-%dT00:00:00Z)
FUT2=$(date -u -d "+2 year" +%Y-%m-%dT00:00:00Z)
PAST=$(date -u -d "-30 day" +%Y-%m-%dT00:00:00Z)
TS_A=$(date -u -d "-30 minutes" +%Y-%m-%dT%H:%M:%SZ)
TS_B=$(date -u -d "-20 minutes" +%Y-%m-%dT%H:%M:%SZ)
TS_C=$(date -u -d "-10 minutes" +%Y-%m-%dT%H:%M:%SZ)
# ultimo mes de 31 dias ja encerrado: 23:30 do dia 31 em Brasilia (UTC-3) = 02:30Z do dia 1 seguinte
for i in 1 2 3 4 5 6; do
  M31=$(date -u -d "$CM-01 -$i month" +%Y-%m)
  D=$(date -u -d "$M31-01 +1 month -1 day" +%d)
  if [ "$D" = "31" ]; then break; fi
done
M31_NEXT=$(date -u -d "$M31-01 +1 month" +%Y-%m)
TS_31="${M31_NEXT}-01T02:30:00Z"

ZERO=00000000-0000-0000-0000-000000000000
RND=11111111-2222-3333-4444-555555555555

# ---------- helpers ----------
jget() { # jget chave -> valor (string ou numero) da chave em $BODY (ultima ocorrencia)
  local v
  v=$(printf '%s' "$BODY" | sed -n "s/.*\"$1\":\"\([^\"]*\)\".*/\1/p" | head -1)
  [ -z "$v" ] && v=$(printf '%s' "$BODY" | sed -n "s/.*\"$1\":\([-0-9.eE+]*\).*/\1/p" | head -1)
  printf '%s' "$v"
}
jfirst() { # primeira ocorrencia de uma chave de valor string
  printf '%s' "$BODY" | grep -o "\"$1\":\"[^\"]*\"" | head -1 | sed 's/.*:"\(.*\)"/\1/'
}

req() { # req METODO caminho QUEM [corpo] [content-type]
  local method="$1" path="$2" who="$3" body="${4-}" ctype="${5-application/json}"
  local args=(-s -X "$method" "$BASE$path")
  local shown="curl -s -X $method '$BASE$path'"
  case "$who" in
    none) ;;
    garbage) args+=(-H "Authorization: Bearer abc.def.ghi"); shown="$shown -H 'Authorization: Bearer abc.def.ghi'";;
    *) args+=(-H "Authorization: Bearer ${TOK[$who]}"); shown="$shown -H 'Authorization: Bearer <token de $who>'";;
  esac
  if [ -n "$body" ]; then
    args+=(-H "Content-Type: $ctype" --data-binary "$body")
    shown="$shown -H 'Content-Type: $ctype' -d '$body'"
  fi
  local out
  out=$(curl "${args[@]}" -w $'\n%{http_code}')
  STATUS="${out##*$'\n'}"
  BODY="${out%$'\n'*}"
  echo "COMANDO: $shown"
  echo "STATUS: $STATUS"
  echo "CORPO: $BODY"
  echo
}

prep() { echo "### PREPARO — $1"; shift; req "$@"; }
caso() { N=$((N+1)); printf '### CASO 4.%03d — %s\n' "$N" "$1"; shift; req "$@"; }
nota() { echo ">>> NOTA: $*"; echo; }

echo "=================================================================="
echo "SESSAO 4 — CoupleSync — metas, fluxo de caixa, relatorios, dashboard"
echo "Execucao: $RUN   Inicio (UTC): $NOW_ISO   Base: $BASE"
echo "Mes corrente: $CM | anterior: $PM | dois atras: $PM2 | mes de 31 dias: $M31 (transacao em $TS_31)"
echo "=================================================================="
echo

# ---------- PREPARO: usuarios e casais ----------
for u in a b c d e; do
  U=$(echo $u | tr a-z A-Z)
  prep "registrar usuario $U" POST /auth/register none "{\"email\":\"s4-$u-$RUN@teste.com\",\"name\":\"S4 Usuario $U\",\"password\":\"$PASS\"}"
  TOK[$U]=$(jget accessToken)
  eval "UID_$U=$(jfirst id)"
done
prep "A cria o casal X" POST /couples A
TOK[A]=$(jget accessToken); JOIN=$(jget joinCode); COUPLE_X=$(jget coupleId)
prep "B entra no casal X" POST /couples/join B "{\"joinCode\":\"$JOIN\"}"
TOK[B]=$(jget accessToken)
prep "C cria o casal Y" POST /couples C
TOK[C]=$(jget accessToken)
prep "E cria o casal Z (ficara sem nenhuma transacao)" POST /couples E
TOK[E]=$(jget accessToken)
nota "D fica sem casal. UID_A=$UID_A UID_B=$UID_B UID_C=$UID_C casalX=$COUPLE_X"

# ---------- PREPARO: transacoes do casal X ----------
tx() { # tx QUEM valor categoria timestamp descricao -> TXID
  prep "transacao '$5' ($2 em $3, $4) por $1" POST /transactions "$1" "{\"amount\":$2,\"currency\":\"BRL\",\"eventTimestampUtc\":\"$4\",\"description\":\"$5\",\"category\":\"$3\"}"
  TXID=$(jfirst id)
}
tx A 100    Alimentacao "$TS_A"                 "s4 mercado";        TX1=$TXID
tx B 50     Transporte  "$TS_B"                 "s4 onibus";         TX2=$TXID
tx A 25.50  Alimentacao "$TS_C"                 "s4 padaria";        TX3=$TXID
tx A 200    Lazer       "${PM}-15T12:00:00Z"    "s4 cinema";         TX4=$TXID
tx B 1000   Moradia     "${PM2}-10T12:00:00Z"   "s4 aluguel";        TX5=$TXID
tx A 77     Saude       "$TS_31"                "s4 farmacia dia 31 23h30 BRT"; TX6=$TXID
tx A 500    Poupanca    "$TS_A"                 "s4 aporte meta";    TX7=$TXID
tx B 300    Poupanca    "$TS_B"                 "s4 aporte meta 2";  TX8=$TXID
nota "Casal X: mes corrente ($CM) = 100+50+25.50+500+300 = 975.50 (5 transacoes); $PM = 200 (+77 se a de 02:30Z contar em UTC); $PM2 = 1000 (+77 se contar no horario de Brasilia). Total geral = 2252.50 (8 transacoes). Por categoria: Alimentacao 125.50, Transporte 50, Lazer 200, Moradia 1000, Saude 77, Poupanca 800."
tx C 999    Viagem      "$TS_A"                 "s4 casal Y";        TXC=$TXID

# ---------- PREPARO: rendas ----------
prep "renda pessoal de A no mes corrente (5000)" POST /incomes A "{\"month\":\"$CM\",\"name\":\"Salario A\",\"amount\":5000,\"currency\":\"BRL\",\"isShared\":false,\"isRecurring\":true}"
prep "renda compartilhada de B no mes corrente (3000)" POST /incomes B "{\"month\":\"$CM\",\"name\":\"Aluguel recebido\",\"amount\":3000,\"currency\":\"BRL\",\"isShared\":true,\"isRecurring\":false}"
prep "renda pessoal de A no mes anterior (4000)" POST /incomes A "{\"month\":\"$PM\",\"name\":\"Salario A\",\"amount\":4000,\"currency\":\"BRL\",\"isShared\":false,\"isRecurring\":false}"
prep "conferir renda cadastrada no mes corrente" GET /incomes/current A
prep "conferir renda cadastrada no mes anterior" GET "/incomes/$PM" A

echo "=================== METAS ==================="; echo
caso "criar meta valida (titulo, descricao, alvo 10000, prazo +1 ano)" POST /goals A "{\"title\":\"Viagem Japao\",\"description\":\"Ferias\",\"targetAmount\":10000,\"currency\":\"BRL\",\"deadline\":\"$FUT\"}"
G1=$(jfirst id)
caso "listar metas" GET /goals A
caso "detalhar meta" GET "/goals/$G1" A
caso "parceiro B detalha a meta criada por A (mesmo casal)" GET "/goals/$G1" B
caso "editar so o titulo" PATCH "/goals/$G1" A '{"title":"Viagem Japao 2027"}'
caso "editar so o valor-alvo (12000)" PATCH "/goals/$G1" A '{"targetAmount":12000}'
caso "editar SO currentAmount (1500), sem mandar prazo" PATCH "/goals/$G1" A '{"currentAmount":1500}'
caso "editar so o prazo (+2 anos)" PATCH "/goals/$G1" A "{\"deadline\":\"$FUT2\"}"
caso "editar so a descricao" PATCH "/goals/$G1" A '{"description":"Nova descricao"}'
caso "editar titulo, alvo, valor atual e prazo juntos (pelo parceiro B)" PATCH "/goals/$G1" B "{\"title\":\"Viagem Japao\",\"targetAmount\":10000,\"currentAmount\":2000,\"deadline\":\"$FUT\"}"
caso "editar com corpo vazio {}" PATCH "/goals/$G1" A '{}'
caso "detalhar apos edicoes (confere persistencia)" GET "/goals/$G1" A

caso "criar meta com prazo no passado (-30 dias)" POST /goals A "{\"title\":\"Prazo passado\",\"targetAmount\":100,\"deadline\":\"$PAST\"}"
G_PAST=""; [ "$STATUS" = "201" ] && G_PAST=$(jfirst id)
caso "criar meta com prazo hoje 00:00Z" POST /goals A "{\"title\":\"Prazo hoje 00h\",\"targetAmount\":100,\"deadline\":\"${TODAY}T00:00:00Z\"}"
caso "criar meta com prazo hoje, so a data (YYYY-MM-DD)" POST /goals A "{\"title\":\"Prazo hoje data\",\"targetAmount\":100,\"deadline\":\"$TODAY\"}"
caso "criar meta com prazo hoje 23:59:59Z" POST /goals A "{\"title\":\"Prazo hoje 23h59\",\"targetAmount\":100,\"deadline\":\"${TODAY}T23:59:59Z\"}"
G_TODAY=""; [ "$STATUS" = "201" ] && G_TODAY=$(jfirst id)
caso "criar meta com valor-alvo zero" POST /goals A "{\"title\":\"Alvo zero\",\"targetAmount\":0,\"deadline\":\"$FUT\"}"
caso "criar meta com valor-alvo negativo" POST /goals A "{\"title\":\"Alvo negativo\",\"targetAmount\":-100,\"deadline\":\"$FUT\"}"
caso "criar meta com valor-alvo gigante (1e18)" POST /goals A "{\"title\":\"Alvo gigante 1e18\",\"targetAmount\":1000000000000000000,\"deadline\":\"$FUT\"}"
G_BIG=""; [ "$STATUS" = "201" ] && G_BIG=$(jfirst id)
caso "criar meta com valor-alvo 9999999999999999.99 (16 digitos inteiros)" POST /goals A "{\"title\":\"Alvo 16 digitos\",\"targetAmount\":9999999999999999.99,\"deadline\":\"$FUT\"}"
G_BIG16=""; [ "$STATUS" = "201" ] && G_BIG16=$(jfirst id)
caso "criar meta com valor-alvo 1e16 (17 digitos inteiros)" POST /goals A "{\"title\":\"Alvo 17 digitos\",\"targetAmount\":10000000000000000,\"deadline\":\"$FUT\"}"
G_BIG17=""; [ "$STATUS" = "201" ] && G_BIG17=$(jfirst id)
caso "criar meta com valor-alvo gigante (29 digitos)" POST /goals A "{\"title\":\"Alvo gigante 29\",\"targetAmount\":99999999999999999999999999999,\"deadline\":\"$FUT\"}"
caso "criar meta com valor-alvo de 40 digitos" POST /goals A "{\"title\":\"Alvo gigante 40\",\"targetAmount\":9999999999999999999999999999999999999999,\"deadline\":\"$FUT\"}"
caso "criar meta com valor-alvo com 3 casas decimais (100.999)" POST /goals A "{\"title\":\"Tres casas\",\"targetAmount\":100.999,\"deadline\":\"$FUT\"}"
G_3C=""; [ "$STATUS" = "201" ] && G_3C=$(jfirst id)
caso "detalhar meta criada com 100.999 (valor gravado?)" GET "/goals/${G_3C:-$RND}" A
caso "criar meta com valor-alvo 0.001 (arredonda para zero?)" POST /goals A "{\"title\":\"Milesimo\",\"targetAmount\":0.001,\"deadline\":\"$FUT\"}"
G_MIL=""; [ "$STATUS" = "201" ] && G_MIL=$(jfirst id)
if [ -n "$G_MIL" ]; then
  caso "detalhar meta criada com alvo 0.001" GET "/goals/$G_MIL" A
  caso "progresso de meta criada com alvo 0.001 (divisao por zero?)" GET "/goals/$G_MIL/progress" A
  caso "progress-summary com meta de alvo 0.001" GET "/goals/progress-summary" A
  caso "excluir meta de alvo 0.001" DELETE "/goals/$G_MIL" A
fi
caso "criar meta com valor-alvo minimo 0.01" POST /goals A "{\"title\":\"Um centavo\",\"targetAmount\":0.01,\"deadline\":\"$FUT\"}"
caso "criar meta com titulo vazio" POST /goals A "{\"title\":\"\",\"targetAmount\":100,\"deadline\":\"$FUT\"}"
caso "criar meta com titulo so de espacos" POST /goals A "{\"title\":\"   \",\"targetAmount\":100,\"deadline\":\"$FUT\"}"
T300=$(printf 'T%.0s' $(seq 1 300))
caso "criar meta com titulo de 300 caracteres" POST /goals A "{\"title\":\"$T300\",\"targetAmount\":100,\"deadline\":\"$FUT\"}"
caso "criar meta sem o campo title" POST /goals A "{\"targetAmount\":100,\"deadline\":\"$FUT\"}"
caso "criar meta sem o campo deadline" POST /goals A '{"title":"Sem prazo","targetAmount":100}'
caso "criar meta sem o campo targetAmount" POST /goals A "{\"title\":\"Sem alvo\",\"deadline\":\"$FUT\"}"
caso "criar meta com deadline invalido (texto)" POST /goals A '{"title":"Prazo texto","targetAmount":100,"deadline":"amanha"}'
caso "criar meta com deadline 31/02" POST /goals A '{"title":"Prazo 31-02","targetAmount":100,"deadline":"2027-02-31T00:00:00Z"}'
caso "criar meta com targetAmount como texto" POST /goals A "{\"title\":\"Alvo texto\",\"targetAmount\":\"mil\",\"deadline\":\"$FUT\"}"
caso "criar meta com moeda invalida (XYZW)" POST /goals A "{\"title\":\"Moeda invalida\",\"targetAmount\":100,\"currency\":\"XYZW\",\"deadline\":\"$FUT\"}"
caso "criar meta com moeda USD" POST /goals A "{\"title\":\"Moeda USD\",\"targetAmount\":100,\"currency\":\"USD\",\"deadline\":\"$FUT\"}"
D3000=$(printf 'd%.0s' $(seq 1 3000))
caso "criar meta com descricao de 3000 caracteres" POST /goals A "{\"title\":\"Descricao longa\",\"description\":\"$D3000\",\"targetAmount\":100,\"deadline\":\"$FUT\"}"
caso "criar meta com titulo contendo HTML/script" POST /goals A "{\"title\":\"<script>alert(1)</script>\",\"targetAmount\":100,\"deadline\":\"$FUT\"}"
caso "criar meta com JSON malformado" POST /goals A '{"title":"x",'
caso "criar meta com corpo {}" POST /goals A '{}'
caso "criar meta com Content-Type text/plain" POST /goals A "{\"title\":\"ctype\",\"targetAmount\":100,\"deadline\":\"$FUT\"}" text/plain
caso "criar meta duplicada (mesmo titulo da primeira)" POST /goals A "{\"title\":\"Viagem Japao\",\"targetAmount\":10000,\"deadline\":\"$FUT\"}"
G_DUP=""; [ "$STATUS" = "201" ] && G_DUP=$(jfirst id)

caso "editar com currentAmount negativo" PATCH "/goals/$G1" A '{"currentAmount":-50}'
caso "editar com currentAmount zero" PATCH "/goals/$G1" A '{"currentAmount":0}'
caso "editar com currentAmount negativo + titulo (contorna a recusa de currentAmount sozinho)" PATCH "/goals/$G1" A '{"title":"Viagem Japao","currentAmount":-50}'
caso "detalhar apos currentAmount negativo + titulo" GET "/goals/$G1" A
caso "editar com currentAmount negativo + prazo" PATCH "/goals/$G1" A "{\"currentAmount\":-50,\"deadline\":\"$FUT\"}"
caso "editar com currentAmount com 3 casas (10.555) + titulo" PATCH "/goals/$G1" A '{"title":"Viagem Japao","currentAmount":10.555}'
caso "editar com targetAmount zero" PATCH "/goals/$G1" A '{"targetAmount":0}'
caso "editar com targetAmount negativo" PATCH "/goals/$G1" A '{"targetAmount":-1}'
caso "editar com titulo vazio" PATCH "/goals/$G1" A '{"title":""}'
caso "editar com titulo so de espacos" PATCH "/goals/$G1" A '{"title":"   "}'
caso "editar com titulo de 300 caracteres" PATCH "/goals/$G1" A "{\"title\":\"$T300\"}"
caso "editar com prazo no passado" PATCH "/goals/$G1" A "{\"deadline\":\"$PAST\"}"
caso "editar com prazo hoje 00:00Z" PATCH "/goals/$G1" A "{\"deadline\":\"${TODAY}T00:00:00Z\"}"
caso "editar com targetAmount gigante (29 digitos)" PATCH "/goals/$G1" A '{"targetAmount":99999999999999999999999999999}'
caso "editar com currentAmount gigante (1e18)" PATCH "/goals/$G1" A '{"currentAmount":1000000000000000000}'
caso "editar com currentAmount gigante (1e18) + titulo" PATCH "/goals/$G1" A '{"title":"Viagem Japao","currentAmount":1000000000000000000}'
caso "editar com targetAmount gigante (1e18)" PATCH "/goals/$G1" A '{"targetAmount":1000000000000000000}'
caso "editar com prazo hoje 23:59:59Z (ainda futuro)" PATCH "/goals/$G1" A "{\"deadline\":\"${TODAY}T23:59:59Z\"}"
caso "editar com prazo hoje, so a data (YYYY-MM-DD)" PATCH "/goals/$G1" A "{\"deadline\":\"$TODAY\"}"
caso "editar com descricao de 3000 caracteres" PATCH "/goals/$G1" A "{\"description\":\"$D3000\"}"
caso "detalhar apos tentativas invalidas de edicao (estado de G1)" GET "/goals/$G1" A
caso "restaurar G1 (titulo, alvo 10000, atual 2000, prazo +1 ano)" PATCH "/goals/$G1" A "{\"title\":\"Viagem Japao\",\"targetAmount\":10000,\"currentAmount\":2000,\"deadline\":\"$FUT\"}"

if [ -n "$G_TODAY" ]; then
  caso "editar SO currentAmount de meta cujo prazo e hoje (23:59:59Z)" PATCH "/goals/$G_TODAY" A '{"currentAmount":10}'
  caso "editar currentAmount + titulo de meta cujo prazo e hoje (23:59:59Z)" PATCH "/goals/$G_TODAY" A '{"title":"Prazo hoje 23h59","currentAmount":10}'
  caso "editar so o titulo de meta cujo prazo e hoje (23:59:59Z)" PATCH "/goals/$G_TODAY" A '{"title":"Prazo hoje editada"}'
  caso "progresso de meta cujo prazo e hoje (daysRemaining?)" GET "/goals/$G_TODAY/progress" A
else
  nota "Meta com prazo hoje 23:59:59Z nao foi criada; a edicao de meta com prazo hoje e coberta pelo caso seguinte (prazo em +8 s)."
fi
DL_SOON=$(date -u -d "+8 seconds" +%Y-%m-%dT%H:%M:%SZ)
caso "criar meta com prazo daqui a 8 segundos ($DL_SOON)" POST /goals A "{\"title\":\"Prazo iminente\",\"targetAmount\":100,\"deadline\":\"$DL_SOON\"}"
G_SOON=""; [ "$STATUS" = "201" ] && G_SOON=$(jfirst id)
if [ -n "$G_SOON" ]; then
  sleep 12
  nota "Aguardados 12 s: o prazo da meta acima (hoje) ja venceu. Agora (UTC): $(date -u +%H:%M:%S)"
  caso "editar SO currentAmount de meta cujo prazo (hoje) ja passou" PATCH "/goals/$G_SOON" A '{"currentAmount":10}'
  caso "editar currentAmount + titulo de meta cujo prazo (hoje) ja passou" PATCH "/goals/$G_SOON" A '{"title":"Prazo iminente","currentAmount":10}'
  caso "editar so o titulo de meta cujo prazo (hoje) ja passou" PATCH "/goals/$G_SOON" A '{"title":"Prazo vencido editada"}'
  caso "reenviar o MESMO prazo (ja vencido) ao editar" PATCH "/goals/$G_SOON" A "{\"title\":\"Reenvio\",\"deadline\":\"$DL_SOON\"}"
  caso "progresso de meta com prazo vencido (daysRemaining negativo?)" GET "/goals/$G_SOON/progress" A
fi

caso "valor atual maior que o alvo: currentAmount 15000 em meta de alvo 10000" PATCH "/goals/$G1" A '{"currentAmount":15000}'
caso "valor atual maior que o alvo, mandando titulo junto: currentAmount 15000, alvo 10000" PATCH "/goals/$G1" A '{"title":"Viagem Japao","currentAmount":15000}'
caso "valor atual > alvo: GET /goals/{id}/progress (percentual passa de 100?)" GET "/goals/$G1/progress" A
caso "valor atual > alvo: GET /goals/{id} (detalhe)" GET "/goals/$G1" A
caso "valor atual > alvo: GET /goals/progress-summary" GET /goals/progress-summary A
nota "Meta G1 = $G1. Conferir currentAmount/progressPercent/isAchieved de G1 no resumo acima."
caso "restaurar currentAmount de G1 para 2000 (com titulo)" PATCH "/goals/$G1" A '{"title":"Viagem Japao","currentAmount":2000}'

caso "criar meta para arquivar" POST /goals A "{\"title\":\"Para arquivar\",\"targetAmount\":500,\"deadline\":\"$FUT\"}"
G_ARCH=$(jfirst id)
caso "arquivar meta (DELETE /goals/{id}/archive)" DELETE "/goals/$G_ARCH/archive" A
caso "detalhar meta arquivada" GET "/goals/$G_ARCH" A
caso "listar metas (padrao): arquivada nao deve aparecer" GET /goals A
nota "Id da meta arquivada: $G_ARCH (conferir se aparece na lista acima)."
caso "listar metas com includeArchived=true" GET "/goals?includeArchived=true" A
caso "listar metas com includeArchived=banana" GET "/goals?includeArchived=banana" A
caso "editar meta arquivada" PATCH "/goals/$G_ARCH" A '{"title":"Arquivada editada","currentAmount":123}'
caso "arquivar de novo meta ja arquivada" DELETE "/goals/$G_ARCH/archive" A
caso "progresso de meta arquivada" GET "/goals/$G_ARCH/progress" A
caso "arquivar via POST (metodo nao suportado)" POST "/goals/$G_ARCH/archive" A
caso "criar meta para excluir" POST /goals A "{\"title\":\"Para excluir\",\"targetAmount\":500,\"deadline\":\"$FUT\"}"
G_DEL=$(jfirst id)
caso "excluir meta" DELETE "/goals/$G_DEL" A
caso "detalhar meta excluida" GET "/goals/$G_DEL" A
caso "excluir de novo a mesma meta" DELETE "/goals/$G_DEL" A
caso "editar meta excluida" PATCH "/goals/$G_DEL" A '{"title":"zumbi"}'
caso "meta excluida aparece em includeArchived=true?" GET "/goals?includeArchived=true" A
nota "Id da meta excluida: $G_DEL (conferir se aparece na lista acima)."
caso "detalhar meta inexistente (GUID aleatorio)" GET "/goals/$RND" A
caso "detalhar meta com GUID zerado" GET "/goals/$ZERO" A
caso "detalhar meta com id que nao e GUID" GET "/goals/abc" A
caso "editar meta inexistente" PATCH "/goals/$RND" A '{"title":"x"}'
caso "excluir meta inexistente" DELETE "/goals/$RND" A
caso "arquivar meta inexistente" DELETE "/goals/$RND/archive" A
caso "progresso de meta inexistente" GET "/goals/$RND/progress" A
caso "excluir meta com prazo no passado (se foi criada; senao id inexistente)" DELETE "/goals/${G_PAST:-$RND}" A

echo "=================== PROGRESSO ==================="; echo
caso "criar meta P (alvo 2000) para testes de progresso" POST /goals A "{\"title\":\"Meta Progresso\",\"targetAmount\":2000,\"deadline\":\"$FUT\"}"
GP=$(jfirst id)
caso "progresso da meta P antes de vincular" GET "/goals/$GP/progress" A
caso "vincular transacao de 500 a meta P" PATCH "/transactions/$TX7/goal" A "{\"goalId\":\"$GP\"}"
caso "apos vincular 500: GET /goals/{id}/progress" GET "/goals/$GP/progress" A
caso "apos vincular 500: GET /goals/{id} (detalhe)" GET "/goals/$GP" A
caso "apos vincular 500: GET /goals (lista)" GET /goals A
nota "Meta P = $GP. Conferir currentAmount de P na lista acima."
caso "apos vincular 500: GET /goals/progress-summary" GET /goals/progress-summary A
caso "vincular de novo a mesma transacao a mesma meta (idempotencia)" PATCH "/transactions/$TX7/goal" A "{\"goalId\":\"$GP\"}"
caso "progresso apos vinculo repetido (continua 500?)" GET "/goals/$GP/progress" A
caso "parceiro B vincula transacao de 300 (dele) a meta P" PATCH "/transactions/$TX8/goal" B "{\"goalId\":\"$GP\"}"
caso "progresso com 500 + 300 vinculados" GET "/goals/$GP/progress" B
caso "editar currentAmount manual (100) + titulo em meta que tem 800 vinculados" PATCH "/goals/$GP" A '{"title":"Meta Progresso","currentAmount":100}'
caso "com manual 100 + vinculado 800: progress" GET "/goals/$GP/progress" A
caso "com manual 100 + vinculado 800: detalhe" GET "/goals/$GP" A
caso "com manual 100 + vinculado 800: progress-summary" GET /goals/progress-summary A
caso "desvincular transacao de 300 (goalId null)" PATCH "/transactions/$TX8/goal" B '{"goalId":null}'
caso "desvincular transacao de 500 (goalId null)" PATCH "/transactions/$TX7/goal" A '{"goalId":null}'
caso "apos desvincular: progress" GET "/goals/$GP/progress" A
caso "apos desvincular: detalhe" GET "/goals/$GP" A
caso "apos desvincular: progress-summary" GET /goals/progress-summary A
caso "zerar currentAmount manual de P (com titulo)" PATCH "/goals/$GP" A '{"title":"Meta Progresso","currentAmount":0}'
caso "desvincular transacao que ja nao tem meta" PATCH "/transactions/$TX7/goal" A '{"goalId":null}'
caso "PATCH goal com corpo {} (sem goalId)" PATCH "/transactions/$TX7/goal" A '{}'
caso "PATCH goal com goalId que nao e GUID" PATCH "/transactions/$TX7/goal" A '{"goalId":"abc"}'
caso "vincular transacao a meta ARQUIVADA" PATCH "/transactions/$TX7/goal" A "{\"goalId\":\"$G_ARCH\"}"
caso "progresso da meta arquivada apos tentativa de vinculo" GET "/goals/$G_ARCH/progress" A
caso "desvincular apos tentativa em meta arquivada" PATCH "/transactions/$TX7/goal" A '{"goalId":null}'
caso "vincular transacao a meta INEXISTENTE" PATCH "/transactions/$TX7/goal" A "{\"goalId\":\"$RND\"}"
caso "vincular transacao a meta EXCLUIDA" PATCH "/transactions/$TX7/goal" A "{\"goalId\":\"$G_DEL\"}"
caso "C cria meta do casal Y" POST /goals C "{\"title\":\"Meta do casal Y\",\"targetAmount\":3000,\"deadline\":\"$FUT\"}"
GY=$(jfirst id)
caso "A vincula transacao do casal X a meta do casal Y" PATCH "/transactions/$TX7/goal" A "{\"goalId\":\"$GY\"}"
caso "C vincula transacao dele a meta do casal X" PATCH "/transactions/$TXC/goal" C "{\"goalId\":\"$GP\"}"
caso "C vincula transacao DO CASAL X a meta do casal Y" PATCH "/transactions/$TX7/goal" C "{\"goalId\":\"$GY\"}"
caso "vincular transacao inexistente a meta P" PATCH "/transactions/$RND/goal" A "{\"goalId\":\"$GP\"}"
caso "progresso da meta do casal Y (deve seguir 0)" GET "/goals/$GY/progress" C
caso "progresso da meta P (deve seguir 0)" GET "/goals/$GP/progress" A
caso "vincular 500 de novo a meta P" PATCH "/transactions/$TX7/goal" A "{\"goalId\":\"$GP\"}"
caso "reduzir alvo de P para 400 (vinculado 500 > alvo)" PATCH "/goals/$GP" A '{"targetAmount":400}'
caso "progresso com vinculado 500 e alvo 400 (isAchieved? percentual?)" GET "/goals/$GP/progress" A
caso "progress-summary com vinculado 500 e alvo 400" GET /goals/progress-summary A
caso "criar transacao extra de 40 para excluir depois de vincular" POST /transactions A "{\"amount\":40,\"currency\":\"BRL\",\"eventTimestampUtc\":\"$TS_C\",\"description\":\"s4 extra\",\"category\":\"Outros\"}"
TXE=$(jfirst id)
caso "vincular transacao extra (40) a meta P" PATCH "/transactions/$TXE/goal" A "{\"goalId\":\"$GP\"}"
caso "progresso com 500 + 40 vinculados" GET "/goals/$GP/progress" A
caso "excluir a transacao extra vinculada" DELETE "/transactions/$TXE" A
caso "progresso apos excluir transacao vinculada (volta a 500?)" GET "/goals/$GP/progress" A
caso "arquivar meta P com transacao vinculada" DELETE "/goals/$GP/archive" A
caso "progresso de P arquivada (com vinculo)" GET "/goals/$GP/progress" A
caso "excluir meta P que tem transacao vinculada" DELETE "/goals/$GP" A
caso "a transacao de 500 continua existindo apos excluir a meta? (lista de transacoes)" GET "/transactions?pageSize=100" A

echo "=================== FLUXO DE CAIXA ==================="; echo
caso "cashflow horizon=30" GET "/cashflow?horizon=30" A
nota "Comparar projectedSpend com totalHistoricalSpend e averageDailySpend."
caso "cashflow horizon=90" GET "/cashflow?horizon=90" A
caso "cashflow sem parametro" GET "/cashflow" A
caso "cashflow horizon=0" GET "/cashflow?horizon=0" A
caso "cashflow horizon=-30" GET "/cashflow?horizon=-30" A
caso "cashflow horizon=100000" GET "/cashflow?horizon=100000" A
caso "cashflow horizon=60" GET "/cashflow?horizon=60" A
caso "cashflow horizon=abc (texto)" GET "/cashflow?horizon=abc" A
caso "cashflow horizon=30.5" GET "/cashflow?horizon=30.5" A
caso "cashflow horizon=99999999999 (estouro de int)" GET "/cashflow?horizon=99999999999" A
caso "cashflow horizon vazio" GET "/cashflow?horizon=" A
caso "cashflow horizon=30 visto pelo parceiro B (mesmo resultado?)" GET "/cashflow?horizon=30" B
caso "cashflow horizon=30 de casal sem nenhuma transacao (E)" GET "/cashflow?horizon=30" E
caso "cashflow horizon=90 de casal sem nenhuma transacao (E)" GET "/cashflow?horizon=90" E
caso "cashflow horizon=30 do casal Y (C): so a transacao de 999" GET "/cashflow?horizon=30" C

echo "=================== RELATORIOS ==================="; echo
caso "spending-by-category sem parametros (padrao months=6)" GET "/reports/spending-by-category" A
caso "spending-by-category months=1" GET "/reports/spending-by-category?months=1" A
caso "spending-by-category months=2" GET "/reports/spending-by-category?months=2" A
caso "spending-by-category months=3" GET "/reports/spending-by-category?months=3" A
caso "spending-by-category months=0" GET "/reports/spending-by-category?months=0" A
caso "spending-by-category months=-1" GET "/reports/spending-by-category?months=-1" A
caso "spending-by-category months=1000" GET "/reports/spending-by-category?months=1000" A
caso "spending-by-category months=60 (limite)" GET "/reports/spending-by-category?months=60" A
caso "spending-by-category months=61" GET "/reports/spending-by-category?months=61" A
caso "spending-by-category months=abc" GET "/reports/spending-by-category?months=abc" A
caso "spending-by-category months=1.5" GET "/reports/spending-by-category?months=1.5" A
caso "spending-by-category com periodo valido startDate/endDate (so mes anterior)" GET "/reports/spending-by-category?startDate=${PM}-01&endDate=${PM}-28" A
caso "spending-by-category com datas invertidas" GET "/reports/spending-by-category?startDate=${CM}-28&endDate=${PM}-01" A
caso "spending-by-category com data invalida" GET "/reports/spending-by-category?startDate=banana&endDate=2026-13-45" A
caso "spending-by-category com periodo vazio (2020)" GET "/reports/spending-by-category?startDate=2020-01-01&endDate=2020-01-31" A
caso "spending-by-category de casal sem transacoes (E)" GET "/reports/spending-by-category" E
caso "spending-by-category visto pelo parceiro B" GET "/reports/spending-by-category" B
caso "monthly-trends sem parametros (padrao months=12)" GET "/reports/monthly-trends" A
caso "monthly-trends months=1" GET "/reports/monthly-trends?months=1" A
caso "monthly-trends months=3" GET "/reports/monthly-trends?months=3" A
caso "monthly-trends months=6" GET "/reports/monthly-trends?months=6" A
caso "monthly-trends months=0" GET "/reports/monthly-trends?months=0" A
caso "monthly-trends months=-1" GET "/reports/monthly-trends?months=-1" A
caso "monthly-trends months=1000" GET "/reports/monthly-trends?months=1000" A
caso "monthly-trends months=60 (limite)" GET "/reports/monthly-trends?months=60" A
caso "monthly-trends months=abc" GET "/reports/monthly-trends?months=abc" A
caso "monthly-trends com periodo startDate/endDate (so mes anterior)" GET "/reports/monthly-trends?startDate=${PM}-01&endDate=${PM}-28" A
caso "monthly-trends com datas invertidas" GET "/reports/monthly-trends?startDate=${CM}-28&endDate=${PM}-01" A
caso "monthly-trends com data invalida" GET "/reports/monthly-trends?startDate=banana" A
caso "monthly-trends de casal sem transacoes (E)" GET "/reports/monthly-trends?months=3" E
caso "rota de relatorio inexistente" GET "/reports/nao-existe" A
caso "GET /reports (raiz)" GET "/reports" A

echo "=================== DASHBOARD ==================="; echo
caso "dashboard sem parametros" GET "/dashboard" A
caso "dashboard sem parametros visto pelo parceiro B" GET "/dashboard" B
caso "dashboard so com startDate (inicio de dois meses atras)" GET "/dashboard?startDate=${PM2}-01" A
caso "dashboard so com startDate no futuro" GET "/dashboard?startDate=$(date -u -d "$CM-01 +2 month" +%Y-%m-01)" A
caso "dashboard so com endDate (inicio do mes corrente)" GET "/dashboard?endDate=${CM}-01T00:00:00Z" A
caso "dashboard so com endDate bem antigo (2020-01-31)" GET "/dashboard?endDate=2020-01-31" A
caso "dashboard com periodo cobrindo tudo" GET "/dashboard?startDate=${PM2}-01T00:00:00Z&endDate=$(date -u -d '+1 day' +%Y-%m-%d)T00:00:00Z" A
caso "dashboard so mes anterior ($PM) em UTC" GET "/dashboard?startDate=${PM}-01T00:00:00Z&endDate=$(date -u -d "${CM}-01 -1 day" +%Y-%m-%d)T23:59:59Z" A
caso "dashboard so dois meses atras ($PM2) em UTC" GET "/dashboard?startDate=${PM2}-01T00:00:00Z&endDate=$(date -u -d "${PM}-01 -1 day" +%Y-%m-%d)T23:59:59Z" A
caso "dashboard do mes $M31 no horario de Brasilia (03:00Z a 02:59:59Z)" GET "/dashboard?startDate=${M31}-01T03:00:00Z&endDate=${M31_NEXT}-01T02:59:59Z" A
caso "dashboard do mes $M31 com offset -03:00 na query" GET "/dashboard?startDate=${M31}-01T00:00:00-03:00&endDate=${M31}-31T23:59:59-03:00" A
caso "dashboard de $PM2 com fim exclusivo (endDate = ${PM}-01T00:00:00Z): entra a transacao de ${PM}-01T02:30Z?" GET "/dashboard?startDate=${PM2}-01T00:00:00Z&endDate=${PM}-01T00:00:00Z" A
caso "dashboard so com endDate = ultimo dia do mes anterior" GET "/dashboard?endDate=$(date -u -d "${CM}-01 -1 day" +%Y-%m-%d)" A
caso "dashboard so com endDate = hoje" GET "/dashboard?endDate=$TODAY" A
caso "dashboard so com startDate = hoje" GET "/dashboard?startDate=$TODAY" A
caso "dashboard com endDate = hoje so a data (inclui as transacoes de hoje?)" GET "/dashboard?startDate=${CM}-01&endDate=$TODAY" A
caso "dashboard com startDate = endDate = hoje (so a data)" GET "/dashboard?startDate=$TODAY&endDate=$TODAY" A
caso "dashboard com datas invertidas" GET "/dashboard?startDate=${CM}-28&endDate=${PM}-01" A
caso "dashboard com periodo sem dados (2020)" GET "/dashboard?startDate=2020-01-01&endDate=2020-01-31" A
caso "dashboard com startDate invalida (banana)" GET "/dashboard?startDate=banana" A
caso "dashboard com endDate invalida (2026-02-31)" GET "/dashboard?endDate=2026-02-31" A
caso "dashboard com data em formato brasileiro (dd/mm/aaaa)" GET "/dashboard?startDate=01/10/2026&endDate=31/12/2026" A
caso "dashboard com periodo de 200 anos" GET "/dashboard?startDate=1900-01-01&endDate=2100-01-01" A
caso "dashboard com ano 0001 e 9999" GET "/dashboard?startDate=0001-01-01&endDate=9999-12-31" A
caso "dashboard de casal sem transacoes (E)" GET "/dashboard" E
caso "lista de transacoes do casal X para conferir somas" GET "/transactions?pageSize=100" A

echo "=================== ISOLAMENTO ==================="; echo
caso "C le meta do casal X pelo id" GET "/goals/$G1" C
caso "C edita meta do casal X" PATCH "/goals/$G1" C '{"title":"INVADIDA","currentAmount":1}'
caso "C ve progresso de meta do casal X" GET "/goals/$G1/progress" C
caso "C arquiva meta do casal X" DELETE "/goals/$G1/archive" C
caso "C exclui meta do casal X" DELETE "/goals/$G1" C
caso "A confere que a meta segue intacta" GET "/goals/$G1" A
caso "C lista metas (so as do casal Y)" GET "/goals?includeArchived=true" C
caso "C progress-summary (so metas do casal Y)" GET "/goals/progress-summary" C
caso "C spending-by-category (so Viagem 999)" GET "/reports/spending-by-category" C
caso "C monthly-trends months=3 (so 999 no mes corrente; renda de X nao pode aparecer)" GET "/reports/monthly-trends?months=3" C
caso "C dashboard (so 999)" GET "/dashboard" C
caso "C dashboard com periodo amplo" GET "/dashboard?startDate=2000-01-01&endDate=2100-01-01" C
caso "C dashboard tentando forcar coupleId do casal X na query" GET "/dashboard?coupleId=$COUPLE_X&startDate=2000-01-01&endDate=2100-01-01" C
caso "C cashflow tentando forcar coupleId do casal X na query" GET "/cashflow?horizon=30&coupleId=$COUPLE_X" C
caso "C spending-by-category tentando forcar coupleId do casal X" GET "/reports/spending-by-category?coupleId=$COUPLE_X" C

echo "=================== AUTENTICACAO E FORMATO DE ERRO ==================="; echo
caso "metas sem token" GET /goals none
caso "metas com token invalido" GET /goals garbage
caso "cashflow sem token" GET "/cashflow?horizon=30" none
caso "relatorio sem token" GET /reports/monthly-trends none
caso "dashboard sem token" GET /dashboard none
caso "metas com usuario sem casal (D)" GET /goals D
caso "criar meta com usuario sem casal (D)" POST /goals D "{\"title\":\"Sem casal\",\"targetAmount\":100,\"deadline\":\"$FUT\"}"
caso "cashflow com usuario sem casal (D)" GET "/cashflow?horizon=30" D
caso "relatorio com usuario sem casal (D)" GET /reports/spending-by-category D
caso "dashboard com usuario sem casal (D)" GET /dashboard D
caso "metodo nao suportado: PUT /goals/{id}" PUT "/goals/$G1" A '{"title":"x"}'
caso "metodo nao suportado: POST /dashboard" POST /dashboard A '{}'
caso "metodo nao suportado: DELETE /cashflow" DELETE "/cashflow?horizon=30" A

echo "=================== LIMPEZA ==================="; echo
[ -n "$G_BIG" ] && prep "excluir meta gigante" DELETE "/goals/$G_BIG" A
[ -n "$G_BIG16" ] && prep "excluir meta 16 digitos" DELETE "/goals/$G_BIG16" A
[ -n "$G_BIG17" ] && prep "excluir meta 17 digitos" DELETE "/goals/$G_BIG17" A
[ -n "$G_DUP" ] && prep "excluir meta duplicada" DELETE "/goals/$G_DUP" A

echo "=================================================================="
echo "FIM — total de casos: $N — termino (UTC): $(date -u +%Y-%m-%dT%H:%M:%SZ)"
echo "=================================================================="
