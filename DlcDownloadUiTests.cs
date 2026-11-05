using System.Text.Json;
using System.Windows.Controls;
namespace NovaLibrary;

public static class DlcDownloadUiTests {
 public static List<string> Exercise(string folder){
  var checks=new List<string>();void Check(bool ok,string name){if(!ok)throw new InvalidOperationException(name);checks.Add(name);}
  Directory.CreateDirectory(folder);var steam=Path.Combine(folder,"steam");var library=Path.Combine(folder,"library");
  Directory.CreateDirectory(steam);File.WriteAllText(Path.Combine(steam,"steam.exe"),"fixture");
  var gameDirectory=Path.Combine(library,"steamapps","common","ELDEN RING");Directory.CreateDirectory(Path.Combine(gameDirectory,"Game"));
  var manifest=Path.Combine(library,"steamapps","appmanifest_1245620.acf");var original="\"AppState\"\n{\n\"appid\" \"1245620\"\n\"installdir\" \"ELDEN RING\"\n}\n";File.WriteAllText(manifest,original);
  Directory.CreateDirectory(Path.Combine(steam,"steamapps"));File.WriteAllText(Path.Combine(steam,"steamapps","libraryfolders.vdf"),"\"libraryfolders\" { \"0\" { \"path\" \""+library.Replace("\\","\\\\")+"\" } }");
  File.WriteAllText(Path.Combine(gameDirectory,"Game","base.bin"),"base");File.WriteAllText(Path.Combine(gameDirectory,"Game","dlc.partial"),"dlc");
  var storage=new Storage(Path.Combine(folder,"data"));var settings=new Settings{SteamPath=steam,DownloadLibrary=library,AutoCheckUpdates=false};
  var game=new GameItem{AppId=1245620,Name="ELDEN RING"};var dlc=new GameItem{AppId=2778580,Name="Shadow of the Erdtree · 黄金树幽影",Kind="dlc",ParentId=1245620};
  var window=new DownloadWindow(storage,settings,game,()=>{},()=>{},[dlc],[2778580],true);
  try{
   Check(window.DlcOnly&&window.SelectedDlcIds.SequenceEqual(new uint[]{2778580}),"single-DLC entry keeps the main AppID and selects only the requested DLC");
   Check(((TextBox)window.FindName("InstalledGameBox")).Text==gameDirectory,"DLC-only download locates its existing parent in another Steam library");
   ((CheckBox)window.FindName("IncludeGameBox")).IsChecked=true;
   Check(!window.DlcOnly&&window.SelectedDlcIds.Length==1,"the same DLC selection supports a combined base-and-DLC download");
   window.ConfigureDlcSelection([dlc],[2778580],true);window.ExportPreview(folder);
  }finally{window.Close();}
  var task=Path.Combine(storage.Root,"downloads","1245620");Directory.CreateDirectory(task);
  File.WriteAllText(Path.Combine(task,"job.json"),JsonSerializer.Serialize(new{appid=1245620,name="ELDEN RING",library,language="english",jobs=0,download_proxy="direct",dlc_ids=new uint[]{2778580},dlc_only=true,install_dir=gameDirectory}));
  File.WriteAllText(Path.Combine(task,"status.json"),JsonSerializer.Serialize(new DownloadUpdate{Event="progress",Paused=true,Directory=gameDirectory,Completed=512,Total=1024}));
  var reopened=new DownloadWindow(storage,settings,game,()=>{},()=>{});
  try{Check(reopened.DlcOnly&&reopened.SelectedDlcIds.SequenceEqual(new uint[]{2778580})&&((TextBox)reopened.FindName("InstalledGameBox")).Text==gameDirectory,"reopening a paused DLC task restores its choices, mode and parent directory");}finally{reopened.Close();}
  var service=new GameDownloadService(storage);var info=service.RemovalInfo(1245620);
  Check(info.SharedDirectory&&info.Installed&&info.Directory==gameDirectory,"DLC-only tasks identify their destination as a shared parent directory");
  var removed=service.Remove(1245620,true);
  Check(!removed&&!Directory.Exists(task)&&File.Exists(Path.Combine(gameDirectory,"Game","base.bin"))&&File.Exists(Path.Combine(gameDirectory,"Game","dlc.partial"))&&File.ReadAllText(manifest)==original,"removing a DLC task preserves base files, downloaded DLC files and the original Steam installation record");
  return checks;
 }
}
