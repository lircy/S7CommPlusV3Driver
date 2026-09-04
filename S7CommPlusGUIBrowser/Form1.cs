using S7CommPlusDriver;
using S7CommPlusDriver.ClientApi;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace S7CommPlusGUIBrowser
{
    public partial class Form1 : Form
    {

        private S7CommPlusConnection conn = null;
        private bool onlySecurePGOrPCAndHMI = false;
        private bool connected = false;

        public Form1()
        {
            InitializeComponent();

            string[] args = Environment.GetCommandLineArgs();
            // 1st argument can be the plc ip-address, otherwise use default
            if (args.Length >= 2)
            {
                tbIpAddress.Text = args[1];
            }
            // 2nd argument can be the plc password, otherwise use default (no password)
            if (args.Length >= 3)
            {
                tbPassword.Text = args[2];
            }
        }

        private void setStatus(string status)
        {
            lbStatus.Text = $"Status:{status}|";
        }

        private void btnConnect_Click(object sender, EventArgs e)
        {
            setStatus("connecting...");
            connected = false;
            if (conn == null)
            {
                conn = new S7CommPlusConnection();
                conn.OnlySecurePGOrPCAndHMI = onlySecurePGOrPCAndHMI;
            }
            else
            {
                // 复用同一连接对象：已加载的离线工程（LoadSymbolsFromFile）不受影响
                if (connected)
                    conn.Disconnect();
            }
            int res = conn.Connect(tbIpAddress.Text, tbPassword.Text);
            if (res != 0)
            {
                setStatus("error");
                return;
            }
            connected = true;
            setStatus("connected");
        }

        private void btnDisconnect_Click(object sender, EventArgs e)
        {
            setStatus("disconnecting...");
            connected = false;
            if (conn != null) conn.Disconnect();
            // 树保留：断开只影响在线读取；重新 Connect 同一连接对象即可继续读值
            txt_plcInfo.Text = "PLCType:";
            setStatus("disconnected");
        }

        private void Form1_FormClosed(object sender, FormClosedEventArgs e)
        {
            if (conn != null)
            {
                conn.LoadSymbolsFromFile(null);
                if (connected)
                    conn.Disconnect();
            }
        }

        private void treeView1_AfterExpand(object sender, TreeViewEventArgs e)
        {
            if (e.Node.Nodes.Count < 1 || e.Node.Nodes[0].Text != "Loading...") return;

            setStatus("loading...");
            e.Node.Nodes.Clear();
            uint relTiId = (uint)e.Node.Tag;
            if (relTiId >= 0x90070000 && relTiId < 0x90080000)
            {
                // 多 PLC 工程：PLC 节点展开 = 该 PLC 的块 + 5 区（区 relid 带 PLC 索引变体）
                int plcIndex = (int)((relTiId - 0x90070000) / 0x100);
                List<S7CommPlusConnection.DatablockInfo> dbInfoList;
                if (conn.GetListOfDatablocks(plcIndex, out dbInfoList) != 0)
                {
                    setStatus("error");
                    return;
                }
                AddBlockNodes(e.Node, dbInfoList);
                AddAreaNodes(e.Node, (uint)plcIndex * 0x100U);
                return;
            }
            PObject pObj = null;
            try
            {
                pObj = conn.getTypeInfoByRelId(relTiId);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error expanding node: " + ex.Message);
                setStatus("Error: " + ex.Message);
                e.Node.Nodes.Add("Error: " + ex.Message);
                return;
            }
            setStatus("connected");

            if (pObj == null || pObj.VarnameList == null) return;
            TreeNode tn;
            TreeNode tnarr;
            for (int i = 0; i < pObj.VarnameList.Names.Count; ++i)
            {
                tn = e.Node.Nodes.Add(pObj.VarnameList.Names[i]);
                SetImageKey(ref tn, pObj.VartypeList.Elements[i]);
                if (pObj.VartypeList.Elements[i].OffsetInfoType.Is1Dim())
                {
                    var ioitarr = (IOffsetInfoType_1Dim)pObj.VartypeList.Elements[i].OffsetInfoType;
                    uint arrayElementCount = ioitarr.GetArrayElementCount();
                    int arrayLowerBounds = ioitarr.GetArrayLowerBounds();
                    for (int j = 0; j < arrayElementCount; ++j)
                    {
                        tnarr = tn.Nodes.Add(pObj.VarnameList.Names[i] + "[" + (j + arrayLowerBounds) + "]");
                        SetImageKey(ref tnarr, pObj.VartypeList.Elements[i]);
                        if (pObj.VartypeList.Elements[i].OffsetInfoType.HasRelation())
                        {
                            var ioit = (IOffsetInfoType_Relation)pObj.VartypeList.Elements[i].OffsetInfoType;
                            tnarr.Nodes.Add("Loading...");
                            tnarr.Tag = ioit.GetRelationId();
                            SetImageKey(ref tnarr, pObj.VartypeList.Elements[i]);
                        }
                    }
                    tn.Tag = (uint)0; // is array
                }
                else if (pObj.VartypeList.Elements[i].OffsetInfoType.IsMDim())
                {
                    var ioitarrm = (IOffsetInfoType_MDim)pObj.VartypeList.Elements[i].OffsetInfoType;
                    uint[] MdimArrayElementCount = ioitarrm.GetMdimArrayElementCount();
                    int[] MdimArrayLowerBounds = ioitarrm.GetMdimArrayLowerBounds();
                    int dimCount = MdimArrayElementCount.Aggregate(0, (acc, act) => acc += (act > 0) ? 1 : 0);
                    int[] indexes = new int[dimCount];
                    bool stop = false;
                    while (!stop)
                    {
                        string arrIdxStr = "";
                        for (int j = dimCount - 1; j >= 0; --j)
                        {
                            arrIdxStr += (arrIdxStr != "" ? "," : "") + (indexes[j] + MdimArrayLowerBounds[j]);
                        }
                        tnarr = tn.Nodes.Add(pObj.VarnameList.Names[i] + "[" + arrIdxStr + "]");
                        SetImageKey(ref tnarr, pObj.VartypeList.Elements[i]);
                        if (pObj.VartypeList.Elements[i].OffsetInfoType.HasRelation())
                        {
                            var ioit = (IOffsetInfoType_Relation)pObj.VartypeList.Elements[i].OffsetInfoType;
                            tnarr.Nodes.Add("Loading...");
                            tnarr.Tag = ioit.GetRelationId();
                            SetImageKey(ref tnarr, pObj.VartypeList.Elements[i]);
                        }
                        ++indexes[0];
                        for (int j = 0; j < dimCount; ++j)
                        {
                            if (indexes[j] >= MdimArrayElementCount[j])
                            {
                                if (j + 1 < dimCount)
                                {
                                    indexes[j] = 0;
                                    ++indexes[j + 1];
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
                    tn.Tag = (uint)0; // is array
                }
                else
                {
                    if (pObj.VartypeList.Elements[i].OffsetInfoType.HasRelation())
                    {
                        var ioit = (IOffsetInfoType_Relation)pObj.VartypeList.Elements[i].OffsetInfoType;
                        tn.Nodes.Add("Loading...");
                        tn.Tag = ioit.GetRelationId();
                        SetImageKey(ref tn, pObj.VartypeList.Elements[i]);
                    }
                }
            }
        }

        private void SetImageKey(ref TreeNode tn, PVartypeListElement vte)
        {
            string sk = "Tag";
            switch (vte.Softdatatype)
            {
                case Softdatatype.S7COMMP_SOFTDATATYPE_BOOL:
                case Softdatatype.S7COMMP_SOFTDATATYPE_BBOOL:
                    sk = "Boolean";
                    break;
                case Softdatatype.S7COMMP_SOFTDATATYPE_INT:
                case Softdatatype.S7COMMP_SOFTDATATYPE_DINT:
                case Softdatatype.S7COMMP_SOFTDATATYPE_ULINT:
                case Softdatatype.S7COMMP_SOFTDATATYPE_LINT:
                case Softdatatype.S7COMMP_SOFTDATATYPE_USINT:
                case Softdatatype.S7COMMP_SOFTDATATYPE_UINT:
                case Softdatatype.S7COMMP_SOFTDATATYPE_UDINT:
                case Softdatatype.S7COMMP_SOFTDATATYPE_SINT:
                // Derived types
                case Softdatatype.S7COMMP_SOFTDATATYPE_BLOCKFB:
                case Softdatatype.S7COMMP_SOFTDATATYPE_BLOCKFC:
                case Softdatatype.S7COMMP_SOFTDATATYPE_OBANY:
                case Softdatatype.S7COMMP_SOFTDATATYPE_OBDELAY:
                case Softdatatype.S7COMMP_SOFTDATATYPE_OBTOD:
                case Softdatatype.S7COMMP_SOFTDATATYPE_OBCYCLIC:
                case Softdatatype.S7COMMP_SOFTDATATYPE_OBATT:
                case Softdatatype.S7COMMP_SOFTDATATYPE_PORT:
                case Softdatatype.S7COMMP_SOFTDATATYPE_RTM:
                case Softdatatype.S7COMMP_SOFTDATATYPE_PIP:
                case Softdatatype.S7COMMP_SOFTDATATYPE_OBPCYCLE:
                case Softdatatype.S7COMMP_SOFTDATATYPE_OBHWINT:
                case Softdatatype.S7COMMP_SOFTDATATYPE_OBDIAG:
                case Softdatatype.S7COMMP_SOFTDATATYPE_OBTIMEERROR:
                case Softdatatype.S7COMMP_SOFTDATATYPE_OBSTARTUP:
                case Softdatatype.S7COMMP_SOFTDATATYPE_DBANY:
                case Softdatatype.S7COMMP_SOFTDATATYPE_DBWWW:
                case Softdatatype.S7COMMP_SOFTDATATYPE_DBDYN:
                    sk = "Integer2";
                    break;
                case Softdatatype.S7COMMP_SOFTDATATYPE_BYTE:
                case Softdatatype.S7COMMP_SOFTDATATYPE_WORD:
                case Softdatatype.S7COMMP_SOFTDATATYPE_DWORD:
                case Softdatatype.S7COMMP_SOFTDATATYPE_LWORD:
                // Derived types
                case Softdatatype.S7COMMP_SOFTDATATYPE_AOMIDENT:
                case Softdatatype.S7COMMP_SOFTDATATYPE_EVENTANY:
                case Softdatatype.S7COMMP_SOFTDATATYPE_EVENTATT:
                case Softdatatype.S7COMMP_SOFTDATATYPE_AOMLINK:
                case Softdatatype.S7COMMP_SOFTDATATYPE_HWANY:
                case Softdatatype.S7COMMP_SOFTDATATYPE_HWIOSYSTEM:
                case Softdatatype.S7COMMP_SOFTDATATYPE_HWDPMASTER:
                case Softdatatype.S7COMMP_SOFTDATATYPE_HWDEVICE:
                case Softdatatype.S7COMMP_SOFTDATATYPE_HWDPSLAVE:
                case Softdatatype.S7COMMP_SOFTDATATYPE_HWIO:
                case Softdatatype.S7COMMP_SOFTDATATYPE_HWMODULE:
                case Softdatatype.S7COMMP_SOFTDATATYPE_HWSUBMODULE:
                case Softdatatype.S7COMMP_SOFTDATATYPE_HWHSC:
                case Softdatatype.S7COMMP_SOFTDATATYPE_HWPWM:
                case Softdatatype.S7COMMP_SOFTDATATYPE_HWPTO:
                case Softdatatype.S7COMMP_SOFTDATATYPE_HWINTERFACE:
                case Softdatatype.S7COMMP_SOFTDATATYPE_HWIEPORT:
                case Softdatatype.S7COMMP_SOFTDATATYPE_CONNANY:
                case Softdatatype.S7COMMP_SOFTDATATYPE_CONNPRG:
                case Softdatatype.S7COMMP_SOFTDATATYPE_CONNOUC:
                case Softdatatype.S7COMMP_SOFTDATATYPE_CONNRID:
                    sk = "Binary2";
                    break;
                case Softdatatype.S7COMMP_SOFTDATATYPE_REAL:
                case Softdatatype.S7COMMP_SOFTDATATYPE_LREAL:
                    sk = "Number2";
                    break;
                case Softdatatype.S7COMMP_SOFTDATATYPE_CHAR:
                case Softdatatype.S7COMMP_SOFTDATATYPE_WCHAR:
                    sk = "Char";
                    break;
                case Softdatatype.S7COMMP_SOFTDATATYPE_STRING:
                case Softdatatype.S7COMMP_SOFTDATATYPE_WSTRING:
                    sk = "Text";
                    break;
                case Softdatatype.S7COMMP_SOFTDATATYPE_DATE:
                    sk = "Date";
                    break;
                case Softdatatype.S7COMMP_SOFTDATATYPE_TIMEOFDAY:
                case Softdatatype.S7COMMP_SOFTDATATYPE_LTOD:
                    sk = "Time";
                    break;
                case Softdatatype.S7COMMP_SOFTDATATYPE_TIME:
                case Softdatatype.S7COMMP_SOFTDATATYPE_LTIME:
                case Softdatatype.S7COMMP_SOFTDATATYPE_S5TIME:
                case Softdatatype.S7COMMP_SOFTDATATYPE_TIMER:
                    sk = "Timer"; // Duration
                    break;
                case Softdatatype.S7COMMP_SOFTDATATYPE_DATEANDTIME:
                case Softdatatype.S7COMMP_SOFTDATATYPE_LDT:
                case Softdatatype.S7COMMP_SOFTDATATYPE_DTL:
                    sk = "Datetime";
                    break;
                case Softdatatype.S7COMMP_SOFTDATATYPE_ANY:
                case Softdatatype.S7COMMP_SOFTDATATYPE_POINTER:
                case Softdatatype.S7COMMP_SOFTDATATYPE_REMOTE:
                    sk = "Any";
                    break;
            }
            if (vte.OffsetInfoType.HasRelation())
            {
                sk = "Structure";
            }
            tn.ImageKey = sk;
            tn.SelectedImageKey = tn.ImageKey;
        }

        private string escapeTiaString(string str, bool isRootNode, bool isArray)
        {
            if (isRootNode) return '"' + str + '"';
            Regex re = new Regex("(^[0-9]|[^0-9A-Za-z_])");
            if (isArray)
            {
                Regex reArr = new Regex("^([^\"]*)(\\[[0-9, ]+\\])$");
                Match m = reArr.Match(str);
                if (!m.Success) return str;
                if (re.Match(m.Groups[1].Value).Success) return '"' + m.Groups[1].Value + '"' + m.Groups[2].Value;
                return str;
            }
            if (re.IsMatch(str)) return '"' + str + '"';
            return str;
        }

        private void readTagBySymbol()
        {
            tbValue.Text = "";
            tbSymbolicAddress.Text = "";

            setStatus("loading...");
            PlcTag tag = conn.getPlcTagBySymbol(tbSymbol.Text);
            setStatus("connected");
            if (tag == null) return;

            tbSymbolicAddress.Text = tag.Address.GetAccessString();
            tbDatatype.Text = Softdatatype.Types.ContainsKey(tag.Datatype) ? Softdatatype.Types[tag.Datatype] : "UNKNOWN";

            if (conn == null || (conn != null && !connected) || PlcTags.ReadTags(conn,new List<PlcTag> { tag}) != 0) return;
            tbValue.Text = tag.ToString();
        }

        private void treeView1_AfterSelect(object sender, TreeViewEventArgs e)
        {
            if (e.Node.Tag != null)
            {
                uint tag = (uint)e.Node.Tag;
                if (tag >= 0x90070000 && tag < 0x90080000)
                {
                    // 选中 PLC 节点：切换活动 PLC 并显示其信息（离线读值/符号解析随之切换；在线单 PLC 无操作）
                    conn.SelectPlc(e.Node.Text);
                    txt_plcInfo.Text = $"PLCType: {conn.PLCInformation.PLCType}|MLFB: {conn.PLCInformation.MLFB}|Firmware: {conn.PLCInformation.Firmware}";
                }
                return; // has relId
            }
            if (e.Node.Parent == null)
            {
                // 无 Tag 的树根（兜底）：清空符号字段
                tbSymbol.Text = "";
                tbSymbolicAddress.Text = "";
                return;
            }

            string name = "";
            TreeNode tn = e.Node;
            while (tn != null)
            {
                bool isArray = false;
                string nodeText = tn.Text;
                tn = tn.Parent;
                if (tn != null && tn.Tag != null)
                { // is array
                    if ((uint)tn.Tag == 0)
                    {
                        isArray = true;
                        tn = tn.Parent; // skip array parent
                    }
                }
                if (tn != null && tn.Tag != null)
                { // don't add in/out/merker area as tag
                    uint relId = (uint)tn.Tag;
                    if ((relId >= 0x90010000 && relId < 0x90020000) ||   // Inputs（含 PLC 索引变体）
                        (relId >= 0x90020000 && relId < 0x90030000) ||   // Outputs
                        (relId >= 0x90030000 && relId < 0x90040000))     // Merker
                    {
                        TreeNode plcNode = tn.Parent; // 区上一级 = PLC 节点（单 PLC 时根即 PLC 节点）
                        if (plcNode != null) conn.SelectPlc(plcNode.Text); // 读值解析切换到该 PLC
                        tn = null;
                    }
                    else if (relId >= 0x90070000 && relId < 0x90080000)
                    {
                        // PLC 节点：符号名停止（PLC 名不进符号名），读值解析切换到该 PLC
                        conn.SelectPlc(tn.Text);
                        tn = null;
                    }
                }
                name = escapeTiaString(nodeText, tn == null, isArray) + (name != "" ? "." : "") + name;
            }
            tbSymbol.Text = name;

            readTagBySymbol();
        }

        private void btnRead_Click(object sender, EventArgs e)
        {
            if (tbSymbol.Text == "") return;
            if (!connected || conn == null)
            {
                MessageBox.Show("请先 Connect 到 PLC（离线浏览 + 在线读取）");
                return;
            }

            try
            {
                readTagBySymbol();
            }
            catch (Exception ex)
            {
                MessageBox.Show("ERROR: " + ex.Message);
            }
        }

        /// <summary>
        /// 共享建树（在线/离线同一套代码，零分支）：
        /// 单 PLC / 在线：树根 = 连接对象的 PLC 名（在线 Connect 第 8 步已读；离线 LoadSymbolsFromFile
        /// 已镜像）→ GetListOfDatablocks 块节点（"Loading..." + ti relid）→ 5 个区节点。
        /// 多 PLC 工程：每台 PLC 名直接作为并列根节点（不包工程名层；懒展开才取该 PLC 的块+区，
        /// 区 relid 带 PLC 索引变体）。
        /// 离线时这些 API 内部路由到工程符号（真正按需懒加载）；在线时走原网络路径。
        /// </summary>
        private void BuildProjectTree()
        {
            if (conn == null)
            {
                MessageBox.Show("请先 Connect 到 PLC 或打开离线工程");
                return;
            }
            treeView1.Nodes.Clear();
            setStatus("loading...");
            txt_plcInfo.Text = $"PLCType: {conn.PLCInformation.PLCType}|MLFB: {conn.PLCInformation.MLFB}|Firmware: {conn.PLCInformation.Firmware}";
            List<string> plcNames = conn.GetListOfPlcs();
            if (plcNames.Count > 1)
            {
                // 多 PLC 工程：每台 PLC 名 = 一个根节点（统一从 PLC 名开始，不包工程名层）
                for (int i = 0; i < plcNames.Count; i++)
                {
                    TreeNode plcNode = treeView1.Nodes.Add(plcNames[i]);
                    plcNode.Nodes.Add("Loading...");
                    plcNode.Tag = 0x90070000U + (uint)i * 0x100U; // PLC 节点哨兵（区变体同款编码）
                    plcNode.ImageKey = "Library";
                    plcNode.SelectedImageKey = plcNode.ImageKey;
                    plcNode.Expand(); // 与单 PLC 根一致：建树即展开一层（块列表+5 区，仅一层按需）
                }
                setStatus("load finished");
                return;
            }
            string plcName = plcNames.Count == 1 ? plcNames[0] : null;
            TreeNode root = treeView1.Nodes.Add(string.IsNullOrEmpty(plcName) ? "(PLC)" : plcName);
            root.ImageKey = "Library";
            root.SelectedImageKey = root.ImageKey;
            root.Tag = 0x90070000; // 根哨兵：AfterSelect 名称回走在根处停止（PLC 名不进符号名）
            List<S7CommPlusConnection.DatablockInfo> dbInfoList;
            if (conn.GetListOfDatablocks(out dbInfoList) != 0)
            {
                setStatus("error");
                return;
            }
            AddBlockNodes(root, dbInfoList);
            AddAreaNodes(root, 0);
            root.Expand();
            setStatus("load finished");
        }

        /// <summary>块子节点（"Loading..." + ti relid；在线/离线同款）。</summary>
        private void AddBlockNodes(TreeNode parent, List<S7CommPlusConnection.DatablockInfo> dbInfoList)
        {
            TreeNode tn;
            foreach (S7CommPlusConnection.DatablockInfo dbInfo in dbInfoList)
            {
                tn = parent.Nodes.Add(dbInfo.db_name);
                tn.Nodes.Add("Loading...");
                tn.Tag = dbInfo.db_block_ti_relid;
                tn.ImageKey = "Datablock";
                tn.SelectedImageKey = tn.ImageKey;
            }
        }

        /// <summary>5 个区子节点（区 relid = 基础值 + plcOff；plcOff=0 为单 PLC/在线）。</summary>
        private void AddAreaNodes(TreeNode parent, uint plcOff)
        {
            TreeNode tn;
            // Inputs
            tn = parent.Nodes.Add("Inputs");
            tn.Nodes.Add("Loading...");
            tn.Tag = 0x90010000U + plcOff;
            tn.ImageKey = "Default";
            tn.SelectedImageKey = tn.ImageKey;
            // Outputs
            tn = parent.Nodes.Add("Outputs");
            tn.Nodes.Add("Loading...");
            tn.Tag = 0x90020000U + plcOff;
            tn.ImageKey = "Default";
            tn.SelectedImageKey = tn.ImageKey;
            // Merker
            tn = parent.Nodes.Add("Merker");
            tn.Nodes.Add("Loading...");
            tn.Tag = 0x90030000U + plcOff;
            tn.ImageKey = "Default";
            tn.SelectedImageKey = tn.ImageKey;
            // S7Timers
            tn = parent.Nodes.Add("S7Timers");
            tn.Nodes.Add("Loading...");
            tn.Tag = 0x90050000U + plcOff;
            tn.ImageKey = "Default";
            tn.SelectedImageKey = tn.ImageKey;
            // S7Counters
            tn = parent.Nodes.Add("S7Counters");
            tn.Nodes.Add("Loading...");
            tn.Tag = 0x90060000U + plcOff;
            tn.ImageKey = "Default";
            tn.SelectedImageKey = tn.ImageKey;
        }

        private void btnOfflineBrowse_Click(object sender, EventArgs e)
        {
            string path;
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "选择博途工程（.apXX 归档 / .zapXX 归档 / 解包目录）";
                dlg.Filter = "TIA 工程 (*.ap*;*.zap*)|*.ap*;*.zap*|所有文件 (*.*)|*.*";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                path = dlg.FileName;
            }
            setStatus("loading offline project...");
            if (conn == null)
            {
                conn = new S7CommPlusConnection();
                conn.OnlySecurePGOrPCAndHMI = onlySecurePGOrPCAndHMI;
            }
            Task.Factory.StartNew(delegate()
            {
                try
                {
                    // 唯一入口：LoadSymbolsFromFile 之后浏览/建树与在线完全一致
                    int res = conn.LoadSymbolsFromFile(path);
                    BeginInvoke(new Action(delegate()
                    {
                        if (res != 0)
                        {
                            setStatus("offline load failed");
                            MessageBox.Show("加载工程失败（详见控制台错误输出）");
                            return;
                        }
                        BuildProjectTree();
                        setStatus("offline project loaded");
                    }));
                }
                catch (Exception ex)
                {
                    BeginInvoke(new Action(delegate()
                    {
                        setStatus("offline load failed: " + ex.Message);
                        MessageBox.Show("加载工程失败: " + ex.Message);
                    }));
                }
            });
        }

        private void btnOnlineBrowse_Click(object sender, EventArgs e)
        {
            if (!connected || conn == null)
            {
                MessageBox.Show("请先 Connect 到 PLC");
                return;
            }
            conn.LoadSymbolsFromFile(null); // 卸载工程，回到在线浏览
            BuildProjectTree();
        }

        private void checkBox1_CheckedChanged(object sender, EventArgs e)
        {
                onlySecurePGOrPCAndHMI = checkBox1.Checked;
        }
    }
}
