using System.Collections.Generic;
using System.Globalization;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Objects;
using SummerSync.Unity.Objects;
using SummerSync.Unity.World;
using UnityEngine;

namespace SummerSync.Unity.Players
{
    /// <summary>
    /// What a player throws away after a drink: the empty bottle, pack or cup, made on every machine.
    ///
    /// What is assumed about the game, from the dump of 2026-10-12 and the prefabs in sharedassets3 (docs/GAME_NOTES.md):
    /// the state machine "Drink" of "PLAYER/Pivot/AnimPivot/Camera/FPSCamera/FPSCamera/Drink" makes them with
    /// "CreateObject" at "ThrowBottle" (BottleBeerFly, BottleBoozeFly, VodkaShotFly, MilkFly, CoffeeFly,
    /// BottleSpiritFly). Each is a loose thing on layer 19 with a rigidbody and one state machine that, in its first
    /// frame, gives it the local player's speed ("PlayerVelocity"), renames it ("empty bottle(Clone)", "empty
    /// pack(Clone)", "empty cup(Clone)") and pushes it the way the player looks ("PlayerDir"). The action keeps what
    /// it made in its "storeObject", even unnamed: that is how a new one is noticed here.
    ///
    /// A moment after a throw here, once it has its name and its speed, the others are told; there, the same
    /// prefab is made where it is, with that speed and that name, and without its state machine, which would push
    /// it the way the player of that machine looks. The loose objects' replication pairs the two by name.
    /// </summary>
    internal sealed class ThrowSync
    {
        private const string DrinkPath = "Pivot/AnimPivot/Camera/FPSCamera/FPSCamera/Drink";
        private const string DrinkMachine = "Drink";

        // Where sausages are cooked (dump of 2026-10-14, "SausageTrigger", state machine "Logic"): four made from
        // "sausage0" where the package was. Only the host's cook (see FireSync); the others make what it made, with
        // their own state machines, which are not the throwing kind.
        private static readonly string[] CookPaths =
        {
            "YARD/Building/LIVINGROOM/Fireplace/SausageTrigger", "COTTAGE/Fireplace/SausageTrigger", "COTTAGE/Grill/Fireplace/SausageTrigger",
            "CABIN/Cabin/woodstove/Fireplace/SausageTrigger", "YARD/Building/KITCHEN/OvenStove/SausageTrigger", "ITEMS/grill(itemx)/Fireplace/SausageTrigger",
        };

        private const string CookMachine = "Logic";
        private const double SearchInterval = 2.0;

        // A thrown thing has its name and speed after its first frame; told then, or after this long at most.
        private const double TellAfter = 0.1;
        private const double TellAtMost = 0.6;

        private readonly Logger log;
        private readonly NetworkManager manager;
        private readonly ObjectPresenter objects;
        private readonly List<Thrower> throwers = new List<Thrower>();
        private readonly List<Pending> pending = new List<Pending>();
        private readonly List<ThrownItem> wanted = new List<ThrownItem>();
        private readonly HashSet<string> missingReported = new HashSet<string>();
        private ItemReplication current;
        private Transform player;
        private double nextSearch;
        private bool found;
        private bool searchReported;

        public ThrowSync(Logger log, NetworkManager manager, ObjectPresenter objects)
        {
            this.log = log;
            this.manager = manager;
            this.objects = objects;
        }

        /// <summary>
        /// Once per frame, after the game has moved everything. <paramref name="localPlayer"/> is the player's
        /// root object, or null while there is no game world.
        /// </summary>
        public void LateUpdate(double now, Transform localPlayer)
        {
            ItemReplication items = localPlayer == null ? null : manager.Items;
            if (items != current || localPlayer != player)
            {
                if (current != null)
                {
                    current.ThrownWanted -= OnThrownWanted;
                }

                current = items;
                player = localPlayer;
                if (current != null)
                {
                    current.ThrownWanted += OnThrownWanted;
                }

                throwers.Clear();
                pending.Clear();
                wanted.Clear();
                missingReported.Clear();
                found = false;
                searchReported = false;
                nextSearch = now;
            }

            if (current == null)
            {
                return;
            }

            if (!found && now >= nextSearch)
            {
                nextSearch = now + SearchInterval;
                Search();
            }

            if (!found)
            {
                return;
            }

            for (int i = 0; i < wanted.Count; i++)
            {
                Make(wanted[i], now);
            }

            wanted.Clear();
            Notice(now);
            Tell(now);
        }

