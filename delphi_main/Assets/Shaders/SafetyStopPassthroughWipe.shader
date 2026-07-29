// Ramps the FRAMEBUFFER ALPHA down so the Varjo compositor blends the
// rendered scene against the video see-through image.
//
// WHY A SHADER AND NOT A BLACK FADE QUAD:
//
//   Passthrough on the XR-3 is a COMPOSITOR operation. With
//   VarjoRendering.SetOpaque(false) the compositor blends what the app
//   submitted against the VST cameras using the submitted surface's alpha
//   channel: alpha 1 shows only the app, alpha 0 shows only the real room.
//   Nothing Unity draws in RGB can produce that — a translucent black quad
//   fades the virtual world toward BLACK, not toward the room.
//
//   So this writes alpha and nothing else. ColorMask A leaves every colour
//   channel untouched, and "Blend One Zero" REPLACES the destination alpha
//   rather than blending into it, which matters because opaque scene geometry
//   leaves arbitrary values there — there is nothing meaningful to blend with.
//
// ZTest Always and the Overlay queue put it after the scene and in front of
// all of it. It must still draw BEFORE the safety text (queue Overlay+100),
// which is the whole reason the two queues are stated explicitly.
Shader "Delphi/SafetyStopPassthroughWipe"
{
    Properties
    {
        // 1 = fully virtual, 0 = fully passthrough. Driven from
        // SafetyStopOverlay, which ramps it over fadeSeconds.
        _SceneAlpha ("Scene Alpha", Range(0,1)) = 1
    }

    SubShader
    {
        Tags { "Queue" = "Overlay" "RenderType" = "Overlay" "IgnoreProjector" = "True" }

        Pass
        {
            Cull Off
            ZWrite Off
            ZTest Always
            ColorMask A
            Blend One Zero

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            float _SceneAlpha;

            struct appdata
            {
                float4 vertex : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // RGB is masked out; only this alpha reaches the framebuffer.
                return fixed4(0, 0, 0, _SceneAlpha);
            }
            ENDCG
        }
    }
}
