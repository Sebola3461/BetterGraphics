// Mapa de abrigo da chuva: desenhado de cima, grava a altura (y do mundo) da superfície mais alta em
// cada ponto. Um ponto mais baixo que isso está coberto (telhado, túnel, abrigo). Quem entra no mapa
// (opacos e vidros, sem partículas nem efeitos) é escolhido pelo mod.
Shader "Hidden/BetterGraphics/Altura"
{
    SubShader
    {
        Cull Off ZWrite On ZTest LEqual
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct v2f { float4 pos : SV_POSITION; float y : TEXCOORD0; };

            v2f vert(float4 vertex : POSITION)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(vertex);
                o.y = mul(unity_ObjectToWorld, vertex).y;
                return o;
            }

            float4 frag(v2f i) : SV_Target { return float4(i.y, 0, 0, 1); }
            ENDCG
        }
    }
}
