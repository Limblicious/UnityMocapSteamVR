using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace MocapTools
{
    /// <summary>
    /// Static world-space planar mirror for the FBT feedback system.
    ///
    /// The mirror surface is a scene object (usually a Quad) whose visible
    /// side faces local -Z. Each frame this component renders the scene from
    /// the viewpoint reflected across that side of the surface, so the quad
    /// shows a live, geometrically correct reflection of the avatar.
    ///
    /// Cameras:
    ///   - Play Mode: reflects the game/XR camera (per-eye in stereo).
    ///   - Edit Mode: reflects the Scene view camera for a live preview.
    /// The reflection is captured before each source camera renders
    /// (RenderPipelineManager.beginCameraRendering, the URP hook), so there
    /// is no one-frame latency.
    ///
    /// The surface and all mocap UI visuals (tracker spheres, menu panel,
    /// laser) live on the Ignore Raycast layer (2), which is excluded from
    /// the reflection so the mirror never renders itself or the feedback UI.
    /// </summary>
    [ExecuteAlways]
    [DefaultExecutionOrder(-100)]
    public class MocapFbtPlanarMirror : MonoBehaviour
    {
        const string ShaderName = "MocapTools/URP/PlanarMirror";
        const int UiLayer = 2;

        [Header("Surface")]
        [Tooltip("The mirror plane object. Its visible side faces local -Z. Auto-resolved to a child named 'MirrorPlane'.")]
        public Transform mirrorSurface;

        [Header("Reflection")]
        [Tooltip("Fixed reflection texture size used when Dynamic Resolution is off.")]
        public int textureWidth = 768;
        public int textureHeight = 1024;
        [Tooltip("Matches the reflection texture to the source view (monitor or HMD per-eye resolution), capped by Max Resolution. Behaves like VRChat's Auto mirror resolution.")]
        public bool dynamicResolution = true;
        [Tooltip("Maximum size per dimension for dynamic resolution (VRChat uses 2048).")]
        public int maxResolution = 2048;
        [Range(0.25f, 1f)]
        [Tooltip("Multiplier applied to the source resolution to trade sharpness for performance.")]
        public float resolutionScale = 1f;
        [Tooltip("Excludes the Ignore Raycast layer (2) - all mocap UI visuals - from the reflection.")]
        public bool excludeUiLayer = true;
        [Tooltip("Camera to reflect. Leave null for automatic: game/XR camera in Play Mode, Scene view camera in Edit Mode.")]
        public Camera sourceCamera;

        public bool IsMirrorEnabled { get; private set; } = true;

        Camera _reflCam;
        GameObject _reflCamGo;
        RenderTexture _rtLeft;
        RenderTexture _rtRight;
        MeshRenderer _surfaceRenderer;
        MaterialPropertyBlock _mpb;
        int _lastFrame = -1;
        readonly HashSet<Camera> _renderedThisFrame = new HashSet<Camera>();

        void OnEnable()
        {
            if (mirrorSurface == null)
                mirrorSurface = transform.Find("MirrorPlane");
            if (mirrorSurface != null)
                _surfaceRenderer = mirrorSurface.GetComponent<MeshRenderer>();

            EnsureRenderTextures(textureWidth, textureHeight);
            EnsureReflectionCamera();
            SetMirrorEnabled(true);

            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
#if UNITY_EDITOR
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
#endif
        }

        void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
#if UNITY_EDITOR
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
#endif
            ClearSurfaceBlock();
            ReleaseReflectionCamera();
            ReleaseRenderTextures();
        }

#if UNITY_EDITOR
        void OnBeforeAssemblyReload()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            ClearSurfaceBlock();
            ReleaseReflectionCamera();
            ReleaseRenderTextures();
        }
