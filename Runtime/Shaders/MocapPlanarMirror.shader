// URP-compatible planar mirror surface shader.
//
// Displays the per-eye reflection render textures produced by
// MocapFbtPlanarMirror. Fragments sample the reflection at the same
// screen-space UV they occupy in the source camera, which is valid because
// the reflection camera uses the exact per-eye projection of the source
// camera (plus an oblique near plane at the mirror surface).
//
// Front-side only (Cull Back) with depth writes enabled, so the surface
// occludes the scene instead of blending with it; the prefab adds an opaque
// backing cube for the rear.
Shader "MocapTools/URP/PlanarMirror"
{
    Properties
    {
        [HideInInspector] _ReflectionTex0("Left Eye Reflection", 2D) = "white" {}
        [HideInInspector] _ReflectionTex1("Right Eye Reflection", 2D) = "white" {}
        _TintColor("Tint", Color) = (1, 1, 1, 1)
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalRenderPipeline" }
        LOD 100

        Pass
        {
            Name "PlanarMirror"
            ZWrite On
            Cull Back

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            #include "UnityInstancing.cginc"

            sampler2D _ReflectionTex0;
            sampler2D _ReflectionTex1;
            float4 _TintColor;

            struct appdata
            {
                float4 vertex : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float4 screenPos : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.screenPos = ComputeScreenPos(o.pos);
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float2 uv = i.screenPos.xy / i.screenPos.w;

                half4 refl;
                #if defined(UNITY_SINGLE_PASS_STEREO) || defined(UNITY_SINGLE_PASS_MULTIVIEW) || defined(UNITY_SINGLE_PASS_STEREO_MULTIVIEW)
                    refl = unity_StereoEyeIndex == 0 ? tex2D(_ReflectionTex0, uv) : tex2D(_ReflectionTex1, uv);
                #else
                    refl = tex2D(_ReflectionTex0, uv);
                #endif

                return refl * _TintColor;
            }
            ENDCG
        }
    }
    Fallback Off
}
