Realismo de iluminação e chuva para o Proton Bus Simulator.

Novo nesta versão:

- **Profundidade de campo** (desfoque de lente) com foco automático no que a câmera está olhando: raio pelo meio da tela, alvo suavizado a cada quadro (troca de foco como o olho, ~0,3 s). De fora, o ônibus fotografado fica em foco e o fundo desfoca; de dentro do ônibus, o foco não vem mais perto que 6 m, então o painel e o volante ficam levemente fora, como numa foto real tirada de dentro. Abertura (f/) e distância focal (mm) nas opções — e no menu Mods.
- **Vento em rajadas**: a direção da chuva gira devagar (±40°) e a força das gusts varia em ciclos suaves de soma de senos, sem custo por gota.
- **Tom frio na chuva de dia**: junto com a menor exposição que já existia, a temperatura da cor desce conforme o chão encharca (o céu fechado azula a luz difusa). Some à noite.

O que já existia (1.0.0): iluminação global (Radiant GI com rebatimento e mapa de sombra), oclusão de ambiente, luz ambiente do céu, sombras físicas, exposição automática com adaptação dentro/fora do ônibus, materiais PBR corretos no mapa (sem o metal falso), asfalto molhado com poças que refletem céu e faróis, reflexo em tempo real na lataria com verniz e nos vidros, chuva densa com spray dos pneus, splash nas poças e respingos no chão.

Requer o PBSLoader (PBSLoaderCommonUtils e PBSLoaderCommonInterface).