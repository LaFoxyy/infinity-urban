# Novos resultados para revisão da Channex — 29/10/2026

O código foi corrigido, compilou sem erros/avisos e passou nos 52 testes locais. Os casos 2–10 foram refeitos pela interface normal do calendário: nove chamadas aceitas pela API de staging, cada uma com um task ID novo. O ciclo de reserva foi recebido pelo feed, salvo antes do ACK e verificado nas três etapas. Foram feitas 59 conferências de leitura na Channex, todas com o resultado esperado.

Esses resultados não substituem a aprovação formal da Channex. O Full Sync anterior já foi aprovado e não foi repetido.

## Casos 2–10

Copie somente o UUID para o campo do cenário correspondente.

| Caso | Endpoint | Novo task ID |
|---|---|---|
| 2 | restrictions | `85d2c022-42c0-4f38-8bfc-4ab3287ba0e8` |
| 3 | restrictions | `f4f6076b-4655-492b-9bce-30d2d048f702` |
| 4 | restrictions | `2ab70dce-c2d6-4b13-ad91-9def2a60d4ae` |
| 5 | restrictions | `1eb76178-72c8-4473-9351-65d3d2c037de` |
| 6 | restrictions | `2185d8c9-67fb-4a92-9165-c155e4a8f9df` |
| 7 | restrictions | `5d594477-cbc1-4357-833c-9b6ffea1513f` |
| 8 | restrictions | `e9078afd-6d48-42b4-9a40-1070d36218ff` |
| 9 | availability | `5f2babfb-6939-4f39-9b32-8760a17568a4` |
| 10 | availability | `fa7fbe74-156c-49e2-bdab-1f85eb51ce8d` |

## Caso 11 — Booking Receiving

| Campo do formulário | Novo ID |
|---|---|
| Booking ID | `28617ecd-d835-4c2e-a76d-89f90fa618e0` |
| Booking Revision ID for New Revision | `09449866-b0a1-4615-86a7-c5aa6602517b` |
| Booking Revision ID for Modified Revision | `d729fd0e-e426-4129-98d1-68b7cb047178` |
| Booking Revision ID for Cancelled Revision | `c3bb7339-7774-4b31-a71a-aaf4cf0027e3` |

Todos os três recibos têm origem `feed`, persistência confirmada e ACK posterior. A reserva começou com entrada 10/12 e saída 12/12, foi modificada para saída 13/12 e depois cancelada. A disponibilidade de Twin Room voltou a 1 em 10, 11 e 12/12; checkout em 13/12 permanece livre.

## Adaptação de inventário

Nos casos 9 e 10, ambos os tipos de quarto continuam representando uma unidade. Foi enviada disponibilidade zero nas datas exatas pedidas, usando a adaptação de unidades únicas já aceita no email. Isso não representa inventário de 7, 3 ou 4 quartos; descreva a adaptação na resposta à Channex.

## Evidências

- `Stayline-retest-2026-10-29.json`: corpos completos das nove requisições aceitas, datas, planos e task IDs.
- `Stayline-booking-retest-2026-10-29.json`: Booking ID, Revision IDs, origem e horários de persistência/ACK.
- `Stayline-retest-readback-2026-10-29.json`: 59 conferências do estado lido na Channex.
- `Stayline-regression-checks.txt`: 52 testes locais aprovados; o transporte desses testes é simulado e não gera IDs reais.

As evidências JSON e o log estão na pasta outputs, acima da pasta do projeto. IDs antigos de requisições rejeitadas ou recibos legacy não foram reaproveitados. Nenhuma consulta remota de lista de reservas/revisões nem GET por ID foi usada para coletar os novos IDs de reserva.
