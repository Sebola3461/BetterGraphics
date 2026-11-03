# BetterGraphics

Iluminação mais realista para o Proton Bus Simulator. O foco é luz e reflexos, não texturas. Calibrado contra fotos reais
de ruas de São Paulo e Uberlândia, de dia, no fim de tarde e à noite.

- Céu físico próprio (Rayleigh, aerossol, ozônio e espalhamento múltiplo): azul ao meio-dia, dourado no fim de tarde,
  hora azul no anoitecer e céu cinza da cidade à noite. Nuvens, estrelas e lua continuam do jogo.
- Sol com a cor e a força que sobram depois da atmosfera, e névoa por perspectiva aérea: o que está longe ganha a cor do
  céu naquela direção. A densidade da névoa segue o clima do jogo. Com o céu fechado não há sol direto.
- Iluminação deferred própria: queda física das luzes, reflexos com energia certa e postes LED que iluminam a rua como nas
  fotos noturnas.
- Exposição automática, curva de tom e branco automático de câmera, com exposição própria para dia, noite, céu nublado e
  cabine.
- Pintura dos veículos com verniz de verdade (camada lisa por cima da tinta). Pneus foscos.
- Vidros com Fresnel: espelham de lado, deixam ver de frente; janelas laterais fumê vistas de fora, quase sem escurecer
  vistas de dentro.
- Luz do interior vindo pelas janelas, calculada por pixel, e reflexos do próprio salão dentro do ônibus.
- Chuva: gotas simuladas no para-brisa com limpador, chuva caindo na GPU com respingos, tudo que fica exposto molha e
  embaixo de telhado fica seco. Não chove dentro do ônibus.
- Profundidade de campo com foco automático e Radiant GI ajustada.
- O pós-processamento nativo do jogo é desligado, para não somar efeitos.

## Teclas

| Tecla | Ação |
|---|---|
| Ctrl+Alt+F9 | Liga e desliga o mod, para comparar com o original |
| Ctrl+Alt+Shift+F9 | Grava `UserData/BetterGraphics_diagnostico.txt` |

## Opções

As opções ficam no `MelonPreferences.cfg`, nas categorias `[Realismo]`, `[Lighting]`, `[Sky]` e `[Rain_*]`. Também dá
para ajustar pelo menu **Mods** da PBSLoaderCommonInterface.

O MelonPreferences guarda o valor que foi salvo primeiro. Quando uma versão nova muda um padrão, a mudança não chega a
quem já tem o arquivo. Depois de atualizar, apague as categorias do BetterGraphics do `.cfg` para pegar os padrões novos.

## Desempenho

O jogo costuma ser limitado pelo processador (a quantidade de objetos desenhados), não pela placa de vídeo. Na máquina
de teste (Xeon de 2,2 GHz e RX 5500 XT) o jogo sem o mod roda a ~42 fps, e com o mod a ~36 fps de dia e ~31 fps à
noite. Chuva forte é o caso pesado (~23 fps) e ali quem limita é a placa de vídeo; `particle_density` em
`[Rain_FallingDrops]` alivia.

## Problemas conhecidos

- Se a imagem ficar chapada ou lavada, grave o diagnóstico e veja "Luzes com sombra / direcionais". Outra luz direcional
  na cena (de outro mod, por exemplo) estraga a exposição.

## Desenvolvimento

Os shaders próprios ficam no projeto Unity em `Shaders/` (Unity 2020.3.39f1, a mesma versão do jogo). Para gerar
`Recursos/bettergraphics.shaders`, que vai embutido na DLL, abra o editor com
`Unity.exe -quit -projectPath Shaders -executeMethod Construir.Pacote`.

`--graficos-lab`, junto com `--pbsloadercommoninterface-partida`, entra numa partida e executa comandos de
`UserData/GraficosLab/cmd.txt`: hora, clima, câmera, `set` de opções, fotos, medidas de desempenho. É o que foi usado
para calibrar o mod.
