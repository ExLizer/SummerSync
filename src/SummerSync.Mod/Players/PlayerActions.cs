using System;
using System.Globalization;
using HutongGames.PlayMaker;
using SummerSync.Core.Logging;
using SummerSync.Core.Players;
using UnityEngine;

namespace SummerSync.Unity.Players
{
    /// <summary>
    /// What the local player does with their hands that the others see on them: drinking, smoking, peeing; and the game's
    /// own things for it (what is drunk from, the cigarette, the breath of smoke, the stream and its sound), which the
    /// others' copies are given (<see cref="ActivityPose"/>).
    ///
    /// What is assumed about the game (dump of 2026-10-14 and the game on 2026-10-15; docs/GAME_NOTES.md, "Tomar, fumar y
    /// mear"): under the player's eyes ("FPSCamera/FPSCamera"), "Drink" has the machine "Drink", which while the player
    /// drinks sets the global "PlayerDrinkOn" and switches on "Drink/Hand" and, under it, what is drunk from
    /// ("BeerBottle", "HandJuice/JuiceCan"…); "Smoking" is switched on only while the player smokes, and its machine
    /// "Start" draws on the cigarette in "Inhale" and breathes the smoke out in "Outhale" (from "BreathSmoke"); under the
    /// camera ("FPSCamera"), "Piss/Fluid" is switched on only while the player pees (machine "Logic"), its own "Fluid"
    /// the stream, and "PLAYER/PeeSound" is its sound.
    /// </summary>
    internal sealed class PlayerActions
    {
        /// <summary>
        /// What can be drunk from, under "Drink/Hand"; the detail of <see cref="PlayerActivity.Drinking"/> is the place
        /// here plus one. Never reordered: it is what the other games read.
        /// </summary>
        public static readonly string[] Drinks =
        {
            "BeerBottle", "BoozeBottle", "SpiritBottle", "ShotGlass", "MilkGlass", "Coffee", "CoffeeGranny", "HandJuice/JuiceCan", "HandMilk/Milk",
            "HandCoffeeHome/CoffeeHome",
        };

        private const string EyesPath = "Pivot/AnimPivot/Camera/FPSCamera/FPSCamera";
        private const string DrinkHandPath = "Drink/Hand";
        private const string SmokingPath = "Smoking";
        private const string CigaretteName = "Cigarette";
        private const string BreathPath = "Smoking/BreathSmoke";
        private const string StreamPath = "Piss/Fluid";
        private const string StreamEmitterPath = "Piss/Fluid/Fluid";
        private const string PeeSoundPath = "PeeSound";
        private const string SmokingMachine = "Start";

        // The hand's clips that bring a drink up to the mouth and back (machine "Drink"): they move and turn only the hand,
        // which holds the drinks. The cups and the shot glass get the shorter one, the same move tipped less.
        private const string DrinkClip = "drink_rotate";
        private const string ShortDrinkClip = "drink_rotate_short";
        private static readonly string[] ShortDrinks = { "ShotGlass", "MilkGlass", "Coffee", "CoffeeGranny" };
        private const float SampleStep = 0.05f;

        // The states of "Start" with the cigarette at the lips, and with the smoke going out.
        private static readonly string[] Drawing = { "Anim 2", "Explosion?", "Gasoline nozzle", "Inhale" };
        private static readonly string[] Breathing = { "Anim 3", "Outhale", "Stop smoke", "Anim 4", "Explosion? 2", "Gasoline nozzle 2", "Outhale 2", "Stop smoke 2" };

        private static PlayerActions current;

        private readonly Transform drinkHand;
        private readonly Transform[] drinks;
        private readonly GameObject smoking;
        private readonly PlayMakerFSM smokingMachine;
        private readonly GameObject stream;
        private readonly FsmBool drinkOn;

        // For each drink, from the eyes: how the game turns it at the mouth and when the hand is down. Read once from the
        // game's own clip.
        private readonly Quaternion[] drinkTurn;
        private readonly Quaternion[] drinkRest;

        // And, in its own coordinates, the middle of the side of it that is up with the hand down: where it is drunk from
        // (the neck of a bottle, the rim of a cup).
        private readonly Vector3[] drinkLips;
        private string drinkPoseProblem;

