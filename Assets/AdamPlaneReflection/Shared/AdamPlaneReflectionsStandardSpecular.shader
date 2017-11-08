Shader "Adam/PlaneReflections Standard (Specular setup)"
{
	Properties
	{
		_Color("Color", Color) = (1,1,1,1)
		_MainTex("Albedo", 2D) = "white" {}
		
		_Cutoff("Alpha Cutoff", Range(0.0, 1.0)) = 0.5

		_Glossiness("Smoothness", Range(0.0, 1.0)) = 0.5
		_GlossMapScale("Smoothness Factor", Range(0.0, 1.0)) = 1.0
		[Enum(Specular Alpha,0,Albedo Alpha,1)] _SmoothnessTextureChannel ("Smoothness texture channel", Float) = 0

		_SpecColor("Specular", Color) = (0.2,0.2,0.2)
		_SpecGlossMap("Specular", 2D) = "white" {}
		[ToggleOff] _SpecularHighlights("Specular Highlights", Float) = 1.0
		[ToggleOff] _GlossyReflections("Glossy Reflections", Float) = 1.0

		_BumpScale("Scale", Float) = 1.0
		_BumpMap("Normal Map", 2D) = "bump" {}

		_Parallax ("Height Scale", Range (0.005, 0.08)) = 0.02
		_ParallaxMap ("Height Map", 2D) = "black" {}

		_OcclusionStrength("Strength", Range(0.0, 1.0)) = 1.0
		_OcclusionMap("Occlusion", 2D) = "white" {}

		_EmissionColor("Color", Color) = (0,0,0)
		_EmissionMap("Emission", 2D) = "white" {}
		
		_DetailMask("Detail Mask", 2D) = "white" {}

		_DetailAlbedoMap("Detail Albedo x2", 2D) = "grey" {}
		_DetailNormalMapScale("Scale", Float) = 1.0
		_DetailNormalMap("Normal Map", 2D) = "bump" {}

		[Enum(UV0,0,UV1,1)] _UVSec ("UV Set for secondary textures", Float) = 0


		// Blending state
		[HideInInspector] _Mode ("__mode", Float) = 0.0
		[HideInInspector] _SrcBlend ("__src", Float) = 1.0
		[HideInInspector] _DstBlend ("__dst", Float) = 0.0
		[HideInInspector] _ZWrite ("__zw", Float) = 1.0

//adam-begin:
		_PlaneReflectionIntensityScale("Plane Reflection Intensity Scale", Range(0.0, 4.0)) = 1.0
		_PlaneReflectionBumpScale("Plane Reflection Bump Scale", Range(0.0, 1.0)) = 0.4
		_PlaneReflectionBumpClamp("Plane Reflection Bump Clamp", Range(0.0, 0.15)) = 0.05
//adam-end:
	}

	CGINCLUDE
		#define UNITY_SETUP_BRDF_INPUT SpecularSetup
	ENDCG

	SubShader
	{
		Tags { "RenderType"="Opaque" "PerformanceChecks"="False" "Special"="PlaneReflection" }
		LOD 300
	

		// ------------------------------------------------------------------
		//  Base forward pass (directional light, emission, lightmaps, ...)
		Pass
		{
			Name "FORWARD" 
			Tags { "LightMode" = "ForwardBase" }

			Blend [_SrcBlend] [_DstBlend]
			ZWrite [_ZWrite]

			CGPROGRAM
//adam-begin: 5.0 / d3d11 only (includes d3d12)
			#pragma target 5.0
			#pragma only_renderers d3d11
//adam-end:

			// -------------------------------------

			#pragma shader_feature _NORMALMAP
			#pragma shader_feature _ _ALPHATEST_ON _ALPHABLEND_ON _ALPHAPREMULTIPLY_ON
			#pragma shader_feature _EMISSION
			#pragma shader_feature _SPECGLOSSMAP
			#pragma shader_feature ___ _DETAIL_MULX2
			#pragma shader_feature _ _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A
			#pragma shader_feature _ _SPECULARHIGHLIGHTS_OFF
			#pragma shader_feature _ _GLOSSYREFLECTIONS_OFF
			#pragma shader_feature _PARALLAXMAP
			
			#pragma multi_compile_fwdbase
			#pragma multi_compile_fog

//adam-begin:
			#pragma multi_compile _ PLANE_REFLECTION
			#pragma multi_compile _ PLANE_REFLECTION_USER_CLIPPLANE
			uniform float4 _PlaneReflectionClipPlane;
//adam-end:

//adam-begin: Skip the CoreForward proxy for vertex, use custom fragment
			#pragma vertex vertForwardBase
			#pragma fragment fragForwardBasePR
			#include "UnityStandardCore.cginc"
//adam-end:

//adam-begin: Custom fragment
			uniform sampler2D	_PlaneReflection;
			uniform float		_PlaneReflectionIntensityScale;
			uniform float		_PlaneReflectionBumpScale;
			uniform float		_PlaneReflectionBumpClamp;
			uniform float		_PlaneReflectionLodSteps;

			half4 fragForwardBasePR(VertexOutputForwardBase i) : SV_Target
			{
//adam-begin:
				FRAGMENT_SETUP(s)
//adam-end:
#if UNITY_OPTIMIZE_TEXCUBELOD
				s.reflUVW = i.reflUVW;
#endif

				UnityLight mainLight = MainLight(s.normalWorld);
				half atten = SHADOW_ATTENUATION(i);

//adam-begin:
				half occlusion = Occlusion(i.tex.xy, i.vertexOcclusion);
//adam-end:
				UnityGI gi = FragmentGI(s, occlusion, i.ambientOrLightmapUV, atten, mainLight);

//adam-begin:
#if PLANE_REFLECTION
				float mip = pow(1.f - s.oneMinusRoughness, 3.0 / 4.0) * _PlaneReflectionLodSteps;
				float2 vpos = i.pos / _ScreenParams.xy;

			#ifdef _NORMALMAP
				half3 tanNormal = NormalInTangentSpace(i.tex);
				vpos.xy += clamp(tanNormal.xy * _PlaneReflectionBumpScale /* * float2(-1.f, -1.f)*/, -_PlaneReflectionBumpClamp, _PlaneReflectionBumpClamp);
			#endif
				float4 lookup = float4(vpos.x, vpos.y, 0.f, mip);
				float4 hdrRefl = tex2Dlod(_PlaneReflection, lookup);
				gi.indirect.specular = hdrRefl.rgb * _PlaneReflectionIntensityScale * occlusion;
#endif
//adam-end:

				half4 c = UNITY_BRDF_PBS(s.diffColor, s.specColor, s.oneMinusReflectivity, s.oneMinusRoughness, s.normalWorld, -s.eyeVec, gi.light, gi.indirect);
				c.rgb += UNITY_BRDF_GI(s.diffColor, s.specColor, s.oneMinusReflectivity, s.oneMinusRoughness, s.normalWorld, -s.eyeVec, occlusion, gi);
				c.rgb += Emission(i.tex.xy);

				UNITY_APPLY_FOG(i.fogCoord, c.rgb);
				return OutputForward(c, s.alpha);
			}

			ENDCG
		}
		// ------------------------------------------------------------------
		//  Additive forward pass (one light per pass)
		Pass
		{
			Name "FORWARD_DELTA"
			Tags { "LightMode" = "ForwardAdd" }
			Blend [_SrcBlend] One
			Fog { Color (0,0,0,0) } // in additive pass fog should be black
			ZWrite Off
			ZTest LEqual

			CGPROGRAM
//adam-begin: 5.0 / d3d11 only (includes d3d12)
			#pragma target 5.0
			#pragma only_renderers d3d11
//adam-end:

			// -------------------------------------

			#pragma shader_feature _NORMALMAP
			#pragma shader_feature _ _ALPHATEST_ON _ALPHABLEND_ON _ALPHAPREMULTIPLY_ON
			#pragma shader_feature _SPECGLOSSMAP
			#pragma shader_feature _ _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A
			#pragma shader_feature _ _SPECULARHIGHLIGHTS_OFF
			#pragma shader_feature ___ _DETAIL_MULX2
			#pragma shader_feature _PARALLAXMAP
			
//adam-begin:
			#pragma multi_compile _ PLANE_REFLECTION_USER_CLIPPLANE
			uniform float4 _PlaneReflectionClipPlane;
//adam-end:

			#pragma multi_compile_fwdadd_fullshadows
			#pragma multi_compile_fog

			#pragma vertex vertAdd
			#pragma fragment fragAdd
			#include "UnityStandardCoreForward.cginc"

			ENDCG
		}
		// ------------------------------------------------------------------
		//  Shadow rendering pass
		Pass {
			Name "ShadowCaster"
			Tags { "LightMode" = "ShadowCaster" }

			ZWrite On ZTest LEqual

			CGPROGRAM
//adam-begin: 5.0 / d3d11 only (includes d3d12)
			#pragma target 5.0
			#pragma only_renderers d3d11
//adam-end:
			
			// -------------------------------------

//adam-begin:
			#pragma multi_compile _ PLANE_REFLECTION_USER_CLIPPLANE
			uniform float4 _PlaneReflectionClipPlane;
//adam-end:

			#pragma shader_feature _ _ALPHATEST_ON _ALPHABLEND_ON _ALPHAPREMULTIPLY_ON
			#pragma multi_compile_shadowcaster

			#pragma vertex vertShadowCaster
			#pragma fragment fragShadowCaster

			#include "UnityStandardShadow.cginc"

			ENDCG
		}

		// ------------------------------------------------------------------
		// Extracts information for lightmapping, GI (emission, albedo, ...)
		// This pass it not used during regular rendering.
		Pass
		{
			Name "META" 
			Tags { "LightMode"="Meta" }

			Cull Off

			CGPROGRAM
//adam-begin: 5.0 / d3d11 only (includes d3d12)
			#pragma target 5.0
			#pragma only_renderers d3d11
//adam-end:

			#pragma vertex vert_meta
			#pragma fragment frag_meta

			#pragma shader_feature _EMISSION
			#pragma shader_feature _SPECGLOSSMAP
			#pragma shader_feature ___ _DETAIL_MULX2

			#include "UnityStandardMeta.cginc"
			ENDCG
		}
	}


//adam-begin-end: Intentionally left out fallback sub shader until later stage.

//adam-begin:
	FallBack "Adam/Standard (Specular setup)"
	CustomEditor "AdamStandardShaderGUI"
//adam-end:
}
