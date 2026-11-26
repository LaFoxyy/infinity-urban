# Publicação exclusiva do Infinityboard

Status: preparação local. Nenhuma stack, serviço, volume, rede, segredo ou entrada DNS foi criado ou alterado no servidor nesta etapa. A imagem Docker/Linux ainda precisa passar no GitHub antes da publicação.

## Escopo e dados confirmados

Somente app.urbaninfinity.com.br e o alias anterior infinityboard.urbaninfinity.com.br, ambos na mesma stack infinityboard. Preservar o domínio principal, LP, demais subdomínios e stacks, inclusive urban_infinit_gestor e traefik. Não modificar o proxy global.

Confirmado nas capturas: Swarm, nó infinityServer, Traefik v2.11.2, rede externa infinityNet (10.0.1.0/24), entrada websecure e emissor letsencryptresolver. Nosso label usa infinityNet; não vamos corrigir o label network_public do outro sistema.

## 1. Construção fora da Hetzner

Enviar os arquivos para main. O workflow Infinity Urban - validar e publicar executa as verificações simuladas e os testes de interface, valida o escopo da stack e constrói uma imagem Linux no GitHub. Testa essa imagem com 0,5 CPU e 512 MiB, usuário sem privilégios e volume preservado entre duas execuções. A Channex fica desativada nesses testes; nenhuma chave de produção é necessária no GitHub.

Se passar, publica ghcr.io/PROPRIETARIO/REPOSITORIO:sha-COMMIT. A referência exata aparece no resumo de Actions. O pacote GHCR pode exigir configuração de leitura se for privado. A visibilidade desse pacote é independente da visibilidade do repositório; o proprietário pode optar por disponibilizar somente a imagem sem segredos como pública.

Sem o segredo GitHub INFINITYBOARD_SERVICE_WEBHOOK, o workflow não aciona o Portainer. A primeira etapa apenas testa e disponibiliza a imagem.

## 2. Dados operacionais antes da ativação

O backup do servidor foi confirmado pelo usuário. Ainda é necessário transferir os dados da instalação local. Combinar uma janela com o responsável pelo servidor e parar o processo local para congelar o processamento. Não executar dois consumidores de produção independentes.

Fazer uma cópia consistente do SQLite e dos registros de entrega em .local/catalog. Considerar WAL: não copiar somente um .db aberto em uso. Restaurar no volume exclusivo infinityboard_data, preservando runtime/production/stayline.db e catalog/production. O volume deve permitir leitura e escrita ao UID/GID 1654:1654. A ferramenta de transferência e a restauração privada ainda precisam ser combinadas com o responsável pelo servidor.

As chaves de proteção de sessão precisam persistir no volume. Chaves protegidas por DPAPI no Windows não são diretamente transferíveis para Linux; o novo servidor deverá gerar suas próprias chaves persistentes e exigir novo login. O desenvolvimento local usa sessões efêmeras.

Os dados Supabase continuam no projeto configurado no Infinity Urban. Não copiar as variáveis do Supabase da outra aplicação mostrada nas capturas. Não publicar backups, chaves ou a pasta .local em imagens ou no GitHub.

## 3. Primeira stack, após imagem aprovada

Com o responsável pelo servidor, criar somente o volume infinityboard_data e restaurar os dados. Criar o Docker Secret infinityboard_channex_v1 com o JSON privado de configuração Channex desta instalação, copiado diretamente do arquivo local para o Portainer, sem passar por código público ou capturas.

Criar uma NOVA stack infinityboard usando deploy/portainer-stack.yml. Informar INFINITYBOARD_IMAGE com a referência exata validada e manter INFINITYBOARD_CHANNEX_ENABLED=false durante a conferência inicial. Essa fase não processa reservas reais e ainda não é a operação completa.

Alterar somente o registro DNS infinityboard na Hostinger para o IP público confirmado pelo responsável. Conferir registros A, AAAA ou CNAME conflitantes apenas desse host. Não alterar registros globais ou curingas.

A stack fixa uma réplica no nó infinityServer, limite de 0,5 CPU/512 MiB, reserva de 128 MiB e logs com rotação. Não publica portas do host, não tem rede host, privilégios elevados nem socket Docker. Os limites iniciais precisam de observação sob carga real.

A regra Traefik aceita exatamente app.urbaninfinity.com.br e infinityboard.urbaninfinity.com.br durante a transição. Nosso adaptador aceita HTTPS/IP encaminhados apenas da rede confirmada 10.0.1.0/24 e não reescreve o host por cabeçalho encaminhado.

Após conferir restauração, acesso, domínio e HTTPS, habilitar INFINITYBOARD_CHANNEX_ENABLED=true somente na nossa stack. Confirmar reservas e sincronização mantendo a instalação local de produção parada.

## 4. Atualização automática, depois da primeira publicação estável

Abrir Services → infinityboard_web e habilitar o webhook SOMENTE desse serviço. Nunca usar webhook de Traefik ou de outro sistema. Guardar a URL HTTPS como segredo do GitHub INFINITYBOARD_SERVICE_WEBHOOK; não colocar a URL em código ou capturas.

A automação usa a tag exata do commit e confere a versão em /health/live. A troca para a versão nova usa stop-first e preserva o volume. Pode haver breve indisponibilidade da nossa aplicação. O rollback automático cobre falhas de inicialização detectadas na janela configurada; não garante detectar todo erro de negócio e não restaura banco. Futuras mudanças de esquema exigem plano de backup próprio.

Se a nova versão não for confirmada, o workflow falha e solicita conferir somente nosso serviço. Não reinicia o servidor, não altera outras stacks e não repete cegamente o acionamento.

Referências: https://doc.traefik.io/traefik/v2.11/routing/providers/docker/ e https://docs.portainer.io/user/docker/services/webhooks


## Troca de endereço para app.urbaninfinity.com.br

A aplicação, o serviço `infinityboard_web`, o volume e o segredo permanecem os mesmos. Não criar outra instância, copiar o banco ou mudar o proxy global.

1. Criar na Hostinger somente o registro A `app`, apontando para `5.161.217.98`, com TTL 300. Se houver registro existente para esse nome, confirmar seu destino antes de substituir.
2. No Editor da stack **infinityboard já existente**, alterar apenas `AllowedHosts` para `"app.urbaninfinity.com.br;infinityboard.urbaninfinity.com.br"` e a label do router para ``traefik.http.routers.infinityboard.rule=Host(`app.urbaninfinity.com.br`) || Host(`infinityboard.urbaninfinity.com.br`)``. Preservar todas as outras linhas, especialmente a imagem atualmente publicada, Channex habilitada, volume, segredo e limites.
3. Atualizar somente essa stack. O nome antigo continua atendido, inclusive para a verificação de saúde da imagem anterior.
4. Confirmar `https://app.urbaninfinity.com.br/health/live` e o login antes de enviar a alteração do workflow ao GitHub. As próximas imagens e a confirmação de publicação usam o novo host.
5. Entrar novamente no endereço novo: a sessão do navegador é específica do endereço. Não remover o registro antigo durante a validação. A retirada do alias fica para uma etapa posterior.

O webhook de atualização do serviço no Portainer não precisa mudar. Nenhuma alteração deve ser feita nos registros `@`, `www`, demais subdomínios, stack Traefik ou stacks de outros produtos.
