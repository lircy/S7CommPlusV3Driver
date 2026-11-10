// 契约冒烟（当前核心库原生 API 版，2026-11-10）：
//   在线阶段: Connect → Browse 全树 → 等距抽样 → PlcTags 批量读值校验；
//   离线阶段: 第二连接 LoadSymbolsFromFile 加载 .ap*/.zap*/目录工程，
//             离线 Browse 叶与在线 Browse 叶按符号名/类型抽样比对（默认 500）。
// 等价替代参考资料中基于 DriverContracts.IPlcDriver 的 ContractSmokeCore
// （_DemoShared/ContractSmokeCore.cs 未随参考资料提供，且 IPlcDriver 契约
// 在当前核心库不存在，故按同口径用原生 API 重新实现）。
using S7CommPlusDriver;
using S7CommPlusDriver.ClientApi;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace TestS7CommPlusDriver.Shared
{
    public static class ContractSmoke
    {
        public const int DefaultSampleSize = 500;
        public const int ReadBatchSize = 100;

        /// <summary>在线阶段：浏览全树 → 抽样 → 批量读值。返回整体是否通过。</summary>
        public static bool Run(S7CommPlusConnection conn, Action<string> log, int sampleSize = DefaultSampleSize)
        {
            List<VarInfo> vars;
            return Run(conn, out vars, log, sampleSize);
        }

        /// <summary>在线阶段（带在线叶表输出，供离线阶段比对）。</summary>
        public static bool Run(S7CommPlusConnection conn, out List<VarInfo> vars, Action<string> log,
            int sampleSize = DefaultSampleSize)
        {
            vars = null;
            if (conn == null) { log("冒烟失败: 连接对象为 null"); return false; }

            log("==== 契约冒烟（在线阶段） ====");
            // 强制在线符号源：若调用方之前加载了离线工程，Browse 会路由到离线树，
            // 抽样地址与在线 PLC 不符导致读值失败（对齐参考 Demo 的
            // AcquireSymbolTree(SymbolTreeSource.Online) 语义，始终取在线树）。
            conn.LoadSymbolsFromFile(null);
            List<VarInfo> browsed;
            int res = conn.Browse(out browsed);
            vars = browsed;
            if (res != 0 || browsed == null || browsed.Count == 0)
            {
                log(string.Format("冒烟失败: Browse res={0}, count={1}",
                    res, browsed == null ? 0 : browsed.Count));
                return false;
            }
            log(string.Format("在线叶变量: {0}", browsed.Count));

            // 等距抽样；经 OfflineSymbolSource.CreatePlcTag 构造 PlcTag，
            // 复用驱动统一的 ItemAddress/SymbolCrc 规则（直接用 AccessSequence 会丢 CRC）。
            int stride = Math.Max(1, browsed.Count / Math.Max(1, sampleSize));
            List<PlcTag> tags = new List<PlcTag>();
            int skipped = 0;
            for (int i = 0; i < browsed.Count && tags.Count < sampleSize; i += stride)
            {
                try
                {
                    PlcTag tag = OfflineSymbolSource.CreatePlcTag(browsed[i]);
                    if (tag == null) { skipped++; continue; }
                    tags.Add(tag);
                }
                catch
                {
                    skipped++;
                }
            }
            log(string.Format("抽样读值: {0}/{1} 个变量（跳过不支持类型 {2} 个，批量 {3}）",
                tags.Count, browsed.Count, skipped, ReadBatchSize));
            if (tags.Count == 0)
            {
                log("冒烟失败: 没有可读的抽样变量");
                return false;
            }

            int readErr = 0;
            Stopwatch watch = Stopwatch.StartNew();
            for (int start = 0; start < tags.Count; start += ReadBatchSize)
            {
                int count = Math.Min(ReadBatchSize, tags.Count - start);
                List<PlcTag> batch = tags.GetRange(start, count);
                int r = PlcTags.ReadTags(conn, batch);
                foreach (PlcTag t in batch)
                {
                    if (r != 0 || t.LastReadError != 0)
                    {
                        readErr++;
                        if (readErr <= 10)
                        {
                            log(string.Format("  READ-FAIL {0}: res={1} LastReadError=0x{2:X}",
                                t.Name, r, t.LastReadError));
                        }
                    }
                }
            }
            watch.Stop();
            log(string.Format("读值完成: {0}/{1} 成功，耗时 {2}ms",
                tags.Count - readErr, tags.Count, watch.ElapsedMilliseconds));

            for (int i = 0; i < Math.Min(5, tags.Count); i++)
            {
                log(string.Format("  样例 {0} = {1}", tags[i].Name,
                    tags[i].LastReadError == 0 ? StripQualityPrefix(tags[i].ToString()) : "(失败)"));
            }

            bool ok = readErr == 0;
            log(ok ? "在线阶段: PASS" : "在线阶段: FAIL");
            return ok;
        }

        /// <summary>离线阶段：第二连接加载 TIA 工程，离线树与在线叶表抽样比对。</summary>
        public static bool RunOfflinePhase(S7CommPlusConnection onlineConn, List<VarInfo> onlineVars,
            Func<S7CommPlusConnection> connectionFactory, string projectPath, Action<string> log,
            int sampleSize = DefaultSampleSize)
        {
            if (onlineVars == null || onlineVars.Count == 0)
            {
                log("离线阶段跳过: 无在线叶表");
                return false;
            }

            log("==== 契约冒烟（离线阶段） ====");
            S7CommPlusConnection offlineConn = connectionFactory == null ? null : connectionFactory();
            if (offlineConn == null) { log("离线阶段失败: 无法创建连接"); return false; }
            try
            {
                int res = offlineConn.LoadSymbolsFromFile(projectPath);
                if (res != 0)
                {
                    log("离线阶段失败: LoadSymbolsFromFile res=" + res);
                    return false;
                }

                List<VarInfo> offlineVars;
                res = offlineConn.Browse(out offlineVars);
                if (res != 0 || offlineVars == null)
                {
                    log("离线阶段失败: 离线 Browse res=" + res);
                    return false;
                }
                log(string.Format("离线叶变量: {0}（工程: {1}）", offlineVars.Count, projectPath));

                Dictionary<string, uint> offlineByName = new Dictionary<string, uint>(StringComparer.Ordinal);
                foreach (VarInfo v in offlineVars)
                {
                    if (v.Name != null && !offlineByName.ContainsKey(v.Name))
                    {
                        offlineByName[v.Name] = v.Softdatatype;
                    }
                }

                int stride = Math.Max(1, onlineVars.Count / Math.Max(1, sampleSize));
                int checkCount = 0, matched = 0, onlineOnly = 0, typeMismatch = 0;
                for (int i = 0; i < onlineVars.Count && checkCount < sampleSize; i += stride)
                {
                    VarInfo ov = onlineVars[i];
                    checkCount++;
                    uint offType;
                    if (!offlineByName.TryGetValue(ov.Name ?? "", out offType)) { onlineOnly++; continue; }
                    matched++;
                    if (offType != ov.Softdatatype)
                    {
                        typeMismatch++;
                        if (typeMismatch <= 10)
                        {
                            log(string.Format("  TYPE-MISMATCH {0}: online={1} offline={2}",
                                ov.Name, ov.Softdatatype, offType));
                        }
                    }
                }
                log(string.Format("抽样比对 {0}: matched={1} onlineOnly={2} typeMismatch={3}",
                    checkCount, matched, onlineOnly, typeMismatch));

                bool ok = onlineOnly == 0 && typeMismatch == 0;
                log(ok ? "离线阶段: PASS" : "离线阶段: FAIL");
                return ok;
            }
            finally
            {
                try
                {
                    offlineConn.LoadSymbolsFromFile(null);
                    offlineConn.Disconnect();
                }
                catch
                {
                    // 清理失败不影响判定。
                }
            }
        }

        /// <summary>去掉 PlcTag.ToString 的质量前缀（上游 ResultString 格式 "XX: value"，
        /// 0xC0=GOOD）。只剥离首字符为质量档（0/4/8/C 档）的前缀，避免误伤以
        /// 两位十六进制开头的字符串值。</summary>
        public static string StripQualityPrefix(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length < 4) return text ?? "";
            char highNibble = text[0];
            bool qualityLike = highNibble == '0' || highNibble == '4'
                || highNibble == '8' || highNibble == 'C' || highNibble == 'c';
            if (qualityLike && text[2] == ':' && text[3] == ' ')
            {
                return text.Substring(4);
            }
            return text;
        }

        /// <summary>日志用值格式化（等价参考资料 ContractSmokeCore.FormatValue）。</summary>
        public static string FormatValue(object value)
        {
            if (value == null) return "(null)";
            byte[] bytes = value as byte[];
            if (bytes != null) return "byte[" + bytes.Length + "]";
            IFormattable formattable = value as IFormattable;
            if (formattable != null) return formattable.ToString(null, CultureInfo.InvariantCulture);
            return value.ToString();
        }
    }
}
