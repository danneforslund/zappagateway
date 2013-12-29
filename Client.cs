using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace ZappaGateway
{
    internal class Client
    {
        public ClientState State { get; set; }
        public int Port { get; set; }
        public IPAddress Address { get; set; }

        public Socket Listener { get; set; }
        public Socket Sender { get; set; }

        public Socket Phone { get; set; }
        public Socket Box { get; set; }

        public Thread BoxThread, PhoneThread, ListenThread;
    }
}
