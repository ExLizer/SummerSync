using System;
using System.Collections.Generic;
using SummerSync.Core.Json;

namespace SummerSync.Core.Vehicles
{
    /// <summary>
    /// A box in a vehicle's own coordinates: x to its right, y up, z to its front, metres from the origin of
    /// its root object.
    /// </summary>
    public struct VehicleBox
    {
        public float X;
        public float Y;
        public float Z;
        public float HalfX;
        public float HalfY;
        public float HalfZ;

        /// <summary>Whether the point is in the box grown by <paramref name="margin"/> on every side.</summary>
        public bool Contains(float x, float y, float z, float margin)
        {
            return Contains(x, y, z, margin, margin);
        }

        /// <summary>
        /// Whether the point is in the box grown by <paramref name="aside"/> to its sides, front and back, and by
        /// <paramref name="upAndDown"/> above and below.
        /// </summary>
        public bool Contains(float x, float y, float z, float aside, float upAndDown)
        {
            return Math.Abs(x - X) <= HalfX + aside && Math.Abs(y - Y) <= HalfY + upAndDown && Math.Abs(z - Z) <= HalfZ + aside;
        }
    }

    /// <summary>
    /// Something on a vehicle that a player turns or flips and that stays where it was put: the wiper switch,
    /// the volume of the radio. In the game it is a state machine with a variable that holds the position; to
    /// the session it is a whole number from 0 to <see cref="Highest"/>.
    /// </summary>
    public sealed class VehicleKnob
    {
        /// <summary>What the log calls it: "wipers".</summary>
        public string Name { get; internal set; }

        /// <summary>
        /// Path of the object with the state machine: below the vehicle's root, or below <see cref="Under"/> if it says
        /// one.
        /// </summary>
        public string Path { get; internal set; }

        /// <summary>
        /// For a knob on a part that is installed on the vehicle and taken off again (the Satsuma's dashboard): the name
        /// of that part's object, to be looked for below the vehicle's root once it is there; <see cref="Path"/> is below
        /// it. Null for a knob that is always on the vehicle.
        /// </summary>
        public string Under { get; internal set; }

        /// <summary>Name of that state machine.</summary>
        public string Machine { get; internal set; }

        /// <summary>Name of the variable that holds the position: a boolean, an integer, or a float if it has a <see cref="Step"/>.</summary>
        public string Variable { get; internal set; }

        /// <summary>
        /// The states that make the game show a position, by position. With fewer states than positions, the last
        /// one is for every position from there up. Empty for a knob that turns through a range and that the
        /// game shows straight from its variable, like the lever of a parking brake.
        /// </summary>
        public string[] States { get; internal set; }

        /// <summary>The highest position.</summary>
        public int Highest { get; internal set; }

        /// <summary>
        /// For a knob that turns through a range: how much of the float variable one position is, and what the
        /// variable is at position 0. A step of 0 is a knob whose variable is the position itself.
        /// </summary>
        public float Step { get; internal set; }

        public float From { get; internal set; }

        /// <summary>
        /// For a knob that keeps no variable of its own (the turn signals): by position, the states its machine is in while
        /// it is there. Null for a knob with a <see cref="Variable"/>.
        /// </summary>
        public string[][] During { get; internal set; }

        /// <summary>
        /// Whether only the machine that simulates the vehicle says where the knob is, because where it goes depends on
        /// something that is not shared (the turn signals go off as the driver's wheel comes back); the others show it and
        /// put it back if their copy goes elsewhere. False for a knob that is where the last one to touch it left it.
        /// </summary>
        public bool BySimulator { get; internal set; }

        /// <summary>
        /// Whether the state that shows a position flips the variable on the way there (the tractor's differential lock):
        /// the variable is written as the other position first.
        /// </summary>
        public bool Toggles { get; internal set; }

        /// <summary>What else of the game's has to have the position when it is shown, worked out from the knob's float. Never null.</summary>
        public KnobCopy[] Copies { get; internal set; }

        /// <summary>The object the game turns or moves by the knob's float when the player works it, or null.</summary>
        public KnobMove Moves { get; internal set; }

        /// <summary>For a knob read from its machine's state: the position at which the machine is in that state, or -1.</summary>
        public int PositionDuring(string state)
        {
            if (During == null || state == null)
            {
                return -1;
            }

            for (int position = 0; position < During.Length; position++)
            {
                string[] states = During[position];
                for (int i = 0; i < states.Length; i++)
                {
                    if (string.Equals(states[i], state, StringComparison.Ordinal))
                    {
                        return position;
                    }
                }
            }

            return -1;
        }

        /// <summary>How many two-state switches it takes to say a position.</summary>
        public int Bits
        {
            get
            {
                int bits = 1;
                while ((1 << bits) <= Highest)
                {
                    bits++;
                }

                return bits;
            }
        }

        /// <summary>The state that shows that position, or null if the game needs none.</summary>
        public string StateFor(int position)
        {
            return States.Length == 0 ? null : States[position < States.Length ? position : States.Length - 1];
        }

        /// <summary>The position that a value of the float variable is nearest to.</summary>
        public int PositionOf(float value)
        {
            int position = (int)Math.Floor(((value - From) / Step) + 0.5);
            return position < 0 ? 0 : position > Highest ? Highest : position;
        }

        /// <summary>What the float variable is at that position.</summary>
        public float ValueAt(int position)
        {
            return From + (position * Step);
        }
    }

    /// <summary>
    /// A variable of the game's that keeps the position of a knob over a range too, in its own units: a hand throttle's lever
    /// is one number for the lever and another, worked out from it, for the engine's idle. It is what the knob's float is,
    /// times <see cref="Times"/>, plus <see cref="Plus"/>, kept between <see cref="Min"/> and <see cref="Max"/>.
    /// </summary>
    public sealed class KnobCopy
    {
        /// <summary>Path of the object with the machine, below the same object as the knob's path; null for the knob's own object.</summary>
        public string Path { get; internal set; }

        /// <summary>Name of the state machine.</summary>
        public string Machine { get; internal set; }

        /// <summary>Name of its float variable.</summary>
        public string Variable { get; internal set; }

        public float Times { get; internal set; }

        public float Plus { get; internal set; }

        public float Min { get; internal set; }

        public float Max { get; internal set; }

