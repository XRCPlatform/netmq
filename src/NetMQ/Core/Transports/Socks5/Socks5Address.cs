using System;
using System.Net;

namespace NetMQ.Core.Transports.Socks5
{
    internal sealed class Socks5Address : Address.IZAddress
    {
        public IPEndPoint Address { get; private set; }

        public DestinationAddress DestinationAddress { get; private set; }

        public string Protocol => Core.Address.Socks5Protocol;

        public void Resolve(string name, bool ip4Only)
        {
            int addressesDelimiter = name.LastIndexOf(';');
            if (addressesDelimiter < 0)
                throw new InvalidException($"Socks5Address.Resolve, address delimiter {{addressDelimiter}} must be non-negative");

            var proxy = GetAddressParsed(name.Substring(0, addressesDelimiter));
            IPAddress ipAddress;
            var parseResult = IPAddress.TryParse(proxy[0], out ipAddress);
            if (!parseResult)
                throw new InvalidException($"Socks5Address.Resolve, unable to find an IP address for {name}");

            this.Address = new IPEndPoint(ipAddress, Convert.ToInt32(proxy[1]));

            var dest = GetAddressParsed(name.Substring(addressesDelimiter + 1));
            this.DestinationAddress = new DestinationAddress
            {
                DomainOrIpAddress = dest[0],
                Port = Convert.ToInt32(dest[1])
            };
        }

        private string[] GetAddressParsed(string address)
        {
            int delimiter = address.LastIndexOf(':');
            if (delimiter < 0)
                throw new InvalidException($"Socks5Address.Resolve, delimiter {{delimiter}} must be non-negative.");
            string addrStr = address.Substring(0, delimiter);
            string portStr = address.Substring(delimiter + 1);

            int port = Convert.ToInt32(portStr);
            if (port == 0)
                throw new InvalidException($"Socks5Address.Resolve, unable to find an IP address for {address}");

            return new string[2] { addrStr, portStr };
        }

        public override string ToString()
        {
            if (this.Address == null)
                return string.Empty;

            var proxy = this.Address;
            var destination = this.DestinationAddress;

            return Protocol + "://" + proxy.Address + ":" + proxy.Port + ";" + destination.DomainOrIpAddress + ":" + destination.Port;
        }
    }

    /// <summary>
    ///   The destination address for a socks5 connection
    /// </summary>
    public class DestinationAddress
    {
        /// <summary>
        ///   Domain or IP address
        /// </summary>
        public string DomainOrIpAddress { get; set; }

        /// <summary>
        /// destination port
        /// </summary>
        public int Port { get; set; }
    }
}
