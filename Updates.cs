using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
namespace NovaLibrary;
public sealed class ReleaseManifest {
 public string Version {get;set;}="";public string AssetUrl {get;set;}="";public string Sha256 {get;set;}="";
 public long Size {get;set;}public string Notes {get;set;}="";public DateTime PublishedUtc {get;set;}
}
public sealed class SignedRelease {public string Payload {get;set;}="";public string Signature {get;set;}="";}
public sealed class PendingUpdate {public string Id {get;set;}="";public string File {get;set;}="";public SignedRelease Envelope {get;set;}=new();public ReleaseManifest Release {get;set;}=new();}
public static class UpdateTrust {
 public const string Repository="BearYeah/bear-game-library-source";
 public const string AssetName="BearLibrary.exe";
 public const string PublicKey="";
 public static string ManifestUrl=>$"https://github.com/{Repository}/releases/latest/download/bear-update.json";
 public static Version Normalize(string text){if(!Version.TryParse(text.TrimStart('v'),out var v)||v.Major<0||v.Minor<0)throw new InvalidDataException("更新版本号无效。");return new(v.Major,v.Minor,Math.Max(0,v.Build),Math.Max(0,v.Revision));}
 public static Version Current=>Normalize(Assembly.GetExecutingAssembly().GetName().Version!.ToString());
 public static ReleaseManifest Verify(SignedRelease envelope){
  if(PublicKey.Length==0)throw new InvalidDataException("此源码构建尚未配置更新签名公钥。");
  if(envelope.Payload.Length>90000||envelope.Signature.Length>512)throw new InvalidDataException("更新清单过大。");
  var payload=Convert.FromBase64String(envelope.Payload);var signature=Convert.FromBase64String(envelope.Signature);
  if(!UpdateSignature.Verify(payload,signature))throw new InvalidDataException("更新发布签名不匹配，未下载或执行程序。");
  var manifest=JsonSerializer.Deserialize<ReleaseManifest>(payload)??throw new InvalidDataException("更新清单为空。");
  _ = Normalize(manifest.Version);
  if(!Uri.TryCreate(manifest.AssetUrl,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.Host!="github.com"||!uri.IsDefaultPort||uri.UserInfo.Length>0||uri.Query.Length>0||uri.Fragment.Length>0||!uri.AbsolutePath.StartsWith("/"+Repository+"/releases/download/",StringComparison.Ordinal)||!Uri.UnescapeDataString(uri.AbsolutePath).EndsWith("/"+AssetName,StringComparison.Ordinal))throw new InvalidDataException("更新下载地址不属于指定发布仓库。");
  if(manifest.Size<1024||manifest.Size>192L*1024*1024||manifest.Sha256.Length!=64||!manifest.Sha256.All(Uri.IsHexDigit)||manifest.Notes.Length>10000)throw new InvalidDataException("更新文件大小或校验信息无效。");
  return manifest;
 }
 public static void VerifyFile(string file,ReleaseManifest release,bool checkProduct=true){
  var info=new FileInfo(file);if(!info.Exists||info.Length!=release.Size||!SafeFiles.HashFile(file).Equals(release.Sha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("更新文件校验失败，已保留当前版本。");
  if(checkProduct){var version=FileVersionInfo.GetVersionInfo(file);if(version.ProductName!="Bear游戏库"||Normalize(version.FileVersion??"")!=Normalize(release.Version))throw new InvalidDataException("更新程序身份或文件版本不匹配。");}
 }
 public static int PrepareRelease(string privateKey,string executable,string version,string output){
  Directory.CreateDirectory(output);var bytes=File.ReadAllBytes(executable);var product=FileVersionInfo.GetVersionInfo(executable);
  if(product.ProductName!="Bear游戏库"||Normalize(product.FileVersion??"")!=Normalize(version))throw new InvalidDataException("待发布程序版本与清单版本不一致。");
  var pem=File.ReadAllText(privateKey);
  var manifest=new ReleaseManifest{Version=Normalize(version).ToString(3),AssetUrl=$"https://github.com/{Repository}/releases/download/v{Normalize(version).ToString(3)}/{Uri.EscapeDataString(AssetName)}",Sha256=SafeFiles.Hash(bytes),Size=bytes.Length,Notes="Bear游戏库更新；保留现有游戏库与设置。",PublishedUtc=DateTime.UtcNow};
  var payload=JsonSerializer.SerializeToUtf8Bytes(manifest);var signed=new SignedRelease{Payload=Convert.ToBase64String(payload),Signature=Convert.ToBase64String(UpdateSignature.Sign(payload,pem))};
  _ = Verify(signed);
  File.WriteAllBytes(Path.Combine(output,AssetName),bytes);File.WriteAllText(Path.Combine(output,"bear-update.json"),JsonSerializer.Serialize(signed,Storage.Json));
  File.WriteAllText(Path.Combine(output,"SHA256.txt"),manifest.Sha256+"  "+AssetName+"\r\n");return 0;
 }
}
public sealed class ReleaseTransport(Storage storage,Settings settings) {
 public async Task Download(string url,string output,long maximum,CancellationToken ct){
  var uri=new Uri(url);if(uri.Scheme!="https"||uri.Host!="github.com"||!uri.AbsolutePath.StartsWith("/"+UpdateTrust.Repository+"/releases/",StringComparison.Ordinal))throw new InvalidDataException("更新请求地址无效。");
  SafeFiles.NoLinks(storage.Root,output);var dir=new CurlTransport(storage,settings).Prepare();string proxy=settings.Proxy;
  if(string.IsNullOrWhiteSpace(proxy)){var system=HttpClient.DefaultProxy;if(!system.IsBypassed(uri)){var target=system.GetProxy(uri);if(target!=null&&target!=uri)proxy=target.AbsoluteUri;}}
  if(proxy.StartsWith("socks5://",StringComparison.OrdinalIgnoreCase))proxy="socks5h://"+proxy[9..];
  var start=new ProcessStartInfo(Path.Combine(dir,"curl.exe")){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true};
  foreach(var arg in new[]{"--fail","--silent","--show-error","--location","--max-redirs","5","--proto","=https","--proto-redir","=https","--connect-timeout","15","--max-time",maximum>1000000?"600":"30","--max-filesize",maximum.ToString(System.Globalization.CultureInfo.InvariantCulture),"--cacert",Path.Combine(dir,"curl-ca-bundle.crt"),"--proxy",proxy,"--user-agent","BearLibrary-Updater/1.5","--output",output,url})start.ArgumentList.Add(arg);
  using var process=new Process{StartInfo=start};using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(maximum>1000000?620:40));process.Start();
  using var registration=timeout.Token.Register(()=>{try{if(!process.HasExited)process.Kill(true);}catch{}});
  var error=process.StandardError.ReadToEndAsync(timeout.Token);var stdout=process.StandardOutput.ReadToEndAsync(timeout.Token);
  try{await process.WaitForExitAsync(timeout.Token);await error;await stdout;if(process.ExitCode!=0)throw new IOException(process.ExitCode==22?"发布服务器暂未提供此更新，请稍后重试。":"下载更新失败，请检查代理或稍后重试（错误码 "+process.ExitCode+"）。");if(!File.Exists(output)||new FileInfo(output).Length>maximum)throw new InvalidDataException("更新下载超过大小限制。");}
  catch{try{if(!process.HasExited)process.Kill(true);}catch{}if(File.Exists(output))File.Delete(output);throw;}
 }
}
public sealed class UpdateService(Storage storage,Settings settings,Version? baseline=null) {
 Version Current=>baseline??UpdateTrust.Current;
 public string Folder=>Path.Combine(storage.Root,"updates");
 public async Task<(SignedRelease Envelope,ReleaseManifest Release)?> Check(CancellationToken ct){
  SafeFiles.NoLinks(storage.Root,Folder);Directory.CreateDirectory(Folder);var file=Path.Combine(Folder,"check-"+Guid.NewGuid().ToString("N")+".json");
  try{await new ReleaseTransport(storage,settings).Download(UpdateTrust.ManifestUrl,file,128*1024,ct);var signed=JsonSerializer.Deserialize<SignedRelease>(await File.ReadAllTextAsync(file,ct))??throw new InvalidDataException("更新清单为空。");var release=UpdateTrust.Verify(signed);return UpdateTrust.Normalize(release.Version)>Current?(signed,release):null;}finally{if(File.Exists(file))File.Delete(file);}
 }
 public PendingUpdate? Ready(){
  var file=Path.Combine(Folder,"ready-update.json");SafeFiles.NoLinks(storage.Root,file);if(!File.Exists(file))return null;
  var pending=JsonSerializer.Deserialize<PendingUpdate>(File.ReadAllBytes(file))??throw new InvalidDataException("已下载更新记录无效。");
  if(pending.Id.Length!=32||!pending.Id.All(Uri.IsHexDigit)||!Path.GetFullPath(pending.File).Equals(Path.Combine(Folder,"download-"+pending.Id+".exe"),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("已下载更新路径无效。");
  pending.Release=UpdateTrust.Verify(pending.Envelope);
  if(UpdateTrust.Normalize(pending.Release.Version)<=Current){File.Delete(file);return null;}
  SafeFiles.NoLinks(storage.Root,pending.File);UpdateTrust.VerifyFile(pending.File,pending.Release);return pending;
 }
 public async Task<PendingUpdate> Stage(SignedRelease signed,CancellationToken ct){
  var release=UpdateTrust.Verify(signed);if(UpdateTrust.Normalize(release.Version)<=Current)throw new InvalidDataException("更新版本不高于当前版本。");
  SafeFiles.NoLinks(storage.Root,Folder);Directory.CreateDirectory(Folder);var id=Guid.NewGuid().ToString("N");var file=Path.Combine(Folder,"download-"+id+".exe");
  try{await new ReleaseTransport(storage,settings).Download(release.AssetUrl,file,release.Size,ct);UpdateTrust.VerifyFile(file,release);var pending=new PendingUpdate{Id=id,File=file,Envelope=signed,Release=release};var ready=Path.Combine(Folder,"ready-update.json");SafeFiles.NoLinks(storage.Root,ready);SafeFiles.Atomic(ready,JsonSerializer.SerializeToUtf8Bytes(pending,Storage.Json));return pending;}
  catch{if(File.Exists(file))File.Delete(file);throw;}
 }
}
