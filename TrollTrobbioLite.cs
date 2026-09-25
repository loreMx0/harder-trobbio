using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TrollTrobbioLite
{
    // Reduced Android port of Troll_Trobbio. Keeps the parts that need nothing beyond the game itself:
    //   - Trobbio boss HP raised to 1400 (in Library_13)
    //   - faster tornado, extra tornado projectiles, and the TornadoTurn behaviour
    // Dropped: the custom bombs. They are built from an object loaded out of another scene
    // ("hang_04_boss") through the Silksong.AssetHelper library, which isn't in your libs folder.
    // Change the GUID/name below to your own.
    [BepInPlugin("com.yourname.trolltrobbiolite", "Troll Trobbio Lite", "1.0.0")]
    public class TrobbioLitePlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        private static bool trobbioPatched;
        private static bool awakeLogged;
        private static bool startLogged;

        private void Awake()
        {
            Log = Logger;
            try
            {
                SceneManager.sceneLoaded += OnSceneLoaded;

                var harmony = new Harmony();                 // parameterless
                Hook(harmony, "Awake", nameof(AwakePostfix));
                Hook(harmony, "Start", nameof(StartPostfix));

                Log.LogInfo("Troll Trobbio Lite loaded");
            }
            catch (Exception ex)
            {
                Log.LogError("Awake failed: " + ex);
            }
        }

        private static void Hook(Harmony harmony, string targetName, string postfixName)
        {
            var original = AccessTools.Method(typeof(PlayMakerFSM), targetName);
            if (original == null)
            {
                Log.LogError("PlayMakerFSM." + targetName + " not found");
                return;
            }

            var postfix = AccessTools.Method(typeof(TrobbioLitePlugin), postfixName);
            harmony.Patch(original, prefix: null, postfix: postfix);   // raw MethodInfo, no HarmonyMethod
            Log.LogInfo("Patched PlayMakerFSM." + targetName);
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            trobbioPatched = false;
        }

        // Boss HP. Runs after every PlayMakerFSM.Awake, so bail out as cheaply as possible.
        private static void AwakePostfix(PlayMakerFSM __instance)
        {
            try
            {
                if (!awakeLogged)
                {
                    awakeLogged = true;
                    Log.LogInfo("Awake hook firing (__instance " + (__instance == null ? "is NULL" : "ok") + ")");
                }

                if (__instance == null || trobbioPatched) return;

                GameObject go = __instance.gameObject;
                if (go == null || go.name != "Trobbio") return;
                if (SceneManager.GetActiveScene().name != "Library_13") return;

                HealthManager hm = go.GetComponent<HealthManager>();
                if (hm == null) return;

                hm.hp = 1400;
                trobbioPatched = true;
                Log.LogInfo("Trobbio HP set to 1400");
            }
            catch (Exception ex)
            {
                Log.LogError("AwakePostfix failed: " + ex);
            }
        }

        // Tornado attack changes. Applied to any FSM on the Trobbio object that has the tornado states.
        private static void StartPostfix(PlayMakerFSM __instance)
        {
            try
            {
                if (!startLogged)
                {
                    startLogged = true;
                    Log.LogInfo("Start hook firing (__instance " + (__instance == null ? "is NULL" : "ok") + ")");
                }

                if (__instance == null) return;

                GameObject go = __instance.gameObject;
                if (go == null || go.name != "Trobbio") return;

                ApplyTornado(__instance.Fsm);
            }
            catch (Exception ex)
            {
                Log.LogError("StartPostfix failed: " + ex);
            }
        }

        private static void ApplyTornado(Fsm fsm)
        {
            if (fsm == null) return;

            // 1) Faster tornado: +/-22 becomes +/-25
            FsmState tornadoStart = fsm.GetState("Tornado Start");
            if (tornadoStart != null)
            {
                foreach (FsmStateAction action in tornadoStart.Actions)
                {
                    SetFloatValue sfv = action as SetFloatValue;
                    if (sfv == null || sfv.floatValue == null) continue;

                    if (sfv.floatValue.Value == 22f) sfv.floatValue.Value = 25f;
                    if (sfv.floatValue.Value == -22f) sfv.floatValue.Value = -25f;
                }
            }

            // 2) Extra tornado projectiles: duplicate each spawn, offset left/right by 5
            FsmState shoot = fsm.GetState("Tornado Shoot");
            if (shoot != null)
            {
                var spawns = new List<SpawnObjectFromGlobalPool>();
                foreach (FsmStateAction action in shoot.Actions)
                {
                    SpawnObjectFromGlobalPool spawn = action as SpawnObjectFromGlobalPool;
                    if (spawn != null) spawns.Add(spawn);
                }

                var actions = new List<FsmStateAction>(shoot.Actions);
                for (int j = 0; j < spawns.Count; j++)
                {
                    SpawnObjectFromGlobalPool src = spawns[j];
                    if (src.gameObject == null || src.spawnPoint == null) continue;

                    float z = src.position != null ? src.position.Value.z : 0f;

                    var copy = new SpawnObjectFromGlobalPool();
                    copy.gameObject = new FsmGameObject { UseVariable = false, Value = src.gameObject.Value };
                    copy.spawnPoint = new FsmGameObject { UseVariable = false, Value = src.spawnPoint.Value };
                    copy.position = new FsmVector3
                    {
                        UseVariable = false,
                        Name = "",
                        Value = new Vector3(Mathf.Pow(-1f, j) * 5f, 0f, z)
                    };
                    copy.rotation = src.rotation;
                    copy.storeObject = src.storeObject;

                    actions.Insert(Mathf.Min(14 + j, actions.Count), copy);
                }
                shoot.Actions = actions.ToArray();
            }

            // 3) Steering behaviour while the tornado is active
            FsmState tornado = fsm.GetState("Tornado");
            if (tornado != null)
            {
                bool hasTurn = false;
                foreach (FsmStateAction action in tornado.Actions)
                {
                    if (action is TornadoTurn) { hasTurn = true; break; }
                }

                if (!hasTurn)
                {
                    var list = new List<FsmStateAction>(tornado.Actions);
                    list.Insert(0, new TornadoTurn());
                    tornado.Actions = list.ToArray();
                }
            }
        }
    }

    // Custom PlayMaker action: if Trobbio is far from Hornet and moving away, fire the "WALL" event.
    public class TornadoTurn : FsmStateAction
    {
        private Rigidbody2D rb;
        private Transform trobbio;
        private Transform hero;
        private float distMax;

        public override void OnEnter()
        {
            trobbio = Owner.transform;

            HeroController h = HeroController.instance;
            hero = h != null ? h.transform : null;

            rb = Owner.GetComponent<Rigidbody2D>();
            distMax = UnityEngine.Random.Range(10f, 12f);
        }

        public override void OnUpdate()
        {
            if (hero == null || rb == null || trobbio == null) return;

            if (Mathf.Abs(hero.position.x - trobbio.position.x) < distMax) return;

            Vector2 toHero = hero.position - trobbio.position;
            Vector2 velocity = rb.velocity;

            if (Vector2.Dot(velocity.normalized, toHero.normalized) < 0f)
            {
                this.Fsm.Event("WALL");
            }
        }
    }
}
