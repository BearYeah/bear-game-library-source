using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
namespace NovaLibrary;
public sealed class DownloadTaskItem(uint appId,string name) : INotifyPropertyChanged {
 public uint AppId {get;}=appId;
 public string Name {get;}=name;
 public DownloadUpdate Update {get;private set;}=new();
 public bool Running {get;private set;}
 bool committing;
 public double Percent=>Update.Total>0?Update.Completed/Update.Total*100:0;
 public bool Indeterminate=>Running&&(Update.Total==0||committing);
 public string Status=>Update.Event=="complete"?"下载完成":committing?"正在写入安装记录":Running?(Update.Phase=="transfer"?"正在下载":"准备下载"):Update.Event=="error"?"下载中断":"已暂停";
 public string Stage=>Update.Message;
 public string DeleteLabel=>Update.Event=="complete"?"移除记录":"删除任务";
 public bool CanDelete=>!committing;
 public string Progress=>$"{Size(Update.Completed)} / {Size(Update.Total)} · {Percent:F1}%";
 public string Speed=>Running&&!committing?(Update.Phase=="transfer"?$"{Size(Update.Speed)}/s":"等待传输"):"";
 public string Directory=>Update.Directory;
 public string ActionLabel=>Running?"查看任务":Update.Event=="complete"?"查看详情":"继续下载";
 public bool CanPause=>Running&&!committing;
 public bool CanOpen=>System.IO.Directory.Exists(Directory);
 public event PropertyChangedEventHandler? PropertyChanged;
 public void Set(DownloadUpdate update,bool running,bool committing=false){Update=update;Running=running;this.committing=committing;PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(""));}
 static string Size(double bytes)=>bytes>=1073741824?$"{bytes/1073741824:F2} GB":bytes>=1048576?$"{bytes/1048576:F1} MB":bytes>=1024?$"{bytes/1024:F1} KB":$"{bytes:F0} B";
}
public partial class MainWindow {
 readonly ObservableCollection<DownloadTaskItem> downloads=[];
 readonly Dictionary<uint,DownloadWindow> downloadWindows=[];
 bool stoppingDownloads;
 DownloadTaskItem DownloadRow(GameItem game){var row=downloads.FirstOrDefault(t=>t.AppId==game.AppId);if(row!=null)return row;row=new(game.AppId,game.Name);downloads.Add(row);return row;}
 void RefreshDownloads(){
  var folder=Path.Combine(storage.Root,"downloads");
  if(Directory.Exists(folder))foreach(var task in Directory.EnumerateDirectories(folder)){
   if(!uint.TryParse(Path.GetFileName(task),out var appId))continue;
   var job=Path.Combine(task,"job.json");if(!File.Exists(job))continue;
   using var document=JsonDocument.Parse(File.ReadAllText(job));var name=document.RootElement.GetProperty("name").GetString()!;
   var row=DownloadRow(new GameItem{AppId=appId,Name=name});
   if(!downloadWindows.TryGetValue(appId,out var window)||!window.IsDownloading)row.Set(new GameDownloadService(storage).Status(appId),false);
  }
  UpdateDownloadsSummary();
 }
 void UpdateDownloadsSummary(){
  DownloadsEmpty.Visibility=downloads.Count==0?Visibility.Visible:Visibility.Collapsed;
  if(view=="downloads")CountLabel.Text=$"{downloads.Count:00} TASKS";
 }
 DownloadWindow OpenDownload(GameItem game){
  if(downloadWindows.TryGetValue(game.AppId,out var existing)){existing.Show();existing.Activate();return existing;}
  var row=DownloadRow(game);var service=new GameDownloadService(storage);row.Set(service.Status(game.AppId),false);
  var dialog=new DownloadWindow(storage,library.Settings,game,()=>{Bookmark(game);SaveLocal();}){Owner=this};downloadWindows.Add(game.AppId,dialog);
  dialog.DownloadStarted+=()=>{row.Set(service.Status(game.AppId),true);ChangeView("downloads");dialog.Hide();};
  dialog.DownloadEvent+=update=>{var saved=row.Update;if(update.Total>0){saved.Completed=update.Completed;saved.Total=update.Total;saved.Speed=update.Speed;}if(update.Directory.Length>0)saved.Directory=update.Directory;saved.Event=update.Event;if(update.Message.Length>0)saved.Message=update.Message;if(update.Phase.Length>0)saved.Phase=update.Phase;row.Set(saved,true,dialog.IsCommitting);};
  dialog.DownloadStopped+=()=>{row.Set(service.Status(game.AppId),false);if(dialog.InstalledDirectory.Length>0){RefreshCards();Notify($"{game.Name} 下载完成，已写入 Steam 安装记录。");}UpdateDownloadsSummary();};
  dialog.DeleteRequested+=async()=>await RemoveDownload(row);
  dialog.Closed+=(_,_)=>{downloadWindows.Remove(game.AppId);row.Set(service.Status(game.AppId),false);UpdateDownloadsSummary();if(selected?.AppId==game.AppId)ShowSelected();};
  dialog.Show();UpdateDownloadsSummary();return dialog;
 }
 async void OpenDownloadTask(object sender,RoutedEventArgs e){
  var row=(DownloadTaskItem)((Button)sender).Tag;var dialog=OpenDownload(new GameItem{AppId=row.AppId,Name=row.Name});
  if(!row.Running&&row.Update.Event!="complete")await dialog.BeginDownload();
 }
 void PauseDownloadTask(object sender,RoutedEventArgs e){var row=(DownloadTaskItem)((Button)sender).Tag;downloadWindows[row.AppId].Pause();}
 async void RemoveDownloadTask(object sender,RoutedEventArgs e)=>await RemoveDownload((DownloadTaskItem)((Button)sender).Tag);
 async Task RemoveDownload(DownloadTaskItem row){
  try{
   var service=new GameDownloadService(storage);var info=service.RemovalInfo(row.AppId);bool deleteFiles=false;
   if(info.Installed){if(MessageBox.Show(this,$"移除“{row.Name}”的下载记录？\n游戏文件和 Steam 安装记录会保留。","移除下载记录",MessageBoxButton.OKCancel,MessageBoxImage.Question)!=MessageBoxResult.OK)return;}
   else{
    var choice=MessageBox.Show(this,$"删除“{row.Name}”的下载任务？\n\n是：停止下载，删除未完成的游戏目录及续传记录。\n否：只移除任务，保留游戏文件。\n取消：保留任务。\n\n游戏目录：{(info.Directory.Length>0?info.Directory:"尚未创建")}","删除下载任务",MessageBoxButton.YesNoCancel,MessageBoxImage.Warning);
    if(choice==MessageBoxResult.Cancel)return;deleteFiles=choice==MessageBoxResult.Yes;
   }
   IsEnabled=false;
   if(downloadWindows.TryGetValue(row.AppId,out var window))await window.StopForExit();
   var removed=await Task.Run(()=>service.Remove(row.AppId,deleteFiles));downloads.Remove(row);UpdateDownloadsSummary();if(selected?.AppId==row.AppId)ShowSelected();Notify(removed?"任务及未完成的游戏目录已删除。":"下载记录已移除，游戏文件已保留。");
  }catch(Exception ex){Notify(ex.Message,true);}finally{IsEnabled=true;}
 }
 void OpenDownloadDirectory(object sender,RoutedEventArgs e){var row=(DownloadTaskItem)((Button)sender).Tag;Process.Start(new ProcessStartInfo(row.Directory){UseShellExecute=true});}
}
