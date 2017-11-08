// Upgrade NOTE: replaced 'mul(UNITY_MATRIX_MVP,*)' with 'UnityObjectToClipPos(*)'

Shader "Hidden/Volund/Deferred Apply" {
Properties{
	_MainTex("Base (RGB) Trans (A)", 2D) = "white" {}
}

SubShader{
	//Tags{ "Queue" = "Transparent" "IgnoreProjector" = "True" "RenderType" = "Transparent" }
	//LOD 100

	ZWrite Off
	//Blend SrcAlpha OneMinusSrcAlpha

	Pass{
		CGPROGRAM

#pragma vertex vert
#pragma fragment frag

#include "UnityCG.cginc"

struct appdata {
	float4 vertex	: POSITION;
	float2 texcoord	: TEXCOORD0;
};

struct v2f {
	float4 vertex	: SV_POSITION;
	float2 texcoord	: TEXCOORD0;
};

v2f vert(appdata v) {
	v2f o;
	o.vertex = UnityObjectToClipPos(v.vertex);
	o.texcoord = v.texcoord;
	//o.screenPos = ComputeScreenPos(o.pos);
;
	return o;
}

sampler2D _MainTex;

float4 frag(v2f i) : SV_Target {
	return tex2D(_MainTex, i.texcoord);
}
			ENDCG
	}
}
Fallback Off
}
