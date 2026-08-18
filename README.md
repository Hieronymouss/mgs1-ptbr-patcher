# MGS1 PT-BR — aplicador de tradução

Aplicador gráfico para Windows da tradução brasileira de **Metal Gear Solid**
(PlayStation). O programa recebe os dois discos americanos Rev 1 em formato
BIN/CUE, confere cada arquivo por tamanho e SHA-256 e cria cópias traduzidas em
uma pasta escolhida pelo usuário.

Este é um projeto de fãs, sem vínculo ou aprovação da Konami. O repositório e
os pacotes de lançamento **não contêm o jogo completo, BIOS, emulador ou saves**.
É necessário possuir uma cópia legítima e compatível do jogo.

## Baixar

Baixe o ZIP mais recente na página de
[Releases](https://github.com/Hieronymouss/mgs1-ptbr-patcher/releases). Extraia
todo o conteúdo antes de abrir `Mgs1.Patcher.Gui.exe`; não execute o programa
de dentro do ZIP.

A versão atual é uma prévia pública do checkpoint traduzido disponível. Ela
não declara que todas as falas ou a futura dublagem estejam concluídas.

## Requisitos

- Windows 10 ou posterior, Intel/AMD de 64 bits.
- Os pares BIN/CUE limpos da versão americana Rev 1 dos dois discos.
- Pelo menos 4 GiB livres na pasta escolhida para as saídas.

O programa é portátil e autossuficiente. Não instala serviços, não altera o
Registro ou o PATH, não pede privilégios de administrador, não acessa a rede e
não envia telemetria.

## Aplicar a tradução

1. Abra `Mgs1.Patcher.Gui.exe`.
2. Selecione o CUE limpo do Disco 1. A BIN indicada pelo CUE deve estar na mesma
   pasta.
3. Faça o mesmo com o Disco 2.
4. Escolha uma pasta de destino vazia.
5. Clique em **Aplicar tradução** e aguarde a confirmação de conclusão.

Entradas de outra região, revisão, disco ou conteúdo modificado são recusadas.
Os originais não são alterados, saídas existentes não são sobrescritas e todos
os arquivos gerados são conferidos antes da publicação final.

## Abrir no DuckStation

Adicione o CUE gerado à lista de jogos ou use **Open File** dentro do próprio
DuckStation. Evite abrir o CUE por duplo clique através da associação de
arquivos do Windows: durante a validação do projeto, essa rota produziu áudio
com vídeo em branco, embora o mesmo arquivo funcionasse normalmente quando
aberto pelo DuckStation.

## Segurança e verificabilidade

O código-fonte do aplicador está neste repositório. O núcleo valida:

- identidade exata dos discos por SHA-256 e tamanho;
- integridade dos payloads BPS por SHA-256 e CRC32;
- espaço livre e segurança da pasta de destino;
- preservação dos originais e ausência de sobrescrita; e
- hash e tamanho finais de cada saída.

Cada Release publica o SHA-256 de seu ZIP. Compare o checksum antes de executar
um download obtido fora da página oficial. Falhas de segurança devem seguir
[SECURITY.md](SECURITY.md); problemas comuns podem ser relatados nas Issues sem
anexar imagens de disco, BIOS ou saves.

## Código-fonte

O aplicador de produção é escrito em C#/.NET e não possui dependências NuGet de
terceiros. Consulte [BUILDING.md](BUILDING.md) para compilar e testar. Os
payloads da tradução são publicados somente nos pacotes oficiais e não fazem
parte do histórico Git.

O código está sob a licença MIT. A tradução, os payloads e as marcas envolvidas
têm condições separadas descritas em [NOTICE.md](NOTICE.md).

