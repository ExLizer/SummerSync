using System.Collections.Generic;
using System.Globalization;
using HutongGames.PlayMaker;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using UnityEngine;

namespace SummerSync.Unity.Objects
{
    /// <summary>
    /// One more of some of a player's own things for each player in the session beyond the first: the motorcycle helmet, the
    /// spanner set and, once it is bought, the ratchet set. The game has one of each, the mod shares it, and without these
    /// only one player at a time could wear the helmet or carry the tools.
    ///
    /// Every game makes the same ones under the same names ("helmet(itemx) 2" for the second player, "helmet(itemx) 3" for
    /// the third), next to the game's own, and the loose objects share them as they share anything else: a client pairs the
    /// host's with its own by name. They are not saved (the game saves its own by reference): a world loaded again has none
    /// until a session needs them. They stay until the world is left, whoever leaves the session.
    ///
    /// What is assumed about the game (docs/GAME_NOTES.md, "El casco y las cajas de herramientas"):
    /// - The helmet's machine "Use", picked with the hands empty, sends "EQUIP" to the player's helmet machine
    ///   ("PLAYER/…/FPSCamera/FPSCamera/Helmet :: Logic"), which puts on the helmet in its variable "Helmet" half a second later
    ///   ("Anim1", then "Disable helmet"), and takes off that same one. That variable is always the game's own helmet: the one
    ///   whose "Use" asked is written there during "Anim1".
    /// - A toolbox's lid ("Pivot/top :: Use") opens and closes it and switches its tools on and off; the player's tool hand
    ///   takes a tool from any box by the tool's name. Nothing of either is saved but by the game's own reference.
    /// </summary>
    internal sealed class PerPlayerThings
    {
        private const float LookEvery = 1f;
        private const int MostCopies = 3;
        private const string ItemsRoot = "ITEMS";

        // What a hand that someone else holds puts things on (ObjectPresenter.HeldLayer), and where they lie loose.
        private const int HeldLayer = 16;
        private const int PickableLayer = 19;

        private const float Spacing = 0.05f;
        private const float SettleDepenetration = 1f;
        private const float SettleSeconds = 2f;

        /// <summary>The game's helmet; its copies are named after it ("helmet(itemx) 2").</summary>
        internal const string HelmetName = "helmet(itemx)";
        private const string HelmetMachinePath = "PLAYER/Pivot/AnimPivot/Camera/FPSCamera/FPSCamera/Helmet";
        private const string HelmetMachine = "Logic";
        private const string HelmetVariable = "Helmet";
        private const string HelmetPuttingOn = "Anim1";
        private const string HelmetUse = "Use";
        private const string HelmetUseAsked = "State 3";

        // The states of the player's helmet machine with the helmet on ("Helmet ON", "State 1" waiting for the button that
        // takes it off, "State 2" after a bee sting).
        private const string HelmetOn = "Helmet ON";
        private const string HelmetOnWait = "State 1";
        private const string HelmetStung = "State 2";

        // Where the middle of a worn helmet goes from the eyes, in the camera's axes, and how it is turned from the camera
        // (its top looks along its +Z, its visor along its -Y). The others put it on the head of this player's figure; this
        // is where it is told to be, and where it is when the game takes it off.
        private static readonly Vector3 WornAt = new Vector3(0f, 0.08f, -0.03f);
        private static readonly Quaternion WornTurn = Quaternion.Euler(-90f, 0f, 0f);

        // The things, by name; where the game keeps each loose: under "ITEMS", or at the root (where the helmet goes once it has
        // been taken off, and the ratchet set once its box is opened in the shop).
        private static readonly string[] Names = { HelmetName, "spanner set(itemx)", "ratchet set(Clone)" };

        private readonly Logger log;
        private readonly NetworkManager manager;
        private readonly List<Settling> settling = new List<Settling>();

        // The copies made in this world, by name: a worn one is switched off and found by no search.
        private readonly Dictionary<string, GameObject> copies = new Dictionary<string, GameObject>();
        private float nextLook;
        private PlayMakerFSM helmetMachine;
        private bool helmetMachineSearched;
        private GameObject helmetReported;

