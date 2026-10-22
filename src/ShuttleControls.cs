using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace StbcRevival
{
    /// <summary>
    /// [Shuttle] WasdControls: fly the main-menu shuttle with the keyboard.
    /// The game ships a developer driver for this (ShuttleDriver: W/S move, A/D turn) that isn't switched on.
    /// On the shuttle scene this enables any ShuttleDriver there; if the scene has none, it adds this mod's
    /// own driver to the player's shuttle ("Player_Shuttle_and_Cameras", which the menu's flight paths move)
    /// and stops those flight paths. The background shuttles keep flying. Keys: W/S forward/back, A/D turn, Q/E down/up, hold Shift to go faster.
    /// </summary>
    internal static class ShuttleControls
    {
        private const string ShuttleScene = "Lobby_Shuttle";
        private const string PlayerShuttle = "Player_Shuttle_and_Cameras";

        internal static void Install()
        {
            if (!Plugin.ShuttleWasd.Value) return;
            SceneManager.sceneLoaded += (scene, mode) =>
            {
                if (scene.name == ShuttleScene) Plugin.Instance.StartCoroutine(SetUpNextFrame());
            };
        }

        // The shuttle's camera rig is placed during the scene's first frame; wait for it.
        private static System.Collections.IEnumerator SetUpNextFrame()
        {
            yield return null;
            try { SetUp(); }
            catch (System.Exception e) { Plugin.Log.LogWarning("Shuttle controls: " + e.Message); }
        }

        private static void SetUp()
        {
            var scene = SceneManager.GetSceneByName(ShuttleScene);
            var drivers = Resources.FindObjectsOfTypeAll<ShuttleDriver>().Where(d => d.gameObject.scene == scene).ToList();
            var animators = Resources.FindObjectsOfTypeAll<CameraPathAnimator>().Where(a => a.gameObject.scene == scene).Distinct().ToList();

            if (drivers.Count > 0)
            {
                foreach (var d in drivers)
                {
                    d.gameObject.SetActive(true);
                    d.enabled = true;
                    StopPathsMoving(animators, d.transform);
                    Plugin.Log.LogInfo($"Shuttle controls: enabled the game's ShuttleDriver on '{d.name}'");
                }
                return;
            }

            // The menu's own flight paths animate the player's shuttle; that's the thing to fly.
            var shuttle = animators.Select(a => a.animationObject).FirstOrDefault(t => t != null && t.name == PlayerShuttle);
            if (shuttle == null)
            {
                Plugin.Log.LogWarning($"Shuttle controls: '{PlayerShuttle}' not found; paths move: " +
                    string.Join(", ", animators.Select(a => a.animationObject != null ? a.animationObject.name : "-").Distinct().ToArray()));
                return;
            }
            StopPathsMoving(animators, shuttle);
            if (shuttle.GetComponent<KeyboardShuttleDriver>() == null) shuttle.gameObject.AddComponent<KeyboardShuttleDriver>();
            Plugin.Log.LogInfo($"Shuttle controls: flying '{shuttle.name}' with the mod's driver");
        }

        // A flight path that animates the shuttle would fight the keyboard; background ships keep theirs.
        private static void StopPathsMoving(System.Collections.Generic.List<CameraPathAnimator> animators, Transform shuttle)
        {
            var stopped = animators.Where(a => a.animationObject == shuttle).ToList();
            foreach (var a in stopped) a.enabled = false;
            Plugin.Log.LogInfo($"Shuttle controls: stopped {stopped.Count} flight path(s): " +
                string.Join(", ", stopped.Select(a => a.name).Distinct().ToArray()));
        }
    }

    /// <summary>
    /// Same controls as the game's ShuttleDriver (W moves along -Z, its forward), plus up/down and boost.
    /// The menu's sequence keeps putting the shuttle back on its flight paths, so this keeps its own pose
    /// and reapplies it after everything else has run (LateUpdate) and again before every camera renders.
    /// </summary>
    internal sealed class KeyboardShuttleDriver : MonoBehaviour
    {
        private Vector3 position;
        private Quaternion rotation;
        private bool reportedOverride;
        private Camera.CameraCallback beforeRender;

        private void Start()
        {
            position = transform.position;
            rotation = transform.rotation;
            beforeRender = _ => Apply();
            Camera.onPreCull += beforeRender;
        }

        private void OnDestroy()
        {
            if (beforeRender != null) Camera.onPreCull -= beforeRender;
        }

        private void Update()
        {
            if (!reportedOverride && (transform.position - position).sqrMagnitude > 0.01f)
            {
                reportedOverride = true;
                Plugin.Log.LogInfo("Shuttle controls: something else moves the shuttle each frame; overriding it");
            }
            float speed = Plugin.ShuttleSpeed.Value * (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift) ? 5f : 1f);
            float dt = Time.deltaTime;
            Vector3 move = Vector3.zero;
            if (Input.GetKey(KeyCode.W)) move.z -= 1f;
            else if (Input.GetKey(KeyCode.S)) move.z += 1f;
            if (Input.GetKey(KeyCode.E)) move.y += 1f;
            else if (Input.GetKey(KeyCode.Q)) move.y -= 1f;
            position += rotation * move * speed * dt;
            if (Input.GetKey(KeyCode.A)) rotation *= Quaternion.Euler(0f, -dt * Plugin.ShuttleTurnSpeed.Value, 0f);
            else if (Input.GetKey(KeyCode.D)) rotation *= Quaternion.Euler(0f, dt * Plugin.ShuttleTurnSpeed.Value, 0f);
            Apply();
        }

        private void LateUpdate() => Apply();

        private void Apply()
        {
            if (this == null) return;
            transform.position = position;
            transform.rotation = rotation;
        }
    }
}
