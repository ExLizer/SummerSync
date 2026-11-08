using System;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Net.Wire;
using SummerSync.Core.Objects;
using SummerSync.Core.Vehicles;

namespace SummerSync.Core.World
{
    /// <summary>Message types of the world's own state.</summary>
    public static class WorldWire
    {
        /// <summary>Host to client, reliable: the host's <see cref="WorldClock"/>.</summary>
        public const byte MessageClock = VehicleWire.FirstBodyMessage + (ObjectWire.MessagePresent - ObjectWire.MessageRequest) + 1;

        /// <summary>Host to client, reliable: the host's <see cref="WeatherState"/>.</summary>
        public const byte MessageWeather = MessageClock + 1;

        /// <summary>
        /// Host to client, reliable: which channel of the radio, and the host's <see cref="RadioState"/> of it.
        /// The number in between is the log relay's.
        /// </summary>
        public const byte MessageRadio = MessageWeather + 2;
    }

    /// <summary>
    /// Shares the state that belongs to the world and to no object in it: what time it is and what the weather
    /// is doing. The host's world is the world. The host says what it has, now and then and at once when it
    /// changes by more than the passing of time explains; a client is told, and works out from that what the
    /// host has at any later moment.
    ///
    /// Nothing is asked for: the host tells every connected player, in the game world or not, and a player who
    /// loads the world later uses the last thing heard.
    /// </summary>
    public sealed class WorldReplication
    {
        /// <summary>Seconds between two clocks sent while time just passes.</summary>
        public const double ClockInterval = 5.0;

        /// <summary>Seconds between two weathers sent while the front just moves on.</summary>
        public const double WeatherInterval = 10.0;

        /// <summary>Seconds between two radio states sent while the same recording plays on.</summary>
        public const double RadioInterval = 10.0;

        /// <summary>Seconds a recording has to be away from where the clients take it to be for them to be told at once.</summary>
        public const float RadioJump = 1f;

        /// <summary>The station that comes with the game, and the one that plays what each player imported.</summary>
        public const int MainChannel = 0;

        public const int FolkChannel = 1;

        public const int RadioChannels = 2;

        /// <summary>A clock further than this many seconds from where the last one sent would be by now is sent at once.</summary>
        public const double ClockJump = 2.0;

        /// <summary>A front further than this many metres from where the last one sent would be by now is sent at once.</summary>
        public const float WeatherJump = 25f;

        private const double MalformedLogInterval = 5.0;

        private readonly NetSession session;
        private readonly Logger log;
        // The largest message is a radio's: two bytes of kind and channel, its state with a clip name of up to 96 bytes.
        private readonly PacketWriter writer = new PacketWriter(128);
        private readonly PacketReader reader = new PacketReader();

        private double clock;
        private bool detached;
        private double nextMalformedLog = double.NegativeInfinity;

        private bool hasLocalClock;
        private WorldClock localClock;
        private bool clockSent;
        private WorldClock sentClock;
        private double clockSentAt;

        private bool hasLocalWeather;
        private WeatherState localWeather;
        private float localWeatherSpeed;
        private bool weatherSent;
        private WeatherState sentWeather;
        private double weatherSentAt;

        private readonly bool[] hasLocalRadio = new bool[RadioChannels];
        private readonly RadioState[] localRadio = new RadioState[RadioChannels];
        private readonly bool[] radioSent = new bool[RadioChannels];
        private readonly RadioState[] sentRadio = new RadioState[RadioChannels];
        private readonly double[] radioSentAt = new double[RadioChannels];
        private readonly bool[] hasHostRadio = new bool[RadioChannels];
        private readonly RadioState[] hostRadio = new RadioState[RadioChannels];
        private readonly double[] hostRadioAt = new double[RadioChannels];

        private bool hasHostClock;
        private WorldClock hostClock;
        private double hostClockAt;
        private bool hasHostWeather;
        private WeatherState hostWeather;
        private double hostWeatherAt;

        public WorldReplication(NetSession session, Logger log)
        {
            if (session == null)
            {
                throw new ArgumentNullException("session");
            }

            this.session = session;
            this.log = log ?? new Logger();
            session.MessageReceived += OnMessage;
            session.BeforeFlush += OnBeforeFlush;
            session.PlayerJoined += OnPlayerJoined;
        }

        /// <summary>Clocks sent, on the host, or received, on a client.</summary>
        public long Clocks { get; private set; }

        /// <summary>Weathers sent, on the host, or received, on a client.</summary>
        public long Weathers { get; private set; }

        /// <summary>Radio states sent, on the host, or received, on a client.</summary>
        public long Radios { get; private set; }

        /// <summary>World messages that could not be parsed or made no sense.</summary>
        public long Malformed { get; private set; }

