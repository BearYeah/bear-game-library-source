using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
namespace NovaLibrary;

public sealed class LanProfile {
 public uint AppId {get;set;}public string Name {get;set;}="";
 public string Executable {get;set;}="";public string ApiFile {get;set;}="";public string Arguments {get;set;}="";
 public string Nickname {get;set;}="Bear玩家";public ulong PlayerId {get;set;}
 public int Port {get;set;}=47584;public string Peers {get;set;}="";public string Language {get;set;}="schinese";
 public bool Overlay {get;set;}
}
public sealed class LanOriginal {public string Name {get;set;}="";public bool Existed {get;set;}}
public sealed class LanInstallation {
 public string ApiFile {get;set;}="";public List<LanOriginal> Originals {get;set;}=[];
}
public sealed class LanService(Storage storage) {
 public string Folder(uint id)=>Path.Combine(storage.Root,"lan","games",id.ToString());
 string ProfileFile(uint id)=>Path.Combine(Folder(id),"profile.json");
 string StateFile(uint id)=>Path.Combine(Folder(id),"installed.json");
 public bool Installed(uint id)=>File.Exists(StateFile(id));
 public static ulong NewPlayerId()=>76561197960265728UL+(uint)RandomNumberGenerator.GetInt32(100000,int.MaxValue);
 public LanProfile Load(GameItem game,Settings settings){
  if(File.Exists(ProfileFile(game.AppId)))return JsonSerializer.Deserialize<LanProfile>(File.ReadAllBytes(ProfileFile(game.AppId)))!;
  var identity=Path.Combine(storage.Root,"lan","player-id.txt");Directory.CreateDirectory(Path.GetDirectoryName(identity)!);
  if(!File.Exists(identity))File.WriteAllText(identity,NewPlayerId().ToString());
  var profile=new LanProfile{AppId=game.AppId,Name=game.Name,PlayerId=ulong.Parse(File.ReadAllText(identity)),Language=settings.DownloadLanguage};
  var job=Path.Combine(storage.Root,"downloads",game.AppId.ToString(),"plan.json");
  if(File.Exists(job)){
   using var json=JsonDocument.Parse(File.ReadAllBytes(job));var dir=json.RootElement.GetProperty("destination").GetString()!;
   if(Directory.Exists(dir))profile.Executable=Directory.EnumerateFiles(dir,"*.exe").FirstOrDefault(p=>!Path.GetFileName(p).Contains("unins",StringComparison.OrdinalIgnoreCase))??"";
  }
  if(game.AppId==413150&&profile.Executable.Length==0){
   var libraries=new List<string>{settings.SteamPath};var list=Path.Combine(settings.SteamPath,"steamapps","libraryfolders.vdf");
   if(File.Exists(list))libraries.AddRange(System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(list),"\"path\"\\s+\"([^\"]+)\"").Select(m=>m.Groups[1].Value.Replace("\\\\","\\")));
   profile.Executable=libraries.Select(p=>Path.Combine(p,"steamapps","common","Stardew Valley","Stardew Valley.exe")).FirstOrDefault(File.Exists)??"";
  }
  if(profile.Executable.Length>0)profile.ApiFile=FindApi(profile.Executable);
  return profile;
 }
 public void Save(LanProfile profile){Directory.CreateDirectory(Folder(profile.AppId));File.WriteAllBytes(ProfileFile(profile.AppId),JsonSerializer.SerializeToUtf8Bytes(profile,Storage.Json));}
 public static string FindApi(string executable){
  var dir=Path.GetDirectoryName(executable)!;
  foreach(var name in new[]{"steam_api64.dll","steam_api.dll"}){var p=Path.Combine(dir,name);if(File.Exists(p))return p;}
  return Directory.EnumerateFiles(dir,"steam_api*.dll",SearchOption.AllDirectories).FirstOrDefault(p=>Path.GetFileName(p) is "steam_api.dll" or "steam_api64.dll")??"";
 }
 public static bool Is64(string dll){using var reader=new BinaryReader(File.OpenRead(dll));if(reader.ReadUInt16()!=0x5a4d)throw new InvalidDataException("所选文件不是 Windows DLL。");reader.BaseStream.Position=0x3c;var offset=reader.ReadInt32();reader.BaseStream.Position=offset;if(reader.ReadUInt32()!=0x4550)throw new InvalidDataException("DLL 的 PE 头无效。");return reader.ReadUInt16() switch{0x8664=>true,0x14c=>false,_=>throw new InvalidDataException("首版支持 x86 / x64 Steam API。")};}
 public static void Validate(LanProfile profile){
  if(!File.Exists(profile.Executable))throw new InvalidOperationException("请先选择已安装游戏的启动程序。");
  if(!File.Exists(profile.ApiFile)||Path.GetFileName(profile.ApiFile) is not ("steam_api.dll" or "steam_api64.dll"))throw new InvalidOperationException("请选择该游戏的 steam_api.dll 或 steam_api64.dll。");
  if(profile.PlayerId<76561197960265729UL||profile.PlayerId>76561202255233023UL)throw new InvalidOperationException("玩家 ID 应为有效的本地 Steam 格式 ID。");
  if(profile.Port<1||profile.Port>65535)throw new InvalidOperationException("端口范围为 1–65535。");
  if(string.IsNullOrWhiteSpace(profile.Nickname)||profile.Nickname.IndexOfAny(['\r','\n','\0'])>=0)throw new InvalidOperationException("玩家昵称应为一行文字。");
 }
 public void Apply(LanProfile profile,string core){
  Validate(profile);bool x64=Is64(profile.ApiFile);var flavor=profile.Overlay?"experimental":"regular";
  var dll=Path.Combine(core,flavor,x64?"x64":"x86",x64?"steam_api64.dll":"steam_api.dll");
  if(!File.Exists(dll))throw new InvalidOperationException("联机核心不完整，请先下载官方核心或导入完整 release 文件夹。");
  var root=Path.GetDirectoryName(Path.GetFullPath(profile.ApiFile))!;var apiName=Path.GetFileName(profile.ApiFile);
  var files=new Dictionary<string,byte[]>{[apiName]=File.ReadAllBytes(dll)};
  if(profile.Overlay){var client=x64?"steamclient64.dll":"steamclient.dll";files[client]=File.ReadAllBytes(Path.Combine(core,flavor,x64?"x64":"x86",client));}
  string saves=Path.Combine(storage.Root,"lan","saves",profile.AppId.ToString(),profile.PlayerId.ToString());
  files["steam_settings/steam_appid.txt"]=Encoding.UTF8.GetBytes(profile.AppId.ToString());
  files["steam_settings/configs.user.ini"]=Encoding.UTF8.GetBytes($"[user::general]\naccount_name={profile.Nickname}\naccount_steamid={profile.PlayerId}\nlanguage={profile.Language}\n[user::saves]\nlocal_save_path={saves}\n");
  files["steam_settings/configs.main.ini"]=Encoding.UTF8.GetBytes($"[main::connectivity]\ndisable_networking=0\ndisable_lobby_creation=0\ndisable_lan_only=1\noffline=0\nlisten_port={profile.Port}\n");
  files["steam_settings/configs.overlay.ini"]=Encoding.UTF8.GetBytes($"[overlay::general]\nenable_experimental_overlay={(profile.Overlay?1:0)}\n[overlay::hotkeys]\nkey_combo=shift + tab\n");
  files["steam_settings/custom_broadcasts.txt"]=Encoding.UTF8.GetBytes(string.Join('\n',profile.Peers.Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries))+"\n");
  Directory.CreateDirectory(Folder(profile.AppId));var backup=Path.Combine(Folder(profile.AppId),"originals");Directory.CreateDirectory(backup);
  var state=Installed(profile.AppId)?JsonSerializer.Deserialize<LanInstallation>(File.ReadAllBytes(StateFile(profile.AppId)))!:new LanInstallation{ApiFile=Path.GetFullPath(profile.ApiFile)};
  if(!state.ApiFile.Equals(Path.GetFullPath(profile.ApiFile),StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("请先恢复原游戏文件，再切换 Steam API 路径。");
  foreach(var name in files.Keys){
   if(state.Originals.Any(o=>o.Name==name))continue;
   var target=Path.Combine(root,name);var original=new LanOriginal{Name=name,Existed=File.Exists(target)};
   if(original.Existed){var saved=Path.Combine(backup,name);Directory.CreateDirectory(Path.GetDirectoryName(saved)!);File.Copy(target,saved);}
   state.Originals.Add(original);
  }
  File.WriteAllBytes(StateFile(profile.AppId),JsonSerializer.SerializeToUtf8Bytes(state,Storage.Json));
  try{foreach(var (name,bytes) in files){var target=Path.Combine(root,name);Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.WriteAllBytes(target,bytes);}Save(profile);}
  catch{Restore(profile.AppId);throw;}
 }
 public void Restore(uint id){
  var state=JsonSerializer.Deserialize<LanInstallation>(File.ReadAllBytes(StateFile(id)))!;var root=Path.GetDirectoryName(Path.GetFullPath(state.ApiFile))!;var backup=Path.Combine(Folder(id),"originals");
  var allowed=new HashSet<string>(StringComparer.OrdinalIgnoreCase){"steam_api.dll","steam_api64.dll","steamclient.dll","steamclient64.dll","steam_settings/steam_appid.txt","steam_settings/configs.user.ini","steam_settings/configs.main.ini","steam_settings/configs.overlay.ini","steam_settings/custom_broadcasts.txt"};
  foreach(var entry in state.Originals){if(!allowed.Contains(entry.Name))throw new InvalidDataException("联机恢复记录包含未知文件。");var target=Path.Combine(root,entry.Name);if(entry.Existed)File.Copy(Path.Combine(backup,entry.Name),target,true);else if(File.Exists(target))File.Delete(target);}
  File.Delete(StateFile(id));foreach(var entry in state.Originals){var saved=Path.Combine(backup,entry.Name);if(File.Exists(saved))File.Delete(saved);}
 }
 public static Process Start(LanProfile profile){
  var start=new ProcessStartInfo(profile.Executable){UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(profile.Executable)!,Arguments=profile.Arguments};start.Environment["SteamAppId"]=profile.AppId.ToString();start.Environment["SteamGameId"]=profile.AppId.ToString();return Process.Start(start)!;
 }
 public static Process Browse(LanProfile profile,string core){
  var tool=Path.Combine(core,"tools","lobby_connect",Is64(profile.ApiFile)?"lobby_connect_x64.exe":"lobby_connect_x86.exe");
  if(!File.Exists(tool))throw new InvalidOperationException("此核心缺少 lobby_connect 工具，请导入完整 release 文件夹。");
  return Process.Start(new ProcessStartInfo(tool){UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(profile.Executable)!,CreateNoWindow=false})!;
 }
}

