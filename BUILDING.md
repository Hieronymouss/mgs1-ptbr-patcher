# Compilar a partir do código-fonte

## Requisitos

- Windows 10 ou posterior em x64.
- .NET SDK 10.0.302, que inclui o runtime de manutenção 10.0.10.

O aplicador não usa pacotes NuGet externos. A versão do SDK é fixada em
`global.json` para que builds futuros não troquem silenciosamente de toolchain.

## Compilar e testar

No diretório raiz do repositório:

```powershell
dotnet restore Mgs1.Patcher.sln -r win-x64 -m:1
dotnet build Mgs1.Patcher.sln -c Release --no-restore -m:1
dotnet run --project tests/Mgs1.Patcher.Core.Tests/Mgs1.Patcher.Core.Tests.csproj -c Release --no-build
dotnet run --project tests/Mgs1.Patcher.Gui.Tests/Mgs1.Patcher.Gui.Tests.csproj -c Release --no-build
```

## Publicar o aplicativo Windows

```powershell
dotnet publish src/Mgs1.Patcher.Gui/Mgs1.Patcher.Gui.csproj `
  -c Release `
  -r win-x64 `
  --self-contained false `
  --no-restore `
  -m:1 `
  -o artifacts/win-x64
```

O apphost publicado procura uma instalação privada do .NET na pasta relativa
`runtime/`. O pacote oficial fornece somente os componentes necessários de
`host/fxr/10.0.10`, `shared/Microsoft.NETCore.App/10.0.10` e
`shared/Microsoft.WindowsDesktop.App/10.0.10`. Ele não inclui o SDK, ASP.NET,
`dotnet.exe` ou workloads.

O comando produz o aplicativo, mas não o runtime privado nem os payloads BPS da
tradução. Os payloads não são armazenados no Git e somente integram os pacotes
oficiais aprovados.

O ZIP oficial contém uma única pasta para que **Extrair aqui** não espalhe os
arquivos no diretório escolhido. Dentro dela, a distribuição funcional usa
esta estrutura:

```text
MGS1-PTBR-Patcher-<versão>-win-x64/
  Mgs1.Patcher.Gui.exe
  Mgs1.Patcher.Gui.dll
  Mgs1.Patcher.Gui.Logic.dll
  Mgs1.Patcher.Core.dll
  Mgs1.Patcher.Gui.deps.json
  Mgs1.Patcher.Gui.runtimeconfig.json
  data/
    release-manifest.json
    patches/
      disc1.bin.bps
      disc1.cue.bps
      disc2.bin.bps
      disc2.cue.bps
  runtime/
    host/fxr/10.0.10/
    shared/Microsoft.NETCore.App/10.0.10/
    shared/Microsoft.WindowsDesktop.App/10.0.10/
  docs/
    licenças, avisos, checksums e proveniência
```

O manifest fixa os tamanhos e hashes de todas as entradas, payloads e saídas.
Não altere o manifest ou substitua payloads em um pacote publicado.