        private void Search()
        {
            Transform drink = player.Find(DrinkPath);
            PlayMakerFSM fsm = null;
            PlayMakerFSM[] machines = drink == null ? new PlayMakerFSM[0] : drink.GetComponents<PlayMakerFSM>();
            for (int i = 0; i < machines.Length; i++)
            {
                if (machines[i].FsmName == DrinkMachine)
                {
                    fsm = machines[i];
                }
            }

            AddThrowers(fsm, true);
            int cooks = 0;
            for (int i = 0; i < CookPaths.Length; i++)
            {
                int slash = CookPaths[i].IndexOf('/');
                GameObject root = GameObject.Find("/" + CookPaths[i].Substring(0, slash));
                Transform at = root == null ? null : root.transform.Find(CookPaths[i].Substring(slash + 1));
                PlayMakerFSM cook = at == null ? null : DoorSync.FindMachine(at, CookMachine);
                if (cook != null)
                {
                    AddThrowers(cook, false);
                    cooks++;
                }
            }

            if (cooks > 0)
            {
                log.Info(LogCategory.Player, "What " + cooks + " fire(s) and grill(s) cook is shared.");
            }

            found = throwers.Count > 0;
            if (found)
            {
                var names = new System.Text.StringBuilder();
                for (int i = 0; i < throwers.Count; i++)
                {
                    names.Append(i == 0 ? string.Empty : ", ").Append(throwers[i].Prefab.name);
                }

                log.Info(LogCategory.Player, "What the player throws away after a drink is shared: " + names + ".");
            }
            else if (!searchReported)
            {
                searchReported = true;
                log.Warning(LogCategory.Player, "The player's drinking was not found as this build expects it ('PLAYER/" + DrinkPath + "', state machine '" + DrinkMachine + "' making things): what a player throws away after a drink is seen only by them.");
            }
        }

        // The things a state machine makes with "CreateObject"; thrown ones are made elsewhere with their machines off.
        private void AddThrowers(PlayMakerFSM fsm, bool thrown)
        {
            FsmState[] states = fsm == null || fsm.Fsm == null ? null : fsm.Fsm.States;
            for (int s = 0; states != null && s < states.Length; s++)
            {
                FsmStateAction[] actions = states[s].Actions;
                for (int a = 0; actions != null && a < actions.Length; a++)
                {
                    var create = actions[a] as CreateObject;
                    if (create == null || create.gameObject == null || create.gameObject.Value == null)
                    {
                        continue;
                    }

                    throwers.Add(new Thrower { Action = create, Prefab = create.gameObject.Value, Last = create.storeObject == null ? null : create.storeObject.Value, Thrown = thrown });
                }
            }
        }

        // Something the local player's drinking has just made.
        private void Notice(double now)
        {
            for (int i = 0; i < throwers.Count; i++)
            {
                Thrower thrower = throwers[i];
                GameObject made = thrower.Action.storeObject == null ? null : thrower.Action.storeObject.Value;
                if (made == null || made == thrower.Last)
                {
                    continue;
                }

                thrower.Last = made;
                pending.Add(new Pending { Made = made, From = thrower, At = now });
            }
        }

