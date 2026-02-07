using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.IO;

namespace KorisnickaPodrska1
{
    public class Server
    {
        static Dictionary<string, (TimeSpan, TimeSpan)> radnoVreme = new Dictionary<string, (TimeSpan, TimeSpan)>();

        static void Main(string[] args)
        {
            UcitajRadnoVreme();

            // ===== TCP deo za operatere =====
            Socket operaterListener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            operaterListener.Bind(new IPEndPoint(IPAddress.Any, 4000));
            operaterListener.Listen(2);

            Console.WriteLine("Server: čekam operatere...");

            string[] uloge = { "Kvar", "Intervencija" };

            for (int i = 0; i < 2; i++)
            {
                Socket op = operaterListener.Accept();
                byte[] data = Encoding.UTF8.GetBytes(uloge[i]);
                op.Send(data);
                op.Close();
                Console.WriteLine($"Dodeljena uloga operateru: {uloge[i]}");
            }

            // ===== UDP deo za klijente =====
            Socket udpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

            udpSocket.Bind(new IPEndPoint(IPAddress.Any, 9000));
            Console.WriteLine("Server: UDP prijem na portu 9000");

            byte[] buffer = new byte[1024];
            EndPoint clientEP = new IPEndPoint(IPAddress.Any, 0);

            while (true)
            {
                int br = udpSocket.ReceiveFrom(buffer, ref clientEP);
                string poruka = Encoding.UTF8.GetString(buffer, 0, br);

                string[] delovi = poruka.Split('|');
                string korisnik = delovi[0];
                string tip = delovi[1];

                Console.WriteLine($"Prijava: {korisnik}, tip: {tip}");

                int port = tip == "Kvar" ? 5001 : 5002;
                byte[] odgovor = Encoding.UTF8.GetBytes(port.ToString());

                udpSocket.SendTo(odgovor, clientEP);
            }
        }

        static void UcitajRadnoVreme()
        {
            foreach (var line in File.ReadAllLines("radnoVreme.txt"))
            {
                var p = line.Split('-');
                radnoVreme[p[0]] =
                    (TimeSpan.Parse(p[1]), TimeSpan.Parse(p[2]));
            }
        }
    }
}
