using System.Text.Json;
namespace NovaLibrary;
public static class BackupRetentionTests {
 public static int Run(string output){
  Directory.CreateDirectory(output);var checks=new List<string>();void Check(bool ok,string label){if(!ok)throw new Exception(label);checks.Add(label);}
  try{
   var storage=new Storage(Path.Combine(output,"mixed"));var original=Path.Combine(storage.Backup,"install-20000101-000000-0000-abcdef");Directory.CreateDirectory(original);File.WriteAllText(Path.Combine(original,"AppList.ini"),"original INI");File.WriteAllText(Path.Combine(original,"user32.dll"),"original DLL");File.WriteAllText(Path.Combine(original,"x86launcher.exe"),"original launcher");
   var receiptFile=Path.Combine(storage.Root,"patch-fixture.json");File.WriteAllText(receiptFile,JsonSerializer.Serialize(new Receipt{Installed=true,OriginalIni=Path.Combine(original,"AppList.ini"),OriginalDll=Path.Combine(original,"user32.dll"),BackupDir=original,NormalFiles=new(){["bin/x86launcher.exe"]=new(){Original=Path.Combine(original,"x86launcher.exe")}}}));
   Directory.SetLastWriteTimeUtc(original,new DateTime(2000,1,1,0,0,0,DateTimeKind.Utc));
   for(int i=0;i<9;i++){var file=Path.Combine(storage.Backup,$"library-fixture-{i:00}.json");File.WriteAllText(file,i.ToString());File.SetLastWriteTimeUtc(file,new DateTime(2025,1,1,0,0,i,DateTimeKind.Utc));}
   var operation=Path.Combine(storage.Backup,"config-20260101-000000-0000-abcdef");Directory.CreateDirectory(operation);File.WriteAllText(Path.Combine(operation,"AppList.ini"),"recent snapshot");File.WriteAllText(Path.Combine(storage.Backup,"manual-note.txt"),"keep");storage.TrimBackups();
   Check(Directory.GetFiles(storage.Backup,"library-*.json").Length==4&&Directory.GetDirectories(storage.Backup).Length==1,"mixed file and operation backups retain five newest snapshots in total");
   Check(!Directory.Exists(original)&&File.Exists(Path.Combine(storage.Backup,"library-fixture-08.json"))&&!File.Exists(Path.Combine(storage.Backup,"library-fixture-04.json")),"old snapshots removed and newest snapshots retained");
   var receipt=JsonSerializer.Deserialize<Receipt>(File.ReadAllText(receiptFile))!;Check(File.ReadAllText(receipt.OriginalIni)=="original INI"&&File.ReadAllText(receipt.OriginalDll)=="original DLL"&&File.ReadAllText(receipt.NormalFiles["bin/x86launcher.exe"].Original)=="original launcher","original restore files migrated before their old snapshot is deleted");
   Check(File.ReadAllText(Path.Combine(storage.Backup,"manual-note.txt"))=="keep","unrelated files preserved");
   var extra=Path.Combine(storage.Backup,"library-startup.json");File.WriteAllText(extra,"new");_=new Storage(storage.Root);Check(Directory.GetFiles(storage.Backup,"library-*.json").Length+Directory.GetDirectories(storage.Backup).Length==5,"startup prunes existing excess snapshots");
   var runtime=new Storage(Path.Combine(output,"restore"));var steam=Path.Combine(output,"fake-steam");Directory.CreateDirectory(Path.Combine(steam,"AppList"));File.WriteAllText(Path.Combine(steam,"steam.exe"),"fixture only; never executed");var ini=Path.Combine(steam,"AppList","AppList.ini");File.WriteAllText(ini,"[Other]\nKeep=1\n[AppList]\n90 = 281990\n");var bytes=File.ReadAllBytes(ini);var games=new[]{new GameItem{AppId=281990,SourceId=90,Enabled=true}};var patch=new PatchService(runtime,()=>{},false);patch.Install(steam,"standard",games);
   for(int i=0;i<8;i++){runtime.Save(new LibraryState());patch.SaveConfig(steam,games);}
   Check(Directory.EnumerateFileSystemEntries(runtime.Backup).Count()==5,"repeated saves and patch operations stay within the five-snapshot limit");patch.Uninstall(steam);Check(File.ReadAllBytes(ini).SequenceEqual(bytes),"uninstall restores the original config after its rotating snapshot has expired");
   var config=Path.Combine(steam,"config");Directory.CreateDirectory(config);for(int i=0;i<8;i++){var file=Path.Combine(config,$"config.vdf.bear-download-fixture-{i}.bak");File.WriteAllText(file,i.ToString());File.SetLastWriteTimeUtc(file,new DateTime(2025,1,1,0,0,i,DateTimeKind.Utc));}File.WriteAllText(Path.Combine(config,"config.vdf.other.bak"),"keep");Storage.TrimDownloadConfigBackups(steam);
   Check(Directory.GetFiles(config,"config.vdf.bear-download-*.bak").Length==5&&File.Exists(Path.Combine(config,"config.vdf.bear-download-fixture-7.bak"))&&!File.Exists(Path.Combine(config,"config.vdf.bear-download-fixture-0.bak")),"existing download config backups retain five newest copies");Check(File.ReadAllText(Path.Combine(config,"config.vdf.other.bak"))=="keep","unrelated Steam config backups preserved");
   File.WriteAllText(Path.Combine(output,"backup-tests.json"),JsonSerializer.Serialize(new{result="pass",checks,scope="isolated fixtures only; no real Steam or user backups modified"},Storage.Json));return 0;
  }catch(Exception error){File.WriteAllText(Path.Combine(output,"backup-tests.json"),JsonSerializer.Serialize(new{result="fail",error=error.ToString(),checks},Storage.Json));return 1;}
 }
}
