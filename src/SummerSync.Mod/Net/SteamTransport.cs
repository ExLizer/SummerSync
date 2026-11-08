using System;
using System.Collections.Generic;
using System.Globalization;
using Steamworks;
using SummerSync.Core.Logging;
using SummerSync.Core.Net;

namespace SummerSync.Unity.Net
{
    /// <summary>
    /// Datagrams over Steam's classic P2P networking, using the Steamworks.NET that ships with the game.
    /// A peer id is a Steam ID. Everything is sent unreliable: reliability comes from <see cref="Connection"/>.
    ///
    /// This is the one part of the network that cannot be tried without a second Steam account, so it logs
    /// what the link is doing: when the first datagram from a peer arrives, how Steam routed it, what state
    /// the P2P session is in while nothing arrives, and the totals when it closes.
    /// </summary>
    internal sealed class SteamTransport : ITransport, IDisposable
    {
        public const string Name = "steam";

        private const int Channel = 0;
        private const int MaxDeferredRequests = 16;
        private const int MaxLeftovers = 10000;
        private const double SilenceReportSeconds = 2.0;
        private const double SilenceReportsEndAfter = 60.0;

        private readonly Logger log;
        private readonly Predicate<ulong> isAllowedPeer;
        private readonly Callback<P2PSessionRequest_t> sessionRequest;
        private readonly Callback<P2PSessionConnectFail_t> sessionFailed;
        // Peer -> age of the transport, in seconds, when we first sent to it or accepted its session. A host can
        // sit in its lobby for minutes before anyone arrives, so waiting times are counted from here.
        private readonly Dictionary<ulong, double> peers = new Dictionary<ulong, double>();
        private readonly HashSet<ulong> heardFrom = new HashSet<ulong>();
        private readonly List<ulong> deferred = new List<ulong>();
        private readonly SteadyClock age = new SteadyClock();
        private double nextSilenceReport = SilenceReportSeconds;
        private byte[] oversized = new byte[0];
        private long sent;
        private long received;
        private bool disposed;

        /// <param name="isAllowedPeer">Decides who may open a P2P session with us; normally "is in my lobby".</param>
        public SteamTransport(Logger log, Predicate<ulong> isAllowedPeer)
        {
            this.log = log;
            this.isAllowedPeer = isAllowedPeer;
            sessionRequest = Callback<P2PSessionRequest_t>.Create(OnSessionRequest);
            sessionFailed = Callback<P2PSessionConnectFail_t>.Create(OnSessionFailed);

            int leftovers = DiscardPending();
            if (leftovers > 0)
            {
                log.Info(LogCategory.Net, "Discarded " + leftovers + " datagram(s) that Steam was still holding from an earlier session.");
            }
        }

        /// <summary>
        /// Steam's limit for unreliable sends is documented as 1200 bytes; this stays below it. The real limit
        /// in this game's Steamworks version is still to be measured.
        /// </summary>
        public int MaxDatagramSize
        {
            get { return 1100; }
        }

        /// <summary>Sends Steam refused outright. Seeing this grow means the P2P session is not usable.</summary>
        public long SendFailures { get; private set; }

        public void Send(ulong peer, byte[] data, int count)
        {
            Track(peer);
            sent++;
            try
            {
                // Plain unreliable rather than the "no delay" variant: that one drops packets while the P2P session
                // is still being negotiated, which is exactly when the connect requests go out.
                if (!SteamNetworking.SendP2PPacket(new CSteamID(peer), data, (uint)count, EP2PSend.k_EP2PSendUnreliable, Channel))
                {
                    SendFailures++;
                }
            }
            catch (InvalidOperationException)
            {
                // Steam is not running any more. To the session this is a lost datagram, which it survives or
                // times out on; an exception here would leave it half closed.
                SendFailures++;
            }
        }

        public bool Receive(byte[] buffer, out int count, out ulong peer)
        {
            try
            {
                ReportSilentPeers();
                if (!Read(buffer, out count, out peer))
                {
                    return false;
                }
            }
            catch (InvalidOperationException)
            {
                // Steam is not running any more: nothing will arrive.
                count = 0;
                peer = 0;
                return false;
            }

            received++;
            if (heardFrom.Add(peer))
            {
                log.Info(
                    LogCategory.Net,
                    "First datagram from " + peer + " over Steam, " + Format(SecondsKnown(peer)) + " s after first contact: " + DescribeSession(peer) + ".");
            }

            return true;
        }

