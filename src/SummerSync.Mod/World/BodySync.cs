using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Objects;
using SummerSync.Core.World;
using SummerSync.Unity.Vehicles;
using UnityEngine;

namespace SummerSync.Unity.World
{
    /// <summary>
    /// The bodies of the people the game lets go as rag dolls (run over, knocked down), one for everyone
    /// (<see cref="BodyDefinition"/>, <c>data/bodies/*.json</c>; what travels: <see cref="BodyWire"/>).
    ///
    /// What is assumed about the game (docs/GAME_NOTES.md, "Los cuerpos"): a person's rag doll hangs below them, switched
    /// off, until a state of theirs takes it off its parent and switches it on (the walkers' "CarHit :: Accident", the
    /// fighters' "Hitbox" state 5); then it is a few rigidbodies held by joints, which some machines below it keep pushing
    /// ("Force": the body squirms), and which the hand drags by any of its bones (tag "RAGDOLL", "PickUp :: Ragdoll picked").
    /// The game does not save it: a world loaded again has the person back.
    ///
    /// The person dies in every game by the syncs that share them (<see cref="PeopleSync"/>, <see cref="FighterSync"/>);
    /// each game would then move its own rag doll. Here one game simulates the body: the one where it was let go first,
    /// and from then on whoever drags it, the game that simulates the vehicle it rests in, or the host's if its owner leaves.
    /// That game tells where each bone is while it moves; the others show their rag doll there, kinematic, without
    /// collisions and without what pushes it, and a hand that takes it there takes it over. A game whose person is gone
    /// (hidden because it was gone in the host's world, or never let go here) takes the rag doll out itself, as the game's
    /// state does: off its parent and on. A player who joins later hears the owner and the bones the host remembers.
    ///
    /// Offline, or before the session's first answer, nothing here touches the game.
    /// </summary>
    internal sealed class BodySync
    {
        private const LogCategory Category = LogCategory.Fsm;
        private const string HandPath = "Pivot/AnimPivot/Camera/FPSCamera/1Hand_Assemble/Hand";
        private const double CensusInterval = 10.0;
        private const double SendInterval = 0.1;
        private const float StepMetres = 0.02f;
        private const float StepDegrees = 3f;

        // How long a game waits for its own game to let a body go, after the session said someone has it, before it takes
        // the rag doll out itself (the person's death travels on its own and is played here the game's way).
        private const double TakeOutAfter = 2.0;
        private const double AskTimeout = 5.0;
        private const double DutiesInterval = 1.0;
        private const float Smoothing = 12f;
        private const float SnapMetres = 3f;
        private const float RestSpeed = 0.2f;
        private const float CheckTolerance = 0.5f;
        private const double PushOutSeconds = 1.5;

        // Below this height a body has fallen out of the world (a walker can walk below the ground where the game puts it, and
        // its rag doll then falls for ever): no longer told, nor checked.
        private const float LostBelow = -100f;

        private readonly Logger log;
        private readonly NetworkManager manager;
        private readonly VehiclePresenter vehicles;
        private readonly BodyDefinition[] catalog;
        private readonly List<Body> bodies = new List<Body>();
        private readonly Dictionary<int, Body> byBone = new Dictionary<int, Body>();
        private readonly List<Heard> heard = new List<Heard>();
        private readonly List<byte> present = new List<byte>();
        private TallyReplication current;
        private bool host;
        private bool sessionStarted;
        private double clock;
        private double nextCensus;
        private double nextDuties;
        private bool censusDone;
        private Transform handAt;
        private Joint hand;
        private bool handSearched;
        private BodiesSection check;

        public BodySync(Logger log, NetworkManager manager, VehiclePresenter vehicles)
        {
            this.log = log;
            this.manager = manager;
            this.vehicles = vehicles;
            catalog = Load(log);
        }

        /// <summary>The section of where the bodies lie (Fase 8 checksums).</summary>
        public ICheckSection Check
        {
            get { return check ?? (check = new BodiesSection(this)); }
        }

