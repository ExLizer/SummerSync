using System;
using System.Collections.Generic;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;
using SummerSync.Core.Net.Wire;

namespace SummerSync.Core.World
{
    /// <summary>
    /// The book of the car's numbers that change by themselves: fluids, wear, the battery's charge, the adjustments.
    ///
    /// They are the game's of the machine that runs the car (the one that simulates it), which the host names
    /// (<see cref="SetValuesOwner"/>; the host itself until then). That machine says the numbers that changed, at most
    /// four times a second (<see cref="ValueInterval"/>), and the smaller changes within <see cref="ValueRefresh"/>.
    /// It says them reliably and in the same order as the steps: there are few of them, they change slowly, and every
    /// machine has to end with exactly the owner's numbers, so a lost message is sent again instead of waiting for a
    /// later one, and a number never arrives before the step that put its part on.
    ///
    /// A player of another machine who changes a number (pours oil) sees it change at once; the change goes to the
    /// host, which numbers it and passes it to the machine that runs the car, whose game adds it. Each of that
    /// machine's messages says up to which of those numbers it holds, and the host tells each player up to which of
    /// its own changes that is, so each machine shows the last word and its own changes not held in it yet, and nothing
    /// counts twice. A machine that starts running the car starts from what it showed, and the host sends it every
    /// change of the others the last word did not hold; what the machine that ran the car before said after that is
    /// no longer the word.
    /// </summary>
    public sealed partial class AssemblyReplication
    {
        // More changes of the numbers than this do not fit in one message.
        private const int MaxNumbersPerMessage = 256;

        private readonly List<OwnDelta> ownDeltas = new List<OwnDelta>();
        private readonly List<HostDelta> uncounted = new List<HostDelta>();
        private readonly Dictionary<byte, uint> countedFor = new Dictionary<byte, uint>();

        // What one message of numbers brings, read whole before any of it is used; kept, so that nothing is allocated.
        private readonly int[] readIndex = new int[MaxNumbersPerMessage];
        private readonly float[] readNumber = new float[MaxNumbersPerMessage];
        private readonly uint[] readGlobal = new uint[MaxNumbersPerMessage];
        private float[] current = new float[0];
        private bool[] hasCurrent = new bool[0];
        private float[] lastSent = new float[0];
        private bool[] sentOnce = new bool[0];
        private float[] word = new float[0];
        private bool[] hasWord = new bool[0];
        private float[] shownValues = new float[0];
        private bool[] everShown = new bool[0];
        private float[] sums = new float[0];
        private byte valuesOwner = Protocol.HostPlayerId;
        private uint absorbed;
        private uint wordAbsorbed;
        private uint nextDeltaNumber = 1;
        private uint nextGlobal = 1;
        private bool wordDue;
        private bool fullWordDue;
        private double nextWordAt;
        private double nextRefreshAt;

        // ---- The book of the car's numbers ----

        // The last player refused as the owner of the numbers, so that the log says it once.
        private byte ownerRefusalSaid = AssemblyWire.NoPlayer;

        /// <summary>
        /// On the host: the machine whose game runs the car's numbers from now on (the one that simulates the car). It
        /// starts from the last numbers said and every change of the others since. Returns false for a player who has
        /// not asked for the car.
        /// </summary>
        public bool SetValuesOwner(byte player)
        {
            if (!IsHost || catalog == null)
            {
                return false;
            }

            if (player == valuesOwner)
            {
                return true;
            }

            if (player != session.LocalPlayerId && !subscribers.Contains(player))
            {
                // Not a fault: their world may still be loading. Said once per player, not every time it is asked.
                if (ownerRefusalSaid != player)
                {
                    ownerRefusalSaid = player;
                    log.Info(LogCategory.Vehicle, "Car: player " + player + " cannot run the car's numbers until they have asked for the car.");
                }

                return false;
            }

            SendBatch();
            if (valuesOwner == session.LocalPlayerId)
            {
                // What this game has not said yet goes before someone else takes over.
                SendWord(true, clock);
            }

            valuesOwner = player;
            writer.Reset();
            writer.WriteByte(AssemblyWire.MessageOwner);
            writer.WriteUInt32(shape);
            writer.WriteByte(player);
            writer.WriteUInt32(wordAbsorbed);
            SendToSubscribers();
            if (player == session.LocalPlayerId)
            {
                TakeOverValues();
            }
            else
            {
                // The new owner's own changes are in its game already; everyone else's go to it.
                for (int i = uncounted.Count - 1; i >= 0; i--)
                {
                    if (uncounted[i].Player == player)
                    {
                        uncounted.RemoveAt(i);
                    }
                    else
                    {
                        HostDelta delta = uncounted[i];
                        delta.Forwarded = false;
                        uncounted[i] = delta;
                    }
                }
            }

            log.Info(LogCategory.Vehicle, "Car: player " + player + " runs the car's numbers now.");
            RaiseOwner(player);
            return true;
        }

