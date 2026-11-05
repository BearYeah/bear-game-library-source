using System.Diagnostics;
using System.Text.Json;
using System.Windows;
namespace NovaLibrary;
public static class DownloadProblemTest {
 public static int Run(string output,string proxy){
  Directory.CreateDirectory(output);int exit=1;var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
  app.Startup+=async(_,_)=>{try{await Exercise(output,proxy);exit=0;}catch(Exception ex){File.WriteAllText(Path.Combine(output,"problem-test.json"),JsonSerializer.Serialize(new{result="fail",error=ex.ToString()},Storage.Json));}finally{app.Shutdown();}};app.Run();return exit;
 }
 static async Task Exercise(string output,string proxy){
  var steam=Path.Combine(output,"Steam");Directory.CreateDirectory(Path.Combine(steam,"config"));File.WriteAllText(Path.Combine(steam,"steam.exe"),"Isolated test placeholder; never executed.");
  File.WriteAllText(Path.Combine(steam,"config","config.vdf"),"\"InstallConfigStore\" { \"Software\" { \"Valve\" { \"Steam\" { } } } }");
  var storage=new Storage(Path.Combine(output,"data"));var settings=new Settings{SteamPath=steam,DownloadLibrary=steam,DownloadLanguage="schinese",Proxy=proxy};var service=new GameDownloadService(storage);
  using var events=new StreamWriter(Path.Combine(output,"events.jsonl")){AutoFlush=true};
  var invalid=new DownloadWindow(storage,settings,new GameItem{AppId=223860,Name="App 223860"},()=>{},()=>{});invalid.DownloadEvent+=u=>events.WriteLine(JsonSerializer.Serialize(u));await invalid.BeginDownload();invalid.Close();
  var invalidStatus=service.Status(223860);if(invalidStatus.Event!="error"||!invalidStatus.Message.Contains("223860"))throw new Exception("Invalid AppID did not show the expected error.");service.Remove(223860,true);
  var window=new DownloadWindow(storage,settings,new GameItem{AppId=233860,Name="Kenshi"},()=>{},()=>{});var timer=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromSeconds(100)};bool paused=false,timedOut=false;double peak=0;var clock=Stopwatch.StartNew();double firstByteSeconds=0;
  window.DownloadEvent+=u=>{events.WriteLine(JsonSerializer.Serialize(new{time=clock.Elapsed.TotalSeconds,u.Event,u.Phase,u.Message,u.Completed,u.Total,u.Speed}));peak=Math.Max(peak,u.Speed);if(firstByteSeconds==0&&u.Completed>0)firstByteSeconds=clock.Elapsed.TotalSeconds;if(!paused&&u.Event=="progress"&&u.Completed>=8*1048576){paused=true;window.Pause();}};
  timer.Tick+=(_,_)=>{timedOut=true;timer.Stop();window.Pause();};timer.Start();await window.BeginDownload();timer.Stop();
  var saved=service.SavedProgress(233860);window.ExportPreview(output);File.Move(Path.Combine(output,"download.png"),Path.Combine(output,"Kenshi实际传输.png"),true);window.Close();
  if(timedOut||!paused||saved.Completed<=0)throw new Exception("Kenshi did not reach a live transfer pause point: "+saved.Message);
  var directory=service.RemovalInfo(233860).Directory;var neighbor=Path.Combine(steam,"steamapps","common","KeepNeighbor");Directory.CreateDirectory(neighbor);File.WriteAllText(Path.Combine(neighbor,"keep.txt"),"keep");
  bool removed=await Task.Run(()=>service.Remove(233860,true));if(!removed||Directory.Exists(directory)||Directory.Exists(Path.Combine(storage.Root,"downloads","233860"))||!File.Exists(Path.Combine(neighbor,"keep.txt")))throw new Exception("Removal did not remove only the selected partial game and task.");
  const uint retained=987654;var retainedTask=Path.Combine(storage.Root,"downloads",retained.ToString());Directory.CreateDirectory(retainedTask);var retainedGame=Path.Combine(steam,"steamapps","common","KeepFiles");Directory.CreateDirectory(retainedGame);File.WriteAllText(Path.Combine(retainedGame,"keep.bin"),"keep");
  File.WriteAllText(Path.Combine(retainedTask,"job.json"),JsonSerializer.Serialize(new{library=steam}));File.WriteAllText(Path.Combine(retainedTask,"status.json"),JsonSerializer.Serialize(new DownloadUpdate{Directory=retainedGame}));service.Remove(retained,false);if(!File.Exists(Path.Combine(retainedGame,"keep.bin")))throw new Exception("Keep files option removed game files.");
  Directory.CreateDirectory(retainedTask);File.WriteAllText(Path.Combine(retainedTask,"job.json"),JsonSerializer.Serialize(new{library=steam}));File.WriteAllText(Path.Combine(retainedTask,"status.json"),JsonSerializer.Serialize(new DownloadUpdate{Directory=retainedGame,Event="complete"}));File.WriteAllText(Path.Combine(steam,"steamapps",$"appmanifest_{retained}.acf"),"installed");service.Remove(retained,true);if(!File.Exists(Path.Combine(retainedGame,"keep.bin")))throw new Exception("Installed game was removed.");
  File.WriteAllText(Path.Combine(output,"problem-test.json"),JsonSerializer.Serialize(new{result="pass",invalidAppId=223860,invalidMessage=invalidStatus.Message,downloadedAppId=233860,downloadedBytes=saved.Completed,totalBytes=saved.Total,firstByteSeconds,peakBytesPerSecond=peak,partialGameDeleted=removed,taskDeleted=!Directory.Exists(Path.Combine(storage.Root,"downloads","233860")),neighborPreserved=true,keepFilesOptionPassed=true,installedGamePreserved=true,scope="Packaged WPF handlers and real Kenshi CDN data; bounded partial download, pause and removal in isolated test directory. No real Steam client or account modified."},Storage.Json));
 }
}
