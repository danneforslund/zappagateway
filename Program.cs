using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace ZappaGateway {
    /*
     * Author: Daniel Forslund 
     * 
     * This application acts as a gateway for the Telia Zappa mobile app between your regular LAN and your 
     * Telia IPTV network. 
     * This comes in handy if you, like me, are running your Telia router in bridged mode where the LAN and 
     * IPTV networks are separated, and the Zappa app wouldn't normally work.
     * 
     * The application requires you to have two network interfaces connected to your computer, one to the LAN 
     * and one to the IPTV.
     * Start the application with two parameters: the first one is your LAN IP-address, the second is your 
     * IPTV IP-address.
     * 
     * How it works:
     * 1. The applications starts to listen for multicast messages on your LAN on port 5555.
     * 2. The Zappa mobile app sends a multicast message on your LAN to 239.16.16.195 on port 5555, from a 
     *    random local port.
     * 3. The application picks up the message and starts a TCP server on the IPTV network, listening for connections 
     *    to the same local port number.
     * 4. The application forwards the same multicast message on the IPTV network, from the same local port number.
     * 5. The IPTV box replies by connecting via TCP to the sender of the multicast on the local port number.
     * 6. The application forwards data between the Zappa app and IPTV box via TCP via the established connection.
     * 
     * Most of this info was taken from Anders Waldenborgs project zappa-alg (https://github.com/wanders/zappa-alg)
     */
    internal class Program
    {
        private const int BUFFER_SIZE = 4096;
        private const int DELAY_TIME_MS = 50;

        private static readonly List<Client> _clients = new List<Client>();
        private static IPAddress _lanAddress;
        private static IPAddress _iptvAddress;
        private static IPAddress _mcAddress;        
       
        private static void Main(string[] args) {
            if (args.Length < 2) {
                Console.WriteLine("Usage: ZappaGateway.exe <lan ip> <iptv ip>");
                return;                
            }

            //Set addresses for global use.
            _lanAddress =    IPAddress.Parse(args[0]);
            _iptvAddress =   IPAddress.Parse(args[1]);
            _mcAddress =     IPAddress.Parse("239.16.16.195");   
         
            var waitHandle = new EventWaitHandle(false, EventResetMode.AutoReset);
            bool exiting;
            
            StartMessagePump();
            
            do {
                exiting = waitHandle.WaitOne(TimeSpan.FromSeconds(5));
            } while (!exiting);

        }

        private static void HandleMulticast(Socket socket) {
            var buffer = new byte[BUFFER_SIZE];
            try {
                EndPoint remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);
                var receivedBytes = socket.ReceiveFrom(buffer, buffer.Length, SocketFlags.None, ref remoteEndPoint);

                var ipe = ((IPEndPoint)remoteEndPoint);

                var c = _clients.FirstOrDefault(f => f.Port == ipe.Port && f.State != ClientState.Free);
                if (c == null) {
                    c = new Client
                    {
                        Address = ipe.Address,
                        Port = ipe.Port,
                        State = ClientState.Listening,
                        Sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
                    };

                    c.Sender.Bind(new IPEndPoint(_iptvAddress, ipe.Port));

                    c.Listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    c.Listener.Bind(new IPEndPoint(_iptvAddress, ipe.Port));
                    c.Listener.Listen(1);

                    //Add client to message pump
                    _clients.Add(c);
                }

                c.Sender.SendTo(buffer, receivedBytes, SocketFlags.None, new IPEndPoint(_mcAddress, 5555));
#if DEBUG
                Console.WriteLine("Got multicast \"{0}\" from Zappa, forwarding on {1}.", Encoding.Default.GetString(buffer, 0, receivedBytes), _iptvAddress);
#endif
            } catch { }
        }

        private static void HandleListen(Socket socket, Client client) {
            if (socket != client.Listener) return;
            try {
                client.Box = client.Listener.Accept();                
                client.Phone = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                client.Phone.Connect(new IPEndPoint(client.Address, client.Port));
                client.State = ClientState.Accepted;
#if DEBUG
                Console.WriteLine("Accepted TCP connection from IPTV box on {0}:{1}", client.Address, client.Port);
#endif
            }
            catch {}
        }

        private static void HandleRedirect(Socket socket, Client client) {
            var buffer = new byte[BUFFER_SIZE];
            try {
                if (client == null) return;                
                var target = socket == client.Phone ? client.Box : client.Phone;
            
                var receivedBytes = socket.Receive(buffer, 0, buffer.Length, SocketFlags.None);
                if (receivedBytes == 0) Disconnect(client);                
                target.Send(buffer, receivedBytes, SocketFlags.None);
#if DEBUG                
                Console.WriteLine("Sent {0} to {1}", Encoding.Default.GetString(buffer, 0, receivedBytes), target == client.Phone ? "phone" : "box");
#endif
            }
            catch {}            
        }

        private static void Disconnect(Client client) {
            client.Phone.Close();
            client.Box.Close();
            client.State = ClientState.Listening;
#if DEBUG
            Console.WriteLine("Phone and box disconnected. Listening...");
#endif
        }

        private static Thread multicastThread;
        private static void StartMessagePump() {
            //Listen for multicasts to 239.16.16.195
            var ipe = new IPEndPoint(_lanAddress, 5555);
            Socket mc = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            mc.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(_mcAddress, _lanAddress));
            mc.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            mc.Bind(ipe);

            Console.WriteLine("Listening for multicasts on {0}:{1}", _lanAddress, "5555");

            //Start message pump
            for (;;) {
                //Loop through all clients
                for (int i = 0; i < _clients.Count; i++) {
                    var client = _clients[i];
                    switch (client.State) {
                        case ClientState.Accepted:
                            //Start listen to messages from phone
                            if (client.PhoneThread == null || client.PhoneThread.ThreadState != ThreadState.Running) {
                                client.PhoneThread = new Thread(() => HandleRedirect(client.Phone, client));
                                client.PhoneThread.Start();
                            }
                            //Start listen to messages from box
                            if (client.BoxThread == null || client.BoxThread.ThreadState != ThreadState.Running) {
                                client.BoxThread = new Thread(() => HandleRedirect(client.Box, client));
                                client.BoxThread.Start();
                            }
                            break;
                        case ClientState.Listening:
                            //Listen for incoming connection from box
                            if (client.ListenThread == null || client.ListenThread.ThreadState != ThreadState.Running) {
                                client.ListenThread = new Thread(() => HandleListen(client.Listener, client));
                                client.ListenThread.Start();
                            }
                            break;
                    }
                }
                //Listen for incoming multicasts
                if (multicastThread == null || !multicastThread.IsAlive) {
                    multicastThread = new Thread(() => HandleMulticast(mc));
                    multicastThread.Start();
                }

                //Sleep for a while to ease CPU load.                
                Thread.Sleep(DELAY_TIME_MS);
            }
        }    
    }
}