        /// <summary>The copy's value for a value of the knob's float.</summary>
        public float Of(float value)
        {
            float copy = (value * Times) + Plus;
            return copy < Min ? Min : copy > Max ? Max : copy;
        }
    }

    /// <summary>
    /// An object that the game sets, by the knob's float, while the player works the knob and only then (the hand throttle's
    /// lever): one of its own angles, or one of its own coordinates, is the float times <see cref="Times"/> plus
    /// <see cref="Plus"/>, and the other two stay as they are.
    /// </summary>
    public sealed class KnobMove
    {
        /// <summary>Path of the object, below the same object as the knob's path.</summary>
        public string Path { get; internal set; }

        /// <summary>Whether it is moved along an axis of its own (true) or turned about one (false).</summary>
        public bool Slides { get; internal set; }

        /// <summary>Which axis: 0 x, 1 y, 2 z.</summary>
        public int Axis { get; internal set; }

        public float Times { get; internal set; }

        public float Plus { get; internal set; }

        /// <summary>The angle (degrees) or the coordinate for a value of the knob's float.</summary>
        public float Of(float value)
        {
            return (value * Times) + Plus;
        }
    }

    /// <summary>
    /// A vehicle's horn: an object that the game switches on while the horn sounds (its sound plays when it wakes up and
    /// loops), and the buttons whose state machines switch it on while a player holds the mouse on them.
    /// </summary>
    public sealed class VehicleHorn
    {
        /// <summary>Path, below the root, of the object that sounds while it is switched on.</summary>
        public string Path { get; internal set; }

        /// <summary>The buttons that sound it. Never empty.</summary>
        public VehicleHornButton[] Buttons { get; internal set; }
    }

    /// <summary>A button that sounds a horn: a state machine that is in one state while it is held.</summary>
    public sealed class VehicleHornButton
    {
        /// <summary>
        /// For a button on a part that is installed and taken off (the Satsuma's steering wheels), the name of the part's
        /// object, looked for below the root; null for a button that is always on the vehicle.
        /// </summary>
        public string Under { get; internal set; }

        /// <summary>Path of the object with the machine, below the root or the part; null for the part's own object.</summary>
        public string Path { get; internal set; }

        public string Machine { get; internal set; }

        /// <summary>The state the machine is in while the button is held.</summary>
        public string Pressed { get; internal set; }
    }

    /// <summary>
    /// A trailer hitch on a vehicle: where the game hitches another vehicle (the tractor's flatbed) when it comes near
    /// enough, and the click that lets it go. A state machine on the towing vehicle says with a boolean whether it is
    /// hitched; the game hitches by a state of that machine and unhitches by a state of another one.
    /// </summary>
    public sealed class VehicleHitch
    {
        /// <summary>Name of the root object of what is towed (a vehicle of its own: it has a data file too).</summary>
        public string Towed { get; internal set; }

        /// <summary>Path, below the towing vehicle's root, of the object with the machine that hitches.</summary>
        public string Path { get; internal set; }

        public string Machine { get; internal set; }

        /// <summary>Its boolean, true while hitched.</summary>
        public string Variable { get; internal set; }

        /// <summary>The state of that machine that hitches what is near, from where the game goes on by itself.</summary>
        public string Attach { get; internal set; }

        /// <summary>
        /// Path, below the towed vehicle's root, of the point that meets the hitch: the game hitches where the two are when it
        /// does (its joint takes that as rest), so another game hitches only once its copies are there too.
        /// </summary>
        public string TowedPoint { get; internal set; }

        /// <summary>Path, below the towing vehicle's root, of the object with the machine whose click unhitches.</summary>
        public string ReleasePath { get; internal set; }

        public string ReleaseMachine { get; internal set; }

        /// <summary>The state of that machine that unhitches.</summary>
        public string Release { get; internal set; }
    }

    /// <summary>
    /// A hydraulic lever that moves part of the vehicle while it is held (the tractor's front loader): a state machine with
    /// a float that is the part's angle, a state for each way the lever is held, in which the game moves the float at a pace
    /// set by the engine and turns the part by it, and states in which nobody holds it. Where the part is depends on how
    /// long the lever was held and how fast the engine ran: the machine that simulates the vehicle says it.
    /// </summary>
    public sealed class VehicleHydraulic
    {
        /// <summary>What the log and the session call it: "loader".</summary>
        public string Name { get; internal set; }

        /// <summary>Path, below the root, of the object with the machine.</summary>
        public string Path { get; internal set; }

        public string Machine { get; internal set; }

        /// <summary>Its float: the part's angle.</summary>
        public string Variable { get; internal set; }

        /// <summary>The least and the most the game lets the float be.</summary>
        public float Min { get; internal set; }

        public float Max { get; internal set; }

        /// <summary>The states in which nobody holds the lever. Never empty.</summary>
        public string[] Rest { get; internal set; }

        /// <summary>The states in which the lever is held one way and the other.</summary>
        public string Up { get; internal set; }

        public string Down { get; internal set; }

        /// <summary>The state that plays the lever's sound and goes back to rest.</summary>
        public string Sound { get; internal set; }

        /// <summary>The part the game turns by the float.</summary>
        public KnobMove Moves { get; internal set; }

        /// <summary>Path, below the root, of the lever, and its angle about <see cref="LeverAxis"/> held up and down (0 at rest).</summary>
        public string LeverPath { get; internal set; }

        public int LeverAxis { get; internal set; }

        public float LeverUp { get; internal set; }

        public float LeverDown { get; internal set; }

        /// <summary>The lever at a state of its machine: 1 held up, 2 held down, 0 at rest, -1 in none of those.</summary>
        public int LeverDuring(string state)
        {
            if (string.Equals(state, Up, StringComparison.Ordinal))
            {
                return 1;
            }

            if (string.Equals(state, Down, StringComparison.Ordinal))
            {
                return 2;
            }

            for (int i = 0; i < Rest.Length; i++)
            {
                if (string.Equals(state, Rest[i], StringComparison.Ordinal))
                {
                    return 0;
                }
            }

            return -1;
        }
    }

    /// <summary>What a driver works with a foot or a hand besides the wheel.</summary>
    public enum DriverInput
    {
        Clutch,
        Brake,
        Throttle,
    }

