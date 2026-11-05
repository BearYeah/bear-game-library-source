using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
namespace NovaLibrary;
public partial class MainWindow : Window {
 readonly Storage storage;readonly PatchService patch;readonly LibraryState library;
 SteamStore store;CancellationTokenSource network=new(),detailWork=new();
 ObservableCollection<GameItem> visible=new();List<GameItem> dlcs=[];
 readonly HashSet<uint> chosenDlcs=[];GameItem? selected;
 readonly Dictionary<uint,GameItem> missingDlcParents=[];
 string view="library",libraryQuery="",onlineQuery="";bool busy,switchingQuery,steamOperation;readonly bool preview;
 readonly List<GameItem> searchResults=[];readonly SteamLifecycle steamLifecycle;
 readonly CancellationTokenSource updateLifetime=new();bool updateBusy,preparingUpdate,applyingUpdate,installUpdateRequested;PendingUpdate? pendingUpdate;SignedRelease? availableUpdate;
 public MainWindow(Storage storage,LibraryState library,bool preview=false){
  this.storage=storage;this.library=library;this.preview=preview;patch=new(storage);steamLifecycle=new();
  store=new(storage,library.Settings);InitializeComponent();Cards.ItemsSource=visible;PoolGrid.ItemsSource=library.Pool;DownloadTasks.ItemsSource=downloads;
  SteamPathBox.Text=library.Settings.SteamPath;ProxyBox.Text=library.Settings.Proxy;CacheLimitBox.Text=library.Settings.CoverLimitMb.ToString();MotionBox.IsChecked=library.Settings.Motion;
  ModeBox.SelectedIndex=library.Settings.Mode=="families"?1:0;
  AutoCheckUpdatesBox.IsChecked=library.Settings.AutoCheckUpdates;AutoDownloadUpdatesBox.IsChecked=library.Settings.AutoDownloadUpdates;
  LocalSniBox.IsChecked=library.Settings.LocalSni;
  VersionLabel.Text=$"by lostlybear    //    DESKTOP {UpdateTrust.Current.ToString(3)} · 测试候选";
  if(preview)PreviewLabel.Text="界面预览 · 测试目录";
  Loaded+=async(_,_)=>{Refresh();Animate();if(!preview){Storage.TrimDownloadConfigBackups(library.Settings.SteamPath);UpdateInstaller.Cleanup(storage);await CheckCompatibilityOnLaunch();ResumeDownloadedUpdate();if(library.Settings.AutoCheckUpdates&&pendingUpdate==null)_=CheckAppUpdates(true);if(library.Games.Count>0)_=WarmCovers(library.Games.ToList(),network.Token);}};
  Closing+=AppClosing;
  Closed+=(_,_)=>{updateLifetime.Cancel();network.Cancel();detailWork.Cancel();store.Dispose();};
 }
 void Notify(string message,bool error=false){StatusLabel.Text=message;StatusLabel.ToolTip=message;StatusLabel.Foreground=new SolidColorBrush(error?Color.FromRgb(245,164,177):Color.FromRgb(160,203,202));}
 async Task Action(Func<Task> work){
  if(busy){Notify("当前操作尚未结束，请稍候。");return;}
  busy=true;BusyBar.Visibility=Visibility.Visible;SetEditing(false);
  try{await work();}
  catch(OperationCanceledException){Notify("查询已取消或超时，可重试，或在设置里检查代理。",true);NetworkState.Text="查询超时";}
  catch(Exception e){Notify(Friendly(e),true);}
  finally{busy=false;BusyBar.Visibility=Visibility.Collapsed;SetEditing(true);RefreshStatus();}
 }
 void SetEditing(bool enabled){CardsPanel.IsEnabled=enabled;PoolPanel.IsEnabled=enabled;SettingsPanel.IsEnabled=enabled;DetailPanel.IsEnabled=enabled;SearchBar.IsEnabled=enabled;ConfigActions.IsEnabled=enabled;}
 static string Friendly(Exception e){
  if(e is HttpRequestException){for(Exception? inner=e;inner!=null;inner=inner.InnerException)if(inner is System.ComponentModel.Win32Exception w&&w.NativeErrorCode==unchecked((int)0x8009030e))return "Windows 安全连接初始化失败。请从桌面直接双击 EXE 再试；如仍失败，请检查系统 TLS 服务。";return "联网查询失败。请检查代理连接，或在设置中填写手动代理。";}
  return e.Message;
 }
 void SaveLocal(){storage.Save(library);}
 void RefreshStatus(){PatchState.Text=patch.Status(library.Settings.SteamPath);UpdateCompatibilityHint();CacheState.Text=$"封面缓存  {storage.CacheBytes()/1024d/1024:F1} / {library.Settings.CoverLimitMb} MB";}
 void UpdateCompatibilityHint(){var mode=(ModeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString()??"families";var purpose=mode=="families"?"隐士模式：用于家庭共享场景。":"隐身模式：仍可能触发家庭共享占用限制。";CompatibilityHint.Text=purpose+"\n"+(patch.LastCompatibility?.Mode==mode?patch.LastCompatibility.Summary:"安装前检查客户端结构；检测通过不代表家庭共享限制已解除。");}
 void ModeSelectionChanged(object sender,SelectionChangedEventArgs e){if(CompatibilityHint!=null)UpdateCompatibilityHint();}
 void RefreshCards(){
  var q=libraryQuery.Trim();
  var list=view=="library"?LibraryCards(q):searchResults.Select(g=>library.Games.FirstOrDefault(actual=>actual.AppId==g.AppId)??g).ToList();
  if(view=="search")foreach(var g in list){g.LibraryDlcs=[];g.LibraryDlcTotal=0;}
  if(view is "library" or "search"){visible.Clear();foreach(var g in list)visible.Add(g);}
  CountLabel.Text=view=="library"?$"{visible.Count:00} TITLES · {library.Games.Count(g=>g.Kind=="dlc")} DLC":$"{visible.Count:00} TITLES";EmptyState.Visibility=visible.Count==0&&CardsPanel.Visibility==Visibility.Visible?Visibility.Visible:Visibility.Collapsed;
 }
 List<GameItem> LibraryCards(string query){
  static bool Matches(GameItem g,string q)=>q.Length==0||g.Name.Contains(q,StringComparison.OrdinalIgnoreCase)||g.AppId.ToString().Contains(q);
  var grouped=library.Games.Where(g=>g.Kind=="dlc"&&g.ParentId>0&&g.ParentId!=g.AppId).GroupBy(g=>g.ParentId).ToDictionary(g=>g.Key,g=>g.ToList());
  var parents=library.Games.Where(g=>g.Kind!="dlc"||g.ParentId==0||g.ParentId==g.AppId).ToList();
  foreach(var id in grouped.Keys){
   if(parents.Any(g=>g.AppId==id))continue;
   var parent=library.Games.FirstOrDefault(g=>g.AppId==id);
   if(parent==null&&!missingDlcParents.TryGetValue(id,out parent)){parent=new GameItem{AppId=id,Name=$"游戏本体 · APP {id}"};missingDlcParents[id]=parent;}
   parents.Add(parent);
  }
  var cards=new List<GameItem>();
  foreach(var parent in parents){
   var children=grouped.GetValueOrDefault(parent.AppId)??[];var parentMatches=Matches(parent,query);
   var matches=parentMatches?children:children.Where(g=>Matches(g,query)).ToList();
   parent.LibraryDlcTotal=children.Count;parent.LibraryDlcs=matches;
   if(!parentMatches&&matches.Count==0)continue;
   if(query.Length>0&&matches.Count>0)parent.LibraryDlcsExpanded=true;
   cards.Add(parent);
  }
  return cards;
 }
 void QueryChanged(object sender,TextChangedEventArgs e){
  if(switchingQuery||Cards==null)return;
  if(view=="library"){libraryQuery=QueryBox.Text;RefreshCards();}else if(view=="search")onlineQuery=QueryBox.Text;
 }
 void ShowSearchResults(IEnumerable<GameItem> results){searchResults.Clear();searchResults.AddRange(results);RefreshCards();}
 void Refresh(){
  RefreshCards();
  PoolGrid.CommitEdit(DataGridEditingUnit.Cell,true);PoolGrid.CommitEdit(DataGridEditingUnit.Row,true);RefreshPool();RefreshStatus();
 }
 void ChangeView(string next){
  network.Cancel();network=new CancellationTokenSource();detailWork.Cancel();
  view=next;CardsPanel.Visibility=next is "library" or "search"?Visibility.Visible:Visibility.Collapsed;
  PoolPanel.Visibility=next=="pool"?Visibility.Visible:Visibility.Collapsed;SettingsPanel.Visibility=next=="settings"?Visibility.Visible:Visibility.Collapsed;
  DownloadsPanel.Visibility=next=="downloads"?Visibility.Visible:Visibility.Collapsed;
  DetailPanel.Visibility=next=="downloads"?Visibility.Collapsed:Visibility.Visible;Grid.SetColumnSpan(ContentArea,next=="downloads"?2:1);
  SearchRow.Height=new GridLength(next=="downloads"?8:52);ConfigActions.Visibility=next=="downloads"?Visibility.Collapsed:Visibility.Visible;
  SearchBar.Visibility=next is "library" or "search"?Visibility.Visible:Visibility.Collapsed;
  PageTitle.Text=next switch{"downloads"=>"下载管理","search"=>"发现下一款游戏","pool"=>"替代 ID 候选池","settings"=>"补丁与设置",_=>"我的游戏库"};
  PageKicker.Text=next switch{"downloads"=>"DOWNLOADS / TASKS","search"=>"DISCOVER / ONLINE","pool"=>"MAPPING / SOURCE POOL","settings"=>"SYSTEM / CONTROL",_=>"BEAR / COLLECTION"};
  PageSubtitle.Text=next switch{"downloads"=>"查看下载进度、速度，以及已保存的任务。","search"=>"输入名字、AppID 或链接，直接查询 Steam 公开资料。","pool"=>"一次维护候选列表，添加游戏时自动分配空闲条目。","settings"=>"安装、卸载、缓存与网络配置。",_=>"收藏热爱，让每一款游戏都有自己的位置。"};
  foreach(var nav in new[]{NavLibrary,NavSearch,NavDownloads,NavPool,NavSettings})nav.Background=new SolidColorBrush(nav.Tag?.ToString()==next?Color.FromRgb(31,47,73):Colors.Transparent);
  switchingQuery=true;
  try{if(next is "library" or "search")QueryBox.Text=next=="library"?libraryQuery:onlineQuery;}
  finally{switchingQuery=false;}
  SearchButton.Content=next=="library"?"筛选":"搜索";
  QueryBox.ToolTip=next=="library"?"筛选游戏或 DLC：名称或 AppID；DLC 结果显示在所属游戏下":"名称、AppID，或 Steam / SteamDB 商店链接";
  FreeOnly.Visibility=next=="search"?Visibility.Visible:Visibility.Collapsed;
  if(next=="search")QueryBox.Focus();
  if(next=="pool"||next=="settings")EmptyState.Visibility=Visibility.Collapsed;
  Refresh();if(next=="downloads")RefreshDownloads();
 }
 void Navigation(object sender,RoutedEventArgs e)=>ChangeView(((Button)sender).Tag?.ToString()??"library");
 void QueryKeyDown(object sender,KeyEventArgs e){if(e.Key==Key.Enter){e.Handled=true;SearchClick(sender,e);}}
 async void SearchClick(object sender,RoutedEventArgs e){
  var q=QueryBox.Text.Trim();
  if(view=="library"){libraryQuery=QueryBox.Text;RefreshCards();Notify(q.Length==0?"已显示全部本地游戏。":visible.Count==0?"游戏库没有匹配条目，查找新游戏请点击“在线搜索”。":"已筛选本地游戏库。");return;}
  if(view!="search")ChangeView("search");
  QueryBox.Text=q;
  await Action(async()=>{
   network.Cancel();network=new();var ct=network.Token;
   Notify("正在搜索 Steam 商店…");NetworkState.Text="正在连接 Steam";
   var results=await store.Search(q,FreeOnly.IsChecked==true,ct);ct.ThrowIfCancellationRequested();
   ShowSearchResults(results);
   CountLabel.Text=$"{visible.Count:00} TITLES";EmptyState.Visibility=visible.Count==0?Visibility.Visible:Visibility.Collapsed;
   NetworkState.Text="Steam 查询成功";Notify(results.Count==0?"没有找到可见的商店条目，可尝试 AppID 或英文名称。":$"找到 {results.Count} 个结果，选择卡片查看详情和 DLC。");
   _=WarmCovers(visible.ToList(),ct);
  });
 }
 async Task WarmCovers(List<GameItem> items,CancellationToken ct){
  using var workers=new SemaphoreSlim(3);
  await Task.WhenAll(items.Select(async source=>{
   bool entered=false;
   try{
    await workers.WaitAsync(ct);entered=true;
    var g=library.Games.FirstOrDefault(x=>x.AppId==source.AppId)??source;
    var cover=await store.Cover(g.AppId,g.CoverUrl,ct);ct.ThrowIfCancellationRequested();g=library.Games.FirstOrDefault(x=>x.AppId==g.AppId)??g;g.CoverPath=cover;
   }catch(OperationCanceledException){}catch{ /* Keep the placeholder until the next refresh. */ }
   finally{if(entered)workers.Release();}
  }));
  if(!ct.IsCancellationRequested){RefreshStatus();SaveLocal();}
 }
 async void GameClick(object sender,RoutedEventArgs e){
  if(((Button)sender).Tag is not GameItem g)return;
  e.Handled=true;await SelectGame(g);
 }
 async Task SelectGame(GameItem g){
  g=library.Games.FirstOrDefault(x=>x.AppId==g.AppId)??g;selected=g;detailWork.Cancel();detailWork=new();var ct=detailWork.Token;
  ShowSelected();chosenDlcs.Clear();dlcs=library.Games.Where(d=>d.Kind=="dlc"&&d.ParentId==g.AppId).Select(d=>{var copy=d.Copy();copy.Enabled=false;return copy;}).ToList();
  DlcTitle.Text=dlcs.Count>0?$"DLC / {dlcs.Count} 个已加入条目":"DLC / 商店公开列表";DlcHint.Text=dlcs.Count>0?"已显示本地 DLC，正在读取商店列表…":"正在查询游戏资料…";DlcFilter.Clear();DlcFilterChanged(DlcFilter,new TextChangedEventArgs(TextBox.TextChangedEvent,UndoAction.None));UpdateDlcCount();
  try{
   var info=await store.Details(g.AppId,ct);ct.ThrowIfCancellationRequested();
   if(selected?.AppId!=g.AppId)return;
   g=library.Games.FirstOrDefault(x=>x.AppId==g.AppId)??g;selected=g;ApplyInfo(g,info);ApplyDlcRelations(g,info);
   var known=library.Games.Where(d=>d.Kind=="dlc"&&d.ParentId==g.AppId).ToList();
   dlcs=info.DlcIds.Concat(known.Select(d=>d.AppId)).Distinct().Select(id=>{var d=known.FirstOrDefault(item=>item.AppId==id)?.Copy()??new GameItem{AppId=id,Name=$"DLC {id}"};d.Kind="dlc";d.ParentId=g.AppId;d.Enabled=chosenDlcs.Contains(id);return d;}).ToList();
   DlcTitle.Text=$"DLC / {dlcs.Count} 个条目";DlcHint.Text=info.Type=="dlc"?(g.ParentId>0?$"此 DLC 属于 APP {g.ParentId}，可返回本体管理其他 DLC。":"当前为待归属 DLC，可查看商店中的所属游戏。"):dlcs.Count==0?"商店未提供可见 DLC 列表。":"正在加载 DLC 名称，选中后可批量加入或下载。";
   DlcFilterChanged(DlcFilter,new TextChangedEventArgs(TextBox.TextChangedEvent,UndoAction.None));UpdateDlcCount();RefreshCards();ShowSelected();
   var coverTask=LoadSelectedCover(g,ct);
   int completed=0;
   await Parallel.ForEachAsync(dlcs,new ParallelOptions{MaxDegreeOfParallelism=3,CancellationToken=ct},async(d,c)=>{
    try{var i=await store.Details(d.AppId,c);await Dispatcher.InvokeAsync(()=>{d.Name=i.Name;d.CoverUrl=i.Header;d.IsFree=i.IsFree;var local=library.Games.FirstOrDefault(item=>item.AppId==d.AppId);if(local!=null){local.Name=i.Name;local.CoverUrl=i.Header;local.IsFree=i.IsFree;local.Kind="dlc";local.ParentId=g.AppId;}});}catch(OperationCanceledException){throw;}catch{}
    var count=Interlocked.Increment(ref completed);
    await Dispatcher.InvokeAsync(()=>{if(selected?.AppId==g.AppId)DlcHint.Text=$"已读取 {count}/{dlcs.Count} 项；未查询到名称的条目显示 AppID。";});
   });
   await coverTask;ct.ThrowIfCancellationRequested();DlcFilterChanged(DlcFilter,new TextChangedEventArgs(TextBox.TextChangedEvent,UndoAction.None));RefreshCards();ShowSelected();NetworkState.Text="Steam 查询成功";SaveLocal();
  }catch(OperationCanceledException){}
  catch(Exception ex){if(selected?.AppId==g.AppId){DlcHint.Text="公开资料暂时未取到，仍可手动管理 AppID。";Notify(Friendly(ex),true);NetworkState.Text="联网暂时不可用";}}
 }
 async Task LoadSelectedCover(GameItem g,CancellationToken ct){try{var cover=await store.Cover(g.AppId,g.CoverUrl,ct);ct.ThrowIfCancellationRequested();g=library.Games.FirstOrDefault(x=>x.AppId==g.AppId)??g;g.CoverPath=cover;if(selected?.AppId==g.AppId)DetailCover.Source=g.Cover;}catch(OperationCanceledException){}catch{}}
  void ApplyDlcRelations(GameItem parent,AppInfo info){
   var ids=info.DlcIds.ToHashSet();foreach(var g in library.Games.Where(g=>ids.Contains(g.AppId))){g.Kind="dlc";g.ParentId=parent.AppId;}
  }
  static void ApplyInfo(GameItem g,AppInfo i){g.Name=i.Name;g.Kind=i.Type;g.IsFree=i.IsFree;g.CoverUrl=i.Header;g.ParentId=i.ParentId>0?i.ParentId:i.Type=="dlc"?g.ParentId:0;}
 void ShowSelected(){
  if(selected==null)return;
  DetailName.Text=selected.Name;DetailMeta.Text=$"APP {selected.AppId} / {selected.TypeLabel}\n{(selected.Kind=="dlc"?selected.ParentId>0?$"所属本体 APP {selected.ParentId}":"所属游戏待查询":selected.IsFree?"商店标记免费":"价格信息以商店为准")}";ParentGameButton.Visibility=selected.Kind=="dlc"&&selected.ParentId>0?Visibility.Visible:Visibility.Collapsed;
  DetailCover.Source=selected.Cover;SourceIdBox.Text=selected.SourceId>0?selected.SourceId.ToString():"";
  AutoMappingLabel.Text=selected.SourceId>0?$"免费替代 ID / {selected.SourceId} · 已分配":"加入时自动分配免费 ID";
  AddButton.Content=library.Games.Any(g=>g.AppId==selected.AppId)?"已加入 · 启用":"加入游戏库";AddButton.IsEnabled=true;DownloadGameButton.IsEnabled=true;LanGameButton.IsEnabled=selected.Kind!="dlc";var task=new GameDownloadService(storage).Status(selected.Kind=="dlc"&&selected.ParentId>0?selected.ParentId:selected.AppId);DownloadGameButton.Content=selected.Kind=="dlc"?"↓ 下载此 DLC":task.Total>0&&task.Event!="complete"?$"↓ 继续下载 · {task.Completed/task.Total*100:F1}%":"↓ 下载 / 安装游戏";ApplyMappingButton.IsEnabled=true;RemoveButton.IsEnabled=library.Games.Any(g=>g.AppId==selected.AppId);
 }
 GameItem Bookmark(GameItem g){
  var existing=library.Games.FirstOrDefault(x=>x.AppId==g.AppId);if(existing!=null)return existing;
  var copy=g.Copy();copy.Enabled=false;copy.SourceId=0;library.Games.Add(copy);return copy;
 }
 readonly Dictionary<uint,LanWindow> lanWindows=[];
 void OpenLanGame(object sender,RoutedEventArgs e){if(selected==null)return;if(lanWindows.TryGetValue(selected.AppId,out var existing)){existing.Activate();return;}try{var window=new LanWindow(storage,library.Settings,selected.Copy()){Owner=this};var id=selected.AppId;lanWindows[id]=window;window.Closed+=(_,_)=>lanWindows.Remove(id);window.Show();}catch(Exception error){Notify(error.Message,true);}}
 void DownloadGame(object sender,RoutedEventArgs e)=>BeginSelectedDownload();
 async void OpenParentGame(object sender,RoutedEventArgs e){
  if(selected==null||selected.ParentId==0)return;var id=selected.ParentId;
  var parent=library.Games.FirstOrDefault(g=>g.AppId==id)??missingDlcParents.GetValueOrDefault(id)??new GameItem{AppId=id,Name=$"游戏本体 · APP {id}"};
  await SelectGame(parent);
 }
 void Enable(GameItem g,string? explicitId=null){
  uint old=g.SourceId;bool enabled=g.Enabled;
  try{
   g.SourceId=string.IsNullOrWhiteSpace(explicitId)?g.SourceId>0?g.SourceId:Rules.Allocate(library.Pool,library.Games,g.AppId):Rules.AppId(explicitId);
   g.Enabled=true;Rules.ValidateMappings(library.Games);
   if(!library.Pool.Any(c=>c.AppId==g.SourceId))library.Pool.Add(new(){AppId=g.SourceId,Name="手动指定候选",Confirmed=true});
  }catch{g.SourceId=old;g.Enabled=enabled;throw;}
 }
 void AddSelected(object sender,RoutedEventArgs e){
  if(selected==null)return;
  try{selected=LibraryEditor.AddAndEnable(library,[selected])[0];SaveLocal();Refresh();ShowSelected();Notify("已加入并自动分配免费 ID。点击‘保存配置并重启 Steam’使映射生效。");}
  catch(Exception ex){Notify(ex.Message,true);}
 }
 void ApplyMapping(object sender,RoutedEventArgs e){
  if(selected==null)return;
  try{if(!library.Games.Any(g=>g.AppId==selected.AppId))selected=LibraryEditor.AddAndEnable(library,[selected])[0];Enable(selected,SourceIdBox.Text);SaveLocal();Refresh();ShowSelected();Notify("映射已更新并启用，保存配置后生效。");}catch(Exception ex){Notify(ex.Message,true);}
 }
 void CardToggle(object sender,RoutedEventArgs e){
  e.Handled=true;var cb=(CheckBox)sender;if(cb.Tag is not GameItem g)return;
  try{var item=cb.IsChecked==true?LibraryEditor.AddAndEnable(library,[g])[0]:library.Games.FirstOrDefault(x=>x.AppId==g.AppId);
   if(item==null)return;if(cb.IsChecked!=true)item.Enabled=false;SaveLocal();Refresh();if(selected?.AppId==item.AppId){selected=item;ShowSelected();}Notify(item.Enabled?"已自动分配并启用，保存配置后需重启 Steam。":"已停用，保存配置后需重启 Steam。");}
  catch(Exception ex){cb.IsChecked=g.Enabled;Notify(ex.Message,true);}
 }
 void ToggleManualAdd(object sender,RoutedEventArgs e){ManualPanel.Visibility=ManualPanel.Visibility==Visibility.Visible?Visibility.Collapsed:Visibility.Visible;if(ManualPanel.Visibility==Visibility.Visible)ManualAppId.Focus();}
 void ManualAppIdKeyDown(object sender,KeyEventArgs e){if(e.Key==Key.Enter){e.Handled=true;AddManualGame(sender,e);}}
 void AddManualGame(object sender,RoutedEventArgs e){
  try{var id=Rules.AppId(ManualAppId.Text);var name=ManualName.Text.Trim();var item=new GameItem{AppId=id,Name=name.Length==0?$"App {id}":name};
   var actual=LibraryEditor.AddAndEnable(library,[item])[0];if(name.Length>0)actual.Name=name;
   SaveLocal();libraryQuery="";ChangeView("library");selected=actual;detailWork.Cancel();dlcs=[];chosenDlcs.Clear();DlcItems.ItemsSource=null;DlcTitle.Text="DLC / 商店公开列表";DlcHint.Text="离线添加完成；联网后点击游戏卡片读取 DLC 和封面。";UpdateDlcCount();ShowSelected();ManualAppId.Clear();ManualName.Clear();
   Notify($"已离线加入 APP {id}，自动分配免费 ID {actual.SourceId}。保存配置后生效。");}
  catch(Exception ex){Notify(ex.Message,true);}
 }
 void RemoveSelected(object sender,RoutedEventArgs e){
  if(selected==null)return;library.Games.RemoveAll(g=>g.AppId==selected.AppId);selected.SourceId=0;selected.Enabled=false;
  SaveLocal();Refresh();ShowSelected();Notify("已从本地游戏库移除，替代 ID 已释放。保存配置后更新 Steam 中的映射。");
 }
 void RemoveLibraryDlc(object sender,RoutedEventArgs e){
  e.Handled=true;if(((Button)sender).Tag is not GameItem g)return;library.Games.RemoveAll(item=>item.AppId==g.AppId);g.SourceId=0;g.Enabled=false;
  SaveLocal();Refresh();if(selected?.AppId==g.AppId){selected=g;ShowSelected();}Notify("已移除此 DLC，所属游戏和其他 DLC 均保留；保存配置后生效。");
 }
 void UpdateDlcCount(){DlcCountLabel.Text=$"已选 {chosenDlcs.Count} / {dlcs.Count}";AddDlcButton.IsEnabled=chosenDlcs.Count>0;DownloadDlcButton.IsEnabled=chosenDlcs.Count>0;SelectAllDlcButton.IsEnabled=dlcs.Count>0;ClearDlcButton.IsEnabled=dlcs.Count>0;}
 void DlcChecked(object sender,RoutedEventArgs e){if(((CheckBox)sender).Tag is GameItem g&&dlcs.Contains(g)){g.Enabled=true;chosenDlcs.Add(g.AppId);UpdateDlcCount();}}
 void DlcUnchecked(object sender,RoutedEventArgs e){if(((CheckBox)sender).Tag is GameItem g&&dlcs.Contains(g)){g.Enabled=false;chosenDlcs.Remove(g.AppId);UpdateDlcCount();}}
 void SelectAllDlcs(object sender,RoutedEventArgs e){bool all=((Button)sender).Tag?.ToString()=="True";LibraryEditor.SelectDlcs(dlcs,all);chosenDlcs.Clear();if(all)foreach(var d in dlcs)chosenDlcs.Add(d.AppId);UpdateDlcCount();}
 void DlcFilterChanged(object sender,TextChangedEventArgs e){
  if(DlcItems==null)return;var q=DlcFilter.Text.Trim();DlcItems.ItemsSource=dlcs.Where(g=>q.Length==0||g.Name.Contains(q,StringComparison.OrdinalIgnoreCase)||g.AppId.ToString().Contains(q)).ToList();
 }
 void AddDlcs(object sender,RoutedEventArgs e){
  try{var added=LibraryEditor.AddAndEnable(library,dlcs.Where(d=>chosenDlcs.Contains(d.AppId)));SaveLocal();Refresh();Notify($"已加入并自动分配 {added.Count} 个 DLC，保存配置后生效。");}
  catch(Exception ex){Notify(ex.Message,true);}
 }
 void RefreshPool(){
  if(PoolGrid==null)return;var q=PoolFilter?.Text.Trim()??"";PoolGrid.ItemsSource=library.Pool.Where(c=>q.Length==0||c.AppId.ToString().Contains(q)||c.Name.Contains(q,StringComparison.OrdinalIgnoreCase)).ToList();
  var active=library.Pool.Count(c=>c.Confirmed&&c.IsFree!=false);var catalog=storage.Catalog;
  CatalogLabel.Text=$"候选 {library.Pool.Count:N0} 个 · 自动分配 {active:N0} 个\n免费目录：{catalog.UpdatedUtc.ToLocalTime():yyyy-MM-dd} / {catalog.Region} · {catalog.Entries.Count:N0} 个条目";
 }
 void PoolFilterChanged(object sender,TextChangedEventArgs e)=>RefreshPool();
 async void UpdateFreeCatalog(object sender,RoutedEventArgs e){
  await Action(async()=>{network.Cancel();network=new();var ct=network.Token;Notify("正在更新公开免费游戏目录，完整读取可能需要数分钟；切换页面可取消。");
   var progress=new Progress<CatalogProgress>(p=>Notify($"免费目录 {p.Scanned:N0}/{p.Total:N0}，已识别 {p.FreeCount:N0} 个免费条目；原候选池继续保留。"));
   var snapshot=await store.FetchFreeCatalog(progress,ct);ct.ThrowIfCancellationRequested();storage.UpdateCatalog(library,snapshot);RefreshPool();Notify($"免费 ID 库已更新：{snapshot.Entries.Count:N0} 个公开免费游戏条目。");});
 }
 void AddCandidate(object sender,RoutedEventArgs e){
  try{var id=Rules.AppId(PoolId.Text);if(library.Pool.Any(c=>c.AppId==id))throw new InvalidOperationException("这个候选 ID 已在列表中。");
   library.Pool.Add(new(){AppId=id,Name=PoolName.Text.Trim(),Confirmed=true});library.ExcludedCandidateIds.Remove(id);SaveLocal();PoolId.Clear();PoolName.Clear();Refresh();Notify("自定义候选已加入自动分配池。");
  }catch(Exception ex){Notify(ex.Message,true);}
 }
 async void QueryCandidate(object sender,RoutedEventArgs e){
  if(PoolGrid.SelectedItem is not Candidate c){Notify("请先选中一个候选。");return;}
  await Action(async()=>{var info=await store.Details(c.AppId,network.Token,true);c.Name=info.Name;c.IsFree=info.IsFree;c.Confirmed=info.IsFree;SaveLocal();PoolGrid.Items.Refresh();Notify(info.IsFree?"商店标记该条目免费，已启用自动分配。":"已更新候选资料，商店未将它标记为免费。");});
 }
 void RemoveCandidate(object sender,RoutedEventArgs e){
  if(PoolGrid.SelectedItem is not Candidate c)return;
  if(library.Games.Any(g=>g.SourceId==c.AppId)){Notify("该 ID 正被游戏使用，请先解除对应映射。",true);return;}
  library.Pool.Remove(c);if(c.Origin is "builtin" or "store")library.ExcludedCandidateIds.Add(c.AppId);SaveLocal();Refresh();
 }
 void PoolEdited(object sender,DataGridCellEditEndingEventArgs e)=>Dispatcher.InvokeAsync(SaveLocal,System.Windows.Threading.DispatcherPriority.Background);
 async void RefreshMetadata(object sender,RoutedEventArgs e){network.Cancel();network=new();Notify("正在刷新当前显示游戏的资料与封面…");await WarmCovers(visible.ToList(),network.Token);Notify("刷新结束。暂时无法读取的封面将显示默认图案。");}
 void ImportInstalled(object sender,RoutedEventArgs e){
  try{var items=storage.Installed(PatchService.ValidateSteam(library.Settings.SteamPath));int n=0;foreach(var g in items){if(library.Games.Any(x=>x.AppId==g.AppId))continue;library.Games.Add(g);n++;}SaveLocal();ChangeView("library");Notify($"已导入 {n} 个已安装游戏，默认未启用映射。可点击“刷新资料”读取封面。");}catch(Exception ex){Notify(ex.Message,true);}
 }
 void ImportIni(object sender,RoutedEventArgs e){
  var dialog=new OpenFileDialog{Filter="AppList 配置|*.ini;*.txt|所有文件|*.*",Title="导入 AppList.ini"};if(dialog.ShowDialog(this)!=true)return;
  try{
   var imported=IniConfig.Read(File.ReadAllBytes(dialog.FileName));var merged=library.Games.Select(g=>g.Copy()).ToList();
   foreach(var g in imported){merged.RemoveAll(x=>x.AppId==g.AppId);merged.Add(g);}
   Rules.ValidateMappings(merged);library.Games=merged;
   foreach(var g in imported){var c=library.Pool.FirstOrDefault(c=>c.AppId==g.SourceId);if(c==null)library.Pool.Add(new(){AppId=g.SourceId,Name="导入配置中的候选",Confirmed=true});else c.Confirmed=true;}
   SaveLocal();ChangeView("library");Notify($"已导入 {imported.Count} 个映射，原文件未改动。");
  }catch(Exception ex){Notify(ex.Message,true);}
 }
 void BrowseSteam(object sender,RoutedEventArgs e){var dialog=new OpenFolderDialog{Title="选择含 steam.exe 的目录"};if(dialog.ShowDialog(this)==true)SteamPathBox.Text=dialog.FolderName;}
 void ReadSettings(){
  if(!int.TryParse(CacheLimitBox.Text,out var limit)||limit<10||limit>1024)throw new InvalidOperationException("封面缓存上限请输入 10 至 1024 MB。");
  var settings=new Settings{SteamPath=SteamPathBox.Text.Trim(),Mode=(ModeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString()??"families",Proxy=ProxyBox.Text.Trim(),LocalSni=LocalSniBox.IsChecked==true,CoverLimitMb=limit,DownloadLibrary=library.Settings.DownloadLibrary,DownloadLanguage=library.Settings.DownloadLanguage,Motion=MotionBox.IsChecked==true,AutoCheckUpdates=AutoCheckUpdatesBox.IsChecked==true,AutoDownloadUpdates=AutoDownloadUpdatesBox.IsChecked==true};
  if(settings.SteamPath.Length>0)settings.SteamPath=PatchService.ValidateSteam(settings.SteamPath);
  var fresh=new SteamStore(storage,settings);network.Cancel();detailWork.Cancel();network=new();store.Dispose();store=fresh;library.Settings=settings;foreach(var window in downloadWindows.Values)window.UpdateSettings(settings);SaveLocal();storage.Evict(limit);Animate();
 }
 void SaveSettings(object sender,RoutedEventArgs e){try{ReadSettings();Refresh();Notify("设置已保存。");}catch(Exception ex){Notify(ex.Message,true);}}
 async void SaveConfig(object sender,RoutedEventArgs e)=>await Action(async()=>{Rules.ValidateMappings(library.Games);SaveLocal();var message=await Task.Run(()=>patch.SaveConfig(library.Settings.SteamPath,library.Games.Select(g=>g.Copy()).ToList()));Notify(message);});
 async void InstallPatch(object sender,RoutedEventArgs e)=>await Action(async()=>{ReadSettings();Notify("正在校验文件、备份并安装补丁…");var message=await Task.Run(()=>patch.Install(library.Settings.SteamPath,library.Settings.Mode,library.Games));Notify(message);});
 async void UninstallPatch(object sender,RoutedEventArgs e)=>await Action(async()=>{ReadSettings();Notify("正在备份、卸载并清理缓存…");var message=await Task.Run(()=>patch.Uninstall(library.Settings.SteamPath));ResetCoverPaths();SaveLocal();Notify(message);});
 void ResetCoverPaths(){foreach(var g in library.Games.Concat(visible).Concat(searchResults).Distinct())g.CoverPath="";DetailCover.Source=null;}
 async Task RestartSteamOperation(string operation){
  steamOperation=true;
  try{
  ReadSettings();if(operation is not ("uninstall" or "start"))Rules.ValidateMappings(library.Games);SaveLocal();
  var steam=library.Settings.SteamPath;var mode=library.Settings.Mode;var games=library.Games.Select(g=>g.Copy()).ToList();
  Action validate=operation switch{
   "install"=>()=>patch.PreflightInstall(steam,mode,games),
   "uninstall"=>()=>patch.PreflightUninstall(steam),
   "start"=>()=>patch.PreflightLaunch(steam),
   _=>()=>patch.PreflightConfig(steam,games,true)};
  var result=await steamLifecycle.Run(steam,validate,()=>Task.Run(()=>operation switch{
   "install"=>patch.Install(steam,mode,games),
   "uninstall"=>patch.Uninstall(steam),
   "start"=>"已检查启动文件。",
   _=>patch.SaveConfig(steam,games,true)}),message=>Dispatcher.Invoke(()=>Notify(message)));
  if(operation=="uninstall"){ResetCoverPaths();SaveLocal();}
  var label=operation switch{"install"=>"补丁已安装","uninstall"=>"补丁已卸载，相关缓存已清理","start"=>"启动文件已检查",_=>"配置已保存，补丁缓存已刷新"};
  Notify(label+"。"+result.RestartMessage+"\n"+result.OperationMessage,!result.Started);
  }finally{steamOperation=false;}
 }
 async void StartSteamClick(object sender,RoutedEventArgs e)=>await Action(()=>RestartSteamOperation("start"));
 async void SaveConfigRestart(object sender,RoutedEventArgs e)=>await Action(()=>RestartSteamOperation("config"));
 async void InstallPatchRestart(object sender,RoutedEventArgs e)=>await Action(()=>RestartSteamOperation("install"));
 async void UninstallPatchRestart(object sender,RoutedEventArgs e)=>await Action(()=>RestartSteamOperation("uninstall"));
 async void AppClosing(object? sender,System.ComponentModel.CancelEventArgs e){
  if(steamOperation){e.Cancel=true;Notify("Steam 操作尚未结束，请等待完成后关闭窗口。");return;}
  if(stoppingDownloads){e.Cancel=true;return;}
  if(downloadWindows.Values.Any(w=>w.IsDownloading)){
   e.Cancel=true;stoppingDownloads=true;Notify("正在保存下载进度并退出…");
   await Task.WhenAll(downloadWindows.Values.ToArray().Select(w=>w.StopForExit()));
   stoppingDownloads=false;Close();return;
  }
  if(applyingUpdate||pendingUpdate==null||AutoDownloadUpdatesBox.IsChecked!=true&&!installUpdateRequested)return;e.Cancel=true;if(preparingUpdate)return;preparingUpdate=true;
  try{SaveLocal();UpdateState.Text="正在准备安装更新，请稍候…";await UpdateInstaller.Launch(storage,pendingUpdate);applyingUpdate=true;Close();}
  catch(Exception ex){UpdateState.Text="更新未安装，原版本已保留。";Notify(ex.Message,true);pendingUpdate=null;ApplyUpdateButton.IsEnabled=false;}
  finally{preparingUpdate=false;}
 }
 async Task CheckAppUpdates(bool automatic){
  if(preview||updateBusy||pendingUpdate!=null)return;updateBusy=true;CheckUpdateButton.IsEnabled=false;DownloadUpdateButton.IsEnabled=false;
  try{
   UpdateState.Text="正在检查 GitHub 发布版本…";var update=await new UpdateService(storage,library.Settings).Check(updateLifetime.Token);
   if(update==null){UpdateState.Text=$"当前为最新版本 {UpdateTrust.Current.ToString(3)}";return;}
   availableUpdate=update.Value.Envelope;UpdateState.Text="发现新版本 "+update.Value.Release.Version;DownloadUpdateButton.IsEnabled=true;
   if(library.Settings.AutoDownloadUpdates)await DownloadAvailableUpdate();else if(!automatic)Notify("发现新版本，可点击下载更新。");
  }catch(OperationCanceledException){}catch(Exception ex){UpdateState.Text="更新检查暂不可用，可稍后重试。";if(!automatic)Notify(ex.Message,true);}
  finally{updateBusy=false;CheckUpdateButton.IsEnabled=true;DownloadUpdateButton.IsEnabled=availableUpdate!=null&&pendingUpdate==null;}
 }
 async Task DownloadAvailableUpdate(){
  if(availableUpdate==null||pendingUpdate!=null)return;DownloadUpdateButton.IsEnabled=false;UpdateState.Text="正在下载更新，期间可以继续使用游戏库…";
  pendingUpdate=await new UpdateService(storage,library.Settings).Stage(availableUpdate,updateLifetime.Token);
  UpdateState.Text="版本 "+pendingUpdate.Release.Version+" 已就绪。"+(library.Settings.AutoDownloadUpdates?"退出时自动安装。":"可点击安装已下载版本。");ApplyUpdateButton.IsEnabled=true;Notify(library.Settings.AutoDownloadUpdates?"更新已下载并通过签名与文件校验；关闭 Bear 时安装，或点击“安装已下载版本”。":"更新已下载并通过签名与文件校验，可点击“安装已下载版本”。");
 }
 async void CheckUpdateClick(object sender,RoutedEventArgs e){try{ReadSettings();await CheckAppUpdates(false);}catch(Exception ex){Notify(ex.Message,true);}}
 async void DownloadUpdateClick(object sender,RoutedEventArgs e){if(updateBusy)return;updateBusy=true;try{await DownloadAvailableUpdate();}catch(OperationCanceledException){}catch(Exception ex){UpdateState.Text="更新下载未完成，请重试。";Notify(ex.Message,true);}finally{updateBusy=false;DownloadUpdateButton.IsEnabled=availableUpdate!=null&&pendingUpdate==null;}}
 void ApplyUpdateClick(object sender,RoutedEventArgs e){if(pendingUpdate!=null){installUpdateRequested=true;Close();}}
 void UpdatePreferencesChanged(object sender,RoutedEventArgs e){library.Settings.AutoCheckUpdates=AutoCheckUpdatesBox.IsChecked==true;library.Settings.AutoDownloadUpdates=AutoDownloadUpdatesBox.IsChecked==true;try{SaveLocal();}catch(Exception ex){Notify(ex.Message,true);}}
 public void UpdateLaunchSucceeded(){UpdateState.Text="已更新至 "+UpdateTrust.Current.ToString(3);Notify("更新完成，游戏库和设置已保留。");}
 void ResumeDownloadedUpdate(){try{pendingUpdate=new UpdateService(storage,library.Settings).Ready();if(pendingUpdate!=null){UpdateState.Text="版本 "+pendingUpdate.Release.Version+" 已就绪。"+(library.Settings.AutoDownloadUpdates?"退出时自动安装。":"可点击安装已下载版本。");ApplyUpdateButton.IsEnabled=true;}}catch(Exception ex){UpdateState.Text="之前的更新未就绪，可重新检查下载。";Notify(ex.Message,true);}}
 async Task CheckCompatibilityOnLaunch(){
  string? mode;
  try{mode=patch.InstalledMode(library.Settings.SteamPath);}catch{return;}
  if(mode==null||mode=="normal")return;
  await Action(async()=>{
   var report=await Task.Run(()=>patch.CheckClient(library.Settings.SteamPath,mode));
   Notify(report.Compatible?report.Summary:"检测到已安装补丁与当前 Steam 不兼容："+report.Failure+" 可在补丁与设置中卸载、清理缓存并重启 Steam。",!report.Compatible);
  });
 }
 async void CheckCompatibility(object sender,RoutedEventArgs e)=>await Action(async()=>{
  ReadSettings();Notify("正在只读检查 Steam 关键代码及调用关系…");
  var report=await Task.Run(()=>patch.CheckClient(library.Settings.SteamPath,library.Settings.Mode));
  Notify(report.Summary+"\n报告："+patch.CompatibilityReportPath,!report.Compatible);
 });
 void OpenCompatibilityReport(object sender,RoutedEventArgs e){if(File.Exists(patch.CompatibilityReportPath))OpenUrl(patch.CompatibilityReportPath);else Notify("请先点击检测当前 Steam 兼容性。");}
 void ClearCovers(object sender,RoutedEventArgs e){try{network.Cancel();detailWork.Cancel();network=new();storage.ClearCovers();foreach(var g in library.Games)g.CoverPath="";foreach(var g in visible)g.CoverPath="";DetailCover.Source=null;SaveLocal();Refresh();Notify("封面缓存已清理，游戏库和映射仍保留。");}catch(Exception ex){Notify(ex.Message,true);}}
 async void ClearPackage(object sender,RoutedEventArgs e)=>await Action(async()=>{var message=await Task.Run(()=>patch.CleanPackageCache(library.Settings.SteamPath));Notify(message);});
 void OpenBackups(object sender,RoutedEventArgs e)=>OpenUrl(storage.Backup);
 void OpenStore(object sender,RoutedEventArgs e){if(selected!=null)OpenUrl($"https://store.steampowered.com/app/{selected.AppId}/");}
 void OpenSteamDb(object sender,RoutedEventArgs e){if(selected!=null)OpenUrl($"https://steamdb.info/app/{selected.AppId}/");}
 void OpenUrl(string url){try{Process.Start(new ProcessStartInfo(url){UseShellExecute=true});}catch(Exception ex){Notify(ex.Message,true);}}
 void TitleDrag(object sender,MouseButtonEventArgs e){if(e.ClickCount==2)Maximize(sender,e);else if(e.LeftButton==MouseButtonState.Pressed)try{DragMove();}catch(InvalidOperationException){}}
 void Minimize(object sender,RoutedEventArgs e)=>WindowState=WindowState.Minimized;
 void Maximize(object sender,RoutedEventArgs e)=>WindowState=WindowState==WindowState.Maximized?WindowState.Normal:WindowState.Maximized;
 void CloseWindow(object sender,RoutedEventArgs e)=>Close();
 public void ExportPreview(string output,int width=1460,int height=900){
  Directory.CreateDirectory(output);library.Settings.Motion=false;
  ManualPanel.Visibility=Visibility.Visible;ManualAppId.Text="281990";ManualName.Text="群星";
  selected=library.Games.FirstOrDefault();ShowSelected();
  dlcs=Enumerable.Range(0,8).Select(n=>new GameItem{AppId=(uint)(100000+n),Name=n==0?"Utopia · 乌托邦":"DLC "+n,Kind="dlc"}).ToList();DlcTitle.Text="DLC / 8 个公开条目";DlcHint.Text="预览示例 · 全选会包含筛选隐藏的条目";DlcItems.ItemsSource=dlcs;SelectAllDlcs(new Button{Tag="True"},new RoutedEventArgs());DlcFilter.Text="Utopia";
  foreach(var screen in new[]{"library","settings","pool","downloads"}){
   ChangeView(screen);
   var surface=(FrameworkElement)Content;
   surface.Measure(new Size(width,height));surface.Arrange(new Rect(0,0,width,height));surface.UpdateLayout();
   var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap(width,height,96,96,System.Windows.Media.PixelFormats.Pbgra32);
   bitmap.Render(surface);
   var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
   using var file=File.Create(Path.Combine(output,screen+".png"));encoder.Save(file);
  }
 }
 void Animate(){
  var transform=(TranslateTransform)ScanLine.RenderTransform;
  transform.BeginAnimation(TranslateTransform.YProperty,null);
  if(library.Settings.Motion)transform.BeginAnimation(TranslateTransform.YProperty,new DoubleAnimation(-100,650,TimeSpan.FromSeconds(9)){RepeatBehavior=RepeatBehavior.Forever});
 }
}
