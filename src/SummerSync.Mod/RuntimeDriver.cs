using System;
using System.Collections;
using SummerSync.Core.Logging;
using SummerSync.Core.World;
using SummerSync.Unity.Menu;
using SummerSync.Unity.Objects;
using SummerSync.Unity.Players;
using SummerSync.Unity.Vehicles;
using SummerSync.Unity.World;
using UnityEngine;

namespace SummerSync.Unity
{
    /// <summary>
    /// The mod's own GameObject. It runs the network every frame in every scene, independently of when
    /// MSCLoader chooses to call the mod, and it notices the moments when the session has to end cleanly:
    /// the game going back to the main menu, and the game closing.
    ///
    /// MSCLoader destroys it, like every other GameObject, when the main menu loads again; the next mod
    /// instance creates a new one.
    /// </summary>
    internal sealed class RuntimeDriver : MonoBehaviour
    {
        private const string ObjectName = "SummerSync";
        private const string MainMenuLevel = "MainMenu";

        private const int MaxConsecutiveFailures = 3;

        private static RuntimeDriver current;

        private int consecutiveFailures;
        private bool leftMainMenu;
        private PlayerPresenter players;
        private ObjectPresenter objects;
        private VehiclePresenter vehicles;
        private DoorSync doors;
        private WorldSync world;
        private bool worldFailed;
        private RadioSync radio;
        private bool radioFailed;
        private MoneySync money;
        private bool moneyFailed;
        private SpawnSync spawns;
        private JoinSync joining;
        private bool joiningFailed;
        private bool spawnsFailed;
        private StoreSync store;
        private bool storeFailed;
        private ThrowSync throws;
        private bool throwsFailed;
        private SwingSync swings;
        private bool swingsFailed;
        private FireSync fires;
        private bool firesFailed;
        private SaunaSync saunas;
        private bool saunasFailed;
        private FuelSync fuel;
        private bool fuelFailed;
        private LotterySync lottery;
        private bool lotteryFailed;
        private PubSync pub;
        private bool pubFailed;
        private WindowSync windows;
        private bool windowsFailed;
        private WoodSync wood;
        private bool woodFailed;
        private StoveSync stove;
        private bool stoveFailed;
        private CarFireSync carFire;
        private bool carFireFailed;
        private PokerSync poker;
        private bool pokerFailed;
        private TrafficSync traffic;
        private bool trafficFailed;
        private PeopleSync people;
        private bool peopleFailed;
        private NpcSync npcs;
        private bool npcsFailed;
        private BusSync bus;
        private bool busFailed;
        private PoliceSync police;
        private bool policeFailed;
        private WantedSync wantedSync;
        private KnockoutSync knockout;
        private bool knockoutFailed;
        private FighterSync fighters;
        private bool fightersFailed;
        private bool wantedFailed;
        private OrderSync orders;
        private bool ordersFailed;
        private JobSync jobs;
        private bool jobsFailed;
        private SaveSync saves;
        private bool savesFailed;
        private CdPlayback cdPlayer;
        private bool cdPlayerFailed;
        private PhoneSync phone;
        private bool phoneFailed;
        private SleepSync sleep;
        private bool sleepFailed;
        private PartSync carParts;
        private bool carPartsFailed;
        private ToolSync tools;
        private bool toolsFailed;
        private MountSync mountsSync;
        private bool mountsFailed;
        private FireworksSync fireworks;
        private CabinSmoke cabinSmoke;
        private bool cabinSmokeFailed;
        private bool fireworksFailed;
        private ElectricitySync electricity;
        private bool electricityFailed;
        private MailboxSync mailboxes;
        private bool mailboxesFailed;
        private TelevisionSync television;
        private bool televisionFailed;
        private SpeechSync speech;
        private bool speechFailed;

        // The session whose checksums have this world's sections (Fase 8).
        private ChecksumReplication checksFor;
        private GuestSync guests;
        private DeathSync deaths;
        private DentSync dents;
        private HikerSync hikers;
        private bool hikersFailed;
        private HydraulicSync hydraulics;
        private bool hydraulicsFailed;
        private RopeSync ropes;
        private bool ropesFailed;
        private PokerHeistSync heist;
        private bool heistFailed;
        private bool guestsFailed;
        private bool deathsFailed;
        private bool dentsFailed;
        private LookSync looks;
        private LookCapture lookCapture;
        private MenuLobby lobby;
        private bool lobbyFailed;
        private bool updaterFailed;
        private CharacterPicker picker;
        private bool pickerFailed;
        private ChatBox chat;
        private bool chatFailed;
        private bool pickerOffered;
        private MenuColumn column;
        private WorldWindow worldWindow;
        private bool worldsFailed;
        private WorldPhoto photo;
        private bool photoFailed;
        private bool vehiclesFailed;
        private bool playersFailed;
        private bool objectsFailed;
        private bool doorsFailed;

        /// <summary>Creates the driver unless a live one already exists.</summary>
        public static void Ensure()
        {
            // Unity's null check is also true for an object that has been destroyed.
            if (current != null)
            {
                return;
            }

            var host = new GameObject(ObjectName);
            DontDestroyOnLoad(host);
            current = host.AddComponent<RuntimeDriver>();
        }

        /// <summary>The "ss bring" console command. See <see cref="VehiclePresenter.Summon"/>.</summary>
        public static string Summon(string nick)
        {
            // Unity's null check is also true for an object that has been destroyed.
            if (current == null || current.vehicles == null)
            {
                return "The mod is not running yet.";
            }

            try
            {
                return current.vehicles.Summon(current.players.LocalPlayer, nick);
            }
            catch (Exception exception)
            {
                ModRuntime.Log.Error(LogCategory.Vehicle, "Bringing the " + nick + " failed. " + exception);
                return "Bringing the " + nick + " failed: " + exception.Message;
            }
        }

        /// <summary>The "ss time" console command. See <see cref="WorldSync.TestHour"/>.</summary>
        public static string TestHour(int hour)
        {
            // Unity's null check is also true for an object that has been destroyed.
            if (current == null || current.world == null || current.worldFailed)
            {
                return "The mod is not running yet.";
            }

            try
            {
                return current.world.TestHour(hour);
            }
            catch (Exception exception)
            {
                ModRuntime.Log.Error(LogCategory.Fsm, "Moving the time failed. " + exception);
                return "Moving the time failed: " + exception.Message;
            }
        }

        /// <summary>The "ss muffle" console command. See <see cref="RadioSync.SetMuffle"/>.</summary>
        public static string Muffle(string value)
        {
            // Unity's null check is also true for an object that has been destroyed.
            if (current == null || current.radio == null || current.radioFailed)
            {
                return "The mod is not running yet.";
            }

            return current.radio.SetMuffle(value);
        }

        /// <summary>The "ss tp" console command. See <see cref="Players.Travel.To"/>.</summary>
        public static string Travel(string place)
        {
            // Unity's null check is also true for an object that has been destroyed.
            if (current == null || current.players == null)
            {
                return "The mod is not running yet.";
            }

            try
            {
                return Players.Travel.To(place, current.players.LocalPlayer, current.vehiclesFailed ? null : current.vehicles, ModRuntime.Log);
            }
            catch (Exception exception)
            {
                ModRuntime.Log.Error(LogCategory.Player, "Taking the player there failed. " + exception);
                return "Taking you there failed: " + exception.Message;
            }
        }

        /// <summary>The "ss wood" console command. See <see cref="Firewood.Spawn"/>.</summary>
        public static string SpawnFirewood(string count)
        {
            // Unity's null check is also true for an object that has been destroyed.
            if (current == null || current.players == null)
            {
                return "The mod is not running yet.";
            }

            try
            {
                return Firewood.Spawn(count, current.players.LocalPlayer, ModRuntime.Log);
            }
            catch (Exception exception)
            {
                ModRuntime.Log.Error(LogCategory.Player, "Making firewood failed. " + exception);
                return "Making firewood failed: " + exception.Message;
            }
        }

