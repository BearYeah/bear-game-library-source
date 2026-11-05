using System.Net;
using System.Text;
using System.Text.Json;
namespace NovaLibrary;

public sealed class SteamRouteSnapshot {
 public DateTime UpdatedUtc {get;set;}
 public Dictionary<string,string> Hosts {get;set;}=new(StringComparer.OrdinalIgnoreCase);
}

public sealed class SteamNetworkRoutes(Storage storage,Settings settings) {
 static readonly SemaphoreSlim RefreshGate=new(1);
 static readonly string[] PublicSources=[
  "https://cdn.jsdelivr.net/gh/Clov614/SteamHostSync@main/Hosts_steam",
  "https://raw.githubusercontent.com/Clov614/SteamHostSync/main/Hosts_steam"
 ];
 static IEnumerable<string> Sources=>new[]{Environment.GetEnvironmentVariable("BEAR_STEAM_HOSTS_URL")??""}.Where(url=>url.Length>0).Concat(PublicSources);
 DateTime checkedUntil;
 public static string CacheFile(Storage storage)=>Path.Combine(storage.Root,"cache","network","steam-hosts.json");
 public static bool IsSource(string url)=>Sources.Contains(url,StringComparer.Ordinal);
 public static bool Supported(string host)=>host is "store.steampowered.com" or "api.steampowered.com" or "steamcommunity.com" or "www.steamcommunity.com" or "steamcdn-a.akamaihd.net"||host.EndsWith(".steamstatic.com",StringComparison.OrdinalIgnoreCase);
 public static bool Direct(Settings settings,Uri uri){
  if(!string.IsNullOrWhiteSpace(settings.Proxy))return false;
  var proxy=BearProxy.Current;
  return proxy.IsBypassed(uri)||proxy.GetProxy(uri) is not { } target||target==uri;
 }
 public static Dictionary<string,string> Parse(string text){
  var hosts=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
  foreach(var line in text.Split('\n')){
   var parts=line.Split('#')[0].Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
   if(parts.Length<2||!IPAddress.TryParse(parts[0],out var address))continue;
   foreach(var host in parts.Skip(1))if(Supported(host))hosts[host]=address.ToString();
  }
  return hosts;
 }
 SteamRouteSnapshot Read(){
  var file=CacheFile(storage);
  if(!File.Exists(file))return new();
  try{return JsonSerializer.Deserialize<SteamRouteSnapshot>(File.ReadAllBytes(file))??new();}
  catch(JsonException){return new();}
 }
 public async Task Refresh(CancellationToken ct){
  if(DateTime.UtcNow<checkedUntil)return;
  await RefreshGate.WaitAsync(ct);
  try{
   var cached=Read();
   if(cached.UpdatedUtc>DateTime.UtcNow.AddHours(-12)){checkedUntil=cached.UpdatedUtc.AddHours(12);return;}
   var curl=new CurlTransport(storage,settings);
   foreach(var source in Sources){
    try{
     var hosts=Parse(Encoding.UTF8.GetString(await curl.Get(source,ct,seconds:6)));
     if(hosts.Count==0)continue;
     var snapshot=new SteamRouteSnapshot{UpdatedUtc=DateTime.UtcNow,Hosts=hosts};
     var file=CacheFile(storage);Directory.CreateDirectory(Path.GetDirectoryName(file)!);
     SafeFiles.Atomic(file,JsonSerializer.SerializeToUtf8Bytes(snapshot));
     checkedUntil=snapshot.UpdatedUtc.AddHours(12);return;
    }catch(HttpRequestException){}
    catch(OperationCanceledException) when(!ct.IsCancellationRequested){}
   }
   checkedUntil=DateTime.UtcNow.AddHours(1);
  }finally{RefreshGate.Release();}
 }
 public async Task<string?> Address(string host,CancellationToken ct){
  if(!Supported(host))return null;
  await Refresh(ct);
  return Read().Hosts.GetValueOrDefault(host);
 }
}
