//#define _TEST_BASIC_VAR
#define _TEST_PLCTAG

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using S7CommPlusDriver;

using S7CommPlusDriver.ClientApi;

namespace DriverTest
{
    class Program
    {
        static void Main(string[] args)
        {
            string HostIp = "192.168.0.250";
            string Password = "";
            int res;
            List<ItemAddress> readlist = new List<ItemAddress>();
            Console.WriteLine("Main - START");
            // Als Parameter lässt sich die IP-Adresse übergeben, sonst Default-Wert von oben
            if (args.Length >= 1) {
                HostIp = args[0];
            }
            // Als Parameter lässt sich das Passwort übergeben, sonst Default-Wert von oben (kein Passwort)
            if (args.Length >= 2) {
                Password = args[1];
            }
            Console.WriteLine("Main - Versuche Verbindungsaufbau zu: " + HostIp);

            S7CommPlusConnection conn = new S7CommPlusConnection();
            conn.OnlySecurePGOrPCAndHMI = false;
            
            System.Diagnostics.Stopwatch stopwatch1 = new System.Diagnostics.Stopwatch();
            stopwatch1.Start();
            res = conn.Connect(HostIp, Password);
            stopwatch1.Stop();
            byte[] var1_crc_bytes = { 0x88, 0xdd, 0xa4, 0x83, 0x34 };
            Console.WriteLine($"PLCType: {conn.PLCInformation.PLCType} | MLFB: {conn.PLCInformation.MLFB} | Firmware: {conn.PLCInformation.Firmware}");
            Console.WriteLine($"连接耗时{stopwatch1.ElapsedMilliseconds}ms.");
            if (res == 0)
            {
                Console.WriteLine("按任意键浏览变量");
                Console.ReadKey();
                Console.WriteLine("Main - Connect fertig");

                #region Variablenhaushalt browsen
                Console.WriteLine("Main - Starte Browse...");
                // Variablenhaushalt auslesen
                List<VarInfo> vars = new List<VarInfo>();
                res = conn.Browse(out vars);
                Console.WriteLine("Main - Browse res=" + res);
                #endregion
                List<VarInfo> vars_ = vars.GetRange(0, 1000);
#if _TEST_PLCTAG
                #region Werte aller Variablen einlesen
                Console.WriteLine("Main - Lese Werte aller Variablen aus");

                List<PlcTag> taglist = new List<PlcTag>();

                foreach (var v in vars_)
                {
                    ItemAddress itemAddress = new ItemAddress(v.AccessSequence);
                    //S7p.DecodeUInt32Vlq(new MemoryStream(var1_crc_bytes), out itemAddress.SymbolCrc);
                    taglist.Add(PlcTags.TagFactory(v.Name, itemAddress, v.Softdatatype));
                }
                if (res == 0)
                {
                    Console.WriteLine("====================== VARIABLENHAUSHALT ======================");

                    string formatstring = "{0,-80}{1,-30}{2,-20}{3,-20}";
                    Console.WriteLine(String.Format(formatstring, "SYMBOLIC-NAME", "ACCESS-SEQUENCE", "TYP", "QC: VALUE"));
                    for (int i = 0; i < vars_.Count; i++)
                    {
                        string s;
      
                        s = String.Format(formatstring, taglist[i].Name, taglist[i].Address.GetAccessString(), Softdatatype.Types[taglist[i].Datatype], taglist[i].ToString());
                        Console.WriteLine(s);
                    }
                }
                #endregion
                Console.WriteLine("按任意键开始读或订阅");
                Console.ReadKey();
                res = conn.SubscriptionCreate(taglist, 100);
                if (res == 0)
                {
                    Task.Run(() =>
                    {
                        conn.TestWaitForVariableChangeNotifications(50000000000);
                    });
                }
                while (res == 0)
                {
                    System.Diagnostics.Stopwatch stopwatch2 = new System.Diagnostics.Stopwatch();
                    stopwatch2.Start();
                    res = PlcTags.ReadTags(conn, taglist);
                    stopwatch2.Stop();
                    long ms = stopwatch2.ElapsedMilliseconds;
                    if (res == 0)
                    {
                        string header = $"读取{vars_.Count}个变量耗时{ms}毫秒";
                        Console.WriteLine(header);
                    }
                    //System.Threading.Thread.Sleep(50);
                }
#endif

#if _TEST_BASIC_VAR
                #region Werte aller Variablen einlesen
                Console.WriteLine("Main - Lese Werte aller Variablen aus");

                foreach (var v in vars)
                {
                    readlist.Add(new ItemAddress(v.AccessSequence));
                }
                List<object> values = new List<object>();
                List<UInt64> errors = new List<UInt64>();

                
                // Fehlerhafte Variable setzen
                //readlist[2].LID[0] = 123;
                res = conn.ReadValues(readlist, out values, out errors);
                #endregion


                #region Variablenhaushalt mit Werten ausgeben

                if (res == 0)
                {
                    Console.WriteLine("====================== VARIABLENHAUSHALT ======================");

                    // Liste ausgeben
                    string formatstring = "{0,-80}{1,-30}{2,-20}{3,-20}";
                    Console.WriteLine(String.Format(formatstring, "SYMBOLIC-NAME", "ACCESS-SEQUENCE", "TYP", "VALUE"));
                    for (int i = 0; i < vars.Count; i++)
                    { 
                        string s = String.Format(formatstring, vars[i].Name, vars[i].AccessSequence, Softdatatype.Types[vars[i].Softdatatype], values[i]);
                        Console.WriteLine(s);
                    }
                    Console.WriteLine("===============================================================");
                }
                #endregion
#endif

                /*
                #region Test: Wert schreiben
                List<PValue> writevalues = new List<PValue>();
                PValue writeValue = new ValueInt(8888);
                writevalues.Add(writeValue);
                List<ItemAddress> writelist = new List<ItemAddress>();
                writelist.Add(new ItemAddress("8A0E0001.F"));
                errors.Clear();
                res = conn.WriteValues(writelist, writevalues, out errors);
                Console.WriteLine("res=" + res);
                #endregion
                */

                /*
                #region Test: Absolutadressen lesen
                // Daten aus nicht "optimierten" Datenbausteinen lesen
                readlist.Clear();
                ItemAddress absAdr = new ItemAddress();
                absAdr.SetAccessAreaToDatablock(100); // DB 100
                absAdr.SymbolCrc = 0;

                absAdr.AccessSubArea = Ids.DB_ValueActual;
                absAdr.LID.Add(3);  // LID_OMS_STB_ClassicBlob
                absAdr.LID.Add(0);  // Blob Start Offset, Anfangsadresse
                absAdr.LID.Add(20); // 20 Bytes

                readlist.Add(absAdr);

                values.Clear();
                errors.Clear();

                res = conn.ReadValues(readlist, out values, out errors);
                Console.WriteLine(values.ToString());
                #endregion
                */


                #region Test: SPS in Stopp setzen
                Console.WriteLine("Setze SPS in STOP...");
                conn.SetPlcOperatingState(1);
                Console.WriteLine("Taste drücken um wieder in RUN zu setzen...");
                Console.ReadKey();
                Console.WriteLine("Setze SPS in RUN...");
                conn.SetPlcOperatingState(3);
                #endregion

                conn.Disconnect();
            }
            else
            {
                Console.WriteLine("Main - Connect fehlgeschlagen!");
            }
            Console.WriteLine("Main - ENDE. Bitte Taste drücken.");
            Console.ReadKey();
        }
    }
    public static class S7SymbolCrc32
    {
        private const uint Polynomial = 0xFA567993; // 多项式(x³² + x³¹ + x³⁰ + x²⁹ + x²⁸ + x²⁶ + x²³ + x²¹ + x¹⁹ + x¹⁸ + x¹⁵ + x¹⁴ + x¹³ + x¹² + x⁹ + x⁸ + x⁴ + x + 1)
        private const uint InitialValue = 0xFFFFFFFF;
        private static readonly uint[] Table;

