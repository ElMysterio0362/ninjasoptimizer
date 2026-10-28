# 2. Estrutura de pastas e arquitetura

## Estrutura

```
ninjasoptimizer/
├─ NinjasOptimizer.sln
├─ Directory.Build.props            # versão, nullable, C# latest (comum a todos os projetos)
├─ src/
│  ├─ NinjasOptimizer.Core/         # net8.0 — lógica pura, sem APIs do Windows (100% testável)
│  │  ├─ Abstractions/              # IRegistryAccess, IServiceManager, ICommandRunner, IRestorePointService…
│  │  ├─ Optimization/
│  │  │  ├─ Tweak.cs                # modelo: nome, descrição, benefício, risco, categoria, perfis
│  │  │  ├─ TweakCatalog.cs         # catálogo declarativo de otimizações
│  │  │  ├─ OptimizationEngine.cs   # aplicar / reverter com a sequência de segurança
│  │  │  └─ Actions/                # ações reversíveis: Registro, Serviço, Plano de energia…
│  │  ├─ Safety/ChangeJournal.cs    # histórico persistido (JSON) com snapshots para desfazer
│  │  ├─ Hardware/                  # modelos de hardware, sensores, inferência de canais de RAM
│  │  └─ Settings/                  # configurações do usuário (JSON)
│  ├─ NinjasOptimizer.Windows/      # net8.0-windows — implementações reais das abstrações
│  │  ├─ Platform/                  # registro, serviços (sc.exe), processos, elevação, ponto de restauração (WMI), backup .reg
│  │  └─ Hardware/                  # inventário via WMI/CIM, monitores via user32, sensores via LibreHardwareMonitor
│  └─ NinjasOptimizer.App/          # WPF (MVVM) — interface
│     ├─ Theme/                     # Colors.xaml (preto + #44088c), Controls.xaml (botões, toggle, nav, scrollbar…)
│     ├─ Controls/                  # Sparkline e ProgressRing (desenho direto, leves)
│     ├─ Services/                  # diálogos, log, estado de hardware e sensores compartilhados
│     ├─ ViewModels/                # Main, Dashboard, Optimize, Hardware, Settings, Placeholder
│     └─ Views/                     # uma UserControl por página
├─ tests/NinjasOptimizer.Core.Tests # xUnit — motor, reversão, journal, parsing (roda em qualquer SO)
├─ installer/NinjasOptimizer.iss    # Inno Setup 6
├─ build/build.ps1                  # testa, publica e gera o instalador
└─ .github/workflows/build.yml      # CI em windows-latest, publica o instalador como artefato
```

## Camadas

```
┌──────────────────────────── App (WPF / MVVM) ────────────────────────────┐
│ Views ──binding──► ViewModels ──► OptimizationEngine / HardwareState     │
└───────────────────────────────────┬──────────────────────────────────────┘
                                    │ depende só de interfaces
┌──────────────────────────── Core (net8.0) ────────────────────────────────┐
│ TweakCatalog → Tweak → TweakAction (Capture / Apply / IsApplied)          │
│ OptimizationEngine → ChangeJournal (snapshots) → ActionReverter           │
└───────────────────────────────────┬──────────────────────────────────────┘
                                    │ implementado por
┌──────────────────────────── Windows (net8.0-windows) ─────────────────────┐
│ Registry64 · sc.exe · powercfg · WMI SystemRestore · reg export · WMI/CIM │
│ LibreHardwareMonitor (sensores)                                           │
└───────────────────────────────────────────────────────────────────────────┘
```

* **Core não conhece o Windows.** Toda alteração passa por `IRegistryAccess`, `IServiceManager`, `ICommandRunner`. Os testes usam fakes em memória e validam aplicar → reverter → estado idêntico ao original.
* **Otimizações são dados.** Uma otimização é uma entrada no `TweakCatalog` composta por ações reutilizáveis. Novas otimizações não exigem código novo de reversão.
* **Snapshots autossuficientes.** Cada ação grava no journal *o estado anterior e onde ele estava* (ex.: hive/chave/valor/tipo). A reversão usa só o snapshot, então desfazer funciona mesmo após atualizar o app ou se o catálogo mudar.

## Sequência de segurança (OptimizationEngine.ApplyAsync)

1. **Risco alto** → só entra com confirmação explícita (diálogo vermelho). Sem confirmação, é pulado. Testado em `High_risk_is_never_applied_without_explicit_confirmation`.
2. **Já aplicado** → ignorado (nada a registrar).
3. **Admin** → se algum ajuste precisa e o app não está elevado, aborta sem tocar em nada. A UI oferece reabrir como administrador mantendo a seleção (`--page optimize --select ids`).
4. **Ponto de restauração** (WMI `SystemRestore.CreateRestorePoint`). O limite de 1 por 24h do Windows é liberado temporariamente e restaurado em seguida. Opcionalmente aborta se falhar.
5. **Backup .reg** de todas as chaves que serão alteradas (`reg export`), em `%LocalAppData%\NinjasOptimizer\backups\<data-hora>\`.
6. **Snapshot** de todas as ações do ajuste **antes** de alterar qualquer uma.
7. **Aplicação atômica por ajuste**: se a 3ª ação falha, as 2 primeiras são desfeitas na hora.
8. **Journal** (`journal.json`, escrita atômica) com status Aplicado / Revertido / Falhou.

Reverter: item a item (botão *Desfazer* no histórico) ou *Reverter tudo* (do mais recente para o mais antigo).

## Privilégios

* O executável usa manifesto `asInvoker`: abre sem UAC.
* Leitura de hardware, sensores básicos e ajustes em `HKCU` funcionam sem admin.
* Ao aplicar/reverter algo que precisa (HKLM, serviços, powercfg, ponto de restauração), o app pergunta e se reabre elevado.
* Observação: ajustes em `HKCU` aplicados com o app elevado vão para o perfil do usuário que autorizou o UAC — em contas comuns que elevam com *outra* conta de administrador, isso é o perfil do admin.

## Dados locais

`%LocalAppData%\NinjasOptimizer\`

| Arquivo | Conteúdo |
|---|---|
| `journal.json` | Histórico de alterações com snapshots para desfazer |
| `settings.json` | Configurações |
| `backups\<data>\*.reg` | Exportações do registro antes de cada aplicação |
| `logs\app-AAAAMMDD.log` | Log de diagnóstico |
