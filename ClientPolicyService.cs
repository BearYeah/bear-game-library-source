using Microsoft.Win32;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Threading;
namespace NovaLibrary;
public sealed record ClientAccessDecision {
 public bool Allowed {get;init;}
 public string Code {get;init;}="";
 public string Message {get;init;}="";
 public string Mode {get;init;}="blacklist";
 public long Revision {get;init;}
 [JsonPropertyName("checked_at")] public double CheckedAt {get;init;}
 [JsonPropertyName("offline_until")] public double OfflineUntil {get;init;}
 public ClientUpdateInfo? Update {get;init;}
 [JsonIgnore] public bool Offline {get;init;}
}
public sealed record ClientAccessState(string Hwid,ClientAccessDecision Decision);
public interface IClientAccessStateStore {ClientAccessState? Read();void Save(ClientAccessState state);}
public sealed class RegistryClientAccessState : IClientAccessStateStore {
 const string Key=@"Software\BearLibrary\ClientAccess";
 public ClientAccessState? Read(){using var key=Registry.CurrentUser.OpenSubKey(Key);var text=key?.GetValue("AccessState") as string;if(text==null)return null;try{return JsonSerializer.Deserialize<ClientAccessState>(text,ClientPolicyService.Json);}catch(JsonException){return null;}}
 public void Save(ClientAccessState state){using var key=Registry.CurrentUser.CreateSubKey(Key);key.SetValue("AccessState",JsonSerializer.Serialize(state,ClientPolicyService.Json));if(state.Decision.Allowed)key.DeleteValue("CachedHandlerID",false);else key.SetValue("CachedHandlerID",state.Hwid);}
}
public static class ClientPolicyService {
 public static bool AccessControlEnabled=>Environment.GetEnvironmentVariable("BEAR_CLIENT_ACCESS_CONTROL")=="1";
 internal static readonly JsonSerializerOptions Json=new(){PropertyNameCaseInsensitive=true};
 public static string CurrentHwid {get;internal set;}="";
 internal static ClientUpdateInfo? StartupUpdate;
 public static async Task<ClientUpdateInfo?> GetUpdate(Storage storage,Settings settings,CancellationToken ct){
  if(StartupUpdate is { } cached){StartupUpdate=null;return cached;}
  return await Request<ClientUpdateInfo>(storage,settings,"update","",ct);
 }
 internal static async Task<T> Request<T>(Storage storage,Settings settings,string action,string hwid,CancellationToken ct){
  string folder=Path.Combine(storage.Root,"runtime","client-status");Directory.CreateDirectory(folder);string file=Path.Combine(folder,Guid.NewGuid().ToString("N")+".json");
  try{
   await File.WriteAllTextAsync(file,JsonSerializer.Serialize(new{action,cache=Path.Combine(storage.Root,"downloads","cache"),hwid,client_version=UpdateTrust.Current.ToString(3)}),ct);
   string worker=await Task.Run(()=>new GameDownloadService(storage).Worker(),ct);
   var start=new ProcessStartInfo(worker){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
   foreach(var arg in new[]{"--client-status","--status-file",file})start.ArgumentList.Add(arg);
   start.Environment["BEAR_DOWNLOAD_PROXY"]=settings.Proxy;start.Environment["PYTHONUTF8"]="1";
   using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(35));
   using var process=new Process{StartInfo=start};process.Start();
   using var registration=timeout.Token.Register(()=>{try{process.Kill(entireProcessTree:true);}catch(InvalidOperationException){}});
   var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
   try{await process.WaitForExitAsync(timeout.Token);}catch(OperationCanceledException){await process.WaitForExitAsync();ct.ThrowIfCancellationRequested();throw new IOException("暂时无法连接验证服务，请稍后重试。");}
   await stdout;await stderr;ct.ThrowIfCancellationRequested();
   if(process.ExitCode!=0)throw new IOException("暂时无法连接验证服务，请检查网络后重试。");
   return JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(file,ct),Json)??throw new IOException("验证服务没有返回有效结果。");
  }finally{File.Delete(file);File.Delete(Path.ChangeExtension(file,".reply"));}
 }
}
public sealed class ClientAccessSession {
 readonly string hwid;readonly IClientAccessStateStore cache;readonly Func<CancellationToken,Task<ClientAccessDecision>> fetch;readonly Func<double> clock;
 DispatcherTimer? timer;bool checking;
 public ClientAccessDecision? LastDecision {get;private set;}
 public string Hwid=>hwid;
 public ClientAccessSession(Storage storage,Func<Settings> settings):this(HardwareFingerprint.Current(),new RegistryClientAccessState(),ct=>ClientPolicyService.Request<ClientAccessDecision>(storage,settings(),"access",ClientPolicyService.CurrentHwid,ct),()=>DateTimeOffset.UtcNow.ToUnixTimeSeconds()) {ClientPolicyService.CurrentHwid=hwid;}
 internal ClientAccessSession(string hwid,IClientAccessStateStore cache,Func<CancellationToken,Task<ClientAccessDecision>> fetch,Func<double> clock){this.hwid=hwid;this.cache=cache;this.fetch=fetch;this.clock=clock;}
 public async Task<ClientAccessDecision> Refresh(CancellationToken ct){
  try{var response=await fetch(ct);cache.Save(new(hwid,response));return LastDecision=response;}
  catch(Exception error) when(error is IOException or JsonException){
   var saved=cache.Read();
   if(saved?.Hwid==hwid){
    if(!saved.Decision.Allowed)return LastDecision=saved.Decision with {Offline=true};
    if(saved.Decision.OfflineUntil>clock())return LastDecision=saved.Decision with {Offline=true,Message="已使用最近一次验证结果，离线许可有效。"};
   }
   return LastDecision=new(){Allowed=false,Code="connection_required",Message="暂时无法连接验证服务。首次使用或离线许可到期后，需要联网重新检查。"};
  }
 }
 public void Attach(MainWindow window){
  timer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(60)};
  var lifetime=new CancellationTokenSource();window.Closed+=(_,_)=>{timer.Stop();lifetime.Cancel();lifetime.Dispose();};
  timer.Tick+=async(_,_)=>{
   if(checking)return;checking=true;
   try{
    var decision=await Refresh(lifetime.Token);if(decision.Allowed)return;
    timer.Stop();await window.SuspendForClientPolicy();
    var gate=new ClientAccessWindow(this){Owner=window};
    if(gate.ShowDialog()==true){window.IsEnabled=true;window.Show();window.Activate();timer.Start();}
    else window.Close();
   }catch(OperationCanceledException){}finally{checking=false;}
  };
  timer.Start();
 }
}
