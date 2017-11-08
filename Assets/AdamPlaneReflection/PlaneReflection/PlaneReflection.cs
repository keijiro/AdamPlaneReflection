//#define ALLOW_ATMOSPHERICS_DEPENDENCY
//#define ALLOW_UNIQUESHADOW_DEPENDENCY
//#define PLANE_REFLECTION_CHEAPER
//#define USE_GLOBAL_KEYWORDS

// Can't use temp main buffer because Unity won't allow us to explicitly
// render to each of the mip levels in a temporary render texture.


using UnityEngine;
using System.Collections.Generic;

[ExecuteInEditMode]
public class PlaneReflection : MonoBehaviour {
	public enum Dimension {
		x128	= 128,
		x256	= 256,
		x512	= 512,
		x1024	= 1024,
		x2048	= 2048,
		x4096	= 4096,
	}

	[HideInInspector] public Shader convolveShader;
	[HideInInspector] public Shader maskShader;
	/*[HideInInspector]*/ public Shader replacementShader;
   
	public Dimension	reflectionMapSize = Dimension.x1024;
	public LayerMask	reflectLayerMask = ~0;
	public float		maxDistance = 80f;
	public float		clipPlaneOffset = 0.01f;
	public bool			clipSkyDome;
	public float		nearPlaneDistance = 0.1f;
	public float		farPlaneDistance = 25f;
	public float		mipShift;
	public bool			useMask;
	public bool			useDepth = true;
	public float		depthScale = 1.25f;
	public float		depthExponent = 2.25f;
	public float		depthRayPinchFadeSteps = 4f;
	public Material[]	explicitMaterials;
	public bool 		disableScattering;
	public float 		scatterWorldFakePush = -1f;
	public float 		scatterHeightFakePush = -1f;
	public bool			renderShadows = false;
	public float 		shadowDistance = 200f;
	public int			maxPixelLights = -1;
	public Color		clearColor = Color.gray;
	public UnityEngine.RenderingPath renderingPath = UnityEngine.RenderingPath.UsePlayerSettings;

#if PLANE_REFLECTION_CHEAPER
	int 						m_downscale = 0;
#endif
	Shader						m_lodShader;
	int 						m_lodShaderLod;
#if ALLOW_UNIQUESHADOW_DEPENDENCY
	bool						m_cookielessMainlight;
#endif
	
public RenderTexture			m_reflectionMap; //hacked public for easier debugging
	RenderTexture				m_reflectionDepthMap;
	UnityEngine.Rendering.CommandBuffer m_copyDepthCB;
	Camera						m_reflectionCamera;
	Camera						m_renderCamera;

	Material[]					m_materials = new Material[0];
	Shader[]					m_shaders;

	Material					m_convolveMaterial;

	bool 						m_isActive;
	Renderer					m_renderer;

#if PLANE_REFLECTION_CHEAPER
	public void SetDownscale(int ds) {
		m_downscale = ds;
	}
#endif

	public void SetShaderLod(Shader shader, int lod) {
		m_lodShader = shader;
		m_lodShaderLod = lod;
	}

#if ALLOW_UNIQUESHADOW_DEPENDENCY
	public void SetCookielessMainlight(bool b) {
		m_cookielessMainlight = b;
	}
#endif

#if UNITY_EDITOR
	void OnValidate() {
		OnEnable();
		UnityEditor.SceneView.RepaintAll();
	}
#endif

	bool CheckSupport() {
		bool supported = true;

		if(convolveShader && !convolveShader.isSupported)
			supported = false;
		
		return supported;
	}

	void Awake() {
		m_renderer = GetComponent<Renderer>();

		if(!convolveShader)
			convolveShader = Shader.Find("Hidden/Volund/Convolve");

		if(!m_convolveMaterial)
			m_convolveMaterial = new Material(convolveShader);

		if(CheckSupport()) { 
			EnsureReflectionCamera(null);
			EnsureReflectionTexture();
			EnsureResolveDepthHooks();
		}
	}