        public PerPlayerThings(Logger log, NetworkManager manager)
        {
            this.log = log;
            this.manager = manager;
        }

        /// <summary>
        /// Once per frame. <paramref name="localPlayer"/> is the player's root object, or null while there is no game world.
        /// Returns true when it made something, for the loose objects to look for it now.
        /// </summary>
        public bool LateUpdate(Transform localPlayer)
        {
            if (localPlayer == null)
            {
                helmetMachine = null;
                helmetMachineSearched = false;
                helmetReported = null;
                settling.Clear();
                copies.Clear();
                return false;
            }

            PointHelmet();
            WearHelmet();
            Settle();
            NetSession session = manager.Session;
            if (!manager.IsActive || session == null || session.State != SessionState.Connected || Time.unscaledTime < nextLook)
            {
                return false;
            }

            nextLook = Time.unscaledTime + LookEvery;
            int wanted = Mathf.Min(session.Players.Count - 1, MostCopies);
            bool made = false;
            for (int i = 0; i < Names.Length; i++)
            {
                made |= MakeCopies(Names[i], wanted, session.Players.Count);
            }

            return made;
        }

        // Makes the copies of that thing that the session needs and this world does not have yet.
        private bool MakeCopies(string name, int wanted, int players)
        {
            if (wanted <= 0)
            {
                return false;
            }

            GameObject original = FindLoose(name);
            if (original == null)
            {
                return false;
            }

            Rigidbody body = original.GetComponent<Rigidbody>();

            // Worn, in someone's hand or moving: looked at again later. A copy is made next to where it lies.
            if (body == null || original.layer == HeldLayer || body.velocity.sqrMagnitude > 0.01f)
            {
                return false;
            }

            bool made = false;
            Bounds bounds = BoundsOf(original);
            float step = Mathf.Max(bounds.size.x, bounds.size.z) + Spacing;
            for (int number = 2; number <= wanted + 1; number++)
            {
                string copyName = name + " " + number.ToString(CultureInfo.InvariantCulture);
                GameObject had;

                // Unity's null check is also true for a destroyed object. One switched off (worn) is there all the same.
                if ((copies.TryGetValue(copyName, out had) && had != null) || FindLoose(copyName) != null)
                {
                    continue;
                }

                Vector3 at = original.transform.position + (original.transform.right * step * (number - 1)) + (Vector3.up * Spacing);
                var copy = (GameObject)Object.Instantiate(original, at, original.transform.rotation);
                copy.name = copyName;
                copy.transform.parent = original.transform.parent;
                copy.layer = PickableLayer;
                Rigidbody copyBody = copy.GetComponent<Rigidbody>();
                copyBody.isKinematic = false;
                copyBody.detectCollisions = true;
                copyBody.velocity = Vector3.zero;
                copyBody.angularVelocity = Vector3.zero;
                copies[copyName] = copy;

                // Next to the game's own, it may start inside a shelf or a wall: pushed out slowly, not thrown.
                settling.Add(new Settling { Body = copyBody, Before = copyBody.maxDepenetrationVelocity, Until = Time.time + SettleSeconds });
                copyBody.maxDepenetrationVelocity = Mathf.Min(copyBody.maxDepenetrationVelocity, SettleDepenetration);
                made = true;
                log.Info(
                    LogCategory.Authority,
                    "Per player: made '" + copyName + "' next to the game's own '" + name + "', at (" + Format(at.x) + ", " + Format(at.y) + ", " + Format(at.z)
                    + "): " + players + " players in the session. The game does not save it; every game in the session makes the same.");
            }

            return made;
        }

