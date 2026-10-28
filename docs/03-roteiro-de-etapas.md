# 3. Roteiro de implementação por etapas

Cada etapa só começa depois que a anterior for testada em um Windows real.

## ✅ Etapa 1 — Base (esta entrega)

* Interface base: janela com barra de título própria, navegação lateral com as 7 seções, tema preto + `#44088c`, transições e hovers animados.
* **Dashboard**: resumo do sistema, gráficos ao vivo de CPU/GPU/RAM, anel de otimizações ativas e botão *Otimizar agora* (perfil Equilibrado).
* **Hardware**: CPU, GPU (VRAM real, driver), RAM (módulos, frequência, canais, slots), placa-mãe/BIOS, discos (tipo, barramento, saúde, temperatura e desgaste quando disponíveis), monitores (resolução/Hz), Windows, fonte (manual) + sensores em tempo real (uso, temperatura, clock, consumo, VRAM).
* **Otimizar**: motor completo com toggles, badges de risco/reversível/admin, perfis prontos, *Aplicar selecionadas*, *Reverter tudo*, histórico com *Desfazer* item a item.
* **Segurança**: ponto de restauração, backup .reg, journal, confirmação para risco alto, elevação sob demanda.
* **3 otimizações funcionais**:
  | Id | Categoria | Risco | O que faz |
  |---|---|---|---|
  | `privacy.telemetry` | Sistema e privacidade | Baixo | `AllowTelemetry=0` (política + chave) e serviço DiagTrack desativado |
  | `gaming.gamedvr` | Jogos | Baixo | Desliga a gravação em segundo plano (GameConfigStore, AppCapture, política AllowGameDVR) |
  | `cpu.ultimate-power-plan` | CPU | Médio | Cria/ativa o plano Desempenho Máximo com GUID próprio (fallback Alto desempenho); reverter reativa o plano anterior e apaga o criado |
* **Testes**: 28 testes do Core (aplicar/reverter, rollback parcial, risco alto, journal após reinício, parsing do powercfg, canais de RAM).

### Como testar a etapa 1 no Windows
1. `dotnet run --project src/NinjasOptimizer.App` (ou o instalador do CI).
2. Hardware: confira os dados com o Gerenciador de Tarefas / CPU-Z.
3. Otimizar → selecione as 3 → *Aplicar selecionadas* → aceite reabrir como admin → confirme.
   * Verifique: `powercfg /getactivescheme`, `reg query "HKCU\System\GameConfigStore" /v GameDVR_Enabled`, `sc qc DiagTrack`.
   * Verifique o ponto de restauração em `rstrui.exe` e os .reg em Configurações → *Abrir pasta de backups*.
4. Histórico → *Desfazer* um item → confira que o valor anterior voltou. Depois *Reverter tudo*.

## Etapa 2 — Catálogo completo de otimizações
Novas ações no Core: `ScheduledTaskAction`, `PowerSettingAction` (`powercfg /setacvalueindex`), `CommandAction` (aplicar/reverter por comando), `StartupEntryAction`, `NetshAction`, e ações pontuais (não persistentes) para limpeza.

| Categoria | Itens |
|---|---|
| Processos e serviços | Serviços desnecessários (SysMain em SSD, Fax, RemoteRegistry, MapsBroker, RetailDemo, WSearch opcional…), gerenciador de inicialização (Run/RunOnce/Startup/Tarefas), prioridade de processos (IFEO `PerfOptions\CpuPriorityClass`), encerrar processos em segundo plano |
| Memória | Limpar standby list (`NtSetSystemInformation` / `SystemMemoryListInformation`), *working set trim*, pagefile (tamanho gerenciado/fixo), `LargeSystemCache`, desativar compressão de memória (risco médio) |
| CPU | Core parking (`CPMINCORES` 100%), `Win32PrioritySeparation` (agendamento para primeiro plano), `PowerThrottlingOff`, *timer resolution* |
| GPU | HAGS (`HwSchMode=2`), MPO, preferência de GPU por app, limpeza de cache de shaders (DirectX/NVIDIA/AMD) |
| Disco | Limpeza de temporários/caches, `cleanmgr /sagerun` e `DISM /StartComponentCleanup` (Windows Update), `Optimize-Volume -ReTrim` (SSD) / desfragmentação (HDD), `NtfsDisableLastAccessUpdate` |
| Rede | Nagle (`TcpAckFrequency`/`TCPNoDelay` por interface), `NetworkThrottlingIndex`, `SystemResponsiveness`, autotuning, DNS (Cloudflare/Google com reversão para DHCP), flush DNS |
| Sistema e privacidade | Efeitos visuais, animações, transparência, apps em segundo plano, dicas/sugestões/anúncios (ContentDeliveryManager), Cortana/Bing na busca, Advertising ID |
| Jogos | Game Mode, Xbox Game Bar, fullscreen optimizations, *modo Gamer* (suspende processos não essenciais ao detectar o jogo e retoma ao fechar) |

Perfis: **Seguro** (só risco baixo), **Equilibrado**, **Máximo Desempenho**, **Gamer**.

## Etapa 3 — Benchmark
CPU (hash/compressão/ponto flutuante, single e multi-thread), RAM (largura de banda com `Span` + latência por *pointer chasing*), disco (sequencial e 4K aleatório com `FILE_FLAG_NO_BUFFERING`), GPU (cena Direct3D 11 via Vortice.Windows com FPS médio). Pontuação normalizada, histórico em SQLite/JSON, comparação antes × depois vinculada ao journal.

## Etapa 4 — Overlay em jogos
* Métricas de quadros via ETW (provedores DxgKrnl / DXGI / D3D9 — mesma técnica do PresentMon), sem injeção de DLL (compatível com anti-cheat). FPS, 1% low e frametime calculados numa janela deslizante.
* Janela WPF transparente `WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`, topmost, atualizada 2–4×/s.
* Configurações: posição (cantos), escala, opacidade, métricas, atalho global (`RegisterHotKey`).
* Limitação documentada: jogos em tela cheia exclusiva não exibem janelas por cima; o modo *borderless/fullscreen otimizado* do Windows 10/11 funciona.

## Etapa 5 — Compatibilidade e gargalos
Base local de desempenho relativo de CPUs/GPUs (tiers), regras de equilíbrio (CPU × GPU × resolução/Hz do monitor), RAM (quantidade, single channel, frequência abaixo do suportado/XMP desligado), armazenamento (Windows em HDD, SSD SATA vs NVMe, espaço livre), fonte informada × consumo estimado. Saída: nota 0–100, gargalos com explicação e upgrades ordenados por impacto/custo.

## Etapa 6 — Polimento e distribuição
Assinatura de código, atualização automática, telemetria **zero**, acessibilidade, localização (pt-BR/en), testes de UI.
