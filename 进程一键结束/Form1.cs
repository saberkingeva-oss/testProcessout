using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
namespace ProcessKiller
{
    public partial class Form1 : Form
    {
        // Win32 API 导入：快速获取进程完整路径，替代 MainModule
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr hObject);
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        private readonly string configFile = Path.Combine(Application.StartupPath, "processList.txt");
        // Tuple: (exe名称, 描述, 完整文件路径)
        private List<Tuple<string, string, string>> _procCache = new List<Tuple<string, string, string>>();
        private HashSet<string> _blackListCache = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private ListView listView1;
        private Label labelTip;
        private Button btnProcessList, btnCheck, btnEdit, btnKillAll;
        private ContextMenuStrip _menu;
        private ToolStripMenuItem _itemAddBlack, _itemOpenFile;

        public Form1()
        {
            InitializeComponent();
            this.Icon = new Icon("silverN.ico");
            this.Text = "进程一键结束器 BY:银月";
            this.Size = new Size(700, 520);
            BuildUI();
        }

        /// <summary>
        /// 快速获取进程路径，替代 proc.MainModule
        /// </summary>
        private string GetProcessPathFast(int pid)
        {
            IntPtr hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProc == IntPtr.Zero)
                return "";
            StringBuilder sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            string path = "";
            if (QueryFullProcessImageName(hProc, 0, sb, ref size))
            {
                path = sb.ToString();
            }
            CloseHandle(hProc);
            return path;
        }
        private void BuildUI()
        {
            // ListView：关闭OwnerDraw！！！不再自定义绘制，解决鼠标移动持续重绘
            listView1 = new ListView
            {
                Location = new Point(20, 20),
                Size = new Size(640, 380),
                View = View.Details,
                VirtualMode = true,
                FullRowSelect = true,
                MultiSelect = false,
                OwnerDraw = false, // 关键：关闭自绘
                HotTracking = false,
                ShowItemToolTips = false
            };
            listView1.Columns.Add("名称", 220);
            listView1.Columns.Add("描述", 400);
            listView1.RetrieveVirtualItem += ListView1_RetrieveVirtualItem;
            // === 删除 DrawSubItem、DrawColumnHeader 事件 ===
            //右键菜单
            _menu = new ContextMenuStrip();
            _itemAddBlack = new ToolStripMenuItem("添加黑名单");
            _itemOpenFile = new ToolStripMenuItem("查看文件");
            _menu.Items.Add(_itemAddBlack);
            _menu.Items.Add(_itemOpenFile);
            _itemAddBlack.Click += MenuAddBlack_Click;
            _itemOpenFile.Click += MenuOpenFileLocation_Click;
            listView1.ContextMenuStrip = _menu;
            //按钮
            btnProcessList = new Button { Text = "进程列表", Location = new Point(20, 410), Size = new Size(120, 30) };
            btnCheck = new Button { Text = "检测进程", Location = new Point(150, 410), Size = new Size(120, 30) };
            btnEdit = new Button { Text = "编辑", Location = new Point(280, 410), Size = new Size(120, 30) };
            btnKillAll = new Button { Text = "一键结束", Location = new Point(410, 410), Size = new Size(120, 30) };
            btnProcessList.Click += btnProcessList_Click;
            btnCheck.Click += btnCheck_Click;
            btnEdit.Click += btnEdit_Click;
            btnKillAll.Click += btnKillAll_Click;
            labelTip = new Label { Location = new Point(540, 415), Size = new Size(120, 20), Text = "" };
            this.Controls.Add(listView1);
            this.Controls.Add(btnProcessList);
            this.Controls.Add(btnCheck);
            this.Controls.Add(btnEdit);
            this.Controls.Add(btnKillAll);
            this.Controls.Add(labelTip);
        }
        private void ListView1_RetrieveVirtualItem(object sender, RetrieveVirtualItemEventArgs e)
        {
            if (e.ItemIndex >= 0 && e.ItemIndex < _procCache.Count)
            {
                var d = _procCache[e.ItemIndex];
                e.Item = new ListViewItem(d.Item1);
                e.Item.SubItems.Add(d.Item2);
                // 黑名单行设置底色 LightCoral，不用GDI绘制
                if (_blackListCache.Contains(d.Item1))
                {
                    e.Item.BackColor = Color.LightCoral;
                }
                else
                {
                    e.Item.BackColor = Color.White;
                }
            }
        }
        private void MenuAddBlack_Click(object sender, EventArgs e)
        {
            if (listView1.SelectedIndices.Count == 0) return;
            int idx = listView1.SelectedIndices[0];
            string name = _procCache[idx].Item1;
            string line = $"{name}-手动添加{Environment.NewLine}";
            File.AppendAllText(configFile, line);
        }
        private void MenuOpenFileLocation_Click(object sender, EventArgs e)
        {
            if (listView1.SelectedIndices.Count == 0) return;
            int idx = listView1.SelectedIndices[0];
            string path = _procCache[idx].Item3;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                MessageBox.Show("文件路径无效或进程已退出");
                return;
            }
            try
            {
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"打开失败：{ex.Message}");
            }
        }
        private async void btnProcessList_Click(object sender, EventArgs e)
        {
            listView1.Enabled = false;
            labelTip.Text = "读取进程...";
            labelTip.ForeColor = Color.Black;
            var list = await Task.Run(() =>
            {
                var res = new List<Tuple<string, string, string>>();
                Process[] allProcs = Process.GetProcesses();
                foreach (var p in allProcs)
                {
                    string exeName = $"{p.ProcessName}.exe";
                    string desc = "无法读取";
                    string filePath = "";
                    try
                    {
                        filePath = GetProcessPathFast(p.Id);
                        if (!string.IsNullOrEmpty(filePath))
                        {
                            exeName = Path.GetFileName(filePath);
                            desc = FileVersionInfo.GetVersionInfo(filePath).FileDescription;
                        }
                    }
                    catch { }
                    res.Add(Tuple.Create(exeName, desc, filePath));
                }
                return res;
            });
            _procCache = list;
            _blackListCache.Clear();
            listView1.VirtualListSize = _procCache.Count;
            labelTip.Text = $"共 {_procCache.Count} 个进程";
            listView1.Enabled = true;
        }
        private async void btnCheck_Click(object sender, EventArgs e)
        {
            listView1.Enabled = false;
            labelTip.Text = "";
            // ===== 清空旧缓存 =====
            _procCache.Clear();
            _blackListCache.Clear();
            listView1.VirtualListSize = 0;
            listView1.Invalidate();
            if (!File.Exists(configFile))
            {
                labelTip.Text = "未检测到相关进程";
                labelTip.ForeColor = Color.Red;
                listView1.Enabled = true;
                return;
            }
            var data = await Task.Run(() =>
            {
                var list = new List<Tuple<string, string, string>>();
                var hash = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var lines = File.ReadAllLines(configFile);
                bool found = false;
                // 黑名单只查询匹配进程，不再枚举全部进程
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    string separator = ".exe-";
                    int sepIndex = line.IndexOf(separator);
                    if (sepIndex <= 0) continue;
                    string exeName = line.Substring(0, sepIndex + 4).Trim();
                    string desc = line.Substring(sepIndex + separator.Length).Trim();
                    string pName = exeName.Remove(exeName.Length - 4);

                    Process[] matches = Process.GetProcessesByName(pName);
                    foreach (var proc in matches)
                    {
                        try
                        {
                            if (!proc.HasExited)
                            {
                                string filePath = GetProcessPathFast(proc.Id);
                                list.Add(Tuple.Create(exeName, desc, filePath));
                                hash.Add(exeName);
                                found = true;
                                break;
                            }
                        }
                        catch (System.ComponentModel.Win32Exception)
                        {
                            continue;
                        }
                    }
                }
                return new { list, hash, found };
            });
            _procCache = data.list;
            _blackListCache = data.hash;
            listView1.VirtualListSize = _procCache.Count;
            listView1.Invalidate();
            if (!data.found)
            {
                labelTip.Text = "未检测到相关进程";
                labelTip.ForeColor = Color.Red;
            }
            listView1.Enabled = true;
        }
        private void btnEdit_Click(object sender, EventArgs e)
        {
            Form editForm = new Form();
            editForm.Text = "编辑黑名单";
            editForm.Size = new Size(450, 320);
            TextBox tb = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                Location = new Point(10, 10),
                Size = new Size(410, 230)
            };
            if (File.Exists(configFile))
                tb.Text = File.ReadAllText(configFile);
            Button saveBtn = new Button { Text = "保存", Location = new Point(340, 250), Size = new Size(80, 30) };
            saveBtn.Click += (s, ev) =>
            {
                File.WriteAllText(configFile, tb.Text);
                editForm.DialogResult = DialogResult.OK;
                editForm.Close();
            };
            editForm.Controls.Add(tb);
            editForm.Controls.Add(saveBtn);
            if (editForm.ShowDialog() == DialogResult.OK)
            {
                labelTip.Text = "保存成功";
                labelTip.ForeColor = Color.Red;
            }
        }
        private void btnKillAll_Click(object sender, EventArgs e)
        {
            if (!File.Exists(configFile)) return;
            var lines = File.ReadAllLines(configFile);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                string separator = ".exe-";
                int sepIndex = line.IndexOf(separator);
                if (sepIndex <= 0) continue;
                string exeName = line.Substring(0, sepIndex + 4).Trim();
                string procName = exeName.Remove(exeName.Length - 4);
                for (int t = 0; t < 3; t++)
                {
                    Process[] ps = Process.GetProcessesByName(procName);
                    if (ps.Length == 0) break;
                    foreach (var p in ps)
                    {
                        try
                        {
                            p.Kill();
                            p.WaitForExit(150);
                        }
                        catch
                        {
                        }
                    }
                }
            }
        }
    }
}
