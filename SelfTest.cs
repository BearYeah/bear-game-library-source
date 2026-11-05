using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
namespace NovaLibrary;
public static class SelfTest {
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern uint GetPrivateProfileInt(string section,string key,int defaultValue,string file);
 static void Assert(bool ok,string what){if(!ok)throw new Exception(what);}
 static void Throws(Action action,string what){try{action();}catch(InvalidOperationException){return;}catch(IOException){return;}throw new Exception(what);}
 public static int Run(string output){
  var report=new List<object>();string root=Path.Combine(Path.GetFullPath(output),"fixtures-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
  int code=0;
  void Test(string name,Action body){try{body();report.Add(new{name,result="pass"});}catch(Exception e){code=1;report.Add(new{name,result="fail",error=e.Message});}}
  var storage=new Storage(Path.Combine(root,"data"));var steam=Path.Combine(root,"fake-steam");Directory.CreateDirectory(steam);File.WriteAllText(Path.Combine(steam,"steam.exe"),"fixture only; never executed");
  Directory.CreateDirectory(Path.Combine(steam,"appcache"));Directory.CreateDirectory(Path.Combine(steam,"AppList"));Directory.CreateDirectory(Path.Combine(steam,"steamapps","common"));
  var sentinel=Path.Combine(steam,"steamapps","common","KEEP.txt");File.WriteAllText(sentinel,"game data must survive");
  var cache=Path.Combine(steam,"appcache","packageinfo.vdf");var dll=Path.Combine(steam,"user32.dll");var ini=Path.Combine(steam,"AppList","AppList.ini");
  var entries=new List<GameItem>{new(){AppId=281990,SourceId=90,Enabled=true,Name="群星"}};
  var patch=new PatchService(storage,()=>{},false);
  Test("Steam and SteamDB links parse to an AppID",()=>{Assert(Rules.AppId("https://store.steampowered.com/app/281990/Stellaris/")==281990,"store link");Assert(Rules.AppId("https://steamdb.info/app/281990/")==281990,"db link");Throws(()=>Rules.AppId("https://example.invalid/app/281990"),"unexpected domain accepted");Throws(()=>Rules.AppId("0"),"zero accepted");});
  Test("duplicate source mappings are rejected",()=>Throws(()=>Rules.ValidateMappings(new[]{entries[0],new GameItem{AppId=1234,SourceId=90,Enabled=true}}),"duplicate source accepted"));
  Test("candidate allocation skips unconfirmed and occupied IDs",()=>{var pool=new[]{new Candidate{AppId=90,Confirmed=true},new Candidate{AppId=205,Confirmed=false},new Candidate{AppId=219,Confirmed=true}};Assert(Rules.Allocate(pool,entries,1234)==219,"invalid allocation");});
  Test("INI output is read by the same Windows profile API as the patch",()=>{
   var bytes=IniConfig.Write(Encoding.UTF8.GetBytes("[Other]\r\nKeep=1\r\n[AppList]\r\n#90 = \r\n"),entries);File.WriteAllBytes(ini,bytes);
   Assert(GetPrivateProfileInt("AppList","90",0,ini)==281990,"Win32 parser did not read UTF16 mapping");Assert(IniConfig.Decode(bytes).Contains("Keep=1"),"other section lost");
   var decoded=IniConfig.Read(bytes);Assert(decoded.Count==1&&decoded[0].AppId==281990,"roundtrip failed");
  });
  var originalIni=File.ReadAllBytes(ini);
  Test("process guard prevents installation before any target file is changed",()=>{
   var guarded=new PatchService(storage,()=>throw new InvalidOperationException("fixture: Steam running"),false);
   Throws(()=>guarded.Install(steam,"standard",entries),"process guard ignored");Assert(!File.Exists(dll),"DLL written despite guard");
  });
  Test("unknown client build is rejected",()=>{File.WriteAllText(Path.Combine(steam,"steamclient64.dll"),"wrong client");var protectedPatch=new PatchService(storage,()=>{},true);Throws(()=>protectedPatch.Install(steam,"standard",entries),"client compatibility guard ignored");Assert(!File.Exists(dll),"DLL written on unverified client");});
  Test("install copies verified payload, writes INI, and removes only package cache",()=>{
   File.WriteAllText(cache,"cached fixture");patch.Install(steam,"standard",entries);
   Assert(SafeFiles.HashFile(dll)==PatchService.StandardHash,"wrong payload");Assert(!File.Exists(cache),"cache remained");Assert(File.ReadAllText(sentinel)=="game data must survive","game data changed");
  });
  Test("mode switch retains initial configuration backup",()=>{patch.Install(steam,"families",entries);Assert(SafeFiles.HashFile(dll)==PatchService.FamilyHash,"SF switch failed");patch.Install(steam,"standard",entries);});
  Test("disabled mappings no longer resolve through Windows profile API",()=>{entries[0].Enabled=false;patch.SaveConfig(steam,entries);Assert(GetPrivateProfileInt("AppList","90",0,ini)==0,"disabled mapping remained active");entries[0].Enabled=true;patch.SaveConfig(steam,entries);});
  Test("uninstall removes patch, restores earlier INI and preserves games",()=>{
   File.WriteAllText(cache,"new cache");File.WriteAllText(Path.Combine(storage.Info,"281990.json"),"{}");File.WriteAllBytes(Path.Combine(storage.Covers,"281990.jpg"),[1,2,3]);patch.Uninstall(steam);
   Assert(!File.Exists(dll),"patch remained");Assert(File.ReadAllBytes(ini).SequenceEqual(originalIni),"prior configuration not restored");Assert(!File.Exists(cache),"package cache remained");Assert(storage.CacheBytes()==0,"covers remained");Assert(Directory.GetFiles(storage.Info,"*.json").Length==0,"metadata remained");Assert(File.Exists(sentinel),"game files removed");
  });
  Test("unknown user32.dll is never overwritten or deleted",()=>{File.WriteAllText(dll,"unrelated DLL");Throws(()=>patch.Install(steam,"standard",entries),"unknown DLL overwritten");Throws(()=>patch.Uninstall(steam),"unknown DLL deleted");Assert(File.ReadAllText(dll)=="unrelated DLL","unrelated bytes changed");File.Delete(dll);});
  Test("cache locked after snapshot rolls back both written DLL and INI",()=>{
   File.WriteAllText(cache,"locked cache");File.WriteAllText(ini,"[AppList]\r\n90 = 281990\r\n");var prior=File.ReadAllBytes(ini);
   using(var locked=new FileStream(cache,FileMode.Open,FileAccess.Read,FileShare.Read)){
    Throws(()=>patch.Install(steam,"standard",new[]{new GameItem{AppId=1234,SourceId=90,Enabled=true}}),"locked cache ignored");
   }
   Assert(!File.Exists(dll),"failure left new DLL");Assert(File.ReadAllBytes(ini).SequenceEqual(prior),"INI not rolled back");Assert(File.ReadAllText(cache)=="locked cache","cache bytes lost");
  });
  Test("path traversal is rejected",()=>Throws(()=>SafeFiles.NoLinks(steam,Path.Combine(root,"outside.txt")),"path escaped root"));
  Test("public metadata parser returns DLC list and free state",()=>{
   var info=SteamStore.ParseDetails(281990,"{\"281990\":{\"success\":true,\"data\":{\"name\":\"Stellaris\",\"type\":\"game\",\"is_free\":false,\"header_image\":\"https://shared.fastly.steamstatic.com/test.jpg\",\"dlc\":[1,2]}}}");
   Assert(info.DlcIds.SequenceEqual(new uint[]{1,2}),"DLC not parsed");Assert(info.Name=="Stellaris"&&!info.IsFree,"metadata wrong");
  });
  Test("Steam HTML search parser extracts app IDs, names and cover URLs",()=>{
   var results=SteamStore.ParseSearch("<a data-ds-appid=\"281990\" href=\"/app/281990/\"><img src=\"https://shared.fastly.steamstatic.com/x.jpg\"><span class=\"title\">Stellaris &amp; Test</span></a>");
   Assert(results.Count==1&&results[0].Name=="Stellaris & Test","search parse failed");
  });
  Test("cover downloader rejects unexpected hosts and schemes",()=>{Assert(!SteamStore.AllowedImage("http://127.0.0.1/test.jpg"),"internal URL accepted");Assert(!SteamStore.AllowedImage("https://steamstatic.com.attacker.invalid/test.jpg"),"untrusted host accepted");Assert(SteamStore.AllowedImage("https://shared.fastly.steamstatic.com/test.jpg"),"Steam CDN rejected");});
  Test("Steam address hints parse multiple names without importing other platforms",()=>{
   var hosts=SteamNetworkRoutes.Parse("# generated\n23.1.2.3 steamcommunity.com www.steamcommunity.com # community\n23.4.5.6 cdn.akamai.steamstatic.com\n1.2.3.4 github.com\ninvalid store.steampowered.com\n");
   Assert(hosts.Count==3&&hosts["www.steamcommunity.com"]=="23.1.2.3"&&!hosts.ContainsKey("github.com"),"wrong address hints");
  });
  Test("local SNI verifies the original domain and rejects certificate chain errors",()=>{
   using var key=System.Security.Cryptography.RSA.Create(2048);
   var request=new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=store.steampowered.com",key,System.Security.Cryptography.HashAlgorithmName.SHA256,System.Security.Cryptography.RSASignaturePadding.Pkcs1);
   var names=new System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder();names.AddDnsName("store.steampowered.com");request.CertificateExtensions.Add(names.Build());
   using var cert=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddDays(1));
   var mismatch=System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch;
   Assert(SteamSniTransport.Validate(cert,mismatch,"store.steampowered.com"),"original hostname rejected");
   Assert(!SteamSniTransport.Validate(cert,mismatch,"steamcommunity.com"),"other hostname accepted");
   Assert(!SteamSniTransport.Validate(cert,mismatch|System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors,"store.steampowered.com"),"invalid chain accepted");
  });
  Test("cover loading falls back across image nodes without a metadata request",()=>{
   using var handler=new CoverFallbackHttp();using var store=new SteamStore(storage,new Settings(),handler);
   var image=store.Cover(105600,"https://shared.fastly.steamstatic.com/missing.jpg",CancellationToken.None).GetAwaiter().GetResult();
   Assert(File.Exists(image)&&new GameItem{CoverPath=image}.Cover!=null,"fallback image not decoded");
   Assert(handler.Urls.Count==3&&handler.Urls.Last().Host=="cdn.akamai.steamstatic.com","image node fallback not used");
   var next=store.Cover(105601,"",CancellationToken.None).GetAwaiter().GetResult();
   Assert(File.Exists(next)&&handler.Urls.All(u=>!u.AbsolutePath.StartsWith("/api/")),"cover depended on metadata");
   int before=handler.Urls.Count;store.Cover(105600,"",CancellationToken.None).GetAwaiter().GetResult();Assert(handler.Urls.Count==before,"cached fallback image downloaded again");
   storage.ClearCache();
  });
  Test("cache eviction respects the configured byte budget",()=>{
   File.WriteAllBytes(Path.Combine(storage.Covers,"1.jpg"),new byte[6*1024*1024]);File.WriteAllBytes(Path.Combine(storage.Covers,"2.jpg"),new byte[6*1024*1024]);storage.Evict(10);Assert(storage.CacheBytes()<=10*1024*1024,"cache over budget");
  });
  Test("HTTP search, metadata, DLC and compressed cover cache integrate correctly",()=>{
   using var handler=new FixtureHttp();using var store=new SteamStore(storage,new Settings(),handler);
   var hits=store.Search("Stellaris",false,CancellationToken.None).GetAwaiter().GetResult();Assert(hits.Count==1,"HTTP search response not consumed");
   var info=store.Details(281990,CancellationToken.None,true).GetAwaiter().GetResult();Assert(info.DlcIds.Count==2,"DLC response lost");
   var image=store.Cover(281990,info.Header,CancellationToken.None).GetAwaiter().GetResult();
   Assert(File.Exists(image)&&new FileInfo(image).Length<=200*1024,"cover not compressed within budget");
   var g=new GameItem{CoverPath=image};Assert(g.Cover?.PixelWidth==400,"cached cover not decoded for library");
   int before=handler.Requests;store.Details(281990,CancellationToken.None).GetAwaiter().GetResult();store.Cover(281990,info.Header,CancellationToken.None).GetAwaiter().GetResult();
   Assert(handler.Requests==before,"fresh cached data made extra HTTP requests");storage.ClearCache();
  });
  Test("uninstall preserves externally changed config while clearing patch and caches",()=>{
   patch.Install(steam,"standard",entries);File.WriteAllText(ini,"[AppList]\r\n90 = 1234\r\n; changed outside app\r\n");var custom=File.ReadAllBytes(ini);
   File.WriteAllText(cache,"cache");patch.Uninstall(steam);
   Assert(!File.Exists(dll)&&!File.Exists(cache),"patch or cache remained");Assert(File.ReadAllBytes(ini).SequenceEqual(custom),"external configuration overwritten");
  });
  Test("unknown user32SF.dll blocks deletion before other files are touched",()=>{
   File.WriteAllText(Path.Combine(steam,"user32SF.dll"),"foreign SF");var original=File.ReadAllBytes(ini);
   Throws(()=>patch.Uninstall(steam),"foreign SF removed");Assert(File.Exists(Path.Combine(steam,"user32SF.dll"))&&File.ReadAllBytes(ini).SequenceEqual(original),"files changed despite guard");
  });
  Test("offline add allocates a free ID without a metadata request",()=>{
   var state=new LibraryState{Pool=[new(){AppId=90,Confirmed=true},new(){AppId=205,Confirmed=true}]};
   var game=LibraryEditor.AddAndEnable(state,[new(){AppId=281990,Name="App 281990"}])[0];Assert(game.SourceId==90&&game.Enabled&&state.Games.Count==1,"offline allocation failed");
   Assert(ReferenceEquals(game,LibraryEditor.AddAndEnable(state,[new(){AppId=281990}])[0])&&state.Games.Count==1&&game.SourceId==90,"duplicate add changed mapping");
  });
  Test("allocation reserves disabled mappings and skips all target IDs",()=>{
   var state=new LibraryState{Pool=new uint[]{90,205,219,310}.Select(id=>new Candidate{AppId=id,Confirmed=true}).ToList(),Games=[new(){AppId=999,SourceId=90,Enabled=false},new(){AppId=205}]};
   var game=LibraryEditor.AddAndEnable(state,[new(){AppId=219}])[0];Assert(game.SourceId==310,"target collision or disabled mapping reused");
  });
  Test("exhausted batch allocation leaves games and existing mappings unchanged",()=>{
   var old=new GameItem{AppId=1000,SourceId=90,Enabled=false};var state=new LibraryState{Games=[old],Pool=[new(){AppId=90,Confirmed=true},new(){AppId=205,Confirmed=true}]};
   Throws(()=>LibraryEditor.AddAndEnable(state,[old,new(){AppId=2000},new(){AppId=3000}]),"incomplete batch accepted");Assert(state.Games.Count==1&&old.SourceId==90&&!old.Enabled,"failure partially committed");
  });
  Test("DLC select all includes hidden rows and bulk assigns unique IDs",()=>{
   var dlc=Enumerable.Range(100000,350).Select(id=>new GameItem{AppId=(uint)id,Name=id==100000?"Visible match":"Hidden by filter",Kind="dlc",ParentId=281990}).ToList();
   var filtered=dlc.Where(d=>d.Name=="Visible match").ToList();LibraryEditor.SelectDlcs(dlc,true);Assert(filtered.Count==1&&dlc.Count(d=>d.Enabled)==350,"select all respected filter instead of complete list");
   var state=new LibraryState{Pool=Enumerable.Range(1,500).Select(id=>new Candidate{AppId=(uint)id,Confirmed=true}).ToList()};var added=LibraryEditor.AddAndEnable(state,dlc);Assert(added.Count==350&&added.All(g=>g.Enabled)&&added.Select(g=>g.SourceId).Distinct().Count()==350,"DLC batch collided");
   LibraryEditor.SelectDlcs(dlc,false);Assert(dlc.All(d=>!d.Enabled)&&state.Games.All(g=>g.Enabled),"DLC selection modified library state");
  });
  Test("free catalog excludes paid advertisements and unreleased rows",()=>{
   string Row(uint id,string price,string release)=>$"<a data-ds-appid=\"{id}\"><span class=\"title\">Name &amp; {id}</span><div class=\"search_released col\">{release}</div><div class=\"discount_final_price{price}\">0</div></a>";
   var parsed=FreeCatalog.Parse(Row(1," free","2020")+Row(2,"","2020")+Row(3," free","Coming Soon")+Row(1," free","2020"));Assert(parsed.Count==1&&parsed[0].AppId==1&&parsed[0].Name=="Name & 1"&&parsed[0].Confirmed,"free catalog parse error");
  });
  Test("version one library migrates auto pool while retaining mappings and settings",()=>{
   var folder=Path.Combine(root,"migration-data");var migratedStorage=new Storage(folder);var old=new LibraryState{Settings=new(){Proxy="socks5://127.0.0.1:10808",SteamPath=steam},Games=[new(){AppId=281990,SourceId=90,Name="Preserve name",Enabled=true}],Pool=[new(){AppId=90,Name="已使用候选",Confirmed=true},new(){AppId=205,Name="模板候选 205",Confirmed=false}]};
   File.WriteAllText(Path.Combine(folder,"library.json"),JsonSerializer.Serialize(old));var loaded=migratedStorage.Load();Assert(loaded.Games[0].SourceId==90&&loaded.Games[0].Name=="Preserve name"&&loaded.Settings.Proxy==old.Settings.Proxy,"migration changed library");Assert(loaded.Pool.First(c=>c.AppId==205).Confirmed,"legacy manual gate remained");
   LibraryEditor.AddAndEnable(loaded,[new(){AppId=123456}]);Assert(loaded.Games[0].SourceId==90,"existing source remapped");
  });
  Test("catalog updates stay compact and retain excluded and disabled candidates",()=>{
   var folder=Path.Combine(root,"compact-data");var compact=new Storage(folder);File.WriteAllText(Path.Combine(folder,"library.json"),JsonSerializer.Serialize(new LibraryState{CatalogRevision=1}));var state=compact.Load();
   var snapshot=new FreeCatalogSnapshot{Complete=true,TotalRows=1000,ScannedRows=1000,UpdatedUtc=DateTime.UtcNow.AddMinutes(1),Source="fixture",Entries=Enumerable.Range(700000,1000).Select(id=>new Candidate{AppId=(uint)id,Name="Free game",Origin="store",Confirmed=true,IsFree=true}).ToList()};
   compact.UpdateCatalog(state,snapshot);state.Pool.First(c=>c.AppId==700000).Confirmed=false;state.ExcludedCandidateIds.Add(700001);state.Pool.RemoveAll(c=>c.AppId==700001);compact.Save(state);
   Assert(new FileInfo(Path.Combine(folder,"library.json")).Length<10000,"entire catalog duplicated in user library");var loaded=compact.Load();Assert(!loaded.Pool.First(c=>c.AppId==700000).Confirmed&&!loaded.Pool.Any(c=>c.AppId==700001)&&loaded.Pool.Any(c=>c.AppId==700999),"candidate overrides lost on reload");
   Throws(()=>compact.UpdateCatalog(loaded,new(){Complete=false,TotalRows=1000,ScannedRows=10,Entries=snapshot.Entries}),"incomplete directory replaced existing snapshot");Assert(compact.Load().Pool.Any(c=>c.AppId==700999),"incomplete update lost stored catalog");
  });
  Test("catalog refresh merges both public filters and excludes paid and upcoming rows",()=>{
   using var handler=new CatalogFixtureHttp();using var store=new SteamStore(storage,new Settings(),handler);var snapshot=store.FetchFreeCatalog(null,CancellationToken.None).GetAwaiter().GetResult();
   Assert(snapshot.Complete&&snapshot.ScannedRows==151&&snapshot.TotalRows==151&&snapshot.Entries.Select(c=>c.AppId).Order().SequenceEqual(new uint[]{1,2,3})&&handler.Requests==3,"catalog pagination or merge failed");
  });
  Test("cancelling a catalog refresh stops before its next page",()=>{
   using var handler=new CatalogFixtureHttp();using var store=new SteamStore(storage,new Settings(),handler);using var cancel=new CancellationTokenSource();
   bool cancelled=false;try{store.FetchFreeCatalog(new ImmediateProgress<CatalogProgress>(_=>cancel.Cancel()),cancel.Token).GetAwaiter().GetResult();}catch(OperationCanceledException){cancelled=true;}
   Assert(cancelled&&handler.Requests==1,"cancel continued downloading pages");
  });
  Directory.CreateDirectory(output);File.WriteAllText(Path.Combine(output,"self-test.json"),JsonSerializer.Serialize(new{fixtureRoot=root,passed=report.Count(x=>JsonSerializer.Serialize(x).Contains("\"pass\"")),total=report.Count,tests=report},Storage.Json));
  return code;
 }
 public static int BuildCatalog(string folder){
  var sources=new[]{"https://store.steampowered.com/search/?maxprice=free&category1=998&cc=US","https://store.steampowered.com/search/?tags=113&category1=998&cc=US"};
  var folders=new[]{folder,Path.Combine(folder,"freeplay")};var merged=new FreeCatalogSnapshot{Complete=true,UpdatedUtc=DateTime.UtcNow,Source="Steam public search: free price + Free to Play tag",Sources=sources.ToList()};var entries=new Dictionary<uint,Candidate>();int pages=0;
  foreach(var input in folders){
   var manifest=JsonSerializer.Deserialize<FreeCatalogSnapshot>(File.ReadAllText(Path.Combine(input,"free-games-snapshot.json")))??throw new InvalidDataException("Missing manifest");
   Assert(manifest.Complete&&manifest.ScannedRows==manifest.TotalRows&&manifest.FailedPages.Count==0,"input collection incomplete");
   var parsed=new Dictionary<uint,Candidate>();
   for(int start=0;start<manifest.TotalRows;start+=100){
    using var page=JsonDocument.Parse(File.ReadAllText(Path.Combine(input,"catalog-pages",start+".json")));
    Assert(page.RootElement.GetProperty("start").GetInt32()==start,"page offset differs from requested offset");
    var html=page.RootElement.GetProperty("results_html").GetString()??"";Assert(html.Length>0,"empty page");
    foreach(var c in FreeCatalog.Parse(html)){parsed[c.AppId]=c;entries[c.AppId]=c;}pages++;
   }
   Assert(parsed.Keys.Order().SequenceEqual(manifest.Entries.Select(c=>c.AppId).Order()),"runtime parser and collection parser differ");
   merged.TotalRows+=manifest.TotalRows;merged.ScannedRows+=manifest.ScannedRows;
  }
  merged.Entries=entries.Values.OrderBy(c=>c.AppId).ToList();Assert(entries.ContainsKey(440),"Free to Play collection omitted Team Fortress 2");Assert(!entries.ContainsKey(1091500)&&!entries.ContainsKey(281990),"paid game entered free catalog");
  File.WriteAllBytes(Path.Combine(folder,"free-catalog-verified.json"),JsonSerializer.SerializeToUtf8Bytes(merged));
  File.WriteAllText(Path.Combine(folder,"catalog-validation.json"),JsonSerializer.Serialize(new{result="pass",pages,rows=merged.TotalRows,uniqueFreeApps=merged.Entries.Count,updatedUtc=merged.UpdatedUtc,sources,scope="US public released/free-price or Free to Play-tagged game rows; hidden or delisted games and account license eligibility are not exhaustively verified"},Storage.Json));return 0;
 }
 public static int PublicFixtures(string folder){
  var storage=new Storage(Path.Combine(folder,"verified-data"));var settings=new Settings();
  using var store=new SteamStore(storage,settings,new PublicFixtureHttp(folder));
  var items=store.Search("Stellaris",false,CancellationToken.None).GetAwaiter().GetResult();
  Assert(items.Count>0&&items.Any(g=>g.AppId==281990),"Live search HTML did not yield Stellaris.");
  var info=store.Details(281990,CancellationToken.None,true).GetAwaiter().GetResult();
  Assert(info.DlcIds.Count>0,"Live DLC list was not parsed.");
  var dlc=store.Details(info.DlcIds[0],CancellationToken.None,true).GetAwaiter().GetResult();
  Assert(dlc.Type=="dlc","Live DLC details were not parsed.");
  var state=new LibraryState();
  foreach(var id in new uint[]{281990,1091500,1245620,570,1145360,105600}){
   var game=store.Details(id,CancellationToken.None,true).GetAwaiter().GetResult().ToGame();
   game.CoverPath=store.Cover(id,game.CoverUrl,CancellationToken.None).GetAwaiter().GetResult();
   Assert(File.Exists(game.CoverPath)&&new FileInfo(game.CoverPath).Length<=200*1024,"Live image failed to cache.");
   state.Games.Add(game);
  }
  storage.Save(state);
  File.WriteAllText(Path.Combine(folder,"validation.json"),JsonSerializer.Serialize(new{source="Real public Steam responses fetched through verified HTTPS and HTTP proxy 10808; then replayed into executable HTTP handler",name=info.Name,searchResults=items.Count,dlcCount=info.DlcIds.Count,firstDlc=dlc.Name,covers=state.Games.Select(g=>new{g.AppId,bytes=new FileInfo(g.CoverPath).Length}),result="pass"},Storage.Json));
  return 0;
 }
 public static async Task<int> Network(string output,string? proxy=null,bool direct=false){
  BearProxy.ForceDirect=direct;
  Directory.CreateDirectory(output);var storage=new Storage(Path.Combine(output,"network-data"));var settings=new Settings{Proxy=proxy??""};var checks=new List<object>();
  using var store=new SteamStore(storage,settings);using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(65));
  try{
   await new SteamNetworkRoutes(storage,settings).Refresh(cancel.Token);
   var addresses=JsonSerializer.Deserialize<SteamRouteSnapshot>(File.ReadAllBytes(SteamNetworkRoutes.CacheFile(storage)))!;
   checks.Add(new{check="Steam address hints",result=addresses.Hosts.Count>0?"pass":"empty",count=addresses.Hosts.Count});
   var independentCover=await store.Cover(105600,"",cancel.Token);
   checks.Add(new{check="cover without store metadata",result=File.Exists(independentCover)?"pass":"unavailable",bytes=File.Exists(independentCover)?new FileInfo(independentCover).Length:0});
   var info=await store.Details(281990,cancel.Token,true);checks.Add(new{check="game details",result="pass",name=info.Name,dlcCount=info.DlcIds.Count});
   var list=await store.Search("Stellaris",false,cancel.Token);checks.Add(new{check="name search",result=list.Count>0?"pass":"empty",count=list.Count});
   if(info.DlcIds.Count>0){var dlc=await store.Details(info.DlcIds[0],cancel.Token);checks.Add(new{check="DLC details",result="pass",type=dlc.Type});}
   var cover=await store.Cover(info.AppId,info.Header,cancel.Token);checks.Add(new{check="cover cache",result=File.Exists(cover)?"pass":"unavailable",bytes=File.Exists(cover)?new FileInfo(cover).Length:0});
   File.WriteAllText(Path.Combine(output,"network-test.json"),JsonSerializer.Serialize(new{checks,compatibility=store.UsesCompatibility,publicRelay=store.UsesPublicRelay,localSni=store.UsesLocalSni,forcedDirect=direct},Storage.Json));return 0;
  }catch(Exception e){checks.Add(new{check="live Steam access",result="unavailable",reason=e.GetType().Name,detail=e.ToString()});File.WriteAllText(Path.Combine(output,"network-test.json"),JsonSerializer.Serialize(new{checks},Storage.Json));return 2;}
 }
}
sealed class FixtureHttp : System.Net.Http.HttpMessageHandler {
 readonly byte[] cover;public int Requests;
 public FixtureHttp(){
  int width=900,height=450;var pixels=new byte[width*height*4];
  for(int y=0;y<height;y++)for(int x=0;x<width;x++){int p=(y*width+x)*4;pixels[p]=(byte)(x%256);pixels[p+1]=(byte)(y%256);pixels[p+2]=110;pixels[p+3]=255;}
  var b=System.Windows.Media.Imaging.BitmapSource.Create(width,height,96,96,System.Windows.Media.PixelFormats.Bgra32,null,pixels,width*4);
  var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(b));using var m=new MemoryStream();encoder.Save(m);cover=m.ToArray();
 }
 protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request,CancellationToken cancellationToken){
  Requests++;var uri=request.RequestUri!;
  System.Net.Http.HttpContent content;
  if(uri.Host.EndsWith("steamstatic.com"))content=new System.Net.Http.ByteArrayContent(cover);
  else if(uri.AbsolutePath.StartsWith("/api/"))content=new System.Net.Http.StringContent("{\"281990\":{\"success\":true,\"data\":{\"name\":\"Stellaris\",\"type\":\"game\",\"is_free\":false,\"header_image\":\"https://shared.fastly.steamstatic.com/test.jpg\",\"dlc\":[1,2]}}}");
  else content=new System.Net.Http.StringContent("{\"results_html\":\"<a data-ds-appid=\\\"281990\\\"><span class=\\\"title\\\">Stellaris</span><img src=\\\"https://shared.fastly.steamstatic.com/test.jpg\\\"></a>\"}");
  return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=content});
 }
}

