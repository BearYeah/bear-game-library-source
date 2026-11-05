using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
namespace NovaLibrary;
public class Receipt {
 public string SteamPath {get;set;}="";public string Mode {get;set;}="";
 public string InstalledHash {get;set;}="";public string IniHash {get;set;}="";
 public string OriginalIni {get;set;}="";public string OriginalDll {get;set;}="";
 public string BackupDir {get;set;}="";public bool Installed {get;set;}
 public Dictionary<string,NormalFileRecord> NormalFiles {get;set;}=new(StringComparer.OrdinalIgnoreCase);
}
public partial class PatchService {
 public const string StandardHash="065aeb26e8e65c3e5e2a47281964b0e3dd144953118293ea276ab1c2823a327c";
 public const string FamilyHash="8b2fc282cdcfe61a03011c174ca2e1c1ff796fbe78f8bf1ae50e8dd866333f70";
 public const string OriginalHash="52a6de71ab613ec51183dbf0d6d47130002b9a6389678e3a3c1b4d15b3437ca7";
 public const string OriginalFamilyHash="e8c7a79917dc941eaf609e192da039eda38920e53f5d121fc3735e1d4ce212ec";
 public const string ClientHash="caba4826aa3501039d095aee1843a6bfb270fb43a3ab4455b2d6733223579fee";
 static readonly HashSet<string> Known=new(StringComparer.OrdinalIgnoreCase){StandardHash,FamilyHash,OriginalHash,OriginalFamilyHash};
 readonly Storage storage;readonly Action checkProcesses;readonly bool checkClient;
 public PatchService(Storage storage,Action? processCheck=null,bool verifyClient=true){this.storage=storage;checkProcesses=processCheck??EnsureClosed;checkClient=verifyClient;}
 public static void EnsureClosed(){
  foreach(var name in new[]{"steam","steamwebhelper"}){
   var found=Process.GetProcessesByName(name);try{if(found.Length>0)throw new InvalidOperationException("请先完全退出 Steam，再安装、卸载或清理补丁缓存。程序不会强制结束游戏。");}finally{foreach(var p in found)p.Dispose();}
  }
 }
 public static string ValidateSteam(string input){
  if(string.IsNullOrWhiteSpace(input))throw new InvalidOperationException("请先在设置里选择 Steam 目录。");
  var p=Path.GetFullPath(input.Trim()).TrimEnd(Path.DirectorySeparatorChar);
  SafeFiles.NoLinks(p,p);
  var exe=Path.Combine(p,"steam.exe");SafeFiles.NoLinks(p,exe);
  if(!File.Exists(exe))throw new InvalidOperationException("该目录中没有 steam.exe，请选择 Steam 安装目录。");
  return p;
 }
 public string Status(string steam){
  try{var p=ValidateSteam(steam);var f=Path.Combine(p,"user32.dll");SafeFiles.NoLinks(p,f);
   if(!File.Exists(f)){var receipt=LoadReceipt(p);return receipt?.Installed==true&&receipt.Mode=="normal"?"旧普通模式 · 可卸载或切换 user32":LegacyNormalFiles.HasCore(p)?"检测到旧普通模式文件":"补丁未安装";}
   if(LegacyNormalFiles.HasCore(p))return "普通 / 隐蔽模式文件冲突 · 请重新安装";
   return SafeFiles.HashFile(f) switch{StandardHash=>"隐身模式 · 已安装",FamilyHash=>"隐士模式 · 已安装",OriginalHash=>"user32 原版 · 未修复",OriginalFamilyHash=>"SF 原版 · 未修复",_=>"发现其他 user32.dll"};
  }catch{return "Steam 路径未设置";}
 }
 string ReceiptPath(string steam)=>Path.Combine(storage.Root,"patch-"+SafeFiles.Hash(System.Text.Encoding.UTF8.GetBytes(steam.ToLowerInvariant()))[..16]+".json");
 Receipt? LoadReceipt(string steam){var file=ReceiptPath(steam);SafeFiles.NoLinks(storage.Root,file);return File.Exists(file)?JsonSerializer.Deserialize<Receipt>(File.ReadAllText(file)):null;}
 void SaveReceipt(string steam,Receipt receipt){var f=ReceiptPath(steam);SafeFiles.NoLinks(storage.Root,f);SafeFiles.Atomic(f,JsonSerializer.SerializeToUtf8Bytes(receipt,Storage.Json));}
 string BackupDir(string action){
  var dir=Path.Combine(storage.Backup,action+"-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-ffff")+"-"+Guid.NewGuid().ToString("N")[..6]);
  SafeFiles.NoLinks(storage.Root,dir);Directory.CreateDirectory(dir);return dir;
 }
 static byte[] Payload(string mode){
  string file=mode=="standard"?"standard.dll":mode=="families"?"families.dll":throw new InvalidOperationException("未知补丁模式。");
  using var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("NovaLibrary.Payloads."+file)??throw new InvalidDataException("程序内的补丁资源缺失。");
  using var m=new MemoryStream();stream.CopyTo(m);var bytes=m.ToArray();
  if(SafeFiles.Hash(bytes)!=(mode=="standard"?StandardHash:FamilyHash))throw new InvalidDataException("补丁资源校验失败，安装已停止。");
  return bytes;
 }
 static void RecognizedDll(string steam,string file){
  SafeFiles.NoLinks(steam,file);
  if(File.Exists(file)&&!Known.Contains(SafeFiles.HashFile(file)))throw new InvalidOperationException("目标目录中的 DLL 不属于已识别补丁，未进行覆盖或删除。请先核对该文件。");
 }
 Dictionary<string,byte[]?> Snapshot(string steam,IEnumerable<string> files){
  var result=new Dictionary<string,byte[]?>();foreach(var f in files){SafeFiles.NoLinks(steam,f);result[f]=File.Exists(f)?File.ReadAllBytes(f):null;}return result;
 }
 static void RestoreSnapshot(string steam,Dictionary<string,byte[]?> snapshots){
  foreach(var (f,b) in snapshots){
   SafeFiles.NoLinks(steam,f);
   if(b==null){if(File.Exists(f))File.Delete(f);}
   else if(!File.Exists(f)||SafeFiles.HashFile(f)!=SafeFiles.Hash(b))SafeFiles.Atomic(f,b);
  }
 }
 void BackupSnapshot(string dir,Dictionary<string,byte[]?> snapshots){
  foreach(var (f,b) in snapshots)if(b!=null){var outFile=Path.Combine(dir,Path.GetFileName(f));SafeFiles.NoLinks(storage.Root,outFile);File.WriteAllBytes(outFile,b);}
 }
 void ClearPackage(string steam,string backup){
  var f=Path.Combine(steam,"appcache","packageinfo.vdf");SafeFiles.NoLinks(steam,f);
  if(File.Exists(f)){SafeFiles.NoLinks(storage.Root,backup);Directory.CreateDirectory(backup);var target=Path.Combine(backup,"packageinfo.vdf");SafeFiles.NoLinks(storage.Root,target);File.Move(f,target,true);}
 }
 public string? InstalledMode(string input){
  var steam=ValidateSteam(input);var file=Path.Combine(steam,"user32.dll");SafeFiles.NoLinks(steam,file);
  if(!File.Exists(file)){var receipt=LoadReceipt(steam);return receipt?.Installed==true&&receipt.Mode=="normal"?"normal":null;}
  if(LegacyNormalFiles.HasCore(steam))throw new InvalidOperationException("检测到普通模式与隐蔽模式文件并存，请选择需要的模式后重新安装。");
  return SafeFiles.HashFile(file) switch{StandardHash=>"standard",FamilyHash=>"families",_=>null};
 }
 public ClientCompatibilityResult? LastCompatibility {get;private set;}
 public string CompatibilityReportPath=>Path.Combine(storage.Root,"compatibility-latest.json");
 public ClientCompatibilityResult CheckClient(string input,string mode){
  var steam=ValidateSteam(input);var file=Path.Combine(steam,"steamclient64.dll");SafeFiles.NoLinks(steam,file);
  var report=ClientCompatibility.Check(file,mode);LastCompatibility=report;
  SafeFiles.NoLinks(storage.Root,CompatibilityReportPath);SafeFiles.Atomic(CompatibilityReportPath,JsonSerializer.SerializeToUtf8Bytes(report,Storage.Json));
  return report;
 }
 void VerifyClient(string steam,string mode){
  if(!checkClient)return;
  var result=CheckClient(steam,mode);
  if(!result.Compatible)throw new InvalidOperationException(result.Summary+"\n请更新补丁适配；检测报告："+CompatibilityReportPath);
 }
 public void PreflightInstall(string input,string mode,IEnumerable<GameItem> games){
  var steam=ValidateSteam(input);var receipt=LoadReceipt(steam);
  _=NormalRemovalPlan(steam,receipt);VerifyClient(steam,mode);Rules.ValidateMappings(games);_ = Payload(mode);
  RecognizedDll(steam,Path.Combine(steam,"user32.dll"));
  var ini=Path.Combine(steam,"AppList","AppList.ini");SafeFiles.NoLinks(steam,ini);SafeFiles.NoLinks(steam,Path.Combine(steam,"appcache","packageinfo.vdf"));
  _ = IniConfig.Write(File.Exists(ini)?File.ReadAllBytes(ini):null,games);_ = LoadReceipt(steam);
 }
 public void PreflightUninstall(string input){
  var steam=ValidateSteam(input);RecognizedDll(steam,Path.Combine(steam,"user32.dll"));RecognizedDll(steam,Path.Combine(steam,"user32SF.dll"));
  var ini=Path.Combine(steam,"AppList","AppList.ini");SafeFiles.NoLinks(steam,ini);SafeFiles.NoLinks(steam,Path.Combine(steam,"appcache","packageinfo.vdf"));
  var receipt=LoadReceipt(steam);_=NormalRemovalPlan(steam,receipt);
  if(receipt?.Installed==true&&File.Exists(ini)&&SafeFiles.HashFile(ini)==receipt.IniHash&&!string.IsNullOrEmpty(receipt.OriginalIni)){
   SafeFiles.NoLinks(storage.Root,receipt.OriginalIni);if(!File.Exists(receipt.OriginalIni))throw new IOException("原配置备份缺失，请恢复备份后重试。");
  }
 }
 public void PreflightConfig(string input,IEnumerable<GameItem> games,bool clearPackageCache=false){
  var steam=ValidateSteam(input);Rules.ValidateMappings(games);var ini=Path.Combine(steam,"AppList","AppList.ini");SafeFiles.NoLinks(steam,ini);
  _ = IniConfig.Write(File.Exists(ini)?File.ReadAllBytes(ini):null,games);_ = LoadReceipt(steam);
  if(clearPackageCache){
   SafeFiles.NoLinks(steam,Path.Combine(steam,"appcache","packageinfo.vdf"));
   var installed=InstalledMode(steam);if(installed=="normal")throw new InvalidOperationException("普通模式已移除，请先卸载旧补丁，或安装 user32 模式完成切换。");
   var dll=Path.Combine(steam,"user32.dll");SafeFiles.NoLinks(steam,dll);
   if(checkClient&&File.Exists(dll)){var hash=SafeFiles.HashFile(dll);if(hash is StandardHash or OriginalHash)VerifyClient(steam,"standard");else if(hash is FamilyHash or OriginalFamilyHash)VerifyClient(steam,"families");}
  }
 }
 string InstallCore(string input,string mode,IEnumerable<GameItem> games){
  checkProcesses();var list=games.ToList();PreflightInstall(input,mode,list);var steam=ValidateSteam(input);
  var dll=Path.Combine(steam,"user32.dll");var ini=Path.Combine(steam,"AppList","AppList.ini");var cache=Path.Combine(steam,"appcache","packageinfo.vdf");
  RecognizedDll(steam,dll);SafeFiles.NoLinks(steam,ini);SafeFiles.NoLinks(steam,cache);
  var receipt=LoadReceipt(steam);var normalRestore=NormalRemovalPlan(steam,receipt);
  var payload=Payload(mode);var before=Snapshot(steam,new[]{dll,ini,cache}.Concat(normalRestore.Keys));var receiptFile=ReceiptPath(steam);
  SafeFiles.NoLinks(storage.Root,receiptFile);
  byte[]? oldReceipt=File.Exists(receiptFile)?File.ReadAllBytes(receiptFile):null;
  var backup=BackupDir("install");BackupSnapshot(backup,before);
  bool installedBefore=receipt?.Installed==true;
  receipt??=new Receipt();
  if(!installedBefore){receipt.OriginalDll=before[dll]==null?"":Path.Combine(backup,"user32.dll");receipt.OriginalIni=before[ini]==null?"":Path.Combine(backup,"AppList.ini");receipt.BackupDir=backup;}
  var bytes=IniConfig.Write(before[ini],list);
  try{
   RestoreSnapshot(steam,normalRestore);receipt.NormalFiles.Clear();
   Directory.CreateDirectory(Path.Combine(steam,"AppList"));SafeFiles.Atomic(ini,bytes);SafeFiles.Atomic(dll,payload);
   ClearPackage(steam,backup);
   receipt.SteamPath=steam;receipt.Mode=mode;receipt.InstalledHash=SafeFiles.Hash(payload);receipt.IniHash=SafeFiles.Hash(bytes);receipt.Installed=true;SaveReceipt(steam,receipt);
   return "补丁已安装，配置已写入。现在可以正常启动 Steam。\n备份："+backup;
  }catch{
   RestoreSnapshot(steam,before);
   if(oldReceipt!=null)SafeFiles.Atomic(receiptFile,oldReceipt);else if(File.Exists(receiptFile))File.Delete(receiptFile);
   throw;
  }
 }
 string SaveConfigCore(string input,IEnumerable<GameItem> games,bool clearPackageCache=false){
  if(clearPackageCache)checkProcesses();var list=games.ToList();PreflightConfig(input,list,clearPackageCache);
  var steam=ValidateSteam(input);var ini=Path.Combine(steam,"AppList","AppList.ini");var cache=Path.Combine(steam,"appcache","packageinfo.vdf");
  var snapshots=Snapshot(steam,clearPackageCache?new[]{ini,cache}:new[]{ini});var existing=snapshots[ini];var bytes=IniConfig.Write(existing,list);
  var receipt=LoadReceipt(steam);var receiptFile=ReceiptPath(steam);SafeFiles.NoLinks(storage.Root,receiptFile);
  var oldReceipt=File.Exists(receiptFile)?File.ReadAllBytes(receiptFile):null;
  var backup=BackupDir("config");BackupSnapshot(backup,snapshots);
  try{
   SafeFiles.Atomic(ini,bytes);if(clearPackageCache)ClearPackage(steam,backup);
   if(receipt?.Installed==true){receipt.IniHash=SafeFiles.Hash(bytes);SaveReceipt(steam,receipt);}
  }catch{
   RestoreSnapshot(steam,snapshots);
   if(oldReceipt!=null)SafeFiles.Atomic(receiptFile,oldReceipt);else if(File.Exists(receiptFile))File.Delete(receiptFile);
   throw;
  }
  return clearPackageCache?"配置已保存，packageinfo 缓存已移至备份。":"配置已保存。请完全退出并重新启动 Steam，使新的映射生效。";
 }
 string UninstallCore(string input){
  var initial=LoadReceipt(ValidateSteam(input));if(initial?.NormalFiles.Count>0)return UninstallNormal(input);
  checkProcesses();PreflightUninstall(input);var steam=ValidateSteam(input);
  var dll=Path.Combine(steam,"user32.dll");var sf=Path.Combine(steam,"user32SF.dll");var ini=Path.Combine(steam,"AppList","AppList.ini");var cache=Path.Combine(steam,"appcache","packageinfo.vdf");
  RecognizedDll(steam,dll);RecognizedDll(steam,sf);SafeFiles.NoLinks(steam,ini);SafeFiles.NoLinks(steam,cache);
  var receipt=LoadReceipt(steam);var before=Snapshot(steam,new[]{dll,sf,ini,cache});var backup=BackupDir("uninstall");BackupSnapshot(backup,before);
  string note="";
  try{
   if(File.Exists(dll))File.Delete(dll);if(File.Exists(sf))File.Delete(sf);
   if(receipt?.Installed==true&&before[ini]!=null&&SafeFiles.Hash(before[ini]!)==receipt.IniHash){
    if(!string.IsNullOrEmpty(receipt.OriginalIni)){SafeFiles.NoLinks(storage.Root,receipt.OriginalIni);if(!File.Exists(receipt.OriginalIni))throw new IOException("原配置备份缺失，卸载已回退。");SafeFiles.Atomic(ini,File.ReadAllBytes(receipt.OriginalIni));}
    else File.Delete(ini);
   }else if(receipt?.Installed==true)note="\nAppList 配置被其他程序改过，已保留它。";
   else if(before[ini]!=null){
    var decoded=IniConfig.Decode(before[ini]!);
    var sections=System.Text.RegularExpressions.Regex.Matches(decoded,@"(?m)^\s*\[([^\]]+)\]");
    if(sections.Count==1&&sections[0].Groups[1].Value.Equals("AppList",StringComparison.OrdinalIgnoreCase))File.Delete(ini);else note="\n未识别的配置内容已保留。";
   }
   ClearPackage(steam,backup);
   storage.ClearCache();
   if(receipt!=null){receipt.Installed=false;SaveReceipt(steam,receipt);}
   return "补丁已卸载，packageinfo 缓存及本程序封面、资料缓存已清理。游戏库和备份仍保留。"+note+"\n备份："+backup;
  }catch{RestoreSnapshot(steam,before);throw;}
 }
 string CleanPackageCacheCore(string input){checkProcesses();var steam=ValidateSteam(input);var backup=BackupDir("cache");ClearPackage(steam,backup);return "packageinfo 缓存已移至备份，下次启动 Steam 会重新获取相关信息。";}
}
