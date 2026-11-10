// --offline 模式：TiaProjectParser 离线导出 + 在线 CRC 对拍 + 只读验证
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using S7CommPlusDriver;
using S7CommPlusDriver.ClientApi;

namespace TestS7CommPlusDriver.Console
{
    static class OfflineExport
    {
        // TestS7CommPlusDriver.System.Console.exe --offline <projectPath> [outputPath] [--plc <name>] [--gt] [--crc] [--conn] [--verify <host> [password]] [--read <N>]
        public static int Run(string[] args)
        {
            string projectPath = null, outputPath = null, plcName = null, verifyHost = null, verifyPassword = "", symProbe = null;
            bool gt = false, crc = false, connCheck = false;
            int readN = 0;
            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--plc")
                {
                    if (i + 1 >= args.Length) return UsageError("--plc needs value");
                    plcName = args[++i];
                }
                else if (a == "--sym")
                {
                    if (i + 1 >= args.Length) return UsageError("--sym needs value");
                    symProbe = args[++i];
                }
                else if (a == "--gt")
                {
                    gt = true;
                }
                else if (a == "--crc")
                {
                    crc = true;
                }
                else if (a == "--conn")
                {
                    connCheck = true;
                }
                else if (a == "--verify")
                {
                    if (i + 1 >= args.Length) return UsageError("--verify needs host");
                    verifyHost = args[++i];
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) verifyPassword = args[++i];
                }
                else if (a == "--read")
                {
                    if (i + 1 >= args.Length) return UsageError("--read needs N");
                    if (!int.TryParse(args[++i], out readN)) return UsageError("--read N not a number");
                }
                else if (a.StartsWith("--"))
                {
                    return UsageError("unknown option: " + a);
                }
                else if (projectPath == null)
                {
                    projectPath = a;
                }
                else if (outputPath == null)
                {
                    outputPath = a;
                }
                else
                {
                    return UsageError("too many args: " + a);
                }
            }
            if (projectPath == null) return UsageError("missing projectPath");

            // ---- 统一 API：先经 S7CommPlusConnection.LoadSymbolsFromFile 加载（headless 验证路由），
            //      未给 --plc 时用连接取到的 PLC 名对齐后续导出/对拍的过滤 ----
            S7CommPlusConnection checkConn = null;
            bool connCheckLoadOk = false;
            if (connCheck)
            {
                System.Console.WriteLine("Unified API check: LoadSymbolsFromFile...");
                checkConn = new S7CommPlusConnection();
                checkConn.UseSecureCommunication = false;
                int res = checkConn.LoadSymbolsFromFile(projectPath);
                if (res != 0)
                {
                    System.Console.WriteLine("LOADSYMBOLSFROMFILE FAILED: " + res);
                    checkConn = null;
                }
                else
                {
                    connCheckLoadOk = true;
                    if (plcName == null) plcName = checkConn.PLCInformation.Name; // 对齐 PLC 过滤
                    System.Console.WriteLine("LOADSYMBOLSFROMFILE OK, PLC name: " + checkConn.PLCInformation.Name);
                }
            }