        // The player's helmet machine puts on the helmet in its variable, always the game's own: while it puts one on, the one
        // whose own machine asked for it.
        private void PointHelmet()
        {
            if (helmetMachine == null && !helmetMachineSearched)
            {
                helmetMachineSearched = true;
                GameObject holder = GameObject.Find(HelmetMachinePath);
                helmetMachine = holder == null ? null : Machine(holder, HelmetMachine);
                if (helmetMachine == null)
                {
                    log.Warning(LogCategory.Authority, "Per player: the player's helmet machine ('" + HelmetMachinePath + " :: " + HelmetMachine + "') was not found: the copies of the helmet put on the game's own.");
                }
            }

            // Unity's null check is also true for a destroyed object.
            if (helmetMachine == null || helmetMachine.ActiveStateName != HelmetPuttingOn)
            {
                return;
            }

            FsmGameObject worn = helmetMachine.FsmVariables.GetFsmGameObject(HelmetVariable);
            if (worn == null)
            {
                return;
            }

            for (int number = 1; number <= MostCopies + 1; number++)
            {
                string name = number == 1 ? HelmetName : HelmetName + " " + number.ToString(CultureInfo.InvariantCulture);
                GameObject helmet = FindLoose(name);
                PlayMakerFSM use = helmet == null ? null : Machine(helmet, HelmetUse);
                if (use == null || use.ActiveStateName != HelmetUseAsked)
                {
                    continue;
                }

                if (worn.Value != helmet)
                {
                    worn.Value = helmet;
                }

                if (helmetReported != helmet)
                {
                    helmetReported = helmet;
                    log.Info(LogCategory.Authority, "Per player: the local player puts on '" + name + "'.");
                }

                return;
            }
        }

        // In a session, the helmet the local player wears sits on their head, as the others see it. The game only switches it
        // off under the camera, 0.2 m in front of the eyes and turned as it lay where it was picked (resetLocalPosition, not the
        // turn), and the loose objects tell the others where it is: they saw it in front of the face, turned to a side
        // (2026-11-13). The helmet's top looks along its +Z (the neck opening along -Z) and its visor, a peak, along its -Y:
        // the visor's mesh is thin along Z and sits 6 cm toward -Y.
        private void WearHelmet()
        {
            // Unity's null check is also true for a destroyed object.
            if (!manager.IsActive || helmetMachine == null)
            {
                return;
            }

            string state = helmetMachine.ActiveStateName;
            if (state != HelmetOn && state != HelmetOnWait && state != HelmetStung)
            {
                return;
            }

            FsmGameObject worn = helmetMachine.FsmVariables.GetFsmGameObject(HelmetVariable);
            Camera eyes = Camera.main;
            GameObject helmet = worn == null ? null : worn.Value;
            if (helmet == null || eyes == null || helmet.activeSelf || !helmet.transform.IsChildOf(eyes.transform))
            {
                return;
            }

            Transform head = eyes.transform;
            helmet.transform.position = head.TransformPoint(WornAt);
            helmet.transform.rotation = head.rotation * WornTurn;
        }

        // The copies made a moment ago, pushed out of what they overlap at the game's speed again.
        private void Settle()
        {
            for (int i = settling.Count - 1; i >= 0; i--)
            {
                Settling one = settling[i];

                // Unity's null check is also true for a destroyed object.
                if (one.Body != null && Time.time < one.Until)
                {
                    continue;
                }

                if (one.Body != null)
                {
                    one.Body.maxDepenetrationVelocity = one.Before;
                }

                settling.RemoveAt(i);
            }
        }

        // A thing lying loose: a child of "ITEMS" or a root object, switched on.
        private static GameObject FindLoose(string name)
        {
            GameObject found = GameObject.Find(ItemsRoot + "/" + name);
            return found != null ? found : GameObject.Find("/" + name);
        }

        private static PlayMakerFSM Machine(GameObject holder, string name)
        {
            PlayMakerFSM[] machines = holder.GetComponents<PlayMakerFSM>();
            for (int i = 0; i < machines.Length; i++)
            {
                if (machines[i].FsmName == name)
                {
                    return machines[i];
                }
            }

            return null;
        }

        private static Bounds BoundsOf(GameObject thing)
        {
            Collider[] colliders = thing.GetComponentsInChildren<Collider>();
            var bounds = new Bounds(thing.transform.position, Vector3.zero);
            for (int i = 0; i < colliders.Length; i++)
            {
                if (!colliders[i].isTrigger)
                {
                    bounds.Encapsulate(colliders[i].bounds);
                }
            }

            return bounds;
        }

        private static string Format(float value)
        {
            return value.ToString("0.0", CultureInfo.InvariantCulture);
        }

        private struct Settling
        {
            public Rigidbody Body;
            public float Before;
            public float Until;
        }
    }
}
