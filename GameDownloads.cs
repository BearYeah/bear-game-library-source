using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
namespace NovaLibrary;
public sealed class DownloadUpdate {
 public string Event {get;set;}="";public string Message {get;set;}="";public string Directory {get;set;}="";
 public string Phase {get;set;}="";
 public double Completed {get;set;}public double Total {get;set;}public double Speed {get;set;}public bool Committing {get;set;}public bool Paused {get;set;}public int Depots {get;set;}
 public int Connections {get;set;}
}
public static class GameDownloadPaths {
 public static string CommonDirectory(string input){
  var full=Path.GetFullPath(input).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
  if(Path.GetFileName(full).Equals("common",StringComparison.OrdinalIgnoreCase)&&string.Equals(Path.GetFileName(Path.GetDirectoryName(full)),"steamapps",StringComparison.OrdinalIgnoreCase))return full;
  return Path.GetFileName(full).Equals("steamapps",StringComparison.OrdinalIgnoreCase)?Path.Combine(full,"common"):Path.Combine(full,"steamapps","common");
 }
 public static string LibraryRoot(string input)=>Path.GetDirectoryName(Path.GetDirectoryName(CommonDirectory(input)))!;
}
public sealed record GameDownloadRequest(uint AppId,string Name,string Steam,string Library,string Language,int Jobs,string Proxy,bool LocalSni=true,bool DownloadUseProxy=false);
public sealed class GameDownloadService(Storage storage) {
 static readonly JsonSerializerOptions EventJson=new(){PropertyNameCaseInsensitive=true};
 string Folder(uint appid)=>Path.Combine(storage.Root,"downloads",appid.ToString());
 public DownloadUpdate Status(uint appid){var file=Path.Combine(Folder(appid),"status.json");return File.Exists(file)?JsonSerializer.Deserialize<DownloadUpdate>(File.ReadAllText(file),EventJson)!:new DownloadUpdate();}
 public DownloadUpdate SavedProgress(uint appid){
  var folder=Folder(appid);var saved=Status(appid);
  var state=Path.Combine(folder,"chunks.json");if(File.Exists(state)){using var json=JsonDocument.Parse(File.ReadAllText(state));if(json.RootElement.TryGetProperty("total",out var total))saved.Total=total.GetDouble();}
  var journal=Path.Combine(folder,"chunks.jsonl");if(File.Exists(journal)){var chunks=new Dictionary<string,long>();using var journalStream=new FileStream(journal,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);using var journalReader=new StreamReader(journalStream);while(journalReader.ReadLine() is { } line){try{using var item=JsonDocument.Parse(line);chunks[item.RootElement.GetProperty("key").GetString()!]=item.RootElement.GetProperty("bytes").GetInt64();}catch(JsonException){break;}}saved.Completed=chunks.Values.Sum();}
  return saved;
 }
 public (string Directory,bool Installed) RemovalInfo(uint appid){
  var folder=Folder(appid);var job=Path.Combine(folder,"job.json");if(!File.Exists(job))return ("",false);
  using var document=JsonDocument.Parse(File.ReadAllText(job));var library=document.RootElement.GetProperty("library").GetString()!;var status=Status(appid);var destination=status.Directory;
  var plan=Path.Combine(folder,"plan.json");if(File.Exists(plan)){using var saved=JsonDocument.Parse(File.ReadAllText(plan));library=saved.RootElement.GetProperty("library").GetString()!;destination=saved.RootElement.GetProperty("destination").GetString()!;}
  if(destination.Length==0)return ("",false);
  var common=GameDownloadPaths.CommonDirectory(library);destination=Path.GetFullPath(destination);
  if(!destination.StartsWith(common+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("任务目录不在记录的 steamapps/common 中，无法删除游戏文件。");
  return (destination,File.Exists(Path.Combine(library,"steamapps",$"appmanifest_{appid}.acf"))||status.Event=="complete");
 }
 public bool Remove(uint appid,bool deleteFiles){
  var info=RemovalInfo(appid);bool removed=false;
  if(deleteFiles&&!info.Installed&&info.Directory.Length>0&&System.IO.Directory.Exists(info.Directory)){System.IO.Directory.Delete(info.Directory,true);removed=true;}
  var folder=Path.GetFullPath(Folder(appid));var downloads=Path.GetFullPath(Path.Combine(storage.Root,"downloads"))+Path.DirectorySeparatorChar;
  if(!folder.StartsWith(downloads,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("下载记录目录无效。");
  if(System.IO.Directory.Exists(folder))System.IO.Directory.Delete(folder,true);return removed;
 }
 public static ProcessStartInfo Command(string worker,string job,string proxy){
  var command=new ProcessStartInfo(worker){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
  command.ArgumentList.Add("--job");command.ArgumentList.Add(job);command.Environment["BEAR_DOWNLOAD_PROXY"]=proxy;command.Environment["PYTHONUTF8"]="1";return command;
 }
 string Worker(){
  var directory=Path.Combine(storage.Root,"runtime","downloads",UpdateTrust.Current.ToString(3));System.IO.Directory.CreateDirectory(directory);var file=Path.Combine(directory,"BearDownloader.exe");
  if(!File.Exists(file)){using var resource=Assembly.GetExecutingAssembly().GetManifestResourceStream("NovaLibrary.DownloadAssets.BearDownloader.exe")!;using var output=File.Create(file);resource.CopyTo(output);}return file;
 }
 public async Task<string> Run(GameDownloadRequest request,IProgress<DownloadUpdate> progress,CancellationToken ct){
  var folder=Folder(request.AppId);System.IO.Directory.CreateDirectory(folder);var job=Path.Combine(folder,"job.json");var status=Path.Combine(folder,"status.json");var saved=SavedProgress(request.AppId);
  await new SteamNetworkRoutes(storage,new Settings{Proxy=request.Proxy}).Refresh(ct);
  await File.WriteAllTextAsync(job,JsonSerializer.Serialize(new{appid=request.AppId,name=request.Name,steam=request.Steam,library=request.Library,language=request.Language,jobs=request.Jobs,download_proxy=request.DownloadUseProxy?"inherit":"direct",resume=true,cache=Path.Combine(storage.Root,"downloads","cache"),state=Path.Combine(folder,"chunks.json"),network_routes=SteamNetworkRoutes.CacheFile(storage),local_sni=request.LocalSni}),ct);
  var worker=await Task.Run(Worker,ct);ct.ThrowIfCancellationRequested();using var process=new Process{StartInfo=Command(worker,job,request.Proxy)};process.Start();
  using var cancellation=ct.Register(()=>{try{process.Kill(entireProcessTree:true);}catch(InvalidOperationException){}});
  var errorOutput=process.StandardError.ReadToEndAsync();string directory="",failure="";bool complete=false;
  try{
   while(await process.StandardOutput.ReadLineAsync(ct) is { } line){
    var update=JsonSerializer.Deserialize<DownloadUpdate>(line,EventJson)!;
    if(update.Event=="error")failure=update.Message;if(update.Directory.Length>0){directory=update.Directory;saved.Directory=directory;}
    if(update.Message.Length>0)saved.Message=update.Message;if(update.Phase.Length>0)saved.Phase=update.Phase;
    if(update.Event=="progress"){saved.Completed=update.Completed;saved.Total=update.Total;saved.Speed=update.Speed;saved.Connections=update.Connections;}
    if(update.Event=="complete")complete=true;saved.Event=update.Event;saved.Paused=false;
    File.WriteAllText(status,JsonSerializer.Serialize(saved));progress.Report(update);
   }
   await process.WaitForExitAsync(ct);var stderr=await errorOutput;ct.ThrowIfCancellationRequested();
   if(process.ExitCode!=0||!complete)throw new InvalidOperationException(failure.Length>0?failure:stderr.Length>0?stderr:"下载未完成，可继续已记录的进度。");return directory;
  }catch(OperationCanceledException){await process.WaitForExitAsync();saved=SavedProgress(request.AppId);saved.Paused=true;saved.Speed=0;saved.Message="下载已暂停，进度已保存。";File.WriteAllText(status,JsonSerializer.Serialize(saved));throw;}
 }
}
