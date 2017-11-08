// Upgrade NOTE: replaced 'mul(UNITY_MATRIX_MVP,*)' with 'UnityObjectToClipPos(*)'

Shader "Hidden/Volund/Mask Plane" {
SubShader {
	Tags { "Queue"="AlphaTest+51" }

	Pass {
		ColorMask A
		Cull Off
		ZWrite Off
		ZTest Always

CGPROGRAM
#pragma vertex vert
#pragma fragment frag

#pragma only_renderers d3d11

float4 vert(float4 vertex : POSITION) : SV_Position {
	return UnityObjectToClipPos(vertex);
}

float4 frag() : COLOR {
	return -1.f;
}
ENDCG

	}
}}