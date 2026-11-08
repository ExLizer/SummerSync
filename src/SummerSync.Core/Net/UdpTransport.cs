using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace SummerSync.Core.Net
{
    /// <summary>
    /// Datagrams over a plain UDP socket, for running two game instances on one machine (or a LAN) without
    /// Steam. A peer id is the IPv4 address and port packed into a number.
    /// </summary>
    public sealed class UdpTransport : ITransport, IDisposable
    {
        private const int MaxFailedReadsPerCall = 16;

        private readonly Socket socket;
        private readonly Dictionary<ulong, IPEndPoint> endpoints = new Dictionary<ulong, IPEndPoint>();
        private EndPoint sender = new IPEndPoint(IPAddress.Any, 0);

        /// <param name="bindAddress">Loopback keeps it local and avoids a firewall prompt; Any accepts LAN peers.</param>
        /// <param name="port">Port to listen on, or 0 to let the system pick one (clients).</param>
        public UdpTransport(IPAddress bindAddress, int port)
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                socket.Blocking = false;
                socket.Bind(new IPEndPoint(bindAddress, port));
                LocalPort = ((IPEndPoint)socket.LocalEndPoint).Port;
            }
            catch
            {
                socket.Close();
                throw;
            }
        }

        public int LocalPort { get; }

        public int MaxDatagramSize
        {
            get { return 1200; }
        }

        /// <summary>Send failures since creation. UDP reports the previous send's fate on a later call, if at all.</summary>
        public long SendErrors { get; private set; }

        public static ulong PeerId(IPAddress address, int port)
        {
            byte[] bytes = address.GetAddressBytes();
            if (bytes.Length != 4)
            {
                throw new ArgumentException("Only IPv4 addresses are supported.", "address");
            }

            ulong ip = ((ulong)bytes[0] << 24) | ((ulong)bytes[1] << 16) | ((ulong)bytes[2] << 8) | bytes[3];
            return (ip << 16) | (ushort)port;
        }

        public static IPEndPoint EndPointOf(ulong peer)
        {
            uint ip = (uint)(peer >> 16);
            var bytes = new[] { (byte)(ip >> 24), (byte)(ip >> 16), (byte)(ip >> 8), (byte)ip };
            return new IPEndPoint(new IPAddress(bytes), (int)(peer & 0xFFFF));
        }

        public void Send(ulong peer, byte[] data, int count)
        {
            IPEndPoint endpoint;
            if (!endpoints.TryGetValue(peer, out endpoint))
            {
                endpoint = EndPointOf(peer);
                endpoints[peer] = endpoint;
            }

            try
            {
                socket.SendTo(data, 0, count, SocketFlags.None, endpoint);
            }
            catch (SocketException)
            {
                // A full buffer or an unreachable peer: the same as a lost datagram, which the protocol handles.
                SendErrors++;
            }
        }

        public bool Receive(byte[] buffer, out int count, out ulong peer)
        {
            // On Windows, a datagram sent earlier to a closed port comes back as a "connection reset" error on a
            // later read. That consumes one read without yielding data, so keep reading.
            for (int attempt = 0; attempt < MaxFailedReadsPerCall; attempt++)
            {
                try
                {
                    // Asking how much is waiting can report that same error, so it is inside the guard too.
                    if (socket.Available <= 0)
                    {
                        break;
                    }

                    count = socket.ReceiveFrom(buffer, 0, buffer.Length, SocketFlags.None, ref sender);
                    var from = (IPEndPoint)sender;
                    peer = PeerId(from.Address, from.Port);
                    return true;
                }
                catch (SocketException)
                {
                    // See above.
                }
            }

            count = 0;
            peer = 0;
            return false;
        }

        public void Dispose()
        {
            socket.Close();
        }
    }
}