        /// <summary>On the machine that runs the car: what one of its numbers is in its game now. Call it when it changes, or every frame.</summary>
        public void SetValue(int index, float value)
        {
            if (catalog == null || index < 0 || index >= current.Length || float.IsNaN(value) || float.IsInfinity(value))
            {
                return;
            }

            current[index] = value;
            hasCurrent[index] = true;
        }

        /// <summary>
        /// On a machine that does not run the car: this game's player changed a number by <paramref name="amount"/>
        /// (poured oil into the engine). It stands here already; the machine that runs the car is told. Returns false
        /// when it is not told.
        /// </summary>
        public bool AddToValue(int index, float amount)
        {
            if (catalog == null || index < 0 || index >= current.Length || float.IsNaN(amount) || float.IsInfinity(amount) || amount == 0f || OwnsValues || Mismatch)
            {
                return false;
            }

            Poured++;
            shownValues[index] += amount;
            uint number = nextDeltaNumber++;
            if (IsHost)
            {
                uncounted.Add(new HostDelta { Global = nextGlobal++, Player = session.LocalPlayerId, Number = number, Index = index, Amount = amount });
                return true;
            }

            ownDeltas.Add(new OwnDelta { Number = number, Index = index, Amount = amount });
            return true;
        }

        /// <summary>Whether this machine knows a number of the car: its own on the machine that runs it, what was said on the others.</summary>
        public bool HasValue(int index)
        {
            if (catalog == null || index < 0 || index >= current.Length)
            {
                return false;
            }

            return OwnsValues ? hasCurrent[index] : hasWord[index];
        }

        /// <summary>A number of the car as this machine has it: its game's on the machine that runs it; elsewhere what was said, and this player's changes not held in it yet.</summary>
        public float ShownValue(int index)
        {
            if (catalog == null || index < 0 || index >= current.Length)
            {
                return 0f;
            }

            return OwnsValues ? current[index] : shownValues[index];
        }

        /// <summary>A number made from every number of the car as this machine has it, for comparing machines.</summary>
        public ulong ValuesDigest()
        {
            ulong hash = Hashing.Fnv1a.OffsetBasis;
            var bits = default(FloatBits);
            for (int i = 0; i < current.Length; i++)
            {
                bool has = HasValue(i);
                hash = Mix(hash, has ? (byte)1 : (byte)0);
                if (has)
                {
                    bits.Float = ShownValue(i);
                    hash = Mix(hash, (byte)bits.Bits);
                    hash = Mix(hash, (byte)(bits.Bits >> 8));
                    hash = Mix(hash, (byte)(bits.Bits >> 16));
                    hash = Mix(hash, (byte)(bits.Bits >> 24));
                }
            }

            return hash;
        }

        // ---- The book of numbers: the messages ----

        private void SendPoured()
        {
            int start = 0;
            while (start < ownDeltas.Count && ownDeltas[start].Sent)
            {
                start++;
            }

            while (start < ownDeltas.Count)
            {
                writer.Reset();
                writer.WriteByte(AssemblyWire.MessagePoured);
                writer.WriteUInt32(shape);
                writer.WriteUInt32(ownDeltas[start].Number);
                int countAt = writer.Length;
                writer.WriteUInt16(0);
                int count = 0;
                while (start < ownDeltas.Count && writer.Length + 10 <= AssemblyWire.MessageBytes)
                {
                    OwnDelta each = ownDeltas[start];
                    writer.WriteVarUInt32((uint)each.Index);
                    writer.WriteSingle(each.Amount);
                    each.Sent = true;
                    ownDeltas[start] = each;
                    start++;
                    count++;
                }

                writer.Buffer[countAt] = (byte)count;
                writer.Buffer[countAt + 1] = (byte)(count >> 8);
                session.Send(Protocol.HostPlayerId, writer.Buffer, 0, writer.Length, true);
            }
        }

