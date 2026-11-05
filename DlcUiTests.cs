using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace NovaLibrary;

public partial class MainWindow {
 static MainWindow CreateDlcFixture(string folder){
  var state=new LibraryState{CatalogRevision=1,Settings=new(){Motion=false,AutoCheckUpdates=false,AutoDownloadUpdates=false}};
  state.Games.Add(new(){AppId=281990,Name="Stellaris · 群星",Enabled=true,SourceId=90});
  state.Games.AddRange(Enumerable.Range(0,350).Select(n=>new GameItem{AppId=(uint)(600000+n),Name=n==0?"Utopia · 乌托邦":$"群星扩展内容 {n:000}",Kind="dlc",ParentId=281990,Enabled=true,SourceId=(uint)(500000+n)}));
  state.Games.Add(new(){AppId=900001,Name="尚未识别所属游戏的 DLC",Kind="dlc",Enabled=true,SourceId=700001});
  state.Games.Add(new(){AppId=900002,Name="只有 DLC 已加入的扩展",Kind="dlc",ParentId=777000,SourceId=700002});
  state.Pool=state.Games.Select(g=>new Candidate{AppId=g.SourceId,Confirmed=true,IsFree=true}).ToList();
  var fixture=new MainWindow(new Storage(folder),state,true);fixture.store.Dispose();fixture.store=new SteamStore(fixture.storage,state.Settings,new DlcFixtureHttp());
  fixture.ChangeView("library");return fixture;
 }

 public List<string> VerifyDlcGrouping(){
  var checks=new List<string>();void Check(bool ok,string label){if(!ok)throw new InvalidOperationException(label);checks.Add(label);}
  var fixture=CreateDlcFixture(Path.Combine(storage.Root,"dlc-grouping-check"));
  try{
   var state=fixture.library;var parent=state.Games.Single(g=>g.AppId==281990);var first=state.Games.Single(g=>g.AppId==600000);var sibling=state.Games.Single(g=>g.AppId==600001);
   Check(fixture.visible.Count==3&&fixture.visible.Single(g=>g.AppId==281990).LibraryDlcs.Count==350&&!parent.LibraryDlcsExpanded,"350 saved DLCs share one initially collapsed parent card without removing saved entries");
   Check(state.Games.Count==353&&ReferenceEquals(parent.LibraryDlcs[0],first)&&state.Games.All(g=>g.SourceId>0),"DLC grouping retains original game records and assigned mappings");
   Check(fixture.visible.Any(g=>g.AppId==900001)&&first.ParentId==281990,"DLCs with unknown parents remain visible for later identification");
   var placeholder=fixture.visible.Single(g=>g.AppId==777000);
   Check(placeholder.LibraryDlcs.Single().AppId==900002&&!placeholder.Enabled&&placeholder.SourceId==0&&!state.Games.Any(g=>g.AppId==777000),"missing parent appears as an inactive view placeholder without adding or enabling a mapping");
   fixture.QueryBox.Text="Utopia";
   Check(fixture.visible.Count==1&&fixture.visible[0]==parent&&parent.LibraryDlcsExpanded&&parent.LibraryDlcs.Single()==first&&parent.LibraryDlcTotal==350,"searching a DLC name reveals its parent and expands only matching children");
   fixture.GameClick(new Button{Tag=first},new RoutedEventArgs(Button.ClickEvent));
   Check(fixture.selected==first&&fixture.ParentGameButton.Visibility==Visibility.Visible&&fixture.DownloadGameButton.IsEnabled&&fixture.DownloadGameButton.Content.ToString()=="↓ 下载此 DLC","a grouped DLC opens its own detail view and retains download and parent navigation controls");
   fixture.OpenParentGame(fixture,new RoutedEventArgs(Button.ClickEvent));
   Check(fixture.selected==parent,"DLC detail can return to the saved parent without adding a game");
   fixture.CardToggle(new CheckBox{Tag=first,IsChecked=false},new RoutedEventArgs(Button.ClickEvent));
   Check(!first.Enabled&&first.SourceId==500000&&parent.Enabled&&parent.SourceId==90&&sibling.Enabled&&sibling.SourceId==500001,"disabling one child retains its reserved mapping and leaves parent and sibling enabled");
   fixture.CardToggle(new CheckBox{Tag=first,IsChecked=true},new RoutedEventArgs(Button.ClickEvent));
   Check(first.Enabled&&first.SourceId==500000&&parent.SourceId==90&&sibling.SourceId==500001,"re-enabling a child reuses its own mapping without changing the parent or sibling");
   fixture.RemoveLibraryDlc(new Button{Tag=first},new RoutedEventArgs(Button.ClickEvent));fixture.QueryBox.Clear();
   Check(state.Games.Count==352&&!state.Games.Any(g=>g.AppId==600000)&&parent.LibraryDlcs.Count==349&&parent.Enabled&&parent.SourceId==90&&sibling.Enabled&&sibling.SourceId==500001,"removing one child changes only that saved DLC and refreshes the folded collection");
   fixture.SaveLocal();var reloaded=fixture.storage.Load();
   Check(reloaded.Games.Count==352&&reloaded.Games.Single(g=>g.AppId==600001).ParentId==281990&&reloaded.Games.Single(g=>g.AppId==900002).ParentId==777000&&!reloaded.Games.Any(g=>g.AppId==777000),"saving and reloading retains parent relationships without persisting view placeholders");
   Check(!File.ReadAllText(Path.Combine(fixture.storage.Root,"library.json")).Contains("LibraryDlcs",StringComparison.OrdinalIgnoreCase),"presentation-only DLC collections are excluded from saved library data");
   var legacy=state.Games.Single(g=>g.AppId==900001);legacy.Kind="game";
   fixture.ApplyDlcRelations(parent,new AppInfo{AppId=parent.AppId,Type="game",DlcIds=[legacy.AppId]});fixture.RefreshCards();
   Check(legacy.Kind=="dlc"&&legacy.ParentId==parent.AppId&&legacy.Enabled&&legacy.SourceId==700001&&parent.LibraryDlcs.Contains(legacy)&&fixture.visible.All(g=>g.AppId!=legacy.AppId),"parent app DLC list immediately groups an old unclassified entry without fetching that DLC details or changing its mapping");
   fixture.SaveLocal();Check(fixture.storage.Load().Games.Single(g=>g.AppId==legacy.AppId).ParentId==parent.AppId,"DLC relationship learned from the parent app list persists without child metadata");
   return checks;
  }finally{fixture.Close();}
 }

