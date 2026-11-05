using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
namespace NovaLibrary;
public partial class Storage {
 public object CacheGate {get;}=new();
 public string Root {get;}
 public string Covers=>Path.Combine(Root,"cache","covers");
 public string Info=>Path.Combine(Root,"cache","metadata");
 public string Backup=>Path.Combine(Root,"backups");
 public static JsonSerializerOptions Json=>new(){WriteIndented=true};
 public Storage(string root){Root=Path.GetFullPath(root);foreach(var d in new[]{Root,Covers,Info,Backup}){SafeFiles.NoLinks(Root,d);Directory.CreateDirectory(d);}TrimBackups();}
 public FreeCatalogSnapshot Catalog {get;private set;}=new();
 List<Candidate> BuiltIn(){using var s=typeof(Storage).Assembly.GetManifestResourceStream("NovaLibrary.Payloads.pool.json");return s==null?[]:JsonSerializer.Deserialize<List<Candidate>>(s)??[];}
 void MergeCatalog(LibraryState state){
  using var s=typeof(Storage).Assembly.GetManifestResourceStream("NovaLibrary.Payloads.free-catalog.json");
  Catalog=s==null?new():JsonSerializer.Deserialize<FreeCatalogSnapshot>(s)??new();
  var file=Path.Combine(Root,"free-catalog.json");SafeFiles.NoLinks(Root,file);
  if(File.Exists(file)){try{var saved=JsonSerializer.Deserialize<FreeCatalogSnapshot>(File.ReadAllText(file));if(saved!=null&&ValidCatalog(saved)&&(!ValidCatalog(Catalog)||saved.UpdatedUtc>Catalog.UpdatedUtc))Catalog=saved;}catch(JsonException){}}
  FreeCatalog.Merge(state,BuiltIn(),Catalog);
 }
 static bool ValidCatalog(FreeCatalogSnapshot c)=>c.Complete&&c.FailedPages.Count==0&&c.ScannedRows>=c.TotalRows&&c.Entries.Count>0&&c.Entries.All(e=>e.AppId>0&&e.IsFree==true);
 public void UpdateCatalog(LibraryState state,FreeCatalogSnapshot catalog){
  if(!ValidCatalog(catalog))throw new InvalidOperationException("免费目录未完整读取，已保留原候选池。请稍后重试。");
  catalog.Entries=catalog.Entries.GroupBy(c=>c.AppId).Select(g=>g.First()).ToList();
  foreach(var c in catalog.Entries){c.Origin="store";c.Confirmed=true;}
  var file=Path.Combine(Root,"free-catalog.json");SafeFiles.NoLinks(Root,file);SafeFiles.Atomic(file,JsonSerializer.SerializeToUtf8Bytes(catalog));
  Catalog=catalog;FreeCatalog.Merge(state,BuiltIn(),Catalog);Save(state);
 }
 public LibraryState Load(){
  var file=Path.Combine(Root,"library.json");
  if(File.Exists(file)){
   try{var loaded=JsonSerializer.Deserialize<LibraryState>(File.ReadAllText(file))??throw new InvalidDataException();MergeCatalog(loaded);return loaded;}
   catch(Exception e){throw new InvalidOperationException("游戏库数据读取失败，已保留原文件。请从 data/backups 恢复备份。",e);}
  }
  using var stream=typeof(Storage).Assembly.GetManifestResourceStream("NovaLibrary.Payloads.pool.json");
  var pool=stream==null?new List<Candidate>():JsonSerializer.Deserialize<List<Candidate>>(stream)??[];
  var state=new LibraryState {Pool=pool};
  var defaults=Path.Combine(Root,"defaults.json");SafeFiles.NoLinks(Root,defaults);
  if(File.Exists(defaults))state.Settings=JsonSerializer.Deserialize<Settings>(File.ReadAllText(defaults))??new();
  state.Settings.SteamPath=DetectSteam();
  var ini=Path.Combine(state.Settings.SteamPath,"AppList","AppList.ini");
  if(!string.IsNullOrWhiteSpace(state.Settings.SteamPath)&&File.Exists(ini)){
   try{state.Games=IniConfig.Read(File.ReadAllBytes(ini));foreach(var g in state.Games){if(!state.Pool.Any(c=>c.AppId==g.SourceId))state.Pool.Add(new(){AppId=g.SourceId,Name="已使用的替代 ID",Confirmed=true});else state.Pool.First(c=>c.AppId==g.SourceId).Confirmed=true;}}
   catch{ /* Initial import can be retried explicitly; never modify the source. */ }
  }
  MergeCatalog(state);return state;
 }
 public void Save(LibraryState state){lock(BackupGate){
  var file=Path.Combine(Root,"library.json");SafeFiles.NoLinks(Root,file);
  if(File.Exists(file))File.Copy(file,Path.Combine(Backup,"library-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-ffff")+".json"),false);
  SafeFiles.Atomic(file,Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new LibraryState{Settings=state.Settings,Games=state.Games,Pool=FreeCatalog.Overrides(state.Pool).ToList(),CatalogRevision=state.CatalogRevision,ExcludedCandidateIds=state.ExcludedCandidateIds},Json)));
  TrimBackups();
 }}
 public static string DetectSteam(){
  using var k=Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
  var v=k?.GetValue("SteamPath")?.ToString();
  if(!string.IsNullOrWhiteSpace(v)&&File.Exists(Path.Combine(v,"steam.exe")))return Path.GetFullPath(v);
  foreach(var p in new[]{@"D:\Steam",@"C:\Program Files (x86)\Steam"})if(File.Exists(Path.Combine(p,"steam.exe")))return p;
  return "";
 }
 public List<GameItem> Installed(string steam){
  var file=Path.Combine(steam,"steamapps");SafeFiles.NoLinks(steam,file);
  if(!Directory.Exists(file))return [];
  var results=new List<GameItem>();
  foreach(var f in Directory.GetFiles(file,"appmanifest_*.acf")){
   SafeFiles.NoLinks(steam,f);
   var text=File.ReadAllText(f);var a=Regex.Match(text,"\"appid\"\\s*\"(\\d+)\"");var n=Regex.Match(text,"\"name\"\\s*\"([^\"]+)\"");
   if(a.Success&&uint.TryParse(a.Groups[1].Value,out var id)&&id>0)results.Add(new(){AppId=id,Name=n.Success?n.Groups[1].Value:$"App {id}"});
  }
  return results;
 }
 public long CacheBytes(){lock(CacheGate)return Directory.GetFiles(Covers,"*.jpg").Sum(f=>new FileInfo(f).Length);}
 public void ClearCovers(){lock(CacheGate){
  SafeFiles.NoLinks(Root,Covers);
  foreach(var f in Directory.GetFiles(Covers,"*.jpg")){SafeFiles.NoLinks(Root,f);File.Delete(f);}
 }
 }
 public void ClearCache(){lock(CacheGate){
  ClearCovers();SafeFiles.NoLinks(Root,Info);
  foreach(var f in Directory.GetFiles(Info,"*.json")){SafeFiles.NoLinks(Root,f);File.Delete(f);}
 }
 }
 public void Evict(int mb,string? keep=null){lock(CacheGate){
  SafeFiles.NoLinks(Root,Covers);var files=Directory.GetFiles(Covers,"*.jpg").OrderBy(File.GetLastWriteTimeUtc).ToList();
  long size=files.Sum(f=>new FileInfo(f).Length),limit=Math.Clamp(mb,10,1024)*1024L*1024L;
  foreach(var f in files){if(size<=limit)break;if(f==keep)continue;SafeFiles.NoLinks(Root,f);long n=new FileInfo(f).Length;File.Delete(f);size-=n;}
 }
 }
}
public static class SafeFiles {
 public static void NoLinks(string root,string file){
  var r=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
  var f=Path.GetFullPath(file);
  if(!string.Equals(f,r,StringComparison.OrdinalIgnoreCase)&&!f.StartsWith(r+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("文件路径超出目标目录。");
  var walk=r;
  if((Directory.Exists(walk)||File.Exists(walk))&&(File.GetAttributes(walk)&FileAttributes.ReparsePoint)!=0)throw new IOException("目标目录是链接，无法安全处理。");
  var rel=Path.GetRelativePath(r,f);
  if(rel==".")return;
  foreach(var part in rel.Split(Path.DirectorySeparatorChar)){
   walk=Path.Combine(walk,part);
   if((Directory.Exists(walk)||File.Exists(walk))&&(File.GetAttributes(walk)&FileAttributes.ReparsePoint)!=0)throw new IOException("路径包含目录链接，操作已停止。");
  }
 }
 public static void Atomic(string file,byte[] bytes){
  Directory.CreateDirectory(Path.GetDirectoryName(file)!);
  var temp=Path.Combine(Path.GetDirectoryName(file)!,".nova-"+Guid.NewGuid().ToString("N")+".tmp");
  try{File.WriteAllBytes(temp,bytes);File.Move(temp,file,true);}finally{if(File.Exists(temp))File.Delete(temp);}
 }
 public static string Hash(byte[] bytes)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
 public static string HashFile(string file)=>Hash(File.ReadAllBytes(file));
}
public static class IniConfig {
 static readonly Regex Entry=new(@"^\s*(?<disabled>#)?\s*(?<old>\d+)\s*=\s*(?<new>\d+)\s*(?:[;#].*)?$");
 public static string Decode(byte[] bytes){
  if(bytes.Length>=2&&bytes[0]==0xff&&bytes[1]==0xfe)return Encoding.Unicode.GetString(bytes,2,bytes.Length-2);
  if(bytes.Length>=2&&bytes[0]==0xfe&&bytes[1]==0xff)return Encoding.BigEndianUnicode.GetString(bytes,2,bytes.Length-2);
  try{return new UTF8Encoding(false,true).GetString(bytes).TrimStart('\uFEFF');}
  catch{Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);return Encoding.GetEncoding(936).GetString(bytes);}
 }
 public static List<GameItem> Read(byte[] bytes){
  var list=new List<GameItem>();bool section=false;
  foreach(var line in Decode(bytes).Split('\n')){
   var s=line.Trim();if(s.StartsWith('[')){section=s.Equals("[AppList]",StringComparison.OrdinalIgnoreCase);continue;}
   if(!section||s.StartsWith(';'))continue;var m=Entry.Match(s);if(!m.Success)continue;
   var source=Rules.AppId(m.Groups["old"].Value);var target=Rules.AppId(m.Groups["new"].Value);
   list.Add(new(){AppId=target,SourceId=source,Name=$"App {target}",Enabled=!m.Groups["disabled"].Success});
  }
  Rules.ValidateMappings(list);return list;
 }
 public static byte[] Write(byte[]? existing,IEnumerable<GameItem> games){
  var entries=games.ToList();Rules.ValidateMappings(entries);
  var original=existing==null?"":Decode(existing);
  var lines=original.Replace("\r\n","\n").Split('\n');var output=new List<string>();bool inside=false,found=false;
  var sectionLines=new List<string>();
  void AddMappings(){output.Add("; Managed by NovaLibrary — 编辑器自动备份");foreach(var g in entries.Where(g=>g.SourceId>0).OrderBy(g=>g.SourceId))output.Add($"{(g.Enabled?"":"#")}{g.SourceId} = {g.AppId}");}
  foreach(var line in lines){
   var s=line.Trim();
   if(s.StartsWith('[')){
    if(inside){AddMappings();inside=false;}
    output.Add(line);if(s.Equals("[AppList]",StringComparison.OrdinalIgnoreCase)){inside=true;found=true;}
    continue;
   }
   if(!inside){output.Add(line);continue;}
   if(Entry.IsMatch(s)||s.StartsWith("; Managed by NovaLibrary"))continue;
   output.Add(line);
  }
  if(inside)AddMappings();
  if(!found){output.Add("[AppList]");AddMappings();}
  var text=string.Join("\r\n",output).Trim()+"\r\n";
  return Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text)).ToArray();
 }
}