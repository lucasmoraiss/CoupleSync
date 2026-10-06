#!/usr/bin/env bash
# Sessao de teste exploratorio no 2 - CoupleSync
# Escopo: /api/v1/transactions, /api/v1/integrations/{events,status}, /api/v1/dashboard
# Uso: bash sessao-2.sh   (gera sessao-2.log na mesma pasta; reexecutavel do zero)
set -u
BASE="${BASE:-http://localhost:5000}"
DIR="$(cd "$(dirname "$0")" && pwd)"
LOG="$DIR/sessao-2.log"
RUN="$(date +%Y%m%d%H%M%S)-$RANDOM"
PASS='Senha@12345'
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
exec > "$LOG" 2>&1

N=0
STATUS=""; BODY=""
TOK_NONE=""; TOK_BAD="abc.def.ghi"
CT="application/json"

caso() { N=$((N+1)); printf '\n### CASO 2.%02d — %s\n' "$N" "$1"; }
curto() { # abrevia corpos grandes no log do comando
  local s="$1"
  if [ ${#s} -gt 300 ]; then printf '%s...[corpo com %d caracteres, abreviado]' "${s:0:160}" "${#s}"; else printf '%s' "$s"; fi
}
# req METODO CAMINHO QUEM [CORPO]
req() {
  local method="$1" path="$2" who="$3" body="${4-}" tokvar="TOK_$3" out
  local token="${!tokvar}"
  local args=(-s -X "$method" "$BASE$path" -w $'\n%{http_code}')
  local shown="curl -s -X $method '$BASE$path'"
  if [ -n "$token" ]; then args+=(-H "Authorization: Bearer $token"); shown+=" -H 'Authorization: Bearer <token $who>'"; fi
  if [ $# -ge 4 ]; then
    if [ -n "$CT" ]; then args+=(-H "Content-Type: $CT"); shown+=" -H 'Content-Type: $CT'"; fi
    args+=(--data-binary @-); shown+=" -d '$(curto "$body")'"
    out="$(printf '%s' "$body" | curl "${args[@]}")"
  else
    out="$(curl "${args[@]}")"
  fi
  STATUS="${out##*$'\n'}"; BODY="${out%$'\n'*}"
  [ "$out" = "$STATUS" ] && BODY=""
  printf '$ %s\nHTTP %s\n%s\n' "$shown" "$STATUS" "$BODY"
}
campo() { printf '%s' "$BODY" | grep -o "\"$1\":\"[^\"]*\"" | head -1 | cut -d'"' -f4; }
rep() { head -c "$1" /dev/zero | tr '\0' "${2:-a}"; }
conta() { printf '%s' "$BODY" | grep -o "$1" | wc -l | tr -d ' '; }

echo "SESSAO 2 — transacoes, ingestao de notificacoes e dashboard"
echo "Inicio: $(date '+%Y-%m-%d %H:%M:%S %z')   BASE=$BASE   RUN=$RUN"

############################ PREPARO ############################
printf '\n### PREPARO — cadastro de usuarios e casais\n'
registrar() { # letra nome
  req POST /api/v1/auth/register NONE "{\"email\":\"s2-$1-$RUN@teste.local\",\"name\":\"$2\",\"password\":\"$PASS\"}"
  printf -v "TOK_$1" '%s' "$(campo accessToken)"
  printf -v "UID_$1" '%s' "$(printf '%s' "$BODY" | grep -o '"id":"[^"]*"' | head -1 | cut -d'"' -f4)"
}
registrar A "Ana S2"; registrar B "Bruno S2"; registrar C "Carla S2"; registrar D "Davi S2 sem casal"
req POST /api/v1/couples A ''
JOIN_X="$(campo joinCode)"; TOK_A="$(campo accessToken)"
req POST /api/v1/couples/join B "{\"joinCode\":\"$JOIN_X\"}"
TOK_B="$(campo accessToken)"
req POST /api/v1/couples C ''
TOK_C="$(campo accessToken)"
echo "PREPARO: UID_A=$UID_A UID_B=$UID_B UID_C=$UID_C JOIN_X=$JOIN_X"
if [ -z "$TOK_A" ] || [ -z "$TOK_B" ] || [ -z "$TOK_C" ]; then echo "PREPARO FALHOU — abortando"; echo "PREPARO FALHOU" >&2; exit 1; fi

T=/api/v1/transactions
tx() { printf '{"amount":%s,"currency":"BRL","eventTimestampUtc":"%s","description":"%s","merchant":"%s","category":"%s"}' "$1" "$2" "$3" "$4" "$5"; }

############################ CAMINHO FELIZ ############################
caso "Dashboard do casal X antes de qualquer transacao (A)"
req GET /api/v1/dashboard A
caso "Listar transacoes do casal X vazio (A)"
req GET $T A
caso "Criar transacao manual valida (A): 100.50 BRL, Alimentacao"
req POST $T A "$(tx 100.50 2026-10-03T12:00:00Z 'Almoco de sabado' 'Restaurante Bom Prato' 'Alimentacao')"
TX1="$(campo id)"; echo "TX1=$TX1"
caso "Listar transacoes (A) — deve conter a transacao criada"
req GET $T A
caso "B (mesmo casal) lista e deve ver a transacao de A"
req GET $T B
echo "VERIFICACAO: ocorrencias de TX1 na lista de B = $(conta "$TX1")"
caso "Dashboard depois de criar 100.50 (A) — esperado totalExpenses=100.50, transactionCount=1"
req GET /api/v1/dashboard A
caso "Alterar categoria (PATCH /{id}/category) de Alimentacao para Transporte (A)"
req PATCH $T/$TX1/category A '{"category":"Transporte"}'
caso "Dashboard depois de recategorizar — esperado expensesByCategory so com Transporte=100.50"
req GET /api/v1/dashboard A
caso "Editar transacao (PATCH /{id}) alterando valor e descricao (A)"
req PATCH $T/$TX1 A '{"amount":120.00,"description":"Almoco editado"}'
caso "Editar transacao via PUT /{id} (A)"
req PUT $T/$TX1 A "$(tx 120.00 2026-10-03T12:00:00Z 'Almoco editado' 'Restaurante Bom Prato' 'Transporte')"
caso "Ler transacao por id (GET /{id}) (A)"
req GET $T/$TX1 A
caso "B cria transacao 50.25 Lazer; dashboard deve somar 150.75 e detalhar por parceiro"
req POST $T B "$(tx 50.25 2026-10-04T20:00:00Z 'Cinema' 'Cine Centro' 'Lazer')"
TX2="$(campo id)"; echo "TX2=$TX2"
req GET /api/v1/dashboard A
caso "Filtrar por categoria=Transporte (A) — esperado so TX1"
req GET "$T?category=Transporte" A
caso "Filtrar por categoria em minusculas (category=transporte)"
req GET "$T?category=transporte" A
caso "Filtrar por categoria inexistente (category=NaoExiste) — esperado lista vazia"
req GET "$T?category=NaoExiste" A
caso "Filtrar por periodo startDate/endDate cobrindo so 2026-10-04 — esperado so TX2"
req GET "$T?startDate=2026-10-04T00:00:00Z&endDate=2026-10-04T23:59:59Z" A
caso "Filtrar por periodo com startDate > endDate"
req GET "$T?startDate=2026-10-05T00:00:00Z&endDate=2026-10-01T00:00:00Z" A
caso "Filtrar com startDate invalida (startDate=ontem)"
req GET "$T?startDate=ontem" A
caso "Dashboard com periodo explicito de 2026-10-04 — esperado total 50.25"
req GET "/api/v1/dashboard?startDate=2026-10-04T00:00:00Z&endDate=2026-10-04T23:59:59Z" A
caso "Dashboard com startDate > endDate"
req GET "/api/v1/dashboard?startDate=2026-10-05T00:00:00Z&endDate=2026-10-01T00:00:00Z" A
caso "Dashboard com startDate invalida"
req GET "/api/v1/dashboard?startDate=abc" A
caso "B recategoriza a transacao de A (mesmo casal) para Mercado"
req PATCH $T/$TX1/category B '{"category":"Mercado"}'
caso "Vincular transacao a meta nula (PATCH /{id}/goal com goalId null) (A)"
req PATCH $T/$TX1/goal A '{"goalId":null}'
caso "Vincular transacao a meta inexistente (goalId GUID aleatorio) (A)"
req PATCH $T/$TX1/goal A '{"goalId":"11111111-2222-3333-4444-555555555555"}'
caso "B tenta excluir a transacao de A (mesmo casal)"
req DELETE $T/$TX1 B
req GET $T A
echo "VERIFICACAO: ocorrencias de TX1 apos tentativa de B = $(conta "$TX1")"
caso "A exclui a transacao TX2, criada por B (mesmo casal)"
req DELETE $T/$TX2 A
caso "B exclui a propria transacao TX2"
req DELETE $T/$TX2 B
caso "Excluir de novo a mesma transacao TX2 (ja excluida) — esperado 404"
req DELETE $T/$TX2 B
caso "Lista e dashboard depois das exclusoes (A)"
req GET $T A
req GET /api/v1/dashboard A

############################ ISOLAMENTO ############################
caso "Isolamento: A cria transacao TXI do casal X que C tentara acessar"
req POST $T A "$(tx 77.70 2026-10-02T09:00:00Z 'Segredo do casal X' 'Loja Privada X' 'Casa')"
TXI="$(campo id)"; echo "TXI=$TXI"
caso "Isolamento: C (casal Y) lista transacoes — nao pode ver nada do casal X"
req GET $T C
echo "VERIFICACAO: ocorrencias de TXI na lista de C = $(conta "$TXI"); de 'Loja Privada X' = $(conta 'Loja Privada X')"
caso "Isolamento: C tenta ler a transacao de X por id (GET /{id})"
req GET $T/$TXI C
caso "Isolamento: C tenta recategorizar a transacao de X"
req PATCH $T/$TXI/category C '{"category":"Invadido"}'
caso "Isolamento: C tenta editar a transacao de X (PATCH /{id})"
req PATCH $T/$TXI C '{"amount":1.00,"description":"invadido"}'
caso "Isolamento: C tenta vincular a transacao de X a meta (PATCH /{id}/goal)"
req PATCH $T/$TXI/goal C '{"goalId":null}'
caso "Isolamento: C tenta excluir a transacao de X"
req DELETE $T/$TXI C
caso "Isolamento: A confere que TXI continua intacta (categoria Casa, 77.70)"
req GET "$T?category=Casa" A
echo "VERIFICACAO: ocorrencias de TXI = $(conta "$TXI"); categoria Invadido = $(conta 'Invadido')"
caso "Isolamento: dashboard de C nao pode refletir valores do casal X"
req GET /api/v1/dashboard C
caso "Isolamento: status de integracao de C"
req GET /api/v1/integrations/status C
caso "Usuario D sem casal tenta listar e criar transacoes"
req GET $T D
req POST $T D "$(tx 10 2026-10-03T12:00:00Z 'x' 'y' 'Casa')"
caso "Sem token: listar, criar, dashboard, status e ingestao"
req GET $T NONE
req POST $T NONE "$(tx 10 2026-10-03T12:00:00Z 'x' 'y' 'Casa')"
req GET /api/v1/dashboard NONE
req GET /api/v1/integrations/status NONE
req POST /api/v1/integrations/events NONE '{"bank":"NUBANK","amount":1,"currency":"BRL","eventTimestamp":"2026-10-03T12:00:00Z"}'
caso "Token invalido (abc.def.ghi): listar"
req GET $T BAD

############################ PAGINACAO (casal Y, limpo) ############################
caso "Paginacao: C cria 25 transacoes (valores 1.00 a 25.00, soma 325.00)"
for i in $(seq 1 25); do
  d=$(printf '%02d' $(( (i % 4) + 1 )))
  req POST $T C "$(tx "$i.00" "2026-10-${d}T10:$(printf '%02d' "$i"):00Z" "Pag item $i" "Loja Pag $i" 'Paginacao')" | sed -n '2p' | sed "s/^/item $i (valor $i.00, dia $d): /"
done
caso "Paginacao: sem parametros — esperado totalCount=25, page=1, pageSize=20, 20 itens"
req GET $T C
echo "VERIFICACAO: itens devolvidos = $(conta '"id":')"
caso "Paginacao: page=1&pageSize=10 — esperado 10 itens"
req GET "$T?page=1&pageSize=10" C
echo "VERIFICACAO: itens devolvidos = $(conta '"id":')"
caso "Paginacao: page=3&pageSize=10 — esperado 5 itens"
req GET "$T?page=3&pageSize=10" C
echo "VERIFICACAO: itens devolvidos = $(conta '"id":')"
caso "Paginacao: page=4&pageSize=10 (alem do fim) — esperado 0 itens e totalCount=25"
req GET "$T?page=4&pageSize=10" C
echo "VERIFICACAO: itens devolvidos = $(conta '"id":')"
caso "Paginacao: pageSize=100 — esperado 25 itens"
req GET "$T?pageSize=100" C
echo "VERIFICACAO: itens devolvidos = $(conta '"id":')"
caso "Paginacao: pageSize=101 (acima do maximo)"
req GET "$T?pageSize=101" C
echo "VERIFICACAO: itens devolvidos = $(conta '"id":')"
caso "Paginacao: page=0"
req GET "$T?page=0&pageSize=5" C
caso "Paginacao: page=-1"
req GET "$T?page=-1&pageSize=5" C
caso "Paginacao: pageSize=0"
req GET "$T?pageSize=0" C
echo "VERIFICACAO: itens devolvidos = $(conta '"id":')"
caso "Paginacao: pageSize=-5"
req GET "$T?pageSize=-5" C
echo "VERIFICACAO: itens devolvidos = $(conta '"id":')"
caso "Paginacao: page=abc"
req GET "$T?page=abc" C
caso "Paginacao: page=2147483647&pageSize=100 (estouro de inteiro no deslocamento)"
req GET "$T?page=2147483647&pageSize=100" C
caso "Paginacao: page=99999999999 (maior que int32)"
req GET "$T?page=99999999999" C
caso "Paginacao + filtro: category=Paginacao&page=2&pageSize=20 — esperado 5 itens, totalCount=25"
req GET "$T?category=Paginacao&page=2&pageSize=20" C
echo "VERIFICACAO: itens devolvidos = $(conta '"id":')"
caso "Dashboard de C depois das 25 — esperado totalExpenses=325.00, transactionCount=25"
req GET /api/v1/dashboard C

############################ ENTRADAS INVALIDAS (casal X) ############################
TS=2026-10-03T12:00:00Z
caso "Invalida: valor zero"
req POST $T A "$(tx 0 $TS 'valor zero' 'Loja Inv zero' 'Casa')"
caso "Invalida: valor negativo (-10.00)"
req POST $T A "$(tx -10.00 $TS 'valor negativo' 'Loja Inv negativo' 'Casa')"
caso "Invalida: valor com 3 casas decimais (10.999)"
req POST $T A "$(tx 10.999 $TS 'tres casas' 'Loja Inv 3casas' 'Casa')"
caso "Invalida: valor 99999999999999999999"
req POST $T A "$(tx 99999999999999999999 $TS 'valor gigante' 'Loja Inv gigante' 'Casa')"
caso "Invalida: valor 9999999999999999.99 (cabe em decimal, pode nao caber na coluna)"
req POST $T A "$(tx 9999999999999999.99 $TS 'valor grande' 'Loja Inv grande' 'Casa')"
caso "Limite: valor 1000000000.00 (um bilhao)"
req POST $T A "$(tx 1000000000.00 $TS 'um bilhao' 'Loja Inv bilhao' 'Casa')"
caso "Invalida: texto no lugar de numero (amount=\"abc\")"
req POST $T A "$(tx '"abc"' $TS 'texto' 'Loja Inv texto' 'Casa')"
caso "Invalida: numero entre aspas (amount=\"10.50\")"
req POST $T A "$(tx '"10.50"' $TS 'numero como string' 'Loja Inv string' 'Casa')"
caso "Invalida: valor com virgula decimal brasileira (amount=\"10,50\")"
req POST $T A "$(tx '"10,50"' $TS 'virgula' 'Loja Inv virgula' 'Casa')"
caso "Invalida: amount null"
req POST $T A "$(tx null $TS 'nulo' 'Loja Inv null' 'Casa')"
caso "Notacao cientifica (amount=1e3)"
req POST $T A "$(tx 1e3 $TS 'cientifica' 'Loja Inv 1e3' 'Casa')"
caso "Invalida: data futura distante (2099-12-31)"
req POST $T A "$(tx 10.00 2099-12-31T23:59:59Z 'futuro distante' 'Loja Inv futuro' 'Casa')"
caso "Invalida: data muito antiga (1900-01-01)"
req POST $T A "$(tx 10.00 1900-01-01T00:00:00Z 'passado distante' 'Loja Inv 1900' 'Casa')"
caso "Data sem fuso (2026-10-05T10:00:00 sem Z) — como e interpretada?"
req POST $T A "$(tx 10.00 2026-10-05T10:00:00 'sem fuso' 'Loja Inv semfuso' 'Casa')"
caso "Data com fuso -03:00 (2026-10-03T10:00:00-03:00) — esperado 13:00Z"
req POST $T A "$(tx 10.00 2026-10-03T10:00:00-03:00 'fuso BRT' 'Loja Inv brt' 'Casa')"
caso "Invalida: data em formato brasileiro (05/10/2026)"
req POST $T A "$(tx 10.00 05/10/2026 'data br' 'Loja Inv databr' 'Casa')"
caso "Invalida: data impossivel (2026-02-30T10:00:00Z)"
req POST $T A "$(tx 10.00 2026-02-30T10:00:00Z 'data impossivel' 'Loja Inv dataimp' 'Casa')"
caso "Sem eventTimestampUtc (campo omitido) — usa agora?"
req POST $T A '{"amount":10.00,"currency":"BRL","description":"sem data","merchant":"Loja Inv semdata","category":"Casa"}'
caso "Invalida: descricao vazia"
req POST $T A "$(tx 10.00 $TS '' 'Loja Inv descvazia' 'Casa')"
caso "Invalida: descricao so com espacos"
req POST $T A "$(tx 10.00 $TS '   ' 'Loja Inv descesp' 'Casa')"
caso "Invalida: descricao com 513 caracteres"
req POST $T A "$(tx 10.00 $TS "$(rep 513 d)" 'Loja Inv d513' 'Casa')"
caso "Limite: descricao com 512 caracteres"
req POST $T A "$(tx 10.00 $TS "$(rep 512 d)" 'Loja Inv d512' 'Casa')"
caso "Invalida: descricao com 5000 caracteres"
req POST $T A "$(tx 10.00 $TS "$(rep 5000 d)" 'Loja Inv d5000' 'Casa')"
caso "Invalida: estabelecimento (merchant) com 5000 caracteres"
req POST $T A "$(tx 10.00 $TS 'merchant longo' "$(rep 5000 m)" 'Casa')"
caso "Invalida: categoria vazia"
req POST $T A "$(tx 10.00 $TS 'cat vazia' 'Loja Inv catvazia' '')"
caso "Invalida: categoria so com espacos"
req POST $T A "$(tx 10.00 $TS 'cat espacos' 'Loja Inv catesp' '   ')"
caso "Invalida: categoria com 65 caracteres"
req POST $T A "$(tx 10.00 $TS 'cat 65' 'Loja Inv cat65' "$(rep 65 c)")"
caso "Limite: categoria com 64 caracteres"
req POST $T A "$(tx 10.00 $TS 'cat 64' 'Loja Inv cat64' "$(rep 64 c)")"
caso "Invalida: categoria ausente"
req POST $T A '{"amount":10.00,"currency":"BRL","eventTimestampUtc":"2026-10-03T12:00:00Z","description":"sem categoria","merchant":"Loja Inv semcat"}'
caso "Invalida: categoria null"
req POST $T A '{"amount":10.00,"currency":"BRL","eventTimestampUtc":"2026-10-03T12:00:00Z","description":"cat null","merchant":"Loja Inv catnull","category":null}'
caso "Invalida: moeda XYZ"
req POST $T A '{"amount":10.00,"currency":"XYZ","eventTimestampUtc":"2026-10-03T12:00:00Z","description":"moeda XYZ","merchant":"Loja Inv xyz","category":"Casa"}'
caso "Moeda USD (outra moeda real) — entra no total em BRL?"
req POST $T A '{"amount":10.00,"currency":"USD","eventTimestampUtc":"2026-10-03T12:00:00Z","description":"moeda USD","merchant":"Loja Inv usd","category":"Casa"}'
caso "Moeda em minusculas (brl)"
req POST $T A '{"amount":10.00,"currency":"brl","eventTimestampUtc":"2026-10-03T12:00:00Z","description":"moeda brl","merchant":"Loja Inv brlmin","category":"Casa"}'
caso "Invalida: moeda com 10 caracteres (REAISREAIS)"
req POST $T A '{"amount":10.00,"currency":"REAISREAIS","eventTimestampUtc":"2026-10-03T12:00:00Z","description":"moeda longa","merchant":"Loja Inv moedalonga","category":"Casa"}'
caso "Invalida: amount ausente"
req POST $T A '{"currency":"BRL","eventTimestampUtc":"2026-10-03T12:00:00Z","description":"sem valor","merchant":"Loja Inv semvalor","category":"Casa"}'
caso "Invalida: corpo {} (todos os campos ausentes)"
req POST $T A '{}'
caso "Minimo: apenas amount e category"
req POST $T A '{"amount":12.34,"category":"Casa"}'
caso "Invalida: JSON malformado"
req POST $T A '{"amount":10.00,"category":"Casa"'
caso "Invalida: corpo vazio"
req POST $T A ''
caso "Invalida: corpo e um array JSON"
req POST $T A '[]'
caso "Invalida: Content-Type text/plain"
CT="text/plain"; req POST $T A "$(tx 10.00 $TS 'text plain' 'Loja Inv textplain' 'Casa')"; CT="application/json"
caso "Descricao com HTML/script e caracteres especiais (armazenada como veio?)"
req POST $T A "$(tx 10.00 $TS '<script>alert(1)</script> ção ñ 😀' 'Loja Inv <b>html</b>' 'Casa')"
caso "Categoria com aspas simples e ponto-e-virgula (tentativa de injecao SQL)"
req POST $T A "$(tx 10.00 $TS 'sqli' 'Loja Inv sqli' "x'; DROP TABLE transactions;--")"
req GET "$T?category=x'%3B%20DROP%20TABLE%20transactions%3B--" A
caso "Campos desconhecidos e tentativa de forjar coupleId/userId/source/bank/id no corpo"
req POST $T A "{\"amount\":10.00,\"category\":\"Casa\",\"merchant\":\"Loja Inv forjada\",\"userId\":\"$UID_C\",\"coupleId\":\"11111111-2222-3333-4444-555555555555\",\"source\":\"Notification\",\"bank\":\"ITAU\",\"id\":\"11111111-2222-3333-4444-555555555555\"}"

caso "PATCH category: categoria vazia"
req PATCH $T/$TXI/category A '{"category":""}'
caso "PATCH category: categoria com 65 caracteres"
req PATCH $T/$TXI/category A "{\"category\":\"$(rep 65 c)\"}"
caso "PATCH category: campo ausente ({})"
req PATCH $T/$TXI/category A '{}'
caso "PATCH category: category numerica (123)"
req PATCH $T/$TXI/category A '{"category":123}'
caso "PATCH category: JSON malformado"
req PATCH $T/$TXI/category A '{"category":'
NOID=00000000-0000-4000-8000-000000000001
caso "Id inexistente: PATCH /{id}/category"
req PATCH $T/$NOID/category A '{"category":"Casa"}'
caso "Id inexistente: PATCH /{id}/goal"
req PATCH $T/$NOID/goal A '{"goalId":null}'
caso "Id inexistente: DELETE /{id}"
req DELETE $T/$NOID A
caso "Id inexistente: GET /{id} e PATCH /{id}"
req GET $T/$NOID A
req PATCH $T/$NOID A '{"amount":1}'
caso "Id que nao e GUID: PATCH /abc/category"
req PATCH $T/abc/category A '{"category":"Casa"}'
caso "Id que nao e GUID: PATCH /abc/goal"
req PATCH $T/abc/goal A '{"goalId":null}'
caso "Id que nao e GUID: DELETE /abc"
req DELETE $T/abc A
caso "Id que nao e GUID: GET /abc e DELETE /123"
req GET $T/abc A
req DELETE $T/123 A
caso "Id GUID zerado: DELETE /00000000-0000-0000-0000-000000000000"
req DELETE $T/00000000-0000-0000-0000-000000000000 A
caso "Conferencia apos entradas invalidas: quais transacoes 'Loja Inv*' ficaram gravadas no casal X"
req GET "$T?pageSize=100" A
echo "VERIFICACAO: transacoes gravadas com merchant iniciando em 'Loja Inv' = $(conta '"merchant":"Loja Inv')"
printf '%s' "$BODY" | grep -o '{[^{}]*"merchant":"Loja Inv[^{}]*}' | sed 's/"id":"[^"]*","userId":"[^"]*","authorName":"[^"]*",//; s/"description":"\(.\{40\}\)[^"]*"/"description":"\1..."/; s/^/GRAVADA: /'
caso "Dashboard do casal X apos entradas invalidas (total coerente com o que foi aceito?)"
req GET /api/v1/dashboard A
req GET "/api/v1/dashboard?startDate=1900-01-01T00:00:00Z&endDate=2100-01-01T00:00:00Z" A

############################ INGESTAO DE NOTIFICACOES (casal X) ############################
E=/api/v1/integrations/events
ev() { printf '{"bank":"%s","amount":%s,"currency":"BRL","eventTimestamp":"%s","description":%s,"merchant":%s,"rawNotificationText":%s}' "$1" "$2" "$3" "$4" "$5" "$6"; }
lista_x() { req GET "$T?pageSize=100" A >/dev/null; }
mostra() { printf '%s' "$BODY" | grep -o "{[^{}]*$1[^{}]*}" | sed 's/^/TRANSACAO: /'; }

caso "Status de integracao do casal X ANTES (ja ha transacoes manuais, nenhuma ingestao)"
req GET /api/v1/integrations/status A
caso "Criar transacao MANUAL e conferir se o status de integracao a conta como ingestao"
req POST $T A "$(tx 5.55 2026-10-04T08:00:00Z 'manual para status' 'Loja Status Manual' 'Casa')"
req GET /api/v1/integrations/status A
caso "Ingestao: evento valido NUBANK 45.90 no estabelecimento 'Supermercado Extra S2'"
req POST $E A "$(ev NUBANK 45.90 2026-10-04T14:30:00Z '"Compra aprovada"' '"Supermercado Extra S2"' '"Compra de R$ 45,90 APROVADA em Supermercado Extra S2"')"
caso "Ingestao: conferir transacao criada e categoria atribuida ao evento 45.90"
lista_x
echo "VERIFICACAO: transacoes com merchant 'Supermercado Extra S2' = $(conta '"merchant":"Supermercado Extra S2"')"
mostra '"merchant":"Supermercado Extra S2"'
caso "Ingestao: status depois de 1 evento aceito"
req GET /api/v1/integrations/status A
caso "Ingestao: MESMO evento enviado duas vezes em sequencia (Padaria Duplicada Seq, 12.34)"
DUP="$(ev NUBANK 12.34 2026-10-04T15:00:00Z '"Compra aprovada"' '"Padaria Duplicada Seq"' '"Compra de R$ 12,34 APROVADA em Padaria Duplicada Seq"')"
req POST $E A "$DUP"
req POST $E A "$DUP"
lista_x
echo "VERIFICACAO: transacoes com merchant 'Padaria Duplicada Seq' apos 2 envios em sequencia = $(conta '"merchant":"Padaria Duplicada Seq"')"
caso "Ingestao: MESMO evento enviado duas vezes em PARALELO (Padaria Duplicada Par, 23.45)"
PAR="$(ev NUBANK 23.45 2026-10-04T16:00:00Z '"Compra aprovada"' '"Padaria Duplicada Par"' '"Compra de R$ 23,45 APROVADA em Padaria Duplicada Par"')"
echo "\$ (curl -s -X POST '$BASE$E' -H 'Authorization: Bearer <token A>' -H 'Content-Type: application/json' -d '$PAR' &) x2 ; wait"
for k in 1 2; do
  printf '%s' "$PAR" | curl -s -X POST "$BASE$E" -H "Authorization: Bearer $TOK_A" -H 'Content-Type: application/json' --data-binary @- -w $'\nHTTP %{http_code}\n' > "$TMP/par$k.out" 2>&1 &
done
wait
for k in 1 2; do echo "--- resposta paralela $k:"; cat "$TMP/par$k.out"; done
lista_x
echo "VERIFICACAO: transacoes com merchant 'Padaria Duplicada Par' apos 2 envios em paralelo = $(conta '"merchant":"Padaria Duplicada Par"')"
caso "Ingestao: MESMO evento enviado 6 vezes em PARALELO (Padaria Duplicada Par6, 34.56)"
PAR6="$(ev NUBANK 34.56 2026-10-04T17:00:00Z '"Compra aprovada"' '"Padaria Duplicada Par6"' '"Compra de R$ 34,56 APROVADA em Padaria Duplicada Par6"')"
echo "\$ (curl -s -X POST '$BASE$E' -H 'Authorization: Bearer <token A>' -H 'Content-Type: application/json' -d '$PAR6' &) x6 ; wait"
for k in 1 2 3 4 5 6; do
  printf '%s' "$PAR6" | curl -s -X POST "$BASE$E" -H "Authorization: Bearer $TOK_A" -H 'Content-Type: application/json' --data-binary @- -w $'\nHTTP %{http_code}\n' > "$TMP/p6$k.out" 2>&1 &
done
wait
for k in 1 2 3 4 5 6; do echo "--- resposta paralela $k:"; cat "$TMP/p6$k.out"; done
lista_x
echo "VERIFICACAO: transacoes com merchant 'Padaria Duplicada Par6' apos 6 envios em paralelo = $(conta '"merchant":"Padaria Duplicada Par6"')"
caso "Ingestao: B (mesmo casal) envia o MESMO evento ja enviado por A (Padaria Duplicada Seq)"
req POST $E B "$DUP"
lista_x
echo "VERIFICACAO: transacoes com merchant 'Padaria Duplicada Seq' = $(conta '"merchant":"Padaria Duplicada Seq"')"
caso "Ingestao: C (casal Y) envia o MESMO evento do casal X — deve valer para Y sem afetar X"
req POST $E C "$DUP"
req GET "$T?category=Paginacao&pageSize=1" C | sed -n '2p'
req GET "$T?pageSize=100" C >/dev/null
echo "VERIFICACAO: transacoes 'Padaria Duplicada Seq' no casal Y = $(conta '"merchant":"Padaria Duplicada Seq"')"
caso "Ingestao: status depois das duplicidades"
req GET /api/v1/integrations/status A
caso "Ingestao: evento so com texto 'Compra R\$ 19,99 LOJA ABC' (sem merchant/descricao) — qual categoria?"
req POST $E A "$(ev NUBANK 19.99 2026-10-04T18:00:00Z null null '"Compra R$ 19,99 LOJA ABC"')"
lista_x; mostra '"amount":19.99'
caso "Ingestao: evento com descricao 'Compra R\$ 19,98 LOJA ABC' e merchant 'LOJA ABC' — qual categoria?"
req POST $E A "$(ev NUBANK 19.98 2026-10-04T18:01:00Z '"Compra R$ 19,98 LOJA ABC"' '"LOJA ABC"' '"Compra R$ 19,98 LOJA ABC"')"
lista_x; mostra '"amount":19.98'
caso "Ingestao: categorizacao automatica de estabelecimentos conhecidos (Uber, iFood, Drogaria, Posto, Netflix)"
i=0
for m in 'UBER *TRIP' 'IFOOD *RESTAURANTE' 'DROGARIA SAO PAULO' 'POSTO SHELL' 'NETFLIX.COM'; do
  i=$((i+1))
  req POST $E A "$(ev NUBANK "31.0$i" "2026-10-04T19:0$i:00Z" '"Compra aprovada"' "\"$m\"" "\"Compra aprovada em $m\"")"
done
lista_x
printf '%s' "$BODY" | grep -o '{[^{}]*"amount":31.0[0-9][^{}]*}' | sed 's/.*"amount":\([0-9.]*\).*"merchant":"\([^"]*\)","category":"\([^"]*\)".*/CATEGORIA: \2 (\1) -> \3/'
caso "Ingestao: eventTimestamp sem Z (2026-10-05T10:00:00)"
req POST $E A "$(ev NUBANK 21.00 2026-10-05T10:00:00 '"sem fuso"' '"Loja Evt SemFuso"' '"Compra R$ 21,00 Loja Evt SemFuso"')"
lista_x; mostra '"merchant":"Loja Evt SemFuso"'
caso "Ingestao: eventTimestamp com fuso -03:00 (2026-10-04T10:00:00-03:00)"
req POST $E A "$(ev NUBANK 22.00 2026-10-04T10:00:00-03:00 '"fuso brt"' '"Loja Evt BRT"' '"Compra R$ 22,00 Loja Evt BRT"')"
lista_x; mostra '"merchant":"Loja Evt BRT"'
caso "Ingestao: valor negativo (-45.90)"
req POST $E A "$(ev NUBANK -45.90 2026-10-04T14:31:00Z '"negativo"' '"Loja Evt Negativo"' '"Estorno"')"
caso "Ingestao: valor zero"
req POST $E A "$(ev NUBANK 0 2026-10-04T14:32:00Z '"zero"' '"Loja Evt Zero"' '"zero"')"
caso "Ingestao: valor com 3 casas (9.999)"
req POST $E A "$(ev NUBANK 9.999 2026-10-04T14:33:00Z '"3 casas"' '"Loja Evt 3casas"' '"x"')"
caso "Ingestao: valor 99999999999999999999"
req POST $E A "$(ev NUBANK 99999999999999999999 2026-10-04T14:34:00Z '"gigante"' '"Loja Evt Gigante"' '"x"')"
caso "Ingestao: valor texto (\"abc\")"
req POST $E A "$(ev NUBANK '"abc"' 2026-10-04T14:35:00Z '"texto"' '"Loja Evt Texto"' '"x"')"
caso "Ingestao: banco desconhecido (BANCOXYZ)"
req POST $E A "$(ev BANCOXYZ 8.00 2026-10-04T14:36:00Z '"banco desconhecido"' '"Loja Evt BancoXYZ"' '"x"')"
caso "Ingestao: banco em minusculas (nubank)"
req POST $E A "$(ev nubank 8.01 2026-10-04T14:37:00Z '"banco minusculo"' '"Loja Evt nubank"' '"x"')"
caso "Ingestao: banco vazio"
req POST $E A "$(ev '' 8.02 2026-10-04T14:38:00Z '"banco vazio"' '"Loja Evt BancoVazio"' '"x"')"
caso "Ingestao: moeda XYZ"
req POST $E A '{"bank":"NUBANK","amount":8.03,"currency":"XYZ","eventTimestamp":"2026-10-04T14:39:00Z","description":"moeda xyz","merchant":"Loja Evt XYZ","rawNotificationText":"x"}'
caso "Ingestao: moeda ausente"
req POST $E A '{"bank":"NUBANK","amount":8.04,"eventTimestamp":"2026-10-04T14:40:00Z","description":"sem moeda","merchant":"Loja Evt SemMoeda","rawNotificationText":"x"}'
caso "Ingestao: eventTimestamp ausente"
req POST $E A '{"bank":"NUBANK","amount":8.05,"currency":"BRL","description":"sem data","merchant":"Loja Evt SemData","rawNotificationText":"x"}'
caso "Ingestao: eventTimestamp futuro distante (2099-12-31)"
req POST $E A "$(ev NUBANK 8.06 2099-12-31T23:59:59Z '"futuro"' '"Loja Evt Futuro"' '"x"')"
caso "Ingestao: corpo {}"
req POST $E A '{}'
caso "Ingestao: JSON malformado"
req POST $E A '{"bank":"NUBANK","amount":'
caso "Ingestao: rawNotificationText com 20000 caracteres"
req POST $E A "$(ev NUBANK 8.07 2026-10-04T14:41:00Z '"raw longo"' '"Loja Evt RawLongo"' "\"$(rep 20000 r)\"")"
caso "Ingestao: merchant com 5000 caracteres"
req POST $E A "$(ev NUBANK 8.08 2026-10-04T14:42:00Z '"merchant longo"' "\"$(rep 5000 m)\"" '"x"')"
caso "Ingestao: descricao com 5000 caracteres"
req POST $E A "$(ev NUBANK 8.09 2026-10-04T14:43:00Z "\"$(rep 5000 d)\"" '"Loja Evt DescLonga"' '"x"')"
caso "Ingestao: outros bancos (ITAU, BRADESCO, INTER, C6, BANCO DO BRASIL)"
i=0
for b in ITAU BRADESCO INTER C6 'BANCO DO BRASIL'; do i=$((i+1)); req POST $E A "$(ev "$b" "7.0$i" "2026-10-04T13:0$i:00Z" '"compra"' "\"Loja Evt Banco $i\"" '"x"')"; done
caso "Ingestao: status final do casal X e conferencia do que foi gravado a partir de eventos"
req GET /api/v1/integrations/status A
req GET "$T?pageSize=100" A
echo "VERIFICACAO: transacoes 'Loja Evt*' gravadas = $(conta '"merchant":"Loja Evt'); com source Manual = $(conta '"source":"Manual"'); itens na pagina = $(conta '"id":')"
printf '%s' "$BODY" | grep -o '{[^{}]*"merchant":"Loja Evt[^{}]*}' | sed 's/"id":"[^"]*","userId":"[^"]*","authorName":"[^"]*",//; s/"description":"\(.\{40\}\)[^"]*"/"description":"\1..."/; s/^/GRAVADA: /'
caso "Ingestao: excluir (A) a transacao criada por notificacao e reenviar o mesmo evento"
lista_x
TXN="$(printf '%s' "$BODY" | grep -o '{[^{}]*"merchant":"Supermercado Extra S2"[^{}]*}' | grep -o '"id":"[^"]*"' | head -1 | cut -d'"' -f4)"; echo "TXN=$TXN"
req DELETE $T/$TXN A
req POST $E A "$(ev NUBANK 45.90 2026-10-04T14:30:00Z '"Compra aprovada"' '"Supermercado Extra S2"' '"Compra de R$ 45,90 APROVADA em Supermercado Extra S2"')"
lista_x
echo "VERIFICACAO: transacoes com merchant 'Supermercado Extra S2' apos excluir e reenviar = $(conta '"merchant":"Supermercado Extra S2"')"
caso "Dashboard final do casal X (mes corrente e periodo amplo) e do casal Y"
req GET /api/v1/dashboard A
req GET "/api/v1/dashboard?startDate=1900-01-01T00:00:00Z&endDate=2100-01-01T00:00:00Z" A
req GET /api/v1/dashboard C
caso "Formato de erro: rota inexistente e metodo nao permitido"
req GET /api/v1/transactions/export/naoexiste A
req PUT $T A '{}'
req DELETE $T A

printf '\nFim: %s   Total de casos: %d\n' "$(date '+%Y-%m-%d %H:%M:%S %z')" "$N"
echo "Total de casos: $N" >&2
