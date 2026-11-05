using System.Diagnostics;
using System.Text.Json;
namespace NovaLibrary;
public sealed class ApplyUpdateJob {
 public string Id {get;set;}="";public string DataRoot {get;set;}="";public string Target {get;set;}="";public string Staged {get;set;}="";
 public int ParentId {get;set;}public long ParentStartedUtcTicks {get;set;}public string OriginalHash {get;set;}="";public SignedRelease Envelope {get;set;}=new();
}
public static class UpdateInstaller {
 static bool Same(string a,string b)=>Path.GetFullPath(a).Equals(Path.GetFullPath(b),StringComparison.OrdinalIgnoreCase);
 static void Id(string id){if(id.Length!=32||!id.All(Uri.IsHexDigit))throw new InvalidDataException("更新任务标识无效。");}
 static string Folder(string data){var p=Path.Combine(Path.GetFullPath(data),"updates");SafeFiles.NoLinks(data,p);return p;}
 static string Marker(ApplyUpdateJob job,string suffix)=>Path.Combine(Folder(job.DataRoot),job.Id+suffix);
 static void Status(ApplyUpdateJob job,string text){var file=Path.Combine(Folder(job.DataRoot),"last-update.json");SafeFiles.NoLinks(job.DataRoot,file);SafeFiles.Atomic(file,JsonSerializer.SerializeToUtf8Bytes(new{job.Id,message=text,utc=DateTime.UtcNow},Storage.Json));}
 public static void Cleanup(Storage storage){
  var folder=Folder(storage.Root);if(!Directory.Exists(folder))return;
  foreach(var file in Directory.GetFiles(folder,"apply-*.json"))try{
   SafeFiles.NoLinks(storage.Root,file);if(File.GetCreationTimeUtc(file)>DateTime.UtcNow.AddDays(-1)||new FileInfo(file).Length>256*1024)continue;
   var job=JsonSerializer.Deserialize<ApplyUpdateJob>(File.ReadAllBytes(file));if(job==null)continue;Id(job.Id);
   if(!Same(job.DataRoot,storage.Root)||!Same(file,Path.Combine(folder,"apply-"+job.Id+".json"))||!Same(job.Staged,Path.Combine(folder,"download-"+job.Id+".exe")))continue;
   _ = UpdateTrust.Verify(job.Envelope);var helper=Path.Combine(folder,"updater-"+job.Id+".exe");SafeFiles.NoLinks(storage.Root,helper);
   if(File.Exists(helper)){if(FileVersionInfo.GetVersionInfo(helper).ProductName!="Bear游戏库")continue;File.Delete(helper);}
   SafeFiles.NoLinks(storage.Root,job.Staged);if(File.Exists(job.Staged)){UpdateTrust.VerifyFile(job.Staged,UpdateTrust.Verify(job.Envelope));File.Delete(job.Staged);}
   foreach(var suffix in new[]{".ready",".started",".cancel"}){var marker=Marker(job,suffix);SafeFiles.NoLinks(storage.Root,marker);if(File.Exists(marker))File.Delete(marker);}File.Delete(file);
  }catch{ /* Retry later if an owned helper is still running or a record is not recognized. */ }
 }
 public static async Task Launch(Storage storage,PendingUpdate update){
  Id(update.Id);var release=UpdateTrust.Verify(update.Envelope);UpdateTrust.VerifyFile(update.File,release);
  var target=Environment.ProcessPath??throw new IOException("无法确定当前程序路径。");var folder=Folder(storage.Root);Directory.CreateDirectory(folder);SafeFiles.NoLinks(storage.Root,update.File);
  using var parent=Process.GetCurrentProcess();
  var job=new ApplyUpdateJob{Id=update.Id,DataRoot=storage.Root,Target=Path.GetFullPath(target),Staged=Path.GetFullPath(update.File),ParentId=parent.Id,ParentStartedUtcTicks=parent.StartTime.ToUniversalTime().Ticks,OriginalHash=SafeFiles.HashFile(target),Envelope=update.Envelope};
  var helper=Path.Combine(folder,"updater-"+job.Id+".exe");var jobFile=Path.Combine(folder,"apply-"+job.Id+".json");SafeFiles.NoLinks(storage.Root,helper);SafeFiles.NoLinks(storage.Root,jobFile);
  await Task.Run(()=>File.Copy(target,helper,false));SafeFiles.Atomic(jobFile,JsonSerializer.SerializeToUtf8Bytes(job,Storage.Json));
  var start=new ProcessStartInfo(helper){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=folder};start.ArgumentList.Add("--apply-update");start.ArgumentList.Add(jobFile);
  using var process=Process.Start(start)??throw new IOException("无法启动更新助手。");var timeout=DateTime.UtcNow.AddSeconds(20);
  while(!File.Exists(Marker(job,".ready"))){if(process.HasExited||DateTime.UtcNow>=timeout){File.WriteAllText(Marker(job,".cancel"),"cancel");throw new IOException("更新助手没有就绪，本次保留原程序。请稍后重试。");}await Task.Delay(150);}
 }
 public static bool MarkStarted(Storage storage,string id){Id(id);var folder=Folder(storage.Root);var job=Path.Combine(folder,"apply-"+id+".json");if(!File.Exists(job))return false;var marker=Path.Combine(folder,id+".started");SafeFiles.NoLinks(storage.Root,marker);SafeFiles.Atomic(marker,[1]);return true;}
 public static int Run(string jobFile){
  ApplyUpdateJob? job=null;string? temp=null;bool replaced=false,trustedJob=false;string backup="";ReleaseManifest? expectedRelease=null;
  try{
   job=JsonSerializer.Deserialize<ApplyUpdateJob>(File.ReadAllBytes(jobFile))??throw new InvalidDataException("更新任务为空。");Id(job.Id);
   var folder=Folder(job.DataRoot);if(!Same(jobFile,Path.Combine(folder,"apply-"+job.Id+".json"))||!Same(job.Staged,Path.Combine(folder,"download-"+job.Id+".exe"))||!Same(Environment.ProcessPath??"",Path.Combine(folder,"updater-"+job.Id+".exe")))throw new InvalidDataException("更新助手路径校验未通过。");
   SafeFiles.NoLinks(job.DataRoot,jobFile);SafeFiles.NoLinks(job.DataRoot,job.Staged);var release=UpdateTrust.Verify(job.Envelope);expectedRelease=release;
   if(UpdateTrust.Normalize(release.Version)<=UpdateTrust.Current)throw new InvalidDataException("禁止安装当前版本或降级。");UpdateTrust.VerifyFile(job.Staged,release);
   var targetFolder=Path.GetDirectoryName(Path.GetFullPath(job.Target))!;SafeFiles.NoLinks(targetFolder,job.Target);
   if(!SafeFiles.HashFile(job.Target).Equals(job.OriginalHash,StringComparison.OrdinalIgnoreCase)||!SafeFiles.HashFile(Environment.ProcessPath!).Equals(job.OriginalHash,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("当前程序或更新助手身份不匹配。");
   using(var parent=Process.GetProcessById(job.ParentId)){
    if(parent.StartTime.ToUniversalTime().Ticks!=job.ParentStartedUtcTicks||!Same(parent.MainModule?.FileName??"",job.Target))throw new InvalidDataException("启动更新任务的进程身份不匹配。");
    trustedJob=true;SafeFiles.Atomic(Marker(job,".ready"),[1]);var deadline=DateTime.UtcNow.AddMinutes(2);
    while(!parent.HasExited){if(File.Exists(Marker(job,".cancel"))||DateTime.UtcNow>=deadline)throw new IOException("等待程序退出超时，未替换文件。");Thread.Sleep(150);}
   }
   UpdateTrust.VerifyFile(job.Staged,release);if(SafeFiles.HashFile(job.Target)!=job.OriginalHash)throw new IOException("程序文件在等待期间发生变化，更新已停止。");
   temp=Path.Combine(targetFolder,".bear-update-"+job.Id+".exe");backup=job.Target+".previous";SafeFiles.NoLinks(targetFolder,temp);SafeFiles.NoLinks(targetFolder,backup);
   if(File.Exists(backup)&&FileVersionInfo.GetVersionInfo(backup).ProductName!="Bear游戏库")throw new IOException("回退文件名已被其他文件使用，更新已停止。");
   File.Copy(job.Staged,temp,false);UpdateTrust.VerifyFile(temp,release);var replaceUntil=DateTime.UtcNow.AddSeconds(12);
   while(true){try{File.Replace(temp,job.Target,backup,true);break;}catch(Exception locked) when(locked is IOException or UnauthorizedAccessException){if(DateTime.UtcNow>=replaceUntil)throw;Thread.Sleep(200);}}
   temp=null;replaced=true;
   var start=new ProcessStartInfo(job.Target){UseShellExecute=false,WorkingDirectory=targetFolder};start.ArgumentList.Add("--data-dir");start.ArgumentList.Add(job.DataRoot);start.ArgumentList.Add("--update-complete");start.ArgumentList.Add(job.Id);
   using var child=Process.Start(start)??throw new IOException("新版程序无法启动。");var until=DateTime.UtcNow.AddSeconds(25);
   while(!File.Exists(Marker(job,".started"))){if(child.HasExited)throw new IOException("新版程序提前退出，恢复原版本。");if(DateTime.UtcNow>=until){Status(job,"新版尚未确认启动，已保留回退副本；没有强制结束进程。");return 1;}Thread.Sleep(150);}
   Status(job,"已更新至 "+release.Version+"，游戏库和设置已保留。");File.Delete(job.Staged);return 0;
  }catch(Exception e){
   if(job!=null&&trustedJob){
    if(!replaced&&expectedRelease!=null&&File.Exists(backup)&&SafeFiles.HashFile(backup)==job.OriginalHash&&(!File.Exists(job.Target)||SafeFiles.HashFile(job.Target)==expectedRelease.Sha256))replaced=true;
    if(replaced&&File.Exists(backup)){try{
     if(SafeFiles.HashFile(backup)!=job.OriginalHash)throw new IOException("回退副本的校验值发生变化。");
     var rollback=job.Target+".rollback-"+job.Id;var root=Path.GetDirectoryName(job.Target)!;SafeFiles.NoLinks(root,rollback);var until=DateTime.UtcNow.AddSeconds(12);
     try{while(true){try{File.Copy(backup,rollback,true);if(File.Exists(job.Target))File.Replace(rollback,job.Target,null,true);else File.Move(rollback,job.Target);break;}catch(Exception locked) when(locked is IOException or UnauthorizedAccessException){if(DateTime.UtcNow>=until)throw;Thread.Sleep(200);}}}
     finally{if(File.Exists(rollback))try{File.Delete(rollback);}catch{}}
     var start=new ProcessStartInfo(job.Target){UseShellExecute=false,WorkingDirectory=root};start.ArgumentList.Add("--data-dir");start.ArgumentList.Add(job.DataRoot);using var restored=Process.Start(start);Status(job,"更新未完成，已恢复原版本："+e.Message);
    }catch(Exception rollbackFailure){try{Status(job,"更新未完成，回退副本仍保留："+e.Message+"；回退原因："+rollbackFailure.Message);}catch{}}}
    else try{Status(job,"更新未完成，原程序仍保留："+e.Message);}catch{}
   }return 1;
  }finally{if(temp!=null)try{File.Delete(temp);}catch{}}
 }
}
