# 4. Build e geração do instalador

## Pré-requisitos (Windows 10/11 x64)

| Ferramenta | Instalação |
|---|---|
| .NET 8 SDK | `winget install Microsoft.DotNet.SDK.8` |
| Inno Setup 6 (só para o instalador) | `winget install JRSoftware.InnoSetup` |
| (opcional) Visual Studio 2022 com a carga *Desenvolvimento para desktop com .NET* | — |

## Rodar em modo desenvolvimento

```powershell
git clone <url-do-repo>
cd ninjasoptimizer
dotnet run --project src/NinjaOptimizer.App
```

Os testes do núcleo rodam em qualquer sistema (inclusive Linux/macOS):

```powershell
dotnet test
```

## Gerar o instalador

```powershell
./build/build.ps1                      # self-contained: não exige .NET na máquina do usuário (~35–45 MB)
./build/build.ps1 -FrameworkDependent  # pequeno (~3 MB): exige o .NET 8 Desktop Runtime instalado
```

O script roda os testes, publica um executável único em `artifacts/publish/` e gera
`installer/Output/NinjaOptimizer-Setup-<versão>.exe`.

Para mudar a versão, edite `<Version>` em `Directory.Build.props`.

## CI

O workflow `.github/workflows/build.yml` roda em `windows-latest` a cada push: testes → publish → instalador,
e anexa o `.exe` do instalador como artefato da execução (aba *Actions* do GitHub).

## Observações

* **Sensores da CPU (temperatura, clock por núcleo, consumo):** a LibreHardwareMonitor 0.9.6 lê esses valores pelo
  driver **PawnIO** (código aberto, assinado). São necessários o app como administrador **e** o PawnIO instalado
  (`https://pawnio.eu`). O app detecta o que falta, mostra um aviso com o botão certo e, sem o driver, estima o clock
  pelo contador "% Processor Performance" do Windows (o mesmo do Gerenciador de Tarefas). GPU e RAM funcionam sem nada disso.
* **Assinatura de código:** para distribuição pública, assine o `.exe` e o instalador (`signtool sign /fd sha256 /tr <timestamp> /td sha256 ...`)
  para evitar o aviso do SmartScreen.
* **Compilar no Linux/macOS:** possível graças a `EnableWindowsTargeting` (útil para CI), mas o app só executa no Windows.
