# Guia de uso — CoupleSync

O CoupleSync é um aplicativo Android para acompanhar os gastos de um casal ou de uma família em um só lugar. Os gastos entram de três formas: pela leitura das notificações do aplicativo do banco, por lançamento manual e pela importação de um extrato em PDF.

Este guia descreve o que o aplicativo faz hoje. O CoupleSync é um projeto acadêmico em fase de piloto; a seção [O que o aplicativo ainda não faz](#13-o-que-o-aplicativo-ainda-não-faz) lista os limites conhecidos.

No aplicativo e neste guia, o grupo de pessoas que compartilha os dados é chamado de **casal**, mas ele aceita mais de dois membros.

---

## 1. Criar conta e entrar

**Criar conta**

1. Na tela inicial, toque em **Cadastre-se**.
2. Preencha **Nome**, **E-mail**, **Senha** (mínimo de 8 caracteres) e **Confirmar senha**.
3. Toque em **Criar conta**.

Não há confirmação por e-mail: a conta passa a valer na hora. Confira o endereço digitado, porque ele não pode ser alterado depois.

**Entrar**

1. Informe **E-mail** e **Senha**.
2. Toque em **Entrar**.

A sessão fica guardada no aparelho e é renovada sozinha enquanto você usa o aplicativo. Depois de um período longo sem uso (7 dias, na configuração padrão do servidor), aparece o aviso "Sua sessão expirou. Entre novamente."

Login e cadastro aceitam até 5 tentativas por minuto. Passando disso, aguarde um minuto.

**Sair:** aba **Config** > **Sair da conta**.

---

## 2. Criar o casal e convidar

Depois do cadastro, o aplicativo abre a tela **Vamos configurar**, com duas opções.

**Criar casal** (primeira pessoa)

1. Toque em **Criar casal**.
2. A tela **Casal criado!** mostra o **Código de convite**, com 6 letras e números.
3. Toque em **Copiar código** e envie para quem vai participar.
4. Toque em **Ir para o Dashboard**.

> **Guarde o código.** Ele só é exibido nessa tela. O item "Código do casal" em Config ainda não está ativo.

**Entrar em um casal** (demais pessoas)

1. Cada pessoa cria a própria conta.
2. Na tela **Vamos configurar**, toque em **Entrar em um casal**.
3. Digite o código e toque em **Entrar**.

O mesmo código vale para todos os convidados. Um membro que já estava no casal recebe um aviso de que alguém entrou, se os alertas por push estiverem funcionando.

Cada conta pertence a um único casal. Se você tentar criar ou entrar em outro, o aplicativo avisa "Você já faz parte de um casal."

---

## 3. Painel (aba Dashboard)

Mostra o resumo do **mês corrente**, do dia 1 até hoje:

- **Total de gastos** e a quantidade de transações;
- um cartão com o total de cada membro (o seu aparece como **Você**);
- **Por categoria**, do maior gasto para o menor.

Os atalhos **Ver rendas** e **Ver todas as transações** levam às respectivas telas. Arraste a tela para baixo para atualizar.

---

## 4. Transações

A aba **Transações** lista as 20 transações mais recentes do casal e mostra, no topo, quantas existem no total. Cada linha traz o estabelecimento ou a descrição, a categoria, a origem (**Notificação**, **Manual** ou **OCR**, para as importadas de extrato), quem registrou, o valor e a data.

**Lançar manualmente**

1. Toque em **Nova**.
2. Preencha **Valor (R$)**, e, se quiser, **Descrição** e **Estabelecimento**.
3. Escolha a **Categoria**.
4. Toque em **Salvar**.

A transação é gravada com a data e a hora do momento do lançamento.

**Trocar a categoria**

Toque na transação e escolha a nova categoria em **Editar categoria**. As categorias são: Alimentação, Transporte, Compras, Saúde, Lazer, Moradia e Outros.

**Excluir**

Toque no ícone da lixeira (ou segure o dedo sobre a transação) e confirme em **Excluir**. Qualquer membro do casal pode excluir qualquer transação do casal. A exclusão não pode ser desfeita.

Valor, descrição e data de uma transação já gravada não podem ser editados. Para corrigir, exclua e lance de novo.

---

## 5. Captura de notificações do banco

Com a sua autorização, o CoupleSync lê as notificações dos aplicativos de banco no seu aparelho e registra as despesas sozinho.

**Bancos reconhecidos:** Nubank, Itaú, Inter, C6 Bank e Bradesco.

**O que vira transação:** compras, pagamentos e Pix **enviados**. Pix recebido, transferências recebidas, estornos, depósitos, compras negadas e mensagens de propaganda são ignorados. Notificações em um formato que o aplicativo não conhece também são ignoradas.

**Como ativar**

1. Abra a aba **Config** e toque em **Notificações do sistema** (ou, na aba Transações, toque em **Ativar** na faixa "Captura de notificações desativada").
2. O Android abre a tela de acesso a notificações. Ative o **CoupleSync** e confirme.
3. Mantenha as notificações do aplicativo do banco ligadas.

**Como desativar**

Volte à mesma tela do Android (Config > **Notificações do sistema**) e desative o CoupleSync. A partir daí nada mais é lido.

**O que é e o que não é enviado**

O texto da notificação é analisado no próprio aparelho. Para cada despesa reconhecida, o aplicativo envia ao servidor somente:

- o banco;
- o valor;
- a moeda (sempre real);
- a data e a hora da notificação;
- o estabelecimento — em um Pix enviado, o nome de quem recebeu.

**Não** são enviados o texto completo da notificação, o seu saldo, nem notificações de outros aplicativos. O CoupleSync nunca pede a senha do banco e não se conecta à sua conta bancária.

**Limites:** cada pessoa precisa ativar a captura no próprio aparelho. As notificações são processadas com o aplicativo aberto; se o Android encerrar o CoupleSync, as que chegarem enquanto ele estiver fechado podem não ser registradas. A mesma despesa não é gravada duas vezes.

---

## 6. Importar extrato em PDF

Serve para trazer de uma vez as transações de um extrato.

1. Na aba **Transações**, toque no ícone de nuvem com seta (no topo), ou em **Importar extrato** quando a lista estiver vazia.
2. Toque em **Arquivo PDF** e escolha o extrato salvo no aparelho (até 10 MB).
3. Aguarde as mensagens "Enviando arquivo...", "Aguardando processamento..." e "Processando extrato...".
4. Na tela **Revisão de Importação**, confira cada linha:
   - marque ou desmarque as que quer importar (**Selecionar Todos** / **Desmarcar Todos**);
   - corrija a **Descrição** e o valor, se preciso;
   - ajuste a **Categoria**;
   - linhas que parecem repetir uma transação já gravada vêm com o aviso **Possível duplicata**.
5. Toque em **Confirmar Importação**.

Todas as transações do aplicativo contam como gasto. Desmarque as linhas do extrato que não forem despesas. Linhas idênticas a transações que já existem são puladas na gravação.

**Requisitos do arquivo**

- Só PDF. O aplicativo não importa foto nem imagem.
- O PDF precisa ter texto selecionável (extrato digital). PDF digitalizado como imagem não é lido.
- PDF protegido por senha não é aceito: exporte sem senha.
- Formatos de extrato reconhecidos: Nubank, Inter, Banco do Brasil, Itaú, Santander, Caixa e Mercantil. Para outros bancos aparece "Formato do banco não reconhecido".

O arquivo PDF é apagado do servidor assim que o processamento termina. Ficam guardadas as linhas extraídas para a revisão e as transações que você confirmou.

---

## 7. Rendas (aba Rendas)

A tela **Fontes de Renda** mostra as rendas do mês corrente e a **Renda Total do Casal**, separadas em **Minhas Rendas**, **Rendas do Parceiro** e **Rendas Compartilhadas**.

**Adicionar**

1. Toque em **Adicionar fonte de renda**.
2. Informe o nome (por exemplo, Salário) e o valor mensal.
3. Se quiser, marque **Renda compartilhada (editável por ambos)**.
4. Deixe **Renda recorrente** ligado para rendas fixas; desligado, ela aparece em **Rendas Extras**.
5. Toque em **Adicionar**.

**Editar ou remover:** use os ícones ao lado de cada fonte. Você altera as suas rendas e as compartilhadas; as rendas pessoais dos outros membros aparecem só para consulta.

---

## 8. Metas (aba Metas)

**Criar**

1. Toque no botão de adicionar e preencha **Título**, **Descrição** (opcional), **Valor alvo (R$)** e **Prazo** (DD/MM/AAAA, de hoje em diante).
2. Toque em **Criar meta**.

**Acompanhar:** cada meta mostra uma barra de progresso com o valor atual, o valor alvo e o prazo. Ao chegar a 100%, aparece o selo **Meta atingida**.

**Atualizar o progresso:** toque em **Editar**, altere o **Valor atual (R$)** e toque em **Salvar alterações**. O progresso é informado por você; o aplicativo não o calcula a partir das transações.

**Excluir:** toque em **Excluir** e confirme. Não pode ser desfeito.

As metas são do casal: todos os membros veem e podem alterar.

---

## 9. Fluxo de caixa (aba Fluxo)

Mostra uma projeção de despesas a partir do que já foi gasto. Escolha **30 dias** ou **90 dias** e veja:

- **Gasto histórico** no período;
- **Média diária**;
- **Projeção** para os próximos 30 ou 90 dias;
- a divisão **Por categoria**;
- as **Premissas do cálculo**.

É uma estimativa simples, baseada na média do período. Ela não considera rendas, contas futuras nem saldo bancário.

---

## 10. Relatórios (aba Relatórios)

Escolha o período (**3m**, **6m** ou **12m**) e consulte:

- **Gastos por categoria**, em gráfico de pizza;
- **Gastos mensais**, em gráfico de barras;
- **Progresso das Metas**.

---

## 11. Alertas

Em **Config** > **Alertas** há três chaves:

| Chave | O que acontece hoje |
|---|---|
| **Transação grande** | Avisa quando uma transação sua passa de R$ 500. |
| **Saldo baixo** | Avisa quando os gastos do casal nos últimos 30 dias passam de R$ 3.000. Apesar do nome, o aplicativo não conhece o seu saldo bancário. |
| **Lembretes de contas** | A chave existe, mas nenhum lembrete é enviado ainda. |

Os alertas são avaliados sempre que uma transação é registrada (por notificação, manualmente ou por extrato) e chegam como notificação no aparelho de quem a registrou. Para recebê-los, permita as notificações do CoupleSync quando o Android perguntar. O texto de alguns alertas ainda está em inglês.

O envio depende de o serviço de push estar configurado no servidor e no aplicativo instalado. Se não estiver, os alertas não chegam, e o restante do aplicativo funciona normalmente.

---

## 12. Assistente (aba Chat IA) e Configurações

**Assistente.** A aba **Chat IA** só aparece se o assistente estiver habilitado na versão instalada. Nela você faz perguntas em texto sobre gastos, rendas e metas do casal. O limite é de 30 perguntas por hora por casal, e a conversa não fica guardada: ao fechar o aplicativo, ela some. As respostas são geradas por um modelo de inteligência artificial e podem conter erros; não são aconselhamento financeiro.

**Configurações.** A aba **Config** tem:

- **Notificações do sistema** — abre a tela do Android para ativar ou desativar a captura de notificações;
- **Alertas** — as três chaves da seção anterior;
- **Código do casal** — item ainda inativo;
- **Sair da conta**.

---

## 13. O que o aplicativo ainda não faz

- Recuperar ou trocar a senha, e alterar nome ou e-mail.
- Confirmar o e-mail no cadastro.
- Mostrar o código de convite depois da tela de criação do casal.
- Sair do casal, remover um membro, trocar o código de convite ou participar de mais de um casal.
- Apagar a conta ou exportar os dados pelo aplicativo.
- Editar valor, descrição ou data de uma transação já gravada.
- Buscar ou filtrar transações, e ver além das 20 mais recentes na lista.
- Registrar dinheiro que entra a partir de notificações (só despesas são capturadas).
- Importar foto ou imagem de extrato, e ler PDF com senha.
- Definir orçamento por categoria pelo aplicativo.
- Enviar lembretes de contas a pagar.
- Conectar-se diretamente ao banco.
- Funcionar em iPhone.

---

## 14. Privacidade

O CoupleSync é um projeto acadêmico em piloto. **Ainda não há política de privacidade publicada nem tela de consentimento no aplicativo.** Leve isso em conta antes de registrar dados reais.

**O que é coletado**

- Nome, e-mail e senha (a senha é guardada de forma irreversível, como hash).
- As transações: valor, data e hora, estabelecimento, descrição, categoria, banco de origem e quem registrou.
- As rendas e as metas que você cadastrar.
- As preferências de alerta e o identificador de notificações push do aparelho.
- Das notificações do banco, apenas os campos listados na [seção 5](#5-captura-de-notificações-do-banco).

O aplicativo não coleta dados de uso para estatística nem publicidade.

**Quem vê**

Todos os membros do casal veem todas as transações, rendas e metas do casal, incluindo o nome de quem registrou cada transação. Quem tem o código de convite consegue entrar no casal e ver tudo; compartilhe-o só com quem deve participar. Quem administra o servidor e o banco de dados tem acesso técnico aos dados.

**O que vai a terceiros**

- **Hospedagem:** os dados ficam em um banco de dados PostgreSQL no serviço Neon, e a API roda no serviço Render.
- **Notificações push:** o título e o texto de cada alerta, que podem incluir valores, passam pelo Firebase Cloud Messaging, do Google.
- **Assistente e categorização automática** (somente se a inteligência artificial estiver habilitada): a sua pergunta e um resumo das finanças do casal (renda, gastos por categoria dos últimos 30 dias e metas) são enviados à API Gemini, do Google. Na importação de extrato, as descrições das linhas também são enviadas para sugerir a categoria.
- **Atualizações do aplicativo:** são distribuídas pelos serviços da Expo.

Não há venda nem compartilhamento de dados para publicidade.

**O que ainda falta**

Não existem, no aplicativo, as funções de apagar a conta, exportar os dados ou sair do casal. Para pedir a remoção dos seus dados durante o piloto, fale com a pessoa responsável pelo projeto que lhe passou o aplicativo.
