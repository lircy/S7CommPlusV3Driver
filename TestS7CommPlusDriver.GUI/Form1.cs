using Microsoft.Extensions.Logging;
using S7CommPlusDriver;
using S7CommPlusDriver.ClientApi;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using TestS7CommPlusDriver.Shared;

namespace TestS7CommPlusDriver.GUI
{
    /// <summary>
    /// S7CommPlusDriver GUI 测试 Demo：连接、在线/离线取树、符号读写。
    /// 界面控件只在 Form1.Designer.cs 中创建，本文件不手工构造控件。
    /// </summary>
    public partial class Form1 : Form
    {
        private const string ProjectFilter = "TIA 工程 (*.ap*;*.zap*)|*.ap*;*.zap*|所有文件 (*.*)|*.*";

        private S7CommPlusConnection conn;
        private bool connected;

        public Form1()
        {
            InitializeComponent();
            conn = new S7CommPlusConnection();
            conn.Logger = new ControlDiagLogger(this, LogLevel.Debug);

            string[] args = Environment.GetCommandLineArgs();
            if (args.Length >= 2) tbHost.Text = args[1];
            if (args.Length >= 3) tbPassword.Text = args[2];
        }

        // ---------- 公共辅助 ----------

        private void InvokeIfNeeded(Action action)
        {
            if (IsDisposed || action == null) return;
            try
            {
                if (InvokeRequired) BeginInvoke(action);
                else action();
            }
            catch
            {
                // 窗口关闭过程中的异步回调。
            }
        }

