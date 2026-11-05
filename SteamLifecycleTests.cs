using System.Text;
using System.Text.Json;
namespace NovaLibrary;
public static class SteamLifecycleTests {
 static void Assert(bool ok,string message){if(!ok)throw new Exception(message);}
 static async Task Reject(Func<Task> action){try{await action();}catch(InvalidOperationException){return;}catch(IOException){return;}throw new Exception("expected rejection");}
 public static async Task<int> Run(string output){
  Directory.CreateDirectory(output);var root=Path.Combine(Path.GetFullPath(output),"lifecycle-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
  var steam=Path.Combine(root,"fake steam 含空格");Directory.CreateDirectory(steam);File.WriteAllText(Path.Combine(steam,"steam.exe"),"fixture, never executed");
  Directory.CreateDirectory(Path.Combine(steam,"AppList"));Directory.CreateDirectory(Path.Combine(steam,"appcache"));Directory.CreateDirectory(Path.Combine(steam,"steamapps","common"));
  var cache=Path.Combine(steam,"appcache","packageinfo.vdf");var ini=Path.Combine(steam,"AppList","AppList.ini");var dll=Path.Combine(steam,"user32.dll");
  var sentinel=Path.Combine(steam,"steamapps","common","keep.txt");File.WriteAllText(sentinel,"keep game files");
  var storage=new Storage(Path.Combine(root,"data"));var game=new GameItem{AppId=281990,SourceId=90,Enabled=true};var games=new[]{game};
  var reports=new List<object>();int failed=0;
  async Task Test(string name,Func<Task> body){try{await body();reports.Add(new{name,result="pass"});}catch(Exception e){failed++;reports.Add(new{name,result="fail",error=e.Message});}}
  SteamLifecycle Flow(FakeSteamRuntime runtime)=>new(runtime,TimeSpan.FromMilliseconds(40),TimeSpan.FromMilliseconds(1));
  PatchService Patch(FakeSteamRuntime runtime)=>new(storage,()=>{if(runtime.Running)throw new InvalidOperationException("Steam still running");},false);
  await Test("preflight rejects before shutdown or file changes",async()=>{
   var rt=new FakeSteamRuntime();var changed=false;
   await Reject(()=>Flow(rt).Run(steam,()=>throw new InvalidOperationException("invalid mapping"),()=>{changed=true;return Task.FromResult("unexpected");}));
   Assert(!changed&&rt.Events.Count==0,"invalid input closed Steam or ran change");
  });
  await Test("unverified client is rejected before requesting Steam exit",async()=>{
   var rt=new FakeSteamRuntime();var patch=new PatchService(storage,()=>{},true);var changed=false;
   await Reject(()=>Flow(rt).Run(steam,()=>patch.PreflightInstall(steam,"standard",games),()=>{changed=true;return Task.FromResult("unexpected");}));
   Assert(!changed&&rt.Events.Count==0,"unknown client triggered shutdown");
  });
  await Test("shutdown timeout aborts without applying any mutation",async()=>{
   var rt=new FakeSteamRuntime{ShutdownCompletes=false};bool changed=false;
   await Reject(()=>Flow(rt).Run(steam,()=>{},()=>{changed=true;return Task.FromResult("unexpected");}));
   Assert(!changed&&rt.Running&&rt.Events.SequenceEqual(new[]{"shutdown"}),"timeout altered state or attempted forced/start operation");
  });
  await Test("unknown DLL is rejected before shutdown",async()=>{
   File.WriteAllText(dll,"foreign DLL");var rt=new FakeSteamRuntime();var patch=Patch(rt);
   await Reject(()=>Flow(rt).Run(steam,()=>patch.PreflightInstall(steam,"standard",games),()=>Task.FromResult(patch.Install(steam,"standard",games))));
   Assert(rt.Events.Count==0&&File.ReadAllText(dll)=="foreign DLL","foreign file modified or Steam closed");File.Delete(dll);
  });
  await Test("install and restart waits for closed Steam then installs verified payload",async()=>{
   var rt=new FakeSteamRuntime();var patch=Patch(rt);File.WriteAllText(cache,"install cache");
   var result=await Flow(rt).Run(steam,()=>patch.PreflightInstall(steam,"standard",games),()=>{Assert(!rt.Running,"changed before exit");rt.Events.Add("install");return Task.FromResult(patch.Install(steam,"standard",games));});
   Assert(result.Started&&rt.Events.SequenceEqual(new[]{"shutdown","install","start"}),"wrong operation order");
   Assert(SafeFiles.HashFile(dll)==PatchService.StandardHash&&!File.Exists(cache)&&IniConfig.Read(File.ReadAllBytes(ini)).Single().AppId==281990,"install did not apply valid bytes");
  });
  await Test("save and restart writes mapping and backs up stale package cache",async()=>{
   var rt=new FakeSteamRuntime();var patch=Patch(rt);File.WriteAllText(cache,"stale mapping cache");var previous=File.ReadAllBytes(ini);game.AppId=1245620;
   var result=await Flow(rt).Run(steam,()=>patch.PreflightConfig(steam,games,true),()=>Task.FromResult(patch.SaveConfig(steam,games,true)));
   Assert(result.Started&&rt.Events.SequenceEqual(new[]{"shutdown","start"}),"did not restart");Assert(IniConfig.Read(File.ReadAllBytes(ini)).Single().AppId==1245620&&!File.Exists(cache),"mapping/cache not refreshed");
   Assert(Directory.GetFiles(storage.Backup,"packageinfo.vdf",SearchOption.AllDirectories).Any(f=>File.ReadAllText(f)=="stale mapping cache"),"cache backup missing");
   Assert(Directory.GetFiles(storage.Backup,"AppList.ini",SearchOption.AllDirectories).Any(f=>File.ReadAllBytes(f).SequenceEqual(previous)),"configuration backup missing");
  });
  await Test("locked cache rolls back save then restores previous Steam session",async()=>{
   File.WriteAllText(cache,"locked cache bytes");var previous=File.ReadAllBytes(ini);game.AppId=1091500;var rt=new FakeSteamRuntime();var patch=Patch(rt);
   using(var locked=new FileStream(cache,FileMode.Open,FileAccess.Read,FileShare.Read))
    await Reject(()=>Flow(rt).Run(steam,()=>patch.PreflightConfig(steam,games,true),()=>Task.FromResult(patch.SaveConfig(steam,games,true))));
   Assert(File.ReadAllBytes(ini).SequenceEqual(previous)&&File.ReadAllText(cache)=="locked cache bytes","failed save not rolled back");
   Assert(rt.Running&&rt.Events.SequenceEqual(new[]{"shutdown","start"}),"old Steam session was not restored after failure");
  });
  await Test("Steam launch failure reports committed changes without claiming rollback",async()=>{
   var rt=new FakeSteamRuntime{StartFails=true};var patch=Patch(rt);game.AppId=105600;
   var result=await Flow(rt).Run(steam,()=>patch.PreflightConfig(steam,games,true),()=>Task.FromResult(patch.SaveConfig(steam,games,true)));
   Assert(!result.Started&&result.RestartMessage.Contains("更改已保存")&&IniConfig.Read(File.ReadAllBytes(ini)).Single().AppId==105600,"committed save mislabeled as failed");
  });
  await Test("closed Steam skips shutdown and launches after saving",async()=>{
   var rt=new FakeSteamRuntime{Running=false};var patch=Patch(rt);var result=await Flow(rt).Run(steam,()=>patch.PreflightConfig(steam,games,true),()=>Task.FromResult(patch.SaveConfig(steam,games,true)));
   Assert(result.Started&&rt.Events.SequenceEqual(new[]{"start"}),"closed client got unnecessary shutdown request");
  });
  await Test("uninstall cleans patch and public caches then starts Steam while keeping library and games",async()=>{
   var rt=new FakeSteamRuntime();var patch=Patch(rt);File.WriteAllText(cache,"uninstall cache");File.WriteAllText(Path.Combine(storage.Covers,"cover.jpg"),"fixture");File.WriteAllText(Path.Combine(storage.Info,"info.json"),"{}");
   var state=new LibraryState{Games=games.ToList()};storage.Save(state);var library=File.ReadAllBytes(Path.Combine(storage.Root,"library.json"));
   var result=await Flow(rt).Run(steam,()=>patch.PreflightUninstall(steam),()=>{Assert(!rt.Running,"uninstalled before exit");return Task.FromResult(patch.Uninstall(steam));});
   Assert(result.Started&&!File.Exists(dll)&&!File.Exists(cache)&&storage.CacheBytes()==0&&Directory.GetFiles(storage.Info).Length==0,"uninstall/cache cleanup failed");
   Assert(File.ReadAllBytes(Path.Combine(storage.Root,"library.json")).SequenceEqual(library)&&File.ReadAllText(sentinel)=="keep game files","uninstall changed library/game data");
  });
  await Test("shutdown command uses exact selected executable and a single shutdown argument",()=>{
   var quit=SystemSteamRuntime.Command(steam,true);var start=SystemSteamRuntime.Command(steam,false);
   Assert(quit.FileName==Path.Combine(steam,"steam.exe")&&quit.ArgumentList.SequenceEqual(new[]{"-shutdown"})&&quit.CreateNoWindow&&quit.WorkingDirectory==steam,"unsafe shutdown command");
   Assert(start.ArgumentList.Count==0&&!start.CreateNoWindow&&start.WorkingDirectory==steam,"invalid start command");return Task.CompletedTask;
  });
  await Test("restart config refuses an active Steam before touching files",()=>{
   var rt=new FakeSteamRuntime();var patch=Patch(rt);var before=File.Exists(ini)?File.ReadAllBytes(ini):null;bool rejected=false;
   try{patch.SaveConfig(steam,games,true);}catch(InvalidOperationException){rejected=true;}
   Assert(rejected&&(before==null?!File.Exists(ini):File.ReadAllBytes(ini).SequenceEqual(before)),"process guard ignored");return Task.CompletedTask;
  });
  File.WriteAllText(Path.Combine(output,"lifecycle-test.json"),JsonSerializer.Serialize(new{result=failed==0?"pass":"fail",scope="fake Steam runtime and isolated filesystem fixtures only; no real Steam shutdown/start and no live files modified",passed=reports.Count-failed,failed,checks=reports},Storage.Json));
  return failed==0?0:1;
 }
 sealed class FakeSteamRuntime : ISteamRuntime {
  public bool Running=true,ShutdownCompletes=true,StartFails;public List<string> Events=[];
  public bool IsRunning(string steam)=>Running;
  public void RequestShutdown(string steam){Events.Add("shutdown");if(ShutdownCompletes)Running=false;}
  public void Start(string steam){Events.Add("start");if(StartFails)throw new IOException("fixture start failure");Running=true;}
 }
}
