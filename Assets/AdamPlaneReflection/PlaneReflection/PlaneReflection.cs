using UnityEngine;
using UnityEngine.Rendering;
using System.Collections.Generic;

[ExecuteInEditMode]
public class PlaneReflection : MonoBehaviour {
    public enum Dimension {
        x128    = 128,
        x256    = 256,
        x512    = 512,
        x1024   = 1024,
        x2048   = 2048,
        x4096   = 4096,
    }

    [HideInInspector] public Shader convolveShader;

    public Dimension    reflectionMapSize = Dimension.x1024;
    public LayerMask    reflectLayerMask = ~0;
    public float        clipPlaneOffset = 0.01f;
    public bool         clipSkyDome;
    public float        nearPlaneDistance = 0.1f;
    public float        farPlaneDistance = 25f;
    public float        mipShift;
    public bool         useDepth = true;
    public float        depthScale = 1.25f;
    public float        depthExponent = 2.25f;
    public float        depthRayPinchFadeSteps = 4f;
    public bool         renderShadows = false;
    public float        shadowDistance = 200f;
    public int          maxPixelLights = -1;
    public Color        clearColor = Color.gray;
    public RenderingPath renderingPath = RenderingPath.UsePlayerSettings;

    RenderTexture               m_reflectionMap;
    RenderTexture               m_reflectionDepthMap;
    CommandBuffer m_copyDepthCB;
    Camera                      m_reflectionCamera;
    Camera                      m_renderCamera;

    Material[]                  m_materials = new Material[0];

    Material                    m_convolveMaterial;

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
        m_materials = GetComponent<Renderer>().sharedMaterials;

        if(!m_convolveMaterial)
            m_convolveMaterial = new Material(convolveShader);

        if(useDepth) {
            m_convolveMaterial.EnableKeyword("USE_DEPTH");
            m_convolveMaterial.SetFloat("_DepthScale", depthScale);
            m_convolveMaterial.SetFloat("_DepthExponent", depthExponent);

        } else {
            m_convolveMaterial.DisableKeyword("USE_DEPTH");
        }

        m_convolveMaterial.hideFlags = HideFlags.DontSave | HideFlags.NotEditable;

        if(CheckSupport())
            EnsureReflectionCamera(null);
    }

    void OnDisable() {
        for(int i = 0, n = m_materials.Length; i < n; ++i)
            m_materials[i].DisableKeyword("PLANE_REFLECTION");
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
            return;
        }

        m_reflectionCamera = EnsureReflectionCamera(m_renderCamera);
        EnsureReflectionTexture();
        EnsureResolveDepthHooks();

        var reflectionMap0 = m_reflectionMap;

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

        var oldShadowDist = QualitySettings.shadowDistance;
        if(!renderShadows)
            QualitySettings.shadowDistance = 0f;
        else if(shadowDistance > 0f)
            QualitySettings.shadowDistance = shadowDistance;

        var oldPixelLights = QualitySettings.pixelLightCount;
        if(maxPixelLights != -1)
            QualitySettings.pixelLightCount = maxPixelLights;

        for(int i = 0, n = m_materials.Length; i < n; ++i)
            m_materials[i].DisableKeyword("PLANE_REFLECTION");

        GL.invertCulling = true;
        m_reflectionCamera.Render();
        GL.invertCulling = false;

        for(int i = 0, n = m_materials.Length; i < n; ++i)
            m_materials[i].EnableKeyword("PLANE_REFLECTION");

        if(!renderShadows || shadowDistance > 0f)
            QualitySettings.shadowDistance = oldShadowDist;
        if(maxPixelLights != -1)
            QualitySettings.pixelLightCount = oldPixelLights;

        Shader.DisableKeyword("PLANE_REFLECTION_USER_CLIPPLANE");

        SetupConvolveParams(srcCamPos, srcCamRgt, srcCamUp, srcCamFwd, reflectionMatrix, planeNormal);
        Convolve(m_reflectionCamera.targetTexture, m_reflectionDepthMap);
        m_reflectionCamera.targetTexture = null;

        float mipCount = Mathf.Max(0f, Mathf.Round(Mathf.Log ((float)m_reflectionMap.width, 2f)) - mipShift);
        for(int i = 0, n = m_materials.Length; i < n; ++i) {
            var m = m_materials[i];
            m.SetFloat("_PlaneReflectionLodSteps", mipCount);
            m.SetTexture("_PlaneReflection", m_reflectionMap);
        }
    }

    void EnsureReflectionTexture() {
        var expectedSize = (int)reflectionMapSize;
        if(m_reflectionMap == null || m_reflectionMap.width != expectedSize || ((m_reflectionMap.depth == 0) == (m_reflectionCamera.actualRenderingPath == RenderingPath.Forward))) {
            Object.DestroyImmediate(m_reflectionMap);
            Object.DestroyImmediate(m_reflectionDepthMap);
            m_reflectionMap = new RenderTexture(expectedSize, expectedSize, 16, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
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
            m_copyDepthCB = new CommandBuffer();
            m_copyDepthCB.name = "CopyResolveReflectionDepth";
            m_copyDepthCB.Blit(
                new RenderTargetIdentifier(BuiltinRenderTextureType.None),
                new RenderTargetIdentifier(m_reflectionDepthMap),
                m_convolveMaterial,
                2
            );
        }

        if(m_reflectionCamera.commandBufferCount == 0)
            m_reflectionCamera.AddCommandBuffer(CameraEvent.AfterEverything, m_copyDepthCB);
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
//requires version>5.5b10:
        if(SystemInfo.usesReversedZBuffer)
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

        for(int i = 0, n = m_reflectionMap.width; (n >> i) > 1; ++i)
            ConvolveStep(i, m_reflectionMap, i, i+1);

        RenderTexture.active = oldRT;
    }

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