	void OnEnable() {
		if(!maskShader)
			return;

		if (m_renderer == null)
			m_renderer = GetComponent<Renderer>();

		if(explicitMaterials != null && explicitMaterials.Length > 0)
			m_materials = explicitMaterials;
		else
			m_materials = m_renderer.sharedMaterials;

		m_shaders = m_shaders != null && m_shaders.Length == m_materials.Length ? m_shaders : new Shader[m_materials.Length];

		for(int i = 0, n = m_materials.Length; i < n; ++i)
			m_shaders[i] = m_materials[i].shader;

		if(!m_convolveMaterial)
			m_convolveMaterial = new Material(convolveShader);

		if(useDepth) {
			m_convolveMaterial.EnableKeyword("USE_DEPTH");
			m_convolveMaterial.SetFloat("_DepthScale", depthScale);
			m_convolveMaterial.SetFloat("_DepthExponent", depthExponent);

		} else {
			m_convolveMaterial.DisableKeyword("USE_DEPTH");
		}

		if(useMask)
			m_convolveMaterial.EnableKeyword("USE_MASK");
		else
			m_convolveMaterial.DisableKeyword("USE_MASK");

#if PLANE_REFLECTION_CHEAPER
		m_convolveMaterial.EnableKeyword("PLANE_REFLECTION_CHEAPER");
#else
		m_convolveMaterial.DisableKeyword("PLANE_REFLECTION_CHEAPER");
#endif
		m_convolveMaterial.hideFlags = HideFlags.DontSave | HideFlags.NotEditable;

		if(CheckSupport())
			EnsureReflectionCamera(null);
	}
	
	void OnDisable() {
#if USE_GLOBAL_KEYWORDS
		Shader.DisableKeyword("PLANE_REFLECTION");
#else
		for(int i = 0, n = m_materials.Length; i < n; ++i)
			m_materials[i].DisableKeyword("PLANE_REFLECTION");
#endif

		m_isActive = false;
	}

	void OnDestroy() {
		if(m_reflectionCamera)
			Object.DestroyImmediate(m_reflectionCamera.gameObject);
		m_reflectionCamera = null;
		if(m_copyDepthCB != null) {
			m_copyDepthCB.Release();
			m_copyDepthCB = null;
		}
		Object.DestroyImmediate(m_convolveMaterial);
		m_convolveMaterial = null;
		Object.DestroyImmediate(m_reflectionMap);
		Object.DestroyImmediate(m_reflectionDepthMap);
	}

	void OnBecameInvisible() {
		CheckCulling(null);
	}

	bool CheckCulling(Camera cam) {
		bool active = false;
		if(cam) {
			var d2 = Vector3.SqrMagnitude(transform.position - cam.transform.position);
			active = d2 < maxDistance * maxDistance;
		}

		if(active == m_isActive)
			return m_isActive;

//		if(cam == m_renderCamera) {
//			if(active) {
//#if USE_GLOBAL_KEYWORDS
//				Shader.EnableKeyword("PLANE_REFLECTION");
//#else
//				for(int i = 0, n = m_materials.Length; i < n; ++i)
//					m_materials[i].EnableKeyword("PLANE_REFLECTION");
//#endif
//			} else {
//#if USE_GLOBAL_KEYWORDS
//				Shader.DisableKeyword("PLANE_REFLECTION");
//#else
//				for(int i = 0, n = m_materials.Length; i < n; ++i)
//					m_materials[i].DisableKeyword("PLANE_REFLECTION");
//#endif

//				// This is probably temp, we'd like to keep this around, or at
//				// the very least shared between instances!
//				Object.DestroyImmediate(m_reflectionMap);
//				m_reflectionMap = null;
//			}
//		}

		return m_isActive = active;
	}