public sealed class LanCoreService(Storage storage,Settings settings) {
 public const string Version="release-2026_09_27";
 public const string Repository="Detanup01/gbe_fork";
 public const string SourceUrl="https://github.com/Detanup01/gbe_fork/tree/"+Version;
 const string Digest="300186c34f49a9ea75b33ce2af299267cc1dad0225cf45e81a229578d4e11c85";
 string Selection=>Path.Combine(storage.Root,"lan","core-path.txt");
 public string Current=>File.Exists(Selection)?File.ReadAllText(Selection):Path.Combine(storage.Root,"lan","core",Version,"release");
 public void Select(string folder){if(Directory.Exists(Path.Combine(folder,"release")))folder=Path.Combine(folder,"release");if(!File.Exists(Path.Combine(folder,"regular","x64","steam_api64.dll")))throw new InvalidOperationException("请选择含 regular、experimental、tools 的完整 release 文件夹。");Directory.CreateDirectory(Path.GetDirectoryName(Selection)!);File.WriteAllText(Selection,Path.GetFullPath(folder));}
 public async Task Download(IProgress<string> progress,CancellationToken ct,bool useProxy=false){
  var folder=Path.Combine(storage.Root,"lan","core",Version);Directory.CreateDirectory(folder);var archive=Path.Combine(folder,"core.7z");
  try{
   progress.Report("正在下载官方联机核心…");await Fetch($"https://github.com/{Repository}/releases/download/{Version}/emu-win-release-vs22.7z",archive,32*1024*1024,ct,progress,useProxy);
   if(!SafeFiles.HashFile(archive).Equals(Digest,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("联机核心下载不完整，请重新获取。");
   var seven=FindExtractor();if(seven==null){seven=Path.Combine(storage.Root,"lan","tools","7zr.exe");if(!File.Exists(seven)){Directory.CreateDirectory(Path.GetDirectoryName(seven)!);progress.Report("正在获取 7-Zip 官方解包组件…");await Fetch("https://www.7-zip.org/a/7zr.exe",seven,4*1024*1024,ct,useProxy:useProxy);}}
   progress.Report("正在解压联机核心…");await Run(seven,["x","-y","-o"+folder,archive,"release/regular/*","release/experimental/*","release/tools/lobby_connect/*","release/tools/generate_interfaces/*","release/README.release.md"],ct);Select(Path.Combine(folder,"release"));
   File.WriteAllText(Path.Combine(folder,"SOURCE.txt"),"Goldberg Steam Emulator\nLGPL-3.0\nCorresponding source: "+SourceUrl+"\n7-Zip source: https://www.7-zip.org/sdk.html\n");
   foreach(var name in new[]{"GPL-3.0.txt","LGPL-3.0.txt"}){using var source=typeof(LanCoreService).Assembly.GetManifestResourceStream("NovaLibrary.Assets."+name)!;using var target=File.Create(Path.Combine(folder,name));source.CopyTo(target);}
  }finally{if(File.Exists(archive))File.Delete(archive);}
 }
 static string? FindExtractor(){
  var registered=Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\7-Zip","Path",null)?.ToString();
  foreach(var folder in new[]{registered,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"7-Zip"),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),"7-Zip")})if(folder!=null&&File.Exists(Path.Combine(folder,"7z.exe")))return Path.Combine(folder,"7z.exe");return null;
 }
 async Task Fetch(string url,string output,long max,CancellationToken ct,IProgress<string>? progress=null,bool useProxy=false){
  var curl=new CurlTransport(storage,settings).Prepare();string proxy=useProxy?settings.Proxy:"";var uri=new Uri(url);
  if(useProxy&&string.IsNullOrWhiteSpace(proxy)&&!BearProxy.Current.IsBypassed(uri)){var p=BearProxy.Current.GetProxy(uri);if(p!=null&&p!=uri)proxy=p.AbsoluteUri;}
  if(proxy.StartsWith("socks5://",StringComparison.OrdinalIgnoreCase))proxy="socks5h://"+proxy[9..];
  try{
   var task=Run(Path.Combine(curl,"curl.exe"),["--fail","--silent","--show-error","--location","--proto","=https","--proto-redir","=https","--connect-timeout","15","--max-time","180","--max-filesize",max.ToString(),"--cacert",Path.Combine(curl,"curl-ca-bundle.crt"),"--proxy",proxy,"--user-agent","BearLibrary-LAN/1.8.15","--output",output,url],ct);
   var clock=Stopwatch.StartNew();long previous=0;double last=0;
   while(!task.IsCompleted){await Task.WhenAny(task,Task.Delay(1000));if(File.Exists(output)){long bytes=new FileInfo(output).Length;double now=clock.Elapsed.TotalSeconds;progress?.Report($"正在获取联机核心 · {bytes/1048576d:F1} MB · {(bytes-previous)/Math.Max(now-last,0.001)/1024:F0} KB/s");previous=bytes;last=now;}}
   await task;
  }catch{if(File.Exists(output))File.Delete(output);throw;}
 }
 static async Task Run(string program,IEnumerable<string> args,CancellationToken ct){
  var start=new ProcessStartInfo(program){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true};foreach(var arg in args)start.ArgumentList.Add(arg);
  using var process=Process.Start(start)!;using var cancel=ct.Register(()=>{try{if(!process.HasExited)process.Kill(true);}catch(InvalidOperationException){}});
  var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();await process.WaitForExitAsync(ct);await output;await error;if(process.ExitCode!=0)throw new IOException("联机组件获取失败，请检查代理，或导入已解压的官方核心文件夹。");
 }
}
