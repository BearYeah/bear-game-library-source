using System.Diagnostics;
using System.Reflection;
using System.Net;
using System.Text;
namespace NovaLibrary;
public sealed class CurlTransport(Storage storage,Settings settings) {
 static readonly object ExtractGate=new();
 static readonly Dictionary<string,string> Hashes=new(){
  ["curl.exe"]="39340e791e71ee3e712c734369c777cc258991c519e42e3f99a889b4d02d1adc",
  ["curl-ca-bundle.crt"]="1fdcf2a55c9806c4c1e8391cb283871ee3b209d68a2879ce4c0898d949de2545"
 };
 public static bool Needed(Exception e){
  for(Exception? inner=e;inner!=null;inner=inner.InnerException)
   if(inner is System.ComponentModel.Win32Exception w&&w.NativeErrorCode==unchecked((int)0x8009030e))return true;
  return false;
 }
 internal string Prepare(){
  var dir=Path.Combine(storage.Root,"runtime","curl");lock(ExtractGate){
   SafeFiles.NoLinks(storage.Root,dir);Directory.CreateDirectory(dir);
   foreach(var (name,hash) in Hashes){
    var file=Path.Combine(dir,name);SafeFiles.NoLinks(storage.Root,file);
    if(File.Exists(file)&&SafeFiles.HashFile(file)==hash)continue;
    using var resource=Assembly.GetExecutingAssembly().GetManifestResourceStream("NovaLibrary.NetAssets."+name)??throw new IOException("兼容联网资源缺失。");
    using var m=new MemoryStream();resource.CopyTo(m);var bytes=m.ToArray();if(SafeFiles.Hash(bytes)!=hash)throw new IOException("兼容联网资源校验失败。");
    SafeFiles.Atomic(file,bytes);
   }
  }return dir;
 }
 public async Task<byte[]> Get(string url,CancellationToken ct,string? address=null,int seconds=14){
  var uri=new Uri(url);
  if(uri.Scheme!="https"||(uri.Host!="store.steampowered.com"&&!SteamStore.AllowedImage(url)&&!SteamNetworkRoutes.IsSource(url)&&!SteamPublicRelay.Allowed(uri)))throw new HttpRequestException("请求地址不属于支持的公开服务。");
  var dir=Prepare();string proxy=settings.Proxy;
  if(string.IsNullOrWhiteSpace(proxy)){
   var system=BearProxy.Current;
   if(!system.IsBypassed(uri)){var target=system.GetProxy(uri);if(target!=null&&target!=uri)proxy=target.AbsoluteUri;}
  }
  if(proxy.StartsWith("socks5://",StringComparison.OrdinalIgnoreCase))proxy="socks5h://"+proxy[9..];
  var start=new ProcessStartInfo(Path.Combine(dir,"curl.exe")){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=dir};
  foreach(var arg in new[]{"--fail","--silent","--show-error","--location","--proto","=https","--proto-redir","=https","--compressed","--connect-timeout",Math.Min(seconds,5).ToString(),"--max-time",seconds.ToString(),"--max-filesize","4194304","--cacert",Path.Combine(dir,"curl-ca-bundle.crt"),"--proxy",proxy,"--user-agent","Mozilla/5.0 BearLibrary/1.2","--header","Accept-Language: zh-CN,zh;q=0.9,en;q=0.6"})start.ArgumentList.Add(arg);
  if(address!=null&&SteamNetworkRoutes.Direct(settings,uri)){
   start.ArgumentList.Add("--resolve");start.ArgumentList.Add($"{uri.Host}:443:{address}");
  }
  start.ArgumentList.Add("--write-out");start.ArgumentList.Add("%{stderr}BEAR_HTTP_STATUS:%{http_code}");start.ArgumentList.Add(url);
  using var process=new Process{StartInfo=start};
  using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(seconds+3));
  process.Start();using var registration=timeout.Token.Register(()=>{try{if(!process.HasExited)process.Kill(true);}catch(InvalidOperationException){}});
  try{
   var error=process.StandardError.ReadToEndAsync(timeout.Token);using var output=new MemoryStream();var buffer=new byte[8192];int n;
   while((n=await process.StandardOutput.BaseStream.ReadAsync(buffer,timeout.Token))>0){
    if(output.Length+n>4*1024*1024){process.Kill(true);throw new InvalidDataException("公开资料响应超过大小限制。");}output.Write(buffer,0,n);
   }
   await process.WaitForExitAsync(timeout.Token);var failure=await error;
   if(process.ExitCode==22){
    var match=System.Text.RegularExpressions.Regex.Match(failure,@"BEAR_HTTP_STATUS:(\d{3})");
    var status=match.Success?(HttpStatusCode)int.Parse(match.Groups[1].Value):HttpStatusCode.BadGateway;
    throw new HttpRequestException(status==HttpStatusCode.TooManyRequests?"Steam 请求过于频繁，请稍后再试。":"公开服务暂时无法提供该内容。",null,status);
   }
   if(process.ExitCode!=0)throw new HttpRequestException(process.ExitCode==60?"网络证书验证未通过。":$"兼容联网组件连接失败（错误码 {process.ExitCode}），请检查代理。");
   return output.ToArray();
  }catch(OperationCanceledException) when(!ct.IsCancellationRequested){throw new HttpRequestException("网络连接超时，请稍后重试。");}
  catch{try{if(!process.HasExited)process.Kill(true);}catch(InvalidOperationException){}throw;}
 }
}
