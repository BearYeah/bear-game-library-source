using System.Windows;
namespace NovaLibrary;
public partial class MainWindow {
 TaskCompletionSource? policyOperation;
 void CopyHardwareId(object sender,RoutedEventArgs e){if(ClientPolicyService.CurrentHwid.Length==0)return;Clipboard.SetText(ClientPolicyService.CurrentHwid);Notify("设备指纹已复制。");}
 internal void ShowHardwareId()=>HardwareIdBox.Text=ClientPolicyService.CurrentHwid;
 internal async Task SuspendForClientPolicy(){
  IsEnabled=false;Hide();
  var operation=policyOperation?.Task??Task.CompletedTask;
  await Task.WhenAll(downloadWindows.Values.ToArray().Select(w=>w.StopForExit()).Append(operation));
  foreach(var window in lanWindows.Values.ToArray())window.Close();
  SaveLocal();
 }
}
