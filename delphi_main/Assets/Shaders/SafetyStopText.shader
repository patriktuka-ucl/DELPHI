// uGUI text that survives the passthrough wipe and cannot be occluded.
//
// TWO THINGS THE STOCK UI/Default CANNOT DO HERE:
//
//   1. ORDER. The wipe quad (queue Overlay, 4000) rewrites the framebuffer
//      alpha for the whole view. Anything drawn BEFORE it — which is every
//      normal world-space canvas, in the Transparent queue at 3000 — has its
//      alpha overwritten and dissolves into the room along with the scene.
//      Sitting at Overlay+100 puts the glyphs after the wipe, so they write
//      alpha 1 and stay solid over the passthrough.
//
//   2. OCCLUSION. The banner is a 360° ring around the driver at a couple of
//      metres, which puts the windscreen pillars, the seat and the car body
//      between them and most of it. ZTest Always ignores depth entirely: the
//      one message a participant must be able to read during an emergency
//      stop cannot be hidden behind the vehicle they are trying to get out of.
//
// Otherwise this is Unity's font path: the atlas carries glyph coverage in
// its alpha channel, modulating the vertex colour the canvas supplies.
Shader "Delphi/SafetyStopText"
{
    Properties
    {
        [PerRendererData] _MainTex ("Font Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Overlay+100"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
            "PreviewType" = "Plane"
        }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest Always
        Fog { Mode Off }
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;

            struct appdata_t
            {
                float4 vertex : POSITION;
                fixed4 color : COLOR;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                fixed4 color : COLOR;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert (appdata_t v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.color = v.color * _Color;
                o.texcoord = TRANSFORM_TEX(v.texcoord, _MainTex);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 c = i.color;
                c.a *= tex2D(_MainTex, i.texcoord).a;   // glyph coverage
                return c;
            }
            ENDCG
        }
    }
}
