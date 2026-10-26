using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using GhubRepair;
class Tests {
    static int count;
    static void Assert(bool ok,string name) { if(!ok)throw new Exception(name); count++; Console.WriteLine("PASS "+name); }
    static void Reject(Action action,string name) { bool rejected=false; try {action();} catch {rejected=true;} Assert(rejected,name); }
    static int Main() {
        byte[] data=Encoding.UTF8.GetBytes("fixture official depot");
        string hash; using(var sha=SHA256.Create())hash=BitConverter.ToString(sha.ComputeHash(data)).Replace("-","").ToLowerInvariant();
        Depot d=new Depot {Hash=hash,Size=data.Length,Url="https://updates-ghub.services.logitechg.com/test.depot"};
        Assert(Repair.ValidBytes(data,d),"matching hash and size");
        Reject(()=>Repair.OfficialHttps("https://example.com/test"),"reject foreign host");
        Reject(()=>Repair.OfficialHttps("https://updates-ghub.services.logitechg.com.evil.test/test"),"reject suffix spoofing");
        Reject(()=>Repair.OfficialHttps("https://updates-ghub.services.logitechg.com:444/test"),"reject nonstandard port");
        Assert(Repair.OfficialHttps("http://updates-ghub.services.logitechg.com/test").StartsWith("https://"),"upgrade to HTTPS");
        string manifest="{\"depots\":[{\"size\":\""+data.Length+"\",\"mac\":\""+hash+"\",\"url\":\""+d.Url+"\"}]}";
        Assert(Repair.SelectDepot(manifest).Hash==hash,"select verified-size candidate");
        Reject(()=>Repair.SelectDepot("{\"depots\":[{\"size\":\"0\",\"mac\":\"bad\",\"url\":\"http://example.com\"}]}"),"reject unusable manifest");
        string dir=Path.Combine(Path.GetTempPath(),"ghub-repair-tests-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try {
            Assert(Repair.NeedsRepair(dir),"empty-cache diagnosis");
            Reject(()=>Repair.WriteVerifiedCache(dir,new byte[]{1,2},d),"reject corrupted download before write");
            Assert(Directory.GetFiles(dir).Length==0,"corruption leaves no cache files");
            Repair.WriteVerifiedCache(dir,data,d);
            Assert(File.ReadAllBytes(Path.Combine(dir,hash)).Length==data.Length,"verified atomic cache restore");
            File.WriteAllText(Path.Combine(dir,"keep.txt"),"existing cache");
            Repair.WriteVerifiedCache(dir,data,d);
            Assert(File.ReadAllText(Path.Combine(dir,"keep.txt"))=="existing cache","nonempty cache preserved");
            Assert(Directory.GetFiles(dir,"*.tmp").Length==0,"no leftover temporary cache");
        } finally { foreach(string f in Directory.GetFiles(dir)) File.Delete(f); Directory.Delete(dir); }
        Console.WriteLine("Passed "+count+" checks.");return 0;
    }
}
