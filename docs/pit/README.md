# Projeto Integrador Transdisciplinar em Engenharia de Software

**Projeto:** CoupleSync — gestão financeira compartilhada para casais e famílias
**Aluno:** Lucas Henrique Morais Salomão
**Curso:** Engenharia de Software — Universidade Cruzeiro do Sul
**Repositório:** <https://github.com/lucasmoraiss/CoupleSync>

Esta pasta reúne a documentação do projeto: o planejamento (visão, requisitos, casos de uso, modelagem), o projeto técnico (arquitetura, interface) e a garantia de qualidade (plano de testes e laudo).

## Documentos

| # | Documento | Conteúdo | Tema do material da disciplina |
|---|---|---|---|
| 1 | [Visão e escopo](01-visao-e-escopo.md) | Problema, proposta, público, objetivos, escopo e o que mudou desde o planejamento | Revisão e revalidação da ideia |
| 2 | [Requisitos](02-requisitos.md) | 39 requisitos funcionais, 18 regras de negócio e 17 requisitos não funcionais, com situação de cada um | Requisitos e sua validação |
| 3 | [Casos de uso](03-casos-de-uso.md) | Atores, diagrama e descrição dos seis casos de uso centrais | Modelagem comportamental (UML) |
| 4 | [Modelagem de dados](04-modelagem-de-dados.md) | Diagrama de classes, DER, mapeamento para o modelo relacional, projeto físico e dicionário de dados | Banco de dados e diagrama de classes |
| 5 | [Arquitetura](05-arquitetura.md) | Camadas, correspondência com o MVC, caminho de uma requisição, decisões, segurança e implantação | Construção em camadas e padrão MVC |
| 6 | [Interface e experiência do usuário](06-ihc-ux.md) | Arquitetura da informação, identidade visual, os oito princípios de interface aplicados, acessibilidade e privacidade | IHC e UX |
| 7 | [Plano de testes](07-plano-de-testes.md) | Verificação e validação, Modelo V, ferramentas, inventário, cobertura e matriz de rastreabilidade | Teste de software |
| 8 | [Laudo de qualidade](08-laudo-de-qualidade.md) | Método, erros encontrados, correções com evidência e pendências | Teste de software e qualidade |

Documentos de apoio, fora desta pasta: [registros de decisão de arquitetura](../adr/README.md), [guia de uso](../guia-de-uso.md), [guia de implantação](../deployment/DEPLOY-GUIDE.md) e [guia do desenvolvedor](../dev-guide.md).

## A solução em resumo

| Item | Descrição |
|---|---|
| Linguagem do back-end | C# (.NET 8, ASP.NET Core Web API) |
| Banco de dados | PostgreSQL 16 |
| Front-end | Aplicativo Android em React Native com Expo (TypeScript) |
| Hospedagem | Render (API em contêiner Docker) e Neon (banco) |
| Modo de codificação | Tradicional |
| Testes automatizados | 592 na API e 102 no aplicativo |

## Histórico de revisão

| Data | Revisão |
|---|---|
| abr/2026 | Planejamento inicial: documento de requisitos de produto e primeiros registros de decisão de arquitetura |
| abr–mai/2026 | Desenvolvimento do piloto: autenticação, grupo, captura de notificações, metas, orçamento, importação de extrato, relatórios |
| out/2026 | Revisão para a entrega. A documentação foi reescrita a partir do sistema real; os requisitos foram confrontados com o sistema em execução; a revisão de código e as sessões de teste geraram um ciclo de 17 correções, descrito no laudo de qualidade |

### O que mudou nesta revisão

- **Documentação reescrita a partir do código.** O modelo de dados deste conjunto vem das migrations e do banco em execução, não do planejamento, que previa entidades que não chegaram a existir.
- **Requisitos com situação declarada.** Cada requisito está marcado como atendido, parcial ou planejado, com a limitação descrita.
- **Segurança.** Uma chave de assinatura que estava no repositório foi removida, e a API passou a exigir o segredo por variável de ambiente e a limitar tentativas de acesso.
- **Privacidade.** O aplicativo deixou de enviar ao servidor o texto das notificações bancárias.
- **Hospedagem.** A API saiu do Azure App Service e passou para o Render.
- **Repositório.** Um framework de terceiros que estava versionado foi removido, e os registros de decisão foram reunidos em `docs/adr`.
