// Prepara, pela API de teste, a conta do fluxo 09: uma pessoa com grupo e três cobranças mensais iguais de um
// estabelecimento inventado ("Streaming E2E", R$ 39,90, há 61, 31 e 1 dia) — o mínimo para o servidor reconhecer
// uma assinatura. Roda no Maestro (na máquina que dirige o emulador), por isso fala com a API em 127.0.0.1.
// Nenhum dado real: e-mail em example.com, estabelecimento genérico.
// API_URL vem de `maestro test -e API_URL=...` (scripts/app-e2e-run-flows.sh); sem ele, a porta padrão da API de teste.
var base = typeof API_URL !== 'undefined' && API_URL ? API_URL : 'http://127.0.0.1:5000';
var email = 'e2e-09-' + Date.now() + '@example.com';
var password = SENHA;

function post(path, token, body) {
  var headers = { 'Content-Type': 'application/json' };
  if (token) headers['Authorization'] = 'Bearer ' + token;
  var response = http.post(base + path, { headers: headers, body: JSON.stringify(body) });
  if (!response.ok) throw new Error('POST ' + path + ' -> ' + response.status + ' ' + response.body);
  return json(response.body);
}

var registered = post('/api/v1/auth/register', null, { email: email, name: 'Rita Teste', password: password });
var group = post('/api/v1/couples', registered.accessToken, {});
var token = group.accessToken;

var day = 24 * 60 * 60 * 1000;
[61, 31, 1].forEach(function (daysAgo) {
  post('/api/v1/transactions', token, {
    amount: 39.9,
    currency: 'BRL',
    eventTimestampUtc: new Date(Date.now() - daysAgo * day).toISOString(),
    merchant: 'Streaming E2E',
    category: 'LAZER',
  });
});

output.email = email;
