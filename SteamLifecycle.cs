using System.Diagnostics;
namespace NovaLibrary;
public interface ISteamRuntime {
 bool IsRunning(string steam);
 void RequestShutdown(string steam);
 void Start(string steam);
}
public sealed class SystemSteamRuntime : ISteamRuntime {
 public bool IsRunning(string steam){
  bool running=false;
  foreach(var name in new[]{"steam","steamwebhelper"}){
   var processes=Process.GetProcessesByName(name);
   try{foreach(var process in processes){
    if(process.HasExited)continue;
    string? executable;
    try{executable=process.MainModule?.FileName;}
    catch(Exception e) when(e is System.ComponentModel.Win32Exception or InvalidOperationException){
     if(process.HasExited)continue;
     throw new InvalidOperationException("无法确认正在运行的 Steam 路径，请先手动退出 Steam 后重试。",e);
    }
    if(string.IsNullOrEmpty(executable)||!Path.GetFullPath(executable).StartsWith(steam+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
     throw new InvalidOperationException("正在运行的 Steam 与所选目录不一致，请检查 Steam 目录后重试。");
    running=true;
   }}finally{foreach(var process in processes)process.Dispose();}
  }
  return running;
 }
 public static ProcessStartInfo Command(string input,bool shutdown){
  var steam=PatchService.ValidateSteam(input);
  var info=new ProcessStartInfo(Path.Combine(steam,"steam.exe")){WorkingDirectory=steam,UseShellExecute=false,CreateNoWindow=shutdown,WindowStyle=shutdown?ProcessWindowStyle.Hidden:ProcessWindowStyle.Normal};
  if(shutdown)info.ArgumentList.Add("-shutdown");
  return info;
 }
 public void RequestShutdown(string steam){using var process=Process.Start(Command(steam,true))??throw new IOException("无法发送 Steam 退出请求。");}
 public void Start(string steam){using var process=Process.Start(Command(steam,false))??throw new IOException("无法启动 Steam。");}
}
public sealed record SteamRestartResult(string OperationMessage,bool Started,string RestartMessage);
public sealed class SteamLifecycle {
 readonly ISteamRuntime runtime;readonly TimeSpan timeout,poll;
 public SteamLifecycle(ISteamRuntime? runtime=null,TimeSpan? timeout=null,TimeSpan? poll=null){this.runtime=runtime??new SystemSteamRuntime();this.timeout=timeout??TimeSpan.FromSeconds(60);this.poll=poll??TimeSpan.FromMilliseconds(300);}
 public async Task<SteamRestartResult> Run(string input,Action validate,Func<Task<string>> change,Action<string>? progress=null){
  var steam=PatchService.ValidateSteam(input);validate();
  bool wasRunning=runtime.IsRunning(steam);
  if(wasRunning){
   progress?.Invoke("正在请求 Steam 正常退出，请稍候…");runtime.RequestShutdown(steam);
   var timer=Stopwatch.StartNew();int closedChecks=0;
   while(closedChecks<2){
    if(timer.Elapsed>=timeout)throw new InvalidOperationException("Steam 尚未完全退出，操作已停止，配置和补丁未改动。请关闭正在运行的游戏或 Steam 提示窗口后重试。");
    await Task.Delay(poll);
    closedChecks=runtime.IsRunning(steam)?0:closedChecks+1;
   }
  }
  progress?.Invoke("Steam 已退出，正在备份并应用更改…");
  string message;
  try{message=await change();}
  catch(Exception failure){
   string recovery="";
   if(wasRunning){try{runtime.Start(steam);recovery=" 已重新发送 Steam 启动请求。";}catch(Exception e){recovery=" Steam 未能重新启动："+e.Message;}}
   throw new InvalidOperationException("操作未完成："+failure.Message+recovery,failure);
  }
  progress?.Invoke("更改已完成，正在启动 Steam…");
  try{runtime.Start(steam);return new(message,true,"已发送 Steam 启动请求。");}
  catch(Exception e){return new(message,false,"更改已保存，但 Steam 启动失败，请手动启动："+e.Message);}
 }
}