        // Host: the others' changes the client that runs the car has not been sent yet.
        private void Forward()
        {
            if (valuesOwner == session.LocalPlayerId || !subscribers.Contains(valuesOwner))
            {
                return;
            }

            int start = 0;
            while (start < uncounted.Count)
            {
                if (uncounted[start].Forwarded || uncounted[start].Player == valuesOwner)
                {
                    start++;
                    continue;
                }

                writer.Reset();
                writer.WriteByte(AssemblyWire.MessageAbsorb);
                writer.WriteUInt32(shape);
                int countAt = writer.Length;
                writer.WriteUInt16(0);
                int count = 0;
                while (start < uncounted.Count && writer.Length + 15 <= AssemblyWire.MessageBytes)
                {
                    HostDelta each = uncounted[start];
                    if (!each.Forwarded && each.Player != valuesOwner)
                    {
                        writer.WriteUInt32(each.Global);
                        writer.WriteByte(each.Player);
                        writer.WriteVarUInt32((uint)each.Index);
                        writer.WriteSingle(each.Amount);
                        each.Forwarded = true;
                        uncounted[start] = each;
                        count++;
                    }

                    start++;
                }

                writer.Buffer[countAt] = (byte)count;
                writer.Buffer[countAt + 1] = (byte)(count >> 8);
                session.Send(valuesOwner, writer.Buffer, 0, writer.Length, true);
            }
        }

        // The machine that runs the car: what changed of its numbers, at most every ValueInterval.
        private void SendWord(bool force, double now)
        {
            bool refresh = now >= nextRefreshAt;
            if (!force && !fullWordDue && now < nextWordAt)
            {
                return;
            }

            writer.Reset();
            writer.WriteByte(AssemblyWire.MessageValues);
            writer.WriteUInt32(shape);
            writer.WriteByte(session.LocalPlayerId);
            writer.WriteUInt32(absorbed);
            writer.WriteUInt32(0);
            writer.WriteByte(fullWordDue ? (byte)1 : (byte)0);
            int countAt = writer.Length;
            writer.WriteUInt16(0);
            int count = 0;
            for (int i = 0; i < current.Length; i++)
            {
                if (!hasCurrent[i])
                {
                    continue;
                }

                bool changed = current[i] != lastSent[i] || !sentOnce[i];
                bool big = !sentOnce[i] || Math.Abs(current[i] - lastSent[i]) >= catalog.ValueStep(i);
                if (fullWordDue || (changed && (big || refresh || force)))
                {
                    writer.WriteVarUInt32((uint)i);
                    writer.WriteSingle(current[i]);
                    lastSent[i] = current[i];
                    sentOnce[i] = true;
                    count++;
                }
            }

            if (refresh)
            {
                nextRefreshAt = now + ValueRefresh;
            }

            if (count == 0 && !wordDue)
            {
                return;
            }

            writer.Buffer[countAt] = (byte)count;
            writer.Buffer[countAt + 1] = (byte)(count >> 8);
            nextWordAt = now + ValueInterval;
            wordDue = false;
            fullWordDue = false;
            if (!IsHost)
            {
                session.Send(Protocol.HostPlayerId, writer.Buffer, 0, writer.Length, true);
                return;
            }

            // The host's word is the word: kept for whoever asks for the car later, and told to each client with how
            // many of its own changes it holds.
            for (int i = 0; i < current.Length; i++)
            {
                if (sentOnce[i])
                {
                    word[i] = lastSent[i];
                    hasWord[i] = true;
                }
            }

            wordAbsorbed = absorbed;
            RelayWord(Protocol.HostPlayerId);
        }

        // Host: the word in the writer to every client that asked for the car but the one that said it, each with its own count.
        private void RelayWord(byte except)
        {
            for (int i = 0; i < subscribers.Count; i++)
            {
                byte client = subscribers[i];
                if (client == except)
                {
                    continue;
                }

                uint counted;
                countedFor.TryGetValue(client, out counted);
                writer.Buffer[10] = (byte)counted;
                writer.Buffer[11] = (byte)(counted >> 8);
                writer.Buffer[12] = (byte)(counted >> 16);
                writer.Buffer[13] = (byte)(counted >> 24);
                session.Send(client, writer.Buffer, 0, writer.Length, true);
            }
        }

