using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
namespace NovaLibrary;
public partial class LanWindow : Window {
 readonly LanService service;readonly LanCoreService core;readonly LanProfile profile;readonly CancellationTokenSource lifetime=new();Process? gameProcess;bool busy;
 public LanWindow(Storage storage,Settings settings,GameItem game){
  service=new(storage);core=new(storage,settings);profile=service.Load(game,settings);InitializeComponent();GameName.Text=game.Name+" · APP "+game.AppId;
  ExeBox.Text=profile.Executable;ApiBox.Text=profile.ApiFile;ArgumentsBox.Text=profile.Arguments;NicknameBox.Text=profile.Nickname;PlayerIdBox.Text=profile.PlayerId.ToString();PortBox.Text=profile.Port.ToString();PeersBox.Text=profile.Peers;OverlayBox.IsChecked=profile.Overlay;
  LanguageBox.SelectedItem=LanguageBox.Items.OfType<ComboBoxItem>().First(i=>i.Tag.ToString()==profile.Language);
  GameHint.Text=game.AppId==413150?"星露谷物语本身支持在“合作”中通过局域网 / IP 加入。此入口也可切换 Goldberg 启动；游戏实际兼容性待验证。":"进入游戏后使用游戏内的合作 / 大厅菜单，或通过好友浮层邀请。依赖厂商专用服务器的游戏需要另行适配。";
  RefreshState();Closed+=(_,_)=>lifetime.Cancel();
 }
 void RefreshState(){CorePath.Text=core.Current;RestoreButton.IsEnabled=service.Installed(profile.AppId);ModeState.Text=service.Installed(profile.AppId)?"此游戏已启用局域网联机模式":"此游戏当前使用原始 Steam API";}
 void ReadProfile(){profile.Executable=ExeBox.Text.Trim();profile.ApiFile=ApiBox.Text.Trim();profile.Arguments=ArgumentsBox.Text;profile.Nickname=NicknameBox.Text.Trim();profile.PlayerId=ulong.Parse(PlayerIdBox.Text);profile.Port=int.Parse(PortBox.Text);profile.Peers=PeersBox.Text;profile.Overlay=OverlayBox.IsChecked==true;profile.Language=((ComboBoxItem)LanguageBox.SelectedItem).Tag.ToString()!;}
 void EnsureStopped(){if(gameProcess is {HasExited:false})throw new InvalidOperationException("请先退出本窗口启动的游戏，再修改联机文件。");}
 void Execute(Action action){try{EnsureStopped();action();RefreshState();}catch(Exception error){StatusLabel.Text=error.Message;}}
 void ChooseExecutable(object sender,RoutedEventArgs e){var dialog=new OpenFileDialog{Title="选择游戏启动程序",Filter="游戏程序|*.exe"};if(dialog.ShowDialog(this)!=true)return;ExeBox.Text=dialog.FileName;ApiBox.Text=LanService.FindApi(dialog.FileName);}
 void ChooseApi(object sender,RoutedEventArgs e){var dialog=new OpenFileDialog{Title="选择游戏使用的 Steam API",Filter="Steam API|steam_api.dll;steam_api64.dll"};if(dialog.ShowDialog(this)==true)ApiBox.Text=dialog.FileName;}
 void NewIdentity(object sender,RoutedEventArgs e)=>PlayerIdBox.Text=LanService.NewPlayerId().ToString();
 void SaveProfile(object sender,RoutedEventArgs e)=>Execute(()=>{ReadProfile();service.Save(profile);StatusLabel.Text="联机配置已保存。游戏自身的存档仍由游戏管理。";});
 void ApplyMode(object sender,RoutedEventArgs e)=>Execute(()=>{ReadProfile();service.Apply(profile,core.Current);StatusLabel.Text="联机模式已启用。原始文件已保存，可使用“恢复原游戏文件”。";});
 void StartLan(object sender,RoutedEventArgs e)=>Execute(()=>{ReadProfile();service.Apply(profile,core.Current);gameProcess=LanService.Start(profile);StatusLabel.Text="游戏已启动，请进入游戏内的合作 / 大厅菜单。";});
 void StartNormal(object sender,RoutedEventArgs e)=>Execute(()=>{ReadProfile();if(!File.Exists(profile.Executable))throw new InvalidOperationException("请先选择游戏启动程序。");if(service.Installed(profile.AppId))service.Restore(profile.AppId);service.Save(profile);gameProcess=LanService.Start(profile);StatusLabel.Text="已恢复原始 Steam API，并普通启动游戏。";});
 void RestoreMode(object sender,RoutedEventArgs e)=>Execute(()=>{service.Restore(profile.AppId);StatusLabel.Text="原始文件已恢复；保留玩家配置和模拟器记录。";});
 void BrowseLobbies(object sender,RoutedEventArgs e)=>Execute(()=>{ReadProfile();service.Apply(profile,core.Current);LanService.Browse(profile,core.Current);StatusLabel.Text="已打开大厅发现工具，请按工具提示选择本游戏的启动程序。";});
 void ImportCore(object sender,RoutedEventArgs e)=>Execute(()=>{var dialog=new OpenFolderDialog{Title="选择 Goldberg release 文件夹"};if(dialog.ShowDialog(this)==true){core.Select(dialog.FolderName);StatusLabel.Text="本地联机核心已导入。";}});
 async void DownloadCore(object sender,RoutedEventArgs e){
  if(busy)return;try{EnsureStopped();busy=true;OptionsPanel.IsEnabled=false;ActionPanel.IsEnabled=false;await core.Download(new Progress<string>(message=>StatusLabel.Text=message),lifetime.Token,CoreProxyBox.IsChecked==true);RefreshState();StatusLabel.Text="官方联机核心已就绪。";}
  catch(OperationCanceledException){StatusLabel.Text="核心下载已取消。";}catch(Exception error){StatusLabel.Text=error.Message;}
  finally{busy=false;OptionsPanel.IsEnabled=true;ActionPanel.IsEnabled=true;}
 }
 void OpenCoreSource(object sender,RoutedEventArgs e)=>Process.Start(new ProcessStartInfo(LanCoreService.SourceUrl){UseShellExecute=true});
 public void ExportPreview(string output){
  var surface=(FrameworkElement)Content;surface.Measure(new Size(730,820));surface.Arrange(new Rect(0,0,730,820));surface.UpdateLayout();var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap(730,820,96,96,System.Windows.Media.PixelFormats.Pbgra32);var background=new System.Windows.Media.DrawingVisual();using(var draw=background.RenderOpen())draw.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(14,21,36)),null,new Rect(0,0,730,820));bitmap.Render(background);bitmap.Render(surface);var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));Directory.CreateDirectory(output);using var file=File.Create(Path.Combine(output,"lan.png"));encoder.Save(file);
 }
}
