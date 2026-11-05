using System.Text.Json;
using System.Text.RegularExpressions;
namespace NovaLibrary;
public partial class Storage {
 internal object BackupGate {get;}=new();
 public const int BackupLimit=5;
 static readonly Regex OperationBackup=new(@"^(install|config|uninstall|cache|normal-uninstall)-\d{8}-\d{6}-\d{4}-[a-f0-9]{6}$",RegexOptions.IgnoreCase);
 public static void TrimDownloadConfigBackups(string steam){
  if(steam.Length==0)return;var folder=Path.Combine(steam,"config");if(!Directory.Exists(folder))return;
  foreach(var file in Directory.EnumerateFiles(folder,"config.vdf.bear-download-*.bak").OrderByDescending(File.GetLastWriteTimeUtc).ThenByDescending(Path.GetFileName,StringComparer.Ordinal).Skip(BackupLimit)){SafeFiles.NoLinks(folder,file);File.Delete(file);}
 }
 public void TrimBackups(){
  lock(BackupGate){
   PreservePatchOriginals();
   var snapshots=Directory.EnumerateFiles(Backup,"library-*.json").Select(f=>(FileSystemInfo)new FileInfo(f))
    .Concat(Directory.EnumerateDirectories(Backup).Where(d=>OperationBackup.IsMatch(Path.GetFileName(d))).Select(d=>(FileSystemInfo)new DirectoryInfo(d)))
    .OrderByDescending(f=>f.LastWriteTimeUtc).ThenByDescending(f=>f.Name,StringComparer.Ordinal).Skip(BackupLimit).ToList();
   foreach(var old in snapshots){SafeFiles.NoLinks(Root,old.FullName);if(old is DirectoryInfo directory)directory.Delete(true);else old.Delete();}
  }
 }
 void PreservePatchOriginals(){
  var backupRoot=Path.GetFullPath(Backup)+Path.DirectorySeparatorChar;
  foreach(var file in Directory.EnumerateFiles(Root,"patch-*.json")){
   var receipt=JsonSerializer.Deserialize<Receipt>(File.ReadAllText(file))!;bool changed=false;
   var originals=Path.Combine(Root,"patch-originals",Path.GetFileNameWithoutExtension(file));
   string Preserve(string source,string name){
    if(source.Length==0||!Path.GetFullPath(source).StartsWith(backupRoot,StringComparison.OrdinalIgnoreCase)||!File.Exists(source))return source;
    var target=Path.Combine(originals,name);SafeFiles.NoLinks(Backup,source);SafeFiles.NoLinks(Root,originals);SafeFiles.NoLinks(originals,target);Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(source,target,true);changed=true;return target;
   }
   receipt.OriginalIni=Preserve(receipt.OriginalIni,"AppList.ini");receipt.OriginalDll=Preserve(receipt.OriginalDll,"user32.dll");
   foreach(var (name,record) in receipt.NormalFiles)record.Original=Preserve(record.Original,Path.Combine("normal",name));
   if(changed){receipt.BackupDir=originals;SafeFiles.Atomic(file,JsonSerializer.SerializeToUtf8Bytes(receipt,Json));}
  }
 }
}
public partial class PatchService {
 string WithBackupRetention(Func<string> operation){lock(storage.BackupGate){try{return operation();}finally{storage.TrimBackups();}}}
 public string Install(string input,string mode,IEnumerable<GameItem> games)=>WithBackupRetention(()=>InstallCore(input,mode,games));
 public string SaveConfig(string input,IEnumerable<GameItem> games,bool clearPackageCache=false)=>WithBackupRetention(()=>SaveConfigCore(input,games,clearPackageCache));
 public string Uninstall(string input)=>WithBackupRetention(()=>UninstallCore(input));
 public string CleanPackageCache(string input)=>WithBackupRetention(()=>CleanPackageCacheCore(input));
}