 public void RenderDlcGrouping(string folder){
  Directory.CreateDirectory(folder);var fixture=CreateDlcFixture(Path.Combine(folder,"isolated-dlc-data"));
  try{
   fixture.PreviewLabel.Text="界面预览 · DLC 折叠与独立管理";fixture.selected=fixture.library.Games[0];fixture.ShowSelected();
   fixture.dlcs=fixture.library.Games.Where(g=>g.Kind=="dlc"&&g.ParentId==281990).Select(g=>{var copy=g.Copy();copy.Enabled=false;return copy;}).ToList();fixture.DlcItems.ItemsSource=fixture.dlcs;fixture.DlcTitle.Text="DLC / 350 个条目";fixture.DlcHint.Text="选中后批量加入或下载；点击名称查看单个 DLC。";fixture.UpdateDlcCount();
   foreach(var expanded in new[]{false,true}){
    fixture.library.Games[0].LibraryDlcsExpanded=expanded;
    var surface=(FrameworkElement)fixture.Content;surface.Measure(new Size(1460,900));surface.Arrange(new Rect(0,0,1460,900));surface.UpdateLayout();
    var bitmap=new RenderTargetBitmap(1460,900,96,96,PixelFormats.Pbgra32);bitmap.Render(surface);
    var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var output=File.Create(Path.Combine(folder,expanded?"dlc-expanded.png":"dlc-collapsed.png"));encoder.Save(output);
   }
  }finally{fixture.Close();}
 }
}

sealed class DlcFixtureHttp : HttpMessageHandler {
 protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)=>throw new HttpRequestException("Offline DLC UI fixture");
}
