# -*- coding: utf-8 -*-
"""Confere a resposta de /ocr/{id}/results (salva em .tmp/body) contra o conteudo do PDF (a)."""
import json

d = json.load(open('.tmp/body', encoding='utf-8'))
esp = [
    ("2026-09-02", "Padaria Trigo Dourado", 18.40),
    ("2026-09-03", "Supermercado Bom Lar", 1234.56),
    ("2026-09-05", "Farmacia Vida Leve", 47.35),
    ("2026-09-08", "Posto Estrada Azul", 210.00),
    ("2026-09-11", "Cinema Tela Grande", 64.00),
    ("2026-09-14", "Livraria Pagina Nova", 89.90),
    ("2026-09-19", "Restaurante Sabor da Serra", 152.75),
]
c = d.get('candidates', [])
print("Conferencia: o PDF tem 7 debitos + 1 credito (21/09/2026 Estorno Loja Girassol + R$ 30,00). Candidatos retornados:", len(c))
for i, e in enumerate(esp):
    if i >= len(c):
        print(f"  [{i}] FALTANDO: esperado {e}")
        continue
    x = c[i]
    ok = x['date'][:10] == e[0] and x['description'] == e[1] and abs(x['amount'] - e[2]) < 0.005
    print(f"  [{x['index']}] {'OK ' if ok else 'DIF'} obtido=({x['date']}, {x['description']!r}, {x['amount']}) esperado={e} "
          f"categoriaSugerida={x['suggestedCategory']!r} duplicata={x['duplicateSuspected']} confianca={x['confidence']} moeda={x['currency']}")
for x in c[len(esp):]:
    print("  EXTRA:", x)
print("  Credito (estorno) presente nos candidatos?", any('Estorno' in x['description'] for x in c))
