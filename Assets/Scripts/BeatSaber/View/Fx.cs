using System;
using System.Collections.Generic;
using UnityEngine;
// `System` is only needed for Func<>; `Object` must remain explicitly UnityEngine.Object
// (Unity API calls like Object.Instantiate/Object.DontDestroyOnLoad).
using Object = UnityEngine.Object;

namespace QuestSaber
{
    /// <summary>
    /// Cached, pooled particle effects: cut sparks (direction-true), mine
    /// explosions, note spawn particles (prefabs) and wall contact sparks.
    /// </summary>
    public static class Fx
    {
        private sealed class Pool
        {
            /// <summary>Creates the template for code-built systems (null = load prefab).</summary>
            public Func<GameObject> Factory;
            /// <summary>Template (loaded prefab OR built by the factory).</summary>
            public GameObject Prefab;
            public readonly Stack<GameObject> Free = new();
        }

        private sealed class Active
        {
            public GameObject Go;
            public Pool Pool;
            public float ReleaseAt;
        }

        private static readonly Dictionary<string, Pool> _pools = new();
        private static readonly List<Active> _active = new();
        private static readonly Stack<Active> _activeSpare = new();
        private static FxRunner _runner;
        private static MaterialPropertyBlock _colorBlock;

        // Pool keys of the code-built systems.
        private const string SparkRedKey = "BuiltSparksRed";
        private const string SparkBlueKey = "BuiltSparksBlue";
        private const float MineLifetimeMin = 0.22f, MineLifetimeMax = 0.32f; // s (short & sharp)
        private const float MineSizeMin = 0.55f, MineSizeMax = 0.68f;         // m start diameter
        private const float MineGrow = 1.4f;                                 // end factor (only larger)
        private const float MineSpinSpeed = 1.4f;                            // rad/s self-rotation
        private const int MineSeedRange = 60;                                // random seed per explosion
        private const int MinePoolMax = 12;                                  // pool upper limit of the 3D balls

        /// <summary>Sparks at the intersection point - DIRECTION = swing velocity of the
        /// blade tip PLUS a forward component (`roomForward` - the burst always flies
        /// a bit into the room, not purely sideways). Stretched, THIN streaks,
        /// barely any gravity. No swing -> direction toward the player as fallback.</summary>
        public static void PlayHit(Vector3 worldPosition, SaberSide side,
            Vector3 swingVelocity, Vector3 roomForward, float scale = 1f)
        {
            bool red = side == SaberSide.Left;
            Vector3 dir = swingVelocity.sqrMagnitude > 1e-6f ? swingVelocity.normalized : Vector3.back;
            // Forward component (room-forward) - "always a bit more forward than straight".
            dir += roomForward.normalized * GameConfig.SparkForwardBias;
            if (dir.sqrMagnitude < 1e-6f) dir = Vector3.back;
            Quaternion rot = Quaternion.LookRotation(dir.normalized, Vector3.up);
            PlayBuilt(
                red ? SparkRedKey : SparkBlueKey,
                worldPosition, rot, Mathf.Max(0.01f, scale),
                red ? GameConfig.SparkColorRed : GameConfig.SparkColorBlue,
                CreateSparkTemplate,
                GameConfig.SparkLifetimeMax + 0.5f);
        }

        private sealed class ContactEmitter
        {
            public GameObject Go;
            public ParticleSystem Ps;
            public bool Active;
        }

        private static readonly Dictionary<int, ContactEmitter> _contactSparks = new();

        private const int ContactChannelWallLeft = 0;
        private const int ContactChannelWallRight = 1;
        private const int ContactChannelClash = 2;

        /// <summary>Wall sparks (persistent): ONE system per hand, follows the entry point
        /// of the blade into the wall as long as the contact exists. `active=false` only stops
        /// the emission - the sparks already emitted keep falling out.</summary>
        public static void SetWallContact(SaberSide side, bool active, Vector3 worldPosition, float scale)
        {
            int channel = side == SaberSide.Left ? ContactChannelWallLeft : ContactChannelWallRight;
            var color = side == SaberSide.Left ? GameConfig.SparkColorRed : GameConfig.SparkColorBlue;
            var em = EnsureContact(channel, color);
            UpdateContact(em, active, worldPosition, scale);
        }

        /// <summary>Sword clash (persistent): ONE white fountain at the contact point
        /// of both blades as long as the swords rest on each other.</summary>
        public static void SetClashContact(bool active, Vector3 worldPosition, float scale)
        {
            var em = EnsureContact(ContactChannelClash, GameConfig.ClashSparkColor);
            UpdateContact(em, active, worldPosition, scale);
        }