        private PlayerActions(Transform player)
        {
            Transform eyes = player.Find(EyesPath);
            Transform camera = eyes == null ? null : eyes.parent;
            drinkHand = eyes == null ? null : eyes.Find(DrinkHandPath);
            drinks = new Transform[Drinks.Length];
            for (int i = 0; i < Drinks.Length; i++)
            {
                drinks[i] = drinkHand == null ? null : drinkHand.Find(Drinks[i]);
            }

            Transform smokingAt = eyes == null ? null : eyes.Find(SmokingPath);
            smoking = smokingAt == null ? null : smokingAt.gameObject;
            smokingMachine = smokingAt == null ? null : World.DoorSync.FindMachine(smokingAt, SmokingMachine);
            Cigarette = smokingAt == null ? null : FindBelow(smokingAt, CigaretteName);
            Transform breath = eyes == null ? null : eyes.Find(BreathPath);
            Breath = breath == null ? null : breath.GetComponent<ParticleEmitter>();
            BreathTurn = breath == null ? Quaternion.identity : Quaternion.Inverse(eyes.rotation) * breath.rotation;
            Transform streamAt = camera == null ? null : camera.Find(StreamPath);
            stream = streamAt == null ? null : streamAt.gameObject;
            Transform emitter = camera == null ? null : camera.Find(StreamEmitterPath);
            Stream = emitter == null ? null : emitter.GetComponent<ParticleEmitter>();
            StreamTurn = emitter == null ? Quaternion.identity : Quaternion.Inverse(camera.rotation) * emitter.rotation;
            Transform sound = player.Find(PeeSoundPath);
            PeeSound = sound == null ? null : sound.GetComponent<AudioSource>();
            drinkOn = FsmVariables.GlobalVariables.FindFsmBool("PlayerDrinkOn");
            drinkTurn = new Quaternion[Drinks.Length];
            drinkRest = new Quaternion[Drinks.Length];
            drinkLips = new Vector3[Drinks.Length];
            ReadDrinkPose(eyes);
        }

        /// <summary>The local player's, once found in this world; null before, or once it is gone.</summary>
        public static PlayerActions Current
        {
            get { return current != null && current.Alive ? current : null; }
        }

        /// <summary>The cigarette in the hand that smokes, to be copied; null if the game has none.</summary>
        public Transform Cigarette { get; private set; }

        /// <summary>The smoke breathed out, to be copied, and how it is turned from where the player looks.</summary>
        public ParticleEmitter Breath { get; private set; }

        public Quaternion BreathTurn { get; private set; }

        /// <summary>The stream, to be copied, and how it is turned from where the player looks (it runs along its -Y).</summary>
        public ParticleEmitter Stream { get; private set; }

        public Quaternion StreamTurn { get; private set; }

        public AudioSource PeeSound { get; private set; }

        // Unity's null check is also true for a destroyed object.
        private bool Alive
        {
            get { return drinkHand != null || smoking != null || stream != null; }
        }

        /// <summary>Finds them under the local player and says what was found. The other players' copies take them from here.</summary>
        public static PlayerActions Find(Transform player, Logger log)
        {
            current = new PlayerActions(player);
            int drinksFound = 0;
            for (int i = 0; i < current.drinks.Length; i++)
            {
                drinksFound += current.drinks[i] != null ? 1 : 0;
            }

            log.Info(
                LogCategory.Player,
                "What the others see this player do with their hands: drinking " + (current.drinkHand == null ? "NOT FOUND" : drinksFound + " of " + Drinks.Length + " drinks")
                + "; smoking " + (current.smoking == null ? "NOT FOUND" : current.Cigarette == null ? "found, without its cigarette" : "found") + Emitter(", breath", current.Breath)
                + "; peeing " + (current.stream == null ? "NOT FOUND" : "found") + Emitter(", stream", current.Stream)
                + (current.PeeSound == null ? ", no sound" : ", sound '" + (current.PeeSound.clip == null ? "none" : current.PeeSound.clip.name) + "' (loop " + current.PeeSound.loop
                   + ", volume " + Format(current.PeeSound.volume) + ")") + "; " + (current.drinkPoseProblem ?? "how each drink is held at the mouth read from the hand's clips") + ".");
            return current;
        }

        /// <summary>
        /// How the game holds what is drunk from with that detail, from the eyes: at the mouth, how it is turned, and where on
        /// it the lips go, in its own coordinates; with the hand down, how it is turned. False if that could not be read.
        /// </summary>
        public bool DrinkPose(int detail, out Quaternion atMouth, out Vector3 lips, out Quaternion down)
        {
            int i = detail - 1;
            bool known = drinkPoseProblem == null && i >= 0 && i < drinks.Length && drinks[i] != null;
            atMouth = known ? drinkTurn[i] : Quaternion.identity;
            lips = known ? drinkLips[i] : Vector3.zero;
            down = known ? drinkRest[i] : Quaternion.identity;
            return known;
        }

        /// <summary>What can be drunk from with that detail, to be copied; null if unknown.</summary>
        public Transform Drink(int detail)
        {
            return detail >= 1 && detail <= drinks.Length ? drinks[detail - 1] : null;
        }