        private void OnValues(byte sender, byte[] data)
        {
            byte owner = reader.ReadByte();
            uint heldAt = reader.ReadUInt32();
            uint counted = reader.ReadUInt32();
            reader.ReadByte();
            int count = reader.ReadUInt16();
            if (count > word.Length)
            {
                throw new PacketFormatException(count + " numbers of a car of " + word.Length + ".");
            }

            for (int i = 0; i < count; i++)
            {
                readIndex[i] = ReadValueIndex();
                readNumber[i] = ReadNumber();
            }

            if (IsHost)
            {
                if (owner != sender || sender != valuesOwner || !subscribers.Contains(sender))
                {
                    // A machine that ran the car before someone else took over: what it said last is not the word.
                    return;
                }
            }
            else if (sender != Protocol.HostPlayerId)
            {
                throw new PacketFormatException("The car's numbers from someone who is not the host.");
            }
            else if (OwnsValues)
            {
                return;
            }

            for (int i = 0; i < count; i++)
            {
                word[readIndex[i]] = readNumber[i];
                hasWord[readIndex[i]] = true;
            }

            wordAbsorbed = heldAt;
            if (IsHost)
            {
                for (int i = uncounted.Count - 1; i >= 0; i--)
                {
                    HostDelta delta = uncounted[i];
                    if (delta.Global > heldAt)
                    {
                        continue;
                    }

                    if (delta.Player != AssemblyWire.NoPlayer)
                    {
                        uint already;
                        countedFor.TryGetValue(delta.Player, out already);
                        countedFor[delta.Player] = Math.Max(already, delta.Number);
                    }

                    uncounted.RemoveAt(i);
                }

                writer.Reset();
                writer.WriteBytes(data, 0, data.Length);
                RelayWord(sender);
            }
            else
            {
                for (int i = ownDeltas.Count - 1; i >= 0; i--)
                {
                    if (ownDeltas[i].Number <= counted)
                    {
                        ownDeltas.RemoveAt(i);
                    }
                }
            }

            ShowValues();
        }

        private void OnPoured(byte sender)
        {
            uint first = reader.ReadUInt32();
            int count = ReadNumbersCount();
            for (int i = 0; i < count; i++)
            {
                readIndex[i] = ReadValueIndex();
                readNumber[i] = ReadNumber();
            }

            if (!subscribers.Contains(sender))
            {
                return;
            }

            for (int i = 0; i < count; i++)
            {
                uint number = first + (uint)i;
                if (sender == valuesOwner)
                {
                    // Said while it did not run the car, heard once it does: its game has it already.
                    continue;
                }

                uint global = nextGlobal++;
                if (valuesOwner == session.LocalPlayerId)
                {
                    TakeIn(readIndex[i], readNumber[i], global);
                    countedFor[sender] = number;
                    continue;
                }

                uncounted.Add(new HostDelta { Global = global, Player = sender, Number = number, Index = readIndex[i], Amount = readNumber[i] });
            }
        }

        private void OnAbsorb()
        {
            int count = ReadNumbersCount();
            for (int i = 0; i < count; i++)
            {
                readGlobal[i] = reader.ReadUInt32();
                reader.ReadByte();
                readIndex[i] = ReadValueIndex();
                readNumber[i] = ReadNumber();
            }

            if (!OwnsValues)
            {
                // Sent before someone else took over: the host sends it to them.
                return;
            }

            for (int i = 0; i < count; i++)
            {
                if (readGlobal[i] > absorbed)
                {
                    TakeIn(readIndex[i], readNumber[i], readGlobal[i]);
                }
            }
        }

        private void OnOwner()
        {
            byte owner = reader.ReadByte();
            uint heldAt = reader.ReadUInt32();
            if (!synced || owner == valuesOwner)
            {
                return;
            }

            valuesOwner = owner;
            wordAbsorbed = heldAt;
            if (owner == session.LocalPlayerId)
            {
                TakeOverValues();
            }

            log.Info(LogCategory.Vehicle, "Car: player " + owner + " runs the car's numbers now" + (owner == session.LocalPlayerId ? " (this game)." : "."));
            RaiseOwner(owner);
        }

