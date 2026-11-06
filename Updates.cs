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
public sealed record ClientUpdateInfo(string Version,string Url,string Notes);
public static class UpdatePromptFlow {
 public const string DownloadPage="https://wwbhp.lanzoul.com/b004jv6lfi";
 public static string Message(ClientUpdateInfo update)=>"发现新版本 "+update.Version+"。"+(string.IsNullOrWhiteSpace(update.Notes)?"":"\n\n"+update.Notes)+"\n\n是否打开蓝奏云更新网页？";
 public static bool TryOpen(ClientUpdateInfo update,Func<string,bool> confirm,Action<string> open){
  if(!confirm(Message(update)))return false;
  open(DownloadPage);return true;
 }
}
public sealed class UpdateService(Storage storage,Settings settings,Version? baseline=null,Func<CancellationToken,Task<ClientUpdateInfo?>>? fetch=null) {
 public async Task<ClientUpdateInfo?> Check(CancellationToken ct){
  var update=await (fetch?.Invoke(ct)??ClientPolicyService.GetUpdate(storage,settings,ct));
  return update!=null&&UpdateTrust.Normalize(update.Version)>(baseline??UpdateTrust.Current)?update:null;
 }
}
