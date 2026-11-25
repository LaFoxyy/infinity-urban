# Stayline — cópia separada com Channex de produção

Esta pasta contém a Stayline original da certificação, com a interface própria, calendário, propriedades, quartos, tarifas, reservas, fila ARI, deltas, restrições, controle de frequência, tentativas e recebimento de revisões pelo feed/webhook com ACK após persistência. Inclui as correções de certificação de 29/10 e do formulário de reservas de 03/11/2026.

A origem é o arquivo Stayline-source.zip de 03/11/2026. Não inclui Urban Infinity, Guesty, React, Supabase, migração de imóveis ou o login da versão integrada. O projeto atual não foi alterado.

## Adaptação autorizada para produção

Somente nesta cópia foram aplicados o cliente de produção já existente, a seleção do ambiente, o isolamento do banco por ambiente e os ajustes do inicializador/status correspondentes. O destino é https://app.channex.io/api/v1/.

Os arquivos PmsService.cs, AriFields.cs, ChannexSyncWorker.cs, BookingInboxWorker.cs, BookingFeedSnapshot.cs e ChannexEndpoints.cs permanecem idênticos aos da cópia original aprovada. A interface original também foi preservada. Por isso, alguns textos antigos ainda dizem staging; o destino efetivo é o ambiente informado pelo servidor em /api/status.

A chave de produção solicitada está exclusivamente em src/Stayline.Api/.local/production-secrets.json. Esse arquivo é configuração privada do servidor, fica fora de wwwroot e é ignorado pelo Git. O ZIP contém essa credencial real e deve ser mantido privado.

Não foram incluídos bancos, reservas, UUIDs de mapeamento, filas, logs ou credenciais de staging da instalação existente. Os exemplos e resultados de certificação nos documentos são históricos. O banco desta cópia começa separado em App_Data/production; configure os vínculos reais de produção pela própria interface.

## Abrir localmente

1. Extraia o ZIP inteiro em uma pasta separada.
2. Instale o SDK .NET 10, se necessário.
3. Execute Start-Stayline.cmd. A interface abre em http://127.0.0.1:5080.
4. Use Stop-Stayline.cmd para parar esta cópia e Restart-Stayline.cmd para reiniciá-la.

Se a porta 5080 estiver em uso pela versão integrada, o inicializador avisa e não encerra esse outro projeto. Para usar outra porta sem tocar no sistema atual, abra um terminal nesta pasta e execute:

```powershell
dotnet run --project src/Stayline.Api --no-launch-profile -- --urls http://127.0.0.1:5081
```

A cópia mantém o funcionamento local da Stayline original, sem login. Ambiente de produção aqui significa o destino da API Channex; este pacote não acrescenta autenticação nem publicação pública.

Não é necessário Node.js, npm ou build da Urban Infinity. A chave já está configurada para esta cópia. A sincronização e o feed iniciam quando o servidor é aberto; não foi aberto nenhum servidor nem enviada qualquer requisição real à Channex durante o empacotamento.

## Conferência

O diretório tests/Stayline.Checks mantém os 52 checks originais e inclui os cinco checks existentes de separação entre ambientes. Eles usam transporte simulado e não acessam a conta real.

O arquivo EXTRACAO-VERIFICACAO.json registra a integridade dos fluxos preservados e a validação do pacote, sem divulgar a chave.
