# MGS1 PT-BR — aplicador de tradução

Aplicador gráfico para Windows da tradução brasileira de **Metal Gear Solid**
(PlayStation).

## Download

### [⬇️ Baixar o aplicador para Windows (ZIP)](https://github.com/Hieronymouss/mgs1-ptbr-patcher/releases/download/v0.1.0-beta.5/MGS1-PTBR-Patcher-0.1.0-beta.5-win-x64.zip)

Extraia todo o conteúdo antes de abrir `Mgs1.Patcher.Gui.exe`. Não execute o
programa de dentro do ZIP.

Esta é uma versão beta do checkpoint traduzido atual. O executável ainda não
possui assinatura digital comercial, então o Windows pode identificar o editor
como desconhecido. Baixe somente deste repositório e confira o
[SHA-256 publicado](https://github.com/Hieronymouss/mgs1-ptbr-patcher/releases/tag/v0.1.0-beta.5).

No primeiro uso, o Microsoft Defender SmartScreen pode mostrar **“O Windows
protegeu o computador”** porque o executável ainda não é assinado e não possui
reputação estabelecida. Esse aviso de aplicativo não reconhecido não é, por si
só, uma detecção de malware. Use **Executar assim mesmo** somente se o ZIP veio
deste repositório oficial e seu SHA-256 coincide com o publicado. Se a Segurança
do Windows identificar uma ameaça pelo nome ou colocar um arquivo em
quarentena, não prossiga e abra um relato em
[SECURITY.md](SECURITY.md).

## Requisitos

- Windows 10 ou posterior em um computador Intel/AMD de 64 bits.
- Os pares BIN/CUE limpos da versão americana Rev 1 dos dois discos.
- Pelo menos 4 GiB livres na pasta de destino.

## Como usar

1. Abra `Mgs1.Patcher.Gui.exe`.
2. Selecione os arquivos CUE limpos dos Discos 1 e 2 com **Procurar...** ou
   arraste cada CUE para o painel do disco correspondente. A BIN associada deve
   estar na mesma pasta de cada CUE.
3. Escolha uma pasta de destino vazia com **Procurar...** ou arraste a pasta
   para o painel de destino.
4. Clique em **Aplicar tradução** e aguarde a confirmação.

Entradas de outra região, revisão ou conteúdo modificado são recusadas. Os
originais não são alterados e saídas existentes não são sobrescritas. Cada par
gerado conserva o nome do CUE selecionado e recebe o sufixo `(PT-BR)`.

Os discos gerados estão prontos para serem carregados no emulador de sua
preferência ou gravados em CD-ROM. **Testado no DuckStation.**

## Sobre o projeto

Este é um projeto de fãs, sem vínculo ou aprovação da Konami. O download não
contém o jogo completo, BIOS, emulador ou saves; é necessário possuir uma cópia
legítima e compatível do jogo.

O aplicador é portátil, inclui seu próprio runtime .NET dentro da pasta
`runtime/` e é open source. Não instala serviços, não altera o Registro ou o
PATH, não pede privilégios de administrador, não acessa a rede e não envia
telemetria.

Consulte [BUILDING.md](BUILDING.md) para compilar o código e
[SECURITY.md](SECURITY.md) para relatar uma vulnerabilidade. O código-fonte está
sob a licença MIT; a tradução, os payloads e as marcas envolvidas têm condições
separadas descritas em [NOTICE.md](NOTICE.md).
