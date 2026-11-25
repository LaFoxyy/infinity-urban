# Reteste após o retorno da Channex — 29 de outubro de 2026

O Full Sync foi aprovado pela Channex. Corrigir o software não modifica as requisições antigas nem seus IDs. Os cenários rejeitados precisam de novas ações na interface normal do PMS e de novos IDs aceitos.

## Executar e conferir um lote

1. Escolha a propriedade USD correta e aguarde a fila **dessa propriedade** ficar vazia.
2. Abra **Calendar → Edit dates → Rates & restrictions** ou **Open inventory**.
3. Selecione quarto/plano, datas e somente os campos pedidos. Datas finais de calendário são inclusivas; saídas de reservas são exclusivas.
4. Para mínimas iguais, mantenha **Use the same minimum stay for arrival and through** marcado. Desmarque apenas quando o negócio realmente exigir regras diferentes.
5. Use **Add change to batch** para cada linha. Confira propriedade, quarto, plano, datas e campos no resumo antes de confirmar.
6. Para reaplicar valores já salvos, marque **Reapply selected values to channels**. Isso reenvia somente a seleção explícita; não é necessário alterar preços artificialmente e desfazer.
7. Revise e confirme o lote uma vez. Em **Sync history**, confira a requisição aceita e copie seu **Channex task ID** para o cenário correspondente. Não copie JSON ou IDs de propriedade/quarto/plano.

Erros de datas e planos não se resolvem trocando um ID no formulário: é necessário executar a operação correta e usar o ID dessa operação.

## Conferir os mapeamentos usados na submissão

Estes IDs são referência de conferência, não constantes do código de produção.

| Plano | Channex rate plan ID |
|---|---|
| Twin Room · Best Available Rate | `21e4998b-4aa3-432f-9a79-91f2c4ddd4e2` |
| Twin Room · Bed & Breakfast Rate | `0514e970-855c-49dd-a8cc-e7bfe9e3bdd0` |
| Double Room · Best Available Rate | `5c2b645e-1817-4d57-a2ab-60b9ccc35239` |
| Double Room · Bed & Breakfast Rate | `36f55f69-6de5-4d25-aa5c-8da605e8a287` |

## Valores dos cenários

Todas as datas são de **2026**, exceto o término do cenário 8.

| Caso | Linhas no mesmo lote | Campos enviados |
|---|---|---|
| 2 | Twin Best Available: **22/11 = USD 333** | Somente `rate`; uma chamada |
| 3 | Twin Best Available **21/11 = 333**; Double Best Available **25/11 = 444**; Double Bed & Breakfast **29/11 = 456.23** | Somente `rate`; uma chamada |
| 4 | Twin Best Available **01–10/11 = 241**; Double Best Available **10–16/11 = 312.66**; Double Bed & Breakfast **01–20/11 = 111** | Somente `rate`; uma chamada, sequências iguais agrupadas |
| 5 | Twin Best Available **23/11 = 3**; Double Best Available **25/11 = 2**; Double Bed & Breakfast **15/11 = 5** | Somente as duas mínimas, iguais; uma chamada |
| 6 | Habilitar stop sell: Twin Best Available **14/11**; Double Best Available **16/11**; Double Bed & Breakfast **20/11** | Somente `stop_sell: true`; uma chamada |
| 7 | Tabela abaixo | Somente as restrições selecionadas; uma chamada |
| 8 | **01/12/2026–01/05/2027**: Twin Best Available **432**, mínimas **2**, chegada/saída abertas; Double Best Available **342**, mínimas **3** | Twin: preço, duas mínimas, CTA false e CTD false. Double: preço e duas mínimas; uma chamada |
| 9 | Twin **21/11 = 7**; Double **25/11 = 0** | Somente disponibilidade; uma ou duas chamadas |
| 10 | Twin **10–16/11 = 3**; Double **17–24/11 = 4** | Somente disponibilidade; uma ou duas chamadas, sequências iguais agrupadas |

### Caso 7

| Plano | Intervalo | Seleção |
|---|---|---|
| Twin Best Available | 01–10/11/2026 | CTA **true**, CTD **false**, máxima **4**, duas mínimas **1** |
| Twin Bed & Breakfast | 12–16/11/2026 | CTA **false**, CTD **true**, duas mínimas **6** |
| Double Best Available | 10–16/11/2026 | CTA **true**, duas mínimas **2** |
| Double Bed & Breakfast | 01–20/11/2026 | Duas mínimas **10** |

Campos não pedidos ficam em branco ou **Unchanged**, inclusive preço e stop sell.

### Inventário e unidades únicas

Disponibilidade é capacidade aberta menos reservas ativas. **Open inventory** é a capacidade antes da subtração; não pode ficar abaixo das reservas existentes. O email aceitou disponibilidade zero como adaptação de unidades únicas. Se os quartos continuarem com uma unidade, descreva essa adaptação no formulário e confirme com a Channex. Não invente quantidades para obter o número esperado. As **datas continuam obrigatórias** nessa adaptação.

## Caso 11: novo ciclo de reserva

Use a interface **Booking CRS** da Channex. Não consulte a lista de reservas/revisões nem revisões por ID para obter os IDs do teste.

1. Crie uma reserva em propriedade/quarto/plano mapeados.
2. Em Stayline, use **Channex & sync → Check incoming bookings**. O consumidor normal do feed persiste a reserva e só depois envia ACK.
3. Confira **New**, origem **Feed**, **Acknowledged**. Copie **Booking ID** e **Booking revision ID** dessa entrada.
4. Modifique a mesma reserva na Channex, confira o PMS atualizado e ACK; copie o ID da entrada **Modified**.
5. Cancele a mesma reserva na Channex, confira cancelamento, disponibilidade restaurada e ACK; copie o ID **Cancelled**.

O Booking ID permanece igual; os três Revision IDs são diferentes. Entradas antigas sem metadata completa continuam identificadas como antigas: a atualização não transforma evidência anterior em recebimento via feed.

## O que conta como evidência

Testes locais com transporte simulado verificam a implementação e não geram IDs reais. Um novo teste ao vivo exige resposta da Channex e estado recebido; reservas exigem ACK após persistência. A aprovação final depende da nova revisão da Channex.

Referências: [cenários oficiais](https://docs.channex.io/api-v.1-documentation/pms-certification-tests), [ARI](https://docs.channex.io/api-v.1-documentation/ari), [feed de revisões](https://docs.channex.io/api-v.1-documentation/bookings-collection#booking-revisions-feed).
