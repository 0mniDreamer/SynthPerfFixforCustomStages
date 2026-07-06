using System;
using System.Collections.Generic;
using System.Reflection;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(SynthPerfFix.Main), "SynthPerfFix", "1.4.0", "OmniDreamer")]
[assembly: MelonGame(null, null)]

namespace SynthPerfFix
{
    /// <summary>
    /// Fixes the Unity 6 (URP 17 / Render Graph) performance regression on
    /// custom SDK stages, diagnosed with SynthPerfProbe:
    ///
    /// 1. RECEIVER THROTTLE - custom stages drive strobe colors through four
    ///    "StrobeReciever" cameras rendering the ENTIRE scene (cullingMask -1)
    ///    into 2x2 render textures every frame. Under Render Graph these four
    ///    extra full camera passes raise the frame-time jitter floor and cause
    ///    recurring 15-30 ms CPU hitches (confirmed by live bisection: with
    ///    receivers off, Unity 6 matches the 2021 branch exactly). The fix
    ///    keeps them functional but renders each receiver once every N frames,
    ///    staggered so at most one receiver renders per frame.
    ///
    /// 2. SHADER WARMUP - custom stage bundles trigger on-first-use shader
    ///    variant compilation on Unity 6 (150-250 ms mid-song stalls).
    ///    Shader.WarmupAllShaders() on gameplay-scene load moves that cost
    ///    into the song-load stall where it is imperceptible. Menu scenes are
    ///    skipped so the ~800 ms first warmup never freezes the menu.
    /// </summary>
    public class Main : MelonMod
    {
        internal static MelonLogger.Instance Log;

        private MelonPreferences_Category _cfg;
        private MelonPreferences_Entry<string> _receiverMode;
        private MelonPreferences_Entry<string> _colorProperty;
        private MaterialPropertyBlock _mpb;
        private MelonPreferences_Entry<int> _throttleInterval;
        private MelonPreferences_Entry<float> _receiverFarClip;
        private MelonPreferences_Entry<bool> _shaderWarmup;
        private MelonPreferences_Entry<string> _toggleKey;
        private MelonPreferences_Entry<bool> _debugLogging;

        private class Receiver
        {
            public Camera Cam;
            public float OriginalFarClip;
            public int OriginalCullingMask = -1;
            public RenderTexture Rt;
            public Renderer Quad;
            public Color LastColor = new Color(-1f, -1f, -1f, -1f);
            public bool WarnedNoQuad;
        }

        private readonly List<Receiver> _receivers = new List<Receiver>();
        private readonly HashSet<int> _knownIds = new HashSet<int>();

        // Settle window with re-scans: custom stages instantiate into the
        // existing scene, so a single scan right after load misses them.
        private readonly float[] _scanOffsets = { 3f, 6f, 10f };
        private readonly List<float> _pendingScans = new List<float>();

        private bool _active = true;       // live toggle (hotkey)
        private bool _warmedThisScene;
        private long _frame;
        private KeyCode _kToggle = KeyCode.None;

        private static MethodInfo _warmupMethod;
        private static bool _warmupUnavailable;

        public override void OnInitializeMelon()
        {
            Log = LoggerInstance;

            _cfg = MelonPreferences.CreateCategory("SynthPerfFix");
            _receiverMode = _cfg.CreateEntry("ReceiverMode", "Emulate",
                description: "What to do with StrobeReciever cameras. 'Emulate' (default): disable the cameras and write the strobe color into their render textures directly each frame - zero camera passes, strobe visuals preserved. 'Disable': cameras off, strobe colors freeze. 'Throttle': render each receiver 1-in-N frames (some hitching remains). 'MaskTest': diagnostic, receivers run every frame culling nothing. 'Off': leave the game alone.");
            _colorProperty = _cfg.CreateEntry("StrobeColorProperty", "_Color",
                description: "Material color property the game animates on the Strober quads (SimpleColorShader uses _Color). Emulate mode reads this from the quad's MaterialPropertyBlock, falling back to its sharedMaterial.");
            _throttleInterval = _cfg.CreateEntry("ThrottleInterval", 8,
                description: "Throttle mode only: each receiver renders once every N frames, staggered.");
            _receiverFarClip = _cfg.CreateEntry("ReceiverFarClip", 0f,
                description: "If > 0, clamp receiver camera far clip plane to this many meters. 0 = leave unchanged. Only relevant in Throttle mode.");
            _shaderWarmup = _cfg.CreateEntry("ShaderWarmupEnabled", false,
                description: "Run Shader.WarmupAllShaders() on gameplay-scene loads. Default off: controlled testing showed the receiver cameras were the sole cause of the Unity 6 stutters; warmup contributed nothing and its ~1 s stall can land in-song.");
            _toggleKey = _cfg.CreateEntry("ToggleKey", "F4",
                description: "Hotkey to toggle receiver management on/off live (for A/B testing in headset). Set to None to disable.");
            _debugLogging = _cfg.CreateEntry("DebugLogging", false,
                description: "Verbose diagnostic output.");

            if (!Enum.TryParse(_toggleKey.Value, true, out _kToggle))
                _kToggle = KeyCode.None;

            Log.Msg("SynthPerfFix loaded - custom stage strobe receiver performance fix is running.");
            if (_debugLogging.Value)
                Log.Msg($"Unity {Application.unityVersion} | mode={_receiverMode.Value} | interval={_throttleInterval.Value} | warmup={_shaderWarmup.Value} | toggle={_kToggle}");
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            _receivers.Clear();
            _knownIds.Clear();
            _warmedThisScene = false;
            _pendingScans.Clear();
            float now = Time.unscaledTime;
            foreach (float offset in _scanOffsets)
                _pendingScans.Add(now + offset);
            if (_debugLogging.Value)
                Log.Msg($"Scene \"{sceneName}\" loaded; receiver scans scheduled.");
        }

