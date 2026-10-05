# -*- coding: utf-8 -*-
"""Gera os arquivos de entrada da sessao 5 (todos os dados sao INVENTADOS).
Uso: python gerar_arquivos.py   (requer reportlab: pip install --user reportlab)
"""
import os, struct, zlib
from reportlab.pdfgen import canvas
from reportlab.lib.pagesizes import A4
from reportlab.lib.pdfencrypt import StandardEncryption

HERE = os.path.dirname(os.path.abspath(__file__))

def pdf(nome, linhas, encrypt=None, pagina_por_linha=False):
    c = canvas.Canvas(os.path.join(HERE, nome), pagesize=A4, encrypt=encrypt, pageCompression=0)
    c.setFont("Helvetica", 11)
    y = 800
    for l in linhas:
        c.drawString(40, y, l)
        y -= 18
        if pagina_por_linha or y < 60:
            c.showPage(); c.setFont("Helvetica", 11); y = 800
    c.save()

CAB_INTER = ["Banco Inter S.A.", "Fatura do cliente Marina Teste Exemplo", "Final do plastico 1234****5678"]

# (a) fatura valida: 7 debitos distintos + 1 credito (estorno)
A = CAB_INTER + [
    "02/09/2026 Padaria Trigo Dourado R$ 18,40",
    "03/09/2026 Supermercado Bom Lar R$ 1.234,56",
    "05/09/2026 Farmacia Vida Leve R$ 47,35",
    "08/09/2026 Posto Estrada Azul R$ 210,00",
    "11/09/2026 Cinema Tela Grande R$ 64,00",
    "14/09/2026 Livraria Pagina Nova R$ 89,90",
    "19/09/2026 Restaurante Sabor da Serra R$ 152,75",
    "21/09/2026 Estorno Loja Girassol + R$ 30,00",
]
pdf("a-fatura-inter-valida.pdf", A)

# (b) igual, com DUAS linhas identicas no mesmo dia (duas corridas de R$ 12,90)
B = CAB_INTER + [
    "02/09/2026 Mercearia Canto Verde R$ 23,10",
    "04/09/2026 Corrida App Vai Rapido R$ 12,90",
    "04/09/2026 Corrida App Vai Rapido R$ 12,90",
    "06/09/2026 Lanchonete Ponto Certo R$ 31,50",
    "09/09/2026 Pet Shop Patas Felizes R$ 76,80",
    "13/09/2026 Academia Corpo Ativo R$ 99,00",
    "17/09/2026 Floricultura Jardim Sol R$ 45,25",
]
pdf("b-fatura-inter-linhas-identicas.pdf", B)

# (c) banco nao suportado
pdf("c-banco-nao-suportado.pdf", [
    "Cooperativa Financeira Ficticia do Vale - Extrato mensal",
    "Cliente: Joaquim Exemplo da Silva   Conta 00000-0",
    "03/09/2026 Compra Mercado Feliz 55,10",
    "07/09/2026 Conta de luz 143,22",
    "12/09/2026 Assinatura streaming 29,90",
])

# (d) protegido por senha (mesmo layout Inter)
pdf("d-protegido-senha.pdf", CAB_INTER + ["10/09/2026 Loja Protegida R$ 10,00", "11/09/2026 Loja Trancada R$ 20,00"],
    encrypt=StandardEncryption("segredo123", ownerPassword="dono456", canPrint=0))

# (e) PDF vazio (uma pagina em branco, sem texto)
pdf("e-vazio.pdf", [])

# (f) .txt renomeado para .pdf
with open(os.path.join(HERE, "f-texto-renomeado.pdf"), "w", encoding="utf-8") as f:
    f.write("Banco Inter S.A.\n02/09/2026 Isto e apenas um arquivo de texto R$ 10,00\n")

# (g) PNG pequeno 8x8
def png(nome, w=8, h=8):
    raw = b"".join(b"\x00" + bytes([200, 60, 60]) * w for _ in range(h))
    def chunk(t, d):
        return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d) & 0xffffffff)
    data = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0)) \
        + chunk(b"IDAT", zlib.compress(raw)) + chunk(b"IEND", b"")
    open(os.path.join(HERE, nome), "wb").write(data)
png("g-imagem.png")

# (h) 11 MB: PDF valido (a) seguido de preenchimento ate 11 MB
base = open(os.path.join(HERE, "a-fatura-inter-valida.pdf"), "rb").read()
with open(os.path.join(HERE, "h-11mb.pdf"), "wb") as f:
    f.write(base); f.write(b"\n%" + b"0" * (11 * 1024 * 1024 - len(base) - 2))

# (k) PDF corrompido: cabecalho %PDF e lixo
open(os.path.join(HERE, "k-corrompido.pdf"), "wb").write(b"%PDF-1.4\n" + b"isto nao e um pdf de verdade\n" * 40)

# (i) Nubank, uma linha por lancamento na MESMA pagina (formato dos testes unitarios)
NU = ["Nu Pagamentos S.A. - Extrato",
      "03/09/2026 Mercado Horta Fresca -R$ 58,20",
      "06/09/2026 Transporte Metro Leste -R$ 9,80",
      "10/09/2026 Sorveteria Polo Norte -R$ 22,00"]
pdf("i-nubank-uma-pagina.pdf", NU)
# (i2) Nubank, cada linha em uma pagina separada
pdf("i2-nubank-pagina-por-linha.pdf", NU, pagina_por_linha=True)

# (j) Inter somente com creditos
pdf("j-inter-so-creditos.pdf", CAB_INTER + [
    "05/09/2026 Reembolso Empresa Ficticia + R$ 500,00",
    "15/09/2026 Devolucao Loja Aurora + R$ 80,00"])

# (m) Inter com compra parcelada ("03/10" na descricao) e extrato com sufixo D/C
pdf("m-inter-parcelado.pdf", CAB_INTER + [
    "07/09/2026 Magazine Casa Bela Parcela 03/10 R$ 99,90",
    "09/09/2026 Otica Visao Clara 02/06 lentes R$ 150,00",
    "12/09/2026 Papelaria Risco Fino R$ 15,00 D",
    "13/09/2026 Deposito Tia Fulana R$ 300,00 C"])

# (v1..v9) faturas pequenas e distintas para os casos de confirmacao
lojas = ["Aurora", "Baleia", "Cometa", "Dunas", "Estrela", "Farol", "Gaivota", "Horizonte", "Ilha"]
for i, loja in enumerate(lojas, start=1):
    pdf(f"v{i}-inter-confirmacao.pdf", CAB_INTER + [
        f"0{i}/08/2026 Emporio {loja} Um R$ {10+i},11",
        f"1{i}/08/2026 Emporio {loja} Dois R$ {20+i},22",
        f"2{i}/08/2026 Emporio {loja} Tres R$ {30+i},33"])

# (n) Banco do Brasil, 3 lancamentos em linhas da mesma pagina (formato dos testes unitarios)
pdf("n-bb-uma-pagina.pdf", ["Banco do Brasil S.A. - Extrato de conta",
    "04/09/2026 Quitanda Folha Verde 34,60 D",
    "08/09/2026 Barbearia Corte Fino 45,00 D",
    "15/09/2026 Lavanderia Bolha Azul 28,90 D"])
# (o) Itau, 3 lancamentos em linhas da mesma pagina (formato dos testes unitarios)
pdf("o-itau-uma-pagina.pdf", ["Itau Unibanco S.A. - Extrato mensal",
    "04/09 Peixaria Mar Aberto 61,30-",
    "09/09 Chaveiro Porta Segura 25,00-",
    "16/09 Doceria Mel e Canela 19,75-"])
print("ok")
