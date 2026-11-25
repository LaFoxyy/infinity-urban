# Infinity Urban

Dashboard interno de gestão de acomodações, contas, proprietários, despesas e reservas, integrado à Channex de produção e ao Supabase.

Para abrir o site nesta máquina, dê dois cliques em **Iniciar.cmd**. O endereço é [localhost:5189](http://localhost:5189/login). Se já estiver aberto, o inicializador reutiliza a instância existente.

## Estrutura

| Pasta | Conteúdo |
| --- | --- |
| `src/InfinityUrban.Web` | Aplicação, telas, autenticação e funcionalidades do dashboard |
| `src/InfinityUrban.Channex` | Módulo interno com o transporte, os serviços e os workers da integração preservada |
| `tests/InfinityUrban.Checks` | Verificações de comportamento, permissões e integração simulada |
| `database` | Scripts SQL do Supabase |
| `docs` | Arquitetura, integridade e histórico técnico |
| `tools` | Ferramentas de validação e empacotamento |

**InfinityUrban.slnx** abre toda a solução. Há uma única aplicação web e um único servidor; o módulo Channex é uma biblioteca interna, sem inicialização independente.

## Configuração local

Requer .NET 10. As configurações privadas, os dados operacionais e os registros desta instalação ficam em `src/InfinityUrban.Web/.local`, fora dos arquivos públicos e do pacote de distribuição. A reorganização preservou os dados e as configurações existentes.

A chave Channex permanece no arquivo privado `.local/channex-secrets.json`. Credenciais privilegiadas não devem ser colocadas no código ou no navegador. O Supabase mantém a configuração pública da autenticação; a chave Channex fica no servidor.

## Verificação e entrega

Execute `Validar.ps1` para compilar e rodar as verificações simuladas, os testes de interface e a conferência dos arquivos da integração. Node.js e Python são usados por essas ferramentas; para iniciar o site, basta o .NET.

Execute `python tools/package.py` na pasta do projeto para gerar `dist/Infinity-Urban.zip`, sem dados locais, chaves, históricos de execução ou arquivos de compilação. Os scripts SQL são documentação de implantação e não são executados automaticamente.

As versões anteriores e os protótipos foram preservados em **Arquivo**, ao lado desta pasta. Essa pasta é um arquivo privado de desenvolvimento e não faz parte do produto ativo.