        /// <summary>Whether this machine's game is showing a time of day. See <see cref="Show"/>.</summary>
        public bool ShowsClock { get; private set; }

        /// <summary>The time this machine's game shows.</summary>
        public WorldClock ShownClock { get; private set; }

        /// <summary>Whether this machine's game has a weather to show.</summary>
        public bool ShowsWeather { get; private set; }

        /// <summary>The weather this machine's game shows.</summary>
        public WeatherState ShownWeather { get; private set; }

        /// <summary>Whether this machine's world is the one the others copy.</summary>
        public bool IsAuthority
        {
            get { return IsHost; }
        }

        /// <summary>Counts up every time a clock arrives from the host.</summary>
        public int ClockSerial { get; private set; }

        /// <summary>Counts up every time a weather arrives from the host.</summary>
        public int WeatherSerial { get; private set; }

        private bool IsHost
        {
            get { return session.Role == SessionRole.Host; }
        }

        /// <summary>Call once per frame, before the session's own update, with the same clock.</summary>
        public void Update(double now)
        {
            clock = now;
        }

        /// <summary>On the host, once per frame while it has a game world: what time it is there.</summary>
        public void SetClock(WorldClock time)
        {
            localClock = time;
            hasLocalClock = true;
        }

        /// <summary>
        /// On the host, once per frame while it has a game world: where the front is, what falls from it, and how
        /// fast it moves along X in metres per second.
        /// </summary>
        public void SetWeather(WeatherState weather, float speed)
        {
            localWeather = weather;
            localWeatherSpeed = speed;
            hasLocalWeather = true;
        }

        /// <summary>On the host, once per frame while it has a game world: what its radio station has on the air.</summary>
        public void SetRadio(RadioState radio)
        {
            SetRadio(MainChannel, radio);
        }

        /// <summary>The same for one channel of the radio.</summary>
        public void SetRadio(int channel, RadioState radio)
        {
            localRadio[channel] = radio;
            hasLocalRadio[channel] = true;
        }

        /// <summary>
        /// On a client: what the host's station has on the air right now, as far as the last word from the host
        /// tells: the recording has gone on playing since. False until the first one arrives.
        /// </summary>
        public bool TryGetRadio(out RadioState radio)
        {
            return TryGetRadio(MainChannel, out radio);
        }

        /// <summary>The same for one channel of the radio.</summary>
        public bool TryGetRadio(int channel, out RadioState radio)
        {
            if (!hasHostRadio[channel] || IsHost)
            {
                radio = default(RadioState);
                return false;
            }

            radio = hostRadio[channel].Later(clock - hostRadioAt[channel]);
            return true;
        }

        /// <summary>
        /// Once per frame, on any machine: what its own game shows right now, for whoever wants to compare it
        /// with the host's. It changes nothing.
        /// </summary>
        public void Show(bool hasClock, WorldClock time, bool hasWeather, WeatherState weather)
        {
            ShowsClock = hasClock;
            ShownClock = time;
            ShowsWeather = hasWeather;
            ShownWeather = weather;
        }

        /// <summary>This machine left the game world: on the host, there is nothing to tell until it is back.</summary>
        public void ClearWorld()
        {
            ShowsClock = false;
            ShowsWeather = false;
            hasLocalClock = false;
            hasLocalWeather = false;
            clockSent = false;
            weatherSent = false;
            for (int channel = 0; channel < RadioChannels; channel++)
            {
                hasLocalRadio[channel] = false;
                radioSent[channel] = false;
            }
        }

        /// <summary>
        /// On a client: what time the host's world has right now, worked out from the last clock it sent and how
        /// long ago that was. False until the first one arrives.
        /// </summary>
        public bool TryGetClock(out WorldClock time)
        {
            if (!hasHostClock || IsHost)
            {
                time = default(WorldClock);
                return false;
            }

            time = hostClock.Later(clock - hostClockAt);
            return true;
        }

        /// <summary>
        /// On a client: the last weather the host sent, and how many seconds ago. The front has moved on since,
        /// at the speed the caller's own game moves it. False until the first one arrives.
        /// </summary>
        public bool TryGetWeather(out WeatherState weather, out double age)
        {
            if (!hasHostWeather || IsHost)
            {
                weather = default(WeatherState);
                age = 0.0;
                return false;
            }

            weather = hostWeather;
            age = clock - hostWeatherAt;
            return true;
        }

        /// <summary>Stops listening to the session.</summary>
        public void Detach()
        {
            if (detached)
            {
                return;
            }

            detached = true;
            session.MessageReceived -= OnMessage;
            session.BeforeFlush -= OnBeforeFlush;
            session.PlayerJoined -= OnPlayerJoined;
            ClearWorld();
            hasHostClock = false;
            hasHostWeather = false;
            for (int channel = 0; channel < RadioChannels; channel++)
            {
                hasHostRadio[channel] = false;
            }
        }

