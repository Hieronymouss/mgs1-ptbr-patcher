# Compilar a partir do código-fonte

## Requisitos

- Windows 10 ou posterior em x64.
- .NET SDK 10.0.302, que inclui o runtime de manutenção 10.0.10.

O aplicador não usa pacotes NuGet externos. A versão do SDK é fixada em
`global.json` para que builds futuros não troquem silenciosamente de toolchain.

## Compilar e testar

No diretório raiz do repositório:

```powershell
dotnet restore Mgs1.Patcher.sln -m:1
dotnet build Mgs1.Patcher.sln -c Release --no-restore -m:1
dotnet run --project tests/Mgs1.Patcher.Core.Tests/Mgs1.Patcher.Core.Tests.csproj -c Release --no-build
dotnet run --project tests/Mgs1.Patcher.Gui.Tests/Mgs1.Patcher.Gui.Tests.csproj -c Release --no-build
```

## Publicar o aplicativo Windows

```powershell
dotnet publish src/Mgs1.Patcher.Gui/Mgs1.Patcher.Gui.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  --no-restore `
  -m:1 `
  -o artifacts/win-x64
```

Isso produz o aplicativo, mas não os payloads BPS da tradução. Eles não são
armazenados no Git e somente integram os pacotes oficiais aprovados.

Uma distribuição funcional usa esta estrutura:

```text
Mgs1.Patcher.Gui.exe
data/
  release-manifest.json
  patches/
    disc1.bin.bps
    disc1.cue.bps
    disc2.bin.bps
    disc2.cue.bps
```

O manifest fixa os tamanhos e hashes de todas as entradas, payloads e saídas.
Não altere o manifest ou substitua payloads em um pacote publicado.
