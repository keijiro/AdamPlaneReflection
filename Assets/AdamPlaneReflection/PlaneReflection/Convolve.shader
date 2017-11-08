Shader "Hidden/Volund/Convolve" {
Properties {
	_MainTex("Diffuse", 2D) = "white" {}
	_DepthScale("DepthScale", Float) = 1.0
	_DepthExponent("DepthExponent", Float) = 1.0
}

CGINCLUDE

#pragma only_renderers d3d11
#pragma target 3.0

#include "UnityCG.cginc"

uniform sampler2D _MainTex;
uniform float4 _MainTex_TexelSize;

uniform sampler2D _CameraDepthTexture;
uniform sampler2D _CameraDepthTextureCopy;

uniform float4x4	_FrustumCornersWS;
uniform float4		_CameraWS;

uniform float		_DepthScale;
uniform float		_DepthExponent;
uniform float		_SampleMip;
uniform float		_CosPower;
uniform float		_RayPinchInfluence;

struct v2f {
	float4 pos				: SV_POSITION;
	float2 uv				: TEXCOORD0;
	float4 interpolatedRay	: TEXCOORD1;
	float3 interpolatedRayN	: TEXCOORD2;
};

v2f vert(appdata_img v)  {
	v2f o;
	half index = v.vertex.z;
	v.vertex.z = 0.1;
	o.pos = UnityObjectToClipPos(v.vertex);
	o.uv = v.texcoord;
	o.interpolatedRay = _FrustumCornersWS[(int)index];
	o.interpolatedRay.w = index;
	o.interpolatedRayN = normalize(o.interpolatedRay.xyz);
	return o;
}

uniform float4 _PlaneReflectionClipPlane;
uniform float4 _PlaneReflectionZParams;


float4 frag(v2f i, const float2 dir) {
	float4 baseUV;
	baseUV.xy = i.uv.xy;
	baseUV.z = 0;
	baseUV.w = _SampleMip;
	
#if USE_DEPTH
//	float rawDepth = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv);
//	float depth = 1.f / (rawDepth * _PlaneReflectionZParams.x + _PlaneReflectionZParams.y);
	float depth = tex2D(_CameraDepthTextureCopy, i.uv);

	float3 wsDir = depth * i.interpolatedRay.xyz;
	float4 wsPos = float4(_CameraWS.xyz + wsDir, 1.f);
	float pointToPlaneDist = dot(wsPos, _PlaneReflectionClipPlane) / dot(_PlaneReflectionClipPlane.xyz, normalize(i.interpolatedRayN));
	float sampleScale1 = saturate(pow(saturate(pointToPlaneDist * _DepthScale), _DepthExponent));
	sampleScale1 = max(_RayPinchInfluence, sampleScale1);
#else
	float sampleScale1 = 1.f;
#endif
	float2 sampleScale = dir * _MainTex_TexelSize.xy * sampleScale1;

	float weight = 0.f;
	float4 color = 0.f;

	float4 uv = baseUV;

	for(int i = -32; i <= 32; i += 2) {
		float2 off = i * sampleScale;
		uv.xy = baseUV.xy + off;

		// Kill any samples falling outside of the screen.
		// Otherwise, as a bright source pixel touches the edge of the screen, it suddenly
		// gets exploded by clamping to have the width/height equal to kernel's radius
		// and introduces that much more energy to the result.
		if (any(uv.xy < 0.0) || any(uv.xy > 1.0))
			continue;
		
		float4 s = tex2Dlod(_MainTex, uv);

		float c = clamp(i / 20.f, -1.57f, 1.57f);
		float w = pow(max(0.f, cos(c)), _CosPower);
	
		color.rgb += s.rgb * w;
		weight += w;
	}

		return color.rgbb / weight;
}

float4 fragH(v2f i) : COLOR { return frag(i, float2(1.f, 0.f)); }
float4 fragV(v2f i) : COLOR { return frag(i, float2(0.f, 1.f)); }


float fragResolve(v2f i) : SV_Target{
	float rawDepth = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv);
	float depth = 1.f / (rawDepth * _PlaneReflectionZParams.x + _PlaneReflectionZParams.y);
	return depth;
}

ENDCG

SubShader {
	Cull Off ZTest Always ZWrite Off

	Pass {
		CGPROGRAM
		#pragma vertex vert
		#pragma fragment fragH
		#pragma multi_compile _ USE_DEPTH
		#pragma multi_compile _ CP0 CP1 CP2 CP3
		ENDCG
	}
	
	Pass {
		CGPROGRAM
		#pragma vertex vert
		#pragma fragment fragV
		#pragma multi_compile _ USE_DEPTH
		#pragma multi_compile _ CP0 CP1 CP2 CP3
		ENDCG
	}

	Pass {
		CGPROGRAM
		#pragma vertex vert
		#pragma fragment fragResolve
		ENDCG
	}
}
}