        private void OnPlayerJoined(PlayerInfo player)
        {
            // Whoever has just come in is told at the next flush, not at the next turn of the interval.
            clockSent = false;
            weatherSent = false;
            for (int channel = 0; channel < RadioChannels; channel++)
            {
                radioSent[channel] = false;
            }
        }

        private void OnBeforeFlush()
        {
            if (!IsHost || session.State != SessionState.Connected || session.Players.Count < 2)
            {
                return;
            }

            if (hasLocalClock && ClockIsDue())
            {
                writer.Reset();
                writer.WriteByte(WorldWire.MessageClock);
                localClock.Write(writer);
                session.Send(Protocol.Broadcast, writer.Buffer, 0, writer.Length, true);
                sentClock = localClock;
                clockSentAt = clock;
                clockSent = true;
                Clocks++;
            }

            if (hasLocalWeather && WeatherIsDue())
            {
                writer.Reset();
                writer.WriteByte(WorldWire.MessageWeather);
                localWeather.Write(writer);
                session.Send(Protocol.Broadcast, writer.Buffer, 0, writer.Length, true);
                sentWeather = localWeather;
                weatherSentAt = clock;
                weatherSent = true;
                Weathers++;
            }

            for (int channel = 0; channel < RadioChannels; channel++)
            {
                if (hasLocalRadio[channel] && RadioIsDue(channel))
                {
                    writer.Reset();
                    writer.WriteByte(WorldWire.MessageRadio);
                    writer.WriteByte((byte)channel);
                    localRadio[channel].Write(writer);
                    session.Send(Protocol.Broadcast, writer.Buffer, 0, writer.Length, true);
                    sentRadio[channel] = localRadio[channel];
                    radioSentAt[channel] = clock;
                    radioSent[channel] = true;
                    Radios++;
                }
            }
        }

        private bool RadioIsDue(int channel)
        {
            if (!radioSent[channel] || clock - radioSentAt[channel] >= RadioInterval || !localRadio[channel].SameRecording(sentRadio[channel]))
            {
                return true;
            }

            // Where the clients take the recording to be by now. A recording that starts over is elsewhere.
            return Math.Abs(localRadio[channel].Position - sentRadio[channel].Later(clock - radioSentAt[channel]).Position) > RadioJump;
        }

        private bool ClockIsDue()
        {
            if (!clockSent || clock - clockSentAt >= ClockInterval)
            {
                return true;
            }

            // What the clients take the time to be by now. Sleeping, or loading a save, moves it elsewhere.
            WorldClock expected = sentClock.Later(clock - clockSentAt);
            return Math.Abs(localClock.Ahead(expected)) > ClockJump;
        }

        private bool WeatherIsDue()
        {
            if (!weatherSent || clock - weatherSentAt >= WeatherInterval || !localWeather.SameFront(sentWeather))
            {
                return true;
            }

            double expected = sentWeather.X + (localWeatherSpeed * (clock - weatherSentAt));
            return Math.Abs(localWeather.X - expected) > WeatherJump;
        }

        private void OnMessage(byte sender, byte[] data, bool reliable)
        {
            byte type = data[0];
            if (type != WorldWire.MessageClock && type != WorldWire.MessageWeather && type != WorldWire.MessageRadio)
            {
                return;
            }

            try
            {
                if (IsHost || sender != Protocol.HostPlayerId)
                {
                    throw new PacketFormatException("The state of the world sent by someone who is not the host.");
                }

                reader.SetBuffer(data, 1, data.Length - 1);
                if (type == WorldWire.MessageClock)
                {
                    hostClock = WorldClock.Read(reader);
                    hostClockAt = clock;
                    hasHostClock = true;
                    ClockSerial++;
                    Clocks++;
                }
                else if (type == WorldWire.MessageRadio)
                {
                    int channel = reader.ReadByte();
                    if (channel >= RadioChannels)
                    {
                        throw new PacketFormatException("Channel " + channel + " of a radio with " + RadioChannels + ".");
                    }

                    hostRadio[channel] = RadioState.Read(reader);
                    hostRadioAt[channel] = clock;
                    hasHostRadio[channel] = true;
                    Radios++;
                }
                else
                {
                    hostWeather = WeatherState.Read(reader);
                    hostWeatherAt = clock;
                    hasHostWeather = true;
                    WeatherSerial++;
                    Weathers++;
                }
            }
            catch (PacketFormatException exception)
            {
                Malformed++;
                if (clock >= nextMalformedLog)
                {
                    nextMalformedLog = clock + MalformedLogInterval;
                    log.Warning(LogCategory.Fsm, "Dropped a world message from player " + sender + " (" + Malformed + " so far): " + exception.Message);
                }
            }
        }
    }
}