            if (string.IsNullOrEmpty(outputPath))
            {
                outputPath = Path.Combine(
                    Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location),
                    "offline_export.txt");
            }

            System.Console.WriteLine("Offline export: " + projectPath);
            OfflineSymbolSource src = new OfflineSymbolSource(projectPath, plcName);
            if (gt) src.UseStreamZeroObjects = true;
            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                src.Open();
            }
            catch (Exception ex)
            {
                System.Console.WriteLine("OPEN FAILED: " + ex.Message);
                return 1;
            }
            System.Console.WriteLine("Project: " + src.ProjectName + " V" + src.ProjectVersion);
            try
            {
                src.Parse();
            }
            catch (Exception ex)
            {
                System.Console.WriteLine("PARSE FAILED: " + ex.Message);
                src.Dispose();
                return 1;
            }
            System.Console.WriteLine("Parsed: PLCs=" + src.Plcs.Length + " DBs=" + src.Datablocks.Length + " time=" + sw.ElapsedMilliseconds + "ms");
            List<VarInfo> leaves = src.BrowseLeaves();
            System.Console.WriteLine("Leaves: " + leaves.Count + " time=" + sw.ElapsedMilliseconds + "ms");

            // ---- 导出（3 列 GT 同款：Name / Access / TypeName）----
            using (StreamWriter writer = new StreamWriter(outputPath, false, new UTF8Encoding(false)))
            {
                writer.WriteLine("# Offline symbol export from TiaProjectParser");
                writer.WriteLine("# Project: " + projectPath);
                writer.WriteLine("# PLC: " + (plcName ?? "ALL"));
                writer.WriteLine("# Count: " + leaves.Count);
                foreach (VarInfo v in leaves)
                {
                    string tn = Softdatatype.Types.ContainsKey(v.Softdatatype)
                        ? Softdatatype.Types[v.Softdatatype]
                        : "UNKNOWN_" + v.Softdatatype;
                    writer.WriteLine(v.Name + "\t" + v.AccessSequence + "\t" + tn);
                }
            }
            System.Console.WriteLine("Wrote: " + outputPath);

            // ---- CRC 副产 ----
            if (crc)
            {
                string crcPath = Path.ChangeExtension(outputPath, null) + ".crc.txt";
                using (StreamWriter writer = new StreamWriter(crcPath, false, new UTF8Encoding(false)))
                {
                    writer.WriteLine("# Count: " + leaves.Count);
                    foreach (VarInfo v in leaves)
                    {
                        writer.WriteLine(v.Name + "\t" + v.AccessSequence + "\t0x" + v.SymbolCrc.ToString("X8") + "\t" + OfflineSymbolSource.FormatSymbolCrcPath(v));
                    }
                }
                System.Console.WriteLine("Wrote: " + crcPath);
            }

            S7CommPlusConnection conn = null;
            int gate = -1; // -1 = 未执行对拍

            // ---- 统一 API 检查报告（LoadSymbolsFromFile 后 Browse/块表/逐层 relid 与直用 OfflineSymbolSource 等价）----
            if (connCheck)
            {
                string connCheckPath = Path.ChangeExtension(outputPath, null) + ".conn_check.txt";
                List<string> lines = new List<string>();
                bool allOk = connCheckLoadOk;
                lines.Add("# Unified API check (S7CommPlusConnection.LoadSymbolsFromFile vs OfflineSymbolSource)");
                lines.Add("# Project: " + projectPath);
                lines.Add("# PLC: " + (plcName ?? "ALL"));
                if (!connCheckLoadOk)
                {
                    lines.Add("# LOAD: FAILED");
                    allOk = false;
                }
                else
                {
                    lines.Add("# LOAD: OK res=0");
                    lines.Add("# conn.PLCInformation.Name: " + checkConn.PLCInformation.Name);
                    List<VarInfo> connLeaves = new List<VarInfo>();
                    int res = checkConn.Browse(out connLeaves);
                    // --gt 时源侧为流 0（已下载）语义而连接侧为最新流语义，叶数不强制一致（res 仍必须 0）
                    bool okBrowse = res == 0 && (gt || connLeaves.Count == leaves.Count);
                    lines.Add(string.Format("# BROWSE: res={0} conn={1} src={2} -> {3}", res, res == 0 ? connLeaves.Count.ToString() : "-", leaves.Count, okBrowse ? "OK" : "FAIL"));
                    if (gt && res == 0 && connLeaves.Count != leaves.Count)
                    {
                        lines.Add("# BROWSE note: --gt 流 0 语义 vs 连接侧最新流语义，叶数差 " + (leaves.Count - connLeaves.Count));
                    }
                    allOk &= okBrowse;
                    List<S7CommPlusConnection.DatablockInfo> dbInfoList;
                    int resDb = checkConn.GetListOfDatablocks(out dbInfoList);
                    bool okDb = resDb == 0 && dbInfoList.Count == src.Datablocks.Length;
                    lines.Add(string.Format("# DATABLOCKS: res={0} conn={1} src={2} -> {3}", resDb, resDb == 0 ? dbInfoList.Count.ToString() : "-", src.Datablocks.Length, okDb ? "OK" : "FAIL"));
                    allOk &= okDb;
                    if (okDb && dbInfoList.Count > 0)
                    {
                        PObject pObj = checkConn.getTypeInfoByRelId((uint)dbInfoList[0].db_block_ti_relid);
                        int members = pObj != null && pObj.VarnameList != null ? pObj.VarnameList.Names.Count : -1;
                        bool okBlock = members > 0;
                        lines.Add(string.Format("# BLOCK TYPEINFO: {0} members={1} -> {2}", dbInfoList[0].db_name, members, okBlock ? "OK" : "FAIL"));
                        allOk &= okBlock;
                    }
                    PObject pArea = checkConn.getTypeInfoByRelId(0x90010000); // Inputs
                    int areaMembers = pArea != null && pArea.VarnameList != null ? pArea.VarnameList.Names.Count : -1;
                    // 空输入区（工程无输入标签）合法：调用成功即 OK（与逐 PLC 检查的 >=0 同宽）
                    bool okArea = areaMembers >= 0;
                    lines.Add(string.Format("# AREA TYPEINFO: 0x90010000 members={0} -> {1}", areaMembers, okArea ? "OK" : "FAIL"));
                    allOk &= okArea;
                    // 多 PLC：逐 PLC 检查（每 PLC 块表 + 区 relid 变体 + SelectPlc 切换后活动块表一致）
                    List<string> plcNames = checkConn.GetListOfPlcs();
                    for (int i = 0; i < plcNames.Count; i++)
                    {
                        List<S7CommPlusConnection.DatablockInfo> plDb;
                        int resP = checkConn.GetListOfDatablocks(i, out plDb);
                        int membersP = -1;
                        PObject pAreaP = checkConn.getTypeInfoByRelId(0x90010000U + (uint)i * 0x100U);
                        if (pAreaP != null && pAreaP.VarnameList != null) membersP = pAreaP.VarnameList.Names.Count;
                        // SelectPlc 切换后活动块表与按索引取表一致（活动 PLC 切换正确性）
                        int resSel = checkConn.SelectPlc(plcNames[i]);
                        List<S7CommPlusConnection.DatablockInfo> actDb;
                        int resAct = checkConn.GetListOfDatablocks(out actDb);
                        bool okSel = resSel == 0 && resAct == 0 && actDb != null && actDb.Count == (plDb != null ? plDb.Count : -1);
                        bool okP = resP == 0 && plDb != null && membersP >= 0 && okSel;
                        lines.Add(string.Format("# PLC[{0}] {1}: blocks={2} inputs={3} selectSwitch={4} -> {5}",
                            i, plcNames[i], resP == 0 && plDb != null ? plDb.Count.ToString() : "-", membersP,
                            okSel ? "OK" : "FAIL", okP ? "OK" : "FAIL"));
                        allOk &= okP;
                    }
                    // 符号解析探针（getPlcTagBySymbol 全链路：树走查+叶缓存/树合成+TagFactory）。
                    // 探针前切回 PLC0（逐 PLC 循环末位活动 PLC 不确定）。
                    if (!string.IsNullOrEmpty(symProbe))
                    {
                        try
                        {
                            if (plcNames.Count > 0) checkConn.SelectPlc(plcNames[0]);
                            PlcTag t = checkConn.getPlcTagBySymbol(symProbe);
                            bool okSym = t != null && t.Address != null && !string.IsNullOrEmpty(t.Address.GetAccessString());
                            string typeName = t != null && Softdatatype.Types.ContainsKey(t.Datatype) ? Softdatatype.Types[t.Datatype] : "UNKNOWN";
                            lines.Add(string.Format("# SYM: {0} -> addr={1} type={2} -> {3}", symProbe, t != null ? t.Address.GetAccessString() : "-", typeName, okSym ? "OK" : "FAIL"));
                            allOk &= okSym;
                            // 大小写扰动回归（博途符号名大小写不敏感）：翻转全部字母大小写后解析，
                            // 地址与 CRC 须与规范符号完全一致（CRC 按 PLC 侧规范名计算）
                            string perturbed = PerturbCase(symProbe);
                            if (perturbed != symProbe)
                            {
                                PlcTag tp = checkConn.getPlcTagBySymbol(perturbed);
                                bool okCase = tp != null && t != null
                                    && string.Equals(tp.Address.GetAccessString(), t.Address.GetAccessString(), StringComparison.Ordinal)
                                    && tp.Address.SymbolCrc == t.Address.SymbolCrc;
                                lines.Add(string.Format("# SYM-CASE: {0} -> addr={1} crc=0x{2:X8} -> {3}", perturbed,
                                    tp != null ? tp.Address.GetAccessString() : "-",
                                    tp != null ? tp.Address.SymbolCrc : 0, okCase ? "OK" : "FAIL"));
                                allOk &= okCase;
                            }
                            else
                            {
                                lines.Add("# SYM-CASE: (no letters in symbol, skipped)");
                            }
                        }
                        catch (Exception ex)
                        {
                            lines.Add("# SYM: " + symProbe + " -> EX " + ex.Message + " -> FAIL");
                            allOk = false;
                        }
                    }
                }
                lines.Add(allOk ? "# CHECK: ALL OK" : "# CHECK: FAILED");
                File.WriteAllLines(connCheckPath, lines.ToArray(), new UTF8Encoding(false));
                System.Console.WriteLine("Wrote: " + connCheckPath);
                System.Console.WriteLine(allOk ? "CONN CHECK: ALL OK" : "CONN CHECK: FAILED");
                if (checkConn != null)
                {
                    try { checkConn.Disconnect(); } catch { }
                }
                if (!allOk) gate = 0;
            }

            try
            {
                if (verifyHost != null)
                {
                    System.Console.WriteLine("Connecting to: " + verifyHost);
                    conn = new S7CommPlusConnection();
                    conn.UseSecureCommunication = false;
                    int res = conn.Connect(verifyHost, verifyPassword);
                    if (res != 0)
                    {
                        System.Console.WriteLine("CONNECT FAILED: " + res);
                        return 1;
                    }
                    List<VarInfo> onlineVars = new List<VarInfo>();
                    res = conn.Browse(out onlineVars);
                    if (res != 0)
                    {
                        System.Console.WriteLine("BROWSE FAILED: " + res);
                        return 1;
                    }
                    System.Console.WriteLine("Online leaves: " + onlineVars.Count);

                    Dictionary<string, VarInfo> onlineByAccess = new Dictionary<string, VarInfo>(StringComparer.Ordinal);
                    foreach (VarInfo v in onlineVars)
                    {
                        onlineByAccess[v.AccessSequence] = v;
                    }
                    Dictionary<string, VarInfo> offlineByAccess = new Dictionary<string, VarInfo>(StringComparer.Ordinal);
                    foreach (VarInfo v in leaves)
                    {
                        offlineByAccess[v.AccessSequence] = v;
                    }

                    int onlineOnly = 0, offlineOnly = 0, segMismatch = 0, crcMismatch = 0, ok = 0, matched = 0;
                    List<string> mismatchLines = new List<string>();
                    List<VarInfo> matchedVars = new List<VarInfo>();
                    foreach (KeyValuePair<string, VarInfo> kv in onlineByAccess)
                    {
                        VarInfo off;
                        if (!offlineByAccess.TryGetValue(kv.Key, out off))
                        {
                            onlineOnly++;
                            mismatchLines.Add("ONLINE_ONLY\t" + kv.Value.Name + "\t" + kv.Key + "\t0x" + kv.Value.SymbolCrc.ToString("X8") + "\t" + OfflineSymbolSource.FormatSymbolCrcPath(kv.Value));
                        }
                        else
                        {
                            matched++;
                            bool segEq = OfflineSymbolSource.FormatSymbolCrcPath(kv.Value) == OfflineSymbolSource.FormatSymbolCrcPath(off);
                            bool crcEq = kv.Value.SymbolCrc == off.SymbolCrc;
                            if (!segEq) segMismatch++;
                            if (!crcEq) crcMismatch++;
                            if (segEq && crcEq)
                            {
                                ok++;
                                matchedVars.Add(off);
                            }
                            else
                            {
                                mismatchLines.Add("MISMATCH\t" + kv.Value.Name + "\t" + kv.Key);
                                mismatchLines.Add("  ONLINE : 0x" + kv.Value.SymbolCrc.ToString("X8") + "\t" + OfflineSymbolSource.FormatSymbolCrcPath(kv.Value));
                                mismatchLines.Add("  OFFLINE: 0x" + off.SymbolCrc.ToString("X8") + "\t" + OfflineSymbolSource.FormatSymbolCrcPath(off));
                            }
                        }
                    }
                    foreach (KeyValuePair<string, VarInfo> kv in offlineByAccess)
                    {
                        if (!onlineByAccess.ContainsKey(kv.Key)) offlineOnly++;
                    }

                    gate = (onlineOnly == 0 && segMismatch == 0 && crcMismatch == 0) ? 1 : 0;
                    System.Console.WriteLine(string.Format("SUMMARY online={0} offline={1} matched={2} onlineOnly={3} offlineOnly={4} segMismatch={5} crcMismatch={6} ok={7} GATE={8}",
                        onlineVars.Count, leaves.Count, matched, onlineOnly, offlineOnly, segMismatch, crcMismatch, ok, gate == 1 ? "PASS" : "FAIL"));

                    // ---- 只读实测（离线 CRC 在优化块读取端到端验证）----
                    string readSection = "";
                    if (readN > 0)
                    {
                        List<VarInfo> readCandidates = matchedVars.Where(v => !OfflineSymbolSource.HasIndexedArray(v)).Take(readN).ToList();
                        int readOk = 0, readFail = 0;
                        StringBuilder readLines = new StringBuilder();
                        foreach (VarInfo v in readCandidates)
                        {
                            PlcTag tag = OfflineSymbolSource.CreatePlcTag(v);
                            string result;
                            try
                            {
                                int r = PlcTags.ReadTags(conn, new List<PlcTag> { tag });
                                if (r == 0)
                                {
                                    result = "OK " + tag.ToString();
                                    readOk++;
                                }
                                else
                                {
                                    result = "ERR " + r;
                                    readFail++;
                                }
                            }
                            catch (Exception ex)
                            {
                                result = "EX " + ex.Message;
                                readFail++;
                            }
                            readLines.Append("READ\t" + v.Name + "\t" + v.AccessSequence + "\t" + result).AppendLine();
                        }
                        readSection = readLines.ToString();
                        System.Console.WriteLine(string.Format("READ tried={0} ok={1} fail={2}", readCandidates.Count, readOk, readFail));
                        if (readOk != readCandidates.Count) gate = 0;
                    }

                    // ---- 对拍报告（文件为准，GBK 控制台只打 ASCII 汇总）----
                    string cmpPath = Path.ChangeExtension(outputPath, null) + ".crc_cmp.txt";
                    using (StreamWriter writer = new StreamWriter(cmpPath, false, new UTF8Encoding(false)))
                    {
                        writer.WriteLine("# CRC comparison online vs offline");
                        writer.WriteLine("# Host: " + verifyHost);
                        writer.WriteLine("# online=" + onlineVars.Count + " offline=" + leaves.Count + " matched=" + matched);
                        writer.WriteLine("# onlineOnly=" + onlineOnly + " offlineOnly=" + offlineOnly + " segMismatch=" + segMismatch + " crcMismatch=" + crcMismatch + " ok=" + ok);
                        writer.WriteLine("# GATES: onlineOnly==0 segMismatch==0 crcMismatch==0");
                        foreach (string line in mismatchLines)
                        {
                            writer.WriteLine(line);
                        }
                        if (readN > 0)
                        {
                            writer.WriteLine("# READ: " + readSection.Length);
                            writer.Write(readSection);
                        }
                    }
                    System.Console.WriteLine("Wrote: " + cmpPath);
                }

                if (gate == 1)
                {
                    System.Console.WriteLine("GATE=PASS");
                    return 0;
                }
                if (gate == 0)
                {
                    System.Console.WriteLine("GATE=FAIL");
                    return 1;
                }
                System.Console.WriteLine("EXPORT DONE (no verify)");
                return 0;
            }
            finally
            {
                if (conn != null)
                {
                    try
                    {
                        conn.Disconnect();
                    }
                    catch
                    {
                    }
                }
                src.Dispose();
            }
        }

        private static int UsageError(string msg)
        {
            System.Console.WriteLine("USAGE ERROR: " + msg);
            System.Console.WriteLine("Usage: TestS7CommPlusDriver.System.Console.exe --offline <projectPath> [outputPath] [--plc <name>] [--gt] [--crc] [--conn] [--sym <symbol>] [--verify <host> [password]] [--read <N>]");
            return 2;
        }

        /// <summary>符号大小写扰动：翻转全部字母大小写（无字母返回原串）——回归"博途符号名大小写不敏感"。</summary>
        private static string PerturbCase(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            char[] chars = s.ToCharArray();
            bool changed = false;
            for (int i = 0; i < chars.Length; i++)
            {
                if (char.IsLower(chars[i])) { chars[i] = char.ToUpperInvariant(chars[i]); changed = true; }
                else if (char.IsUpper(chars[i])) { chars[i] = char.ToLowerInvariant(chars[i]); changed = true; }
            }
            return changed ? new string(chars) : s;
        }
    }
}