#endif

        /// <summary>
        /// Toggles the mirror surface and reflection rendering.
        /// </summary>
        public void SetMirrorEnabled(bool enabled)
        {
            IsMirrorEnabled = enabled;
            if (_surfaceRenderer != null)
                _surfaceRenderer.enabled = enabled;
        }

        #region Reflection rendering

        void OnBeginCameraRendering(ScriptableRenderContext context, Camera cam)
        {
            if (!isActiveAndEnabled || !IsMirrorEnabled)
                return;
            if (_reflCam == null || mirrorSurface == null)
                return;
            if (cam == null)
                return;
            if (ReferenceEquals(cam, _reflCam))
                return;

            // Game/XR cameras render the headset view in Play Mode; the
            // Scene view camera renders the editor preview. Both stay
            // disabled and render into target textures, so neither the
            // enabled nor the targetTexture state can be used to filter
            // them. Only Game and SceneView camera types ever qualify.
            if (cam.cameraType != CameraType.Game && cam.cameraType != CameraType.SceneView)
                return;
            if (sourceCamera != null && !ReferenceEquals(cam, sourceCamera))
                return;
#if UNITY_EDITOR
            if (sourceCamera == null)
            {
                if (Application.isPlaying
                    ? cam.cameraType != CameraType.Game
                    : cam.cameraType != CameraType.SceneView)
                    return;
            }
#endif

            // Render each source camera once per frame. URP can report the
            // same camera once per stereo eye, so dedupe per camera per frame.
            // Time.frameCount does not advance in Edit Mode, so Scene view
            // repaints are never deduped (they already serialize rendering).
            if (!Application.isPlaying || Time.frameCount != _lastFrame)
            {
                _renderedThisFrame.Clear();
                _lastFrame = Application.isPlaying ? Time.frameCount : -1;
            }
            if (!_renderedThisFrame.Add(cam))
                return;

            if (cam.stereoEnabled)
            {
                RenderEye(cam, Camera.StereoscopicEye.Left);
                RenderEye(cam, Camera.StereoscopicEye.Right);
            }
            else
            {
                RenderEye(cam, Camera.StereoscopicEye.Left, useStereo: false);
            }
        }

        void RenderEye(Camera sourceCam, Camera.StereoscopicEye eye, bool useStereo = true)
        {
            Vector3 planePos = mirrorSurface.position;
            // The Quad's visible side faces local -Z, so the reflective
            // normal points along -forward.
            Vector3 planeNormal = -mirrorSurface.forward;

            // Per-eye matrices: in stereo the headset provides separate view
            // and projection matrices for each eye. The eye's world pose is
            // recovered from its view matrix so each eye is reflected from
            // its own position instead of the head center.
            Matrix4x4 eyeView = useStereo ? sourceCam.GetStereoViewMatrix(eye) : sourceCam.worldToCameraMatrix;
            Matrix4x4 eyeProj = useStereo ? sourceCam.GetStereoProjectionMatrix(eye) : sourceCam.projectionMatrix;
            Matrix4x4 invView = eyeView.inverse;
            Vector3 eyePos = invView.MultiplyPoint3x4(Vector3.zero);
            Quaternion eyeRot = invView.rotation;

            // Nothing to reflect when the camera is on or behind the plane.
            if (Vector3.Dot(planeNormal, eyePos - planePos) <= 0.01f)
                return;

            // Keep the render targets matched to the source view size.
            Vector2Int targetSize = ResolveTargetSize(sourceCam);
            EnsureRenderTextures(targetSize.x, targetSize.y);

            // Mirror the viewpoint and view matrix across the surface plane.
            Vector4 plane = new Vector4(
                planeNormal.x, planeNormal.y, planeNormal.z,
                -Vector3.Dot(planeNormal, planePos));
            Matrix4x4 reflection = CalculateReflectionMatrix(plane);

            Vector3 reflectedPos = reflection.MultiplyPoint3x4(eyePos);
            _reflCam.transform.position = reflectedPos;
            _reflCam.transform.rotation = ReflectionRotation(eyeRot, planeNormal);

            _reflCam.worldToCameraMatrix = eyeView * reflection;

            // Oblique near plane at the mirror surface clips everything
            // behind it, so the reflection never renders through the mirror.
            Vector4 clipPlane = CameraSpacePlane(_reflCam, planePos, planeNormal, 1f, 0f);
            _reflCam.projectionMatrix = ObliqueProjection(eyeProj, clipPlane);

            _reflCam.cullingMask = excludeUiLayer
                ? sourceCam.cullingMask & ~(1 << UiLayer)
                : sourceCam.cullingMask;
            _reflCam.targetTexture = eye == Camera.StereoscopicEye.Left ? _rtLeft : _rtRight;

            bool previousInvert = GL.invertCulling;
            GL.invertCulling = true;
            try
            {
                _reflCam.Render();
            }
            finally
            {
                GL.invertCulling = previousInvert;
            }
        }

        /// <summary>
        /// Resolves the reflection texture size from the source view,
        /// mirroring VRChat's Auto mirror resolution: the monitor resolution
        /// in desktop mode or the HMD resolution per eye in VR, capped by
        /// MaxResolution and scaled by ResolutionScale.
        /// </summary>
        Vector2Int ResolveTargetSize(Camera sourceCam)
        {
            int w = 0;
            int h = 0;
            if (dynamicResolution)
            {
                if (sourceCam.stereoEnabled)
                {
#if ENABLE_VR && ENABLE_XR_MODULE
                    w = UnityEngine.XR.XRSettings.eyeTextureWidth;
                    h = UnityEngine.XR.XRSettings.eyeTextureHeight;
#endif
                    if (w <= 0 || h <= 0)
                    {
                        w = sourceCam.pixelWidth;
                        h = sourceCam.pixelHeight;
                    }
                }
                else
                {
                    w = sourceCam.pixelWidth;
                    h = sourceCam.pixelHeight;
                }

                w = Mathf.RoundToInt(w * resolutionScale);
                h = Mathf.RoundToInt(h * resolutionScale);
            }

            if (w <= 0 || h <= 0)
            {
                w = textureWidth;
                h = textureHeight;
            }

            return new Vector2Int(
                Mathf.Clamp(w, 64, Mathf.Max(64, maxResolution)),
                Mathf.Clamp(h, 64, Mathf.Max(64, maxResolution)));
        }

        static Quaternion ReflectionRotation(Quaternion rotation, Vector3 normal)
        {
            Vector3 forward = Vector3.Reflect(rotation * Vector3.forward, normal);
            Vector3 up = Vector3.Reflect(rotation * Vector3.up, normal);
            if (forward.sqrMagnitude < 0.0001f)
                forward = -normal;
            return Quaternion.LookRotation(forward, up);
        }

        static Matrix4x4 CalculateReflectionMatrix(Vector4 plane)
        {
            Matrix4x4 m = Matrix4x4.identity;
            float x = plane.x, y = plane.y, z = plane.z, w = plane.w;

            m.m00 = 1f - 2f * x * x;
            m.m01 = -2f * y * x;
            m.m02 = -2f * z * x;
            m.m03 = -2f * w * x;

            m.m10 = -2f * x * y;
            m.m11 = 1f - 2f * y * y;
            m.m12 = -2f * z * y;
            m.m13 = -2f * w * y;

            m.m20 = -2f * x * z;
            m.m21 = -2f * y * z;
            m.m22 = 1f - 2f * z * z;
            m.m23 = -2f * w * z;

            m.m30 = 0f;
            m.m31 = 0f;
            m.m32 = 0f;
            m.m33 = 1f;
            return m;
        }

        /// <summary>
        /// Modifies a projection matrix so its near plane becomes the given
        /// camera-space clip plane (Lengyel-style oblique projection).
        /// </summary>
        static Matrix4x4 ObliqueProjection(Matrix4x4 projection, Vector4 clipPlane)
        {
            Vector4 q = projection.inverse * new Vector4(
                Mathf.Sign(clipPlane.x),
                Mathf.Sign(clipPlane.y),
                1f,
                1f);
            Vector4 c = clipPlane * (2f / Vector4.Dot(clipPlane, q));

            Matrix4x4 m = projection;
            m.m20 = c.x - m.m30;
            m.m21 = c.y - m.m31;
            m.m22 = c.z - m.m32;
            m.m23 = c.w - m.m33;
            return m;
        }

        static Vector4 CameraSpacePlane(Camera cam, Vector3 pos, Vector3 normal, float sideSign, float clipPlaneOffset)
        {
            Vector3 offsetPos = pos + normal * clipPlaneOffset;
            Matrix4x4 m = cam.worldToCameraMatrix;
            Vector3 cpos = m.MultiplyPoint(offsetPos);
            Vector3 cnormal = m.MultiplyVector(normal).normalized * sideSign;
            return new Vector4(cnormal.x, cnormal.y, cnormal.z, -Vector3.Dot(cpos, cnormal));
        }

        #endregion

        #region Resource management

        void EnsureRenderTextures(int width, int height)
        {
            if (_rtLeft != null && _rtRight != null &&
                _rtLeft.width == width && _rtLeft.height == height)
                return;

            ReleaseRenderTextures();

            _rtLeft = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
            {
                name = "MocapFbtMirror.Left"
            };
            _rtLeft.Create();

            _rtRight = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
            {
                name = "MocapFbtMirror.Right"
            };
            _rtRight.Create();

            ApplySurfaceBlock();
        }

        void ApplySurfaceBlock()
        {
            if (_surfaceRenderer == null)
                return;

            if (_mpb == null)
                _mpb = new MaterialPropertyBlock();
            _surfaceRenderer.GetPropertyBlock(_mpb);

            if (_rtLeft != null)
                _mpb.SetTexture("_ReflectionTex0", _rtLeft);
            if (_rtRight != null)
                _mpb.SetTexture("_ReflectionTex1", _rtRight);

            _surfaceRenderer.SetPropertyBlock(_mpb);
        }

        void ClearSurfaceBlock()
        {
            if (_surfaceRenderer != null)
                _surfaceRenderer.SetPropertyBlock(null);
        }

        void ReleaseRenderTextures()
        {
            if (_rtLeft != null)
            {
                _rtLeft.Release();
                DestroyTracked(_rtLeft);
                _rtLeft = null;
            }

            if (_rtRight != null)
            {
                _rtRight.Release();
                DestroyTracked(_rtRight);
                _rtRight = null;
            }
        }

        void EnsureReflectionCamera()
        {
            if (_reflCam != null)
                return;

            _reflCamGo = new GameObject("MocapFbtMirror.ReflectionCamera");
            _reflCamGo.hideFlags = HideFlags.HideAndDontSave;
            _reflCamGo.transform.SetParent(transform, false);

            _reflCam = _reflCamGo.AddComponent<Camera>();
            _reflCam.enabled = false; // Rendered manually each frame.
            _reflCam.cameraType = CameraType.Reflection;
            _reflCam.clearFlags = CameraClearFlags.SolidColor;
            _reflCam.backgroundColor = new Color(0.08f, 0.08f, 0.1f, 1f);
            _reflCam.useOcclusionCulling = false;
            _reflCam.allowHDR = false;
            _reflCam.allowMSAA = false;
            _reflCam.stereoTargetEye = StereoTargetEyeMask.None;
            _reflCam.nearClipPlane = 0.05f;
            _reflCam.farClipPlane = 60f;
            _reflCam.depth = -10f;
        }

        void ReleaseReflectionCamera()
        {
            if (_reflCam != null)
            {
                _reflCam.targetTexture = null;
                _reflCam = null;
            }

            if (_reflCamGo != null)
            {
                DestroyTracked(_reflCamGo);
                _reflCamGo = null;
            }
        }

        void DestroyTracked(Object target)
        {
            if (target == null)
                return;

            if (Application.isPlaying)
                Destroy(target);
            else
                DestroyImmediate(target);
        }

        #endregion
    }
}
