# Dashboard e relatórios

Rotas: `/dashboard` e `/relatorios`. Ambas usam `/api/analytics`; o CSV usa `/api/analytics/export.csv`, com a mesma autorização. O botão **Imprimir / PDF** abre a impressão do navegador, na qual é possível salvar em PDF.

## Acesso

- Master e Admin consultam toda a carteira.
- Proprietários e funcionários consultam somente pares de apartamento/prédio em `urban_channex_assignments`, no ambiente atual. Sem designações, o painel fica vazio.
- Filtros e exportações são validados no servidor. A lista de designações é conferida novamente antes de liberar a resposta.
- Despesas continuam internas: proprietários não recebem seus detalhes, totais nem saldo após despesas.
- Funcionários recebem despesas somente dos apartamentos designados. Contas de prédio e sem vínculo aparecem apenas no consolidado administrativo, uma vez, sem rateio implícito entre apartamentos.
- Essa restrição é do Dashboard e dos Relatórios. As demais ferramentas operacionais de funcionários mantêm as permissões anteriores.

## Base financeira

O armazenamento próprio é `.local/catalog/<ambiente>/analytics.db`, no volume persistente existente. Ele guarda somente UUID da reserva/prédio/apartamentos, moeda, datas, valores em centavos, distribuição diária e instante da revisão. Não guarda cartões, contatos ou respostas brutas.

`AnalyticsFeedObserver` observa a resposta do feed já solicitado pela integração original. Não cria outro consumidor, não faz chamadas por ID/lista de reservas e não altera os arquivos aprovados, suas mensagens ou acknowledgments. A projeção é persistida antes de entregar a resposta ao consumidor original. Uma falha de persistência impede o recebimento dessa tentativa, que poderá ser repetida pelo fluxo existente. Revisões antigas não substituem revisões financeiras mais novas.

**Limite histórico:** o consumidor anterior descartava valores financeiros. Reservas já recebidas antes desta versão, e reservas manuais sem valor registrado, continuam úteis para ocupação e contagem, mas não têm receita confirmada. Não é possível recuperar esse valor de uma diária de calendário. A interface marca a receita como parcial, informa quantas estadias estão sem valor e exclui esses apartamentos dos rankings de receita até receber valores confirmados de nova revisão. Não foi implementada importação histórica nem atribuição manual de valores nesta etapa.

## Cálculos

- Datas do filtro são inclusivas; máximo de 366 dias. O check-out é exclusivo para ocupação/receita. Reservas canceladas não geram receita nem ocupação.
- Receita usa o valor do apartamento; o total da reserva é usado somente quando há um único apartamento. Valores de outros apartamentos nunca são rateados implicitamente.
- A distribuição diária fornecida pela OTA é usada quando completa e consistente com o total. Caso contrário, o valor é distribuído em centavos pelas noites, conservando exatamente o total. Apenas as noites dentro do filtro são somadas.
- Receita representa diárias informadas pela OTA, não extrato de recebimentos bancários. Não há dedução de comissão, previsão de repasse ou conversão cambial. Cada moeda é consultada separadamente.
- Ocupação = noites ocupadas / (dias selecionados × quantidade de unidades). Sobreposições não ultrapassam a capacidade física. Diária média só é exibida quando todos os valores necessários estão disponíveis.
- Rankings ordenam receita, depois ocupação e nome; no máximo 10 linhas cada. O mesmo apartamento pode estar nos dois rankings quando a carteira tem poucos apartamentos.
- Mensais: proporção dos dias selecionados em cada mês vigente; último mês informado inclusivo. Por estadia: cobrada no check-in, uma vez por reserva/apartamento. Avulsa: data exata cadastrada.

## Banco e publicação

`database/analytics-assignments-access.sql` amplia somente as políticas de designações próprias para aceitar funcionários aprovados; administração e exclusão continuam exclusivas de Master/Admin. Não altera tabelas Guesty, comissões ou dados do site antigo. A atualização foi aplicada no Supabase do projeto e pode ser reaplicada.

Não execute uma instância local com a chave de produção enquanto o servidor estiver consumindo o feed. A validação usa serviços simulados, incluindo recebimento financeiro, autorização, revogação durante a consulta e exportação. A publicação continua pelo fluxo existente do GitHub para o serviço `infinityboard_web`.
