using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
namespace NovaLibrary;
public class SteamStore : IDisposable {
 readonly CurlTransport curl;bool compatibility;public bool UsesCompatibility=>compatibility;
 readonly HttpClient http;readonly Storage storage;readonly SemaphoreSlim requests=new(3);readonly Settings settings;
 readonly SteamNetworkRoutes routes;readonly bool liveTransport;
 bool publicRelay;public bool UsesPublicRelay=>publicRelay;
 readonly SteamSniTransport sni=new();readonly System.Collections.Concurrent.ConcurrentDictionary<string,byte> sniUnavailable=new(StringComparer.OrdinalIgnoreCase);
 public bool UsesLocalSni {get;private set;}
 readonly System.Collections.Concurrent.ConcurrentDictionary<string,string> activeRoutes=new(StringComparer.OrdinalIgnoreCase);
 public SteamStore(Storage storage,Settings settings,HttpMessageHandler? transport=null){
  this.storage=storage;this.settings=settings;curl=new(storage,settings);routes=new(storage,settings);liveTransport=transport==null;
  var handler=new HttpClientHandler{UseProxy=true,Proxy=new BearProxy(),AutomaticDecompression=DecompressionMethods.All};
  if(!string.IsNullOrWhiteSpace(settings.Proxy)){
   if(!Uri.TryCreate(settings.Proxy,UriKind.Absolute,out var proxy)||proxy.Scheme is not ("http" or "https" or "socks5"))throw new InvalidOperationException("代理地址应为 http://127.0.0.1:端口 或 socks5://127.0.0.1:端口。");
   handler.Proxy=new WebProxy(proxy);
  }else handler.DefaultProxyCredentials=CredentialCache.DefaultCredentials;
  http=new(transport??handler){Timeout=TimeSpan.FromSeconds(14)};
  http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 BearLibrary/1.2");
  http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.6");
 }
 public void Dispose(){http.Dispose();sni.Dispose();}
 public static List<GameItem> ParseSearch(string html){
  var found=new Dictionary<uint,GameItem>();
  var anchors=Regex.Matches(html,"<a\\b(?<attrs>[^>]*\\bdata-ds-appid\\s*=\\s*\"(?<id>\\d+)\"[^>]*)>(?<body>.*?)</a>",RegexOptions.Singleline|RegexOptions.IgnoreCase);
  foreach(Match a in anchors){
   if(!uint.TryParse(a.Groups["id"].Value,out var id)||id==0)continue;
   var body=a.Groups["body"].Value;
   var n=Regex.Match(body,"<(?:span|div)[^>]*class=\"(?:title|match_name)\"[^>]*>(.*?)</(?:span|div)>",RegexOptions.Singleline|RegexOptions.IgnoreCase);
   if(!n.Success)continue;
   var name=WebUtility.HtmlDecode(Regex.Replace(n.Groups[1].Value,"<.*?>","")).Trim();
   var image=Regex.Match(body,"<img[^>]*(?:src|data-src)=\"([^\"]+)\"",RegexOptions.IgnoreCase);
   found.TryAdd(id,new GameItem{AppId=id,Name=name,CoverUrl=image.Success?WebUtility.HtmlDecode(image.Groups[1].Value):"",IsFree=Regex.IsMatch(body,"class=\"[^\"]*\\bdiscount_final_price\\b[^\"]*\\bfree\\b[^\"]*\"",RegexOptions.IgnoreCase)});
  }
  return found.Values.ToList();
 }
 public async Task<List<GameItem>> Search(string query,bool free,CancellationToken ct){
  query=query.Trim();if(string.IsNullOrEmpty(query))throw new InvalidOperationException("请输入游戏名称、AppID 或商店链接。");
  try{var id=Rules.AppId(query);var info=await Details(id,ct);return [info.ToGame()];}catch(InvalidOperationException) when(!Regex.IsMatch(query,@"^\d+$|^https?://",RegexOptions.IgnoreCase)){ }
  var url="https://store.steampowered.com/search/results/?term="+Uri.EscapeDataString(query)+"&start=0&count=30&infinite=1&l=schinese&cc=CN"+(free?"&maxprice=free":"");
  var text=await GetString(url,ct);
  string html=text;try{using var doc=JsonDocument.Parse(text);if(doc.RootElement.TryGetProperty("results_html",out var p))html=p.GetString()??"";}catch(JsonException){}
  var results=ParseSearch(html);if(free)results=results.Where(g=>g.IsFree).ToList();
  if(results.Count==0&&html.Contains("captcha",StringComparison.OrdinalIgnoreCase))throw new HttpRequestException("Steam 搜索返回了验证页面，请稍后重试或使用 AppID 查询。");
  return results;
 }
 public async Task<FreeCatalogSnapshot> FetchFreeCatalog(IProgress<CatalogProgress>? progress,CancellationToken ct){
  var snapshot=new FreeCatalogSnapshot{Source="Steam public search: free price + Free to Play tag",Sources=["https://store.steampowered.com/search/?maxprice=free&category1=998&cc=US","https://store.steampowered.com/search/?tags=113&category1=998&cc=US"]};
  var entries=new Dictionary<uint,Candidate>();
  foreach(var filter in new[]{"maxprice=free","tags=113"}){
   int total=0,previous=snapshot.ScannedRows;
   for(int start=0;start==0||start<total;start+=100){
    if(snapshot.ScannedRows>0)await Task.Delay(1500,ct);
    string data="";
    for(int attempt=0;;attempt++){
     try{data=await GetString($"https://store.steampowered.com/search/results/?{filter}&category1=998&start={start}&count=100&infinite=1&l=english&cc=US&sort_by=Name_ASC",ct);break;}
     catch(HttpRequestException e) when(e.StatusCode==HttpStatusCode.TooManyRequests&&attempt<2){await Task.Delay(TimeSpan.FromSeconds(60),ct);}
    }
    using var doc=JsonDocument.Parse(data);
    if(start==0){total=doc.RootElement.GetProperty("total_count").GetInt32();if(total<=0||total>100000)throw new InvalidDataException("免费目录返回异常，已保留原候选池。");snapshot.TotalRows+=total;}
    var html=doc.RootElement.GetProperty("results_html").GetString()??"";
    if(string.IsNullOrWhiteSpace(html))throw new InvalidDataException("免费目录未完整读取，已保留原候选池。");
    foreach(var entry in FreeCatalog.Parse(html))entries[entry.AppId]=entry;
    snapshot.ScannedRows=previous+Math.Min(start+100,total);progress?.Report(new(snapshot.ScannedRows,snapshot.TotalRows,entries.Count));
   }
  }
  snapshot.Complete=true;snapshot.UpdatedUtc=DateTime.UtcNow;snapshot.Entries=entries.Values.OrderBy(c=>c.AppId).ToList();return snapshot;
 }
 async Task<string> GetString(string url,CancellationToken ct){
  await requests.WaitAsync(ct);try{return System.Text.Encoding.UTF8.GetString(await Download(url,ct));}finally{requests.Release();}
 }
 async Task<byte[]> Download(string url,CancellationToken ct,int seconds=6){
  var uri=new Uri(url);
  var relay=SteamPublicRelay.Address(uri);
  if(liveTransport&&settings.LocalSni&&SteamNetworkRoutes.Supported(uri.Host)&&!sniUnavailable.ContainsKey(uri.Host)&&SteamNetworkRoutes.Direct(settings,uri)){
   var address=await routes.Address(uri.Host,ct);
   if(address!=null){
    try{var accelerated=await sni.Get(uri,address,ct,Math.Min(seconds,5));UsesLocalSni=true;return accelerated;}
    catch(HttpRequestException e) when(e.StatusCode==null){sniUnavailable.TryAdd(uri.Host,0);}
   }
  }
  if(publicRelay&&relay!=null)return await curl.Get(relay,ct,seconds:10);
  if(activeRoutes.TryGetValue(uri.Host,out var active)&&SteamNetworkRoutes.Direct(settings,uri))return await curl.Get(url,ct,active,seconds);
  try{return await DownloadOnce(url,ct,seconds);}
  catch(Exception e) when(liveTransport&&!ct.IsCancellationRequested&&
    (e is HttpRequestException {StatusCode:null}||e is OperationCanceledException)){
   if(relay!=null){var relayed=await curl.Get(relay,ct,seconds:10);publicRelay=true;return relayed;}
   if(!SteamNetworkRoutes.Direct(settings,uri))throw;
   var address=await routes.Address(uri.Host,ct);if(address==null)throw;
   var data=await curl.Get(url,ct,address,seconds);activeRoutes[uri.Host]=address;return data;
  }
 }
 async Task<byte[]> DownloadOnce(string url,CancellationToken ct,int seconds){
  if(compatibility)return await curl.Get(url,ct,seconds:seconds);
  using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(seconds));var token=timeout.Token;
  try{
   using var response=await http.GetAsync(url,HttpCompletionOption.ResponseHeadersRead,token);response.EnsureSuccessStatusCode();
   if(response.Content.Headers.ContentLength>4*1024*1024)throw new InvalidDataException("公开资料响应超过大小限制。");
   using var stream=await response.Content.ReadAsStreamAsync(token);using var data=new MemoryStream();var buffer=new byte[8192];int n;
   while((n=await stream.ReadAsync(buffer,token))>0){if(data.Length+n>4*1024*1024)throw new InvalidDataException("公开资料响应超过大小限制。");data.Write(buffer,0,n);}
   return data.ToArray();
  }catch(HttpRequestException e) when(CurlTransport.Needed(e)){compatibility=true;return await curl.Get(url,ct,seconds:seconds);}
 }
 public static AppInfo ParseDetails(uint id,string json){
  using var doc=JsonDocument.Parse(json);
  if(!doc.RootElement.TryGetProperty(id.ToString(),out var app)||!app.TryGetProperty("success",out var success)||success.ValueKind!=JsonValueKind.True||!app.TryGetProperty("data",out var d))throw new InvalidOperationException("Steam 商店没有返回该 AppID 的公开资料。它可能是工具、地区不可见或已下架条目。");
  var info=new AppInfo{AppId=id};
  if(d.TryGetProperty("name",out var name))info.Name=name.GetString()??$"App {id}";
  if(d.TryGetProperty("type",out var type))info.Type=type.GetString()??"game";
  if(d.TryGetProperty("is_free",out var f))info.IsFree=f.ValueKind==JsonValueKind.True;
  foreach(var key in new[]{"header_image","capsule_image"})if(d.TryGetProperty(key,out var h)&&!string.IsNullOrWhiteSpace(h.GetString())){info.Header=h.GetString()!;break;}
  if(d.TryGetProperty("dlc",out var dlc)&&dlc.ValueKind==JsonValueKind.Array)foreach(var entry in dlc.EnumerateArray())if(entry.TryGetUInt32(out var n)&&n>0)info.DlcIds.Add(n);
  if(d.TryGetProperty("fullgame",out var parent)&&parent.TryGetProperty("appid",out var pid)&&uint.TryParse(pid.ToString(),out var p))info.ParentId=p;
  if(string.IsNullOrWhiteSpace(info.Name))info.Name=$"App {id}";
  return info;
 }
 public async Task<AppInfo> Details(uint id,CancellationToken ct,bool force=false){
  var file=Path.Combine(storage.Info,id+".json");SafeFiles.NoLinks(storage.Root,file);
  AppInfo? cached=null;
  if(File.Exists(file)){try{cached=JsonSerializer.Deserialize<AppInfo>(await File.ReadAllTextAsync(file,ct));}catch(JsonException){}}
  if(!force&&cached!=null&&cached.FetchedUtc>DateTime.UtcNow.AddHours(-24))return cached;
  try{
   var data=await GetString($"https://store.steampowered.com/api/appdetails?appids={id}&l=schinese&cc=cn",ct);
   var info=ParseDetails(id,data);ct.ThrowIfCancellationRequested();lock(storage.CacheGate){ct.ThrowIfCancellationRequested();SafeFiles.Atomic(file,JsonSerializer.SerializeToUtf8Bytes(info,Storage.Json));}return info;
  }catch(Exception e) when(cached!=null&&(e is HttpRequestException||e is TaskCanceledException&&!ct.IsCancellationRequested)){return cached;}
 }
 public static bool AllowedImage(string url){
  if(!Uri.TryCreate(url,UriKind.Absolute,out var uri)||uri.Scheme!="https")return false;
  var h=uri.Host;return h=="steamcdn-a.akamaihd.net"||h=="cdn.akamai.steamstatic.com"||h=="shared.akamai.steamstatic.com"||h.EndsWith(".steamstatic.com",StringComparison.OrdinalIgnoreCase);
 }
 public async Task<string> Cover(uint id,string url,CancellationToken ct){
  var file=Path.Combine(storage.Covers,id+".jpg");SafeFiles.NoLinks(storage.Root,file);
  if(File.Exists(file)){File.SetLastWriteTimeUtc(file,DateTime.UtcNow);return file;}
  await requests.WaitAsync(ct);
  try{
   byte[]? image=null;
   var candidates=new[]{url,$"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{id}/header.jpg",$"https://cdn.akamai.steamstatic.com/steam/apps/{id}/header.jpg"}.Where(AllowedImage).Distinct();
   foreach(var candidate in candidates){
    try{image=await Download(candidate,ct,6);break;}
    catch(HttpRequestException e) when(e.StatusCode is null or HttpStatusCode.NotFound){}
    catch(OperationCanceledException) when(!ct.IsCancellationRequested){}
   }
   if(image==null)return "";
   using var data=new MemoryStream(image);
   data.Position=0;var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.DecodePixelWidth=480;bitmap.StreamSource=data;bitmap.EndInit();bitmap.Freeze();
   byte[]? bytes=null;foreach(var quality in new[]{82,65,45,25}){var encoder=new JpegBitmapEncoder{QualityLevel=quality};encoder.Frames.Add(BitmapFrame.Create(bitmap));using var encoded=new MemoryStream();encoder.Save(encoded);bytes=encoded.ToArray();if(bytes.Length<=200*1024)break;}
   if(bytes==null||bytes.Length>200*1024)return "";
   lock(storage.CacheGate){ct.ThrowIfCancellationRequested();SafeFiles.Atomic(file,bytes);storage.Evict(settings.CoverLimitMb,file);}return file;
  }finally{requests.Release();}
 }
}
