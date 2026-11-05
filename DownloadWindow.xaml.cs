using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
namespace NovaLibrary;
public partial class DownloadWindow : Window {
 readonly Storage storage;Settings settings;readonly GameItem game;readonly Action saveSettings;readonly Action ensureClosed;
 public void UpdateSettings(Settings updated)=>settings=updated;
 public string InstalledDirectory {get;private set;}="";
 public event Action<DownloadUpdate>? DownloadEvent;
 public bool IsDownloading=>downloading;
 public bool IsCommitting=>committing;
 public Task? ActiveDownload {get;private set;}
 public event Action? DownloadStarted;
 public event Action? DownloadStopped;
 public event Action? DeleteRequested;
 public string DisplayedSpeed=>DownloadSpeedLabel.Text;
 public string DisplayedProgress=>ProgressLabel.Text;
 CancellationTokenSource? cancellation;bool downloading,committing,closeWhenStopped;string gameDirectory="";
 public DownloadWindow(Storage storage,Settings settings,GameItem game,Action saveSettings,Action? ensureClosed=null){
  this.storage=storage;this.settings=settings;this.game=game;this.saveSettings=saveSettings;this.ensureClosed=ensureClosed??PatchService.EnsureClosed;InitializeComponent();GameName.Text=game.Name;AppIdLabel.Text=$"APP {game.AppId} · Windows / public";
  var libraries=new List<string>();if(settings.SteamPath.Length>0)libraries.Add(settings.SteamPath);var list=Path.Combine(settings.SteamPath,"steamapps","libraryfolders.vdf");
  if(File.Exists(list))libraries.AddRange(Regex.Matches(File.ReadAllText(list),"\"path\"\\s+\"([^\"]+)\"").Select(m=>m.Groups[1].Value.Replace("\\\\","\\")));
  LibraryBox.ItemsSource=libraries.Distinct(StringComparer.OrdinalIgnoreCase).Select(GameDownloadPaths.CommonDirectory).ToList();
  var chosen=settings.DownloadLibrary.Length>0?settings.DownloadLibrary:settings.SteamPath;LibraryBox.Text=chosen.Length>0?GameDownloadPaths.CommonDirectory(chosen):"";
  LanguageBox.SelectedItem=LanguageBox.Items.OfType<ComboBoxItem>().First(i=>i.Tag.ToString()==settings.DownloadLanguage);TransferProxyBox.SelectedIndex=settings.DownloadUseProxy?1:0;RestoreOptions();UpdatePathHint();RestoreProgress();
 }
 void RestoreOptions(){
  var job=Path.Combine(storage.Root,"downloads",game.AppId.ToString(),"job.json");if(!File.Exists(job))return;
  using var document=System.Text.Json.JsonDocument.Parse(File.ReadAllText(job));var saved=document.RootElement;
  LibraryBox.Text=GameDownloadPaths.CommonDirectory(saved.GetProperty("library").GetString()!);
  LanguageBox.SelectedItem=LanguageBox.Items.OfType<ComboBoxItem>().First(i=>i.Tag.ToString()==saved.GetProperty("language").GetString());
  JobsBox.SelectedItem=JobsBox.Items.OfType<ComboBoxItem>().First(i=>i.Tag.ToString()==saved.GetProperty("jobs").GetInt32().ToString());
  if(saved.TryGetProperty("download_proxy",out var proxy))TransferProxyBox.SelectedIndex=proxy.GetString()=="inherit"?1:0;
 }
 void UpdatePathHint(){InstallPathHint.Text=LibraryBox.Text.Length>0?$"每款游戏使用独立子目录：{LibraryBox.Text}\\<游戏目录>":"选择 Steam\\steamapps\\common 目录。";}
 void CommonPathChanged(object sender,RoutedEventArgs e){if(LibraryBox.Text.Length>0)LibraryBox.Text=GameDownloadPaths.CommonDirectory(LibraryBox.Text);UpdatePathHint();}
 void BrowseLibrary(object sender,RoutedEventArgs e){var dialog=new OpenFolderDialog{Title="选择 Steam 游戏文件目录（steamapps\\common）",InitialDirectory=LibraryBox.Text};if(dialog.ShowDialog(this)==true){LibraryBox.Text=GameDownloadPaths.CommonDirectory(dialog.FolderName);UpdatePathHint();}}
 void RestoreProgress(){
  var saved=new GameDownloadService(storage).SavedProgress(game.AppId);
  if(saved.Total>0||saved.Directory.Length>0){RenderProgress(saved);gameDirectory=saved.Directory;DestinationLabel.Text=gameDirectory;
   bool complete=saved.Event=="complete";DeleteButton.Content=complete?"移除记录":"删除任务";StageLabel.Text=complete?"已下载并登记到 Steam":"上次下载进度已恢复";StartButton.Content=complete?"开始下载":"继续下载";OpenFolderButton.IsEnabled=saved.Completed>0;}
 }
 async void StartDownload(object sender,RoutedEventArgs e)=>await BeginDownload();
 public Task BeginDownload(){if(downloading)return ActiveDownload!;ActiveDownload=DownloadCore();return ActiveDownload;}
 async Task DownloadCore(){
  try{
   var steam=PatchService.ValidateSteam(settings.SteamPath);ensureClosed();if(LibraryBox.Text.Trim().Length==0)throw new InvalidOperationException("请选择 Steam\\steamapps\\common 目录。");var library=GameDownloadPaths.LibraryRoot(LibraryBox.Text.Trim());
   settings.DownloadLibrary=library;settings.DownloadLanguage=((ComboBoxItem)LanguageBox.SelectedItem).Tag.ToString()!;settings.DownloadUseProxy=TransferProxyBox.SelectedIndex==1;saveSettings();
   InstalledDirectory="";DeleteButton.Content="删除任务";cancellation=new();downloading=true;committing=false;OptionsPanel.IsEnabled=false;StartButton.IsEnabled=false;OpenFolderButton.IsEnabled=false;PauseButton.IsEnabled=true;DeleteButton.IsEnabled=true;
   StageLabel.Text="正在恢复任务 / 准备下载引擎…";DownloadSpeedLabel.Text="等待开始传输";DownloadBar.IsIndeterminate=true;LogBox.Clear();
   var request=new GameDownloadRequest(game.AppId,game.Name,steam,library,settings.DownloadLanguage,int.Parse(((ComboBoxItem)JobsBox.SelectedItem).Tag.ToString()!),settings.Proxy,settings.LocalSni,settings.DownloadUseProxy);
   DownloadStarted?.Invoke();
   gameDirectory=await new GameDownloadService(storage).Run(request,new Progress<DownloadUpdate>(UpdateProgress),cancellation.Token);
   InstalledDirectory=gameDirectory;DeleteButton.Content="移除记录";StageLabel.Text="已下载并登记到 Steam";DownloadBar.IsIndeterminate=false;DownloadBar.Value=100;DownloadSpeedLabel.Text="下载完成";ProgressLabel.Text="重新打开 Steam 后查看游戏";OpenFolderButton.IsEnabled=true;StartButton.Content="开始下载";
  }catch(OperationCanceledException){RestoreProgress();StageLabel.Text="已暂停 · 退出后仍可继续";DownloadSpeedLabel.Text="下载速度  0 B/s";StartButton.Content="继续下载";}
  catch(Exception ex){StageLabel.Text="下载未完成";DownloadSpeedLabel.Text="下载速度  0 B/s";AppendLog(ex.Message);ProgressLabel.Text=ex.Message;StartButton.Content="继续下载";}
  finally{downloading=false;committing=false;cancellation?.Dispose();cancellation=null;OptionsPanel.IsEnabled=true;StartButton.IsEnabled=true;PauseButton.IsEnabled=false;DeleteButton.IsEnabled=true;DownloadBar.IsIndeterminate=false;DownloadStopped?.Invoke();if(closeWhenStopped)Close();}
 }
 public async Task StopForExit(){closeWhenStopped=true;if(downloading){Pause();if(ActiveDownload!=null)await ActiveDownload;}else Close();}
 void RenderProgress(DownloadUpdate update){DownloadBar.IsIndeterminate=false;DownloadBar.Value=update.Total>0?update.Completed/update.Total*100:0;DownloadSpeedLabel.Text=downloading&&update.Phase!="transfer"?"等待开始传输":$"下载速度  {Size(update.Speed)}/s";ProgressLabel.Text=$"安装进度 {Size(update.Completed)} / {Size(update.Total)} · {DownloadBar.Value:F1}%"+(downloading&&update.Connections>0?$" · {update.Connections} 并发":"");}
 void UpdateProgress(DownloadUpdate update){
  if(!downloading)return;
  if(update.Message.Length>0){StageLabel.Text=update.Message;AppendLog(update.Message);}if(update.Directory.Length>0){gameDirectory=update.Directory;DestinationLabel.Text=gameDirectory;}if(update.Event=="plan")AppendLog($"{update.Depots} 个内容包 → {update.Directory}");
  if(update.Event=="progress"){if(update.Phase=="transfer")StageLabel.Text="正在下载游戏文件…";RenderProgress(update);}
  if(update.Committing){committing=true;PauseButton.IsEnabled=false;DeleteButton.IsEnabled=false;DownloadBar.IsIndeterminate=true;}
  DownloadEvent?.Invoke(update);
 }
 static string Size(double bytes)=>bytes>=1073741824?$"{bytes/1073741824:F2} GB":bytes>=1048576?$"{bytes/1048576:F1} MB":bytes>=1024?$"{bytes/1024:F1} KB":$"{bytes:F0} B";
 void AppendLog(string line){LogBox.AppendText(line+Environment.NewLine);LogBox.ScrollToEnd();}
 void PauseDownload(object sender,RoutedEventArgs e)=>Pause();
 public void Pause(){if(!downloading||committing)return;StageLabel.Text="正在保存进度并暂停…";PauseButton.IsEnabled=false;cancellation!.Cancel();}
 void DeleteDownload(object sender,RoutedEventArgs e)=>DeleteRequested?.Invoke();
 void ExitDownload(object sender,RoutedEventArgs e)=>Close();
 void OpenGameFolder(object sender,RoutedEventArgs e)=>Process.Start(new ProcessStartInfo(gameDirectory){UseShellExecute=true});
 public void ExportPreview(string output){
  var surface=(FrameworkElement)Content;surface.Measure(new Size(660,700));surface.Arrange(new Rect(0,0,660,700));surface.UpdateLayout();
  var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap(660,700,96,96,System.Windows.Media.PixelFormats.Pbgra32);var background=new System.Windows.Media.DrawingVisual();using(var drawing=background.RenderOpen())drawing.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(14,21,36)),null,new Rect(0,0,660,700));bitmap.Render(background);bitmap.Render(surface);
  var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(output,"download.png"));encoder.Save(file);
 }
 void WindowClosing(object? sender,CancelEventArgs e){if(!downloading)return;e.Cancel=true;closeWhenStopped=true;if(!committing)Pause();}
}