        /// <summary>The "ss worlds" console command: the saved worlds of the main menu's "LOAD SAVE" and "HOST".</summary>
        public static string Worlds(string[] args)
        {
            // Unity's null check is also true for an object that has been destroyed.
            if (current == null || current.worldWindow == null)
            {
                return "The mod is not running yet.";
            }

            if (current.worldsFailed)
            {
                return "The saved worlds' windows failed; they are off until the main menu loads again (the log says why).";
            }

            try
            {
                return current.worldWindow.Command(args);
            }
            catch (Exception exception)
            {
                ModRuntime.Log.Error(LogCategory.General, "'ss worlds' failed. " + exception);
                return "'ss worlds' failed: " + exception.Message;
            }
        }

        /// <summary>The "ss look" console command: opens the character window, or sets a look by number or name.</summary>
        public static string Look(string which)
        {
            // Unity's null check is also true for an object that has been destroyed.
            if (current == null || current.looks == null)
            {
                return "The mod is not running yet.";
            }

            if (string.IsNullOrEmpty(which))
            {
                return current.picker.Open(current.players.LocalPlayer) ? "Character window open." : "There is no game world yet.";
            }

            int number;
            if (int.TryParse(which, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out number) && number >= 1 && number <= Characters.All.Length)
            {
                current.looks.Choose(number - 1);
                return "You look like " + Characters.All[number - 1].Name + ".";
            }

            if (string.Equals(which, "capsule", StringComparison.OrdinalIgnoreCase) || string.Equals(which, "capsula", StringComparison.OrdinalIgnoreCase))
            {
                current.looks.Choose(-1);
                return "You look like a capsule.";
            }

            for (int i = 0; i < Characters.All.Length; i++)
            {
                if (Characters.All[i].Name.IndexOf(which, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    current.looks.Choose(i);
                    return "You look like " + Characters.All[i].Name + ".";
                }
            }

            return "No character called '" + which + "'. 'ss look' opens the window.";
        }

        /// <summary>The "ss weather" console command. See <see cref="WorldSync.TestWeather"/>.</summary>
        public static string TestWeather(string kind)
        {
            // Unity's null check is also true for an object that has been destroyed.
            if (current == null || current.world == null || current.worldFailed)
            {
                return "The mod is not running yet.";
            }

            try
            {
                return current.world.TestWeather(kind, current.players.LocalPlayer);
            }
            catch (Exception exception)
            {
                ModRuntime.Log.Error(LogCategory.Fsm, "Changing the weather failed. " + exception);
                return "Changing the weather failed: " + exception.Message;
            }
        }

        /// <summary>"ss doors" (a test aid): the shared list of doors and switches, written to a file. See <see cref="DoorSync.WriteList"/>.</summary>
        public static string DoorsReport()
        {
            // Unity's null check is also true for an object that has been destroyed.
            if (current == null || current.doors == null || ModRuntime.SettingsFolder == null)
            {
                return "The mod is not running yet.";
            }

            try
            {
                return current.doors.WriteList(System.IO.Path.Combine(ModRuntime.SettingsFolder, "switches.txt"));
            }
            catch (Exception exception)
            {
                ModRuntime.Log.Error(LogCategory.Fsm, "Writing the doors failed. " + exception);
                return "Writing the doors failed: " + exception.Message;
            }
        }

        /// <summary>"ss seat [n|off|belt]": the passenger seats of the vehicle at hand; sitting in one, getting up, the belt (test aid).</summary>
        public static string Seat(string what)
        {
            // Unity's null check is also true for an object that has been destroyed.
            if (current == null || current.vehicles == null || current.vehiclesFailed)
            {
                return "The mod is not running yet, or sharing the vehicles failed (the log says why).";
            }

            try
            {
                return current.vehicles.SeatCommand(what);
            }
            catch (Exception exception)
            {
                ModRuntime.Log.Error(LogCategory.Vehicle, "The seat command failed. " + exception);
                return "The seat command failed: " + exception.Message;
            }
        }

        /// <summary>
        /// "ss cruise m/s [s] [coast]": a test aid that drives the vehicle at hand at an even speed, where this game simulates it;
        /// with "coast" it rolls on afterwards instead of stopping.
        /// </summary>
        public static string Cruise(string speed, string seconds, string coast)
        {
            // Unity's null check is also true for an object that has been destroyed.
            if (current == null || current.vehicles == null || current.vehiclesFailed)
            {
                return "The mod is not running yet, or sharing the vehicles failed (the log says why).";
            }

            try
            {
                return current.vehicles.CruiseTest(speed, seconds, coast);
            }
            catch (Exception exception)
            {
                ModRuntime.Log.Error(LogCategory.Vehicle, "The cruise test aid failed. " + exception);
                return "The cruise test aid failed: " + exception.Message;
            }
        }

        /// <summary>"ss fighter [punch [name] [part]]": how the men who pick a fight are shared; with punch, a test aid that punches one.</summary>
        public static string Fighter(string what, string name, string part)
        {
            // Unity's null check is also true for an object that has been destroyed.
            if (current == null || current.fighters == null || current.fightersFailed)
            {
                return "The mod is not running yet, or sharing the fighters failed (the log says why).";
            }

            try
            {
                return what == "punch" ? current.fighters.TestPunch(name, part) : current.fighters.Describe();
            }
            catch (Exception exception)
            {
                ModRuntime.Log.Error(LogCategory.Fsm, "The fighter command failed. " + exception);
                return "The fighter command failed: " + exception.Message;
            }
        }

        /// <summary>"ss save": a test aid that saves the world where it is (a guest asks the host), as the toilets do in a session.</summary>
        public static string SaveWorld()
        {
            // Unity's null check is also true for an object that has been destroyed.
            if (current == null || current.saves == null || current.savesFailed)
            {
                return "The mod is not running yet, or saving without leaving failed (the log says why).";
            }

            try
            {
                return current.saves.SaveFromConsole();
            }
            catch (Exception exception)
            {
                ModRuntime.Log.Error(LogCategory.Fsm, "The save test aid failed. " + exception);
                return "The save test aid failed: " + exception.Message;
            }
        }

        /// <summary>
        /// "ss drive throttle steering [s]": a test aid that works the controls of the vehicle the player drives, where this
        /// game simulates it, as if from the keys.
        /// </summary>
        public static string Drive(string throttle, string steer, string seconds)
        {
            // Unity's null check is also true for an object that has been destroyed.
            if (current == null || current.vehicles == null || current.vehiclesFailed)
            {
                return "The mod is not running yet, or sharing the vehicles failed (the log says why).";
            }

            try
            {
                return current.vehicles.DriveTest(throttle, steer, seconds);
            }
            catch (Exception exception)
            {
                ModRuntime.Log.Error(LogCategory.Vehicle, "The drive test aid failed. " + exception);
                return "The drive test aid failed: " + exception.Message;
            }
        }

        /// <summary>The "ss dents" console command: what the dents and crash sounds have done in this world.</summary>
        public static string Dents()
        {
            // Unity's null check is also true for an object that has been destroyed.
            if (current == null || current.dents == null)
            {
                return "The mod is not running yet.";
            }

            return current.dentsFailed ? "Sharing the dents is off (the log says why)." : current.dents.Describe();
        }

        /// <summary>The "ss smoke" console command: how much smoke there is in each cabin, and who breathes it out there.</summary>
        public static string Smoke()
        {
            // Unity's null check is also true for an object that has been destroyed.
            if (current == null || current.cabinSmoke == null)
            {
                return "The mod is not running yet.";
            }

            return current.cabinSmokeFailed ? "The smoke in the cabins is off (the log says why)." : current.cabinSmoke.Describe();
        }

        /// <summary>"ss crash [m/s]": a test aid that changes in one go how fast the vehicle at hand goes, where this game simulates it.</summary>
        public static string Crash(string what)
        {
            // Unity's null check is also true for an object that has been destroyed.
            if (current == null || current.vehicles == null || current.vehiclesFailed)
            {
                return "The mod is not running yet, or sharing the vehicles failed (the log says why).";
            }

            try
            {
                return current.vehicles.CrashTest(what);
            }
            catch (Exception exception)
            {
                ModRuntime.Log.Error(LogCategory.Vehicle, "The crash test aid failed. " + exception);
                return "The crash test aid failed: " + exception.Message;
            }
        }

        /// <summary>"ss satsuma [build]": the Satsuma's assembly here and in the session; "build" puts it together here (test aid).</summary>
        public static string SatsumaReport(string what)
        {
            // Unity's null check is also true for an object that has been destroyed.
            if (current == null || current.carParts == null)
            {
                return "The mod is not running yet.";
            }

            if (current.carPartsFailed)
            {
                return "Sharing the Satsuma's assembly failed in this session; the log says why.";
            }

            try
            {
                return current.carParts.Report(what);
            }
            catch (Exception exception)
            {
                ModRuntime.Log.Error(LogCategory.Vehicle, "Describing the Satsuma's assembly failed. " + exception);
                return "Describing the Satsuma's assembly failed: " + exception.Message;
            }
        }

        // Each session's checksums get this world's sections, the same on every machine (Fase 8).
        private void AddChecks(Transform localPlayer)
        {
            ChecksumReplication checks = localPlayer == null ? null : ModRuntime.Network.Manager.Checks;
            if (checks == checksFor)
            {
                return;
            }

            checksFor = checks;
            if (checks == null)
            {
                return;
            }

            try
            {
                checks.Add(objects.PlacesCheck);
                checks.Add(vehicles.ParkedCheck);
                checks.Add(objects.ContentsCheck);
                checks.Add(doors.Check);
                checks.Add(spawns.MakersCheck);
                checks.Add(electricity.Check);
                checks.Add(mailboxes.Check);
                checks.Add(new TallyCheckSection(ModRuntime.Network.Manager.Tallies));
                ModRuntime.Log.Info(LogCategory.Net, "Checksums: 8 sections of this world are checked against the host's every " + ChecksumReplication.IntervalSeconds + " s.");
            }
            catch (Exception exception)
            {
                ModRuntime.Log.Error(LogCategory.Net, "The checksums' sections could not be added; nothing is checked in this session. " + exception);
            }
        }

        /// <summary>The "ss chat" console command. See <see cref="ChatBox.Command"/>.</summary>
        public static string Chat(string text)
        {
            // Unity's null check is also true for an object that has been destroyed.
            if (current == null || current.chat == null)
            {
                return "The mod is not running yet.";
            }

            if (current.chatFailed)
            {
                return "The chat failed and is off until the main menu loads again (see the log).";
            }

            try
            {
                return current.chat.Command(text);
            }
            catch (Exception exception)
            {
                current.ChatFailed(exception);
                return "The chat failed: " + exception.Message;
            }
        }

        /// <summary>The "ss car" console command. See <see cref="VehiclePresenter.Report"/>.</summary>
        public static string CarReport()
        {
            // Unity's null check is also true for an object that has been destroyed.
            if (current == null || current.vehicles == null)
            {
                return "The mod is not running yet.";
            }

            try
            {
                return current.vehicles.Report(current.players.LocalPlayer);
            }
            catch (Exception exception)
            {
                ModRuntime.Log.Error(LogCategory.Vehicle, "Describing the vehicle failed. " + exception);
                return "Describing the vehicle failed: " + exception.Message;
            }
        }

        private void Awake()
        {
            // Only names are drawn in OnGUI, with explicit rectangles: no need for Unity's layout pass.
            useGUILayout = false;
            vehicles = new VehiclePresenter(ModRuntime.Log, ModRuntime.Network.Manager);
            players = new PlayerPresenter(ModRuntime.Log, ModRuntime.Network.Manager, transform, vehicles);
            looks = new LookSync(ModRuntime.Log, ModRuntime.Network.Manager, ModRuntime.SettingsFolder);
            picker = new CharacterPicker(ModRuntime.Log, looks);
            chat = new ChatBox(ModRuntime.Log, ModRuntime.Network.Manager);
            players.Looks = looks;
            lookCapture = new LookCapture(ModRuntime.Log, ModRuntime.Network.Manager);
            lobby = new MenuLobby(ModRuntime.Log, ModRuntime.Network.Manager, looks);
            column = new MenuColumn(ModRuntime.Log);
            worldWindow = new WorldWindow(ModRuntime.Log, ModRuntime.Network);
            photo = new WorldPhoto(ModRuntime.Log);
            MenuClicks.Forget();
            objects = new ObjectPresenter(ModRuntime.Log, ModRuntime.Network.Manager, vehicles);
            doors = new DoorSync(ModRuntime.Log, ModRuntime.Network.Manager, vehicles);
            world = new WorldSync(ModRuntime.Log, ModRuntime.Network.Manager);
            radio = new RadioSync(ModRuntime.Log, ModRuntime.Network.Manager, ModRuntime.SettingsFolder, vehicles, doors);
            money = new MoneySync(ModRuntime.Log, ModRuntime.Network.Manager);
            spawns = new SpawnSync(ModRuntime.Log, ModRuntime.Network.Manager, objects);
            joining = new JoinSync(ModRuntime.Log, ModRuntime.Network.Manager, objects, spawns);
            store = new StoreSync(ModRuntime.Log, ModRuntime.Network.Manager);
            throws = new ThrowSync(ModRuntime.Log, ModRuntime.Network.Manager, objects);
            swings = new SwingSync(ModRuntime.Log, ModRuntime.Network.Manager);
            fires = new FireSync(ModRuntime.Log, ModRuntime.Network.Manager);
            saunas = new SaunaSync(ModRuntime.Log, ModRuntime.Network.Manager);
            fuel = new FuelSync(ModRuntime.Log, ModRuntime.Network.Manager, vehicles);
            lottery = new LotterySync(ModRuntime.Log, ModRuntime.Network.Manager);
            pub = new PubSync(ModRuntime.Log, ModRuntime.Network.Manager, objects);
            windows = new WindowSync(ModRuntime.Log, ModRuntime.Network.Manager);
            wood = new WoodSync(ModRuntime.Log, ModRuntime.Network.Manager, objects);
            stove = new StoveSync(ModRuntime.Log, ModRuntime.Network.Manager);
            carFire = new CarFireSync(ModRuntime.Log, ModRuntime.Network.Manager);
            poker = new PokerSync(ModRuntime.Log, ModRuntime.Network.Manager);
            traffic = new TrafficSync(ModRuntime.Log, ModRuntime.Network.Manager);
            people = new PeopleSync(ModRuntime.Log, ModRuntime.Network.Manager);
            npcs = new NpcSync(ModRuntime.Log, ModRuntime.Network.Manager);
            players.Npcs = npcs;
            bus = new BusSync(ModRuntime.Log, ModRuntime.Network.Manager, npcs);
            police = new PoliceSync(ModRuntime.Log, ModRuntime.Network.Manager);
            wantedSync = new WantedSync(ModRuntime.Log, ModRuntime.Network.Manager, vehicles);
            knockout = new KnockoutSync(ModRuntime.Log, ModRuntime.Network.Manager);
            fighters = new FighterSync(ModRuntime.Log, ModRuntime.Network.Manager, vehicles);
            orders = new OrderSync(ModRuntime.Log, ModRuntime.Network.Manager, objects);
            jobs = new JobSync(ModRuntime.Log, ModRuntime.Network.Manager, objects, vehicles);
            saves = new SaveSync(ModRuntime.Log, ModRuntime.Network.Manager);
            cdPlayer = new CdPlayback(ModRuntime.Log, ModRuntime.Network.Manager, vehicles);
            phone = new PhoneSync(ModRuntime.Log, ModRuntime.Network.Manager);
            sleep = new SleepSync(ModRuntime.Log, ModRuntime.Network.Manager);
            carParts = new PartSync(ModRuntime.Log, ModRuntime.Network.Manager, objects, vehicles);
            tools = new ToolSync(ModRuntime.Log, ModRuntime.Network.Manager);
            mountsSync = new MountSync(ModRuntime.Log, ModRuntime.Network.Manager, objects);
            fireworks = new FireworksSync(ModRuntime.Log, ModRuntime.Network.Manager, objects);
            cabinSmoke = new CabinSmoke(ModRuntime.Log, ModRuntime.Network.Manager, vehicles, players);
            electricity = new ElectricitySync(ModRuntime.Log, ModRuntime.Network.Manager);
            mailboxes = new MailboxSync(ModRuntime.Log, ModRuntime.Network.Manager);
            television = new TelevisionSync(ModRuntime.Log, ModRuntime.Network.Manager);
            speech = new SpeechSync(ModRuntime.Log, ModRuntime.Network.Manager, players);
            guests = new GuestSync(ModRuntime.Log, ModRuntime.Network.Manager);
            deaths = new DeathSync(ModRuntime.Log, ModRuntime.Network.Manager, vehicles, players);
            dents = new DentSync(ModRuntime.Log, ModRuntime.Network.Manager, vehicles);
            hikers = new HikerSync(ModRuntime.Log, ModRuntime.Network.Manager, vehicles);
            hydraulics = new HydraulicSync(ModRuntime.Log, ModRuntime.Network.Manager, vehicles);
            ropes = new RopeSync(ModRuntime.Log, ModRuntime.Network.Manager, vehicles, objects);
            heist = new PokerHeistSync(ModRuntime.Log, ModRuntime.Network.Manager, ropes);
            dentsFailed = !DentSync.Working;
            players.OnLevelLoaded(Application.loadedLevelName);
            HostWorld.OnLevelLoaded(Application.loadedLevelName);
            StartCoroutine(AfterEveryPhysicsStep());
        }

        // Unity resumes this once the physics has moved everything in a step: the moment a vehicle has moved
        // and its passenger has not yet.
        private IEnumerator AfterEveryPhysicsStep()
        {
            var afterPhysics = new WaitForFixedUpdate();
            for (;;)
            {
                yield return afterPhysics;
                if (vehiclesFailed || playersFailed)
                {
                    continue;
                }

                try
                {
                    vehicles.AfterPhysics();
                }
                catch (Exception exception)
                {
                    DisableVehicles(exception);
                }
            }
        }

        private void Update()
        {
            if (!pickerFailed && Input.GetKeyDown(CharacterPicker.Key))
            {
                try
                {
                    if (picker.IsOpen)
                    {
                        picker.Close(false);
                    }
                    else
                    {
                        if (!worldsFailed && worldWindow.IsOpen)
                        {
                            worldWindow.Close();
                        }

                        picker.Open(players.LocalPlayer);
                    }
                }
                catch (Exception exception)
                {
                    PickerFailed(exception);
                }
            }

            try
            {
                ModRuntime.Network.Update();
                consecutiveFailures = 0;
            }
            catch (Exception exception)
            {
                // A bug must not throw on every frame: drop the session, and if that does not cure it, stop trying.
                consecutiveFailures++;
                ModRuntime.Log.Error(LogCategory.Net, "The network update failed; closing the session. " + exception);
                ModRuntime.Network.AbortSession();
                if (consecutiveFailures >= MaxConsecutiveFailures)
                {
                    ModRuntime.Log.Error(LogCategory.Net, "The network is switched off until the main menu loads again.");
                    enabled = false;
                }
            }

            // After the network: the lines that just came in show this frame.
            if (!chatFailed)
            {
                try
                {
                    chat.Update((!pickerFailed && picker.IsOpen) || (!worldsFailed && (worldWindow.IsOpen || worldWindow.Creating)));
                }
                catch (Exception exception)
                {
                    ChatFailed(exception);
                }
            }

            MusicImport.Update(HostWorld.InMenu, ModRuntime.ImportMusic, ModRuntime.Network.Now, ModRuntime.Log);
            CdSync.Update(ModRuntime.Network.Manager, ModRuntime.Log, ModRuntime.Network.Now, ModRuntime.SettingsFolder);
            ArtSync.Update(ModRuntime.Network.Manager, ModRuntime.Log, ModRuntime.Network.Now, ModRuntime.SettingsFolder);
            if (!updaterFailed)
            {
                try
                {
                    ModUpdater.Update(ModRuntime.Network.Now, HostWorld.InMenu);
                }
                catch (Exception exception)
                {
                    updaterFailed = true;
                    ModRuntime.Log.Error(LogCategory.General, "The main menu's offer to update failed; it is off until the main menu loads again. " + exception);
                }
            }

            // After the network: what the session says about the host's world is the freshest it can be.
            if (!HostWorld.Failed)
            {
                try
                {
                    HostWorld.Update(ModRuntime.Network.Manager, ModRuntime.Log, ModRuntime.Network.Now);
                }
                catch (Exception exception)
                {
                    HostWorld.Disable(exception);
                }
            }

            UpdateMenuWorlds();
        }

        // The main menu's "LOAD SAVE" and "HOST", their windows, and the menu's buttons held while a window is open.
        private void UpdateMenuWorlds()
        {
            bool inMenu = HostWorld.InMenu;
            if (!worldsFailed)
            {
                try
                {
                    Core.Net.NetworkManager manager = ModRuntime.Network.Manager;
                    bool guest = HostWorld.PlaysHostWorld || manager.Mode == Core.Net.NetworkMode.Client;
                    int clicked;
                    column.Update(inMenu, guest, manager.Mode == Core.Net.NetworkMode.Host, out clicked);
                    if (clicked == MenuColumn.LeaveLobby)
                    {
                        worldWindow.LeaveLobby();
                    }
                    else if (clicked != 0 && !guest)
                    {
                        if (!pickerFailed && picker.IsOpen)
                        {
                            picker.Close(false);
                        }

                        switch (clicked)
                        {
                            case MenuColumn.LoadSave:
                                worldWindow.OpenLoad();
                                break;
                            case MenuColumn.Host:
                            case MenuColumn.LobbySettings:
                                worldWindow.OpenHost();
                                break;
                            case MenuColumn.Invite:
                                worldWindow.Invite();
                                break;
                            case MenuColumn.CloseLobby:
                                worldWindow.CloseLobby();
                                break;
                        }
                    }

                    if (inMenu && World.DeathSync.ReturnAfterDeath && !worldWindow.IsOpen)
                    {
                        World.DeathSync.ReturnAfterDeath = false;
                        worldWindow.ReturnAfterDeath();
                    }

                    worldWindow.Update(ModRuntime.Network.Now, inMenu);
                }
                catch (Exception exception)
                {
                    WorldsFailed(exception);
                }
            }

            if (inMenu)
            {
                MenuClicks.Hold((!worldsFailed && worldWindow.IsOpen) || (!pickerFailed && picker.IsOpen), ModRuntime.Log);
            }
        }

        private void WorldsFailed(Exception exception)
        {
            worldsFailed = true;
            ModRuntime.Log.Error(LogCategory.General, "The main menu's saved worlds failed; 'LOAD SAVE' and 'HOST' do nothing until the main menu loads again. " + exception);
            try
            {
                worldWindow.Close();
                MenuClicks.Release();
            }
            catch (Exception)
            {
                // Whatever broke may break the clean-up too.
            }
        }

        // The vehicles someone else drives are moved with the physics, so that what hangs from them by a joint
        // is carried along.
        private void FixedUpdate()
        {
            if (vehiclesFailed || playersFailed)
            {
                return;
            }

            try
            {
                vehicles.FixedUpdate();
            }
            catch (Exception exception)
            {
                DisableVehicles(exception);
            }

            if (!trafficFailed)
            {
                try
                {
                    traffic.FixedUpdate();
                }
                catch (Exception exception)
                {
                    DisableTraffic(exception);
                }
            }

            if (!npcsFailed)
            {
                try
                {
                    npcs.FixedUpdate();
                }
                catch (Exception exception)
                {
                    DisableNpcs(exception);
                }
            }
        }

        private void DisableNpcs(Exception exception)
        {
            npcsFailed = true;
            ModRuntime.Log.Error(LogCategory.Fsm, "Sharing the NPCs (train, boats, moose…) failed; each game moves its own until the main menu loads again. " + exception);
            try
            {
                npcs.Release();
            }
            catch (Exception)
            {
                // Whatever broke may break the clean-up too.
            }
        }

        private void DisableTraffic(Exception exception)
        {
            trafficFailed = true;
            ModRuntime.Log.Error(LogCategory.Vehicle, "Sharing the traffic failed; each game drives its own until the main menu loads again. " + exception);
            try
            {
                traffic.Release();
            }
            catch (Exception)
            {
                // Whatever broke may break the clean-up too.
            }
        }

        // After the game's own updates, so that the local player is read where this frame left them and the
        // others are placed right before the camera renders.
        private void LateUpdate()
        {
            if (playersFailed)
            {
                return;
            }

            // The vehicles first: a player who rides one is read and placed relative to it.
            if (!vehiclesFailed)
            {
                try
                {
                    vehicles.LateUpdate(ModRuntime.Network.Now, players.LocalPlayer);
                }
                catch (Exception exception)
                {
                    DisableVehicles(exception);
                }
            }

            try
            {
                players.LateUpdate(ModRuntime.Network.Now);
            }
            catch (Exception exception)
            {
                DisablePlayers(exception);
                return;
            }

            // Each of these stands or falls by itself: a door that cannot be found must not stop a crate from
            // being carried, nor either of them the players from seeing each other.
            Transform localPlayer = players.LocalPlayer;

            // What the games made first: what is made here for another player is then found in the same frame.
            if (!spawnsFailed && !objectsFailed)
            {
                try
                {
                    spawns.LateUpdate(ModRuntime.Network.Now, localPlayer);
                }
                catch (Exception exception)
                {
                    spawnsFailed = true;
                    ModRuntime.Log.Error(LogCategory.Fsm, "Sharing what the games make failed; what is bought appears only for whoever bought it until the main menu loads again. " + exception);
                }
            }

            if (!objectsFailed)
            {
                try
                {
                    objects.ShowOwners = ModRuntime.ShowObjectOwners;
                    objects.LateUpdate(ModRuntime.Network.Now, localPlayer);
                }
                catch (Exception exception)
                {
                    objectsFailed = true;
                    ModRuntime.Log.Error(LogCategory.Authority, "Sharing the loose objects failed; they are the game's alone until the main menu loads again. " + exception);
                    try
                    {
                        objects.ReleaseAll();
                    }
                    catch (Exception)
                    {
                        // Whatever broke may break the clean-up too.
                    }
                }
            }

            // A world loaded from the host's save, brought to what the host's world has now: after the makers and the
            // loose objects, which it uses.
            if (!joiningFailed && !spawnsFailed && !objectsFailed)
            {
                try
                {
                    joining.LateUpdate(ModRuntime.Network.Now, localPlayer);
                }
                catch (Exception exception)
                {
                    joiningFailed = true;
                    ModRuntime.Log.Error(LogCategory.Authority, "Bringing this world to the host's after joining failed; what the host made or ate since its save may differ here. " + exception);
                }
            }

            if (!doorsFailed)
            {
                try
                {
                    doors.LateUpdate(ModRuntime.Network.Now, localPlayer);
                }
                catch (Exception exception)
                {
                    doorsFailed = true;
                    ModRuntime.Log.Error(LogCategory.Fsm, "Sharing the doors failed; they are the game's alone until the main menu loads again. " + exception);
                }
            }

            if (!worldFailed)
            {
                try
                {
                    world.LateUpdate(ModRuntime.Network.Now, localPlayer);
                }
                catch (Exception exception)
                {
                    worldFailed = true;
                    ModRuntime.Log.Error(LogCategory.Fsm, "Sharing the time and the weather failed; they are the game's alone until the main menu loads again. " + exception);
                    try
                    {
                        world.Release();
                    }
                    catch (Exception)
                    {
                        // Whatever broke may break the clean-up too.
                    }
                }
            }

            if (!radioFailed)
            {
                try
                {
                    radio.LateUpdate(ModRuntime.Network.Now, localPlayer);
                }
                catch (Exception exception)
                {
                    radioFailed = true;
                    ModRuntime.Log.Error(LogCategory.Fsm, "Sharing the radio station failed; it is the game's alone until the main menu loads again. " + exception);
                    try
                    {
                        radio.Release();
                    }
                    catch (Exception)
                    {
                        // Whatever broke may break the clean-up too.
                    }
                }
            }

            if (!throwsFailed && !objectsFailed)
            {
                try
                {
                    throws.LateUpdate(ModRuntime.Network.Now, localPlayer);
                }
                catch (Exception exception)
                {
                    throwsFailed = true;
                    ModRuntime.Log.Error(LogCategory.Player, "Sharing what players throw away after a drink failed; each game shows only its own until the main menu loads again. " + exception);
                }
            }

            if (!pickerFailed)
            {
                try
                {
                    looks.LateUpdate(localPlayer, HostWorld.InMenu);

                    // The first time in a world with others, the window opens by itself for whoever never chose.
                    bool together = localPlayer != null && ModRuntime.Network.Manager.Session != null && ModRuntime.Network.Manager.Session.State == Core.Net.SessionState.Connected;
                    if (together && !pickerOffered && !looks.HasChosen)
                    {
                        pickerOffered = true;
                        picker.Open(localPlayer);
                    }

                    picker.LateUpdate(localPlayer);
                }
                catch (Exception exception)
                {
                    PickerFailed(exception);
                }
            }

            if (!lobbyFailed)
            {
                try
                {
                    lobby.LateUpdate(ModRuntime.Network.Now, HostWorld.InMenu);
                    lookCapture.LateUpdate(ModRuntime.Network.Now, localPlayer);
                }
                catch (Exception exception)
                {
                    lobbyFailed = true;
                    ModRuntime.Log.Error(LogCategory.Player, "The players in the main menu (or keeping their looks for it) failed; it is off until the main menu loads again. " + exception);
                    try
                    {
                        lobby.Clear();
                    }
                    catch (Exception)
                    {
                        // Going anyway.
                    }
                }
            }

            if (!firesFailed)
            {
                try
                {
                    fires.LateUpdate(ModRuntime.Network.Now, localPlayer);
                }
                catch (Exception exception)
                {
                    firesFailed = true;
                    ModRuntime.Log.Error(LogCategory.Fsm, "Sharing the fires failed; each game burns its own until the main menu loads again. " + exception);
                    try
                    {
                        fires.Release();
                    }
                    catch (Exception)
                    {
                        // Whatever broke may break the clean-up too.
                    }
                }
            }

            if (!saunasFailed)
            {
                try
                {
                    saunas.LateUpdate(ModRuntime.Network.Now, localPlayer);
                }
                catch (Exception exception)
                {
                    saunasFailed = true;
                    ModRuntime.Log.Error(LogCategory.Fsm, "Sharing the saunas failed; each game heats its own until the main menu loads again. " + exception);
                    try
                    {
                        saunas.Release();
                    }
                    catch (Exception)
                    {
                        // Whatever broke may break the clean-up too.
                    }
                }
            }

            if (!swingsFailed)
            {
                try
                {
                    swings.LateUpdate(ModRuntime.Network.Now, localPlayer);
                }
                catch (Exception exception)
                {
                    swingsFailed = true;
                    ModRuntime.Log.Error(LogCategory.Fsm, "Sharing the garage doors failed; they are each game's own until the main menu loads again. " + exception);
                    try
                    {
                        swings.Release();
                    }
                    catch (Exception)
                    {
                        // Whatever broke may break the clean-up too.
                    }
                }
            }

            if (!fuelFailed && !vehiclesFailed)
            {
                try
                {
                    fuel.LateUpdate(ModRuntime.Network.Now, localPlayer);
                }
                catch (Exception exception)
                {
                    fuelFailed = true;
                    ModRuntime.Log.Error(LogCategory.Fsm, "Sharing the fuel failed; tanks and pumps are each game's own until the main menu loads again. " + exception);
                    try
                    {
                        fuel.Release();
                    }
                    catch (Exception)
                    {
                        // Whatever broke may break the clean-up too.
                    }
                }
            }

            if (!lotteryFailed)
            {
                try
                {
                    lottery.LateUpdate(ModRuntime.Network.Now, localPlayer);
                }
                catch (Exception exception)
                {
                    lotteryFailed = true;
                    ModRuntime.Log.Error(LogCategory.Fsm, "Sharing the lottery failed; each game draws its own until the main menu loads again. " + exception);
                    try
                    {
                        lottery.Release();
                    }
                    catch (Exception)
                    {
                        // Whatever broke may break the clean-up too.
                    }
                }
            }

            if (!pubFailed && !objectsFailed)
            {
                try
                {
                    pub.LateUpdate(ModRuntime.Network.Now, localPlayer);
                }
                catch (Exception exception)
                {
                    pubFailed = true;
                    ModRuntime.Log.Error(LogCategory.Fsm, "Sharing the pub failed; each game's Teimo serves only its own player until the main menu loads again. " + exception);
                    try
                    {
                        pub.Release();
                    }
                    catch (Exception)
                    {
                        // Whatever broke may break the clean-up too.
                    }
                }
            }

            if (!windowsFailed)
            {
                try
                {
                    windows.LateUpdate(ModRuntime.Network.Now, localPlayer);
                }
                catch (Exception exception)
                {
                    windowsFailed = true;
                    ModRuntime.Log.Error(LogCategory.Fsm, "Sharing the shop's windows failed; a window broken in one game stays whole in the others until the main menu loads again. " + exception);
                }
            }

            if (!woodFailed && !objectsFailed)
            {
                try
                {
                    wood.LateUpdate(ModRuntime.Network.Now, localPlayer);
                }
                catch (Exception exception)
                {
                    woodFailed = true;
                    ModRuntime.Log.Error(LogCategory.Fsm, "Sharing the wood carrier and the flatbed's pile failed; each game has its own until the main menu loads again. " + exception);
                }
            }

            if (!stoveFailed)
            {
                try
                {
                    stove.LateUpdate(ModRuntime.Network.Now, localPlayer);
                }
                catch (Exception exception)
                {
                    stoveFailed = true;
                    ModRuntime.Log.Error(LogCategory.Fsm, "Sharing the kitchen stove's knobs failed; each game has its own until the main menu loads again. " + exception);
                }
            }

            if (!carFireFailed)
            {
                try
                {
                    carFire.LateUpdate(ModRuntime.Network.Now, localPlayer);
                }
                catch (Exception exception)
                {
                    carFireFailed = true;
                    ModRuntime.Log.Error(LogCategory.Vehicle, "Sharing the Satsuma's short circuit and fire failed; each game has its own until the main menu loads again. " + exception);
                }
            }

            if (!trafficFailed)
            {
                try
                {
                    traffic.LateUpdate(ModRuntime.Network.Now, localPlayer);
                }
                catch (Exception exception)
                {
                    DisableTraffic(exception);
                }
            }

            if (!peopleFailed)
            {
                try
                {
                    people.LateUpdate(ModRuntime.Network.Now, localPlayer);
                }
                catch (Exception exception)
                {
                    peopleFailed = true;
                    ModRuntime.Log.Error(LogCategory.Fsm, "Sharing the people who walk the roads failed; each game moves its own until the main menu loads again. " + exception);
                    try
                    {
                        people.Release();
                    }
                    catch (Exception)
                    {
                        // Whatever broke may break the clean-up too.
                    }
                }
            }

            if (!npcsFailed)
            {
                try
                {
                    npcs.LateUpdate(ModRuntime.Network.Now, localPlayer);
                }
                catch (Exception exception)
                {
                    DisableNpcs(exception);
                }
            }

            if (!jobsFailed)
            {
                try
                {
                    jobs.LateUpdate(ModRuntime.Network.Now, localPlayer);
                }
                catch (Exception exception)
                {
                    jobsFailed = true;
                    ModRuntime.Log.Error(LogCategory.Fsm, "Sharing the jobs failed; each game runs its own until the main menu loads again. " + exception);
                    try
                    {
                        jobs.Release();
                    }
                    catch (Exception)
                    {
                        // Whatever broke may break the clean-up too.
                    }
                }
            }

            if (!savesFailed)
            {
                try
                {
                    saves.LateUpdate(ModRuntime.Network.Now, localPlayer != null);
                }
                catch (Exception exception)
                {
                    savesFailed = true;
                    ModRuntime.Log.Error(LogCategory.Fsm, "Saving without leaving failed; the toilets save and quit to the menu until the main menu loads again. " + exception);
                    try
                    {
                        saves.Release();
                    }
                    catch (Exception)
                    {
                        // Whatever broke may break the clean-up too.
                    }
                }
            }

            if (!cdPlayerFailed)
            {
                try
                {
                    cdPlayer.LateUpdate(ModRuntime.Network.Now, localPlayer);
                }
                catch (Exception exception)
                {
                    cdPlayerFailed = true;
                    ModRuntime.Log.Error(LogCategory.Fsm, "Sharing the CD player failed; each game plays its own until the main menu loads again. " + exception);
                    try
                    {
                        cdPlayer.Release();
                    }
                    catch (Exception)
                    {
                        // Whatever broke may break the clean-up too.
                    }
                }
            }

            if (!policeFailed)
            {
                try
                {
                    police.LateUpdate(ModRuntime.Network.Now, localPlayer);
                }
                catch (Exception exception)
                {
                    policeFailed = true;
                    ModRuntime.Log.Error(LogCategory.Fsm, "Sharing the police's checkpoints failed; each game draws its own until the main menu loads again. " + exception);
                    try
                    {
                        police.Release();
                    }
                    catch (Exception)
                    {
                        // Whatever broke may break the clean-up too.
                    }
                }
            }

            if (!ordersFailed)
            {
                try
                {
                    orders.LateUpdate(ModRuntime.Network.Now, localPlayer);
                }
                catch (Exception exception)
                {
                    ordersFailed = true;
                    ModRuntime.Log.Error(LogCategory.Fsm, "Sharing the mail orders and their boxes failed; each game has its own until the main menu loads again. " + exception);
                    try
                    {
                        orders.Release();
                    }
                    catch (Exception)
                    {
                        // Whatever broke may break the clean-up too.
                    }
                }
            }

            if (!wantedFailed)
            {
                try
                {
                    wantedSync.LateUpdate(ModRuntime.Network.Now, localPlayer);
                }
                catch (Exception exception)
                {
                    wantedFailed = true;
                    ModRuntime.Log.Error(LogCategory.Fsm, "The police coming for the player who did the crime failed; each game's police come as the game decides until the main menu loads again. " + exception);
                    try
                    {
                        wantedSync.Release();
                    }
                    catch (Exception)
                    {
                        // Whatever broke may break the clean-up too.
                    }
                }
            }

            if (!knockoutFailed)
            {
                try
                {
                    knockout.LateUpdate(localPlayer);
                }
                catch (Exception exception)
                {
                    knockoutFailed = true;
                    ModRuntime.Log.Error(LogCategory.Fsm, "Keeping the clock through a knockout failed; a knockout moves the time as the game does until the main menu loads again. " + exception);
                    try
                    {
                        knockout.Release();
                    }
                    catch (Exception)
                    {
                        // Whatever broke may break the clean-up too.
                    }
                }
            }

            if (!fightersFailed)
            {
                try
                {
                    fighters.LateUpdate(ModRuntime.Network.Now, localPlayer);
                }
                catch (Exception exception)
                {
                    fightersFailed = true;
                    ModRuntime.Log.Error(LogCategory.Fsm, "Sharing the men who pick a fight failed; each game's are its own until the main menu loads again. " + exception);
                    try
                    {
                        fighters.Release();
                    }
                    catch (Exception)
                    {
                        // Whatever broke may break the clean-up too.
                    }
                }
            }

            if (!busFailed && !npcsFailed)
            {
                try
                {
                    bus.LateUpdate(ModRuntime.Network.Now, localPlayer);
                }
                catch (Exception exception)
                {
                    busFailed = true;
                    ModRuntime.Log.Error(LogCategory.Fsm, "Sharing the bus's riders failed; each game's bus waits for its own player until the main menu loads again. " + exception);
                    try
                    {
                        bus.Release();
                    }
                    catch (Exception)
                    {
                        // Whatever broke may break the clean-up too.
                    }
                }
            }

            if (!phoneFailed)
            {
                try
                {
                    phone.LateUpdate(ModRuntime.Network.Now, localPlayer);
                }
                catch (Exception exception)
                {
                    phoneFailed = true;
                    ModRuntime.Log.Error(LogCategory.Fsm, "Sharing the phone failed; each game's rings on its own until the main menu loads again. " + exception);
                    try
                    {
                        phone.Release();
                    }
                    catch (Exception)
                    {
                        // Whatever broke may break the clean-up too.
                    }
                }
            }

            if (!sleepFailed)
            {
                try
                {
                    sleep.LateUpdate(ModRuntime.Network.Now, localPlayer);
                }
                catch (Exception exception)
                {
                    sleepFailed = true;
                    ModRuntime.Log.Error(LogCategory.Fsm, "Sleeping with others failed; each game sleeps on its own until the main menu loads again. " + exception);
                    try
                    {
                        sleep.Release();
                    }
                    catch (Exception)
                    {
                        // Whatever broke may break the clean-up too.
                    }
                }
            }

            if (!pokerFailed)
            {
                try
                {
                    poker.LateUpdate(ModRuntime.Network.Now, localPlayer);
                }
                catch (Exception exception)
                {
                    pokerFailed = true;
                    ModRuntime.Log.Error(LogCategory.Fsm, "Sharing the video poker failed; each game's is its own until the main menu loads again. " + exception);
                    try
                    {
                        poker.Release();
                    }
                    catch (Exception)
                    {
                        // Whatever broke may break the clean-up too.
                    }
                }
            }

            if (!carPartsFailed && !objectsFailed)
            {
                try
                {
                    carParts.LateUpdate(ModRuntime.Network.Now, localPlayer);
                }
                catch (Exception exception)
                {
                    carPartsFailed = true;
                    ModRuntime.Log.Error(LogCategory.Vehicle, "Sharing the Satsuma's assembly failed; each game builds its own until the main menu loads again. " + exception);
                    try
                    {
                        carParts.Release();
                    }
                    catch (Exception)
                    {
                        // Whatever broke may break the clean-up too.
                    }
                }
            }

            AddChecks(localPlayer);

            if (!photoFailed)
            {
                try
                {
                    photo.LateUpdate(localPlayer);
                }
                catch (Exception exception)
                {
                    photoFailed = true;
                    ModRuntime.Log.Error(LogCategory.General, "The photo of the Satsuma when saving failed; the menu's cards keep their last photo. " + exception);
                }
            }

            if (!deathsFailed)
            {
                try
                {
                    deaths.LateUpdate(ModRuntime.Network.Now, localPlayer);
                }
                catch (Exception exception)
                {
                    deathsFailed = true;
                    ModRuntime.Log.Error(LogCategory.Player, "Showing the others' deaths and coming back from one failed; until the main menu loads again a death is the game's alone. " + exception);
                }
            }

            if (!hikersFailed)
            {
                try
                {
                    hikers.LateUpdate(ModRuntime.Network.Now, localPlayer);
                }
                catch (Exception exception)
                {
                    hikersFailed = true;
                    ModRuntime.Log.Error(LogCategory.Player, "Sharing the hitchhikers' rides failed; until the main menu loads again each game has its own. " + exception);
                    try
                    {
                        hikers.Release();
                    }
                    catch (Exception)
                    {
                        // Whatever broke may break the clean-up too.
                    }
                }
            }

            if (!hydraulicsFailed)
            {
                try
                {
                    hydraulics.LateUpdate(ModRuntime.Network.Now, localPlayer);
                }
                catch (Exception exception)
                {
                    hydraulicsFailed = true;
                    ModRuntime.Log.Error(LogCategory.Vehicle, "Sharing the hydraulic levers failed; until the main menu loads again each game moves its own. " + exception);
                    try
                    {
                        hydraulics.Release();
                    }
                    catch (Exception)
                    {
                        // Whatever broke may break the clean-up too.
                    }
                }
            }

            if (!ropesFailed)
            {
                try
                {
                    ropes.LateUpdate(ModRuntime.Network.Now, localPlayer);
                }
                catch (Exception exception)
                {
                    ropesFailed = true;
                    ModRuntime.Log.Error(LogCategory.Vehicle, "Sharing the towing ropes failed; until the main menu loads again each game has its own. " + exception);
                    try
                    {
                        ropes.Release();
                    }
                    catch (Exception)
                    {
                        // Whatever broke may break the clean-up too.
                    }
                }
            }

            if (!heistFailed)
            {
                try
                {
                    heist.LateUpdate(ModRuntime.Network.Now, localPlayer);
                }
                catch (Exception exception)
                {
                    heistFailed = true;
                    ModRuntime.Log.Error(LogCategory.Vehicle, "Sharing the video poker torn out of the pub failed; until the main menu loads again each game has its own. " + exception);
                    try
                    {
                        heist.Release();
                    }
                    catch (Exception)
                    {
                        // Whatever broke may break the clean-up too.
                    }
                }
            }

            if (!dentsFailed)
            {
                try
                {
                    dents.LateUpdate(ModRuntime.Network.Now, localPlayer);
                }
                catch (Exception exception)
                {
                    dentsFailed = true;
                    ModRuntime.Log.Error(LogCategory.Vehicle, "Sharing the dents failed; until the main menu loads again each game dents its own vehicles. " + exception);
                    try
                    {
                        dents.Release();
                    }
                    catch (Exception)
                    {
                        // Whatever broke may break the clean-up too.
                    }
                }
            }

            if (!guestsFailed)
            {
                try
                {
                    guests.LateUpdate(ModRuntime.Network.Now, localPlayer);
                }
                catch (Exception exception)
                {
                    guestsFailed = true;
                    ModRuntime.Log.Error(LogCategory.Player, "Keeping the guests' needs and places with the host's save failed; a guest starts as the save's player until the main menu loads again. " + exception);
                }
            }

            if (!speechFailed)
            {
                try
                {
                    speech.LateUpdate(ModRuntime.Network.Now, localPlayer);
                }
                catch (Exception exception)
                {
                    speechFailed = true;
                    ModRuntime.Log.Error(LogCategory.Player, "Sharing what the players say failed; nobody hears the others' swearing until the main menu loads again. " + exception);
                    try
                    {
                        speech.Release();
                    }
                    catch (Exception)
                    {
                        // Whatever broke may break the clean-up too.
                    }
                }
            }

            if (!televisionFailed)
            {
                try
                {
                    television.LateUpdate(ModRuntime.Network.Now, localPlayer);
                }
                catch (Exception exception)
                {
                    televisionFailed = true;
                    ModRuntime.Log.Error(LogCategory.Fsm, "Sharing the television's film and teletext failed; each game has its own until the main menu loads again. " + exception);
                    try
                    {
                        television.Release();
                    }
                    catch (Exception)
                    {
                        // Whatever broke may break the clean-up too.
                    }
                }
            }

            if (!mailboxesFailed)
            {
                try
                {
                    mailboxes.LateUpdate(ModRuntime.Network.Now, localPlayer);
                }
                catch (Exception exception)
                {
                    mailboxesFailed = true;
                    ModRuntime.Log.Error(LogCategory.Fsm, "Sharing the flyer job's mailboxes failed; each game has its own until the main menu loads again. " + exception);
                    try
                    {
                        mailboxes.Release();
                    }
                    catch (Exception)
                    {
                        // Whatever broke may break the clean-up too.
                    }
                }
            }

            if (!electricityFailed)
            {
                try
                {
                    electricity.LateUpdate(ModRuntime.Network.Now, localPlayer);
                }
                catch (Exception exception)
                {
                    electricityFailed = true;
                    ModRuntime.Log.Error(LogCategory.Fsm, "Sharing the house's electricity failed; each game has its own until the main menu loads again. " + exception);
                    try
                    {
                        electricity.Release();
                    }
                    catch (Exception)
                    {
                        // Whatever broke may break the clean-up too.
                    }
                }
            }

            if (!mountsFailed && !objectsFailed)
            {
                try
                {
                    mountsSync.LateUpdate(ModRuntime.Network.Now, localPlayer);
                }
                catch (Exception exception)
                {
                    mountsFailed = true;
                    ModRuntime.Log.Error(LogCategory.Fsm, "Sharing what is mounted onto things (the bucket's lid, the diskettes, the battery charger) failed; each game has its own until the main menu loads again. " + exception);
                }
            }

            if (!fireworksFailed && !objectsFailed)
            {
                try
                {
                    fireworks.LateUpdate(ModRuntime.Network.Now, localPlayer);
                }
                catch (Exception exception)
                {
                    fireworksFailed = true;
                    ModRuntime.Log.Error(LogCategory.Fsm, "Sharing the lighting of the rockets failed; a rocket flies only in the game where it is lit until the main menu loads again. " + exception);
                }
            }

            if (!cabinSmokeFailed)
            {
                try
                {
                    cabinSmoke.LateUpdate(ModRuntime.Network.Now, localPlayer);
                }
                catch (Exception exception)
                {
                    cabinSmokeFailed = true;
                    try
                    {
                        cabinSmoke.Release();
                    }
                    catch (Exception)
                    {
                        // Already failing: what it drew goes with the world.
                    }

                    ModRuntime.Log.Error(LogCategory.Vehicle, "The smoke in the cabins failed; there is none until the main menu loads again. " + exception);
                }
            }

            if (!toolsFailed)
            {
                try
                {
                    tools.LateUpdate(ModRuntime.Network.Now, localPlayer);
                }
                catch (Exception exception)
                {
                    toolsFailed = true;
                    ModRuntime.Log.Error(LogCategory.Fsm, "Sharing the hoist's arm and the jack's lift failed; each game has its own until the main menu loads again. " + exception);
                }
            }

            if (!storeFailed)
            {
                try
                {
                    store.LateUpdate(ModRuntime.Network.Now, localPlayer);
                }
                catch (Exception exception)
                {
                    storeFailed = true;
                    ModRuntime.Log.Error(LogCategory.Fsm, "Sharing the shop's shelves and till failed; they are each game's own until the main menu loads again. " + exception);
                }
            }

            if (!moneyFailed)
            {
                try
                {
                    money.LateUpdate(ModRuntime.Network.Now, localPlayer);
                }
                catch (Exception exception)
                {
                    moneyFailed = true;
                    ModRuntime.Log.Error(LogCategory.Fsm, "Sharing the money failed; each game has its own until the main menu loads again. " + exception);
                }
            }
        }

        private void OnGUI()
        {
            DrawScreens();

            // Last, over everything else of the mod's.
            if (!chatFailed)
            {
                try
                {
                    chat.OnGUI();
                }
                catch (Exception exception)
                {
                    ChatFailed(exception);
                }
            }
        }

        private void DrawScreens()
        {
            if (!HostWorld.Failed)
            {
                try
                {
                    HostWorld.OnGUI();
                }
                catch (Exception exception)
                {
                    HostWorld.Disable(exception);
                }
            }

            if (!deathsFailed && deaths != null)
            {
                try
                {
                    deaths.OnGUI();
                }
                catch (Exception exception)
                {
                    deathsFailed = true;
                    ModRuntime.Log.Error(LogCategory.Player, "Drawing the screen of a death failed; until the main menu loads again a death is the game's alone. " + exception);
                }
            }

            if (playersFailed)
            {
                return;
            }

            try
            {
                players.OnGUI();
            }
            catch (Exception exception)
            {
                DisablePlayers(exception);
            }

            // The lobby's names first: the character window, drawn after, covers them.
            if (!lobbyFailed)
            {
                try
                {
                    lobby.OnGUI();
                }
                catch (Exception exception)
                {
                    lobbyFailed = true;
                    ModRuntime.Log.Error(LogCategory.Player, "Naming the players in the main menu failed; it is off until the main menu loads again. " + exception);
                }
            }

            if (!pickerFailed)
            {
                try
                {
                    picker.OnGUI(!worldsFailed && (worldWindow.IsOpen || worldWindow.Creating));
                }
                catch (Exception exception)
                {
                    PickerFailed(exception);
                }
            }

            if (!worldsFailed)
            {
                try
                {
                    worldWindow.OnGUI(MenuColumn.InterfaceShown, !pickerFailed && picker.IsOpen);
                }
                catch (Exception exception)
                {
                    WorldsFailed(exception);
                }
            }

            if (!updaterFailed)
            {
                try
                {
                    Core.Net.NetSession session = ModRuntime.Network.Manager.Session;
                    // Not over a window of the mod's, or the game's licence.
                    bool covered = (!worldsFailed && (worldWindow.IsOpen || worldWindow.Creating)) || (!pickerFailed && picker.IsOpen) || !MenuColumn.InterfaceShown;
                    ModUpdater.OnGUI(HostWorld.InMenu && !covered, session != null && session.State != Core.Net.SessionState.Idle);
                }
                catch (Exception exception)
                {
                    updaterFailed = true;
                    ModRuntime.Log.Error(LogCategory.General, "Drawing the main menu's offer to update failed. " + exception);
                }
            }

            if (!objectsFailed)
            {
                try
                {
                    objects.OnGUI();
                }
                catch (Exception exception)
                {
                    objectsFailed = true;
                    ModRuntime.Log.Error(LogCategory.Authority, "Labelling the objects failed. " + exception);
                }
            }
        }

        private void ChatFailed(Exception exception)
        {
            chatFailed = true;
            ChatInput.Holding = false;
            ModRuntime.Log.Error(LogCategory.Player, "The chat failed; it is off until the main menu loads again. " + exception);
            try
            {
                chat.Close();
            }
            catch (Exception)
            {
                // Whatever broke may break the clean-up too.
            }
        }

        private void PickerFailed(Exception exception)
        {
            pickerFailed = true;
            ModRuntime.Log.Error(LogCategory.Player, "The character window failed; it is off until the main menu loads again. " + exception);
            try
            {
                picker.Close(false);
            }
            catch (Exception)
            {
                // Whatever broke may break the clean-up too.
            }
        }

        // Drawing the other players must never take the session down with it, nor fill the log every frame.
        private void DisableVehicles(Exception exception)
        {
            vehiclesFailed = true;
            ModRuntime.Log.Error(LogCategory.Vehicle, "Sharing the vehicles failed; they are the game's alone until the main menu loads again. " + exception);
            try
            {
                vehicles.ReleaseAll();
            }
            catch (Exception)
            {
                // Whatever broke may break the clean-up too.
            }
        }

        private void DisablePlayers(Exception exception)
        {
            playersFailed = true;
            ModRuntime.Log.Error(LogCategory.Player, "Showing the other players failed; they stay hidden until the main menu loads again. " + exception);
            try
            {
                players.Clear();
            }
            catch (Exception)
            {
                // Whatever broke may break the clean-up too. The objects go when MSCLoader clears the scene.
            }

            // The vehicles and the loose objects need the local player, which this found: they go back to the game, and
            // what this machine moved to the host, rather than stay as they are (seen in a review on 2026-10-15).
            if (!vehiclesFailed)
            {
                DisableVehicles(exception);
            }

            if (!objectsFailed)
            {
                objectsFailed = true;
                try
                {
                    objects.ReleaseAll();
                }
                catch (Exception)
                {
                    // Whatever broke may break the clean-up too.
                }
            }
        }

        // When the main menu loads after a game, MSCLoader destroys every GameObject on its next update,
        // including the game's SteamManager, which shuts Steam down. This message arrives before that, while
        // Steam can still deliver a goodbye and leave the lobby.
        private void OnLevelWasLoaded(int level)
        {
            players.OnLevelLoaded(Application.loadedLevelName);
            HostWorld.OnLevelLoaded(Application.loadedLevelName);
            if (Application.loadedLevelName != MainMenuLevel)
            {
                leftMainMenu = true;
            }
            else if (leftMainMenu)
            {
                // Only a return counts: a driver created while the menu itself is loading must not react to it.
                ModRuntime.Network.Shutdown("returning to the main menu");
            }
        }

        private void OnApplicationQuit()
        {
            ModRuntime.Network.Shutdown("the game is closing");
        }

        private void OnDestroy()
        {
            // Normally a repeat of one of the two above, and then it does nothing.
            ModRuntime.Network.Shutdown("the mod's game object was destroyed");
        }
    }
}
