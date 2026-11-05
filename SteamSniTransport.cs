using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
namespace NovaLibrary;

public sealed class SteamSniTransport : IDisposable {
 static readonly HttpRequestOptionsKey<Destination> DestinationKey=new("Bear.SniDestination");
 sealed record Destination(Uri Original,string Address);
 readonly HttpClient http;
 public SteamSniTransport(){
  http=new(new SocketsHttpHandler{
   UseProxy=false,UseCookies=false,AllowAutoRedirect=false,
   AutomaticDecompression=DecompressionMethods.All,
   PooledConnectionLifetime=TimeSpan.FromMinutes(5),
   ConnectCallback=Connect
  }){Timeout=Timeout.InfiniteTimeSpan};
 }
 internal static bool Validate(X509Certificate? certificate,SslPolicyErrors errors,string hostname){
  if(certificate==null||(errors&~SslPolicyErrors.RemoteCertificateNameMismatch)!=SslPolicyErrors.None)return false;
  using var cert=new X509Certificate2(certificate);
  return cert.MatchesHostname(hostname,allowWildcards:true,allowCommonName:false);
 }
 static async ValueTask<Stream> Connect(SocketsHttpConnectionContext context,CancellationToken ct){
  context.InitialRequestMessage.Options.TryGetValue(DestinationKey,out var value);
  var destination=value!;
  var socket=new Socket(SocketType.Stream,ProtocolType.Tcp);
  try{
   await socket.ConnectAsync(IPAddress.Parse(destination.Address),destination.Original.Port,ct);
   var tls=new SslStream(new NetworkStream(socket,ownsSocket:true),leaveInnerStreamOpen:false);
   try{
    await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions{
     // An IP target omits SNI. Validate the original domain independently.
     TargetHost=destination.Address,
     RemoteCertificateValidationCallback=(_,cert,_,errors)=>Validate(cert,errors,destination.Original.Host)
    },ct);
    return tls;
   }catch{await tls.DisposeAsync();throw;}
  }catch{socket.Dispose();throw;}
 }
 public async Task<byte[]> Get(Uri original,string address,CancellationToken ct,int seconds=5){
  using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
  // HTTP framing travels inside the TLS stream created above; no plaintext request leaves the app.
  var internalUri=new UriBuilder(original){Scheme="http",Port=original.Port}.Uri;
  using var request=new HttpRequestMessage(HttpMethod.Get,internalUri){Version=HttpVersion.Version11,VersionPolicy=HttpVersionPolicy.RequestVersionExact};
  request.Options.Set(DestinationKey,new Destination(original,address));
  request.Headers.Host=original.Authority;
  request.Headers.UserAgent.ParseAdd("Mozilla/5.0 BearLibrary/1.8");
  request.Headers.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.6");
  try{
   using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
   if((int)response.StatusCode is >=300 and <400)throw new HttpRequestException("该节点需要普通连接重定向。");
   response.EnsureSuccessStatusCode();
   using var stream=await response.Content.ReadAsStreamAsync(timeout.Token);using var data=new MemoryStream();var buffer=new byte[8192];int n;
   while((n=await stream.ReadAsync(buffer,timeout.Token))>0){if(data.Length+n>4*1024*1024)throw new InvalidDataException("公开资料响应超过大小限制。");data.Write(buffer,0,n);}
   return data.ToArray();
  }catch(OperationCanceledException e) when(!ct.IsCancellationRequested){throw new HttpRequestException("本地加速连接超时。",e);}
 }
 public void Dispose()=>http.Dispose();
}
