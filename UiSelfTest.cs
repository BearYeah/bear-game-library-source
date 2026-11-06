using System.Text.Json;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
namespace NovaLibrary;
public static class UiSelfTest {
 public static int Run(string output){
  Directory.CreateDirectory(output);var folder=Path.Combine(Path.GetFullPath(output),"isolated-ui-data");
  var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
  var storage=new Storage(folder);var state=new LibraryState{CatalogRevision=1,Settings=new(){Motion=false},Pool=new uint[]{90,205,219}.Concat(Enumerable.Range(500000,1000).Select(n=>(uint)n)).Select(id=>new Candidate{AppId=id,Confirmed=true}).ToList()};
  var window=new MainWindow(storage,state,true);
  try{var result=window.VerifyOfflineWorkflow();window.RenderDlcGrouping(Path.Combine(output,"dlc-ui"));File.WriteAllText(Path.Combine(output,"workflow-test.json"),JsonSerializer.Serialize(new{result="pass",scope="WPF handlers exercised inside an isolated application; no desktop window shown, no Steam files modified",checks=result},Storage.Json));return 0;}
  catch(Exception e){File.WriteAllText(Path.Combine(output,"workflow-test.json"),JsonSerializer.Serialize(new{result="fail",error=e.ToString()},Storage.Json));return 1;}
  finally{window.Close();app.Shutdown();}
 }
}
public partial class MainWindow {
 public List<string> VerifyOfflineWorkflow(){
  var checks=new List<string>();void Check(bool ok,string label){if(!ok)throw new InvalidOperationException(label);checks.Add(label);}
  using var offline=new OfflineFixtureHttp();store.Dispose();store=new SteamStore(storage,library.Settings,offline);
  ChangeView("search");var first=new GameItem{AppId=281990,Name="Stellaris"};var second=new GameItem{AppId=1091500,Name="Cyberpunk"};ShowSearchResults(new[]{first,second});selected=first;
  AddSelected(this,new RoutedEventArgs(Button.ClickEvent));var added=library.Games.Single(g=>g.AppId==281990);
  Check(added.Enabled&&added.SourceId==90&&ReferenceEquals(visible[0],added),"add button automatically assigns and replaces search card with saved game");
  CardToggle(new CheckBox{Tag=second,IsChecked=true},new RoutedEventArgs(Button.ClickEvent));var toggled=library.Games.Single(g=>g.AppId==1091500);
  Check(toggled.Enabled&&toggled.SourceId==205&&ReferenceEquals(visible[1],toggled),"card checkbox adds game and allocates next unused ID");
  QueryBox.Text="Stellaris";ChangeView("library");Check(QueryBox.Text==""&&visible.Count==2,"online search text never filters the local library on return");
  QueryBox.Text="Cyberpunk";Check(visible.Count==1&&visible[0].AppId==1091500,"local library filters immediately while typing");
  QueryBox.Clear();Check(visible.Count==2,"clearing library query immediately restores all games without searching");
  SearchClick(this,new RoutedEventArgs(Button.ClickEvent));Check(view=="library"&&visible.Count==2&&offline.Requests==0,"empty local search stays in the library and performs no HTTP request");
  QueryBox.Text="no matching game";SearchClick(this,new RoutedEventArgs(Button.ClickEvent));Check(view=="library"&&visible.Count==0&&offline.Requests==0,"no local matches does not silently switch to online search");
  QueryBox.Text="Cyberpunk";ChangeView("search");Check(QueryBox.Text=="Stellaris"&&visible.Count==2,"online query and results survive library navigation independently");
  ChangeView("library");Check(QueryBox.Text=="Cyberpunk"&&visible.Count==1,"local filter is restored independently of online search");
  QueryBox.Clear();ChangeView("search");Check(QueryBox.Text=="Stellaris"&&visible.Count==2,"returning to online results does not inherit cleared local input");
  Check(SaveRestartButton.Content.ToString()=="保存配置并重启 Steam"&&InstallRestartButton.Content.ToString()=="安装补丁并重启 Steam"&&UninstallRestartButton.Content.ToString()=="卸载、清理缓存并重启 Steam","all three new restart controls are wired into the WPF layout");
  CardToggle(new CheckBox{Tag=toggled,IsChecked=false},new RoutedEventArgs(Button.ClickEvent));Check(!toggled.Enabled&&toggled.SourceId==205,"unchecking keeps ID reserved for later reactivation");
  ManualAppId.Text="https://store.steampowered.com/app/1245620/";ManualName.Text="ELDEN RING";AddManualGame(this,new RoutedEventArgs(Button.ClickEvent));
  var manual=library.Games.Single(g=>g.AppId==1245620);Check(manual.Enabled&&manual.SourceId==219&&manual.Name=="ELDEN RING"&&selected==manual,"manual AppID entry adds offline and automatically allocates");
  dlcs=Enumerable.Range(600000,350).Select(n=>new GameItem{AppId=(uint)n,Name=n==600000?"Filter match":"Hidden DLC",Kind="dlc",ParentId=manual.AppId}).ToList();DlcItems.ItemsSource=dlcs;DlcFilter.Text="Filter match";
  SelectAllDlcs(new Button{Tag="True"},new RoutedEventArgs(Button.ClickEvent));Check(chosenDlcs.Count==350&&DlcItems.Items.Count==1&&DlcCountLabel.Text=="已选 350 / 350","DLC select all includes all filtered hidden entries and updates count");
  AddDlcs(this,new RoutedEventArgs(Button.ClickEvent));Check(library.Games.Count==353&&library.Games.Where(g=>g.Kind=="dlc").All(g=>g.Enabled&&g.SourceId>0)&&library.Games.Select(g=>g.SourceId).Distinct().Count()==353,"selected DLC batch is fully added with distinct source IDs");
  SelectAllDlcs(new Button{Tag="False"},new RoutedEventArgs(Button.ClickEvent));Check(chosenDlcs.Count==0&&!AddDlcButton.IsEnabled&&library.Games.Count(g=>g.Kind=="dlc"&&g.Enabled)==350,"clear selection changes selection only");
  selected=added;RemoveSelected(this,new RoutedEventArgs(Button.ClickEvent));ManualAppId.Text="234234";ManualName.Clear();AddManualGame(this,new RoutedEventArgs(Button.ClickEvent));Check(library.Games.Single(g=>g.AppId==234234).SourceId==90,"removing a game releases its source ID for future allocation");
  Check(ModeBox.Items.OfType<ComboBoxItem>().Select(i=>i.Tag?.ToString()).SequenceEqual(new[]{"standard","families"})&&StartSteamButton.Content.ToString()!.Contains("启动"),"only standard and Families modes remain, with Steam launch controls");
  ModeBox.SelectedIndex=1;ReadSettings();Check(storage.Load().Settings.Mode=="families","Families selection persists in the existing settings format");
  var before=library.Games.Count;ManualAppId.Text="invalid";AddManualGame(this,new RoutedEventArgs(Button.ClickEvent));Check(library.Games.Count==before,"invalid manual AppID does not alter saved library");
  Check(offline.Requests==0,"all add and select operations complete with zero network requests");
  Check(AutoCheckUpdatesBox.IsChecked==false&&FindName("AutoDownloadUpdatesBox")==null&&FindName("ApplyUpdateButton")==null&&OpenUpdatePageButton.Content.ToString()=="前往蓝奏云更新","update settings offer optional startup checks and a Lanzou webpage without automatic downloads or installs");
  var download=new DownloadWindow(storage,library.Settings,new GameItem{AppId=105600,Name="Fixture download"},()=>{},()=>{});
  var jobs=(ComboBox)download.FindName("JobsBox");var transfer=(ComboBox)download.FindName("TransferProxyBox");
  Check(((ComboBoxItem)jobs.SelectedItem).Tag.ToString()=="0"&&((ComboBoxItem)transfer.SelectedItem).Tag.ToString()=="direct","new downloads default to automatic concurrency and direct game-content connections");download.Close();
  AutoCheckUpdatesBox.IsChecked=false;UpdatePreferencesChanged(this,new RoutedEventArgs());var reloaded=storage.Load();Check(!reloaded.Settings.AutoCheckUpdates,"turning off startup update checks persists without altering the library");checks.AddRange(VerifyDlcGrouping());checks.AddRange(DlcDownloadUiTests.Exercise(Path.Combine(storage.Root,"dlc-download-check")));return checks;
 }
}
sealed class OfflineFixtureHttp : HttpMessageHandler {
 public int Requests;
 protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){Requests++;throw new HttpRequestException("Fixture: proxy unavailable");}
}
