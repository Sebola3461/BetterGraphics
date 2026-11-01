# BetterGraphics

Iluminação mais realista para o Proton Bus Simulator. O foco é luz e reflexos, não texturas.

- Exposição automática, curva de tom e cor calibradas com fotos reais. A exposição muda dentro e fora do ônibus.
- Profundidade de campo (desfoque de lente) com foco automático no que a câmera está olhando. De dentro do ônibus, o painel fica levemente fora de foco, como numa foto real.
- Iluminação global (Radiant GI) ajustada. Céu, neblina e luz ambiente caem em túneis e sob coberturas.
- Pintura do ônibus com verniz, e reflexos com probes locais.
- Asfalto molhado com poças que refletem conforme a chuva.
- Chuva mais densa, com respingo dos pneus e splash nas poças, vento em rajadas e tom mais frio de dia. Não chove dentro do ônibus.
- O pós-processamento nativo do jogo é desligado, para não somar efeitos.

## Teclas

| Tecla | Ação |
|---|---|
| Ctrl+Alt+F9 | Liga e desliga o mod, para comparar com o original |
| Ctrl+Alt+Shift+F9 | Grava `UserData/BetterGraphics_diagnostico.txt` |

## Opções

As opções ficam na categoria `[Realismo]` do `MelonPreferences.cfg`. Também dá para ajustar pelo menu **Mods** da
PBSLoaderCommonInterface.

O MelonPreferences guarda o valor que foi salvo primeiro. Quando uma versão nova muda um padrão, a mudança não chega a
quem já tem o arquivo. Se a imagem ficar estranha depois de atualizar, apague a seção `[Realismo]` do `.cfg` para
voltar aos padrões.

## Problemas conhecidos

- O tempo chuvoso custa cerca de 20 ms por quadro, a maior parte do próprio sistema de clima do jogo (Enviro).
- Se a imagem ficar chapada ou lavada, grave o diagnóstico e veja "Luzes com sombra / direcionais" no diagnóstico. Outra luz
  direcional na cena (de outro mod, por exemplo) estraga a exposição.

## Desenvolvimento

`--graficos-lab`, junto com `--pbsloadercommoninterface-partida`, entra numa partida e executa comandos de
`UserData/GraficosLab/cmd.txt`: hora, clima, câmera, `set` de opções, fotos. É o que foi usado para calibrar o mod.
