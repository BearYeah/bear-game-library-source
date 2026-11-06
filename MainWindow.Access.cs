using System.Windows;
namespace NovaLibrary;
public partial class MainWindow {
 TaskCompletionSource? policyOperation;
 void CopyDeviceId(object sender,RoutedEventArgs e){var id=ClientDeviceId.Read(storage);if(id.Length==0)return;Clipboard.SetText(id);Notify("设备编号已复制，可发给管理员调整下载额度。");}
 void RefreshDeviceId(){var id=ClientDeviceId.Read(storage);DeviceIdLabel.Text=id.Length>0?id:"联网后生成";CopyDeviceIdButton.IsEnabled=id.Length>0;}
 internal async Task SuspendForClientPolicy(){
  IsEnabled=false;Hide();
  var operation=policyOperation?.Task??Task.CompletedTask;
  await Task.WhenAll(downloadWindows.Values.ToArray().Select(w=>w.StopForExit()).Append(operation));
  foreach(var window in lanWindows.Values.ToArray())window.Close();
  SaveLocal();
 }
}
