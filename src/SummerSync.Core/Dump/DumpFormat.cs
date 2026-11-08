namespace SummerSync.Core.Dump
{
    /// <summary>
    /// Property names of the state dump JSON. The in-game dumper writes them and the desktop tools read them,
    /// so both sides use these constants instead of string literals.
    /// </summary>
    public static class DumpFormat
    {
        /// <summary>Bumped whenever a reader of the previous version would misread the file.</summary>
        public const int Version = 1;

        // Dump kinds.
        public const string KindFull = "full";
        public const string KindState = "state";

        // Root.
        public const string Format = "format";
        public const string Kind = "kind";
        public const string Tool = "tool";
        public const string Created = "created";
        public const string Trigger = "trigger";
        public const string Game = "game";
        public const string Globals = "globals";
        public const string Fsms = "fsms";
        public const string Collections = "collections";
        public const string Rigidbodies = "rigidbodies";
        public const string Vehicles = "vehicles";
        public const string Stats = "stats";
        public const string Errors = "errors";

        // Game section.
        public const string Unity = "unity";
        public const string Scene = "scene";
        public const string Time = "time";
        public const string Realtime = "realtime";
        public const string Frame = "frame";

        // Identity shared by every dumped object.
        public const string Path = "path";

        /// <summary>Sibling indices from the root down ("0/3/1"): tells apart objects that share a name path.</summary>
        public const string IndexPath = "ipath";
        public const string InstanceId = "id";
        public const string ActiveInHierarchy = "act";

        // FSM.
        public const string Name = "name";

        /// <summary>Position among the FSMs with the same name on the same GameObject.</summary>
        public const string Ordinal = "k";
        public const string Enabled = "enabled";
        public const string Initialized = "init";
        public const string ActiveState = "state";
        public const string Variables = "vars";
        public const string StartState = "start";
        public const string Events = "events";
        public const string GlobalTransitions = "gtrans";
        public const string States = "states";

        // State.
        public const string ActionsLoaded = "loaded";
        public const string Transitions = "trans";
        public const string Actions = "actions";

        // Transition.
        public const string Event = "e";
        public const string ToState = "to";

        // Action.
        public const string ActionType = "t";
        public const string ActionCategory = "cat";
        public const string ActionEnabled = "en";
        public const string ActionParameters = "p";

        // A parameter bound to a named variable is written as { "var": name, "v": value }.
        public const string VariableName = "var";
        public const string VariableValue = "v";

        // ArrayMaker proxies.
        public const string CollectionType = "type";
        public const string Reference = "ref";
        public const string Items = "items";

        // Rigidbody.
        public const string Position = "pos";
        public const string Rotation = "rot";
        public const string Velocity = "vel";
        public const string AngularVelocity = "avel";
        public const string Mass = "mass";
        public const string Kinematic = "kin";
        public const string UseGravity = "grav";
        public const string Sleeping = "sleep";
        public const string Layer = "layer";

        // Vehicle.
        public const string Components = "components";
        public const string Body = "rb";
        public const string Drivetrain = "drivetrain";
        public const string Wheels = "wheels";
        public const string Colliders = "colliders";

        // Collider.
        public const string ColliderType = "type";
        public const string IsTrigger = "trigger";
        public const string Center = "center";
        public const string Size = "size";

        // Stats.
        public const string StatFsms = "fsms";
        public const string StatStates = "states";
        public const string StatActions = "actions";
        public const string StatVariables = "variables";
        public const string StatCollections = "collections";
        public const string StatRigidbodies = "rigidbodies";
        public const string StatVehicles = "vehicles";
        public const string StatMilliseconds = "ms";
    }
}
