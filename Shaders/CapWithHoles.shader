// The hideout's black cap (Hideout_Stub) as the game draws it, black on both sides with no shadows, but left out over
// the built extension rooms, so the overview shows them. _Holes: up to 16 rectangles in world x/z (xmin, zmin, xmax, zmax).
Shader "HideoutExtensions/CapWithHoles"
{
    Properties
    {
        _Color ("Colour", Color) = (0, 0, 0, 1)
        _HoleCount ("Holes", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }
        Cull Off
        ZWrite On
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _Color;
            float _HoleCount;
            float4 _Holes[16];

            struct v2f { float4 pos : SV_POSITION; float3 world : TEXCOORD0; };

            v2f vert (appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.world = mul(unity_ObjectToWorld, v.vertex).xyz;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                for (int k = 0; k < 16; k++)
                {
                    if (k >= (int)_HoleCount) break;
                    float4 h = _Holes[k];
                    if (i.world.x > h.x && i.world.x < h.z && i.world.z > h.y && i.world.z < h.w) discard;
                }
                return _Color;
            }
            ENDCG
        }
    }
}