        /// <summary>Once per frame, before the syncs whose people die. <paramref name="localPlayer"/> is null without a world.</summary>
        public void LateUpdate(double now, Transform localPlayer)
        {
            clock = now;
            if (localPlayer == null)
            {
                return;
            }

            if (!censusDone && now >= nextCensus)
            {
                nextCensus = now + CensusInterval;
                Census();
            }

            TallyReplication tallies = manager.Tallies;
            if (tallies != current)
            {
                Release();
                current = tallies;
                host = manager.Mode == NetworkMode.Host;
                sessionStarted = false;
                if (current != null)
                {
                    current.StateWanted += OnStateWanted;
                }
            }

            NetSession session = manager.Session;
            bool connected = current != null && session != null && session.State == SessionState.Connected;
            if (connected)
            {
                for (int i = 0; i < heard.Count; i++)
                {
                    Hear(heard[i]);
                }
            }

            heard.Clear();
            for (int i = 0; i < bodies.Count; i++)
            {
                WatchLetGo(bodies[i], connected && (host || current.HasHeardAnswer));
            }

            if (!connected)
            {
                return;
            }

            if (!sessionStarted && (host || current.HasHeardAnswer))
            {
                sessionStarted = true;
                log.Info(Category, "Bodies: " + bodies.Count + " rag doll(s) of " + catalog.Length + " known here; " + (host ? "the bodies this world has lie for everyone as they lie here." : "the host's bodies lie here as they lie there."));
            }

            Body held = HeldBy(HeldBody(localPlayer));
            byte me = session.LocalPlayerId;
            for (int i = 0; i < bodies.Count; i++)
            {
                Body body = bodies[i];
                if (body.Root == null)
                {
                    continue;
                }

                if (body == held && !Mine(body, me) && body.Asked == 0)
                {
                    Take(body, me, "the hand here took it");
                }

                if (body.Asked > 0 && now - body.AskedAt > AskTimeout)
                {
                    log.Warning(Category, "Bodies: '" + body.Name + "' was asked for " + AskTimeout + " s ago and the host has not said it is this game's; it is shown as the session has it.");
                    body.Asked = 0;
                }

                if (Mine(body, me))
                {
                    Own(body, now);
                }
                else if (body.Asked > 0)
                {
                    // Taken here, not said back yet: its own physics, so that the hand drags it meanwhile.
                    Free(body);
                }
                else if (body.Claim.Known)
                {
                    Follow(body, Time.deltaTime);
                }
                else if (body.LetGo && (host || body.MayClaim) && sessionStarted)
                {
                    // The host's world is the session's: what lies in it lies for everyone, also what was let go before the
                    // session. A guest's only what its game let go once it had the host's world.
                    Take(body, me, host ? "it lies in this game" : "it was let go in this game");
                }
            }

            if (now >= nextDuties)
            {
                nextDuties = now + DutiesInterval;
                Duties(session, me);
            }
        }

        /// <summary>Everything back as the game had it: the session is over, or the world.</summary>
        public void Release()
        {
            if (current != null)
            {
                current.StateWanted -= OnStateWanted;
            }

            current = null;
            heard.Clear();
            for (int i = 0; i < bodies.Count; i++)
            {
                Body body = bodies[i];
                Free(body);
                ReleasePushOut(body, true);
                PutBack(body);
                body.Claim.Forget();
                body.Asked = 0;
                body.SaidAt = double.NaN;
                body.SaidInCatchUp = false;
                body.Waiting = false;
                for (int b = 0; b < body.Bones.Length; b++)
                {
                    body.HasTarget[b] = false;
                    body.HasSent[b] = false;
                }
            }
        }

        /// <summary>"ss bodies": each body this game knows, how it lies and whose it is.</summary>
        public string Describe()
        {
            var text = new StringBuilder();
            text.Append("Bodies: ").Append(bodies.Count).Append(" rag doll(s) of ").Append(catalog.Length).Append(" known here");
            text.Append(current == null ? "; not in a session." : ".");
            byte me = manager.Session == null ? (byte)0 : manager.Session.LocalPlayerId;
            for (int i = 0; i < bodies.Count; i++)
            {
                Body body = bodies[i];
                text.Append('\n').Append(body.Name).Append(": ");
                if (body.Root == null)
                {
                    text.Append("gone from this game");
                    continue;
                }

                text.Append(body.LetGo ? (body.TakenOut ? "taken out by the mod" : "let go") : "in its person");
                if (body.Claim.Known)
                {
                    text.Append(", ").Append(Mine(body, me) ? "simulated here" : "player " + body.Claim.Owner + "'s").Append(" (generation ").Append(body.Claim.Generation).Append(')');
                }
                else if (body.Asked > 0)
                {
                    text.Append(", asked for");
                }

                if (body.LetGo)
                {
                    Vector3 at = body.Bones[0].position;
                    text.Append(", at ").Append(Place(at));
                }

                text.Append(body.Showing ? ", shown as another's" : string.Empty);
            }

            return text.ToString();
        }