    /// <summary>
    /// A pedal, or a lever on the handlebar, that the game turns by how far the driver presses it: its state
    /// machine reads the controller's input every frame and sets one angle of the object to the input times a
    /// number of degrees, keeping the other two.
    /// </summary>
    public sealed class VehiclePedal
    {
        public DriverInput Input { get; internal set; }

        /// <summary>Path of the object that turns, below the vehicle's root.</summary>
        public string Path { get; internal set; }

        /// <summary>Which of its own angles turns: 0 about x, 1 about y, 2 about z.</summary>
        public int Axis { get; internal set; }

        /// <summary>That angle with the input at 1 (fully down); with the input at 0 it is 0.</summary>
        public float Degrees { get; internal set; }
    }

    /// <summary>How a passenger seat's belt is put on, as the driver's of the same vehicle is.</summary>
    public enum SeatBelt : byte
    {
        /// <summary>The seat has no belt.</summary>
        None = 0,

        /// <summary>The handle is taken from the pillar and pulled down to the lock (the van, the Gifu, the Satsuma).</summary>
        Handle = 1,

        /// <summary>One click on the buckle fastens it and another unfastens it (the Ferndale).</summary>
        Buckle = 2,
    }

    /// <summary>
    /// A seat a passenger can sit in, as the game seats a driver: the player is hung from the vehicle where the seat is,
    /// does not walk, and looks around. Everything is in the vehicle's own coordinates.
    /// </summary>
    public sealed class VehicleSeat
    {
        /// <summary>What the log calls it: "front", "rear left".</summary>
        public string Name { get; internal set; }

        /// <summary>Where the player's root object sits (x, y, z), as the game leaves a driver's at the wheel.</summary>
        public float[] Player { get; internal set; }

        /// <summary>How near the seat, across, a player has to stand to be offered it.</summary>
        public float Reach { get; internal set; }

        /// <summary>
        /// Paths, below the root, of which one has to have something under it for the seat to be there (the Satsuma's
        /// seats are parts, put on their pivots). Empty for a seat that is always there. Never null.
        /// </summary>
        public string[] Needs { get; internal set; }

        public SeatBelt Belt { get; internal set; }

        /// <summary>
        /// For a belt: path, below the root, of the driver's belt that this one is the mirror image of across the
        /// vehicle. Null without a belt.
        /// </summary>
        public string BeltPath { get; internal set; }
    }

    /// <summary>
    /// What the mod has to know about one kind of vehicle to share it. It comes from a file in data/vehicles,
    /// and every name and number in it from the game itself (docs/GAME_NOTES.md says where each was read).
    /// </summary>
    public sealed class VehicleDefinition
    {
        /// <summary>The format this code reads. A file of another format is refused.</summary>
        public const int Format = 2;

        /// <summary>A short name for people to type, in lower case: "van".</summary>
        public string Nick { get; private set; }

        /// <summary>Name of the vehicle's root object in the scene.</summary>
        public string RootName { get; private set; }

        /// <summary>
        /// Path, below the root, of the object whose state machine offers driving mode. Null for something that is towed
        /// and never driven (the tractor's flatbed).
        /// </summary>
        public string DriveTriggerPath { get; private set; }

        /// <summary>Name of that state machine; null with no drive trigger.</summary>
        public string DriveMachineName { get; private set; }

        /// <summary>Whether a player can drive it: it has a drive trigger.</summary>
        public bool CanBeDriven
        {
            get { return DriveTriggerPath != null; }
        }

        /// <summary>The horn, or null for a vehicle without one.</summary>
        public VehicleHorn Horn { get; private set; }

        /// <summary>Its trailer hitches. Never null.</summary>
        public VehicleHitch[] Hitches { get; private set; }

        /// <summary>Its hydraulic levers. Never null.</summary>
        public VehicleHydraulic[] Hydraulics { get; private set; }

        /// <summary>
        /// Name of the game's global variable that says the player has the key: the ignition wants it to be 1.
        /// Null for a vehicle that needs no key.
        /// </summary>
        public string KeyVariable { get; private set; }

        /// <summary>Whether there is a space inside where someone can ride and things can lie.</summary>
        public bool HasInterior { get; private set; }

        /// <summary>
        /// That space: whoever is in it and not at the wheel rides the vehicle. Measured to the origin of the
        /// player's object, which is a little above their feet.
        /// </summary>
        public VehicleBox Interior { get; private set; }

        /// <summary>
        /// How far out of <see cref="Interior"/> a rider has to be before they count as having got off. Without
        /// it, someone standing in the doorway would get on and off on every frame.
        /// </summary>
        public float ExitMargin { get; private set; }

        /// <summary>
        /// The doors that open the space inside to the outside (paths below the root, to the object whose state machine
        /// "Use" has the boolean "Open"): what smoke in it goes out by. One that is not there (a door taken off) is a hole
        /// that is always open. Empty for a vehicle without that space or without doors.
        /// </summary>
        public string[] CabinVents { get; private set; }

        /// <summary>
        /// The game's state machines that move the vehicle by themselves ("path::machine", the path below the root, empty
        /// for the root itself: the boat's floating and its bow), paused on a machine that shows the vehicle where another
        /// machine simulates it. Empty for most.
        /// </summary>
        public string[] FollowOff { get; private set; }

        /// <summary>
        /// Bodies below the root (paths) held to it by a joint that carry the vehicle's colliders (the boat's hull), made
        /// kinematic on a machine that shows the vehicle where another machine simulates it: dragged by their joint they
        /// would come a step behind, and whoever stands on them would sink through. Empty for most.
        /// </summary>
        public string[] FollowKinematic { get; private set; }

        /// <summary>
        /// Path, below the root, of the object with the headlight switch: a state machine with an integer
        /// "Selection" (0 off, 1 dipped, 2 full) and the states "Off", "Shorts" and, on most, "Longs". Null if
        /// the lights are not shared.
        /// </summary>
        public string LightSwitchPath { get; private set; }

        /// <summary>Name of that state machine.</summary>
        public string LightSwitchMachine { get; private set; }

        /// <summary>
        /// For a light switch on an installable part: the part object's name, found below the root once installed;
        /// <see cref="LightSwitchPath"/> is then below it. Null if the switch is always on the vehicle.
        /// </summary>
        public string LightSwitchUnder { get; private set; }

