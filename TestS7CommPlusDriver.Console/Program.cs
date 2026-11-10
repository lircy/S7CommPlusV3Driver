using Microsoft.Extensions.Logging;
using S7CommPlusDriver;
using S7CommPlusDriver.ClientApi;
using S7CommPlusDriver.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TestS7CommPlusDriver.Console
{
    /// <summary>
    /// S7CommPlusDriver 控制台测试 Demo（当前核心库 API 版）：
    ///   (无参)                                  打印用法
    ///   contract &lt;host&gt; [password] [projectPath]
    ///                                           契约冒烟：在线浏览+抽样读值，
    ///                                           projectPath 可选（追加离线树比对阶段）
    ///   diag &lt;host&gt; [password] [out] [timeoutMs]
    ///                                           连接、浏览并导出全部符号
    ///   readloop &lt;host&gt; [password] [count] [intervalMs]
    ///                                           循环批量读取变量并显示值
    ///   --offline &lt;projectPath&gt; [out] [选项]   TIA 离线导出 / CRC 对拍
    ///                                           （见 OfflineExport.cs）
    /// </summary>
    internal class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--offline")
            {
                return OfflineExport.Run(args);
            }

            if (args.Length == 0)
            {
                PrintUsage();
                return 0;
            }

            try
            {
                if (args[0] == "contract")
                {
                    string host = args.Length > 1 ? args[1] : "192.168.0.250";
                    string password = args.Length > 2 ? args[2] : "";
                    string projectPath = args.Length > 3 ? args[3] : null;
                    return RunContractSmoke(host, password, projectPath);
                }

                if (args[0] == "diag")
                {
                    return RunDiag(args);
                }

                if (args[0] == "readloop")
                {
                    string host = args.Length > 1 ? args[1] : "192.168.0.250";
                    string password = args.Length > 2 ? args[2] : "";
                    int count = args.Length > 3 ? int.Parse(args[3]) : 3;
                    int intervalMs = args.Length > 4 ? int.Parse(args[4]) : 500;
                    int timeoutMs = args.Length > 5 ? int.Parse(args[5]) : 60000;
                    return RunReadLoop(host, password, count, intervalMs, timeoutMs, null);
                }
            }
            catch (Exception ex)
            {
                System.Console.Error.WriteLine("命令执行失败: " + ex.Message);
                return 1;
            }

            PrintUsage();
            return 2;
        }

        private static void PrintUsage()
        {
            System.Console.WriteLine("S7CommPlusDriver 控制台测试 Demo");
            System.Console.WriteLine("用法:");
            System.Console.WriteLine("  (无参)                                  打印本用法");
            System.Console.WriteLine("  contract <host> [password] [projectPath]");
            System.Console.WriteLine("                                          契约冒烟（projectPath 可选: 追加离线树比对）");
            System.Console.WriteLine("  diag <host> [password] [out] [timeoutMs]");
            System.Console.WriteLine("                                          连接、浏览并导出全部符号");
            System.Console.WriteLine("  readloop <host> [password] [count] [intervalMs] [timeoutMs]");
            System.Console.WriteLine("                                          循环批量读取变量并显示值");
            System.Console.WriteLine("  --offline <projectPath> [out] [选项]    TIA 离线导出 / CRC 对拍");
        }

        private static S7CommPlusConnection CreateConnection(string host)
        {
            S7CommPlusConnection conn = new S7CommPlusConnection();
            conn.UseSecureCommunication = false;
            conn.Logger = new ConsoleDiagLogger(LogLevel.Debug);
            return conn;
        }

        /// <summary>契约冒烟：在线阶段（浏览全树+抽样读值）；projectPath 非空时追加离线树比对阶段。</summary>
        private static int RunContractSmoke(string host, string password, string projectPath)
        {
            System.Console.WriteLine(string.Format("==== 契约冒烟 (host={0}) ====", host));
            S7CommPlusConnection conn = CreateConnection(host);
            bool overall = false;
            try
            {
                int res = conn.Connect(host, password, "", 60000);
                if (res != 0)
                {
                    System.Console.WriteLine("Connect failed! Error code: " + res);
                    return 1;
                }

                List<VarInfo> onlineVars;
                overall = Shared.ContractSmoke.Run(conn, out onlineVars,
                    msg => System.Console.WriteLine(msg));
                if (!string.IsNullOrWhiteSpace(projectPath))
                {
                    overall = Shared.ContractSmoke.RunOfflinePhase(conn, onlineVars,
                        () => CreateConnection("(offline)"), projectPath,
                        msg => System.Console.WriteLine(msg)) && overall;
                }
                return overall ? 0 : 1;
            }
            catch (Exception ex)
            {
                System.Console.WriteLine("契约冒烟异常: " + ex.Message);
                return 1;
            }
            finally
            {
                conn.Disconnect();
            }
        }

        private static int RunDiag(string[] args)
        {
            string host = args.Length > 1 ? args[1] : "192.168.0.250";
            string password = args.Length > 2 ? args[2] : "";
            string outputPath = args.Length > 3 ? args[3] : null;
            int timeoutMs = args.Length > 4 ? int.Parse(args[4]) : 60000;
            return RunReadLoop(host, password, 0, 0, timeoutMs, outputPath);
        }

        /// <summary>连接 → 浏览 → 导出；count 大于 0 时导出后继续批量读循环。</summary>
        private static int RunReadLoop(string host, string password, int count, int intervalMs,
            int timeoutMs, string outputPath)
        {
            if (string.IsNullOrEmpty(outputPath))
            {
                outputPath = Path.Combine(
                    Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location),
                    "plc_reference_address.txt");
            }

            System.Console.WriteLine("Connecting to: " + host);
            S7CommPlusConnection conn = CreateConnection(host);
            try
            {
                System.Diagnostics.Stopwatch connectWatch = System.Diagnostics.Stopwatch.StartNew();
                int res = conn.Connect(host, password, "", timeoutMs);
                connectWatch.Stop();
                PrintPlcInformation(conn);
                System.Console.WriteLine("Connect time: " + connectWatch.ElapsedMilliseconds + "ms");
                if (res != 0)
                {
                    System.Console.WriteLine("Connect failed! Error code: " + res);
                    return 1;
                }

                System.Console.WriteLine("Browsing all variables...");
                System.Diagnostics.Stopwatch browseWatch = System.Diagnostics.Stopwatch.StartNew();
                List<VarInfo> vars = new List<VarInfo>();
                res = conn.Browse(out vars);
                browseWatch.Stop();
                System.Console.WriteLine(string.Format("Browse complete: res={0}, count={1}, time={2}ms",
                    res, vars == null ? 0 : vars.Count, browseWatch.ElapsedMilliseconds));

                if (res != 0 || vars == null || vars.Count == 0)
                {
                    System.Console.WriteLine("Browse failed or returned 0 symbols. res=" + res);
                    return 1;
                }

                ExportSymbols(host, conn, vars, outputPath);
                PrintPreview(vars);

                if (count > 0)
                {
                    RunReadLoop(conn, vars, count, intervalMs);
                }
                return 0;
            }
            finally
            {
                conn.Disconnect();
                System.Console.WriteLine("Done.");
            }
        }

        private static void PrintPlcInformation(S7CommPlusConnection conn)
        {
            PLCInformation info = conn.PLCInformation;
            System.Console.WriteLine(string.Format("PLCName: {0} | PLCType: {1} | MLFB: {2} | Firmware: {3}",
                info == null || info.Name == null ? "N/A" : info.Name,
                info == null || info.PLCType == null ? "N/A" : info.PLCType,
                info == null || info.MLFB == null ? "N/A" : info.MLFB,
                info == null || info.Firmware == null ? "N/A" : info.Firmware));
        }

        private static void ExportSymbols(string host, S7CommPlusConnection conn,
            List<VarInfo> vars, string outputPath)
        {
            System.Console.WriteLine("Writing " + vars.Count + " symbols to: " + outputPath);
            using (StreamWriter writer = new StreamWriter(outputPath, false, Encoding.UTF8))
            {
                writer.WriteLine("# PLC Reference Address export from S7CommPlusDriver");
                writer.WriteLine("# Host: " + host);
                writer.WriteLine("# PLC Name: " + (conn.PLCInformation == null || conn.PLCInformation.Name == null ? "N/A" : conn.PLCInformation.Name));
                writer.WriteLine("# MLFB: " + (conn.PLCInformation == null || conn.PLCInformation.MLFB == null ? "N/A" : conn.PLCInformation.MLFB));
                writer.WriteLine("# Total symbols: " + vars.Count);
                writer.WriteLine("# Format: SymbolName | AccessSequence | SoftdatatypeInt | SoftdatatypeName");
                writer.WriteLine("# AccessSequence = ReferenceAddress = 8A0E{DB#}.{LID1}.{LID2}...");
                writer.WriteLine("#");
                foreach (VarInfo v in vars)
                {
                    string typeName = Softdatatype.Types.ContainsKey(v.Softdatatype)
                        ? Softdatatype.Types[v.Softdatatype]
                        : "UNKNOWN_" + v.Softdatatype;
                    writer.WriteLine(v.Name + "\t" + v.AccessSequence + "\t" + v.Softdatatype + "\t" + typeName);
                }
            }
            System.Console.WriteLine("Export complete.");
        }

        private static void PrintPreview(List<VarInfo> vars)
        {
            System.Console.WriteLine();
            System.Console.WriteLine("--- First 30 symbols preview ---");
            string fmt = "{0,-80}{1,-30}{2,-20}";
            System.Console.WriteLine(string.Format(fmt, "SYMBOLIC-NAME", "ACCESS-SEQUENCE", "TYP"));
            System.Console.WriteLine(new string('-', 130));
            for (int i = 0; i < Math.Min(vars.Count, 30); i++)
            {
                VarInfo v = vars[i];
                string typeName = Softdatatype.Types.ContainsKey(v.Softdatatype)
                    ? Softdatatype.Types[v.Softdatatype]
                    : "UNKNOWN";
                string name = v.Name.Length > 78 ? "..." + v.Name.Substring(v.Name.Length - 75) : v.Name;
                System.Console.WriteLine(string.Format(fmt, name, v.AccessSequence, typeName));
            }
        }

        private static void RunReadLoop(S7CommPlusConnection conn, List<VarInfo> vars,
            int count, int intervalMs)
        {
            List<string> names = new List<string>();
            List<ItemAddress> addresses = new List<ItemAddress>();
            for (int i = 0; i < vars.Count && addresses.Count < count; i++)
            {
                try
                {
                    // 必须用 VarInfo 建地址：直接用 AccessSequence 会让符号 CRC 恒为 0。
                    addresses.Add(OfflineSymbolSource.CreatePlcTag(vars[i]).Address);
                    names.Add(vars[i].Name);
                }
                catch
                {
                    // 跳过无法构造地址的符号。
                }
            }

            if (addresses.Count == 0)
            {
                System.Console.WriteLine("No readable symbols found for read loop.");
                return;
            }

            System.Console.WriteLine();
            System.Console.WriteLine(string.Format(
                "Starting read loop: {0} vars/cycle, interval={1}ms, Ctrl+C to exit.",
                addresses.Count, intervalMs));
            long cycle = 0;
            while (true)
            {
                cycle++;
                System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
                List<object> values;
                List<ulong> errors;
                int res = conn.ReadValues(addresses, out values, out errors);
                watch.Stop();
                System.Console.WriteLine(string.Format("===== Cycle #{0}  res={1}  time={2}ms =====",
                    cycle, res, watch.ElapsedMilliseconds));
                for (int i = 0; i < names.Count; i++)
                {
                    string value = values[i] == null
                        ? "ERROR 0x" + errors[i].ToString("X")
                        : values[i].ToString();
                    System.Console.WriteLine(string.Format("  {0,-70} = {1}", names[i], value));
                }
                System.Threading.Thread.Sleep(Math.Max(0, intervalMs));
            }
        }

        /// <summary>Demo 宿主注入用 ILogger：只把驱动日志转发到控制台。</summary>
        private sealed class ConsoleDiagLogger : ILogger
        {
            private readonly LogLevel _minLevel;

            internal ConsoleDiagLogger(LogLevel minLevel)
            {
                _minLevel = minLevel;
            }

            public IDisposable BeginScope<TState>(TState state)
            {
                return null;
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                return logLevel >= _minLevel;
            }

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception exception, Func<TState, Exception, string> formatter)
            {
                if (!IsEnabled(logLevel))
                {
                    return;
                }

                string message = formatter == null ? state.ToString() : formatter(state, exception);
                if (exception != null)
                {
                    message += " | " + exception;
                }
                System.Console.WriteLine(string.Format("[驱动 {0}] {1}", logLevel, message));
            }
        }
    }
}
