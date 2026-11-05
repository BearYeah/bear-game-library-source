using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
namespace NovaLibrary;
public static class LanTests {
 public static int LaunchChild(string output){File.WriteAllText(output,JsonSerializer.Serialize(new{appid=Environment.GetEnvironmentVariable("SteamAppId"),gameid=Environment.GetEnvironmentVariable("SteamGameId"),directory=Environment.CurrentDirectory}));return 0;}
 public static async Task<int> PrepareCore(string output){try{var storage=new Storage(output);var core=new LanCoreService(storage);await core.Prepare(CancellationToken.None);var api=Path.Combine(core.Current,"regular","x64","steam_api64.dll");var first=File.GetLastWriteTimeUtc(api);await core.Prepare(CancellationToken.None);File.WriteAllText(Path.Combine(output,"core-prepare.json"),JsonSerializer.Serialize(new{result="pass",version=LanCoreService.Version,api=File.Exists(api),lobbyTool=File.Exists(Path.Combine(core.Current,"tools","lobby_connect","lobby_connect_x64.exe")),reused=first==File.GetLastWriteTimeUtc(api),scope="embedded core preparation with no network request"}));return 0;}catch(Exception error){File.WriteAllText(Path.Combine(output,"core-prepare.json"),JsonSerializer.Serialize(new{result="fail",error=error.ToString()}));return 1;}}