        private void Log(string message)
        {
            if (IsDisposed) return;
            tbLog.AppendText("[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + message + Environment.NewLine);
        }

        private void SetStatus(string status)
        {
            if (IsDisposed) return;
            lbStatus.Text = status;
        }

        private S7CommPlusConnection EnsureConnectionObject()
        {
            if (conn == null)
            {
                conn = new S7CommPlusConnection();
                conn.Logger = new ControlDiagLogger(this, LogLevel.Debug);
            }
            return conn;
        }

        private bool EnsureConnected()
        {
            if (connected && conn != null) return true;
            try
            {
                S7CommPlusConnection target = EnsureConnectionObject();
                target.UseSecureCommunication = false;
                int res = target.Connect(tbHost.Text.Trim(), tbPassword.Text.Trim(), tbUser.Text.Trim(), 8000);
                if (res != 0)
                {
                    connected = false;
                    Log("连接失败，错误码: " + res);
                    return false;
                }
                connected = true;
                Log("已连接 " + tbHost.Text.Trim());
                return true;
            }
            catch (Exception ex)
            {
                connected = false;
                Log("连接异常: " + ex.Message);
                return false;
            }
        }

        // ---------- 连接 ----------

        private void btnConnect_Click(object sender, EventArgs e)
        {
            SetStatus("connecting...");
            SetStatus(EnsureConnected() ? "connected" : "error");
        }

        private void btnDisconnect_Click(object sender, EventArgs e)
        {
            SetStatus("disconnecting...");
            // 未连接（或已断开过）时不许再调用核心库 Disconnect：
            // 从未连接时 DeleteObject(0)/关闭空套接字会抛异常；
            // 重复断开时 DeleteObject 带着旧 SessionId 在无连接套接字上等待响应会卡死 UI。
            try
            {
                if (connected && conn != null)
                {
                    conn.Disconnect();
                }
            }
            catch (Exception ex)
            {
                Log("断开异常: " + ex.Message);
            }
            connected = false;
            SetStatus("disconnected");
        }

        private void Form1_FormClosed(object sender, FormClosedEventArgs e)
        {
            try
            {
                if (conn != null)
                {
                    conn.LoadSymbolsFromFile(null);
                    if (connected) conn.Disconnect();
                }
            }
            catch
            {
                // Demo 退出时忽略清理异常。
            }
        }

        // ---------- 契约冒烟（共享 ContractSmoke，原生 API 实现） ----------

        private void btnContractSmoke_Click(object sender, EventArgs e)
        {
            if (!EnsureConnected()) return;
            btnContractSmoke.Enabled = false;
            SetStatus("contract smoke running...");
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    bool ok = ContractSmoke.Run(conn, msg => InvokeIfNeeded(() => Log(msg)));
                    InvokeIfNeeded(() =>
                    {
                        treeView1.Nodes.Clear();
                        SetStatus(ok ? "contract smoke OK" : "contract smoke FAIL");
                        Log("注: 冒烟测试重建了底层符号缓存，树控件已清空，请重新在线获取");
                    });
                }
                catch (Exception ex)
                {
                    InvokeIfNeeded(() =>
                    {
                        Log("冒烟异常: " + ex.Message);
                        SetStatus("error");
                    });
                }
                finally
                {
                    InvokeIfNeeded(() => btnContractSmoke.Enabled = true);
                }
            });
        }

        // ---------- 在线/离线符号树 ----------

        private void btnBrowse_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog dlg = new OpenFileDialog { Filter = ProjectFilter })
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    tbProject.Text = dlg.FileName;
                }
            }
        }

        private void btnLoadProject_Click(object sender, EventArgs e)
        {
            string path = tbProject.Text.Trim();
            if (path.Length == 0)
            {
                Log("请先选择博途工程文件");
                return;
            }

            S7CommPlusConnection target = EnsureConnectionObject();
            SetStatus("loading offline project...");
            Task.Factory.StartNew(() => target.LoadSymbolsFromFile(path)).ContinueWith(task =>
            {
                InvokeIfNeeded(() =>
                {
                    try
                    {
                        int res = task.Result;
                        if (res != 0)
                        {
                            SetStatus("offline load failed");
                            Log("加载工程失败，错误码: " + res);
                            return;
                        }
                        BuildProjectTree();
                        SetStatus("offline project loaded");
                        Log("离线符号树已加载: " + path);
                    }
                    catch (Exception ex)
                    {
                        SetStatus("offline load failed");
                        Log("加载工程异常: " + ex.Message);
                    }
                });
            });
        }

        private void btnOnlineTree_Click(object sender, EventArgs e)
        {
            if (!EnsureConnected()) return;
            try
            {
                conn.LoadSymbolsFromFile(null);
                BuildProjectTree();
            }
            catch (Exception ex)
            {
                SetStatus("error");
                Log("在线取树异常: " + ex.Message);
            }
        }

        /// <summary>共享建树：在线/离线都使用当前驱动的块表、区对象和 relid 懒加载接口。</summary>
        private void BuildProjectTree()
        {
            if (conn == null)
            {
                Log("连接对象尚未创建");
                return;
            }

            treeView1.BeginUpdate();
            try
            {
                treeView1.Nodes.Clear();
                SetStatus("loading...");
                List<string> plcNames = conn.GetListOfPlcs() ?? new List<string>();
                if (plcNames.Count > 1)
                {
                    for (int i = 0; i < plcNames.Count; i++)
                    {
                        TreeNode plcNode = treeView1.Nodes.Add(plcNames[i]);
                        plcNode.Nodes.Add("Loading...");
                        plcNode.Tag = 0x90070000U + (uint)i * 0x100U;
                        plcNode.Expand();
                    }
                    SetStatus("load finished");
                    return;
                }

                string plcName = plcNames.Count == 1 ? plcNames[0] : null;
                TreeNode root = treeView1.Nodes.Add(string.IsNullOrEmpty(plcName) ? "(PLC)" : plcName);
                root.Tag = 0x90070000U;
                List<S7CommPlusConnection.DatablockInfo> dbInfoList;
                if (conn.GetListOfDatablocks(out dbInfoList) != 0)
                {
                    SetStatus("error");
                    return;
                }
                AddBlockNodes(root, dbInfoList);
                AddAreaNodes(root, 0);
                root.Expand();
                SetStatus("load finished");
            }
            finally
            {
                treeView1.EndUpdate();
            }
        }

        private void AddBlockNodes(TreeNode parent, List<S7CommPlusConnection.DatablockInfo> dbInfoList)
        {
            if (dbInfoList == null) return;
            foreach (S7CommPlusConnection.DatablockInfo dbInfo in dbInfoList)
            {
                TreeNode node = parent.Nodes.Add(dbInfo.db_name);
                node.Nodes.Add("Loading...");
                node.Tag = dbInfo.db_block_ti_relid;
            }
        }

        private void AddAreaNodes(TreeNode parent, uint plcOffset)
        {
            AddAreaNode(parent, "Inputs", 0x90010000U + plcOffset);
            AddAreaNode(parent, "Outputs", 0x90020000U + plcOffset);
            AddAreaNode(parent, "Merker", 0x90030000U + plcOffset);
            AddAreaNode(parent, "S7Timers", 0x90050000U + plcOffset);
            AddAreaNode(parent, "S7Counters", 0x90060000U + plcOffset);
        }

        private void AddAreaNode(TreeNode parent, string text, uint relationId)
        {
            TreeNode node = parent.Nodes.Add(text);
            node.Nodes.Add("Loading...");
            node.Tag = relationId;
        }

        private void treeView1_AfterExpand(object sender, TreeViewEventArgs e)
        {
            if (e.Node == null || e.Node.Nodes.Count == 0 || e.Node.Nodes[0].Text != "Loading...") return;
            if (conn == null) return;

            e.Node.Nodes.Clear();
            uint relationId = (uint)e.Node.Tag;
            if (relationId >= 0x90070000U && relationId < 0x90080000U)
            {
                int plcIndex = (int)((relationId - 0x90070000U) / 0x100U);
                List<S7CommPlusConnection.DatablockInfo> dbInfoList;
                if (conn.GetListOfDatablocks(plcIndex, out dbInfoList) != 0)
                {
                    SetStatus("error");
                    return;
                }
                AddBlockNodes(e.Node, dbInfoList);
                AddAreaNodes(e.Node, (uint)plcIndex * 0x100U);
                return;
            }

            PObject pObj;
            try
            {
                pObj = conn.getTypeInfoByRelId(relationId);
            }
            catch (Exception ex)
            {
                SetStatus("error");
                Log("展开节点失败: " + ex.Message);
                e.Node.Nodes.Add("Error: " + ex.Message);
                return;
            }
            if (pObj == null || pObj.VarnameList == null || pObj.VartypeList == null) return;

            for (int i = 0; i < pObj.VarnameList.Names.Count; ++i)
            {
                PVartypeListElement vte = pObj.VartypeList.Elements[i];
                TreeNode memberNode = e.Node.Nodes.Add(pObj.VarnameList.Names[i]);
                if (vte.OffsetInfoType.Is1Dim())
                {
                    IOffsetInfoType_1Dim array = (IOffsetInfoType_1Dim)vte.OffsetInfoType;
                    uint elementCount = array.GetArrayElementCount();
                    int lowerBound = array.GetArrayLowerBounds();
                    for (int j = 0; j < elementCount; ++j)
                    {
                        TreeNode elementNode = memberNode.Nodes.Add(pObj.VarnameList.Names[i] + "[" + (j + lowerBound) + "]");
                        if (vte.OffsetInfoType.HasRelation())
                        {
                            IOffsetInfoType_Relation relation = (IOffsetInfoType_Relation)vte.OffsetInfoType;
                            elementNode.Nodes.Add("Loading...");
                            elementNode.Tag = relation.GetRelationId();
                        }
                    }
                    memberNode.Tag = 0U;
                }
                else if (vte.OffsetInfoType.IsMDim())
                {
                    AddMultiDimArrayChildren(memberNode, pObj.VarnameList.Names[i], vte);
                    memberNode.Tag = 0U;
                }
                else if (vte.OffsetInfoType.HasRelation())
                {
                    IOffsetInfoType_Relation relation = (IOffsetInfoType_Relation)vte.OffsetInfoType;
                    memberNode.Nodes.Add("Loading...");
                    memberNode.Tag = relation.GetRelationId();
                }
            }
        }

        private void AddMultiDimArrayChildren(TreeNode parent, string memberName, PVartypeListElement vte)
        {
            IOffsetInfoType_MDim multiDim = (IOffsetInfoType_MDim)vte.OffsetInfoType;
            uint[] elementCounts = multiDim.GetMdimArrayElementCount();
            int[] lowerBounds = multiDim.GetMdimArrayLowerBounds();
            int dimCount = elementCounts.Aggregate(0, (count, value) => count += value > 0 ? 1 : 0);
            int[] indexes = new int[dimCount];
            bool stop = false;
            while (!stop)
            {
                string indexText = "";
                for (int dimension = dimCount - 1; dimension >= 0; --dimension)
                {
                    indexText += (indexText.Length == 0 ? "" : ",") + (indexes[dimension] + lowerBounds[dimension]);
                }

                TreeNode elementNode = parent.Nodes.Add(memberName + "[" + indexText + "]");
                if (vte.OffsetInfoType.HasRelation())
                {
                    IOffsetInfoType_Relation relation = (IOffsetInfoType_Relation)vte.OffsetInfoType;
                    elementNode.Nodes.Add("Loading...");
                    elementNode.Tag = relation.GetRelationId();
                }

                ++indexes[0];
                for (int dimension = 0; dimension < dimCount; ++dimension)
                {
                    if (indexes[dimension] >= elementCounts[dimension])
                    {
                        if (dimension + 1 < dimCount)
                        {
                            indexes[dimension] = 0;
                            ++indexes[dimension + 1];
                        }
                        else
                        {
                            stop = true;
                        }
                    }
                    else
                    {
                        break;
                    }
                }
            }
        }

        private void treeView1_AfterSelect(object sender, TreeViewEventArgs e)
        {
            if (conn == null || e.Node == null) return;

            if (e.Node.Tag is uint)
            {
                uint relationId = (uint)e.Node.Tag;
                if (relationId >= 0x90070000U && relationId < 0x90080000U)
                {
                    conn.SelectPlc(e.Node.Text);
                    Log("活动 PLC: " + e.Node.Text);
                }
                return;
            }

            string symbol = GetSymbolFromNode(e.Node);
            tbSymbol.Text = symbol;
            if (symbol.Length > 0)
            {
                ReadSelectedSymbol();
            }
        }

        private string GetSymbolFromNode(TreeNode node)
        {
            string name = "";
            TreeNode current = node;
            while (current != null)
            {
                bool isArray = false;
                string nodeText = current.Text;
                current = current.Parent;
                if (current != null && current.Tag is uint && (uint)current.Tag == 0U)
                {
                    isArray = true;
                    current = current.Parent;
                }

                if (current != null && current.Tag is uint)
                {
                    uint relationId = (uint)current.Tag;
                    if (relationId >= 0x90010000U && relationId < 0x90040000U)
                    {
                        TreeNode plcNode = current.Parent;
                        if (plcNode != null) conn.SelectPlc(plcNode.Text);
                        current = null;
                    }
                    else if (relationId >= 0x90070000U && relationId < 0x90080000U)
                    {
                        conn.SelectPlc(current.Text);
                        current = null;
                    }
                }

                name = EscapeTiaString(nodeText, current == null, isArray)
                    + (name.Length == 0 ? "" : ".") + name;
            }
            return name;
        }

        private string EscapeTiaString(string text, bool isRootNode, bool isArray)
        {
            if (isRootNode) return "\"" + text + "\"";
            Regex invalidChars = new Regex("(^[0-9]|[^0-9A-Za-z_])");
            if (!isArray)
            {
                return invalidChars.IsMatch(text) ? "\"" + text + "\"" : text;
            }

            Match match = new Regex("^([^\"]*)(\\[[0-9, ]+\\])$").Match(text);
            if (!match.Success) return text;
            string memberName = match.Groups[1].Value;
            string indexes = match.Groups[2].Value;
            return invalidChars.IsMatch(memberName) ? "\"" + memberName + "\"" + indexes : text;
        }

        // ---------- 符号读写 ----------

        private void btnRead_Click(object sender, EventArgs e)
        {
            if (tbSymbol.Text.Trim().Length == 0) return;
            ReadSelectedSymbol();
        }

        private void btnWrite_Click(object sender, EventArgs e)
        {
            WriteSelectedSymbol(false);
        }

        private void btnWriteText_Click(object sender, EventArgs e)
        {
            WriteSelectedSymbol(true);
        }

        private void ReadSelectedSymbol()
        {
            if (conn == null) return;
            tbValue.Text = "";
            tbDatatype.Text = "";
            try
            {
                SetStatus("reading...");
                PlcTag tag = conn.getPlcTagBySymbol(tbSymbol.Text.Trim());
                if (tag == null)
                {
                    SetStatus("symbol not found");
                    Log("未找到变量: " + tbSymbol.Text.Trim());
                    return;
                }

                tbDatatype.Text = Softdatatype.Types.ContainsKey(tag.Datatype)
                    ? Softdatatype.Types[tag.Datatype]
                    : "UNKNOWN_" + tag.Datatype;

                if (!connected || PlcTags.ReadTags(conn, new List<PlcTag> { tag }) != 0)
                {
                    SetStatus(connected ? "read failed" : "not connected");
                    return;
                }

                tbValue.Text = Shared.ContractSmoke.StripQualityPrefix(tag.ToString());
                SetStatus("connected");
                Log("读 " + tbSymbol.Text.Trim() + " = " + tbValue.Text);
            }
            catch (Exception ex)
            {
                SetStatus("error");
                Log("读异常: " + ex.Message);
            }
        }

        private void WriteSelectedSymbol(bool textMode)
        {
            if (!connected || conn == null)
            {
                Log("请先连接 PLC");
                return;
            }

            string symbol = tbSymbol.Text.Trim();
            if (symbol.Length == 0) return;
            try
            {
                SetStatus("writing...");
                PlcTag tag = conn.getPlcTagBySymbol(symbol);
                if (tag == null)
                {
                    SetStatus("symbol not found");
                    Log("未找到变量: " + symbol);
                    return;
                }

                SetTagValue(tag, tbValue.Text, textMode);
                int res = PlcTags.WriteTags(conn, new List<PlcTag> { tag });
                if (res == 0 && tag.LastWriteError == 0)
                {
                    SetStatus("connected");
                    Log((textMode ? "文本写 " : "写 ") + symbol + " ← " + tbValue.Text + " : 成功");
                }
                else
                {
                    SetStatus("write failed");
                    Log((textMode ? "文本写 " : "写 ") + symbol
                        + " 失败，res=" + res + "，LastWriteError=0x" + tag.LastWriteError.ToString("X"));
                }
            }
            catch (Exception ex)
            {
                SetStatus("error");
                Log((textMode ? "文本写" : "写") + "异常: " + ex.Message);
            }
        }

        private void SetTagValue(PlcTag tag, string text, bool preserveText)
        {
            PropertyInfo valueProperty = tag.GetType().GetProperty("Value");
            if (valueProperty == null || !valueProperty.CanWrite)
            {
                throw new InvalidOperationException("变量类型不支持写入");
            }

            Type targetType = valueProperty.PropertyType;
            object value;
            if (targetType == typeof(string))
            {
                value = text;
            }
            else if (targetType == typeof(bool))
            {
                value = ParseBool(text);
            }
            else if (targetType == typeof(char))
            {
                if (text.Length == 0) throw new FormatException("字符值为空");
                value = text[0];
            }
            else if (targetType == typeof(DateTime))
            {
                value = DateTime.Parse(text, CultureInfo.InvariantCulture);
            }
            else if (targetType == typeof(TimeSpan))
            {
                value = TimeSpan.Parse(text, CultureInfo.InvariantCulture);
            }
            else if (targetType.IsEnum)
            {
                value = Enum.Parse(targetType, text);
            }
            else
            {
                value = Convert.ChangeType(text, targetType, CultureInfo.InvariantCulture);
            }

            valueProperty.SetValue(tag, value, null);
        }

        private bool ParseBool(string text)
        {
            string normalized = text.Trim().ToUpperInvariant();
            if (normalized == "1" || normalized == "TRUE" || normalized == "ON") return true;
            if (normalized == "0" || normalized == "FALSE" || normalized == "OFF") return false;
            return bool.Parse(text);
        }

        /// <summary>Demo 宿主注入用 ILogger：驱动日志安全转发到 WinForms 文本框。</summary>
        private sealed class ControlDiagLogger : ILogger
        {
            private readonly Form1 _form;
            private readonly LogLevel _minLevel;

            internal ControlDiagLogger(Form1 form, LogLevel minLevel)
            {
                _form = form;
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
                if (_form.IsDisposed || !IsEnabled(logLevel)) return;
                string message = formatter == null ? state.ToString() : formatter(state, exception);
                if (exception != null)
                {
                    message += " | " + exception;
                }
                string line = string.Format("[驱动 {0}] {1}", logLevel, message);
                _form.InvokeIfNeeded(() => _form.Log(line));
            }
        }
    }
}
