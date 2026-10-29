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
1. `dotnet run --project src/NinjaOptimizer.App` (ou o instalador do CI).
2. Hardware: confira os dados com o Gerenciador de Tarefas / CPU-Z.
3. Otimizar → selecione as 3 → *Aplicar selecionadas* → aceite reabrir como admin → confirme.
   * Verifique: `powercfg /getactivescheme`, `reg query "HKCU\System\GameConfigStore" /v GameDVR_Enabled`, `sc qc DiagTrack`.
   * Verifique o ponto de restauração em `rstrui.exe` e os .reg em Configurações → *Abrir pasta de backups*.
4. Histórico → *Desfazer* um item → confira que o valor anterior voltou. Depois *Reverter tudo*.

## ✅ Etapa 2A — Sistema, privacidade e jogos

14 ajustes novos, todos de registro (reversíveis item a item). Os que mudam HKCU não pedem administrador.

| Id | O que faz | Risco | Perfis |
|---|---|---|---|
| `privacy.ads-tips` | Dicas, sugestões e apps patrocinados (ContentDeliveryManager) | Baixo | Todos |
| `privacy.advertising-id` | ID de publicidade e experiências personalizadas | Baixo | Todos |
| `privacy.activity-history` | Histórico de atividades (política) | Baixo | Todos |
| `privacy.bing-search` | Bing na busca do Iniciar | Baixo | Equilibrado, Máximo, Gamer |
| `system.background-apps` | Apps da Store em segundo plano | Médio | Máximo, Gamer |
| `system.animations` | Animações de janela, barra de tarefas e Aero Peek | Baixo | Equilibrado, Máximo, Gamer |
| `system.menu-delay` | Atraso dos menus 400 → 100 ms | Baixo | Equilibrado, Máximo, Gamer |
| `system.transparency` | Efeitos de transparência | Baixo | Máximo |
| `gaming.game-mode` | Modo de Jogo ligado | Baixo | Todos |
| `gaming.game-bar-shortcuts` | Botão Xbox do controle não abre a Game Bar | Baixo | Máximo, Gamer |
| `gaming.fullscreen-optimizations` | Otimizações de tela cheia desligadas | Médio | Gamer |
| `gaming.mouse-acceleration` | Aceleração do mouse desligada | Baixo | Gamer |
| `gaming.scheduler-priority` | Perfil "Games" do agendador multimídia | Baixo | Máximo, Gamer |
| `gaming.sticky-keys` | Atalho Shift ×5 das Teclas de Aderência | Baixo | Gamer |

Testes: cada ajuste do catálogo é aplicado e revertido automaticamente, com o registro vazio e com valores que já existiam (74 testes).

### Como testar a etapa 2A no Windows
1. Otimizar → filtro **Sistema e privacidade** e **Jogos**: confira textos, selos e toggles.
2. Perfil **Gamer** → *Aplicar selecionadas*. Confira, por exemplo:
   * `reg query "HKCU\Control Panel\Mouse" /v MouseSpeed` → `0`
   * `reg query "HKCU\Software\Microsoft\GameBar" /v AutoGameModeEnabled` → `0x1`
   * Configurações do Windows → Jogos → Modo de Jogo ligado.
3. Saia e entre de novo na conta: animações reduzidas, busca sem resultados da web, mouse sem aceleração.
4. *Reverter tudo* e confira que os valores voltaram (ou sumiram, se não existiam).

## ✅ Etapa 2C — Memória e disco

### Ajustes (reversíveis)

| Id | O que faz | Como | Risco | Perfis |
|---|---|---|---|---|
| `storage.trim` | Garante o TRIM do SSD ligado | `DisableDeleteNotification = 0` | Baixo | Todos |
| `storage.last-access` | Não grava a data de último acesso | `NtfsDisableLastAccessUpdate = 0x80000001` (reiniciar) | Baixo | Equilibrado, Máximo, Gamer |
| `storage.hibernation-off` | Desativa a hibernação (libera ~RAM no disco) | `powercfg /h off`, estado lido do registro | Médio | Nenhum |
| `memory.pagefile-auto` | Arquivo de paginação automático | `PagingFiles = ?:\pagefile.sys` (reiniciar) | Médio | Nenhum (correção) |
| `memory.compression-off` | Desativa a compressão de memória (32 GB+) | `Disable-MMAgent -MemoryCompression` (reiniciar) | Médio | Nenhum |

Nova ação: `CommandStateAction` (recurso ligado/desligado por comando; o snapshot guarda o estado e os comandos de volta).

### Limpeza e manutenção (aba nova em Otimizar; não tem desfazer)