        /// <summary>Stops all contact sparks immediately (song/menu change): leftovers fall out.</summary>
        public static void StopAllContacts()
        {
            foreach (var kv in _contactSparks)
            {
                if (kv.Value == null || kv.Value.Ps == null) continue;
                kv.Value.Active = false;
                kv.Value.Ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            }
        }

        private static ContactEmitter EnsureContact(int channel, Color color)
        {
            if (_contactSparks.TryGetValue(channel, out var existing)) return existing;

            var em = new ContactEmitter();
            em.Go = CreateContactFountain(channel, color);
            em.Ps = em.Go.GetComponent<ParticleSystem>();
            Object.DontDestroyOnLoad(em.Go);
            em.Go.SetActive(false);
            em.Ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            _contactSparks[channel] = em;
            return em;
        }

        private static GameObject CreateContactFountain(int channel, Color color)
        {
            var go = new GameObject("ContactSparks[" + channel + "]");
            var ps = go.AddComponent<ParticleSystem>();

            var main = ps.main;
            main.loop = true;
            main.playOnAwake = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(
                GameConfig.ContactSparkLifetimeMin, GameConfig.ContactSparkLifetimeMax);
            main.startSpeed = new ParticleSystem.MinMaxCurve(
                GameConfig.ContactSparkSpeedMin, GameConfig.ContactSparkSpeedMax);
            main.startSize = new ParticleSystem.MinMaxCurve(
                GameConfig.ContactSparkSizeMin, GameConfig.ContactSparkSizeMax);
            main.startColor = Color.white; // tint comes via _Color (MPB)
            main.gravityModifier = GameConfig.ContactSparkGravity; // first spray radially, then pull down
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            main.maxParticles = 400;

            var emission = ps.emission;
            emission.rateOverTime = GameConfig.WallSparkRate; // continuous emission instead of burst
            emission.SetBursts(new ParticleSystem.Burst[0]);

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Sphere; // all directions, not only up
            shape.radius = 0.03f;
            shape.radiusThickness = 1f;

            var col = ps.colorOverLifetime;
            col.enabled = true;
            col.color = FadeGradient();

            var siz = ps.sizeOverLifetime;
            siz.enabled = true;
            siz.size = new ParticleSystem.MinMaxCurve(1f, ShrinkCurve(0.35f));

            ConfigureSparkRenderer(go, 1.0f, 0.05f);

            // HDR color per channel (additive spark shader, MPB) - permanent for this emitter.
            var block = new MaterialPropertyBlock();
            block.SetColor("_Color", color);
            var pr = go.GetComponent<ParticleSystemRenderer>();
            if (pr != null) pr.SetPropertyBlock(block);
            return go;
        }

        private static void UpdateContact(ContactEmitter em, bool active,
            Vector3 worldPosition, float scale)
        {
            if (em == null || em.Ps == null) return;

            if (active)
            {
                if (!em.Active)
                {
                    em.Go.SetActive(true);
                    em.Ps.Play(true);
                }
                em.Go.transform.position = worldPosition;
                em.Go.transform.localScale = Vector3.one * Mathf.Max(0.01f, scale);
                em.Active = true;
            }
            else if (em.Active)
            {
                em.Active = false;
                // Only stop the emission - already emitted sparks keep falling out.
                em.Ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            }
        }

        public static void PlayMineExplosion(Vector3 worldPosition, float scale = 1f)
        {
            var mat = MineFireballMaterial();
            if (mat == null) return; // shader missing: effect is omitted (no error)

            GameObject go = _mineFree.Count > 0 ? _mineFree.Pop() : CreateMineFireball();
            var t = go.transform;
            t.SetPositionAndRotation(worldPosition, Quaternion.identity);
            go.SetActive(true);

            float baseScale = Mathf.Max(0.01f, scale);
            go.GetComponent<MineFireballFx>().Init(
                UnityEngine.Random.Range(MineSizeMin, MineSizeMax) * baseScale,
                MineGrow,
                UnityEngine.Random.Range(MineLifetimeMin, MineLifetimeMax),
                UnityEngine.Random.Range(0, MineSeedRange),
                UnityEngine.Random.Range(-MineSpinSpeed, MineSpinSpeed),
                GameConfig.MineFlashColor);
        }

