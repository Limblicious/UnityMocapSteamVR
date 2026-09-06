using System.Collections.Generic;
using UnityEngine;

namespace MocapTools
{
    /// <summary>
    /// World-space feedback for the VRChat-style calibration flow:
    ///
    ///   - Blue tracker spheres at each tracked point (documented VRChat behavior).
    ///   - Blue adjustment spheres at the adjustable regions (elbows, hip, feet)
    ///     during Adjust FBT mode.
    ///
    /// The mirror is provided by a separate static MocapFbtPlanarMirror scene
    /// object (MocapFbtMirrorSystem prefab); this class no longer spawns one.
    /// All visual objects live on the Ignore Raycast layer (2), which the
    /// mirror's reflection camera excludes.
    /// </summary>
    public class MocapFbtUi : MonoBehaviour
    {
        [Header("Spheres")]
        public float trackerSphereRadius = 0.03f;
        public float adjustSphereRadius = 0.02f;

        const int UiLayer = 2; // Ignore Raycast

        Material _blueMat;

        readonly Dictionary<MocapFbtRegion, GameObject> _trackerSpheres = new Dictionary<MocapFbtRegion, GameObject>();
        readonly Dictionary<MocapFbtRegion, GameObject> _adjustSpheres = new Dictionary<MocapFbtRegion, GameObject>();

        void Awake()
        {
            _blueMat = CreateColorMaterial(new Color(0.2f, 0.5f, 1f, 1f));
        }

        #region Public API

        /// <summary>
        /// Shows the calibration visuals: one blue tracker sphere per valid binding.
        /// </summary>
        public void ShowCalibration(List<MocapFbtBinding> bindings)
        {
            ClearTrackerSpheres();

            foreach (var binding in bindings)
            {
                if (!binding.isValid) continue;

                _trackerSpheres[binding.region] =
                    CreateSphere($"TrackerSphere_{binding.region}", trackerSphereRadius, _blueMat, transform);
            }
        }

        /// <summary>
        /// Hides tracker spheres after calibration completes.
        /// </summary>
        public void EndCalibration()
        {
            ClearTrackerSpheres();
        }

        /// <summary>
        /// Hides all calibration visuals (cancel path).
        /// </summary>
        public void HideCalibration()
        {
            ClearTrackerSpheres();
            HideAdjustSpheres();
        }

        /// <summary>
        /// Positions tracker spheres from their live tracker poses.
        /// </summary>
        public void UpdateCalibrationVisuals(List<MocapFbtBinding> bindings)
        {
            foreach (var binding in bindings)
            {
                if (!binding.isValid) continue;

                if (_trackerSpheres.TryGetValue(binding.region, out var sphere) &&
                    binding.trackerTransform != null)
                {
                    sphere.SetActive(true);
                    sphere.transform.position = binding.trackerTransform.position;
                }
            }
        }

        /// <summary>
        /// Creates adjustment spheres for the adjustable regions (elbows, hip, feet).
        /// </summary>
        public void ShowAdjustSpheres(List<MocapFbtBinding> bindings)
        {
            HideAdjustSpheres();

            foreach (var binding in bindings)
            {
                if (!binding.isValid || !MocapFbtRegionUtil.IsAdjustable(binding.region)) continue;
                _adjustSpheres[binding.region] = CreateSphere(
                    $"AdjustSphere_{binding.region}", adjustSphereRadius, _blueMat, transform);
            }
        }

        public void HideAdjustSpheres()
        {
            foreach (var sphere in _adjustSpheres.Values)
            {
                if (sphere != null) Destroy(sphere);
            }
            _adjustSpheres.Clear();
        }

        /// <summary>
        /// Positions adjustment spheres at their calibrated target points.
        /// </summary>
        public void UpdateAdjustVisuals(List<MocapFbtBinding> bindings)
        {
            foreach (var binding in bindings)
            {
                if (_adjustSpheres.TryGetValue(binding.region, out var sphere) &&
                    binding.offsetTransform != null)
                {
                    sphere.SetActive(true);
                    sphere.transform.position = binding.offsetTransform.position;
                }
            }
        }

        /// <summary>
        /// Destroys every visual object created by this component.
        /// </summary>
        public void TearDown()
        {
            ClearTrackerSpheres();
            HideAdjustSpheres();
        }

        #endregion

        #region Visual Construction

        void ClearTrackerSpheres()
        {
            foreach (var sphere in _trackerSpheres.Values)
            {
                if (sphere != null) Destroy(sphere);
            }
            _trackerSpheres.Clear();
        }

        GameObject CreateSphere(string name, float radius, Material material, Transform parent)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = name;
            go.layer = UiLayer;
            go.transform.SetParent(parent, false);
            go.transform.localScale = Vector3.one * (radius * 2f);

            var collider = go.GetComponent<Collider>();
            if (collider != null) Destroy(collider);

            var renderer = go.GetComponent<MeshRenderer>();
            if (material != null)
                renderer.sharedMaterial = material;
            return go;
        }

        static Material CreateColorMaterial(Color color)
        {
            foreach (var name in new[] { "Unlit/Color", "Sprites/Default" })
            {
                var shader = Shader.Find(name);
                if (shader == null) continue;
                return new Material(shader) { color = color };
            }

            Debug.LogWarning("[MocapFbtUi] No compatible color shader found; spheres disabled.");
            return null;
        }

        #endregion

        void OnDestroy()
        {
            TearDown();
        }
    }
}
