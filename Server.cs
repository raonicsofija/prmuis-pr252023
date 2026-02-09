using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ServerApp
{
    public class Termin
    {
        public string Priroda;
        public string Podnosilac;
        public string DatumPodnosenja;  // dd-MM-yyyy-HH-mm-ss
        public string TelefonSif;
        public string EmailSif;
        public string Opis;
        public string Pocetak;          // dd-MM-yyyy-HH-mm-ss
        public string Kraj;             // dd-MM-yyyy-HH-mm-ss
    }

    class Operater
    {
        public string Uloga; // Kvar / Intervencija
        public Socket Soket;
        public string Bafer = ""; // skupljamo tekst dok ne dobijemo '\n'
    }

    class Program
    {
        const int TCP_PORT = 4000;
        const int UDP_PORT = 9000;
        const string FORMAT = "dd-MM-yyyy-HH-mm-ss";

        static Dictionary<string, (TimeSpan Poc, TimeSpan Kraj)> radnoVreme = new Dictionary<string, (TimeSpan, TimeSpan)>();
        static Dictionary<string, List<Termin>> kalendar = new Dictionary<string, List<Termin>>(); // kljuc dd-MM-yyyy

        static List<Operater> operateri = new List<Operater>();

        static void Main()
        {
            Console.OutputEncoding = Encoding.UTF8;

            UcitajRadnoVreme();
            InicijalizujKalendar();

            // UDP prijava klijenata
            Socket udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            udp.Bind(new IPEndPoint(IPAddress.Any, UDP_PORT));
            Console.WriteLine($"SERVER: UDP prijava na portu {UDP_PORT}");

            // TCP listener za operatere
            Socket listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Any, TCP_PORT));
            listener.Listen(10);
            Console.WriteLine($"SERVER: TCP operateri na portu {TCP_PORT}");

            byte[] privremeni = new byte[4096];

            while (true)
            {
                // Socket.Select traži listu soketa koje pratimo
                List<Socket> citaj = new List<Socket>();
                citaj.Add(udp);
                citaj.Add(listener);
                foreach (var op in operateri) citaj.Add(op.Soket);

                Socket.Select(citaj, null, null, 1_000_000);

                foreach (var s in citaj)
                {
                    if (s == udp)
                    {
                        ObradiUdp(udp);
                        continue;
                    }

                    if (s == listener)
                    {
                        PrihvatiOperatera(listener);
                        continue;
                    }

                    // poruka od operatera
                    Operater op2 = operateri.First(o => o.Soket == s);

                    int r;
                    try { r = s.Receive(privremeni); }
                    catch { r = 0; }

                    if (r <= 0)
                    {
                        Console.WriteLine($"SERVER: operater {op2.Uloga} se diskonektovao.");
                        try { s.Close(); } catch { }
                        operateri.Remove(op2);
                        continue;
                    }

                    op2.Bafer += Encoding.UTF8.GetString(privremeni, 0, r);

                    // uzimamo sve kompletne linije (do '\n')
                    while (true)
                    {
                        int idx = op2.Bafer.IndexOf('\n');
                        if (idx < 0) break;

                        string linija = op2.Bafer.Substring(0, idx).Trim('\r');
                        op2.Bafer = op2.Bafer.Substring(idx + 1);

                        ObradiPorukuOperatera(op2, linija);
                    }
                }
            }
        }

        static void ObradiUdp(Socket udp)
        {
            byte[] buf = new byte[1024];
            EndPoint ep = new IPEndPoint(IPAddress.Any, 0);
            int r = udp.ReceiveFrom(buf, ref ep);

            // format: korisnik|Kvar ili korisnik|Intervencija
            string poruka = Encoding.UTF8.GetString(buf, 0, r);
            string[] p = poruka.Split('|');
            if (p.Length < 2) return;

            string korisnik = p[0];
            string tip = p[1];

            int port = (tip == "Kvar") ? 5001 : 5002;
            udp.SendTo(Encoding.UTF8.GetBytes(port.ToString()), ep);

            Console.WriteLine($"SERVER: prijava {korisnik} ({tip}) -> port {port}");
        }

        static void PrihvatiOperatera(Socket listener)
        {
            Socket s = listener.Accept();
            s.NoDelay = true;

            string uloga = (operateri.Count == 0) ? "Kvar" : "Intervencija";
            operateri.Add(new Operater { Uloga = uloga, Soket = s });

            PosaljiLiniju(s, "ULOGA|" + uloga);
            Console.WriteLine($"SERVER: povezan operater, uloga = {uloga}");
        }

        static void ObradiPorukuOperatera(Operater op, string linija)
        {
            // Operater šalje:
            // HELLO|5001
            // SLOBODAN|minuti
            // PROVERI|startStamp|minuti
            // UPISI|podnosilac|priroda|datumPodnosenja|telSif|emailSif|opis|pocetak|kraj

            if (linija.StartsWith("HELLO|"))
            {
                PosaljiLiniju(op.Soket, "OK");
                return;
            }

            if (linija.StartsWith("SLOBODAN|"))
            {
                int minuti = int.Parse(linija.Split('|')[1]);
                var slot = NadjiPrviSlobodanTermin(minuti);

                if (slot == null) PosaljiLiniju(op.Soket, "NE");
                else PosaljiLiniju(op.Soket, $"OK|{slot.Value.pocetak}|{slot.Value.kraj}");
                return;
            }

            if (linija.StartsWith("PROVERI|"))
            {
                string[] p = linija.Split('|');
                string startStamp = p[1];
                int minuti = int.Parse(p[2]);

                string endStamp;
                bool ok = ProveriPredlog(startStamp, minuti, out endStamp);

                if (ok) PosaljiLiniju(op.Soket, $"OK|{startStamp}|{endStamp}");
                else PosaljiLiniju(op.Soket, "NE");
                return;
            }

            if (linija.StartsWith("UPISI|"))
            {
                string[] p = linija.Split('|');

                // očekujemo 9 delova: UPISI + 8 polja
                if (p.Length < 9)
                {
                    PosaljiLiniju(op.Soket, "NE");
                    return;
                }

                var t = new Termin
                {
                    Podnosilac = p[1],
                    Priroda = p[2],
                    DatumPodnosenja = p[3],
                    TelefonSif = p[4],
                    EmailSif = p[5],
                    Opis = p[6],
                    Pocetak = p[7],
                    Kraj = p[8]
                };

                bool upisano = UpisiAkoJeSlobodno(t);
                PosaljiLiniju(op.Soket, upisano ? "UPISANO" : "NE");

                if (upisano)
                {
                    Console.WriteLine("SERVER: termin upisan.");
                    IspisiKalendar();
                }
                return;
            }
        }

        // ===== KALENDAR / RADNO VREME =====

        static void UcitajRadnoVreme()
        {
            foreach (var linija in File.ReadAllLines("radnoVreme.txt"))
            {
                if (string.IsNullOrWhiteSpace(linija)) continue;

                string[] p = linija.Split('-');
                string dan = p[0].Trim();
                TimeSpan poc = TimeSpan.Parse(p[1].Trim());
                TimeSpan kraj = TimeSpan.Parse(p[2].Trim());

                radnoVreme[dan] = (poc, kraj);
            }
            Console.WriteLine("SERVER: učitano radno vreme.");
        }

        static void InicijalizujKalendar()
        {
            kalendar[DateTime.Today.ToString("dd-MM-yyyy")] = new List<Termin>();
            kalendar[DateTime.Today.AddDays(1).ToString("dd-MM-yyyy")] = new List<Termin>();
        }

        static (string pocetak, string kraj)? NadjiPrviSlobodanTermin(int minuti)
        {
            DateTime[] dani = new[] { DateTime.Today, DateTime.Today.AddDays(1) };

            foreach (var datum in dani)
            {
                string danSr = DanUSrpskom(datum);
                if (!radnoVreme.ContainsKey(danSr)) continue;

                var rv = radnoVreme[danSr];
                if (rv.Poc == rv.Kraj) continue; // neradno

                DateTime radPoc = datum.Date + rv.Poc;
                DateTime radKraj = datum.Date + rv.Kraj;

                string kljuc = datum.ToString("dd-MM-yyyy");
                var lista = kalendar[kljuc]
                    .Select(x => (P: Parsiraj(x.Pocetak), K: Parsiraj(x.Kraj)))
                    .OrderBy(x => x.P)
                    .ToList();

                DateTime kursor = radPoc;

                foreach (var z in lista)
                {
                    if (kursor.AddMinutes(minuti) <= z.P)
                        return (Stamp(kursor), Stamp(kursor.AddMinutes(minuti)));

                    if (kursor < z.K) kursor = z.K;
                }

                if (kursor.AddMinutes(minuti) <= radKraj)
                    return (Stamp(kursor), Stamp(kursor.AddMinutes(minuti)));
            }

            return null;
        }

        static bool ProveriPredlog(string startStamp, int minuti, out string endStamp)
        {
            endStamp = "";

            DateTime start;
            try { start = Parsiraj(startStamp); }
            catch { return false; }

            DateTime datum = start.Date;
            if (datum != DateTime.Today && datum != DateTime.Today.AddDays(1)) return false;

            string danSr = DanUSrpskom(datum);
            if (!radnoVreme.ContainsKey(danSr)) return false;

            var rv = radnoVreme[danSr];
            if (rv.Poc == rv.Kraj) return false;

            DateTime radPoc = datum + rv.Poc;
            DateTime radKraj = datum + rv.Kraj;

            DateTime end = start.AddMinutes(minuti);
            if (start < radPoc || end > radKraj) return false;

            string kljuc = datum.ToString("dd-MM-yyyy");

            foreach (var t in kalendar[kljuc])
            {
                DateTime p = Parsiraj(t.Pocetak);
                DateTime k = Parsiraj(t.Kraj);

                bool preklop = start < k && end > p;
                if (preklop) return false;
            }

            endStamp = Stamp(end);
            return true;
        }

        static bool UpisiAkoJeSlobodno(Termin t)
        {
            DateTime p = Parsiraj(t.Pocetak);
            DateTime k = Parsiraj(t.Kraj);

            DateTime datum = p.Date;
            if (datum != DateTime.Today && datum != DateTime.Today.AddDays(1)) return false;

            string kljuc = datum.ToString("dd-MM-yyyy");

            foreach (var postojeci in kalendar[kljuc])
            {
                DateTime pp = Parsiraj(postojeci.Pocetak);
                DateTime kk = Parsiraj(postojeci.Kraj);

                bool preklop = p < kk && k > pp;
                if (preklop) return false;
            }

            kalendar[kljuc].Add(t);
            return true;
        }

        static void IspisiKalendar()
        {
            Console.WriteLine("\n===== KALENDAR (danas i sutra) =====");

            foreach (var datum in new[] { DateTime.Today, DateTime.Today.AddDays(1) })
            {
                string kljuc = datum.ToString("dd-MM-yyyy");
                Console.WriteLine($"\n--- {kljuc} ({DanUSrpskom(datum)}) ---");

                if (kalendar[kljuc].Count == 0)
                {
                    Console.WriteLine("(nema termina)");
                    continue;
                }

                Console.WriteLine($"{"Pocetak",-20} {"Kraj",-20} {"Priroda",-12} {"Podnosilac",-12} {"Telefon",-16} {"Email",-22} Opis");

                foreach (var t in kalendar[kljuc].OrderBy(x => Parsiraj(x.Pocetak)))
                {
                    // kljuc = korisnickoIme + brojDanaUNedelji (1..7)
                    int brojDana = BrojDanaUNedelji(Parsiraj(t.DatumPodnosenja));
                    string kljucSifre = t.Podnosilac + brojDana;

                    string tel = Decrypt(t.TelefonSif, kljucSifre);
                    string em = Decrypt(t.EmailSif, kljucSifre);

                    Console.WriteLine($"{t.Pocetak,-20} {t.Kraj,-20} {t.Priroda,-12} {t.Podnosilac,-12} {tel,-16} {em,-22} {t.Opis}");
                }
            }

            Console.WriteLine("====================================\n");
        }

        // ===== MINIMALNE pomoćne (kratke) =====

        static void PosaljiLiniju(Socket s, string tekst)
        {
            byte[] data = Encoding.UTF8.GetBytes(tekst + "\n");
            s.Send(data);
        }

        static DateTime Parsiraj(string stamp)
            => DateTime.ParseExact(stamp, FORMAT, CultureInfo.InvariantCulture);

        static string Stamp(DateTime dt)
            => dt.ToString(FORMAT);

        static string DanUSrpskom(DateTime dt)
        {
            switch (dt.DayOfWeek)
            {
                case DayOfWeek.Monday: return "Ponedeljak";
                case DayOfWeek.Tuesday: return "Utorak";
                case DayOfWeek.Wednesday: return "Sreda";
                case DayOfWeek.Thursday: return "Cetvrtak";
                case DayOfWeek.Friday: return "Petak";
                case DayOfWeek.Saturday: return "Subota";
                case DayOfWeek.Sunday: return "Nedelja";
                default: return "Ponedeljak";
            }
        }

        static int BrojDanaUNedelji(DateTime dt)
        {
            int d = (int)dt.DayOfWeek; // Sunday=0
            return d == 0 ? 7 : d;
        }

        // ===== Keyword cipher (slova pa brojevi) =====
        const string Alfabet = "abcdefghijklmnopqrstuvwxyz0123456789";

        static string NapraviAlfabet(string kljuc)
        {
            kljuc = new string(kljuc.ToLower().Where(c => Alfabet.Contains(c)).ToArray());

            string rezultat = "";
            foreach (char c in kljuc)
                if (!rezultat.Contains(c)) rezultat += c;

            foreach (char c in Alfabet)
                if (!rezultat.Contains(c)) rezultat += c;

            return rezultat;
        }

        static string Encrypt(string tekst, string kljuc)
        {
            string keyAbc = NapraviAlfabet(kljuc);
            tekst = tekst.ToLower();

            char[] outp = new char[tekst.Length];
            for (int i = 0; i < tekst.Length; i++)
            {
                char ch = tekst[i];
                int idx = Alfabet.IndexOf(ch);
                outp[i] = (idx < 0) ? ch : keyAbc[idx];
            }
            return new string(outp);
        }

        static string Decrypt(string sifrat, string kljuc)
        {
            string keyAbc = NapraviAlfabet(kljuc);
            sifrat = sifrat.ToLower();

            char[] outp = new char[sifrat.Length];
            for (int i = 0; i < sifrat.Length; i++)
            {
                char ch = sifrat[i];
                int idx = keyAbc.IndexOf(ch);
                outp[i] = (idx < 0) ? ch : Alfabet[idx];
            }
            return new string(outp);
        }
    }
}