        /// <summary>Spawn particles of a note at its appearance position (red = left).
        /// Reference prefabs (ParticleSpawnL/R), rotation -90° X - deliberately UNCHANGED.</summary>
        public static void PlayNoteSpawn(Vector3 worldPosition, bool red, float scale = 1f)
        {
            PlayPrefab(
                red ? GameConfig.NoteSpawnParticleRedResource
                    : GameConfig.NoteSpawnParticleBlueResource,
                worldPosition, scale);
        }

        // ==================================================================
        // Code-built one-shot bursts (sparks / mine explosion)
        // ==================================================================

        private static void PlayBuilt(string key, Vector3 position, Quaternion rotation,
            float scale, Color color, Func<GameObject> factory, float holdSeconds)
        {
            EnsureRunner();
            Pool pool = PoolFor(key, factory);
            GameObject go = pool.Free.Count > 0 ? pool.Free.Pop() : Object.Instantiate(pool.Prefab);
            var t = go.transform;
            t.SetPositionAndRotation(position, rotation);
            t.localScale = Vector3.one * Mathf.Max(0.01f, scale);
            go.SetActive(true);

            // HDR color per burst (additive spark shader, MPB per instance) - the template
            // has exactly ONE renderer (ParticleSystemRenderer at the root).
            if (_colorBlock == null) _colorBlock = new MaterialPropertyBlock();
            _colorBlock.SetColor("_Color", color);
            var pr = go.GetComponent<ParticleSystemRenderer>();
            if (pr != null) pr.SetPropertyBlock(_colorBlock);

            var ps = go.GetComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ps.Play(true);

            var entry = _activeSpare.Count > 0 ? _activeSpare.Pop() : new Active();
            entry.Go = go;
            entry.Pool = pool;
            entry.ReleaseAt = Time.time + holdSeconds + 0.1f;
            _active.Add(entry);
        }

