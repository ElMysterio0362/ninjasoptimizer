# 5. Identidade visual

Implementação no app do guia de marca **NinjaOptimizer** (design: [canvas da marca](https://claude.ai/code/artifact/2403bbe2-4ab3-4388-bbb8-9df2c0c5fa41)).

## Nome e logo

* O nome é **NinjaOptimizer**, numa palavra só. Wordmark: **Ninja** em Sora 700 + **Optimizer** em Sora 300, Roxo 300 (`#B98BFF`).
* Símbolo: shuriken "varrido" com furo central, degradê `#D5BAFF → #7B2FE0`. Abaixo de 24 px, versão sem furo.
* O símbolo gira enquanto o app trabalha (indicador de carregamento, estilo `Spinner`). O logo inteiro nunca gira.

## Onde cada coisa está

| O quê | Arquivo |
|---|---|
| Cores, degradês e fontes | `src/NinjaOptimizer.App/Theme/Colors.xaml` |
| Ícones (contorno 1,75 px, grade 24) e o símbolo | `src/NinjaOptimizer.App/Theme/Icons.xaml`, gerado por `build/brand/generate_icons.py` |
| Botões, toggle, menu, abas, campos, selos, cards, foco | `src/NinjaOptimizer.App/Theme/Controls.xaml` |
| Ícone do .exe, logo e imagens do instalador | gerados por `build/brand/generate_assets.py` |
| Fontes Sora e Inter (SIL OFL) | `src/NinjaOptimizer.App/Assets/Fonts/` (embutidas no executável) |

Para mudar um ícone ou o símbolo, edite o script e rode `python build/brand/generate_icons.py` ou `python build/brand/generate_assets.py` (requer Pillow).

## Paleta

| Token | Cor | Uso |
|---|---|---|
| Base | `#07060B` | Fundo da janela |
| Superfície 1 / 2 / 3 | `#0E0C15` / `#14111D` / `#1C1828` | Menu e seções / cards / botões secundários |
| Borda / borda forte | `#2A2438` / `#3A3350` | Contornos |
| Texto primário / secundário / terciário / desativado | `#F5F3FA` / `#B4AEC4` / `#8F88A3` / `#6F6883` | |
| Roxo 700 (marca) | `#44088C` | Botão primário, fundo do ícone. **Nunca como texto sobre o escuro** (1,6:1) |
| Roxo 600 / 800 | `#5A12B3` / `#2E0661` | Hover / pressionado |
| Roxo 500 | `#7B2FE0` | Toggle ligado, barras, série principal dos gráficos |
| Roxo 300 | `#B98BFF` | Texto, valores e ícones em destaque (7,9:1) |
| Sucesso / Alerta / Erro / Informação | `#3DDC97` / `#FFB547` / `#FF5C7A` / `#6FA8FF` | Sempre com ícone e texto, nunca só cor |

## Tipografia

| Nível | Fonte |
|---|---|
| Título da página | Sora 600 · 28/36 |
| Título de card | Sora 600 · 24 ou 18 |
| Métrica | Sora 600 · 32 (20 nas grades), algarismos tabulares |
| Corpo | Inter 400 · 14/22 |
| Botão e rótulo | Inter 600 · 14 |
| Legenda | Inter 500 · 12, caixa alta, `#8F88A3` |

Nenhum texto abaixo de 12 px. Valores que mudam em tempo real usam algarismos tabulares, para a largura não oscilar.

## Componentes

* Controles com 44 px de altura e cantos de 12 px; cards com 16 px; seções com 20 px.
* Um único botão principal da marca por tela ("Otimização rápida", 56 px, com brilho roxo).
* Item ativo do menu: roxo a 18%, borda fina e ícone em Roxo 300.
* Foco do teclado visível: contorno de 2 px em Roxo 300.

## Tom de voz

Direta, simples, confiante e discreta. Botões começam pelo verbo ("Aplicar alterações", "Exportar relatório"), nunca "OK".
Sem exclamação e sem emoji. Toda ação que muda o PC diz como voltar atrás.

## Limitação conhecida

O WPF não tem espaçamento entre letras (*letter-spacing*). Os ajustes finos de −2% a −3% dos títulos do guia não são reproduzidos; o resto da tipografia é fiel.
