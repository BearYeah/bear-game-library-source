using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
namespace NovaLibrary;
public sealed class ClientAccessWindow : Window {
 readonly ClientAccessSession session;readonly TextBlock status;readonly Button retry;CancellationTokenSource lifetime=new();bool busy;
 public ClientAccessWindow(ClientAccessSession session){
  this.session=session;Title="Bear游戏库 · 使用状态";Width=590;Height=380;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterScreen;Background=Brush("#101826");Foreground=Brush("#D4DCE9");FontFamily=new("Microsoft YaHei UI");
  var panel=new StackPanel{Margin=new Thickness(28)};Content=panel;
  panel.Children.Add(new TextBlock{Text="BEAR / CLIENT ACCESS",FontFamily=new("Consolas"),Foreground=Brush("#79E8F7"),FontSize=12});
  panel.Children.Add(new TextBlock{Text="正在确认使用状态",FontSize=23,FontWeight=FontWeights.Bold,Margin=new(0,15,0,15)});
  status=new TextBlock{Text="正在连接服务，请稍候…",TextWrapping=TextWrapping.Wrap,FontSize=14};panel.Children.Add(new ScrollViewer{Content=status,Height=76,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
  panel.Children.Add(new TextBlock{Text="本机指纹（可发给管理员处理）",Foreground=Brush("#8FA3BF"),FontSize=11,Margin=new(0,10,0,6)});
  var identity=new TextBox{Text=session.Hwid,IsReadOnly=true,FontFamily=new("Consolas"),FontSize=11,Padding=new Thickness(8),Background=Brush("#1B293D"),Foreground=Foreground};panel.Children.Add(identity);
  var buttons=new WrapPanel{Margin=new Thickness(0,24,0,0)};panel.Children.Add(buttons);
  retry=Button("重新检查",async()=>await Check());buttons.Children.Add(retry);
  buttons.Children.Add(Button("更新网页",()=>{Process.Start(new ProcessStartInfo(UpdatePromptFlow.DownloadPage){UseShellExecute=true});return Task.CompletedTask;}));
  buttons.Children.Add(Button("退出",()=>{DialogResult=false;return Task.CompletedTask;}));
  Loaded+=async(_,_)=>await Check();Closed+=(_,_)=>{lifetime.Cancel();lifetime.Dispose();};
 }
 async Task Check(){
  if(busy)return;busy=true;retry.IsEnabled=false;status.Text="正在连接服务，请稍候…";
  try{var decision=await session.Refresh(lifetime.Token);if(decision.Allowed){DialogResult=true;return;}status.Text=decision.Message;}
  catch(OperationCanceledException){}catch(Exception error){status.Text=error.Message;}
  finally{busy=false;retry.IsEnabled=true;}
 }
 static SolidColorBrush Brush(string color)=>new((Color)ColorConverter.ConvertFromString(color));
 static Button Button(string text,Func<Task> action){var button=new Button{Content=text,Padding=new Thickness(16,8,16,8),Margin=new Thickness(0,0,10,0),Background=Brush("#254350"),Foreground=Brush("#D4DCE9"),BorderBrush=Brush("#486272")};button.Click+=async(_,_)=>await action();return button;}
}
