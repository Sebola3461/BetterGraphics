// Iluminação deferred própria (troca o Internal-DeferredShading da Unity via GraphicsSettings). Um passe por luz,
// lendo o G-buffer:
//  - BRDF: GGX com visibilidade Smith correlacionada pela altura e compensação da energia perdida nos
//    espalhamentos múltiplos (metal áspero não escurece); difuso Burley. Mesma convenção de unidades da Unity
//    (difuso sem 1/pi, especular x pi): o sol do Enviro continua com o mesmo brilho.
//  - Queda das luzes pontuais e spot pelo inverso do quadrado, normalizada pelo alcance e com janela suave no fim.
//    A da Unity (1/(1+25 r²) cortada no alcance) fazia o poste iluminar um disco chapado.
//  - Sol: o disco tem tamanho (0,27°), o reflexo num material muito liso não vira um ponto infinito.
// Direção, cone do spot (cookie) e sombras continuam os da Unity (UnityDeferredLibrary).
Shader "Hidden/BetterGraphics/DeferredShading"
{
    Properties
    {
        _LightTexture0 ("", any) = "" {}
        _LightTextureB0 ("", 2D) = "" {}
        _ShadowMapTexture ("", any) = "" {}
        _SrcBlend ("", Float) = 1
        _DstBlend ("", Float) = 1
    }
    SubShader
    {
        // 1: um passe por luz
        Pass
        {
            ZWrite Off
            Blend [_SrcBlend] [_DstBlend]

            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert_deferred
            #pragma fragment frag
            #pragma multi_compile_lightpass
            #pragma multi_compile ___ UNITY_HDR_ON
            #pragma exclude_renderers nomrt

            #include "UnityCG.cginc"
            #include "UnityDeferredLibrary.cginc"
            #include "UnityGBuffer.cginc"

            sampler2D _CameraGBufferTexture0;
            sampler2D _CameraGBufferTexture1;
            sampler2D _CameraGBufferTexture2;

            float _BG_LightK;          // brilho da luz pontual a 1 alcance de distância, em frações da intensidade
            float _BG_PhysicalFalloff; // 1 = queda física; 0 = a da Unity
            float _BG_SunRadius;       // raio angular do sol (rad)

            // Queda física: I·k·R² / (d² + tamanho da lâmpada²), com janela (1 - (d/R)^4)² que leva a zero no alcance.
            float Falloff(float d2, float invRange2, float unityAtten)
            {
                float r = d2 * invRange2;
                float win = saturate(1.0 - r * r);
                win *= win;
                float phys = _BG_LightK / (r + 0.0025) * win;
                return lerp(unityAtten, phys, _BG_PhysicalFalloff);
            }

            // Mesmo cálculo da UnityDeferredCalculateLightParams, com a queda trocada.
            void LightParams(unity_v2f_deferred i, out float3 wpos, out float2 uv, out half3 lightDir, out float atten, out float fadeDist)
            {
                i.ray = i.ray * (_ProjectionParams.z / i.ray.z);
                uv = i.uv.xy / i.uv.w;
                float depth = Linear01Depth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv));
                float4 vpos = float4(i.ray * depth, 1);
                wpos = mul(unity_CameraToWorld, vpos).xyz;
                fadeDist = UnityComputeShadowFadeDistance(wpos, vpos.z);

                #if defined(SPOT)
                    float3 tolight = _LightPos.xyz - wpos;
                    lightDir = normalize(tolight);
                    float4 uvCookie = mul(unity_WorldToLight, float4(wpos, 1));
                    atten = tex2Dbias(_LightTexture0, float4(uvCookie.xy / uvCookie.w, 0, -8)).w;
                    atten *= uvCookie.w < 0;
                    float d2 = dot(tolight, tolight);
                    atten *= Falloff(d2, _LightPos.w, tex2D(_LightTextureB0, (d2 * _LightPos.w).rr).r);
                    atten *= UnityDeferredComputeShadow(wpos, fadeDist, uv);
                #elif defined(DIRECTIONAL) || defined(DIRECTIONAL_COOKIE)
                    lightDir = -_LightDir.xyz;
                    atten = UnityDeferredComputeShadow(wpos, fadeDist, uv);
                    #if defined(DIRECTIONAL_COOKIE)
                        atten *= tex2Dbias(_LightTexture0, float4(mul(unity_WorldToLight, half4(wpos, 1)).xy, 0, -8)).w;
                    #endif
                #elif defined(POINT) || defined(POINT_COOKIE)
                    float3 tolight = wpos - _LightPos.xyz;
                    lightDir = -normalize(tolight);
                    float d2 = dot(tolight, tolight);
                    atten = Falloff(d2, _LightPos.w, tex2D(_LightTextureB0, (d2 * _LightPos.w).rr).r);
                    atten *= UnityDeferredComputeShadow(tolight, fadeDist, uv);
                    #if defined(POINT_COOKIE)
                        atten *= texCUBEbias(_LightTexture0, float4(mul(unity_WorldToLight, half4(wpos, 1)).xyz, -8)).w;
                    #endif
                #else
                    lightDir = 0;
                    atten = 0;
                #endif
            }

            // Aproximação analítica da integral do BRDF especular (Karis): A + B dá a energia refletida com F0 = 1.
            float2 EnvBrdf(float perceptual, float NoV)
            {
                const float4 c0 = float4(-1.0, -0.0275, -0.572, 0.022);
                const float4 c1 = float4(1.0, 0.0425, 1.04, -0.04);
                float4 r = perceptual * c0 + c1;
                float a004 = min(r.x * r.x, exp2(-9.28 * NoV)) * r.x + r.y;
                return float2(-1.04, 1.04) * a004 + r.zw;
            }

            float3 Shade(float3 diffColor, float3 specColor, float perceptual, float3 N, float3 V, float3 L, float3 lightColor, bool sun)
            {
                float3 H = normalize(L + V);
                float NoL = saturate(dot(N, L));
                float NoV = max(abs(dot(N, V)), 1e-4);
                float NoH = saturate(dot(N, H));
                float LoH = saturate(dot(L, H));
                if (NoL <= 0.0) return 0;

                float a = max(perceptual * perceptual, 0.002);
                // Luz com tamanho (o sol): alarga o lóbulo do material liso, como uma esfera de luz.
                if (sun) a = saturate(a + _BG_SunRadius * 0.5);
                float a2 = a * a;
                float d = NoH * NoH * (a2 - 1.0) + 1.0;
                float D = a2 / (UNITY_PI * d * d + 1e-7);
                float vis = 0.5 / (NoL * sqrt(NoV * NoV * (1.0 - a2) + a2) + NoV * sqrt(NoL * NoL * (1.0 - a2) + a2) + 1e-5);
                float3 F = specColor + (1.0 - specColor) * pow(1.0 - LoH, 5.0);
                float2 ab = EnvBrdf(perceptual, NoV);
                float3 energy = 1.0 + specColor * (1.0 / max(ab.x + ab.y, 0.05) - 1.0);
                float3 spec = D * vis * F * energy * UNITY_PI;
                // A Unity não deixa sumir o especular de um material com F0 zero; aqui também não há.

                // Difuso Burley (sem 1/pi, como a Unity).
                float fd90 = 0.5 + 2.0 * LoH * LoH * perceptual;
                float fl = 1.0 + (fd90 - 1.0) * pow(1.0 - NoL, 5.0);
                float fv = 1.0 + (fd90 - 1.0) * pow(1.0 - NoV, 5.0);
                float3 diff = diffColor * fl * fv;

                return (diff + spec) * lightColor * NoL;
            }

            half4 CalculateLight(unity_v2f_deferred i)
            {
                float3 wpos; float2 uv; half3 L; float atten, fadeDist;
                LightParams(i, wpos, uv, L, atten, fadeDist);
                half4 g0 = tex2D(_CameraGBufferTexture0, uv);
                half4 g1 = tex2D(_CameraGBufferTexture1, uv);
                half4 g2 = tex2D(_CameraGBufferTexture2, uv);
                UnityStandardData data = UnityStandardDataFromGbuffer(g0, g1, g2);
                float3 V = -normalize(wpos - _WorldSpaceCameraPos);
                #if defined(DIRECTIONAL) || defined(DIRECTIONAL_COOKIE)
                    bool sun = true;
                #else
                    bool sun = false;
                #endif
                float3 c = Shade(data.diffuseColor, data.specularColor, 1.0 - data.smoothness, data.normalWorld, V, L, _LightColor.rgb * atten, sun);
                return half4(c, 1.0);
            }

            #ifdef UNITY_HDR_ON
            half4
            #else
            fixed4
            #endif
            frag(unity_v2f_deferred i) : SV_Target
            {
                half4 c = CalculateLight(i);
                #ifdef UNITY_HDR_ON
                    return c;
                #else
                    return exp2(-c);
                #endif
            }
            ENDCG
        }

        // 2: decodifica o buffer logarítmico (só com HDR desligado)
        Pass
        {
            ZTest Always Cull Off ZWrite Off
            Stencil
            {
                ref [_StencilNonBackground]
                readmask [_StencilNonBackground]
                compback equal
                compfront equal
            }

            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma exclude_renderers nomrt
            #include "UnityCG.cginc"

            sampler2D _LightBuffer;
            struct v2f { float4 vertex : SV_POSITION; float2 texcoord : TEXCOORD0; };

            v2f vert(float4 vertex : POSITION, float2 texcoord : TEXCOORD0)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(vertex);
                o.texcoord = texcoord.xy;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target { return -log2(tex2D(_LightBuffer, i.texcoord)); }
            ENDCG
        }
    }
    Fallback Off
}