        /// <summary>Writes into the pose what the player does now with their hands.</summary>
        public void Read(ref PlayerPose pose)
        {
            // Unity's null check is also true for a destroyed object.
            if (stream != null && stream.activeSelf)
            {
                pose.Activity = PlayerActivity.Peeing;
                pose.ActivityDetail = 0;
                return;
            }

            if ((drinkOn != null && drinkOn.Value) || (drinkHand != null && drinkHand.gameObject.activeSelf))
            {
                pose.Activity = PlayerActivity.Drinking;
                pose.ActivityDetail = 0;
                for (int i = 0; i < drinks.Length; i++)
                {
                    if (drinks[i] != null && drinks[i].gameObject.activeInHierarchy)
                    {
                        pose.ActivityDetail = (byte)(i + 1);
                        break;
                    }
                }

                return;
            }

            if (smoking != null && smoking.activeSelf)
            {
                pose.Activity = PlayerActivity.Smoking;
                string state = smokingMachine == null ? null : smokingMachine.ActiveStateName;
                pose.ActivityDetail = Array.IndexOf(Drawing, state) >= 0 ? PlayerPose.SmokeDrawing
                    : Array.IndexOf(Breathing, state) >= 0 ? PlayerPose.SmokeBreathing : PlayerPose.SmokeHolding;
            }
        }

        // The hand is off until the player drinks: its clips are sampled on it and it is put back as it was.
        private void ReadDrinkPose(Transform eyes)
        {
            Animation hand = drinkHand == null ? null : drinkHand.GetComponent<Animation>();
            if (hand == null || eyes == null)
            {
                drinkPoseProblem = "the hand's clips NOT FOUND: drinks are held as they come";
                return;
            }

            Vector3 place = drinkHand.localPosition;
            Quaternion turn = drinkHand.localRotation;
            try
            {
                drinkPoseProblem = SampleClip(hand, DrinkClip, false, eyes) ?? SampleClip(hand, ShortDrinkClip, true, eyes);
            }
            finally
            {
                drinkHand.localPosition = place;
                drinkHand.localRotation = turn;
            }
        }

        // For the drinks that clip is played with: how they are turned at its start, and at the mouth, where it turns the
        // hand the furthest from that. Returns what went wrong, or null.
        private string SampleClip(Animation hand, string name, bool shortClip, Transform eyes)
        {
            AnimationState state = hand[name];
            if (state == null || state.clip == null)
            {
                return "the hand's clip '" + name + "' NOT FOUND: drinks are held as they come";
            }

            AnimationClip clip = state.clip;
            clip.SampleAnimation(drinkHand.gameObject, 0f);
            Quaternion start = drinkHand.localRotation;
            for (int i = 0; i < drinks.Length; i++)
            {
                if (drinks[i] != null && (Array.IndexOf(ShortDrinks, Drinks[i]) >= 0) == shortClip)
                {
                    drinkRest[i] = Quaternion.Inverse(eyes.rotation) * drinks[i].rotation;
                    drinkLips[i] = TopSide(drinks[i], eyes.up);
                }
            }

            float furthest = 0f;
            float at = 0f;
            for (float t = 0f; t <= clip.length; t += SampleStep)
            {
                clip.SampleAnimation(drinkHand.gameObject, t);
                float angle = Quaternion.Angle(start, drinkHand.localRotation);
                if (angle > furthest)
                {
                    furthest = angle;
                    at = t;
                }
            }

            if (furthest < 1f)
            {
                return "the hand's clip '" + name + "' does not turn it: drinks are held as they come";
            }

            clip.SampleAnimation(drinkHand.gameObject, at);
            for (int i = 0; i < drinks.Length; i++)
            {
                if (drinks[i] != null && (Array.IndexOf(ShortDrinks, Drinks[i]) >= 0) == shortClip)
                {
                    drinkTurn[i] = Quaternion.Inverse(eyes.rotation) * drinks[i].rotation;
                }
            }

            return null;
        }

        // Of the six sides of a thing's mesh, the middle of the one that faces most that way, in the thing's coordinates.
        private static Vector3 TopSide(Transform thing, Vector3 up)
        {
            MeshFilter filter = thing.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null)
            {
                return Vector3.zero;
            }

            Bounds bounds = filter.sharedMesh.bounds;
            Vector3 best = bounds.center;
            float most = float.MinValue;
            for (int axis = 0; axis < 3; axis++)
            {
                for (int sign = -1; sign <= 1; sign += 2)
                {
                    Vector3 facing = Vector3.zero;
                    facing[axis] = sign;
                    float upness = Vector3.Dot(thing.TransformDirection(facing), up);
                    if (upness > most)
                    {
                        most = upness;
                        best = bounds.center;
                        best[axis] += sign * bounds.extents[axis];
                    }
                }
            }

            return best;
        }

        private static Transform FindBelow(Transform at, string name)
        {
            if (at.name == name)
            {
                return at;
            }

            for (int i = 0; i < at.childCount; i++)
            {
                Transform found = FindBelow(at.GetChild(i), name);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private static string Emitter(string what, ParticleEmitter emitter)
        {
            if (emitter == null)
            {
                return what + " NOT FOUND";
            }

            return what + " (emission " + Format(emitter.minEmission) + " to " + Format(emitter.maxEmission) + ", life " + Format(emitter.minEnergy) + " to "
                + Format(emitter.maxEnergy) + " s, size " + Format(emitter.minSize) + " to " + Format(emitter.maxSize) + ", velocity local " + emitter.localVelocity
                + " world " + emitter.worldVelocity + ", world space " + emitter.useWorldSpace + ")";
        }

        private static string Format(float value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
