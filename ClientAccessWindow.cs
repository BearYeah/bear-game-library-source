using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
namespace NovaLibrary;
public sealed class ClientAccessWindow : Window {
 readonly ClientAccessSession session;readonly TextBlock status;readonly Button retry;CancellationTokenSource lifetime=new();bool busy;
 public ClientAccessWindow(ClientAccessSession session,ClientAccessDecision decision){
  this.session=session;Title="Bear游戏库";Width=460;Height=260;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterScreen;Background=Brush("#101826");Foreground=Brush("#D4DCE9");FontFamily=new("Microsoft YaHei UI");
  var panel=new StackPanel{Margin=new Thickness(28)};Content=panel;
  panel.Children.Add(new TextBlock{Text="暂时无法使用",FontSize=23,FontWeight=FontWeights.Bold,Margin=new(0,0,0,18)});
  status=new TextBlock{Text=Message(decision),TextWrapping=TextWrapping.Wrap,FontSize=14};panel.Children.Add(new ScrollViewer{Content=status,Height=58,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
  var buttons=new WrapPanel{Margin=new Thickness(0,24,0,0)};panel.Children.Add(buttons);
  retry=Button("重试",async()=>await Check());buttons.Children.Add(retry);
  buttons.Children.Add(Button("更新网页",()=>{Process.Start(new ProcessStartInfo(UpdatePromptFlow.DownloadPage){UseShellExecute=true});return Task.CompletedTask;}));
  buttons.Children.Add(Button("退出",()=>{DialogResult=false;return Task.CompletedTask;}));
  Closed+=(_,_)=>{lifetime.Cancel();lifetime.Dispose();};
 }
 async Task Check(){
  if(busy)return;busy=true;retry.IsEnabled=false;status.Text="正在重试…";
  try{var decision=await session.Refresh(lifetime.Token);if(decision.Allowed){DialogResult=true;return;}status.Text=Message(decision);}
  catch(OperationCanceledException){}catch(Exception){status.Text="暂时无法连接服务，请检查网络后重试。";}
  finally{busy=false;retry.IsEnabled=true;}
 }
 static string Message(ClientAccessDecision decision)=>decision.Code=="connection_required"?"暂时无法连接服务，请联网后重试。":"当前暂时无法使用，请稍后重试或联系管理员。";
 static SolidColorBrush Brush(string color)=>new((Color)ColorConverter.ConvertFromString(color));
 static Button Button(string text,Func<Task> action){var button=new Button{Content=text,Padding=new Thickness(16,8,16,8),Margin=new Thickness(0,0,10,0),Background=Brush("#254350"),Foreground=Brush("#D4DCE9"),BorderBrush=Brush("#486272")};button.Click+=async(_,_)=>await action();return button;}
}