        public override void OnUpdate()
        {
            _frame++;

            try
            {
                if (_pendingScans.Count > 0 && Time.unscaledTime >= _pendingScans[0])
                {
                    _pendingScans.RemoveAt(0);
                    Scan();
                }
            }
            catch (Exception ex)
            {
                if (_debugLogging.Value) Log.Warning("Scan failed: " + ex.Message);
            }

            try
            {
                if (_kToggle != KeyCode.None && Input.GetKeyDown(_kToggle))
                {
                    _active = !_active;
                    if (!_active) RestoreReceivers();
                    Log.Msg($"Receiver management {(_active ? "ON" : "OFF (receivers restored)")}");
                }
            }
            catch { }

            if (!_active) return;

            try { ApplyThrottle(); }
            catch (Exception ex)
            {
                if (_debugLogging.Value) Log.Warning("Throttle failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Finds strobe receiver cameras: render-texture targets at tiny
        /// resolutions (the strobe sampling pattern). Also detects gameplay
        /// scenes and triggers the one-time shader warmup for this scene.
        /// </summary>
        private void Scan()
        {
            int found = 0;
            var cams = UnityEngine.Object.FindObjectsOfType(
                Il2CppInterop.Runtime.Il2CppType.Of<Camera>());
            if (cams != null)
            {
                for (int i = 0; i < cams.Length; i++)
                {
                    var cam = cams[i] == null ? null : cams[i].TryCast<Camera>();
                    if (cam == null) continue;

                    RenderTexture rt = null;
                    try { rt = cam.targetTexture; } catch { }
                    if (rt == null) continue;
                    bool tiny = false;
                    try { tiny = rt.width <= 8 && rt.height <= 8; } catch { }
                    bool named = false;
                    try { named = cam.name.Contains("StrobeReciever") || cam.name.Contains("StrobeReceiver"); } catch { }
                    if (!tiny && !named) continue;

                    int id;
                    try { id = cam.GetInstanceID(); } catch { continue; }
                    if (!_knownIds.Add(id)) continue;

                    var rec = new Receiver { Cam = cam, OriginalFarClip = 1000f, Rt = rt };
                    try { rec.OriginalFarClip = cam.farClipPlane; } catch { }
                    try { rec.OriginalCullingMask = cam.cullingMask; } catch { }
                    // The Strober quad the game animates is the receiver's child.
                    try { rec.Quad = cam.GetComponentInChildren<Renderer>(); } catch { }
                    if (_receiverFarClip.Value > 0f)
                    {
                        try { cam.farClipPlane = _receiverFarClip.Value; } catch { }
                    }
                    _receivers.Add(rec);
                    found++;
                }
            }

            if (found > 0)
                if (_debugLogging.Value)
                    Log.Msg($"Tracking {_receivers.Count} strobe receiver camera(s). Mode: {_receiverMode.Value}.");

            // Gameplay detection for warmup: receivers present, or the game's
            // note pool container exists (covers built-in stages too).
            if (_shaderWarmup.Value && !_warmedThisScene)
            {
                bool gameplay = _receivers.Count > 0;
                if (!gameplay)
                {
                    try { gameplay = GameObject.Find("Rail Manager(Clone)") != null; } catch { }
                }
                if (gameplay)
                {
                    _warmedThisScene = true;
                    WarmupShaders();
                }
            }
        }

        /// <summary>
        /// Disable mode: receivers off entirely (the verified 2021-match config).
        /// Throttle mode: staggered 1-in-N rendering - reduces but does not
        /// eliminate hitching, since each pass still carries Render Graph
        /// per-pass overhead on Unity 6.
        /// MaskTest mode: receivers enabled EVERY frame with cullingMask=0 -
        /// passes execute but cull/draw nothing. The decisive template
        /// experiment: flat frame times mean the cost was full-scene
        /// cull/submit (fix = layer-masked receivers in the stage template);
        /// continued hitching means Render Graph per-pass overhead itself is
        /// the cost (fix = camera-free color transport). Strobe colors freeze
        /// while testing.
        /// </summary>
        private void ApplyThrottle()
        {
            if (_receivers.Count == 0) return;

            string mode = (_receiverMode.Value ?? "Emulate").Trim().ToLowerInvariant();
            if (mode == "off") return;

            if (mode == "emulate")
            {
                for (int i = 0; i < _receivers.Count; i++)
                    EmulateReceiver(_receivers[i]);
                return;
            }

            if (mode == "masktest")
            {
                for (int i = 0; i < _receivers.Count; i++)
                {
                    var cam = _receivers[i].Cam;
                    if (cam == null) continue;
                    try
                    {
                        cam.enabled = true;
                        if (cam.cullingMask != 0) cam.cullingMask = 0;
                    }
                    catch { }
                }
                return;
            }

            bool disableAll = mode != "throttle"; // Disable (and any unknown value) => off
            int interval = Math.Max(2, _throttleInterval.Value);

            for (int i = 0; i < _receivers.Count; i++)
            {
                var cam = _receivers[i].Cam;
                if (cam == null) continue;
                try
                {
                    cam.enabled = !disableAll && ((_frame + i) % interval == 0);
                }
                catch { }
            }
        }

        /// <summary>
        /// Camera-free strobe: the receiver camera only ever photographed its
        /// child Strober quad (a solid color the game animates). MaskTest
        /// proved Render Graph per-pass overhead causes hitching regardless of
        /// pass content, so instead of rendering, read the quad's color and
        /// clear the 2x2 render texture to it directly. Stage shaders sample
        /// the RT exactly as before; zero camera passes remain.
        /// </summary>
        private void EmulateReceiver(Receiver rec)
        {
            var cam = rec.Cam;
            if (cam == null || rec.Rt == null) return;
            try
            {
                if (cam.enabled) cam.enabled = false;

                if (rec.Quad == null)
                {
                    if (!rec.WarnedNoQuad)
                    {
                        rec.WarnedNoQuad = true;
                        Log.Warning($"Emulate: no Strober quad under {cam.name}; its strobe channel will freeze (camera stays disabled).");
                    }
                    return;
                }

                Color c = ReadStrobeColor(rec.Quad);
                if (c != rec.LastColor)
                {
                    rec.LastColor = c;
                    FillRt(rec.Rt, c);
                }
            }
            catch (Exception ex)
            {
                if (_debugLogging.Value) Log.Warning("Emulate failed: " + ex.Message);
            }
        }

        private Color ReadStrobeColor(Renderer quad)
        {
            string prop = _colorProperty.Value;
            if (string.IsNullOrEmpty(prop)) prop = "_Color";

            // Per-renderer MaterialPropertyBlock first: the four quads share one
            // material asset, so per-channel colors can only arrive via MPBs.
            if (_mpb == null) _mpb = new MaterialPropertyBlock();
            try
            {
                quad.GetPropertyBlock(_mpb);
                if (!_mpb.isEmpty)
                {
                    Color c = _mpb.GetColor(prop);
                    if (c != default(Color)) return c;
                }
            }
            catch { }

            try
            {
                var mat = quad.sharedMaterial;
                if (mat != null && mat.HasProperty(prop))
                    return mat.GetColor(prop);
            }
            catch { }

            return Color.black;
        }

        private static void FillRt(RenderTexture rt, Color c)
        {
            var prev = RenderTexture.active;
            try
            {
                RenderTexture.active = rt;
                GL.Clear(false, true, c);
            }
            finally
            {
                RenderTexture.active = prev;
            }
        }

        private void RestoreReceivers()
        {
            foreach (var rec in _receivers)
            {
                if (rec.Cam == null) continue;
                try
                {
                    rec.Cam.enabled = true;
                    if (rec.OriginalCullingMask != -1 || rec.Cam.cullingMask == 0)
                        rec.Cam.cullingMask = rec.OriginalCullingMask;
                    if (_receiverFarClip.Value > 0f)
                        rec.Cam.farClipPlane = rec.OriginalFarClip;
                }
                catch { }
            }
        }

        private void WarmupShaders()
        {
            if (_warmupUnavailable) return;
            try
            {
                if (_warmupMethod == null)
                {
                    _warmupMethod = typeof(Shader).GetMethod("WarmupAllShaders",
                        BindingFlags.Public | BindingFlags.Static);
                    if (_warmupMethod == null)
                    {
                        _warmupUnavailable = true;
                        Log.Warning("Shader.WarmupAllShaders not found; warmup disabled.");
                        return;
                    }
                }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                _warmupMethod.Invoke(null, null);
                sw.Stop();
                if (_debugLogging.Value)
                    Log.Msg($"Shader warmup completed in {sw.ElapsedMilliseconds} ms.");
            }
            catch (Exception ex)
            {
                _warmupUnavailable = true;
                Log.Warning("Shader warmup disabled after error: " + ex.Message);
            }
        }

        public override void OnApplicationQuit()
        {
            try { RestoreReceivers(); } catch { }
        }
    }
}
