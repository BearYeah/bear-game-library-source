namespace NovaLibrary;
// Only removes installations created by older Bear versions. No installer or injector is bundled.
public sealed class NormalFileRecord {
 public string InstalledHash {get;set;}="";public string Original {get;set;}="";public string OriginalHash {get;set;}="";
}
internal static class LegacyNormalFiles {
 internal const string Launcher="bin/x86launcher.exe";
 internal static readonly Dictionary<string,string> Files=new(StringComparer.OrdinalIgnoreCase){
  ["bin/x86launcher.exe"]="a667ca16a350480a1ad3663235c41a9d7f5dbf3ce8cf262b52d3942f1cce5967",
  ["DLLInjector.exe"]="9fedb54b153e475ab8fa1defe207d3d337ad3de6f1fbeae3d22c1f072ebf37be",
  ["GreenLuma2026_Files/AchievementUnlocked.wav"]="d52a59386d0c79e0cb46e9f9130d9639279a2a276512e63417d2a61eb66c7f61",
  ["GreenLuma2026_Files/BootImage.bmp"]="75fc7210b4cc1aaac0711c9a13499fb44d46f52b750bf61b3dd79dc082cdbe40",
  ["GreenLumaSettings_2026.exe"]="91101076a9e1de32be94b653df567db3d6b8079050d30ac49d2ac9f8b5d2e07d",
  ["GreenLuma_2026_x64.dll"]="59a7320c2aaf7249fa2fccfb8a8fd4b649919ff3fb6fc891538a58a62b57297e",
  ["GreenLuma_2026_x86.dll"]="e3eb30b0ae6bf610ff64f089e37cac9faa536d218288bfe491ad82fc33ebc37e",
  ["DLLInjector.ini"]="c3dede2c4613f2b31f2ea114bbd6c04f2a5e26589ff1f5e5abb0e43305c4174a"
 };
 internal static bool Recognized(string name,string hash)=>Files.TryGetValue(name,out var expected)&&(hash==expected||name=="GreenLuma_2026_x64.dll"&&hash=="306e977512a20ecfa91359102583c66decc569439aad066f294766f91d2e6438");
 internal static string Target(string steam,string relative){var file=Path.GetFullPath(Path.Combine(steam,relative));SafeFiles.NoLinks(steam,file);if(Directory.Exists(file))throw new IOException("目标文件位置被目录占用："+relative);return file;}
 internal static bool HasCore(string steam)=>File.Exists(Target(steam,"GreenLuma_2026_x64.dll"))||File.Exists(Target(steam,"GreenLuma_2026_x86.dll"))||File.Exists(Target(steam,"DLLInjector.exe"));
}
public partial class PatchService {
 Dictionary<string,byte[]?> NormalRemovalPlan(string steam,Receipt? receipt){
  var result=new Dictionary<string,byte[]?>();
  if(receipt?.NormalFiles.Count is not >0){if(LegacyNormalFiles.HasCore(steam)||File.Exists(LegacyNormalFiles.Target(steam,LegacyNormalFiles.Launcher))&&SafeFiles.HashFile(LegacyNormalFiles.Target(steam,LegacyNormalFiles.Launcher))==LegacyNormalFiles.Files[LegacyNormalFiles.Launcher])throw new InvalidOperationException("检测到非本程序管理的普通模式文件。请先用原备份恢复手动安装，再由 Bear 安装，以保留原启动器的恢复路径。");return result;}
  if(receipt.NormalFiles.Count!=LegacyNormalFiles.Files.Count)throw new InvalidDataException("普通模式安装记录不完整，请恢复安装记录后重试。");
  foreach(var (relative,record) in receipt.NormalFiles){
   if(!LegacyNormalFiles.Files.TryGetValue(relative,out var expected)||!LegacyNormalFiles.Recognized(relative,record.InstalledHash))throw new InvalidDataException("普通模式安装记录与内置文件不一致。");
   var target=LegacyNormalFiles.Target(steam,relative);if(File.Exists(target)&&SafeFiles.HashFile(target)!=record.InstalledHash){if(relative=="DLLInjector.ini"){result[target]=File.ReadAllBytes(target);continue;}throw new InvalidOperationException("普通模式文件已被其他程序更改，未覆盖或删除："+relative);}
   if(record.Original.Length==0)result[target]=null;
   else{SafeFiles.NoLinks(storage.Root,record.Original);if(!File.Exists(record.Original)||SafeFiles.HashFile(record.Original)!=record.OriginalHash)throw new IOException("普通模式原文件备份缺失或校验失败："+relative);result[target]=File.ReadAllBytes(record.Original);}
  }
  return result;
 }
 string UninstallNormal(string input){
  checkProcesses();PreflightUninstall(input);var steam=ValidateSteam(input);var receipt=LoadReceipt(steam)!;var restore=NormalRemovalPlan(steam,receipt);
  var ini=LegacyNormalFiles.Target(steam,"AppList/AppList.ini");var dll=LegacyNormalFiles.Target(steam,"user32.dll");var sf=LegacyNormalFiles.Target(steam,"user32SF.dll");var cache=LegacyNormalFiles.Target(steam,"appcache/packageinfo.vdf");
  var before=Snapshot(steam,restore.Keys.Concat(new[]{ini,dll,sf,cache}));var backup=BackupDir("normal-uninstall");BackupSnapshot(backup,before);var receiptFile=ReceiptPath(steam);var oldReceipt=File.ReadAllBytes(receiptFile);string note=File.Exists(Path.Combine(steam,"DLLInjector.ini"))&&SafeFiles.HashFile(Path.Combine(steam,"DLLInjector.ini"))!=receipt.NormalFiles["DLLInjector.ini"].InstalledHash?Environment.NewLine+"DLLInjector.ini 已被修改，已保留。":"";
  try{
   RestoreSnapshot(steam,restore);if(File.Exists(dll))File.Delete(dll);if(File.Exists(sf))File.Delete(sf);
   if(before[ini]!=null&&SafeFiles.Hash(before[ini]!)==receipt.IniHash){if(receipt.OriginalIni.Length>0){SafeFiles.NoLinks(storage.Root,receipt.OriginalIni);SafeFiles.Atomic(ini,File.ReadAllBytes(receipt.OriginalIni));}else File.Delete(ini);}else note="\nAppList 已被其他程序修改，已保留。";
   ClearPackage(steam,backup);receipt.NormalFiles.Clear();receipt.Installed=false;SaveReceipt(steam,receipt);storage.ClearCache();
   return "普通模式已卸载，原启动器已按备份恢复，相关缓存已清理；游戏库和备份保留。"+note+"\n备份："+backup;
  }catch{RestoreSnapshot(steam,before);SafeFiles.Atomic(receiptFile,oldReceipt);throw;}
 }
 public void PreflightLaunch(string input){var steam=ValidateSteam(input);var mode=InstalledMode(steam);if(mode=="normal"||mode==null&&LegacyNormalFiles.HasCore(steam))throw new InvalidOperationException("普通模式已移除，请先卸载旧补丁，或安装 user32 模式完成切换。");if(mode!=null)VerifyClient(steam,mode);}
}