| Tarefa | O que faz | Admin |
|---|---|---|
| Apagar arquivos temporários | `%TEMP%` (e `Windows\Temp` como admin), só arquivos com mais de 1 dia | Não |
| Esvaziar a Lixeira | `SHEmptyRecycleBin` | Não |
| Limpar cache de shaders | DirectX, NVIDIA, AMD e Intel | Não |
| Limpar downloads do Windows Update | Pausa `wuauserv`/`bits`, limpa `SoftwareDistribution\Download`, retoma | Sim |
| Limpar componentes antigos | `DISM /StartComponentCleanup` | Sim |
| Otimizar unidades | `defrag /C /O` (TRIM no SSD, desfragmentação no HDD) | Sim |
| Liberar memória em espera | `NtSetSystemInformation` (MemoryPurgeStandbyList) | Sim |

Cada tarefa mostra quanto deve liberar antes de rodar e quanto liberou depois; a última execução fica em `maintenance.json`.
A limpeza de pastas nunca segue atalhos de pasta (pontos de junção) para fora do lugar limpo e pula arquivos em uso.

### Como testar a etapa 2C
1. Otimizar → aba **Limpeza e manutenção**: confira as estimativas.
2. Rode "Apagar arquivos temporários" e "Limpar cache de shaders": o valor liberado deve bater com a estimativa.
3. Como administrador, rode "Liberar memória em espera" e veja a memória livre subir no Gerenciador de Tarefas.
4. Aba **Ajustes** → filtro **Disco**/**Memória**: aplique e reverta "Desativar a hibernação" e confira com `powercfg /a`.

## ✅ Etapa 2B — CPU e GPU

| Id | O que faz | Como | Risco | Perfis |
|---|---|---|---|---|
| `cpu.core-parking` | Núcleos sempre prontos (core parking off) | Plano ativo, CPMINCORES = 100 (AC) | Baixo | Máximo, Gamer |
| `cpu.foreground-priority` | Prioridade ao programa em primeiro plano | `Win32PrioritySeparation = 0x26` | Baixo | Equilibrado, Máximo, Gamer |
| `cpu.power-throttling` | Sem limitação de energia de processos | `PowerThrottlingOff = 1` | Baixo | Máximo, Gamer |
| `gpu.hags` | Agendamento de GPU acelerado por hardware | `HwSchMode = 2` (reiniciar) | Médio | Máximo, Gamer |
| `gpu.windowed-optimizations` | Otimizações para jogos em janela | `SwapEffectUpgradeEnable=1`, preservando HDR automático e VRR | Baixo | Equilibrado, Máximo, Gamer |
| `gpu.pcie-power` | PCI Express sem economia de energia | Plano ativo, ASPM = desligado (AC) | Baixo | Máximo, Gamer |
| `gpu.mpo-off` | Desativa MPO (tela piscando) | `OverlayTestMode = 5` (reiniciar) | Médio | Nenhum (correção opcional) |

Novas ações: `PowerSettingAction` (configurações do plano de energia, inclusive escondidas; reverte no mesmo plano e ignora planos já apagados) e `DirectXSettingAction` (muda uma opção sem apagar as outras).

### Como testar a etapa 2B
1. Benchmark → rode uma vez e use como base.
2. Perfil **Gamer** → *Aplicar selecionadas* → reinicie o PC.
3. Confira: `powercfg /query scheme_current sub_processor CPMINCORES` (índice CA `0x00000064`) e Configurações → Sistema → Tela → Elementos gráficos → *Alterar configurações gráficas padrão* (agendamento de GPU ligado).
4. Rode o benchmark de novo e compare. CPU e latência de memória são os testes com mais chance de mudar.
5. *Reverter tudo* e confira que o valor do core parking voltou ao anterior.

## ✅ Benchmark básico (antecipado da Etapa 3)

Para medir cada bloco da Etapa 2 conforme ele fica pronto.

| Teste | Como mede | Referência (1.000 pontos) |
|---|---|---|
| CPU · um núcleo / todos os núcleos | SHA-256 + crivo de primos + ponto flutuante, rodadas por segundo | 8.000 / 50.000 rodadas/s |
| Memória · velocidade | Cópia entre dois blocos de 128 MB (leitura + escrita) | 25 GB/s |
| Memória · latência | Saltos aleatórios encadeados em 128 MB | 90 ns |
| Disco · sequencial | Arquivo de 256 MB, blocos de 1 MB, sem cache do Windows | 2.000 MB/s |
| Disco · aleatório 4K | Blocos de 4 KB, uma operação por vez, sem cache | 60 MB/s |

* PC de referência: classe Ryzen 5 3600, DDR4-3200 dual channel, SSD NVMe PCIe 3.0. Os valores são fixos para o histórico continuar comparável.
* Cada teste roda 3 vezes e usa a mediana. Mudanças abaixo de 3% (ou abaixo da variação medida entre as amostras) aparecem como "Sem mudança".
* Histórico em `benchmarks.json`, com as otimizações ativas em cada medição. A base de comparação é a medição anterior, ou a que você escolher ("Usar como base").
* Ajustes que só valem depois de reiniciar aparecem como "Aplicado · reinicie para valer" até o próximo reinício.

Fica para a Etapa 3: teste de GPU (cena 3D) e gráficos do histórico.

### Como testar
1. Benchmark → *Rodar benchmark* (cerca de 1 minuto). Rode duas vezes seguidas: a maioria dos testes deve dar "Sem mudança".
2. Aplique o plano *Desempenho Máximo* (Otimizar → CPU) e rode de novo: os testes de CPU tendem a subir ou ficar iguais.

## ✅ Etapa 2D — Serviços, rede e inicialização

### Ajustes (reversíveis)
| Ajuste | Perfis | Risco |
|---|---|---|
| Desativar serviços raramente usados (Fax, Mapas, Varejo, Registro Remoto, WMP Network) | todos | Baixo |
| Desativar SysMain (Superfetch) | nenhum (opcional) | Médio |
| Desativar indexação de pesquisa (WSearch) | nenhum (opcional) | Médio |
| Desativar serviço de impressão (Spooler) | nenhum (opcional) | Médio |
| Remover limite de rede multimídia (NetworkThrottlingIndex / SystemResponsiveness 10) | Equilibrado, Máximo, Gamer | Baixo · reiniciar |
| Desativar Nagle (TcpAckFrequency/TCPNoDelay em cada placa) | Gamer | Baixo · reiniciar |
| DNS Cloudflare 1.1.1.1 (reverte para o DNS anterior de cada placa, ou automático) | nenhum (opcional) | Médio |

Novas ações: `RegistryEachSubkeyAction` (mesmo valor em todas as subchaves, com snapshot por subchave) e `DnsServersAction` (Get-NetAdapter / Set-DnsClientServerAddress).

### Manutenção
- **Limpar cache de DNS** (`ipconfig /flushdns`).

### Aba Inicialização (em Otimizar)
Lista os programas que abrem com o Windows (Run da conta, Run da máquina 64/32 bits, pastas Inicializar) e liga/desliga com o mesmo mecanismo do Gerenciador de Tarefas (`StartupApproved`). Desativar não apaga nada. Itens de "todos os usuários" pedem administrador.

### Como testar a etapa 2D
1. Otimizar → filtro **Rede**: aplique "Remover o limite de rede" e "Nagle"; confira o histórico e use **Desfazer**.
2. Aplique **DNS Cloudflare**, rode `ipconfig /all` (DNS = 1.1.1.1) e depois **Desfazer**: o DNS deve voltar ao anterior.
3. Aba **Inicialização**: desligue um programa, abra o Gerenciador de Tarefas → Inicializar: ele deve aparecer como Desabilitado. Ligue de volta.
4. Limpeza → **Limpar cache de DNS**.

## ✅ Etapa 2E — Modo Gamer

Página nova **Modo Gamer** no menu lateral.
- **Detecção de jogo** (a cada 3 s): executáveis em pastas da Steam (`steamapps\common`), Epic, Riot, GOG, Xbox, Battle.net, Ubisoft e EA, mais a lista "Meus jogos". Ignora launchers, crash handlers e instaladores; prefere o que está em primeiro plano.
- **Pausa** (NtSuspendProcess) os programas marcados na lista, só da sua conta. Lista padrão: OneDrive, Dropbox, Google Drive, Teams, Skype, Vincular ao Celular, Adobe/Creative Cloud. Você liga/desliga cada programa aberto.
- **Nunca pausa**: partes do Windows, Defender, áudio, drivers NVIDIA/AMD/Intel, anti-cheat (EAC, BattlEye, Vanguard, FACEIT) e o próprio app.
- **Segurança**: os pausados são gravados em `gamemode.json` a cada pausa; ao fechar o app tudo é retomado, e se ele travar, retoma na próxima abertura. Antes de retomar, confere o horário de início do processo para não mexer num PID reaproveitado.
- Ligado sozinho → desliga sozinho quando o jogo fecha. Ligado à mão → só desliga à mão.

### Como testar a etapa 2E
1. Abra o OneDrive (ou outro da lista) e vá em **Modo Gamer → Ligar agora**. No Gerenciador de Tarefas → Detalhes, ele aparece como "Suspenso".
2. **Desligar e retomar tudo**: volta a "Em execução".
3. Com "Ligar sozinho" ativo, abra um jogo da Steam: o status muda para "Modo Gamer ligado · nome do jogo". Feche o jogo: em até 3 s tudo é retomado.
4. Ligue o modo, feche o NinjaOptimizer e confira que nada ficou suspenso.

## Catálogo extra — análise do iGust Windows Boost

Funcionalidades tiradas da engenharia reversa de um .bat de otimização (`otim-inspo/analises/features-para-ninjaoptimizer.md`), reimplementadas em C#. Nada do .bat é executado. Feito em blocos A–F; o bloco G (Integridade de Memória/VBS) só com aprovação explícita.

### ✅ A — Condições de aplicabilidade
- `TweakCondition` (`Optimization/Conditions.cs`): `SystemDriveIsSsd`, `IsDesktop`, `IsPortable`, `HasGpuVendor`, `ServiceExists`, `MinWindowsBuild`. `Tweak.Conditions` (vazio = sempre).
- `SystemContext.Profile` (`SystemProfile`): tipo do disco do Windows (MSFT_Partition → MSFT_PhysicalDisk + `DiskTypeHeuristics`), notebook (`ChassisTypes` portátil ou `Win32_Battery`), fabricante das GPUs pelo PCI Vendor ID (10DE/1002/8086) e build. Lido em segundo plano ao abrir o app (`SystemProfileProbe`).
- O motor nunca aplica item não aplicável (`TweakOutcome.NotApplicable`). Perfis e a otimização rápida pulam sem avisar. Sem certeza (disco desconhecido, perfil ainda carregando) → bloqueado com o motivo.
- Interface: toggle desabilitado e o motivo com um cadeado (ex.: "Só para SSD — o Windows está num HD").
- `services.sysmain` agora tem `Conditions = [SystemDriveIsSsd]`.

| Id | Mudança |
|---|---|
| `services.sysmain` | só em SSD |

**Como testar no Windows:** num PC com o Windows num HD, "Desativar o SysMain" aparece bloqueado com o motivo; num SSD, normal. O log (`%LocalAppData%\NinjaOptimizer\logs`) mostra a linha "Perfil do PC: disco do Windows=…".

### ✅ B — Serviços e privacidade

| Id | O que faz | Perfis | Risco | Condições |
|---|---|---|---|---|
| `services.error-reporting` | `WerSvc` e `dmwappushservice` desativados + `Windows Error Reporting\Disabled=1` | Equilibrado, Máximo, Gamer | Baixo | — |
| `privacy.app-launch-tracking` | `Start_TrackProgs=0` | todos | Baixo | — |
| `gpu.nvidia-telemetry` | `NvTelemetryContainer` desativado | Máximo, Gamer | Baixo | GPU NVIDIA **e** serviço existe |

**Como testar no Windows:** aplique os três (o da NVIDIA só fica disponível com driver antigo que tem o serviço; nos atuais aparece bloqueado com o motivo). Em `services.msc`, "Serviço de Relatório de Erros do Windows" fica Desativado. Clique em **Desfazer** no histórico: volta a Manual. Com o rastreio desligado, a seção "Mais usados" do Iniciar some.

### ✅ C — Visual

| Id | O que faz | Perfis | Risco | Reinício |
|---|---|---|---|---|
| `system.visual-best-performance` | `VisualEffects\VisualFXSetting=2` + `Control Panel\Desktop\UserPreferencesMask` = `90 12 03 80 10 00 00 00` (o binário original fica no snapshot) | Máximo | Baixo | sair e entrar na conta |

Não grava `VisualFXSetting` em `Control Panel\Desktop` (placebo do .bat). Não conflita com `system.animations`.

**Como testar no Windows:** aplique, saia e entre na conta: sem sombra sob as janelas e arrastar mostra só o contorno. Em `SystemPropertiesPerformance.exe` aparece "Ajustar para obter um melhor desempenho". **Desfazer** + sair/entrar volta exatamente ao que era.

### ✅ D — Energia e periféricos

| Id | O que faz | Perfis | Risco |
|---|---|---|---|
| `peripherals.usb-selective-suspend` | `powercfg` subgrupo USB `2a737441…` / `48e6b7a6…` = 0, **só na tomada** (bateria não muda) | Gamer | Baixo |
| `network.adapter-power-saving` | `Set-NetAdapterPowerManagement -AllowComputerToTurnOffDevice Disabled` nas placas físicas ativas que estavam `Enabled` | nenhum | Médio |

Nova ação `NetAdapterPowerAction`: guarda o valor por `InterfaceGuid`; pula placas desativadas e as que o driver não aceita (falha só se todas recusarem); na reversão ignora placa que não existe mais; GUID e valor validados antes de entrar no comando. Não usa `Disable-NetAdapterPowerManagement` (desligaria o Wake-on-LAN). Novo campo `Tweak.PortableWarning`: em notebook a linha mostra "vai gastar um pouco mais de bateria".

**Como testar no Windows:** aplique "Não suspender dispositivos USB" e confira em Opções de energia → Alterar configurações avançadas → Configurações USB: "Desabilitado" em "Na tomada" e inalterado em "Na bateria". Aplique "Não desligar a placa de rede": no Gerenciador de Dispositivos → placa → Gerenciamento de Energia, a primeira caixa fica desmarcada e a do Wake-on-LAN como estava. **Desfazer** marca de novo.

### ✅ E — Prioridade de CPU por jogo (Modo Gamer)

| Id | O que faz | Perfis | Risco |
|---|---|---|---|
| `gaming.process-priority:<exe>` (um por jogo) | `IFEO\<exe>\PerfOptions\CpuPriorityClass` = 6 (Acima do normal, padrão) ou 3 (Alta, com aviso) | nenhum | Médio |

- Página **Modo Gamer → Prioridade de CPU**: lista os jogos abertos agora, os de "Meus jogos" e os que já têm prioridade. Cada um tem Normal / Acima do normal / Alta. Passa pelo motor: ponto de restauração, histórico e **Desfazer** iguais aos outros ajustes.
- Nova ação `ProcessPriorityAction`: aceita só nome de arquivo `.exe` (sem pasta, sem caracteres inválidos) e só valores de `PerfOptions` (1, 2, 3, 5, 6 — nunca 4/Tempo real). Escreve só `CpuPriorityClass`; outros valores do IFEO ficam intactos. Na reversão volta o valor anterior ou apaga, e remove `PerfOptions` e `IFEO\<exe>` só se foram criadas pelo ajuste e ficaram vazias. Revalida o nome ao reverter (o journal fica em disco).
- Infra: `IRegistryAccess.DeleteKeyIfEmpty`.
- Detecção de jogo do Modo Gamer passou a reconhecer também executáveis conhecidos fora das lojas (`ProcessRules.KnownGames`, tirados da análise; não inclui `javaw.exe`). Os nomes não foram conferidos um a um: o executável detectado rodando tem preferência.
- Avisos: Alta pode fazer o áudio do Discord/OBS engasgar; anti-cheats podem ignorar; `javaw.exe` vale para todo Java.

**Como testar no Windows (como administrador):** abra um jogo, vá em Modo Gamer → Prioridade de CPU e escolha "Acima do normal". Feche e abra o jogo: no Gerenciador de Tarefas → Detalhes → botão direito → Definir prioridade, aparece "Acima do normal". Volte para Normal (ou **Desfazer** no histórico de Otimizar): em `regedit`, `Image File Execution Options\<jogo>.exe` some se não existia antes.

### ✅ F — Manutenção e reparos (aba Limpeza e manutenção)

| Id | O que faz | Admin | Duração |
|---|---|---|---|
| `maintenance.repair-system-files` | `DISM /Online /Cleanup-Image /RestoreHealth` e depois `sfc /scannow`; resultado pelos códigos de saída (o sfc escreve em UTF-16) | sim | 10–30 min, precisa de internet |
| `maintenance.restart-explorer` | encerra o `explorer.exe` da sessão; espera o Windows reabrir e, se não reabrir, abre sem elevação (`runas /trustlevel`) | não | segundos |
| `cleanup.recent-items` | apaga só `*.lnk` do primeiro nível de `Recent` (nunca `AutomaticDestinations`: as pastas fixadas ficam) | não | segundos |
| `maintenance.repair-windows-update` | para `wuauserv`/`bits`/`cryptsvc`, renomeia `SoftwareDistribution` e `catroot2` para `.bak-AAAAMMDD-HHMMSS`, religa os serviços sempre (mesmo se falhar); não muda o tipo de início | sim | ~1 min |
| `maintenance.repair-network` | salva `ipconfig /all` no log, `netsh winsock reset`, `netsh int ip reset <log>`, `ipconfig /flushdns`; avisa se DNS/Nagle do app estão aplicados; pede reinício | sim | segundos |
| `maintenance.check-disk` | `chkdsk <unidade do Windows> /scan` (online, só NTFS); código 3 oferece, com confirmação, a correção no próximo boot (`Repair-Volume -SpotFix`, que não depende de responder S/N traduzido) | sim | alguns minutos |
| `maintenance.restart-audio` | para `Audiosrv` e `AudioEndpointBuilder`, volta Desativado para Automático, inicia `AudioEndpointBuilder` e `Audiosrv` | sim | segundos |
| `maintenance.restore-default-services` | serviços essenciais **Desativados** voltam ao padrão (Bluetooth, Wi-Fi, DHCP, Netman, modo avião, áudio, BITS, Windows Update, câmera, Xbox); não toca nos que um ajuste ativo do app desativou | sim | segundos |
| `maintenance.repair-store` | religa `BITS`/`wuauserv`/`ClipSVC`/`InstallService` se desativados (só como admin; sem admin avisa), `wsreset.exe` e re-registra só `Microsoft.WindowsStore` e `Microsoft.DesktopAppInstaller` do usuário atual | só p/ serviços | 1–2 min |

- Infra: `MaintenanceResult.FollowUp` (próximo passo com confirmação) e `RequiresRestart`; `JournalQueries` (serviços mexidos e ajustes ativos no histórico).
- **Em aberto:** `NlaSvc` ficou fora de "Restaurar serviços" porque o padrão no Windows 11 não foi confirmado.
- O chkdsk `/scan` em pt-BR: o resultado vem do código de saída, não do texto.

**Como testar no Windows (como administrador):**
1. **Reiniciar o Explorer**: a barra de tarefas some e volta em poucos segundos; no Gerenciador de Tarefas → Detalhes, coluna "Elevado" = Não.
2. **Limpar itens recentes**: o Acesso Rápido perde os recentes, mas as pastas fixadas continuam.
3. **Verificar erros no disco**: termina em alguns minutos sem reiniciar.
4. **Reiniciar o áudio**: o som some e volta.
5. **Restaurar serviços**: desative o Bluetooth em `services.msc`, rode e confira que voltou para Manual.
6. **Reparar a Store**: a Store abre no fim.
7. **Reparar Windows Update / rede / arquivos do Windows**: confira as pastas `.bak-…` em `C:\Windows`, o arquivo `rede-antes-do-reparo-…txt` nos logs e o resumo do DISM/SFC.

### ✅ H — Avisos de consequências

- `Tweak.Consequences` e `MaintenanceTask.Consequences` (frases curtas, para leigo). `Tweak.ConsequencesFor(portátil)` soma o `PortableWarning` em notebook. `MaintenanceTask.HasSideEffects = false` só para o que só verifica (otimizar unidades, verificar disco).
- **Interface (Otimizar):** risco Baixo com consequências → "O que muda ▸" expansível; Médio → caixa amarela "Antes de aplicar" sempre visível; Alto → caixa vermelha + **"Entendi as consequências"**, sem o qual o toggle e a seleção em lote não aceitam o item. O diálogo de risco alto do motor lista as consequências de novo. Recomendações do Dashboard nunca sugerem risco alto.
- **Limpeza e manutenção:** o diálogo de confirmação lista "O que pode acontecer".
- **Testes obrigatórios:** todo ajuste com risco Médio/Alto tem consequências (`CatalogTests`); toda tarefa de manutenção com efeito tem consequências — novo projeto `tests/NinjaOptimizer.Windows.Tests` constrói o catálogo real do Windows sem executar nada (também roda no `build.ps1`).
- Preenchido em todos os itens Médios atuais + `services.error-reporting`, prioridade por jogo e todas as tarefas de manutenção.

**Como testar no Windows:** em Otimizar, itens como "Desativar a indexação de pesquisa" mostram a caixa amarela; itens de risco baixo com consequências mostram "O que muda ▸". Em Limpeza, "Esvaziar a Lixeira" lista "Os arquivos da Lixeira são apagados para sempre" no diálogo.

### ✅ I — Apps do Windows (debloat com escolha)

Nova aba **Otimizar → Apps do Windows**, fora de todos os perfis.

- Lista só os apps instalados da sua conta (`Get-AppxPackage` → JSON), sem frameworks, não-removíveis, pacotes do sistema e a lista **Protegidos** (Store, winget, Segurança do Windows, Shell/Iniciar, `MicrosoftWindows.Client.*`, contas, Terminal, Bloco de Notas, Paint, Captura, Calculadora, extensões de mídia e frameworks). Sem curingas: `SpotifyAB.SpotifyMusic` nunca é pego por `*music*`.
- Catálogo com 24 entradas (`Debloat/AppCatalog.cs`): nome amigável, selo **Seguro remover / Cuidado / Não recomendado** e consequências. Apps fora do catálogo ficam atrás de "Mostrar outros apps instalados", com aviso genérico. Não recomendado e desconhecido exigem "Entendi as consequências". Nada vem marcado.
- "Remover selecionados" mostra o resumo com todas as consequências antes. Cada app vira um ajuste `debloat:<nome>` pelo motor (ponto de restauração + histórico). Risco: Seguro = Baixo, Cuidado = Médio, Não recomendado/desconhecido = Alto.
- Remoção: `Remove-AppxPackage -Package <PackageFullName>` (só sua conta). Opção avançada desmarcada "Remover também para contas novas" (`Remove-AppxProvisionedPackage`, risco alto, administrador).
- **Desfazer** (no histórico): pacote ainda provisionado → `Add-AppxPackage -Register … -DisableDevelopmentMode`; senão Id da Store → `winget install --source msstore`; senão abre `ms-windows-store://pdp/?PFN=…` e o item fica no histórico para tentar de novo.
- **OneDrive** à parte: `winget uninstall/install --id Microsoft.OneDrive`.
- Segurança: nomes de pacote só entram no PowerShell depois de validados contra `^[A-Za-z0-9._-]+$`; Id da Store contra `^[A-Z0-9]{12}$`; caminho do provisionado validado.
- **Em aberto:** os Ids da Store vieram da análise e não foram conferidos com `winget search --source msstore` (sem Windows aqui). Se algum não bater, o desfazer cai no link da Store.

**Como testar no Windows:** abra a aba, marque "Notícias" e "Clima", clique em Remover selecionados e leia o resumo. Confira que sumiram do Iniciar. No histórico de Ajustes, **Desfazer**: voltam (precisa de internet se não estiverem provisionados).

### ✅ J — Ajustes de risco (todos fora dos perfis, com consequências)

| Id | O que faz | Risco | Reinício | Condição |
|---|---|---|---|---|
| `security.memory-integrity-off` | `HypervisorEnforcedCodeIntegrity\Enabled=0` + `EnableVirtualizationBasedSecurity=0` | Alto | sim | bloqueia se imposto por política (`Policies\…\DeviceGuard`) ou travado por UEFI (`Locked=1`) |
| `security.credential-guard-off` | `Lsa\LsaCfgFlags=0` | Alto | sim | só se rodando (`Win32_DeviceGuard`); bloqueia se `LsaCfgFlags=1` (UEFI) |
| `security.hypervisor-off` | `bcdedit /set {current} hypervisorlaunchtype Off` | Alto | sim | — |
| `services.biometrics-off` | `WbioSrvc` desativado | Médio | não | — |
| `gaming.xbox-services-off` | `XblAuthManager`, `XblGameSave`, `XboxNetApiSvc`, `XboxGipSvc` desativados | Médio | não | — |
| `privacy.cortana-off` | `AllowCortana=0` + `CortanaConsent=0` | Baixo | não | `MaxWindowsBuild(21999)` (só Windows 10) |
| `system.taskbar-search-hidden` | `SearchboxTaskbarMode=0` | Baixo | não | — |
| `disk.prefetcher-off` | `EnablePrefetcher=0` (não grava `EnableSuperfetch`) | Médio | sim | aviso extra se o Windows está num HD |
| `compat.pca-off` | `PcaSvc` desativado + `DisablePCA=1` + `DisableInventory=1` | Médio | não | — |
| `compat.engine-off` | `DisableEngine=1` | Alto | sim | — |
| `update.block-internet-locations` | `DoNotConnectToWindowsUpdateInternetLocations=1` | Alto | não | aviso extra se não existe `WUServer` (PC sem WSUS) |

- Infra nova: `BcdSettingAction` (lê `bcdedit /enum {current}`; reverte com `/set` do valor anterior ou `/deletevalue` se não existia; nome e valor só `[A-Za-z]+`); condições `MaxWindowsBuild`, `DeviceGuardChangeable`, `CredentialGuardChangeable`; `SystemProfile.CredentialGuardRunning`; `Tweak.ContextWarnings` (avisos que dependem do PC); `Tweak.ManualOnly` (risco baixo fora dos perfis de propósito: Cortana e pesquisa da barra).

**Como testar no Windows (como administrador):** em Otimizar, os itens de risco alto mostram a caixa vermelha e só deixam selecionar depois de "Entendi as consequências". Aplique "Não carregar o hipervisor", rode `bcdedit /enum {current}` (aparece `hypervisorlaunchtype Off`), **Desfazer** e confira que voltou (ou sumiu, se não existia). Num PC com política de empresa, "Integridade de Memória" aparece bloqueada com o motivo.

### ✅ K — Opções avançadas e reparos

**Opções avançadas** (desmarcadas, consequências ao lado; o diálogo de confirmação lista as escolhidas):

| Tarefa | Opção | Observação |
|---|---|---|
| `maintenance.repair-network` | Também pedir um novo IP (`ipconfig /release` + `/renew`) | |
| `maintenance.check-disk` | Verificação completa no próximo boot (`chkdsk /f /r`) | risco alto; responde o S/N do chkdsk com a letra do idioma do Windows (S/Y/O/J) |
| `maintenance.repair-store` | Re-registrar todos os apps do sistema (todas as contas) | precisa de administrador |
| `cleanup.recent-items` | Também limpar listas de atalho e fixados (`AutomaticDestinations`/`CustomDestinations`) | |
| `maintenance.repair-camera`/`-microphone` | Liberar para os apps · para todos os usuários · remover bloqueios de política (risco alto) · procurar drivers (`UsoClient StartScan`) · microfone: incluir a classe `MEDIA` | as liberações viram ajustes no histórico, com Desfazer |

**Novas tarefas:**

| Id | O que faz |
|---|---|
| `memory.working-sets` | `NtSetSystemInformation` com `MemoryEmptyWorkingSets` (opção: `MemoryFlushModifiedList`); admin |
| `maintenance.repair-xbox-apps` | serviços Xbox Desativados → Manual, reinicia os que rodam, re-registra só `GamingApp`, `XboxGamingOverlay`, `XboxIdentityProvider`, `Xbox.TCUI`. Se `gaming.xbox-services-off` está ativo, não mexe por fora: oferece Desfazer o ajuste |
| `maintenance.repair-camera` | pergunta quais de Teams/Zoom/Discord/Skype/OBS/Câmera fechar (lista com caixas), fecha pedindo e só força se você mandar; reinicia `FrameServer`/`FrameServerMonitor` sem mudar o tipo de início; desliga e liga só `Camera`/`Image`; `pnputil /scan-devices`; re-registra `Microsoft.WindowsCamera`; mostra o estado e abre `ms-settings:privacy-webcam` |
| `maintenance.repair-microphone` | o mesmo com `Audiosrv`/`AudioEndpointBuilder` na ordem certa e a classe `AudioEndpoint`; abre `ms-settings:sound` |
| `maintenance.force-shutdown` | `shutdown /s /f /t 0` |

**Novos ajustes** (fora dos perfis, usados pelas opções dos reparos e também visíveis em Otimizar): `privacy.camera-allow-apps`, `privacy.camera-allow-apps-all-users`, `privacy.camera-remove-policy` (alto), `privacy.microphone-allow-apps`, `privacy.microphone-allow-apps-all-users`, `privacy.microphone-remove-policy` (alto). Nova ação `RegistryDeleteValueAction` (mesmo snapshot do `RegistryValueAction`, então o Desfazer devolve a política exatamente).

**Reiniciar agora:** depois de aplicar ajustes que pedem reinício (ou uma manutenção que pede), o app oferece "Reiniciar agora" (`shutdown /r /t 0`) pedindo para salvar o trabalho.

Infra: `MaintenanceTask.Options`/`SelectedOptions`, `IMaintenanceUi` (perguntas no meio da execução, com lista de caixas de seleção), `IAppCloser`.

**Como testar no Windows (como administrador):**
1. Com o Discord aberto, rode **Reparar a câmera**: ele pergunta se fecha o Discord; a webcam pisca no Gerenciador de Dispositivos e as configurações de privacidade abrem.
2. Marque "Liberar a câmera para os apps" e rode de novo: aparece no histórico de Otimizar com **Desfazer**.
3. **Reparar o microfone**: o som cai por alguns segundos e volta.
4. Aplique "Desativar os serviços Xbox" e rode **Reparar os apps Xbox**: ele oferece desfazer o ajuste em vez de mexer por fora.
5. Em Verificar erros no disco, marque a verificação completa: o chkdsk agenda para o próximo boot (não reinicie se não puder deixar o PC parado).

### ✅ L — Modo Gamer: memória em espera e timer (o que o ISLC fazia)

- **Limpar a memória em espera sozinho** (desligado por padrão): a cada 5 s, só com o Modo Gamer ligado, limpa a lista em espera quando ela passa de **1024 MB** *e* a memória livre fica abaixo de **1024 MB** (os dois limites são editáveis). Intervalo mínimo de 30 s entre limpezas. Reaproveita a limpeza do `memory.standby-list` (precisa de administrador).
- **Timer de alta resolução (0,5 ms)** (desligado por padrão): `NtSetTimerResolution` ao ligar o Modo Gamer, solto ao desligar e ao fechar o app (o Windows solta sozinho se o app travar).
- Novo ajuste `gaming.global-timer-resolution` (`kernel\GlobalTimerResolutionRequests=1`, Windows 11, Médio, reinício, fora dos perfis): sem ele, no Windows 11 o timer pedido vale só para o próprio app — a página avisa.
- Consequências na própria página: engasgo curto na limpeza; mais energia e bateria com o timer.

**Como testar no Windows (como administrador):** ligue "Limpar a memória em espera sozinho", baixe os limites (ex.: 256/8000 MB) e ligue o Modo Gamer: o log mostra "memória em espera limpa automaticamente". Com o timer ligado, `powercfg /energy` (ou o ClockRes da Sysinternals) mostra 0,5 ms enquanto o modo está ligado.

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
