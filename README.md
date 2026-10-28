<p align="center"><img src="src/NinjasOptimizer.App/Assets/logo.png" width="96" /></p>

# Ninjas Optimizer

Otimizador de Windows com foco em desempenho e segurança: cada ajuste é individual, explicado, classificado por risco e **reversível item a item**.

**Stack:** C# / .NET 8 + WPF (MVVM) · LibreHardwareMonitor · WMI — veja a [decisão de stack](docs/01-decisao-de-stack.md).

## Estado atual — Etapa 1

| Módulo | Status |
|---|---|
| Interface base (tema preto + `#44088c`, navegação lateral, animações) | ✅ |
| Dashboard (resumo, gráficos ao vivo, otimização rápida) | ✅ |
| Hardware (inventário completo + sensores em tempo real) | ✅ |
| Otimizar (toggles, perfis, aplicar/reverter, histórico com desfazer) | ✅ motor completo + 3 otimizações |
| Segurança (ponto de restauração, backup .reg, journal, confirmação de risco alto, admin sob demanda) | ✅ |
| Catálogo completo de otimizações | Etapa 2 |
| Benchmark | Etapa 3 |
| Overlay em jogos | Etapa 4 |
| Gargalos e compatibilidade | Etapa 5 |

## Documentação

1. [Decisão de stack (comparativo)](docs/01-decisao-de-stack.md)
2. [Estrutura de pastas e arquitetura](docs/02-arquitetura.md)
3. [Roteiro por etapas e como testar](docs/03-roteiro-de-etapas.md)
4. [Build e instalador](docs/04-build-e-instalador.md)

## Início rápido

```powershell
dotnet test                                  # testes do núcleo (qualquer SO)
dotnet run --project src/NinjasOptimizer.App # executa (Windows)
./build/build.ps1                            # gera o instalador (Windows + Inno Setup 6)
```
