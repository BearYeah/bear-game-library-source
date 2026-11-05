using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;
namespace NovaLibrary;

public class GameItem : INotifyPropertyChanged {
 uint sourceId; bool enabled; string name=""; string coverPath=""; string kind="game";
 public uint AppId {get;set;}
 public uint SourceId {get=>sourceId;set{sourceId=value;Changed();Changed(nameof(MappingLabel));}}
 public string Name {get=>name;set{name=value;Changed();}}
 public string Kind {get=>kind;set{kind=value;Changed();Changed(nameof(TypeLabel));}}
 public bool Enabled {get=>enabled;set{enabled=value;Changed();Changed(nameof(StateLabel));}}
 public string CoverUrl {get;set;}="";
 public string CoverPath {get=>coverPath;set{coverPath=value;Changed();Changed(nameof(Cover));}}
 public bool IsFree {get;set;}
 public uint ParentId {get;set;}
 [JsonIgnore] public string TypeLabel=>Kind=="dlc"?"DLC":Kind=="demo"?"试玩":Kind=="tool"?"工具":"游戏";
 [JsonIgnore] public string MappingLabel=>SourceId>0?$"替代 ID  {SourceId}":"勾选自动分配 ID";
 [JsonIgnore] public string StateLabel=>Enabled?"已启用":"未启用";
 [JsonIgnore] public BitmapSource? Cover {get {try{
  if(!File.Exists(CoverPath))return null;
  var b=new BitmapImage();b.BeginInit();b.CacheOption=BitmapCacheOption.OnLoad;b.UriSource=new Uri(Path.GetFullPath(CoverPath));b.DecodePixelWidth=400;b.EndInit();b.Freeze();return b;
 }catch{return null;}}}
 public event PropertyChangedEventHandler? PropertyChanged;
 void Changed([CallerMemberName]string? n=null)=>PropertyChanged?.Invoke(this,new(n));
 public GameItem Copy()=>new(){AppId=AppId,SourceId=SourceId,Name=Name,Kind=Kind,Enabled=Enabled,CoverUrl=CoverUrl,CoverPath=CoverPath,IsFree=IsFree,ParentId=ParentId};
}
public class Candidate {
 public uint AppId {get;set;} public string Name {get;set;}="";
 public string Origin {get;set;}="manual";
 [JsonIgnore] public string OriginLabel=>Origin=="builtin"?"内置基础条目":Origin=="store"?"商店免费目录":"自定义";
 public bool Confirmed {get;set;} public bool? IsFree {get;set;}
 [JsonIgnore] public string Label=>$"{AppId}  ·  {(string.IsNullOrEmpty(Name)?"候选条目":Name)}";
 [JsonIgnore] public string State=>Confirmed?"已确认可用":"待确认";
}
public class Settings {
 public string SteamPath {get;set;}="";
 public string Mode {get;set;}="families";
 public string Proxy {get;set;}="";
 public bool LocalSni {get;set;}=true;
 public int CoverLimitMb {get;set;}=200;
 public string DownloadLibrary {get;set;}="";
 public string DownloadLanguage {get;set;}="schinese";
 public bool DownloadUseProxy {get;set;}=false;
 public bool Motion {get;set;}=true;
 public bool AutoCheckUpdates {get;set;}=false;
 public bool AutoDownloadUpdates {get;set;}=false;
}
public class LibraryState {
 public Settings Settings {get;set;}=new();
 public List<GameItem> Games {get;set;}=[];
 public List<Candidate> Pool {get;set;}=[];
 public int CatalogRevision {get;set;}
 public List<uint> ExcludedCandidateIds {get;set;}=[];
}
public class AppInfo {
 public uint AppId {get;set;} public string Name {get;set;}="";
 public string Type {get;set;}="game";public bool IsFree {get;set;}
 public string Header {get;set;}="";public List<uint> DlcIds {get;set;}=[]; public uint ParentId {get;set;}
 public DateTime FetchedUtc {get;set;}=DateTime.UtcNow;
 public GameItem ToGame()=>new(){AppId=AppId,Name=Name,Kind=Type,IsFree=IsFree,CoverUrl=Header,ParentId=ParentId};
}
public static class Rules {
 public static uint AppId(string input){
  var match=System.Text.RegularExpressions.Regex.Match(input.Trim(),@"^(?:https?://(?:store\.steampowered\.com|steamdb\.info)/app/)?(\d+)(?:/[^?\s]*)?(?:\?[^\s]*)?$",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
  if(!match.Success||!uint.TryParse(match.Groups[1].Value,out var id)||id==0||id>999999999)throw new InvalidOperationException("请输入有效 AppID，或 Steam / SteamDB 的 app 页面链接。");
  return id;
 }
 public static void ValidateMappings(IEnumerable<GameItem> games){
  var entries=games.Where(g=>g.SourceId>0).ToList();
  if(entries.GroupBy(g=>g.AppId).Any(g=>g.Count()>1)||entries.GroupBy(g=>g.SourceId).Any(g=>g.Count()>1))throw new InvalidOperationException("目标或替代 AppID 重复，请先修改冲突条目。");
  if(entries.Any(g=>g.AppId==0||g.SourceId==0||g.AppId>999999999||g.SourceId>999999999||g.AppId==g.SourceId))throw new InvalidOperationException("映射包含无效或相同的 AppID。");
  if(games.Any(g=>g.Enabled&&g.SourceId==0))throw new InvalidOperationException("启用的游戏需要先分配替代 ID。");
 }
 public static uint Allocate(IEnumerable<Candidate> candidates,IEnumerable<GameItem> games,uint target){
  var list=games.ToList();var used=list.Where(g=>g.SourceId>0).Select(g=>g.SourceId).Concat(list.Select(g=>g.AppId)).ToHashSet();
  return candidates.FirstOrDefault(c=>c.Confirmed&&c.IsFree!=false&&c.AppId!=target&&!used.Contains(c.AppId))?.AppId
    ??throw new InvalidOperationException("免费 ID 池没有空闲条目，本次未加入。请更新免费 ID 库，或移除不再使用的游戏以释放 ID。");
 }
}
