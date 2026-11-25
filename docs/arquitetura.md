# Organização da aplicação

Infinity Urban é uma única aplicação ASP.NET Core. O projeto Web reúne a interface e os fluxos do produto; o projeto Channex é compilado como uma biblioteca interna utilizada por essa aplicação.

As fontes em `src/InfinityUrban.Channex/Approved` foram preservadas byte a byte. Os nomes internos originais, inclusive o namespace `Stayline`, permanecem para respeitar a restrição de não modificar a integração. O antigo ponto de entrada `Program.cs` e o projeto original ficam em `docs/integracao/referencia`, somente como referência de integridade. Eles não são compilados pelo módulo. Não há um segundo site ou servidor para iniciar.

O transporte, os handlers e os workers aprovados continuam sendo os mesmos. As extensões do dashboard permanecem na aplicação Web. A interface operacional reutiliza o JavaScript preservado em `Assets/operations.js` por uma rota protegida; os antigos HTML e CSS não são publicados como outro site.

`docs/integracao/fontes-preservadas.json` registra o caminho de origem, o novo caminho e o SHA-256 de cada arquivo da base preservada. A ferramenta de empacotamento confere todos esses arquivos antes de entregar uma versão.

Os identificadores de compatibilidade de cookies, proteção de dados, variáveis de ambiente e banco foram mantidos para não invalidar configurações e dados por uma alteração de nome. O banco operacional existente permanece com seu nome de arquivo original dentro da pasta privada.

As referências históricas da integração ficam em `docs/integracao/referencia`; não são aplicações ativas. O histórico de desenvolvimento foi separado do guia principal para manter a entrada do projeto objetiva.