        private void Tell(double now)
        {
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                Pending next = pending[i];

                // Unity's null check is also true for a destroyed object.
                if (next.Made == null)
                {
                    pending.RemoveAt(i);
                    continue;
                }

                bool named = next.Made.name != next.From.Prefab.name + "(Clone)";
                if (now - next.At < TellAfter || (!named && now - next.At < TellAtMost))
                {
                    continue;
                }

                pending.RemoveAt(i);
                Transform transform = next.Made.transform;
                Rigidbody body = next.Made.GetComponent<Rigidbody>();
                Vector3 velocity = body == null ? Vector3.zero : body.velocity;
                Vector3 spin = body == null ? Vector3.zero : body.angularVelocity;
                current.TellThrown(new ThrownItem
                {
                    Prefab = next.From.Prefab.name,
                    Name = next.Made.name,
                    Pose = new BodyPose(transform.position.x, transform.position.y, transform.position.z, transform.rotation.x, transform.rotation.y, transform.rotation.z, transform.rotation.w),
                    Motion = new BodyMotion { VX = velocity.x, VY = velocity.y, VZ = velocity.z, AX = spin.x, AY = spin.y, AZ = spin.z },
                });
                objects.ScanSoon(now);
                log.Info(LogCategory.Player, "The player threw away '" + next.Made.name + "' (" + next.From.Prefab.name + ") at " + Where(transform.position) + ", " + velocity.magnitude.ToString("0.0", CultureInfo.InvariantCulture) + " m/s; the others are told.");
            }
        }

        private void OnThrownWanted(ThrownItem thrown)
        {
            wanted.Add(thrown);
        }

        // Another player threw something away: the same thing flies here, from where it was, as fast.
        private void Make(ThrownItem thrown, double now)
        {
            GameObject prefab = null;
            bool machinesOff = true;
            for (int i = 0; i < throwers.Count; i++)
            {
                if (throwers[i].Prefab.name == thrown.Prefab)
                {
                    prefab = throwers[i].Prefab;
                    machinesOff = throwers[i].Thrown;
                    break;
                }
            }

            if (prefab == null)
            {
                if (missingReported.Add(thrown.Prefab))
                {
                    log.Warning(LogCategory.Player, "Player " + thrown.Player + " threw away a '" + thrown.Prefab + "', which this game's drinking does not make.");
                }

                return;
            }

            var position = new Vector3(thrown.Pose.X, thrown.Pose.Y, thrown.Pose.Z);
            var rotation = new Quaternion(thrown.Pose.QX, thrown.Pose.QY, thrown.Pose.QZ, thrown.Pose.QW);
            var made = (GameObject)Object.Instantiate(prefab, position, rotation);

            // Before its first frame: it would push itself the way the player of this machine looks.
            PlayMakerFSM[] machines = made.GetComponents<PlayMakerFSM>();
            for (int i = 0; machinesOff && i < machines.Length; i++)
            {
                machines[i].enabled = false;
            }

            if (!string.IsNullOrEmpty(thrown.Name))
            {
                made.name = thrown.Name;
            }

            Rigidbody body = made.GetComponent<Rigidbody>();
            if (body != null)
            {
                body.velocity = new Vector3(thrown.Motion.VX, thrown.Motion.VY, thrown.Motion.VZ);
                body.angularVelocity = new Vector3(thrown.Motion.AX, thrown.Motion.AY, thrown.Motion.AZ);
            }

            objects.Expect(made, thrown.Player, now);
            objects.ScanSoon(now);
            log.Info(LogCategory.Player, "Player " + thrown.Player + " threw away '" + made.name + "' (" + thrown.Prefab + "); thrown here too, from " + Where(position) + ".");
        }

        private static string Where(Vector3 position)
        {
            return "(" + position.x.ToString("0.0", CultureInfo.InvariantCulture) + ", " + position.y.ToString("0.0", CultureInfo.InvariantCulture) + ", "
                + position.z.ToString("0.0", CultureInfo.InvariantCulture) + ")";
        }

        private sealed class Thrower
        {
            public CreateObject Action;
            public GameObject Prefab;
            public GameObject Last;

            // Thrown by the player's drinking: made elsewhere with its state machines off.
            public bool Thrown;
        }

        private struct Pending
        {
            public GameObject Made;
            public Thrower From;
            public double At;
        }
    }
}
