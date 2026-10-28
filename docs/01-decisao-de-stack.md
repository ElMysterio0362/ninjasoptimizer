# 1. Decisão técnica — escolha da stack

## Requisitos que pesam na decisão

| Requisito | Por que importa |
|---|---|
| Acesso profundo ao Windows | Registro, serviços, WMI/CIM, `powercfg`, PowerShell, P/Invoke em `ntdll`/`kernel32` (ex.: limpar standby list), ETW (FPS do overlay) |
| Desempenho e baixo consumo | Um otimizador que consome 300 MB de RAM perde credibilidade |
| Overlay in-game | Precisa de janela transparente *topmost* sem foco e, idealmente, captura de frametimes via ETW (como o PresentMon) |
| Visual premium | Tema escuro, gradientes, animações suaves, gráficos em tempo real |
| Instalador | Tamanho razoável, assinatura, auto-elevação só quando necessário |

## Comparativo

| Critério | **C# .NET 8 + WPF** | C# .NET 8 + WinUI 3 | Rust + Tauri | Electron + Node |
|---|---|---|---|---|
| Registro / Serviços / WMI | ✅ Nativo (`Microsoft.Win32.Registry`, `ServiceController`, `System.Management`) | ✅ Igual ao WPF | 🟡 Crates `winreg`, `wmi`, `windows-rs` — funcionam, mas mais verbosos | 🔴 Precisa de módulos nativos (`node-ffi`, `regedit`) ou chamar PowerShell — frágil e lento |
| P/Invoke / APIs NT | ✅ `DllImport`/`LibraryImport` triviais | ✅ | ✅ Excelente (`windows-rs`) | 🔴 Só via addon nativo em C++ |
| Sensores (temperatura, clocks) | ✅ **LibreHardwareMonitorLib** (biblioteca .NET pronta e madura) | ✅ | 🟡 Teria que reimplementar ou chamar um sidecar .NET | 🔴 Idem, via sidecar |
| FPS / frametime (ETW, PresentMon) | ✅ `Microsoft.Diagnostics.Tracing.TraceEvent` | ✅ | 🟡 `ferrisetw` (viável) | 🔴 Sidecar obrigatório |
| Overlay transparente *click-through* | ✅ Janela WPF `AllowsTransparency` + `WS_EX_TRANSPARENT/NOACTIVATE` | 🟡 Transparência por janela é limitada no WinUI 3 | 🟡 Possível, mas WebView2 por cima do jogo custa GPU/RAM | 🔴 Chromium por cima do jogo: impacto alto |
| Desempenho / RAM ociosa | ✅ ~60–120 MB | ✅ ~70–130 MB | ✅ ~30–80 MB (+ WebView2 compartilhado) | 🔴 ~200–400 MB |
| Tamanho do instalador | 🟡 ~1 MB (framework-dependent) / ~70 MB (self-contained, comprimido ~35 MB) | 🔴 Maior (Windows App SDK) | ✅ 3–10 MB | 🔴 80–150 MB |
| Qualidade visual | ✅ Estilização total via XAML, gradientes, animações (Storyboards) | ✅ Fluent nativo, Mica/Acrylic | ✅ HTML/CSS — liberdade total | ✅ HTML/CSS |
| Maturidade / ecossistema desktop | ✅ Muito alta, estável há 15+ anos | 🟡 Ainda com arestas (empacotamento, bugs) | 🟡 Boa, mas lógica de sistema fica toda em Rust | ✅ Alta, mas não para ferramenta de sistema |
| Produtividade da equipe | ✅ Alta (MVVM, DI, testes) | 🟡 Média | 🟡 Média (duas linguagens: Rust + TS) | ✅ Alta (só JS/TS) |
| Usado por apps do segmento | Afterburner (C++), HWiNFO (C++), **Ashampoo/IObit usam .NET em partes**, LibreHardwareMonitor (.NET) | — | — | Razer Synapse/Cortex usam Electron/CEF em partes (e são criticados pelo peso) |

## Decisão: **C# .NET 8 + WPF** (MVVM com CommunityToolkit.Mvvm)

**Justificativa**

1. **Acesso ao Windows é de primeira classe.** Registro, serviços, WMI, `powercfg`, P/Invoke para `NtSetSystemInformation` (limpar standby list) e ETW são chamadas diretas — sem pontes, sem sidecars.
2. **Sensores prontos.** A LibreHardwareMonitorLib entrega temperaturas, clocks, cargas e VRAM de CPU/GPU/RAM/disco em .NET. Em Rust ou Electron isso exigiria um processo .NET auxiliar de qualquer forma.
3. **Overlay leve.** Uma janela WPF transparente, *click-through* e sem foco, atualizando poucos `TextBlock` 2–4×/s, custa praticamente zero de GPU. Os FPS/frametimes virão de ETW (mesma técnica do PresentMon/RTSS em modo leitura), sem injetar DLL no jogo — o que também evita problemas com anti-cheat.
4. **Visual premium sem limitações.** XAML permite o tema escuro com gradientes de `#44088c`, animações e templates completamente customizados. Escolhemos WPF em vez de WinUI 3 pela transparência de janela (overlay), estabilidade e porque não precisamos de MSIX.
5. **Tamanho do instalador** é o único ponto em que Tauri ganha. Mitigamos com duas opções de publicação: *framework-dependent* (~2 MB, instala o .NET Desktop Runtime 8 se faltar) ou *self-contained* comprimido com trimming parcial.

**Riscos e mitigação**

| Risco | Mitigação |
|---|---|
| WPF é "só Windows" | É exatamente o alvo do produto. A lógica fica em `Core` (net8.0, multiplataforma e 100% testável). |
| Driver de sensores (LibreHardwareMonitor) pode ser sinalizado por antivírus | Sensores ficam isolados atrás de `ISensorService`; se falhar, o app segue funcionando só com WMI. Versões recentes da LHM migraram para o driver PawnIO. |
| Ajustes de sistema precisam de admin | O app roda como usuário comum (`asInvoker`) e pede elevação **apenas** quando o usuário aplica algo que precisa (ver arquitetura). |