        /// <summary>
        /// Path, below the root, of the object the game switches on while the vehicle has electricity; the lamps
        /// hang from it. Null for a vehicle whose lamps do not depend on one.
        /// </summary>
        public string PowerPath { get; private set; }

        /// <summary>
        /// Path, below the root, of the object with the state machine that runs the engine: it has the states
        /// "Start engine" and "Motor running" and a boolean "ShutOff". Null if the engine is not shared.
        /// </summary>
        public string StarterPath { get; private set; }

        /// <summary>Name of that state machine.</summary>
        public string StarterMachine { get; private set; }

        /// <summary>
        /// The starter's state while the engine runs: "Motor running" on most vehicles, "Running" on the Satsuma. Never
        /// null when <see cref="StarterPath"/> is set.
        /// </summary>
        public string RunningState { get; private set; }

        /// <summary>The running state of every vehicle whose file does not name one.</summary>
        public const string DefaultRunningState = "Motor running";

        /// <summary>
        /// Path, below the root, of the object with the ignition lock: a state machine with the booleans "ACC"
        /// and "MotorOn" and the state "Motor OFF". Null for a vehicle that is started without a key.
        /// </summary>
        public string IgnitionPath { get; private set; }

        /// <summary>Name of that state machine. Null with no ignition lock.</summary>
        public string IgnitionMachine { get; private set; }

        /// <summary>
        /// Path, below the root, of the object the game switches off while the player is far from the vehicle;
        /// the dashboard hangs from it. Null if there is none.
        /// </summary>
        public string DetailPath { get; private set; }

        /// <summary>The knobs and switches on it that are shared. Never null.</summary>
        public VehicleKnob[] Knobs { get; private set; }

        /// <summary>The pedals (or levers on the handlebar) a driver presses. Never null.</summary>
        public VehiclePedal[] Pedals { get; private set; }

        /// <summary>
        /// Paths, below the root, of the gear levers a driver's hand goes to when changing gear: the first one there and
        /// switched on is the one (the Satsuma's depends on its parts). Empty for a vehicle the driver does not shift by
        /// hand. Never null.
        /// </summary>
        public string[] Levers { get; private set; }

        /// <summary>
        /// For a vehicle steered with a handlebar: the paths, below the root, of what the left and the right hand hold.
        /// Null for a steering wheel, which the game turns with its own component and which is found by it.
        /// </summary>
        public string[] Grips { get; private set; }

        /// <summary>
        /// Where a driver's feet are in the vehicle's own coordinates and how high above them their eyes are
        /// (x, y, z, eyes), as the game seats the player at the wheel. Null if not measured. Only test players
        /// without a game use it (netprobe): a real one says where they sit.
        /// </summary>
        public float[] Seat { get; private set; }

        /// <summary>
        /// What the driver's head holds in a crash, in newtons, as the game sets its joint: either "HeadForce" (the base, plus
        /// the belt while it is on, the lot times <see cref="CrashHelmetTimes"/> with a helmet) or "HelmetSim" (the base, or
        /// <see cref="CrashHelmet"/> with a helmet, and no belt). 0 if the file does not say.
        /// </summary>
        public float CrashHead { get; private set; }

        /// <summary>What a belt adds to <see cref="CrashHead"/>; 0 for a vehicle whose belt does not count.</summary>
        public float CrashBelt { get; private set; }

        /// <summary>What a helmet multiplies the head and the belt by ("HeadForce"); 0 if a helmet sets <see cref="CrashHelmet"/>.</summary>
        public float CrashHelmetTimes { get; private set; }

        /// <summary>What the head holds with a helmet ("HelmetSim"), whatever the belt; 0 if a helmet multiplies.</summary>
        public float CrashHelmet { get; private set; }

        /// <summary>What a head in this vehicle holds before it breaks off, as the game reckons a driver's; 0 if not known.</summary>
        public float HeadHolds(bool belted, bool helmet)
        {
            if (CrashHead <= 0f)
            {
                return 0f;
            }

            if (CrashHelmet > 0f)
            {
                return helmet ? CrashHelmet : CrashHead;
            }

            float force = CrashHead + (belted ? CrashBelt : 0f);
            return helmet ? force * CrashHelmetTimes : force;
        }

        /// <summary>The seats a passenger can sit in, in the order a player's state numbers them from 1. Never null.</summary>
        public VehicleSeat[] Seats { get; private set; }

        /// <summary>How near a seat a player stands to be offered it, if its file does not say.</summary>
        public const float DefaultSeatReach = 0.45f;