        static S7SymbolCrc32()
        {
            // 预计算CRC表
            Table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint crc = i << 24;
                for (int j = 0; j < 8; j++)
                {
                    if ((crc & 0x80000000) != 0)
                    {
                        crc = (crc << 1) ^ Polynomial;
                    }
                    else
                    {
                        crc <<= 1;
                    }
                }
                Table[i] = crc;
            }
        }

        /// <summary>
        /// 计算S7CommPlus符号名的CRC32校验和
        /// </summary>
        /// <param name="symbolName">符号名（如"DB1.TempBottom"）</param>
        /// <param name="dataType">数据类型字节</param>
        /// <returns>CRC32校验和</returns>
        public static uint CalculateSymbolCrc(string symbolName)
        {
            // 1. 替换分隔符 '.' → 0x09
            string processedName = symbolName.Replace('.', '\t');

            // 2. 转换为字节数组
            byte[] nameBytes = Encoding.ASCII.GetBytes(processedName);

            // 3. 添加数据类型字节
            byte[] data = new byte[nameBytes.Length];
            Array.Copy(nameBytes, 0, data, 0, nameBytes.Length);

            // 4. 计算CRC32
            uint crc = InitialValue;
            foreach (byte b in data)
            {
                crc = (crc << 8) ^ Table[((crc >> 24) & 0xFF) ^ b];
            }

            // 5. 根据作者提示，可能需要再次计算（需要验证）
            //crc = CalculateSecondPass(crc);

            return crc;
        }

        // 如果需要二次计算的方法
        public static uint CalculateSecondPass(uint firstResult)
        {
            byte[] crcBytes = BitConverter.GetBytes(firstResult);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(crcBytes); // 确保大端序
            }

            uint crc = InitialValue;
            foreach (byte b in crcBytes)
            {
                crc = (crc << 8) ^ Table[((crc >> 24) & 0xFF) ^ b];
            }
            return crc;
        }
    }
}
