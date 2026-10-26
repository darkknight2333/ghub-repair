using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace GhubRepair {
    public sealed class Depot {
        public string Hash, Url; public long Size;
    }
    public static class Repair {
        public const int MaxDownload = 4 * 1024 * 1024;
        public static string DataRoot { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LGHUB"); } }
        public static string InstallRoot {
            get {
                foreach (string env in new[] { "ProgramW6432", "ProgramFiles", "ProgramFiles(x86)" }) {
                    string p = Environment.GetEnvironmentVariable(env);
                    if (!String.IsNullOrEmpty(p) && File.Exists(Path.Combine(p, "LGHUB", "lghub.exe"))) return Path.Combine(p,"LGHUB");
                }
                throw new InvalidOperationException("未找到标准路径中的 G HUB 安装。请先使用罗技官方安装程序修复安装。");
            }
        }
        public static void RejectLinks(string path) {
            for (DirectoryInfo d = new DirectoryInfo(path); d != null; d = d.Parent)
                if (d.Exists && (d.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("检测到目录链接，停止写入：" + d.FullName);
        }
        public static Depot SelectDepot(string json) {
            var doc = new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(json);
            var list = (IEnumerable)doc["depots"];
            var candidates = new List<Depot>();
            foreach (Dictionary<string,object> entry in list) {
                long size;
                string hash = Convert.ToString(entry.ContainsKey("mac") ? entry["mac"] : null);
                string url = Convert.ToString(entry.ContainsKey("url") ? entry["url"] : null);
                if (!entry.ContainsKey("size") || !Int64.TryParse(Convert.ToString(entry["size"]),out size)) continue;
                if (!Regex.IsMatch(hash ?? "", "\\A[a-fA-F0-9]{64}\\z") || size <= 0 || size > MaxDownload) continue;
                try { url = OfficialHttps(url); } catch { continue; }
                candidates.Add(new Depot { Hash=hash.ToLowerInvariant(), Url=url, Size=size });
            }
            if (candidates.Count == 0) throw new InvalidOperationException("本机清单中没有可安全校验的小型官方缓存包，停止修复。");
            return candidates.OrderBy(d => d.Size).First();
        }
        public static string OfficialHttps(string raw) {
            Uri uri;
            if (!Uri.TryCreate(raw,UriKind.Absolute,out uri) || (uri.Scheme != "http" && uri.Scheme != "https") || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new InvalidDataException("缓存地址不受支持。");
            if (uri.Host != "updates-ghub.services.logitechg.com" && uri.Host != "updates-ghub.services.logitechg.com.cn") throw new InvalidDataException("缓存地址不是受支持的罗技更新服务器。");
            return new UriBuilder(uri) { Scheme="https", Port=-1 }.Uri.AbsoluteUri;
        }
        public static bool ValidBytes(byte[] bytes, Depot d) {
            if (bytes.LongLength != d.Size) return false;
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant() == d.Hash;
        }
        public static bool NeedsRepair(string cache) { return !Directory.EnumerateFileSystemEntries(cache).Any(); }
        public static string Check() {
            string install=InstallRoot;
            RejectLinks(DataRoot);
            foreach (string name in new[] { "version.json", "current.json", "groups.json", "installation.json" })
                if (!File.Exists(Path.Combine(DataRoot,name))) throw new IOException("缺少 " + name + "，此工具无法处理，请使用官方安装程序。");
            foreach (string name in new[] { "lghub_agent.exe", "lghub_updater.exe" })
                if (!File.Exists(Path.Combine(install,name))) throw new IOException("安装文件缺失：" + name + "，请使用官方安装程序。");
            string depots = Path.Combine(DataRoot,"depots"), cache = Path.Combine(DataRoot,"cache");
            if (!Directory.Exists(depots) || !Directory.EnumerateFileSystemEntries(depots).Any()) throw new IOException("depots 目录缺失或为空，请使用官方安装程序。");
            if (!Directory.Exists(cache)) throw new IOException("cache 目录缺失，本工具仅处理已存在的空缓存目录。");
            RejectLinks(cache);
            if (!NeedsRepair(cache)) return "缓存目录非空，无需本工具修复。\r\n如果仍无法进入 G HUB，请使用罗技官方故障排查流程。";
            SelectDepot(File.ReadAllText(Path.Combine(DataRoot,"current.json"),Encoding.UTF8));
            return "检测到缓存目录为空，符合本工具可处理的故障条件。\r\n点击“尝试修复”，从罗技服务器下载匹配缓存并校验。";
        }
        public static string Restore() {
            Check();
            string cache=Path.Combine(DataRoot,"cache");
            if (!NeedsRepair(cache)) return "缓存非空，未修改任何文件。";
            Depot d=SelectDepot(File.ReadAllText(Path.Combine(DataRoot,"current.json"),Encoding.UTF8));
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create(d.Url);
            request.AllowAutoRedirect=false; request.Timeout=30000; request.ReadWriteTimeout=30000;
            byte[] bytes;
            using (var response=(HttpWebResponse)request.GetResponse()) {
                if (response.StatusCode != HttpStatusCode.OK) throw new IOException("官方服务器未返回成功响应。");
                using (var input=response.GetResponseStream()) using (var output=new MemoryStream()) {
                    byte[] buffer=new byte[8192]; int count;
                    while ((count=input.Read(buffer,0,buffer.Length))>0) {
                        if (output.Length+count > d.Size) throw new InvalidDataException("下载内容超过清单大小，已停止。");
                        output.Write(buffer,0,count);
                    }
                    bytes=output.ToArray();
                }
            }
            if (!ValidBytes(bytes,d)) throw new InvalidDataException("下载文件的大小或 SHA-256 与清单不符，未写入缓存。");
            // Recheck after the network request: an update may have changed the manifest/cache.
            Depot fresh=SelectDepot(File.ReadAllText(Path.Combine(DataRoot,"current.json"),Encoding.UTF8));
            if (fresh.Hash != d.Hash || fresh.Size != d.Size) throw new IOException("G HUB 清单已改变，请稍后重试。");
            RejectLinks(cache);
            if (!NeedsRepair(cache)) return "缓存已由其他程序恢复，未写入任何文件。";
            return WriteVerifiedCache(cache,bytes,d);
        }
        public static string WriteVerifiedCache(string cache, byte[] bytes, Depot d) {
            if (!ValidBytes(bytes,d) || !Regex.IsMatch(d.Hash ?? "", "\\A[a-f0-9]{64}\\z")) throw new InvalidDataException("缓存校验失败。");
            RejectLinks(cache);
            if (!NeedsRepair(cache)) return "缓存非空，未修改任何文件。";
            string temp=Path.Combine(cache,"ghub-repair-"+Guid.NewGuid().ToString("N")+".tmp");
            try {
                using(var fs=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)) { fs.Write(bytes,0,bytes.Length); fs.Flush(true); }
                File.Move(temp,Path.Combine(cache,d.Hash));
            } finally { if(File.Exists(temp)) File.Delete(temp); }
            return "已恢复经过大小和 SHA-256 校验的官方缓存。";
        }
    }
    public sealed class MainForm : Form {
        TextBox status = new TextBox(); Button check=new Button(), fix=new Button();
        public MainForm() {
            Text="G HUB Repair v1.0.0"; Width=670; Height=405; MinimumSize=new System.Drawing.Size(620,390);
            StartPosition=FormStartPosition.CenterScreen; Font=new System.Drawing.Font("Microsoft YaHei UI",10);
            var title=new Label { Text="G HUB 卡在“正在加载配置 / 缺少必需文件”？", Dock=DockStyle.Top, Height=50, Padding=new Padding(16,15,8,0) };
            var info=new Label { Text="此工具仅尝试修复空缓存问题。保留设备配置，不添加后台任务。\r\n需要联网；写入缓存时会请求管理员权限。非罗技官方工具。", Dock=DockStyle.Top, Height=65, Padding=new Padding(16,5,8,0) };
            status.Multiline=true; status.ReadOnly=true; status.ScrollBars=ScrollBars.Vertical; status.Dock=DockStyle.Fill; status.BackColor=System.Drawing.Color.White;
            var panel=new FlowLayoutPanel { Dock=DockStyle.Bottom, Height=60, Padding=new Padding(12) };
            check.Text="检查状态"; check.Width=125; check.Height=32; fix.Text="尝试修复"; fix.Width=125; fix.Height=32;
            panel.Controls.Add(check); panel.Controls.Add(fix); Controls.Add(status); Controls.Add(panel); Controls.Add(info); Controls.Add(title);
            check.Click += delegate { Inspect(); }; fix.Click += async delegate { await Fix(); }; Shown += delegate { Inspect(); };
        }
        void Inspect() { try { status.Text=Repair.Check(); fix.Enabled=status.Text.StartsWith("检测到"); } catch(Exception e) { status.Text=e.Message; fix.Enabled=false; } }
        async Task Fix() {
            if (MessageBox.Show(this,"将下载并校验一个小型官方缓存文件，写入空缓存目录。\n不会重置配置。是否继续？","尝试修复",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes) return;
            check.Enabled=false; fix.Enabled=false; status.Text="正在修复，请完成管理员权限提示并等待……";
            try {
                var start=new ProcessStartInfo(Application.ExecutablePath,"--repair") { UseShellExecute=true, Verb="runas" };
                using(var child=Process.Start(start)) { await Task.Run(()=>child.WaitForExit()); if(child.ExitCode!=0)throw new IOException("修复未完成。详细原因已在管理员修复窗口中显示。"); }
                Inspect();
                if (MessageBox.Show(this,"缓存检查完成。现在关闭并重新打开 G HUB 界面？\n请确保没有正在编辑的配置或正在进行的更新。","重新打开 G HUB",MessageBoxButtons.YesNo)==DialogResult.Yes) {
                    string exe=Path.Combine(Repair.InstallRoot,"lghub.exe");
                    foreach(var process in Process.GetProcessesByName("lghub")) {
                        using(process) { if(String.Equals(process.MainModule.FileName,exe,StringComparison.OrdinalIgnoreCase)) { process.Kill(); process.WaitForExit(5000); } }
                    }
                    Process.Start(new ProcessStartInfo(exe) { UseShellExecute=true });
                }
            } catch(Exception e) { status.Text="未完成修复："+e.Message+"\r\n可重新检查；若取消了 UAC，请再次点击修复。"; }
            finally { check.Enabled=true; }
        }
    }
    public static class Program {
        [STAThread] public static int Main(string[] args) {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            if(args.Length==1 && args[0]=="--repair") {
                try {
                    if(!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) throw new UnauthorizedAccessException("需要管理员权限。");
                    string result=Repair.Restore(); MessageBox.Show(result,"G HUB Repair"); return 0;
                } catch(Exception e) { MessageBox.Show("修复失败："+e.Message,"G HUB Repair",MessageBoxButtons.OK,MessageBoxIcon.Error); return 1; }
            }
            Application.Run(new MainForm()); return 0;
        }
    }
}