        /// <exception cref="FormatException">The text is not a vehicle definition. The message says why.</exception>
        public static VehicleDefinition Parse(string json)
        {
            Dictionary<string, object> file = JsonReader.Object(JsonReader.Parse(json), "A vehicle file");
            double format = JsonReader.Number(file, "format", "A vehicle file");
            if (format != Format)
            {
                throw new FormatException("A vehicle file of format " + format + " cannot be read; this build reads format " + Format + ".");
            }

            var definition = new VehicleDefinition();
            definition.RootName = JsonReader.String(file, "rootName", "A vehicle file");
            string what = "The vehicle '" + definition.RootName + "'";
            definition.Nick = JsonReader.String(file, "nick", what);
            definition.DriveTriggerPath = Optional(file, "driveTriggerPath", what);
            definition.DriveMachineName = Optional(file, "driveMachineName", what);
            if ((definition.DriveTriggerPath == null) != (definition.DriveMachineName == null))
            {
                throw new FormatException(what + " names a drive trigger without its state machine, or the other way round.");
            }

            definition.KeyVariable = Optional(file, "keyVariable", what);
            definition.ExitMargin = file.ContainsKey("exitMargin") ? (float)JsonReader.Number(file, "exitMargin", what) : 0.25f;
            if (definition.RootName.Length == 0 || definition.Nick.Length == 0 || definition.Nick != definition.Nick.ToLowerInvariant()
                || definition.Nick.IndexOf(' ') >= 0)
            {
                throw new FormatException(what + " needs a name, and a nick in lower case without spaces.");
            }

            if (definition.ExitMargin < 0)
            {
                throw new FormatException(what + " has a negative exit margin.");
            }

            object interior;
            if (file.TryGetValue("interior", out interior) && interior != null)
            {
                Dictionary<string, object> box = JsonReader.Object(interior, what + "'s \"interior\"");
                double[] centre = JsonReader.Numbers(box, "centre", 3, what + "'s interior");
                double[] half = JsonReader.Numbers(box, "halfSize", 3, what + "'s interior");
                if (half[0] <= 0 || half[1] <= 0 || half[2] <= 0)
                {
                    throw new FormatException(what + "'s interior has no size.");
                }

                definition.HasInterior = true;
                definition.Interior = new VehicleBox
                {
                    X = (float)centre[0],
                    Y = (float)centre[1],
                    Z = (float)centre[2],
                    HalfX = (float)half[0],
                    HalfY = (float)half[1],
                    HalfZ = (float)half[2],
                };
            }

            object lights;
            if (file.TryGetValue("lights", out lights) && lights != null)
            {
                Dictionary<string, object> entry = JsonReader.Object(lights, what + "'s \"lights\"");
                definition.LightSwitchPath = JsonReader.String(entry, "switchPath", what + "'s lights");
                definition.LightSwitchMachine = JsonReader.String(entry, "switchMachine", what + "'s lights");
                definition.LightSwitchUnder = Optional(entry, "switchUnder", what + "'s lights");
                definition.PowerPath = Optional(entry, "powerPath", what + "'s lights");
            }

            object engine;
            if (file.TryGetValue("engine", out engine) && engine != null)
            {
                Dictionary<string, object> entry = JsonReader.Object(engine, what + "'s \"engine\"");
                definition.StarterPath = JsonReader.String(entry, "starterPath", what + "'s engine");
                definition.StarterMachine = JsonReader.String(entry, "starterMachine", what + "'s engine");
                definition.RunningState = Optional(entry, "runningState", what + "'s engine") ?? DefaultRunningState;
                definition.IgnitionPath = Optional(entry, "ignitionPath", what + "'s engine");
                definition.IgnitionMachine = Optional(entry, "ignitionMachine", what + "'s engine");
                definition.DetailPath = Optional(entry, "detailPath", what + "'s engine");
                if ((definition.IgnitionPath == null) != (definition.IgnitionMachine == null))
                {
                    throw new FormatException(what + "'s engine names an ignition lock without its state machine, or the other way round.");
                }
            }

            var knobs = new List<VehicleKnob>();
            object listed;
            if (file.TryGetValue("knobs", out listed) && listed != null)
            {
                List<object> entries = JsonReader.Array(listed, what + "'s \"knobs\"");
                for (int i = 0; i < entries.Count; i++)
                {
                    knobs.Add(ParseKnob(JsonReader.Object(entries[i], what + "'s knob " + (i + 1)), what, knobs));
                }
            }

            definition.Knobs = knobs.ToArray();
            definition.Horn = ParseHorn(file, what);
            definition.Hitches = ParseHitches(file, what);
            definition.Hydraulics = ParseHydraulics(file, what);
            ParseDriver(file, what, definition);
            ParseSeats(file, what, definition);
            ParseCrash(file, what, definition);
            definition.FollowOff = Texts(file, "followOff", what);
            definition.CabinVents = Texts(file, "cabinVents", what);
            if (definition.CabinVents.Length > 0 && !definition.HasInterior)
            {
                throw new FormatException(what + " has doors to its space inside (\"cabinVents\") but no \"interior\".");
            }
            definition.FollowKinematic = Texts(file, "followKinematic", what);
            for (int i = 0; i < definition.FollowOff.Length; i++)
            {
                if (definition.FollowOff[i].IndexOf("::", StringComparison.Ordinal) < 0 || definition.FollowOff[i].EndsWith("::", StringComparison.Ordinal))
                {
                    throw new FormatException(what + "'s \"followOff\" has '" + definition.FollowOff[i] + "': it needs path::machine (an empty path for the root).");
                }
            }

            return definition;
        }

        private static void ParseCrash(Dictionary<string, object> file, string vehicle, VehicleDefinition definition)
        {
            object crash;
            if (!file.TryGetValue("crash", out crash) || crash == null)
            {
                return;
            }

            string what = vehicle + "'s crash";
            Dictionary<string, object> entry = JsonReader.Object(crash, vehicle + "'s \"crash\"");
            definition.CrashHead = (float)JsonReader.Number(entry, "head", what);
            definition.CrashBelt = entry.ContainsKey("belt") ? (float)JsonReader.Number(entry, "belt", what) : 0f;
            definition.CrashHelmetTimes = entry.ContainsKey("helmetTimes") ? (float)JsonReader.Number(entry, "helmetTimes", what) : 0f;
            definition.CrashHelmet = entry.ContainsKey("helmet") ? (float)JsonReader.Number(entry, "helmet", what) : 0f;
            if (definition.CrashHead <= 0f || definition.CrashBelt < 0f || (definition.CrashHelmetTimes > 0f) == (definition.CrashHelmet > 0f)
                || (definition.CrashHelmet > 0f && definition.CrashBelt > 0f))
            {
                throw new FormatException(what + " needs a head above 0 N, and either a helmet's times (with a belt or not) or a helmet's newtons (without a belt).");
            }
        }

        private static void ParseSeats(Dictionary<string, object> file, string vehicle, VehicleDefinition definition)
        {
            var seats = new List<VehicleSeat>();
            object listed;
            if (file.TryGetValue("seats", out listed) && listed != null)
            {
                List<object> entries = JsonReader.Array(listed, vehicle + "'s \"seats\"");
                if (entries.Count > 15)
                {
                    throw new FormatException(vehicle + " has more than 15 seats.");
                }

                for (int i = 0; i < entries.Count; i++)
                {
                    string what = vehicle + "'s seat " + (i + 1);
                    Dictionary<string, object> one = JsonReader.Object(entries[i], what);
                    double[] player = JsonReader.Numbers(one, "player", 3, what);
                    var seat = new VehicleSeat
                    {
                        Name = JsonReader.String(one, "name", what),
                        Player = new[] { (float)player[0], (float)player[1], (float)player[2] },
                        Reach = one.ContainsKey("reach") ? (float)JsonReader.Number(one, "reach", what) : DefaultSeatReach,
                        Needs = Texts(one, "needs", what),
                        BeltPath = Optional(one, "beltPath", what),
                    };
                    string belt = Optional(one, "belt", what) ?? "none";
                    seat.Belt = belt == "handle" ? SeatBelt.Handle : belt == "buckle" ? SeatBelt.Buckle : belt == "none" ? SeatBelt.None : (SeatBelt)255;
                    if (seat.Belt == (SeatBelt)255)
                    {
                        throw new FormatException(what + " has a belt '" + belt + "'; it can be handle, buckle or none.");
                    }

                    if (seat.Name.Length == 0 || seat.Reach <= 0f || seat.Reach > 2f)
                    {
                        throw new FormatException(what + " needs a name and a reach from above 0 to 2 m.");
                    }

                    if ((seat.Belt == SeatBelt.None) != (seat.BeltPath == null))
                    {
                        throw new FormatException(what + " names a belt without the driver's it copies, or the other way round.");
                    }

                    for (int j = 0; j < seats.Count; j++)
                    {
                        if (seats[j].Name == seat.Name)
                        {
                            throw new FormatException(vehicle + " has two seats called '" + seat.Name + "'.");
                        }
                    }

                    seats.Add(seat);
                }
            }

            definition.Seats = seats.ToArray();
        }