	public void OnWillRenderObject() {
		if(!CheckSupport())
			return;

		//Debug.LogFormat("OnWillRenderObject: {0} from camera {1} (DepthMode: {2})", name, Camera.current.name, Camera.current.depthTextureMode);

		if(Camera.current == Camera.main) {
			m_renderCamera = Camera.current;
#if UNITY_EDITOR
		} else if(UnityEditor.SceneView.currentDrawingSceneView && UnityEditor.SceneView.currentDrawingSceneView.camera == Camera.current) {
			m_renderCamera = Camera.current;
#endif
		} else {
//#if USE_GLOBAL_KEYWORDS
//			Shader.DisableKeyword("PLANE_REFLECTION");
//#else
//			for(int i = 0, n = m_materials.Length; i < n; ++i)
//				m_materials[i].DisableKeyword("PLANE_REFLECTION");
//#endif
			return;
		}

		if(!CheckCulling(m_renderCamera)) {
			m_renderCamera = null;
			return;
		}

		m_reflectionCamera = EnsureReflectionCamera(m_renderCamera);
		EnsureReflectionTexture();
		EnsureResolveDepthHooks();

#if PLANE_REFLECTION_CHEAPER
		var reflectionMapDim = (int)reflectionMapSize >> m_downscale;
		var reflectionMap0 = RenderTexture.GetTemporary(reflectionMapDim, reflectionMapDim, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Default);
		//reflectionMap0.useMipMap = false;
		reflectionMap0.filterMode = FilterMode.Bilinear;
		reflectionMap0.name = "PlaneReflection Full";
#else
		var reflectionMap0 = m_reflectionMap;
#endif

		// find the reflection plane: position and normal in world space
		Vector3 pos = transform.position;
		Vector3 normal = transform.up;

		// Reflect camera around reflection plane
		float d = -Vector3.Dot (normal, pos) - clipPlaneOffset;
		Vector4 reflectionPlane = new Vector4 (normal.x, normal.y, normal.z, d);
	
		Matrix4x4 reflectionMatrix = Matrix4x4.zero;
		CalculateReflectionMatrix(ref reflectionMatrix, reflectionPlane);
		Vector3 newpos = reflectionMatrix.MultiplyPoint(m_renderCamera.transform.position);
		m_reflectionCamera.worldToCameraMatrix = m_renderCamera.worldToCameraMatrix * reflectionMatrix;

		m_reflectionCamera.cullingMask = reflectLayerMask;
		m_reflectionCamera.targetTexture = reflectionMap0;
		m_reflectionCamera.transform.position = newpos;
		m_reflectionCamera.aspect = m_renderCamera.aspect;

		// find the reflection plane: position and normal in world space
		Vector3 planePos = transform.position;
		Vector3 planeNormal = transform.up;
		float planeDist = -Vector3.Dot(planeNormal, planePos) - clipPlaneOffset;
		/*var*/ reflectionPlane = new Vector4(planeNormal.x, planeNormal.y, planeNormal.z, planeDist);

		// reflect the camera about the reflection plane
		var srcCamPos = m_renderCamera.transform.position;
		//var srcCamPos4 = new Vector4(srcCamPos.x, srcCamPos.y, srcCamPos.z, 1f);
		var srcCamRgt = m_renderCamera.transform.right;
		var srcCamUp = m_renderCamera.transform.up;
		var srcCamFwd = m_renderCamera.transform.forward;
		//var reflectedPos = srcCamPos - 2f * Vector4.Dot(reflectionPlane, srcCamPos4) * planeNormal;
		var reflectedDir = -ReflectVector(planeNormal, srcCamFwd);
m_reflectionCamera.transform.rotation = Quaternion.LookRotation(reflectedDir, srcCamUp);

		if(m_reflectionCamera && ssnap) {
			sup = m_reflectionCamera.transform.up;
			spos = m_reflectionCamera.transform.position;
			srot = m_reflectionCamera.transform.rotation;
			sfov = m_reflectionCamera.fieldOfView;
			sfar = m_reflectionCamera.farClipPlane;
			snear = m_reflectionCamera.nearClipPlane;
			saspect = m_reflectionCamera.aspect;
			ssnap = false;
		}

		// Setup user defined clip plane instead of oblique frustum
		Shader.SetGlobalVector("_PlaneReflectionClipPlane", reflectionPlane);
		Shader.EnableKeyword("PLANE_REFLECTION_USER_CLIPPLANE");

#if ALLOW_ATMOSPHERICS_DEPENDENCY
		bool scatteringOcclusionWasEnabled = Shader.IsKeywordEnabled("ATMOSPHERICS_OCCLUSION");
		Shader.DisableKeyword("ATMOSPHERICS_OCCLUSION");

		bool scatteringWasEnabled = Shader.IsKeywordEnabled("ATMOSPHERICS");
		if(disableScattering && scatteringWasEnabled)
			Shader.DisableKeyword("ATMOSPHERICS");

		float oldScatterPushW = float.MaxValue;
		float oldScatterPushH = float.MaxValue;
		// HACKY HACKS FOLLOW! 
		var s = AtmosphericScattering.instance;
		if(s && scatterWorldFakePush >= 0f) {
			oldScatterPushW = -Mathf.Pow(Mathf.Abs(s.worldNearScatterPush), s.worldScaleExponent) * Mathf.Sign(s.worldNearScatterPush);
			Shader.SetGlobalFloat("u_WorldNearScatterPush", -Mathf.Pow(Mathf.Abs(scatterWorldFakePush), s.worldScaleExponent) * Mathf.Sign(scatterWorldFakePush));
		}
		if(s && scatterWorldFakePush >= 0f) {
			oldScatterPushH = -Mathf.Pow(Mathf.Abs(s.heightNearScatterPush), s.worldScaleExponent) * Mathf.Sign(s.heightNearScatterPush);
			Shader.SetGlobalFloat("u_HeightNearScatterPush", -Mathf.Pow(Mathf.Abs(scatterHeightFakePush), s.worldScaleExponent) * Mathf.Sign(scatterHeightFakePush));
		}

		if(clipSkyDome) {
			Shader.EnableKeyword("CLIP_SKYDOME");
			Shader.SetGlobalFloat("u_SkyDomeClipHeight", transform.position.y + clipPlaneOffset);
		}
#endif

		int oldLodShaderLod = 0;
		if(m_lodShader) {
			oldLodShaderLod = m_lodShader.maximumLOD;
			m_lodShader.maximumLOD = m_lodShaderLod;
		}

#if ALLOW_UNIQUESHADOW_DEPENDENCY
		Light oldMainLight = null;
		Texture oldMainLightCookie = null;
		if(m_cookielessMainlight) {
			var mask = ~((1 << LayerMask.NameToLayer("Characters")) | (1 << LayerMask.NameToLayer("CharactersSkin")));

			// Try somewhat hard to find an active directional cookie light since it has a big impact.
            var cookieLight = UniqueShadowSun.instance && (UniqueShadowSun.instance.cullingMask & mask) != 0 ? UniqueShadowSun.instance : null;
			             if(!cookieLight) {
				var suns = GameObject.FindGameObjectsWithTag("Sun");
				for(int i = 0, n = suns.Length; i < n; ++i) {
					var sl = suns[i].GetComponent<Light>();
					if(sl && sl.enabled && (sl.cullingMask & mask)!= 0 && sl.cookie) {
						cookieLight = sl;
						break;
					}
				}
			}
			if(cookieLight) {
				oldMainLight = cookieLight;
				oldMainLightCookie = cookieLight.cookie;
				cookieLight.cookie = null;
			}
		}
#endif

		var oldShadowDist = QualitySettings.shadowDistance;
		if(!renderShadows)
			QualitySettings.shadowDistance = 0f;
		else if(shadowDistance > 0f)
			QualitySettings.shadowDistance = shadowDistance;

		var oldPixelLights = QualitySettings.pixelLightCount;
		if(maxPixelLights != -1)
			QualitySettings.pixelLightCount = maxPixelLights;

#if USE_GLOBAL_KEYWORDS
		Shader.DisableKeyword("PLANE_REFLECTION");
#else
		for(int i = 0, n = m_materials.Length; i < n; ++i)
			m_materials[i].DisableKeyword("PLANE_REFLECTION");
#endif

		GL.invertCulling = true;
        if(replacementShader)
            m_reflectionCamera.SetReplacementShader(replacementShader, "");
		m_reflectionCamera.Render();
		GL.invertCulling = false;

#if USE_GLOBAL_KEYWORDS
		Shader.EnableKeyword("PLANE_REFLECTION");
#else
		for(int i = 0, n = m_materials.Length; i < n; ++i)
			m_materials[i].EnableKeyword("PLANE_REFLECTION");
#endif

		if(!renderShadows || shadowDistance > 0f)
			QualitySettings.shadowDistance = oldShadowDist;
		if(maxPixelLights != -1)
			QualitySettings.pixelLightCount = oldPixelLights;

#if ALLOW_UNIQUESHADOW_DEPENDENCY
		if(oldMainLight)
			oldMainLight.cookie = oldMainLightCookie;
#endif

		if(m_lodShader)
			m_lodShader.maximumLOD = oldLodShaderLod;

		Shader.DisableKeyword("PLANE_REFLECTION_USER_CLIPPLANE");

		SetupConvolveParams(srcCamPos, srcCamRgt, srcCamUp, srcCamFwd, reflectionMatrix, planeNormal);
		Convolve(m_reflectionCamera.targetTexture, m_reflectionDepthMap);
		m_reflectionCamera.targetTexture = null;

#if ALLOW_ATMOSPHERICS_DEPENDENCY
		if(scatteringOcclusionWasEnabled)
			Shader.EnableKeyword("ATMOSPHERICS_OCCLUSION");

		if(disableScattering && scatteringWasEnabled)
			Shader.EnableKeyword("ATMOSPHERICS");

		if(oldScatterPushW != float.MaxValue)
			Shader.SetGlobalFloat("u_WorldNearScatterPush", oldScatterPushW);
		if(oldScatterPushH != float.MaxValue)
			Shader.SetGlobalFloat("u_HeightNearScatterPush", oldScatterPushH);

		if(clipSkyDome)
			Shader.DisableKeyword("CLIP_SKYDOME");
#endif

		float mipCount = Mathf.Max(0f, Mathf.Round(Mathf.Log ((float)m_reflectionMap.width, 2f)) - mipShift);
#if USE_GLOBAL_KEYWORDS
		Shader.SetGlobalFloat("_PlaneReflectionLodSteps", mipCount);
		Shader.SetGlobalTexture("_PlaneReflection", m_reflectionMap);
#else
		for(int i = 0, n = m_materials.Length; i < n; ++i) {
			var m = m_materials[i];
			if(useMask)
				m.shader = m_shaders[i];
			m.SetFloat("_PlaneReflectionLodSteps", mipCount);
			m.SetTexture("_PlaneReflection", m_reflectionMap);
		}
#endif
	}

