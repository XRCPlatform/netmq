using System;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AsyncIO;

namespace NetMQ.Core.Transports.Socks5
{
    internal class Socks5Connector : Tcp.TcpConnector
    {

        AutoResetEvent clientEvent = new AutoResetEvent(false);

        public Socks5Connector(IOThread ioThread, SessionBase session, Options options, Address addr, bool delayedStart)
            : base(ioThread, session, options, addr, delayedStart)
        {
        }

        protected override void ProcessPlug()
        {
            m_ioObject.SetHandler(this);
            if (m_delayedStart)
                AddReconnectTimer();
            else
            {
                StartConnecting();
            }

        }

        /// <summary>
        /// This is called when the timer expires - to start trying to connect.
        /// </summary>
        /// <param name="id">The timer-id. This is not used.</param>
        public new void TimerEvent(int id)
        {
            m_timerStarted = false;
            StartConnecting();
        }

        private void StartConnecting()
        {
            Debug.Assert(m_s == null);

            // Create the socket.
            try
            {
                m_s = AsyncSocket.Create(m_addr.Resolved.Address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            }
            catch (SocketException)
            {
                AddReconnectTimer();
                return;
            }

            CompletionPort completionPort = CompletionPort.Create();
            completionPort.AssociateSocket(m_s, clientEvent);

            var cTokenSource = new CancellationTokenSource();
            CancellationToken ct = cTokenSource.Token;

            var task = Task.Factory.StartNew(() =>
            {
                while(true)
                {
                    if (cTokenSource.IsCancellationRequested) break;

                    var result = completionPort.GetQueuedCompletionStatus(-1, out CompletionStatus completionStatus);

                    if (result)
                    {
                        if (completionStatus.State != null && completionStatus.State == clientEvent) clientEvent.Set();
                        if (completionStatus.OperationType == OperationType.Disconnect) break;
                    }
                }
            });

            // Connect to the remote peer.
            try
            {
                m_s.Connect(m_addr.Resolved.Address.Address, m_addr.Resolved.Address.Port);
                clientEvent.WaitOne();
                MakeSocks5Connection();

                m_socket.EventConnectDelayed(m_endpoint, ErrorCode.InProgress);

                // Cancel and signal it to shutdown the connection thread.
                cTokenSource.Cancel();
                completionPort.Signal(null);

                // Add socket to have socket listen to events
                m_ioObject.AddSocket(m_s);
                m_handleValid = true;

                // After connections even on tcpconnector connections, it needs to call
                // this to raise and event to say it is connected and pass over control
                // to SocketBase
                OutCompleted(SocketError.Success, 0);
            }
            catch (SocketException ex)
            {
                if (m_handleValid)
                {
                    OutCompleted(ex.SocketErrorCode, 0);
                }
                else
                {
                    Close();
                }
            }
            // TerminatingException can occur in above call to EventConnectDelayed via
            // MonitorEvent.Write if corresponding PairSocket has been sent Term command
            catch (TerminatingException)
            {}
        }

        private void MakeSocks5Connection()
        {
            // Auth
            var authBuffer = new byte[3] { 5, 1, Socks5Constants.AuthMethodNoAuthenticationRequired };
            m_s.Send(authBuffer);
            clientEvent.WaitOne();

            var response = new byte[2];
            m_s.Receive(response); //
            clientEvent.WaitOne();

            var destination = (Socks5Address)m_addr.Resolved;
            string destHost = destination.DestinationAddress.DomainOrIpAddress;
            int destPort = destination.DestinationAddress.Port;

            if (response[1] == Socks5Constants.AuthMethodReplyNoAcceptableMethods)
            {
                throw new SocketException();
            }

            if (response[1] != Socks5Constants.AuthMethodNoAuthenticationRequired)
            {
                throw new SocketException();
            }

            byte[] buffer;

            byte[] destAddr;
            byte addrType;

            if (!IPAddress.TryParse(destination.DestinationAddress.DomainOrIpAddress, out var ipAddr))
            {
                destAddr = new byte[destHost.Length + 1];
                destAddr[0] = Convert.ToByte(destHost.Length);
                Encoding.ASCII.GetBytes(destHost).CopyTo(destAddr, 1);
                addrType = Socks5Constants.AddrtypeDomainName;
                buffer = new byte[7 + destHost.Length];
            }
            else
            {
                destAddr = IPAddress.Parse(destHost).GetAddressBytes();
                addrType = Socks5Constants.AddrtypeIpv4;
                buffer = new byte[7 + 3];
            }

            var destPortBytes = new byte[2] { Convert.ToByte(destPort / 256), Convert.ToByte(destPort % 256) };
            buffer[0] = 5;
            buffer[1] = Socks5Constants.CmdConnect;
            buffer[2] = Socks5Constants.Reserved;
            buffer[3] = addrType;
            destAddr.CopyTo(buffer, 4);
            destPortBytes.CopyTo(buffer, 4 + destAddr.Length);

            m_s.Send(buffer);
            clientEvent.WaitOne();

            var destConnectResponse = new byte[255];
            m_s.Receive(destConnectResponse);
            clientEvent.WaitOne();

            if (destConnectResponse[1] != Socks5Constants.CmdReplySucceeded)
            {
                HandleProxyCommandError(destConnectResponse, destHost, destPort);
            }
        }

        private static void HandleProxyCommandError(byte[] response, string destinationHost, int destinationPort)
        {
            var replyCode = response[1];
            string proxyErrorText = "";
            switch(replyCode) {
                case Socks5Constants.CmdReplyGeneralSocksServerFailure:
                    proxyErrorText = "a general socks destination failure occurred";
                    break;
                case Socks5Constants.CmdReplyConnectionNotAllowedByRuleset:
                    proxyErrorText = "the connection is not allowed by proxy destination rule set";
                    break;
                case Socks5Constants.CmdReplyNetworkUnreachable:
                    proxyErrorText = "the network was unreachable";
                    break;
                case Socks5Constants.CmdReplyHostUnreachable:
                    proxyErrorText ="the host was unreachable";
                    break;
                case Socks5Constants.CmdReplyConnectionRefused:
                    proxyErrorText = "the connection was refused by the remote network";
                    break;
                case Socks5Constants.CmdReplyTtlExpired:
                    proxyErrorText = "the time to live (TTL) has expired";
                    break;
                case Socks5Constants.CmdReplyCommandNotSupported:
                    proxyErrorText = "the command issued by the proxy client is not supported by the proxy destination";
                    break;
                case Socks5Constants.CmdReplyAddressTypeNotSupported:
                    proxyErrorText = "the address type specified is not supported";
                    break;
                default:
                    proxyErrorText = string.Format(CultureInfo.InvariantCulture,
                                        "an unknown SOCKS reply with the code value '{0}' was received",
                                                   replyCode.ToString(CultureInfo.InvariantCulture));
                    break;
                 };
            string exceptionMsg = string.Format(CultureInfo.InvariantCulture,
                                                "proxy error: {0} for destination host {1} port number {2}.",
                                                proxyErrorText, destinationHost, destinationPort);

            Console.WriteLine(exceptionMsg);
            throw new SocketException();
        }
    }
}
