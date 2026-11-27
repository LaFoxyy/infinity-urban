# Cadastro de contas e idiomas

## Cadastro

`POST /api/auth/signup` envia nome, email e senha ao mesmo projeto Supabase já configurado no portal. Usa apenas a chave pública no servidor. O navegador não recebe tokens de autenticação após o cadastro.

O formulário deixou de ser uma demonstração. O endpoint valida os campos, exige CSRF e limita tentativas por endereço. Metadados enviados ao Supabase contêm somente `full_name`; funções e status enviados pelo navegador são ignorados. Não há cadastro de função administrativa nem aprovação automática.

A infraestrutura existente do banco foi consultada: o gatilho `on_auth_user_created` chama `handle_new_user()` e cria o perfil; `profiles.status` tem valor padrão `pending`. Nenhuma alteração de esquema ou de configuração global do Supabase foi necessária. O administrador aprova o perfil e escolhe sua função na Gestão.

Se a confirmação de email estiver habilitada no Supabase, o cadastro orienta o usuário a confirmar. Se estiver desabilitada e o Supabase devolver uma sessão, ela é encerrada no servidor. O retorno público continua sendo a instrução para aguardar aprovação. A URL de retorno de confirmação solicitada é `https://app.urbaninfinity.com.br/login`.

Os testes usam contas simuladas; não criam usuários reais nem enviam emails de teste. A recuperação de senha continua orientando contato com a administração e não faz parte desta correção.

## Idiomas

Português e inglês ficam disponíveis no login e no cabeçalho das áreas internas. A preferência `urban-language` é salva no navegador e acompanha a navegação. Ela não armazena credenciais. Uma cookie de mesmo nome, sem privilégios de autenticação, define os cabeçalhos do CSV.

`wwwroot/i18n.js` traduz textos e atributos de interface, inclusive mensagens e controles criados após a abertura da página. A troca de idioma não recarrega a página, não altera valores dos campos e não desmarca comodidades. Datas e valores exibidos acompanham a localidade. A exportação CSV traduz os cabeçalhos; a impressão em PDF usa o idioma exibido.

`wwwroot/translations.js` contém o catálogo de textos da interface; `wwwroot/amenities.js` contém as traduções das 464 comodidades distintas consultadas nos catálogos de prédios e apartamentos da Channex. Somente o rótulo visível é traduzido. Os identificadores e os dados enviados à Channex permanecem iguais. Novas comodidades não catalogadas preservam o título recebido, em vez de inventar uma tradução ou alterar a seleção.

Nomes e descrições cadastrados pelo usuário não são traduzidos automaticamente. Elementos com `data-i18n-skip` ou `translate="no"` são preservados, assim como as opções de cadastros identificadas por UUID. Campos de preço aceitam o formato do idioma selecionado e valores portugueses já preenchidos antes de trocar para inglês.

Os 52 arquivos preservados da integração não foram alterados. O idioma é aplicado pela camada do portal.

## Validação

`Validar.ps1` executa as verificações de autenticação e permissões, o teste de cadastro e os testes de interface/idioma. `tools/interface/check-i18n.mjs` cobre tradução nos dois sentidos, mensagens dinâmicas, as 464 comodidades, preservação de nomes, UUIDs e seleção, além de valores monetários em centavos. A interface também foi conferida em navegador usando a prévia simulada, incluindo cadastro, login, relatórios, gestão e os formulários de prédio e apartamento.