sealed class PublicFixtureHttp(string folder) : System.Net.Http.HttpMessageHandler {
 protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request,CancellationToken ct){
  var uri=request.RequestUri!;System.Net.Http.HttpContent content;
  if(uri.Host.EndsWith("steamstatic.com")){
   var m=System.Text.RegularExpressions.Regex.Match(uri.AbsolutePath,@"/apps/(\d+)/");
   if(!m.Success)throw new InvalidDataException("Unexpected Steam image route.");
   content=new System.Net.Http.ByteArrayContent(File.ReadAllBytes(Path.Combine(folder,m.Groups[1].Value+".jpg")));
  }else if(uri.AbsolutePath.StartsWith("/api/")){
   var m=System.Text.RegularExpressions.Regex.Match(uri.Query,@"appids=(\d+)");
   content=new System.Net.Http.StringContent(File.ReadAllText(Path.Combine(folder,m.Groups[1].Value+".json")));
  }else content=new System.Net.Http.StringContent(JsonSerializer.Serialize(new{results_html=File.ReadAllText(Path.Combine(folder,"search.html"))}));
  return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=content});
 }
}

sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T> {public void Report(T value)=>report(value);}
sealed class CatalogFixtureHttp : System.Net.Http.HttpMessageHandler {
 public int Requests;
 protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request,CancellationToken ct){
  Requests++;var query=request.RequestUri!.Query;bool tagged=query.Contains("tags=113"),second=query.Contains("start=100");
  string Row(int id,bool free,string released="2020")=>$"<a data-ds-appid=\"{id}\"><span class=\"title\">Free {id}</span><div class=\"search_released col\">{released}</div><div class=\"discount_final_price{(free?" free":"")}\">0</div></a>";
  string html=tagged?Row(3,true):second?Row(2,true):Row(1,true)+Row(6,false)+Row(7,true,"Coming Soon");
  return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new System.Net.Http.StringContent(JsonSerializer.Serialize(new{total_count=tagged?1:150,results_html=html}))});
 }
}

sealed class CoverFallbackHttp : DelegatingHandler {
 public List<Uri> Urls {get;}=[];
 public CoverFallbackHttp():base(new FixtureHttp()){}
 protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){
  Urls.Add(request.RequestUri!);
  if(request.RequestUri!.Host=="shared.fastly.steamstatic.com")return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
  return base.SendAsync(request,ct);
 }
}
