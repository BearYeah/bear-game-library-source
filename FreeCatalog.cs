using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace NovaLibrary;
public sealed class FreeCatalogSnapshot {
 public int Version {get;set;}=1;
 public DateTime UpdatedUtc {get;set;}=DateTime.UtcNow;
 public string Source {get;set;}="https://store.steampowered.com/search/?maxprice=free";
 public string Region {get;set;}="US";
 public List<string> Sources {get;set;}=[];
 public int TotalRows {get;set;}
 public int ScannedRows {get;set;}
 public bool Complete {get;set;}
 public List<string> FailedPages {get;set;}=[];
 public List<Candidate> Entries {get;set;}=[];
}
public record CatalogProgress(int Scanned,int Total,int FreeCount);
public static class FreeCatalog {
 public static List<Candidate> Parse(string html){
  var found=new Dictionary<uint,Candidate>();
  foreach(Match row in Regex.Matches(html,"<a\\b[^>]*\\bdata-ds-appid=\"(?<id>\\d+)\"[^>]*>(?<body>.*?)</a>",RegexOptions.Singleline|RegexOptions.IgnoreCase)){
   var body=row.Groups["body"].Value;
   if(!Regex.IsMatch(body,"class=\"[^\"]*\\bdiscount_final_price\\b[^\"]*\\bfree\\b[^\"]*\"",RegexOptions.IgnoreCase))continue;
   if(Regex.IsMatch(body,"class=\"search_released[^>]*>\\s*(?:Coming Soon|To be announced)",RegexOptions.IgnoreCase))continue;
   var name=Regex.Match(body,"class=\"title\"[^>]*>(.*?)</span>",RegexOptions.Singleline|RegexOptions.IgnoreCase);
   if(!name.Success||!uint.TryParse(row.Groups["id"].Value,out var id)||id==0)continue;
   found.TryAdd(id,new Candidate{AppId=id,Name=WebUtility.HtmlDecode(Regex.Replace(name.Groups[1].Value,"<[^>]*>","")).Trim(),Confirmed=true,IsFree=true,Origin="store"});
  }
  return found.Values.ToList();
 }
 public static IEnumerable<Candidate> Overrides(IEnumerable<Candidate> pool)=>pool.Where(c=>c.Origin is not ("builtin" or "store")||!c.Confirmed||c.IsFree==false);
 public static void Merge(LibraryState state,IEnumerable<Candidate> builtIn,FreeCatalogSnapshot catalog){
  bool migrate=state.CatalogRevision<1;
  var prior=(migrate?state.Pool:Overrides(state.Pool)).GroupBy(c=>c.AppId).ToDictionary(g=>g.Key,g=>g.First());
  var excluded=state.ExcludedCandidateIds.ToHashSet();
  var all=new Dictionary<uint,Candidate>();
  foreach(var c in builtIn.Concat(catalog.Entries)){
   if(c.AppId==0||excluded.Contains(c.AppId)||all.ContainsKey(c.AppId))continue;
   var copy=new Candidate{AppId=c.AppId,Name=c.Name,IsFree=c.IsFree,Confirmed=c.Confirmed,Origin=c.Origin};
   if(prior.Remove(c.AppId,out var old)){
    if(!migrate)copy.Confirmed=old.Confirmed;if(old.IsFree==false){copy.IsFree=false;copy.Confirmed=false;}
    if(old.Name.Length>0&&!old.Name.StartsWith("模板候选")&&!old.Name.StartsWith("基础免费条目")&&old.Name!="已使用候选")copy.Name=old.Name;
   }
   all[c.AppId]=copy;
  }
  foreach(var c in prior.Values){
   if(excluded.Contains(c.AppId)||c.AppId==0)continue;
   if(string.IsNullOrEmpty(c.Origin))c.Origin="manual";
   if(migrate&&c.IsFree==true)c.Confirmed=true;
   all[c.AppId]=c;
  }
  state.Pool=all.Values.OrderBy(c=>c.Origin=="builtin"?0:c.Origin=="store"?2:1).ThenBy(c=>c.AppId).ToList();
  state.CatalogRevision=1;
 }
}
public static class LibraryEditor {
 public static List<GameItem> AddAndEnable(LibraryState state,IEnumerable<GameItem> requested){
  var requests=requested.GroupBy(g=>g.AppId).Select(g=>g.First()).ToList();
  if(requests.Count==0)return [];
  if(requests.Any(g=>g.AppId==0||g.AppId>999999999))throw new InvalidOperationException("请输入有效的目标 AppID。");
  var staged=state.Games.Select(g=>g.Copy()).ToList();
  foreach(var request in requests){
   if(staged.Any(g=>g.AppId==request.AppId))continue;
   var copy=request.Copy();copy.SourceId=0;copy.Enabled=false;staged.Add(copy);
  }
  foreach(var request in requests){
   var item=staged.First(g=>g.AppId==request.AppId);
   if(item.SourceId==0)item.SourceId=Rules.Allocate(state.Pool,staged,item.AppId);
   item.Enabled=true;
  }
  Rules.ValidateMappings(staged);
  foreach(var item in staged){
   var old=state.Games.FirstOrDefault(g=>g.AppId==item.AppId);
   if(old==null)state.Games.Add(item);
   else{old.SourceId=item.SourceId;old.Enabled=item.Enabled;}
  }
  return requests.Select(r=>state.Games.First(g=>g.AppId==r.AppId)).ToList();
 }
 public static void SelectDlcs(IEnumerable<GameItem> dlcs,bool selected){foreach(var d in dlcs)d.Enabled=selected;}
}