 public static int Run(string output,string core){
  Directory.CreateDirectory(output);var checks=new List<string>();void Check(bool value,string name){if(!value)throw new InvalidOperationException(name);checks.Add(name);}
  try{
   var storage=new Storage(Path.Combine(output,"data"));var service=new LanService(storage);var game=new GameItem{AppId=413150,Name="星露谷物语"};var settings=new Settings();var profile=service.Load(game,settings);
   Check(profile.PlayerId==service.Load(new GameItem{AppId=123,Name="Other"},settings).PlayerId,"one local player identity is shared across game profiles");
   var gameDir=Path.Combine(output,"fixture-game");Directory.CreateDirectory(gameDir);profile.Executable=Path.Combine(gameDir,"Stardew Valley.exe");File.WriteAllText(profile.Executable,"Fixture placeholder, never executed.");profile.ApiFile=Path.Combine(gameDir,"steam_api64.dll");var original=File.ReadAllBytes(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"version.dll"));File.WriteAllBytes(profile.ApiFile,original);
   Directory.CreateDirectory(Path.Combine(gameDir,"steam_settings"));var user=Path.Combine(gameDir,"steam_settings","configs.user.ini");File.WriteAllText(user,"original user configuration");File.WriteAllText(Path.Combine(gameDir,"neighbor.txt"),"keep");profile.Peers="127.0.0.1:47652";service.Save(profile);
   var selector=new LanCoreService(storage);selector.Select(core);var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};var window=new LanWindow(storage,settings,game);
   try{
    ((TextBox)window.FindName("NicknameBox")).Text="Bear 联机测试";
    ((Button)window.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    Check(service.Installed(413150),"WPF apply action deploys the real emulator and writes an installation record");
    Check(File.ReadAllText(user).Contains("Bear 联机测试")&&File.ReadAllText(Path.Combine(gameDir,"steam_settings","configs.main.ini")).Contains("disable_lobby_creation=0"),"Unicode nickname and lobby networking options reach the emulator configuration");
    var before=Directory.GetFiles(Path.Combine(service.Folder(413150),"originals"),"*",SearchOption.AllDirectories).Length;
    ((Button)window.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    Check(before==Directory.GetFiles(Path.Combine(service.Folder(413150),"originals"),"*",SearchOption.AllDirectories).Length,"reapplying keeps one original backup per managed file");
    window.ExportPreview(output);((Button)window.FindName("RestoreButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    Check(!service.Installed(413150)&&File.ReadAllBytes(profile.ApiFile).SequenceEqual(original)&&File.ReadAllText(user)=="original user configuration","restore returns the original DLL and preexisting configuration");
    Check(!File.Exists(Path.Combine(gameDir,"steam_settings","configs.main.ini"))&&File.ReadAllText(Path.Combine(gameDir,"neighbor.txt"))=="keep","restore removes created settings and preserves unrelated files");
   }finally{window.Close();app.Shutdown();}
   profile.Overlay=true;service.Apply(profile,core);
   Check(File.Exists(Path.Combine(gameDir,"steamclient64.dll"))&&File.ReadAllText(Path.Combine(gameDir,"steam_settings","configs.overlay.ini")).Contains("enable_experimental_overlay=1"),"experimental mode deploys its required companion DLL and enables the overlay");service.Restore(profile.AppId);
   Check(!File.Exists(Path.Combine(gameDir,"steamclient64.dll")),"restoring experimental mode removes its newly created companion DLL");
   File.Copy(Environment.ProcessPath!,profile.Executable,true);var launched=Path.Combine(output,"launch-child.json");profile.Arguments="--lan-launch-child \""+launched+"\"";
   using(var process=LanService.Start(profile)){if(!process.WaitForExit(10000)){process.Kill(true);throw new TimeoutException("Launch child timed out");}Check(process.ExitCode==0,"launch service starts the selected executable with its arguments");}
   using(var launch=JsonDocument.Parse(File.ReadAllBytes(launched)))Check(launch.RootElement.GetProperty("appid").GetString()=="413150"&&launch.RootElement.GetProperty("directory").GetString()!.Equals(gameDir,StringComparison.OrdinalIgnoreCase),"game receives the chosen AppID and executable working directory");
   var native=Path.Combine(output,"native");Directory.CreateDirectory(native);var exe=Environment.ProcessPath!;
   foreach(var role in new[]{"host","client"}){
    var dir=Path.Combine(native,role);Directory.CreateDirectory(Path.Combine(dir,"steam_settings"));File.Copy(Path.Combine(core,"regular","x64","steam_api64.dll"),Path.Combine(dir,"steam_api64.dll"),true);
    File.WriteAllText(Path.Combine(dir,"steam_settings","steam_appid.txt"),"413150");
    File.WriteAllText(Path.Combine(dir,"steam_settings","configs.user.ini"),$"[user::general]\naccount_name=Bear {role}\naccount_steamid={(role=="host"?76561198001000001UL:76561198001000002UL)}\nlanguage=english\n[user::saves]\nlocal_save_path={Path.Combine(dir,"saves")}\n");
    File.WriteAllText(Path.Combine(dir,"steam_settings","configs.main.ini"),$"[main::connectivity]\ndisable_networking=0\ndisable_lobby_creation=0\ndisable_lan_only=1\nlisten_port={(role=="host"?47651:47652)}\n");
    File.WriteAllText(Path.Combine(dir,"steam_settings","custom_broadcasts.txt"),role=="host"?"127.0.0.1:47652":"127.0.0.1:47651");
   }
   using var host=StartPeer(exe,Path.Combine(native,"host"),"host");using var client=StartPeer(exe,Path.Combine(native,"client"),"client");
   if(!host.WaitForExit(35000)||!client.WaitForExit(5000)){if(!host.HasExited)host.Kill(true);if(!client.HasExited)client.Kill(true);throw new TimeoutException("Native lobby probe timed out.");}
   Check(host.ExitCode==0&&client.ExitCode==0,"two real Goldberg processes discover a lobby and join it over loopback");
   File.WriteAllText(Path.Combine(output,"lan-test.json"),JsonSerializer.Serialize(new{result="pass",checks,scope="Real Goldberg core, isolated WPF deployment/restore and two loopback peers. Stardew Valley is not installed; actual gameplay and two-machine LAN compatibility are not tested."},Storage.Json));return 0;
  }catch(Exception error){File.WriteAllText(Path.Combine(output,"lan-test.json"),JsonSerializer.Serialize(new{result="fail",checks,error=error.ToString()},Storage.Json));return 1;}
 }
 static Process StartPeer(string exe,string folder,string role){var start=new ProcessStartInfo(exe){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=folder};foreach(var arg in new[]{"--lan-native-peer",folder,role})start.ArgumentList.Add(arg);return Process.Start(start)!;}
 [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return:MarshalAs(UnmanagedType.I1)] delegate bool Init();
 [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void Tick();
 [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr Interface();
 [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate ulong Create(IntPtr self,int type,int members);
 [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate ulong Request(IntPtr self);
 [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate ulong ByIndex(IntPtr self,int index);
 [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate ulong Join(IntPtr self,ulong lobby);
 [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Members(IntPtr self,ulong lobby);
 [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return:MarshalAs(UnmanagedType.I1)] delegate bool Completed(IntPtr self,ulong call,out byte failed);
 [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return:MarshalAs(UnmanagedType.I1)] delegate bool Result(IntPtr self,ulong call,IntPtr buffer,int size,int callback,out byte failed);
 public static int Peer(string folder,string role){
  Directory.SetCurrentDirectory(folder);Environment.SetEnvironmentVariable("SteamAppId","413150");Environment.SetEnvironmentVariable("SteamGameId","413150");
  try{
   var dll=NativeLibrary.Load(Path.Combine(folder,"steam_api64.dll"));T Fn<T>(string name)where T:Delegate=>Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(dll,name));
   var init=Fn<Init>("SteamAPI_Init");var tick=Fn<Tick>("SteamAPI_RunCallbacks");if(!init())throw new Exception("SteamAPI_Init failed");
   var match=Fn<Interface>("SteamAPI_SteamMatchmaking_v009")();var utils=Fn<Interface>("SteamAPI_SteamUtils_v010")();var done=Fn<Completed>("SteamAPI_ISteamUtils_IsAPICallCompleted");var result=Fn<Result>("SteamAPI_ISteamUtils_GetAPICallResult");
   byte[] Await(ulong call,int size,int callback){var stop=Stopwatch.StartNew();while(stop.Elapsed.TotalSeconds<10){tick();if(done(utils,call,out var failed)){if(failed!=0)throw new Exception("Native async call failed");var p=Marshal.AllocHGlobal(size);try{if(!result(utils,call,p,size,callback,out failed)||failed!=0)throw new Exception("Native result unavailable");var bytes=new byte[size];Marshal.Copy(p,bytes,0,size);return bytes;}finally{Marshal.FreeHGlobal(p);}}Thread.Sleep(50);}throw new TimeoutException("Native async call timed out");}
   ulong lobby=0;var clock=Stopwatch.StartNew();var members=Fn<Members>("SteamAPI_ISteamMatchmaking_GetNumLobbyMembers");
   if(role=="host"){var created=Await(Fn<Create>("SteamAPI_ISteamMatchmaking_CreateLobby")(match,2,4),16,513);lobby=BitConverter.ToUInt64(created,8);}
   else{
    var request=Fn<Request>("SteamAPI_ISteamMatchmaking_RequestLobbyList");var index=Fn<ByIndex>("SteamAPI_ISteamMatchmaking_GetLobbyByIndex");
    while(lobby==0&&clock.Elapsed.TotalSeconds<20){for(int n=0;n<20;n++){tick();Thread.Sleep(50);}var list=Await(request(match),4,510);if(BitConverter.ToUInt32(list)>0)lobby=index(match,0);}
    if(lobby==0)throw new Exception("No lobby discovered");Fn<Join>("SteamAPI_ISteamMatchmaking_JoinLobby")(match,lobby);
   }
   while(clock.Elapsed.TotalSeconds<28){tick();if(members(match,lobby)>=2){File.WriteAllText(Path.Combine(folder,"peer-result.json"),JsonSerializer.Serialize(new{result="pass",role,lobby,members=members(match,lobby)}));Fn<Tick>("SteamAPI_Shutdown")();return 0;}Thread.Sleep(50);}
   throw new Exception("Lobby did not reach two members");
  }catch(Exception error){File.WriteAllText(Path.Combine(folder,"peer-result.json"),JsonSerializer.Serialize(new{result="fail",role,error=error.ToString()}));return 1;}
 }
}