	void EnsureReflectionTexture() {
#if PLANE_REFLECTION_CHEAPER
		var expectedSize = (int)reflectionMapSize >> 1;
#else
		var expectedSize = (int)reflectionMapSize;
#endif
		if(m_reflectionMap == null || m_reflectionMap.width != expectedSize || ((m_reflectionMap.depth == 0) == (m_reflectionCamera.actualRenderingPath == RenderingPath.Forward))) {
			Object.DestroyImmediate(m_reflectionMap);
			Object.DestroyImmediate(m_reflectionDepthMap);
			m_reflectionMap = new RenderTexture(expectedSize, expectedSize, m_reflectionCamera.actualRenderingPath == RenderingPath.Forward ? 16 : 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
			m_reflectionMap.name = "PlaneReflection Full Color";
			m_reflectionMap.useMipMap = true;
			m_reflectionMap.autoGenerateMips = false;
			m_reflectionMap.filterMode = FilterMode.Trilinear;
			m_reflectionMap.hideFlags = HideFlags.DontSave | HideFlags.NotEditable;
			m_reflectionDepthMap = new RenderTexture(expectedSize, expectedSize, 0, RenderTextureFormat.RHalf, RenderTextureReadWrite.Linear);
			m_reflectionDepthMap.name = "PlaneReflection Full Depth";
			m_reflectionDepthMap.useMipMap = false;
			m_reflectionDepthMap.hideFlags = HideFlags.DontSave | HideFlags.NotEditable;
		}
	}

	void EnsureResolveDepthHooks() {
		if(m_copyDepthCB == null) {
			m_copyDepthCB = new UnityEngine.Rendering.CommandBuffer();
			m_copyDepthCB.name = "CopyResolveReflectionDepth";
			m_copyDepthCB.Blit(
				new UnityEngine.Rendering.RenderTargetIdentifier(UnityEngine.Rendering.BuiltinRenderTextureType.None),
				new UnityEngine.Rendering.RenderTargetIdentifier(m_reflectionDepthMap),
				m_convolveMaterial,
				2
			);
		}

		if(m_reflectionCamera.commandBufferCount == 0)
			m_reflectionCamera.AddCommandBuffer(UnityEngine.Rendering.CameraEvent.AfterEverything, m_copyDepthCB);
	}
	void SetupConvolveParams(Vector3 camPos, Vector3 camRgt, Vector3 camUp, Vector3 camFwd, Matrix4x4 reflectionMatrix, Vector3 planeNormal) {
		camPos = reflectionMatrix.MultiplyPoint(camPos);
		camRgt = -ReflectVector(camRgt, planeNormal);
		camUp = -ReflectVector(camUp, planeNormal);
		camFwd = -ReflectVector(camFwd, planeNormal);

		var camNear = m_reflectionCamera.nearClipPlane;
		var camFar = m_reflectionCamera.farClipPlane;
		var camFov = m_reflectionCamera.fieldOfView;
		var camAspect = m_reflectionCamera.aspect;

		var frustumCorners = Matrix4x4.identity;

		var fovWHalf = camFov * 0.5f;
		var tanFov = Mathf.Tan(fovWHalf * Mathf.Deg2Rad);

		var toRight = camRgt * camNear * tanFov * camAspect;
		var toTop = camUp * camNear * tanFov;

		var topLeft = (camFwd * camNear - toRight + toTop);
		var camScale = topLeft.magnitude * camFar / camNear;

		topLeft.Normalize();
		topLeft *= camScale;

		Vector3 topRight = camFwd * camNear + toRight + toTop;
		topRight.Normalize();
		topRight *= camScale;

		Vector3 bottomRight = camFwd * camNear + toRight - toTop;
		bottomRight.Normalize();
		bottomRight *= camScale;

		Vector3 bottomLeft = camFwd * camNear - toRight - toTop;
		bottomLeft.Normalize();
		bottomLeft *= camScale;

		frustumCorners.SetRow(0, topLeft);
		frustumCorners.SetRow(1, topRight);
		frustumCorners.SetRow(2, bottomRight);
		frustumCorners.SetRow(3, bottomLeft);

		Vector4 camPos4 = new Vector4(camPos.x, camPos.y, camPos.z, 1f);
		m_convolveMaterial.SetMatrix("_FrustumCornersWS", frustumCorners);
		m_convolveMaterial.SetVector("_CameraWS", camPos4);
		var zparams = Vector4.zero;
		zparams.y = farPlaneDistance / nearPlaneDistance;
		zparams.x = 1f - zparams.y;
		zparams.z = zparams.x / farPlaneDistance;
		zparams.z = zparams.y / farPlaneDistance;
#if UNITY_5_5_OR_NEWER
//requires version>5.5b10:		if(SystemInfo.usesReversedZBuffer)
		{
			zparams.y += zparams.x;
			zparams.x = -zparams.x;
			zparams.w += zparams.z;
			zparams.z = -zparams.z;
		}
#endif
		m_convolveMaterial.SetVector("_PlaneReflectionZParams", zparams);
	}

	void Convolve(RenderTexture reflectionMap0, RenderTexture reflectionDepth) {
		// The simplest and most naive texture convolve the world ever saw. It sorta
		// gets the job done, though.

		var oldRT = RenderTexture.active;

		m_convolveMaterial.SetTexture("_CameraDepthTextureCopy", reflectionDepth);

#if PLANE_REFLECTION_CHEAPER
		ConvolveStep(0, reflectionMap0, 0, m_reflectionMap, 0);
		RenderTexture.ReleaseTemporary(reflectionMap0);

		for(int i = 0, n = m_reflectionMap.width; (n >> i) > 1; ++i) 
			ConvolveStep(i + 1, m_reflectionMap, i, m_reflectionMap, i+1);

		m_convolveMaterial.DisableKeyword("CP3");
#else
		for(int i = 0, n = m_reflectionMap.width; (n >> i) > 1; ++i) 
			ConvolveStep(i, m_reflectionMap, i, i+1);
#endif

		RenderTexture.active = oldRT;
	}

#if PLANE_REFLECTION_CHEAPER
	void ConvolveStep(RenderTexture srcMap, int srcMip, RenderTexture dstMap, int dstMip) {
		var srcSize = srcMap.width >> srcMip;
		var tmp = RenderTexture.GetTemporary(srcSize >> 1, srcSize, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
		tmp.name = "PlaneReflection Half";

		if(dstMip == 0) {
			m_convolveMaterial.EnableKeyword("CP0");
		} else if(dstMip == 1) {
			m_convolveMaterial.DisableKeyword("CP0");
			m_convolveMaterial.EnableKeyword("CP1");
		} else if(dstMip == 2) {
			m_convolveMaterial.DisableKeyword("CP1");
			m_convolveMaterial.EnableKeyword("CP2");
		} else  {
			m_convolveMaterial.DisableKeyword("CP2");
			m_convolveMaterial.EnableKeyword("CP3");
		}


		var power = 2048 >> dstMip;
		m_convolveMaterial.SetFloat("_CosPower", (float)power / 1000f);
		
		m_convolveMaterial.SetFloat("_SampleMip", (float)srcMip);
		Graphics.SetRenderTarget(tmp, 0);
		Graphics.Blit(srcMap, m_convolveMaterial, 0);
		
		m_convolveMaterial.SetFloat("_SampleMip", 0f);
		Graphics.SetRenderTarget(dstMap, dstMip);
		Graphics.Blit(tmp, m_convolveMaterial, 1);
		
		RenderTexture.ReleaseTemporary(tmp);
	}
#else
	void ConvolveStep(int step, RenderTexture srcMap, int srcMip, int dstMip) {
		var srcSize = m_reflectionMap.width >> srcMip;
		var tmp = RenderTexture.GetTemporary(srcSize, srcSize, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
		tmp.name = "PlaneReflection Half";

		var power = 2048 >> dstMip;
		m_convolveMaterial.SetFloat("_CosPower", (float)power / 1000f);
		m_convolveMaterial.SetFloat("_SampleMip", (float)srcMip);
		m_convolveMaterial.SetFloat("_RayPinchInfluence", Mathf.Clamp01((float)step / depthRayPinchFadeSteps));
		Graphics.SetRenderTarget(tmp, 0);
		CustomGraphicsBlit(srcMap, m_convolveMaterial, 0);

		m_convolveMaterial.SetFloat("_SampleMip", 0f);
		Graphics.SetRenderTarget(m_reflectionMap, dstMip);
		CustomGraphicsBlit(tmp, m_convolveMaterial, 1);
		
		RenderTexture.ReleaseTemporary(tmp);
	}
#endif
	
	static void CustomGraphicsBlit(RenderTexture src, Material mat, int pass) {
		mat.SetTexture("_MainTex", src);
		
		GL.PushMatrix();
		GL.LoadOrtho();
		
		mat.SetPass(pass);
		
		GL.Begin(GL.QUADS);
		
		GL.MultiTexCoord2(0, 0.0f, 0.0f);
		GL.Vertex3(0.0f, 0.0f, 3.0f); // BL
		
		GL.MultiTexCoord2(0, 1.0f, 0.0f);
		GL.Vertex3(1.0f, 0.0f, 2.0f); // BR
		
		GL.MultiTexCoord2(0, 1.0f, 1.0f);
		GL.Vertex3(1.0f, 1.0f, 1.0f); // TR
		
		GL.MultiTexCoord2(0, 0.0f, 1.0f);
		GL.Vertex3(0.0f, 1.0f, 0.0f); // TL
		
		GL.End();
		GL.PopMatrix();
	}

	void OnRenderObject() {
		if(!CheckSupport())
			return;

		//Debug.LogFormat("OnRenderObject: {0} from camera {1} (self rendercam: {2})", name, Camera.current.name, m_renderCamera);

		if(Camera.current != m_renderCamera) {
//#if USE_GLOBAL_KEYWORDS
//			Shader.EnableKeyword("PLANE_REFLECTION");
//#else
//			for(int i = 0, n = m_materials.Length; i < n; ++i)
//				m_materials[i].EnableKeyword("PLANE_REFLECTION");
//#endif
		} else {
			m_renderCamera = null;
		}
	}

	Camera EnsureReflectionCamera(Camera renderCamera) {
		if(!m_reflectionCamera) {
			var goCam = new GameObject(string.Format("#> _Planar Reflection Camera < ({0})", name));
			goCam.hideFlags = HideFlags.DontSave | HideFlags.NotEditable | HideFlags.HideInHierarchy;

			m_reflectionCamera = goCam.AddComponent<Camera>();
			m_reflectionCamera.enabled = false;
		}

		if(renderCamera) {
			m_reflectionCamera.CopyFrom(renderCamera);

			// Undo some thing we don't want copied.
			m_reflectionCamera.ResetProjectionMatrix(); // definitely don't want to inherit an explicit projection matrix
			m_reflectionCamera.renderingPath = renderingPath == RenderingPath.UsePlayerSettings ? m_renderCamera.actualRenderingPath : renderingPath;
			m_reflectionCamera.allowHDR = renderCamera.allowHDR;
			m_reflectionCamera.rect = new Rect(0f, 0f, 1f, 1f);
		} else {
			m_reflectionCamera.renderingPath = renderingPath;
		}
		m_reflectionCamera.backgroundColor = clearColor;
		m_reflectionCamera.clearFlags = CameraClearFlags.SolidColor;
		m_reflectionCamera.depthTextureMode = useDepth ? DepthTextureMode.Depth : DepthTextureMode.None;
		m_reflectionCamera.useOcclusionCulling = false;
		m_reflectionCamera.nearClipPlane = nearPlaneDistance;
		m_reflectionCamera.farClipPlane = farPlaneDistance + nearPlaneDistance;

		//if(!disableScattering && !m_reflectionCamera.GetComponent<AtmosphericScatteringDeferred>())
		//	m_reflectionCamera.gameObject.AddComponent<AtmosphericScatteringDeferred>();

		return m_reflectionCamera;
	}

	static Vector3 ReflectVector(Vector3 vec, Vector3 normal) {
		return 2f * Vector3.Dot(normal, vec) * normal - vec;
	}

	static void CalculateReflectionMatrix(ref Matrix4x4 reflectionMat, Vector4 plane) {
	    reflectionMat.m00 = (1F - 2F*plane[0]*plane[0]);
	    reflectionMat.m01 = (   - 2F*plane[0]*plane[1]);
	    reflectionMat.m02 = (   - 2F*plane[0]*plane[2]);
	    reflectionMat.m03 = (   - 2F*plane[3]*plane[0]);

	    reflectionMat.m10 = (   - 2F*plane[1]*plane[0]);
	    reflectionMat.m11 = (1F - 2F*plane[1]*plane[1]);
	    reflectionMat.m12 = (   - 2F*plane[1]*plane[2]);
	    reflectionMat.m13 = (   - 2F*plane[3]*plane[1]);
	
    	reflectionMat.m20 = (   - 2F*plane[2]*plane[0]);
    	reflectionMat.m21 = (   - 2F*plane[2]*plane[1]);
    	reflectionMat.m22 = (1F - 2F*plane[2]*plane[2]);
    	reflectionMat.m23 = (   - 2F*plane[3]*plane[2]);

    	reflectionMat.m30 = 0F;
    	reflectionMat.m31 = 0F;
    	reflectionMat.m32 = 0F;
    	reflectionMat.m33 = 1F;
	}


	public bool ssnap;
	Vector3 spos, sup; Quaternion srot;
	float sfov, snear, sfar, saspect;
	void OnDrawGizmos() {
		Gizmos.color = Color.red;
		var s = transform.rotation * new Vector3(0.15f, 0.05f, 0.1f);
		s.Set(Mathf.Abs(s.x), Mathf.Abs(s.y), s.z = Mathf.Abs(s.z));
		Gizmos.DrawCube(transform.position, s);
		Gizmos.DrawSphere(transform.position + transform.up * 0.025f, 0.05f);

		if(sfov != 0f && snear != 0f && sfar != 0f) {
			Gizmos.DrawLine(spos, spos + sup * 0.5f);
			Gizmos.matrix = Matrix4x4.TRS(spos, srot, Vector3.one);
			Gizmos.DrawFrustum(Vector3.zero, sfov, sfar, snear, saspect);
		}
	}
}