        /// <summary>Spark template: ONE burst along +Z (swing direction), stretched
        /// billboard streaks, almost weightless, HDR color via the spark shader.</summary>
        private static GameObject CreateSparkTemplate()
        {
            var go = new GameObject("CutSparks");
            var ps = go.AddComponent<ParticleSystem>();

            var main = ps.main;
            main.duration = GameConfig.SparkLifetimeMax;
            main.loop = false;
            main.playOnAwake = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(
                GameConfig.SparkLifetimeMin, GameConfig.SparkLifetimeMax);
            main.startSpeed = new ParticleSystem.MinMaxCurve(
                GameConfig.SparkSpeedMin, GameConfig.SparkSpeedMax);
            main.startSize = new ParticleSystem.MinMaxCurve(
                GameConfig.SparkSizeMin, GameConfig.SparkSizeMax);
            main.startColor = Color.white; // tint comes per burst via _Color (MPB)
            main.gravityModifier = GameConfig.SparkGravity;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            main.maxParticles = GameConfig.SparkCount * 2;

            var emission = ps.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)GameConfig.SparkCount) });

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = GameConfig.SparkSpreadAngle;
            shape.radius = 0.008f;
            shape.radiusThickness = 1f;

            var col = ps.colorOverLifetime;
            col.enabled = true;
            col.color = FadeGradient();

            var siz = ps.sizeOverLifetime;
            siz.enabled = true;
            siz.size = new ParticleSystem.MinMaxCurve(1f, ShrinkCurve(0.35f));

            ConfigureSparkRenderer(go, 2.5f, 0.12f);
            return go;
        }

        private static GameObject CreateMineFireball()
        {
            var go = new GameObject("MineFireball");
            Object.DontDestroyOnLoad(go);
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = MineSphereMesh();
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = MineFireballMaterial();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.allowOcclusionWhenDynamic = false;
            go.AddComponent<MineFireballFx>().Bind(mr);
            go.SetActive(false);
            return go;
        }

        /// <summary>Procedural UV sphere (radius 0.5) - the volume of the 3D explosion. The shader
        /// is unlit, so only vertices/triangles are needed (no normals/UVs). ONE mesh, cached.</summary>
        private static Mesh _mineSphere;

        private static Mesh MineSphereMesh()
        {
            if (_mineSphere != null) return _mineSphere;
            const int slices = 24, stacks = 16;
            var verts = new Vector3[(stacks + 1) * (slices + 1)];
            var tris = new int[stacks * slices * 6];
            int vi = 0;
            for (int st = 0; st <= stacks; st++)
            {
                float phi = (float)st / stacks * Mathf.PI;   // 0 ... pi (pole to pole)
                float y = Mathf.Cos(phi) * 0.5f;
                float r = Mathf.Sin(phi) * 0.5f;
                for (int sl = 0; sl <= slices; sl++)
                {
                    float th = (float)sl / slices * Mathf.PI * 2f;
                    verts[vi++] = new Vector3(Mathf.Cos(th) * r, y, Mathf.Sin(th) * r);
                }
            }
            int ti = 0;
            for (int st = 0; st < stacks; st++)
            {
                for (int sl = 0; sl < slices; sl++)
                {
                    int a = st * (slices + 1) + sl;
                    int b = a + slices + 1;
                    tris[ti++] = a; tris[ti++] = b; tris[ti++] = a + 1;
                    tris[ti++] = a + 1; tris[ti++] = b; tris[ti++] = b + 1;
                }
            }
            var mesh = new Mesh { name = "FxMineSphere" };
            mesh.vertices = verts;
            mesh.triangles = tris;
            mesh.RecalculateBounds();
            _mineSphere = mesh;
            return mesh;
        }

        // Pooled 3D fireballs (NO ParticleSystem): the MonoBehaviour manages lifetime,
        // growth and fading itself and returns the object here.
        private static readonly Stack<GameObject> _mineFree = new();

        private static void ReleaseMine(GameObject go)
        {
            go.SetActive(false);
            if (_mineFree.Count < MinePoolMax) _mineFree.Push(go);
            else Object.Destroy(go);
        }

        /// <summary>Animates ONE 3D gas explosion: grows monotonically (NEVER gets smaller), fades
        /// out softly at the end and rotates slowly (gaseous). Seed + random parameters make each explosion
        /// somewhat different. HDR tint + _Fade/_Seed are set per instance via MPB.</summary>
        public sealed class MineFireballFx : MonoBehaviour
        {
            private MeshRenderer _mr;
            private MaterialPropertyBlock _mpb;
            private float _start, _duration, _size0, _grow, _spin;

            public void Bind(MeshRenderer mr)
            {
                _mr = mr;
                _mpb = new MaterialPropertyBlock();
            }

            public void Init(float size, float grow, float duration, int seed, float spin, Color color)
            {
                _size0 = Mathf.Max(0.01f, size);
                _grow = Mathf.Max(1f, grow);
                _duration = Mathf.Max(0.05f, duration);
                _spin = spin;
                _start = Time.time;

                transform.localRotation = Quaternion.Euler(0f, 0f, seed * 37f + UnityEngine.Random.Range(0f, 360f));
                transform.localScale = Vector3.one * _size0;

                _mpb.SetColor("_Color", color);
                _mpb.SetFloat("_Seed", seed);
                _mpb.SetFloat("_Fade", 1f);
                if (_mr != null) _mr.SetPropertyBlock(_mpb);
            }

            private void Update()
            {
                if (_mr == null) return;
                float t = (Time.time - _start) / _duration;
                if (t >= 1f)
                {
                    ReleaseMine(gameObject);
                    return;
                }

                // Growth: very fast at the beginning (sharp bang), monotonically increasing
                // (0 -> 1) -> NO shrinking.
                float u = 1f - t;
                float e = 1f - u * u * u;
                transform.localScale = Vector3.one * (_size0 * Mathf.Lerp(1f, _grow, e));
                transform.Rotate(0f, 0f, _spin * Mathf.Rad2Deg * Time.deltaTime, Space.Self);

                // Briefly full, then fade quickly (shorter/sharper).
                float fade = 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((t - 0.1f) / 0.8f));
                _mpb.SetFloat("_Fade", fade);
                _mr.SetPropertyBlock(_mpb);
            }
        }

        private static Material _mineFireballMat;

        /// <summary>Shared material of the 3D explosion (additive volume shader
        /// `BeatSaber/MineFireball`; HDR tint + _Fade/_Seed per instance via MPB). If the
        /// shader is missing, the effect is cleanly omitted (no error).</summary>
        private static Material MineFireballMaterial()
        {
            if (_mineFireballMat != null) return _mineFireballMat;
            var shader = Resources.Load<Shader>("QuestSaber/MineFireball");
            if (shader == null) shader = Shader.Find("QuestSaber/MineFireball");
            if (shader == null) return null;
            _mineFireballMat = new Material(shader);
            return _mineFireballMat;
        }

        /// <summary>Renderer of the one-shot bursts: stretched billboards along the
        /// particle velocity (= motion streaks, no fountain toss).</summary>
        private static void ConfigureSparkRenderer(GameObject go, float lengthScale, float velocityScale)
        {
            var render = go.GetComponent<ParticleSystemRenderer>();
            render.renderMode = ParticleSystemRenderMode.Stretch;
            render.lengthScale = lengthScale;
            render.velocityScale = velocityScale;
            render.maxParticleSize = 0.5f;
            render.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            render.receiveShadows = false;
            var shader = Resources.Load<Shader>("QuestSaber/Spark");
            render.sharedMaterial = new Material(shader != null ? shader : Shader.Find("Sprites/Default"));
        }

        /// <summary>Alpha fade 1 -> 0 over the lifetime (color stays white - the tint
        /// comes from the shader's HDR _Color).</summary>
        private static Gradient FadeGradient()
        {
            var g = new Gradient();
            g.SetKeys(
                new[]
                {
                    new GradientColorKey(Color.white, 0f),
                    new GradientColorKey(Color.white, 1f)
                },
                new[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(0.85f, 0.5f),
                    new GradientAlphaKey(0f, 1f)
                });
            return g;
        }

        private static AnimationCurve ShrinkCurve(float endScale)
        {
            // 1 -> endScale over the lifetime (streaks shrink instead of growing).
            return new AnimationCurve(
                new Keyframe(0f, 1f),
                new Keyframe(1f, endScale));
        }

        // ==================================================================
        // Reference prefab path (spawn particles)
        // ==================================================================

        private static void PlayPrefab(string resourcePath, Vector3 worldPosition, float scale)
        {
            Pool pool = PoolFor(resourcePath, null);
            if (pool.Prefab == null) return; // assets missing: effect is omitted (no error)

            EnsureRunner();

            GameObject go = pool.Free.Count > 0 ? pool.Free.Pop() : Object.Instantiate(pool.Prefab);
            var t = go.transform;
            t.SetPositionAndRotation(worldPosition, GameConfig.ParticleRotation);
            t.localScale = Vector3.one * Mathf.Max(0.01f, scale);
            go.SetActive(true);

            // Prefabs are looping systems: first stop+clear (resets the simulation time
            // so that a burst at t=0 is triggered again), then play.
            var ps = go.GetComponent<ParticleSystem>();
            float hold;
            if (ps != null)
            {
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                ps.Play(true);
                hold = ps.main.duration + ps.main.startLifetime.constantMax;
            }
            else
            {
                hold = 1f; // safety net in case the prefab has no ParticleSystem at the root
            }

            var entry = _activeSpare.Count > 0 ? _activeSpare.Pop() : new Active();
            entry.Go = go;
            entry.Pool = pool;
            entry.ReleaseAt = Time.time + hold + 0.1f;
            _active.Add(entry);
        }

        // ==================================================================
        // Pools/return
        // ==================================================================

        private static Pool PoolFor(string key, Func<GameObject> factory)
        {
            if (_pools.TryGetValue(key, out var pool))
            {
                // Pull the template (code-built): create on first Play.
                if (pool.Prefab == null && pool.Factory != null)
                {
                    pool.Prefab = pool.Factory();
                    Object.DontDestroyOnLoad(pool.Prefab);
                    pool.Prefab.SetActive(false);
                }
                return pool;
            }
            pool = new Pool { Factory = factory };
            _pools.Add(key, pool);
            if (pool.Factory != null)
            {
                pool.Prefab = pool.Factory();
                Object.DontDestroyOnLoad(pool.Prefab);
                pool.Prefab.SetActive(false);
            }
            else
            {
                pool.Prefab = Resources.Load<GameObject>(key);
            }
            return pool;
        }

        private static void EnsureRunner()
        {
            if (_runner != null) return;
            var go = new GameObject("[FxRunner]");
            Object.DontDestroyOnLoad(go);
            _runner = go.AddComponent<FxRunner>();
        }

        /// <summary>Returns due effects back to their pool (runs in the FxRunner Update).</summary>
        private static void ReleaseDue()
        {
            float now = Time.time;
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                var entry = _active[i];
                if (entry.ReleaseAt > now) continue;

                entry.Go.SetActive(false);
                _active.RemoveAt(i);

                // Limit the pool (as in the reference: at most 8 per effect)
                if (entry.Pool.Free.Count < GameConfig.ParticlePoolMaxSize)
                    entry.Pool.Free.Push(entry.Go);
                else
                    Object.Destroy(entry.Go);

                entry.Go = null;
                entry.Pool = null;
                _activeSpare.Push(entry);
            }
        }

        private sealed class FxRunner : MonoBehaviour
        {
            private void Update() => ReleaseDue();
        }
    }
}