        /// <summary>
        /// Looks again at the session requests that were turned down because the sender was not in the lobby
        /// yet. A request can overtake the news that its sender joined, so call this when the lobby changes.
        /// </summary>
        public void RetryDeferredRequests()
        {
            for (int i = deferred.Count - 1; i >= 0; i--)
            {
                if (isAllowedPeer(deferred[i]))
                {
                    Accept(deferred[i]);
                    deferred.RemoveAt(i);
                }
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            sessionRequest.Dispose();
            sessionFailed.Dispose();
            log.Info(
                LogCategory.Net,
                "Steam transport closed after " + Format(age.Seconds) + " s: " + sent + " datagram(s) sent, " + received + " received, " + SendFailures + " refused by Steam.");
            foreach (ulong peer in peers.Keys)
            {
                log.Info(LogCategory.Net, "Steam P2P session with " + peer + " at closing: " + DescribeSession(peer) + ".");
            }

            // The P2P sessions themselves are left to Steam, which drops idle ones. Closing them here, right
            // after the goodbye was handed to Steam, risks throwing that goodbye away before it is sent, and
            // then the other side waits for a timeout instead of seeing us leave. Whether Steam really discards
            // unsent packets on close is not known for this version; not closing avoids having to know.
            peers.Clear();
        }

        // Steam's queue of received packets belongs to the game, not to this object: whatever an earlier session
        // did not read is still there, and it comes from the same Steam IDs. On 2026-10-12 two goodbyes left over
        // from a closed session ended the next two attempts to join. The session now ignores packets of another
        // connection by itself; reading them away first spares it the work and the log the noise.
        private int DiscardPending()
        {
            int discarded = 0;
            try
            {
                uint size;
                while (discarded < MaxLeftovers && SteamNetworking.IsP2PPacketAvailable(out size, Channel))
                {
                    if (oversized.Length < size || oversized.Length == 0)
                    {
                        oversized = new byte[Math.Max(size, 1u)];
                    }

                    uint read;
                    CSteamID remote;
                    if (!SteamNetworking.ReadP2PPacket(oversized, (uint)oversized.Length, out read, out remote, Channel))
                    {
                        break;
                    }

                    discarded++;
                }
            }
            catch (InvalidOperationException)
            {
                // Steam is not running: there is nothing to discard.
            }

            return discarded;
        }

        private bool Read(byte[] buffer, out int count, out ulong peer)
        {
            count = 0;
            peer = 0;

            uint size;
            if (!SteamNetworking.IsP2PPacketAvailable(out size, Channel))
            {
                return false;
            }

            // Something larger than we ever send: read it out of the queue and hand up an empty datagram, which
            // the session counts as malformed.
            byte[] target = buffer;
            if (size > buffer.Length)
            {
                if (oversized.Length < size)
                {
                    oversized = new byte[size];
                }

                target = oversized;
            }

            uint read;
            CSteamID remote;
            if (!SteamNetworking.ReadP2PPacket(target, (uint)target.Length, out read, out remote, Channel))
            {
                return false;
            }

            peer = remote.m_SteamID;
            count = ReferenceEquals(target, buffer) ? (int)read : 0;
            return true;
        }

        // While a peer we are sending to has not answered at all, say every couple of seconds what Steam
        // thinks of the link. This is what tells "still negotiating" from "failed" from "wrong peer" in a log.
        private void ReportSilentPeers()
        {
            double now = age.Seconds;
            if (now < nextSilenceReport)
            {
                return;
            }

            nextSilenceReport = now + SilenceReportSeconds;
            foreach (KeyValuePair<ulong, double> peer in peers)
            {
                double waited = now - peer.Value;
                if (!heardFrom.Contains(peer.Key) && waited >= SilenceReportSeconds && waited <= SilenceReportsEndAfter)
                {
                    log.Info(
                        LogCategory.Net,
                        "Nothing from " + peer.Key + " yet, " + Format(waited) + " s after first contact: " + DescribeSession(peer.Key) + ".");
                }
            }
        }

        private void Track(ulong peer)
        {
            if (!peers.ContainsKey(peer))
            {
                peers[peer] = age.Seconds;
            }
        }

        private double SecondsKnown(ulong peer)
        {
            double since;
            return peers.TryGetValue(peer, out since) ? age.Seconds - since : 0.0;
        }

        /// <summary>Steam's view of the P2P session with a peer. Never includes the peer's address.</summary>
        private static string DescribeSession(ulong peer)
        {
            try
            {
                P2PSessionState_t state;
                if (!SteamNetworking.GetP2PSessionState(new CSteamID(peer), out state))
                {
                    return "no P2P session";
                }

                string text;
                if (state.m_bConnectionActive != 0)
                {
                    text = state.m_bUsingRelay != 0 ? "connected through a Steam relay" : "connected directly";
                }
                else
                {
                    text = state.m_bConnecting != 0 ? "connecting" : "not connected";
                }

                if (state.m_eP2PSessionError != 0)
                {
                    text += ", error " + (EP2PSessionError)state.m_eP2PSessionError;
                }

                if (state.m_nPacketsQueuedForSend > 0)
                {
                    text += ", " + state.m_nPacketsQueuedForSend + " packet(s) waiting to be sent";
                }

                return text;
            }
            catch (InvalidOperationException)
            {
                return "Steam is not running";
            }
        }

        private static string Format(double seconds)
        {
            return seconds.ToString("0.0", CultureInfo.InvariantCulture);
        }

        private void OnSessionRequest(P2PSessionRequest_t request)
        {
            ulong remote = request.m_steamIDRemote.m_SteamID;
            if (isAllowedPeer(remote))
            {
                Accept(remote);
            }
            else
            {
                if (!deferred.Contains(remote) && deferred.Count < MaxDeferredRequests)
                {
                    deferred.Add(remote);
                }

                log.Warning(LogCategory.Net, "Steam P2P session request from " + remote + " put on hold: not in our lobby (yet).");
            }
        }

        private void Accept(ulong remote)
        {
            Track(remote);
            bool accepted = SteamNetworking.AcceptP2PSessionWithUser(new CSteamID(remote));
            log.Info(LogCategory.Net, "Steam P2P session with " + remote + (accepted ? " accepted." : " could not be accepted."));
        }

        private void OnSessionFailed(P2PSessionConnectFail_t failure)
        {
            log.Warning(
                LogCategory.Net,
                "Steam P2P session with " + failure.m_steamIDRemote.m_SteamID + " failed: " + (EP2PSessionError)failure.m_eP2PSessionError + ".");
        }
    }
}