        // This machine runs the car's numbers from now on: it starts from what it showed (the last word and its own
        // changes), and the host sends it everyone else's changes since that word.
        private void TakeOverValues()
        {
            absorbed = wordAbsorbed;
            for (int i = 0; i < current.Length; i++)
            {
                if (hasWord[i])
                {
                    current[i] = shownValues[i];
                    hasCurrent[i] = true;
                    RaiseValue(i, current[i]);
                }
            }

            ownDeltas.Clear();
            if (IsHost)
            {
                byte me = session.LocalPlayerId;
                for (int i = 0; i < uncounted.Count; i++)
                {
                    HostDelta delta = uncounted[i];
                    if (delta.Player != me)
                    {
                        TakeIn(delta.Index, delta.Amount, delta.Global);
                    }

                    if (delta.Player != AssemblyWire.NoPlayer && delta.Player != me)
                    {
                        uint already;
                        countedFor.TryGetValue(delta.Player, out already);
                        countedFor[delta.Player] = Math.Max(already, delta.Number);
                    }
                }

                uncounted.Clear();
            }

            fullWordDue = true;
            wordDue = true;
        }

        // The machine that runs the car adds another player's change: its game is told, and its next word holds it.
        private void TakeIn(int index, float amount, uint global)
        {
            Absorbed++;
            absorbed = Math.Max(absorbed, global);
            if (hasCurrent[index])
            {
                current[index] += amount;
            }

            wordDue = true;
            Action<int, float> handler = ValueDeltaWanted;
            if (handler != null)
            {
                handler(index, amount);
            }
        }

        // A machine that does not run the car: what it shows is the last word and its own changes not held in it yet.
        private void ShowValues()
        {
            if (OwnsValues)
            {
                return;
            }

            for (int i = 0; i < sums.Length; i++)
            {
                sums[i] = 0f;
            }

            if (IsHost)
            {
                byte me = session.LocalPlayerId;
                for (int i = 0; i < uncounted.Count; i++)
                {
                    if (uncounted[i].Player == me)
                    {
                        sums[uncounted[i].Index] += uncounted[i].Amount;
                    }
                }
            }
            else
            {
                for (int i = 0; i < ownDeltas.Count; i++)
                {
                    sums[ownDeltas[i].Index] += ownDeltas[i].Amount;
                }
            }

            for (int i = 0; i < shownValues.Length; i++)
            {
                if (!hasWord[i])
                {
                    continue;
                }

                float value = sums[i] == 0f ? word[i] : word[i] + sums[i];
                if (value != shownValues[i] || !everShown[i])
                {
                    // The first word of a number is always handed to the game, whatever this machine had.
                    shownValues[i] = value;
                    everShown[i] = true;
                    RaiseValue(i, value);
                }
            }
        }

        private int ReadNumbersCount()
        {
            int count = reader.ReadUInt16();
            if (count > MaxNumbersPerMessage)
            {
                throw new PacketFormatException(count + " changes of the car's numbers in one message.");
            }

            return count;
        }

        private int ReadValueIndex()
        {
            uint index = reader.ReadVarUInt32();
            if (index >= (uint)word.Length)
            {
                throw new PacketFormatException("Number " + index + " of a car of " + word.Length + ".");
            }

            return (int)index;
        }

        private float ReadNumber()
        {
            float value = reader.ReadSingle();
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                throw new PacketFormatException("A number of the car that is not a number.");
            }

            return value;
        }

        private void RaiseValue(int index, float value)
        {
            Action<int, float> handler = ValueWanted;
            if (handler != null)
            {
                handler(index, value);
            }
        }

        private void RaiseOwner(byte owner)
        {
            Action<byte> handler = ValuesOwnerChanged;
            if (handler != null)
            {
                handler(owner);
            }
        }

        private struct OwnDelta
        {
            public uint Number;
            public int Index;
            public float Amount;
            public bool Sent;
        }

        private struct HostDelta
        {
            public uint Global;
            public byte Player;
            public uint Number;
            public int Index;
            public float Amount;
            public bool Forwarded;
        }
    }
}