        /// <summary>"ss bodies take &lt;name&gt;": a test aid; this game takes the body, as a hand that drags it.</summary>
        public string TestTake(string name)
        {
            NetSession session = manager.Session;
            if (current == null || session == null || session.State != SessionState.Connected)
            {
                return "Bodies: not in a session.";
            }

            for (int i = 0; i < bodies.Count; i++)
            {
                if (string.Equals(bodies[i].Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    if (!bodies[i].LetGo)
                    {
                        return "Bodies: '" + bodies[i].Name + "' is not lying here.";
                    }

                    Take(bodies[i], session.LocalPlayerId, "a test aid took it");
                    return "Bodies: '" + bodies[i].Name + "' taken here, as by a hand.";
                }
            }

            return "Bodies: no body called '" + name + "' here.";
        }

        // Once the world is there: each rag doll where it hangs, before anyone can have been run over.
        private void Census()
        {
            int missing = 0;
            for (int d = 0; d < catalog.Length; d++)
            {
                BodyDefinition definition = catalog[d];
                bool known = false;
                for (int i = 0; i < bodies.Count && !known; i++)
                {
                    known = bodies[i].Definition == definition;
                }

                if (known)
                {
                    continue;
                }

                Transform root = First(definition.Paths);
                if (root == null)
                {
                    missing++;
                    continue;
                }

                Body body = Body.Make(definition, root, First(definition.Person));
                if (body.Bones.Length == 0 || body.Bones.Length > BodyWire.MaxBones)
                {
                    log.Warning(Category, "Bodies: '" + definition.Name + "' at '" + definition.Paths[0] + "' has " + body.Bones.Length + " bone(s); this build shares 1 to " + BodyWire.MaxBones + ". Each game moves its own.");
                    continue;
                }

                bodies.Add(body);
                for (int b = 0; b < body.Bones.Length; b++)
                {
                    byBone[body.Bones[b].GetInstanceID()] = body;
                }
            }

            if (missing == 0)
            {
                censusDone = true;
            }

            if (missing == 0 || bodies.Count > 0 && !censusLogged)
            {
                censusLogged = true;
                log.Info(Category, "Bodies: " + bodies.Count + " rag doll(s) found where the game keeps them (" + missing + " of the " + catalog.Length + " known not in this world now).");
            }
        }

        private bool censusLogged;

        private static Transform First(string[] paths)
        {
            for (int i = 0; i < paths.Length; i++)
            {
                Transform found = FindPath(paths[i]);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private static Transform FindPath(string path)
        {
            int slash = path.IndexOf('/');
            GameObject top = GameObject.Find("/" + (slash < 0 ? path : path.Substring(0, slash)));
            if (top == null)
            {
                return null;
            }

            return slash < 0 ? top.transform : top.transform.Find(path.Substring(slash + 1));
        }

        // Let go here: by the game (off its parent, on), or by the mod.
        private void WatchLetGo(Body body, bool inSession)
        {
            // Unity's null check is also true for a destroyed object.
            if (body.LetGo || body.Root == null || body.Root.parent != null || !body.Root.gameObject.activeInHierarchy)
            {
                return;
            }

            body.LetGo = true;
            body.MayClaim = inSession || host;
            Vector3 at = body.Bones[0].position;
            log.Info(
                Category,
                "Bodies: '" + body.Name + "' was let go in this game at " + Place(at) + (body.Claim.Known ? "; player " + body.Claim.Owner + "'s game has it." : inSession || host ? "." : "; before the session, so it is this game's own."));
        }

        private bool Mine(Body body, byte me)
        {
            return body.Claim.Known && body.Claim.Owner == me && body.Asked == 0;
        }

        // Says that a player's game simulates the body now: the host's word counts at once; a client's once the host passes it on.
        private void Take(Body body, byte who, string why)
        {
            int generation = Math.Max(body.Claim.Next, body.Asked + 1);
            current.Set(body.OwnerKey, BodyWire.PackOwner(who, generation));
            byte me = manager.Session.LocalPlayerId;
            if (host)
            {
                Owner(body, who, generation, false);
            }
            else if (who == me)
            {
                body.Asked = generation;
                body.AskedAt = clock;
            }

            log.Info(Category, "Bodies: '" + body.Name + "' " + (who == me ? "taken by this game" : "given to player " + who + "'s game") + " (" + why + ", generation " + generation + ")" + (host || who != me ? "." : "; the host says it back."));
        }

        private void OnStateWanted(string key, int[] values)
        {
            if (key.StartsWith(BodyWire.Prefix, StringComparison.Ordinal))
            {
                heard.Add(new Heard { Key = key, Values = values, CatchUp = current != null && current.CatchingUp });
            }
        }

        private void Hear(Heard word)
        {
            string name;
            int bone;
            if (!BodyWire.TryParseKey(word.Key, out name, out bone))
            {
                return;
            }

            Body body = Find(name);
            if (body == null)
            {
                return;
            }

            if (bone < 0)
            {
                byte owner;
                int generation;
                if (BodyWire.TryUnpackOwner(word.Values, out owner, out generation))
                {
                    Owner(body, owner, generation, word.CatchUp);
                }

                return;
            }

            int boneGeneration;
            BonePose pose;
            if (bone >= body.Bones.Length || !BodyWire.TryUnpackBone(word.Values, out boneGeneration, out pose))
            {
                return;
            }

            body.Target[bone] = pose;
            body.TargetGeneration[bone] = boneGeneration;
            body.HasTarget[bone] = true;
            if (word.CatchUp)
            {
                body.Snap = true;
            }
        }

        // A word about whose the body is, in the order every game hears it.
        private void Owner(Body body, byte owner, int generation, bool catchUp)
        {
            byte me = manager.Session.LocalPlayerId;
            bool mineAsked = body.Asked > 0 && owner == me && generation == body.Asked;
            if (!body.Claim.Hear(owner, generation))
            {
                if (mineAsked)
                {
                    body.Asked = 0;
                    log.Info(Category, "Bodies: '" + body.Name + "' was taken by player " + body.Claim.Owner + "'s game first; shown as there.");
                }

                return;
            }

            if (body.Asked > 0 && generation >= body.Asked)
            {
                // This game's word has counted, or can no longer count.
                if (owner != me)
                {
                    log.Info(Category, "Bodies: '" + body.Name + "' was taken by player " + owner + "'s game first; shown as there.");
                }

                body.Asked = 0;
            }

            if (double.IsNaN(body.SaidAt))
            {
                body.SaidAt = clock;
                body.SaidInCatchUp = catchUp;
            }

            if (owner == me)
            {
                // Everything is told again by its new owner.
                for (int b = 0; b < body.Bones.Length; b++)
                {
                    body.HasSent[b] = false;
                }

                log.Info(Category, "Bodies: '" + body.Name + "' is simulated in this game now (generation " + generation + ").");
            }
            else
            {
                body.Snap |= catchUp;
                log.Info(Category, "Bodies: '" + body.Name + "' is simulated in player " + owner + "'s game (generation " + generation + ")" + (catchUp ? ", as the host's world has it." : "."));
            }
        }

        // This game simulates the body: its physics, and where each bone is told while it moves.
        private void Own(Body body, double now)
        {
            if (!body.LetGo)
            {
                // Said to be this game's, but still in its person here (a word of the host for a body this world has not let go).
                TakeOutIfGone(body, true);
                if (!body.LetGo)
                {
                    return;
                }
            }

            Free(body);
            if (now < body.NextSend)
            {
                return;
            }

            body.NextSend = now + SendInterval;
            if (Lost(body))
            {
                if (!body.LostTold)
                {
                    body.LostTold = true;
                    log.Warning(Category, "Bodies: '" + body.Name + "' fell out of the world here (below " + LostBelow + " m): no longer told.");
                }

                return;
            }

            int generation = body.Claim.Generation;
            for (int b = 0; b < body.Bones.Length; b++)
            {
                Rigidbody bone = body.Bones[b];

                // Unity's null check is also true for a destroyed object.
                if (bone == null)
                {
                    continue;
                }

                BonePose pose = Read(bone.transform);
                if (body.HasSent[b] && !BodyWire.Moved(body.Sent[b], pose, StepMetres, StepDegrees))
                {
                    continue;
                }

                body.Sent[b] = pose;
                body.HasSent[b] = true;
                current.Set(body.BoneKeys[b], BodyWire.PackBone(generation, pose));
            }
        }

        // Another game simulates the body: shown where that game has it.
        private void Follow(Body body, float deltaTime)
        {
            if (!body.LetGo)
            {
                TakeOutIfGone(body, body.SaidInCatchUp || clock - body.SaidAt >= TakeOutAfter);
                if (!body.LetGo)
                {
                    return;
                }
            }

            Show(body);
            int generation = body.Claim.Generation;
            float blend = body.Snap ? 1f : 1f - Mathf.Exp(-Smoothing * deltaTime);
            body.Snap = false;
            for (int b = 0; b < body.Bones.Length; b++)
            {
                Rigidbody bone = body.Bones[b];

                // Unity's null check is also true for a destroyed object.
                if (bone == null || !body.HasTarget[b] || body.TargetGeneration[b] != generation)
                {
                    continue;
                }

                BonePose target = body.Target[b];
                Transform at = bone.transform;
                var place = new Vector3(target.X, target.Y, target.Z);
                var turn = new Quaternion(target.QX, target.QY, target.QZ, target.QW);
                if (blend >= 1f || (at.position - place).sqrMagnitude > SnapMetres * SnapMetres)
                {
                    at.position = place;
                    at.rotation = turn;
                }
                else
                {
                    at.position = Vector3.Lerp(at.position, place, blend);
                    at.rotation = Quaternion.Slerp(at.rotation, turn, blend);
                }
            }
        }

        // The session says the body lies, and here it is still in its person: taken out the way the game's state does it
        // (off its parent, switched on), but only if the person is gone or switched off here. A person alive here dies by
        // the sync that shares them, the game's way, and lets the rag doll go by itself.
        private void TakeOutIfGone(Body body, bool now)
        {
            // Unity's null check is also true for a destroyed object.
            bool alive = body.Person != null && body.Person.gameObject.activeInHierarchy;
            if (alive)
            {
                if (!body.Waiting && clock - body.SaidAt >= TakeOutAfter * 5)
                {
                    body.Waiting = true;
                    log.Warning(Category, "Bodies: '" + body.Name + "' lies in player " + body.Claim.Owner + "'s game, and here its person is still there; left to the game.");
                }

                return;
            }

            if (!now)
            {
                return;
            }

            body.Root.parent = null;
            body.Root.gameObject.SetActive(true);
            body.TakenOut = true;
            body.LetGo = true;
            body.Snap = true;
            log.Info(Category, "Bodies: '" + body.Name + "' lies in player " + body.Claim.Owner + "'s game and its person is gone here: its rag doll taken out here as the game does it.");
        }

        // Shown as another game has it: no physics of its own, nothing that pushes it, nothing it pushes.
        private static void Show(Body body)
        {
            if (body.Showing)
            {
                return;
            }

            body.Showing = true;
            for (int b = 0; b < body.Bones.Length; b++)
            {
                Rigidbody bone = body.Bones[b];

                // Unity's null check is also true for a destroyed object.
                if (bone != null)
                {
                    bone.isKinematic = true;
                    bone.detectCollisions = false;
                }
            }

            for (int m = 0; m < body.StillMachines.Count; m++)
            {
                PlayMakerFSM machine = body.StillMachines[m];

                // Unity's null check is also true for a destroyed object.
                if (machine != null && machine.enabled)
                {
                    body.Paused.Add(new PausedMachine(machine));
                }
            }
        }

        // Its own physics again, as the game had it.
        private void Free(Body body)
        {
            if (!body.Showing)
            {
                ReleasePushOut(body);
                return;
            }

            body.Showing = false;
            for (int b = 0; b < body.Bones.Length; b++)
            {
                Rigidbody bone = body.Bones[b];

                // Unity's null check is also true for a destroyed object.
                if (bone == null)
                {
                    continue;
                }

                bone.isKinematic = body.WasKinematic[b];
                bone.detectCollisions = true;
                if (VehiclePresenter.LimitPushOut)
                {
                    // Where another game had it, it may overlap what this game has there: pushed out slowly, not flung.
                    bone.maxDepenetrationVelocity = Mathf.Min(body.Depenetration[b], VehiclePresenter.StartDepenetration);
                }
            }

            body.PushOutUntil = clock + PushOutSeconds;
            for (int m = 0; m < body.Paused.Count; m++)
            {
                body.Paused[m].Resume();
            }

            body.Paused.Clear();
        }

        private void ReleasePushOut(Body body)
        {
            ReleasePushOut(body, false);
        }

        // The bones' push-out back to what the game had, once its time is over (or at once, <paramref name="now"/>).
        private void ReleasePushOut(Body body, bool now)
        {
            if (double.IsNaN(body.PushOutUntil) || (!now && clock < body.PushOutUntil))
            {
                return;
            }

            body.PushOutUntil = double.NaN;
            for (int b = 0; b < body.Bones.Length; b++)
            {
                // Unity's null check is also true for a destroyed object.
                if (body.Bones[b] != null)
                {
                    body.Bones[b].maxDepenetrationVelocity = body.Depenetration[b];
                }
            }
        }

        // A rag doll the mod took out goes back into its person, switched off, as it hung: the person is back here.
        private static void PutBack(Body body)
        {
            // Unity's null check is also true for a destroyed object.
            if (!body.TakenOut || body.Root == null || body.Parent == null)
            {
                return;
            }

            body.Root.parent = body.Parent;
            body.Root.localPosition = body.RootPlace;
            body.Root.localRotation = body.RootTurn;
            for (int b = 1; b < body.Bones.Length; b++)
            {
                if (body.Bones[b] != null)
                {
                    body.Bones[b].transform.localPosition = body.BonePlace[b];
                    body.Bones[b].transform.localRotation = body.BoneTurn[b];
                }
            }

            body.Root.gameObject.SetActive(false);
            body.TakenOut = false;
            body.LetGo = false;
        }

        // Now and then: a body whose owner left goes to the host; one that rests in a vehicle another game simulates goes to
        // that game, which carries it.
        private void Duties(NetSession session, byte me)
        {
            if (host)
            {
                present.Clear();
                IList<PlayerInfo> players = session.Players;
                for (int i = 0; i < players.Count; i++)
                {
                    present.Add(players[i].Id);
                }

                for (int i = 0; i < bodies.Count; i++)
                {
                    Body body = bodies[i];
                    if (body.Root != null && body.Claim.Known && body.Claim.Owner != me && !present.Contains(body.Claim.Owner))
                    {
                        Take(body, me, "player " + body.Claim.Owner + " left");
                    }
                }
            }

            ObjectReplication vehicleList = manager.Vehicles;
            for (int i = 0; i < bodies.Count && vehicleList != null; i++)
            {
                Body body = bodies[i];
                if (body.Root == null || !Mine(body, me) || !body.LetGo || body == HeldBy(HeldBody(null)) || !AtRest(body))
                {
                    continue;
                }

                ushort vehicle;
                Transform root;
                if (!vehicles.TryGetCarrier(body.Bones[0].position, out vehicle, out root))
                {
                    continue;
                }

                SharedBody shared = vehicleList.Find(vehicle);
                if (shared != null && shared.HasId && shared.Owner != me)
                {
                    Take(body, shared.Owner, "it rests in '" + root.name + "', which player " + shared.Owner + "'s game simulates");
                }
            }
        }

        private static bool Lost(Body body)
        {
            // Unity's null check is also true for a destroyed object.
            return body.Bones[0] != null && body.Bones[0].position.y < LostBelow;
        }

        private static bool AtRest(Body body)
        {
            for (int b = 0; b < body.Bones.Length; b++)
            {
                Rigidbody bone = body.Bones[b];

                // Unity's null check is also true for a destroyed object.
                if (bone != null && !bone.isKinematic && bone.velocity.sqrMagnitude > RestSpeed * RestSpeed)
                {
                    return false;
                }
            }

            return true;
        }

        // What the local player's hand drags now, or null. Pass the player once a frame; null asks again what it found.
        private Rigidbody HeldBody(Transform localPlayer)
        {
            if (localPlayer != null && !handSearched)
            {
                handSearched = true;
                handAt = localPlayer.Find(HandPath);
            }

            // Unity's null check is also true for a destroyed object.
            if (handAt == null)
            {
                return null;
            }

            if (hand == null)
            {
                // The game destroys the hand's joint when what it drags snags, and makes another.
                hand = handAt.GetComponent<Joint>();
                if (hand == null)
                {
                    return null;
                }
            }

            return hand.connectedBody;
        }

        // The body one of whose bones that is, or null.
        private Body HeldBy(Rigidbody bone)
        {
            Body body;

            // Unity's null check is also true for a destroyed object.
            return bone != null && byBone.TryGetValue(bone.GetInstanceID(), out body) ? body : null;
        }

        private Body Find(string name)
        {
            for (int i = 0; i < bodies.Count; i++)
            {
                if (bodies[i].Name == name)
                {
                    return bodies[i];
                }
            }

            return null;
        }

        private static BonePose Read(Transform at)
        {
            Vector3 place = at.position;
            Quaternion turn = at.rotation;
            return new BonePose(place.x, place.y, place.z, turn.x, turn.y, turn.z, turn.w);
        }

        private static string Place(Vector3 at)
        {
            return "(" + at.x.ToString("0.0", CultureInfo.InvariantCulture) + ", " + at.y.ToString("0.0", CultureInfo.InvariantCulture) + ", " + at.z.ToString("0.0", CultureInfo.InvariantCulture) + ")";
        }

        private static BodyDefinition[] Load(Logger log)
        {
            Assembly assembly = typeof(BodySync).Assembly;
            string[] resources = assembly.GetManifestResourceNames();
            var found = new List<BodyDefinition>();
            for (int i = 0; i < resources.Length; i++)
            {
                if (!resources[i].StartsWith("SummerSync.bodies.", StringComparison.Ordinal) || !resources[i].EndsWith(".json", StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    using (Stream stream = assembly.GetManifestResourceStream(resources[i]))
                    using (var reader = new StreamReader(stream))
                    {
                        found.AddRange(BodyDefinition.Parse(reader.ReadToEnd()));
                    }
                }
                catch (FormatException exception)
                {
                    log.Error(Category, "The bodies file '" + resources[i] + "' cannot be read; those bodies are each game's own. " + exception.Message);
                }
            }

            found.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            for (int i = found.Count - 1; i > 0; i--)
            {
                if (found[i].Name == found[i - 1].Name)
                {
                    log.Error(Category, "Two bodies are called '" + found[i].Name + "'; only the first is shared.");
                    found.RemoveAt(i);
                }
            }

            return found.ToArray();
        }

        // ---- The checksums' section ----

        private bool CheckReady
        {
            get { return current != null && sessionStarted; }
        }

        private void ReadCheck(List<CheckEntry> entries)
        {
            for (int i = 0; i < bodies.Count; i++)
            {
                Body body = bodies[i];

                // Unity's null check is also true for a destroyed object.
                if (!body.LetGo || !body.Claim.Known || body.Root == null || body.Bones[0] == null || Lost(body))
                {
                    continue;
                }

                Vector3 at = body.Bones[0].position;
                entries.Add(new CheckEntry(ChecksumReplication.KeyOf(body.OwnerKey), CheckPlace.Pack(at.x, at.y, at.z)));
            }
        }

        // A client: a body that lies elsewhere here goes to where the owner said; one that lies only in the host's game is
        // taken out now if its person is gone here.
        private void FixCheckHere(IList<ulong> different, IList<ulong> onlyHost)
        {
            for (int i = 0; i < bodies.Count; i++)
            {
                Body body = bodies[i];
                ulong key = ChecksumReplication.KeyOf(body.OwnerKey);
                if (different.Contains(key) && body.Showing)
                {
                    body.Snap = true;
                    log.Info(Category, "Bodies: '" + body.Name + "' lay elsewhere here than in the host's game: put where its owner has it.");
                }
                else if (onlyHost.Contains(key) && !body.LetGo && body.Claim.Known)
                {
                    TakeOutIfGone(body, true);
                }
            }
        }

        // The host: what it heard of a body, told to that client again.
        private void FixCheckFor(byte player, IList<ulong> keys)
        {
            for (int i = 0; i < bodies.Count; i++)
            {
                Body body = bodies[i];
                if (!keys.Contains(ChecksumReplication.KeyOf(body.OwnerKey)))
                {
                    continue;
                }

                current.ResendTo(player, body.OwnerKey);
                for (int b = 0; b < body.BoneKeys.Length; b++)
                {
                    current.ResendTo(player, body.BoneKeys[b]);
                }
            }
        }

        private struct Heard
        {
            public string Key;
            public int[] Values;
            public bool CatchUp;
        }

        private sealed class Body
        {
            public BodyDefinition Definition;
            public string Name;
            public string OwnerKey;
            public string[] BoneKeys;
            public Transform Root;
            public Transform Parent;
            public Vector3 RootPlace;
            public Quaternion RootTurn;
            public Transform Person;
            public Rigidbody[] Bones;
            public Vector3[] BonePlace;
            public Quaternion[] BoneTurn;
            public bool[] WasKinematic;
            public float[] Depenetration;
            public List<PlayMakerFSM> StillMachines = new List<PlayMakerFSM>();
            public List<PausedMachine> Paused = new List<PausedMachine>();
            public BodyClaim Claim = new BodyClaim();
            public bool LetGo;
            public bool MayClaim;
            public bool TakenOut;
            public bool Showing;
            public bool Waiting;
            public bool LostTold;
            public bool Snap;
            public int Asked;
            public double AskedAt;
            public double SaidAt = double.NaN;
            public bool SaidInCatchUp;
            public double NextSend;
            public double PushOutUntil = double.NaN;
            public BonePose[] Target;
            public int[] TargetGeneration;
            public bool[] HasTarget;
            public BonePose[] Sent;
            public bool[] HasSent;

            public static Body Make(BodyDefinition definition, Transform root, Transform person)
            {
                var body = new Body
                {
                    Definition = definition,
                    Name = definition.Name,
                    OwnerKey = BodyWire.OwnerKey(definition.Name),
                    Root = root,
                    Parent = root.parent,
                    RootPlace = root.localPosition,
                    RootTurn = root.localRotation,
                    Person = person,
                };

                // The rag doll's own body and those held to another by the joints of a rag doll: not what hangs from it
                // otherwise (a watch on a wrist).
                var bones = new List<Rigidbody>();
                Rigidbody[] all = root.GetComponentsInChildren<Rigidbody>(true);
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i].transform == root || all[i].GetComponent<CharacterJoint>() != null)
                    {
                        bones.Add(all[i]);
                    }
                }

                body.Bones = bones.ToArray();
                int count = body.Bones.Length;
                body.BoneKeys = new string[count];
                body.BonePlace = new Vector3[count];
                body.BoneTurn = new Quaternion[count];
                body.WasKinematic = new bool[count];
                body.Depenetration = new float[count];
                body.Target = new BonePose[count];
                body.TargetGeneration = new int[count];
                body.HasTarget = new bool[count];
                body.Sent = new BonePose[count];
                body.HasSent = new bool[count];
                for (int b = 0; b < count; b++)
                {
                    body.BoneKeys[b] = BodyWire.BoneKey(definition.Name, b);
                    body.BonePlace[b] = body.Bones[b].transform.localPosition;
                    body.BoneTurn[b] = body.Bones[b].transform.localRotation;
                    body.WasKinematic[b] = body.Bones[b].isKinematic;
                    body.Depenetration[b] = body.Bones[b].maxDepenetrationVelocity;
                }

                PlayMakerFSM[] machines = root.GetComponentsInChildren<PlayMakerFSM>(true);
                for (int i = 0; i < machines.Length; i++)
                {
                    if (Array.IndexOf(definition.Still, machines[i].FsmName) >= 0)
                    {
                        body.StillMachines.Add(machines[i]);
                    }
                }

                return body;
            }
        }

        private sealed class BodiesSection : ICheckSection
        {
            private readonly BodySync owner;

            public BodiesSection(BodySync owner)
            {
                this.owner = owner;
            }

            public byte Id
            {
                get { return CheckSections.Bodies; }
            }

            public string Name
            {
                get { return "bodies"; }
            }

            public bool Ready
            {
                get { return owner.CheckReady; }
            }

            public bool CountsMissing
            {
                get { return true; }
            }

            public bool SendsEntries
            {
                get { return true; }
            }

            public void Read(List<CheckEntry> entries)
            {
                owner.ReadCheck(entries);
            }

            public ulong Coarse(ulong value)
            {
                return value;
            }

            public bool Same(ulong host, ulong here)
            {
                return CheckPlace.Near(host, here, CheckTolerance);
            }

            public void FixHere(IList<ulong> different, IList<ulong> onlyHost, IList<ulong> onlyHere)
            {
                owner.FixCheckHere(different, onlyHost);
            }

            public void FixFor(byte player, IList<ulong> keys)
            {
                owner.FixCheckFor(player, keys);
            }
        }
    }
}