        private static void ParseDriver(Dictionary<string, object> file, string vehicle, VehicleDefinition definition)
        {
            definition.Pedals = new VehiclePedal[0];
            definition.Levers = new string[0];
            object driver;
            if (!file.TryGetValue("driver", out driver) || driver == null)
            {
                return;
            }

            string what = vehicle + "'s driver";
            Dictionary<string, object> entry = JsonReader.Object(driver, vehicle + "'s \"driver\"");
            var pedals = new List<VehiclePedal>();
            object listed;
            if (entry.TryGetValue("pedals", out listed) && listed != null)
            {
                List<object> entries = JsonReader.Array(listed, what + "'s \"pedals\"");
                for (int i = 0; i < entries.Count; i++)
                {
                    Dictionary<string, object> one = JsonReader.Object(entries[i], what + "'s pedal " + (i + 1));
                    string pedal = what + "'s pedal " + (i + 1);
                    var made = new VehiclePedal
                    {
                        Input = InputNamed(JsonReader.String(one, "input", pedal), pedal),
                        Path = JsonReader.String(one, "path", pedal),
                        Axis = AxisNamed(JsonReader.String(one, "axis", pedal), pedal),
                        Degrees = (float)JsonReader.Number(one, "degrees", pedal),
                    };
                    if (made.Path.Length == 0 || made.Degrees == 0f || Math.Abs(made.Degrees) > 180f)
                    {
                        throw new FormatException(pedal + " needs a path and from 1 to 180 degrees either way.");
                    }

                    for (int j = 0; j < pedals.Count; j++)
                    {
                        if (pedals[j].Input == made.Input)
                        {
                            throw new FormatException(what + " has two pedals for the " + made.Input.ToString().ToLowerInvariant() + ".");
                        }
                    }

                    pedals.Add(made);
                }
            }

            definition.Pedals = pedals.ToArray();
            definition.Levers = Texts(entry, "levers", what);
            string[] grips = Texts(entry, "grips", what);
            if (grips.Length != 0 && grips.Length != 2)
            {
                throw new FormatException(what + " needs two grips, the left one first, or none.");
            }

            definition.Grips = grips.Length == 2 ? grips : null;
            if (entry.ContainsKey("seat"))
            {
                double[] seat = JsonReader.Numbers(entry, "seat", 4, what);
                if (seat[3] <= 0)
                {
                    throw new FormatException(what + "'s seat needs the eyes above the feet.");
                }

                definition.Seat = new[] { (float)seat[0], (float)seat[1], (float)seat[2], (float)seat[3] };
            }
        }

        private static DriverInput InputNamed(string name, string what)
        {
            switch (name)
            {
                case "clutch":
                    return DriverInput.Clutch;
                case "brake":
                    return DriverInput.Brake;
                case "throttle":
                    return DriverInput.Throttle;
                default:
                    throw new FormatException(what + " has an input '" + name + "'; it can be clutch, brake or throttle.");
            }
        }

        private static int AxisNamed(string name, string what)
        {
            int axis = name == "x" ? 0 : name == "y" ? 1 : name == "z" ? 2 : -1;
            if (axis < 0)
            {
                throw new FormatException(what + " has an axis '" + name + "'; it can be x, y or z.");
            }

            return axis;
        }

        // A list of texts that may be missing; none of them empty.
        private static string[] Texts(Dictionary<string, object> parent, string name, string what)
        {
            object value;
            if (!parent.TryGetValue(name, out value) || value == null)
            {
                return new string[0];
            }

            List<object> entries = JsonReader.Array(value, what + "'s \"" + name + "\"");
            var texts = new string[entries.Count];
            for (int i = 0; i < entries.Count; i++)
            {
                texts[i] = entries[i] as string;
                if (string.IsNullOrEmpty(texts[i]))
                {
                    throw new FormatException(what + "'s \"" + name + "\" has something that is not a path.");
                }
            }

            return texts;
        }

        private static VehicleHorn ParseHorn(Dictionary<string, object> file, string vehicle)
        {
            object horn;
            if (!file.TryGetValue("horn", out horn) || horn == null)
            {
                return null;
            }

            string what = vehicle + "'s horn";
            Dictionary<string, object> entry = JsonReader.Object(horn, vehicle + "'s \"horn\"");
            var made = new VehicleHorn { Path = JsonReader.String(entry, "path", what) };
            object listed;
            List<object> entries = entry.TryGetValue("buttons", out listed) && listed != null ? JsonReader.Array(listed, what + "'s \"buttons\"") : new List<object>();
            if (made.Path.Length == 0 || entries.Count == 0)
            {
                throw new FormatException(what + " needs the path of what sounds and at least one button.");
            }

            made.Buttons = new VehicleHornButton[entries.Count];
            for (int i = 0; i < entries.Count; i++)
            {
                string button = what + "'s button " + (i + 1);
                Dictionary<string, object> one = JsonReader.Object(entries[i], button);
                made.Buttons[i] = new VehicleHornButton
                {
                    Under = Optional(one, "under", button),
                    Path = Optional(one, "path", button),
                    Machine = JsonReader.String(one, "machine", button),
                    Pressed = JsonReader.String(one, "pressed", button),
                };
                if ((made.Buttons[i].Path == null && made.Buttons[i].Under == null) || made.Buttons[i].Machine.Length == 0 || made.Buttons[i].Pressed.Length == 0)
                {
                    throw new FormatException(button + " needs a path (or a part it is on), a state machine and the state it is in while held.");
                }
            }

            return made;
        }

