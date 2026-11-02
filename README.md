# BetterGraphics

Iluminação mais realista para o Proton Bus Simulator. O foco é luz e reflexos, não texturas.

- Exposição automática, curva de tom e cor calibradas com fotos reais. A exposição muda dentro e fora do ônibus.
- Profundidade de campo (desfoque de lente) com foco automático no que a câmera está olhando. De dentro do ônibus, o painel fica levemente fora de foco, como numa foto real.
- Iluminação global (Radiant GI) ajustada. Céu, neblina e luz ambiente caem em túneis e sob coberturas.
- Pintura do ônibus com verniz, e reflexos com probes locais. Dentro do ônibus, piso e painéis refletem o próprio interior.
- Luz do interior vindo pelas janelas, calculada por pixel: claro perto do vidro, sombra sob os bancos e no piso.
- Asfalto molhado com poças que refletem conforme a chuva.
- Na chuva tudo que fica exposto molha (calçadas, paredes, veículos, grama), com poças e ondulações das gotas; embaixo de
  telhado, túnel e abrigo fica seco.
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

Os shaders próprios ficam no projeto Unity em `Shaders/` (Unity 2020.3.39f1, a mesma versão do jogo). Para gerar
`Recursos/bettergraphics.shaders`, que vai embutido na DLL, abra o editor com
`Unity.exe -quit -projectPath Shaders -executeMethod Construir.Pacote`.


`--graficos-lab`, junto com `--pbsloadercommoninterface-partida`, entra numa partida e executa comandos de
`UserData/GraficosLab/cmd.txt`: hora, clima, câmera, `set` de opções, fotos. É o que foi usado para calibrar o mod.