        private static VehicleHydraulic[] ParseHydraulics(Dictionary<string, object> file, string vehicle)
        {
            object listed;
            if (!file.TryGetValue("hydraulics", out listed) || listed == null)
            {
                return new VehicleHydraulic[0];
            }

            List<object> entries = JsonReader.Array(listed, vehicle + "'s \"hydraulics\"");
            var hydraulics = new VehicleHydraulic[entries.Count];
            for (int i = 0; i < entries.Count; i++)
            {
                string what = vehicle + "'s hydraulic lever " + (i + 1);
                Dictionary<string, object> one = JsonReader.Object(entries[i], what);
                Dictionary<string, object> lever = JsonReader.Object(one.ContainsKey("lever") ? one["lever"] : null, what + "'s \"lever\"");
                var made = new VehicleHydraulic
                {
                    Name = JsonReader.String(one, "name", what),
                    Path = JsonReader.String(one, "path", what),
                    Machine = JsonReader.String(one, "machine", what),
                    Variable = JsonReader.String(one, "variable", what),
                    Min = (float)JsonReader.Number(one, "min", what),
                    Max = (float)JsonReader.Number(one, "max", what),
                    Rest = Texts(one, "rest", what),
                    Up = JsonReader.String(one, "up", what),
                    Down = JsonReader.String(one, "down", what),
                    Sound = JsonReader.String(one, "sound", what),
                    Moves = ParseMove(one, what),
                    LeverPath = JsonReader.String(lever, "path", what + "'s lever"),
                    LeverAxis = AxisNamed(JsonReader.String(lever, "axis", what + "'s lever"), what + "'s lever"),
                    LeverUp = (float)JsonReader.Number(lever, "up", what + "'s lever"),
                    LeverDown = (float)JsonReader.Number(lever, "down", what + "'s lever"),
                };
                if (made.Name.Length == 0 || made.Path.Length == 0 || made.Machine.Length == 0 || made.Variable.Length == 0 || made.Rest.Length == 0
                    || made.Up.Length == 0 || made.Down.Length == 0 || made.Sound.Length == 0 || made.Moves == null || made.Min >= made.Max)
                {
                    throw new FormatException(what + " needs a name, a machine and its float, a range, the states at rest and held either way, the sound's state and the part it moves.");
                }

                for (int j = 0; j < i; j++)
                {
                    if (hydraulics[j].Name == made.Name)
                    {
                        throw new FormatException(vehicle + " has two hydraulic levers called '" + made.Name + "'.");
                    }
                }

                hydraulics[i] = made;
            }

            return hydraulics;
        }

        private static VehicleHitch[] ParseHitches(Dictionary<string, object> file, string vehicle)
        {
            object listed;
            if (!file.TryGetValue("hitches", out listed) || listed == null)
            {
                return new VehicleHitch[0];
            }

            List<object> entries = JsonReader.Array(listed, vehicle + "'s \"hitches\"");
            var hitches = new VehicleHitch[entries.Count];
            for (int i = 0; i < entries.Count; i++)
            {
                string what = vehicle + "'s hitch " + (i + 1);
                Dictionary<string, object> one = JsonReader.Object(entries[i], what);
                Dictionary<string, object> release = JsonReader.Object(one.ContainsKey("release") ? one["release"] : null, what + "'s \"release\"");
                hitches[i] = new VehicleHitch
                {
                    Towed = JsonReader.String(one, "towed", what),
                    Path = JsonReader.String(one, "path", what),
                    Machine = JsonReader.String(one, "machine", what),
                    Variable = JsonReader.String(one, "variable", what),
                    Attach = JsonReader.String(one, "attach", what),
                    TowedPoint = JsonReader.String(one, "towedPoint", what),
                    ReleasePath = JsonReader.String(release, "path", what + "'s release"),
                    ReleaseMachine = JsonReader.String(release, "machine", what + "'s release"),
                    Release = JsonReader.String(release, "state", what + "'s release"),
                };
                VehicleHitch made = hitches[i];
                if (made.Towed.Length == 0 || made.Path.Length == 0 || made.Machine.Length == 0 || made.Variable.Length == 0 || made.Attach.Length == 0
                    || made.TowedPoint.Length == 0 || made.ReleasePath.Length == 0 || made.ReleaseMachine.Length == 0 || made.Release.Length == 0)
                {
                    throw new FormatException(what + " needs what it tows and where on it, its machine, boolean and state that hitches, and the machine and state that unhitch.");
                }
            }

            return hitches;
        }

        private static VehicleKnob ParseKnob(Dictionary<string, object> entry, string vehicle, List<VehicleKnob> others)
        {
            var knob = new VehicleKnob();
            knob.Name = JsonReader.String(entry, "name", vehicle + "'s knob");
            string what = vehicle + "'s knob '" + knob.Name + "'";
            knob.Path = JsonReader.String(entry, "path", what);
            knob.Machine = JsonReader.String(entry, "machine", what);
            knob.Variable = Optional(entry, "variable", what);
            knob.Under = Optional(entry, "under", what);
            object states;
            bool ranged = entry.ContainsKey("step");
            if (!entry.TryGetValue("states", out states) && !ranged)
            {
                throw new FormatException(what + " needs \"states\".");
            }

            List<object> names = states == null ? new List<object>() : JsonReader.Array(states, what + "'s \"states\"");
            knob.States = new string[names.Count];
            for (int i = 0; i < names.Count; i++)
            {
                knob.States[i] = names[i] as string;
                if (string.IsNullOrEmpty(knob.States[i]))
                {
                    throw new FormatException(what + " has a state that is not a name.");
                }
            }

            knob.During = ParseDuring(entry, what, knob.States.Length);
            if (knob.Name.Length == 0 || knob.Path.Length == 0 || knob.Machine.Length == 0 || (knob.Variable == null) == (knob.During == null))
            {
                throw new FormatException(what + " needs a name, a path, a state machine, and either a variable or the states it is in at each position (\"during\").");
            }

            string by = Optional(entry, "by", what) ?? "anyone";
            if (by != "anyone" && by != "simulator")
            {
                throw new FormatException(what + " is told by '" + by + "'; it can be anyone or simulator.");
            }

            knob.BySimulator = by == "simulator";
            knob.Toggles = Flag(entry, "toggles", what);
            if (knob.Toggles && (knob.Variable == null || ranged))
            {
                throw new FormatException(what + " toggles: it needs a variable of two positions.");
            }

            if (ranged)
            {
                knob.Step = (float)JsonReader.Number(entry, "step", what);
                knob.From = entry.ContainsKey("from") ? (float)JsonReader.Number(entry, "from", what) : 0f;
                knob.Highest = (int)JsonReader.Number(entry, "steps", what);
                if (knob.Step <= 0f || knob.Highest < 1 || knob.Highest > 255)
                {
                    throw new FormatException(what + " needs a step above 0 and from 1 to 255 steps.");
                }
            }
            else
            {
                knob.Highest = knob.States.Length - 1;
                if (knob.Highest < 1)
                {
                    throw new FormatException(what + " needs a state for each of at least two positions.");
                }
            }

            if (ranged && knob.During != null)
            {
                throw new FormatException(what + " turns through a range: it needs a variable, not \"during\".");
            }

            if (knob.Toggles && knob.Highest != 1)
            {
                throw new FormatException(what + " toggles: it needs a variable of two positions.");
            }

            knob.Copies = ParseCopies(entry, what);
            knob.Moves = ParseMove(entry, what);
            if ((knob.Copies.Length > 0 || knob.Moves != null) && !ranged)
            {
                throw new FormatException(what + " copies or moves by its float: it needs a \"step\".");
            }

            for (int i = 0; i < others.Count; i++)
            {
                if (others[i].Name == knob.Name)
                {
                    throw new FormatException(vehicle + " has two knobs called '" + knob.Name + "'.");
                }
            }

            return knob;
        }

        // By position, the states a knob's machine is in while it is there; null if the entry does not say.
        private static string[][] ParseDuring(Dictionary<string, object> entry, string what, int positions)
        {
            object during;
            if (!entry.TryGetValue("during", out during) || during == null)
            {
                return null;
            }

            List<object> lists = JsonReader.Array(during, what + "'s \"during\"");
            if (lists.Count != positions)
            {
                throw new FormatException(what + " has " + positions + " states but says what it is during " + lists.Count + " positions.");
            }

            var result = new string[lists.Count][];
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int position = 0; position < lists.Count; position++)
            {
                List<object> names = JsonReader.Array(lists[position], what + "'s \"during\" " + (position + 1));
                result[position] = new string[names.Count];
                for (int i = 0; i < names.Count; i++)
                {
                    var name = names[i] as string;
                    if (string.IsNullOrEmpty(name) || !seen.Add(name))
                    {
                        throw new FormatException(what + " has, in \"during\", a state that is not a name or is in two positions.");
                    }

                    result[position][i] = name;
                }

                if (names.Count == 0)
                {
                    throw new FormatException(what + " has a position with no state in \"during\".");
                }
            }

            return result;
        }

        private static KnobCopy[] ParseCopies(Dictionary<string, object> entry, string what)
        {
            object copies;
            if (!entry.TryGetValue("copies", out copies) || copies == null)
            {
                return new KnobCopy[0];
            }

            List<object> entries = JsonReader.Array(copies, what + "'s \"copies\"");
            var result = new KnobCopy[entries.Count];
            for (int i = 0; i < entries.Count; i++)
            {
                string copy = what + "'s copy " + (i + 1);
                Dictionary<string, object> one = JsonReader.Object(entries[i], copy);
                result[i] = new KnobCopy
                {
                    Path = Optional(one, "path", copy),
                    Machine = JsonReader.String(one, "machine", copy),
                    Variable = JsonReader.String(one, "variable", copy),
                    Times = one.ContainsKey("times") ? (float)JsonReader.Number(one, "times", copy) : 1f,
                    Plus = one.ContainsKey("plus") ? (float)JsonReader.Number(one, "plus", copy) : 0f,
                    Min = one.ContainsKey("min") ? (float)JsonReader.Number(one, "min", copy) : float.NegativeInfinity,
                    Max = one.ContainsKey("max") ? (float)JsonReader.Number(one, "max", copy) : float.PositiveInfinity,
                };
                if (result[i].Machine.Length == 0 || result[i].Variable.Length == 0 || result[i].Min > result[i].Max)
                {
                    throw new FormatException(copy + " needs a state machine and a variable, and its least value no greater than its most.");
                }
            }

            return result;
        }

        private static KnobMove ParseMove(Dictionary<string, object> entry, string what)
        {
            object moves;
            if (!entry.TryGetValue("moves", out moves) || moves == null)
            {
                return null;
            }

            string move = what + "'s \"moves\"";
            Dictionary<string, object> one = JsonReader.Object(moves, move);
            string kind = JsonReader.String(one, "what", move);
            if (kind != "rotation" && kind != "position")
            {
                throw new FormatException(move + " sets '" + kind + "'; it can be rotation or position.");
            }

            return new KnobMove
            {
                Path = JsonReader.String(one, "path", move),
                Slides = kind == "position",
                Axis = AxisNamed(JsonReader.String(one, "axis", move), move),
                Times = one.ContainsKey("times") ? (float)JsonReader.Number(one, "times", move) : 1f,
                Plus = one.ContainsKey("plus") ? (float)JsonReader.Number(one, "plus", move) : 0f,
            };
        }

        // A boolean that may be missing (false).
        private static bool Flag(Dictionary<string, object> parent, string name, string what)
        {
            object value;
            if (!parent.TryGetValue(name, out value) || value == null)
            {
                return false;
            }

            if (!(value is bool))
            {
                throw new FormatException(what + " has a \"" + name + "\" that is not true or false.");
            }

            return (bool)value;
        }

        // A text that may be missing; an empty one counts as missing.
        private static string Optional(Dictionary<string, object> parent, string name, string what)
        {
            object value;
            if (!parent.TryGetValue(name, out value) || value == null)
            {
                return null;
            }

            var text = value as string;
            if (text == null)
            {
                throw new FormatException(what + " has a \"" + name + "\" that is not a text.");
            }

            return text.Length == 0 ? null : text;
        }
    }
}
