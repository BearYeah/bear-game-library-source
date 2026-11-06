using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace NovaLibrary;
public partial class MainWindow {
 List<string> VerifyClientIdentityUi(){
  var checks=new List<string>();void Check(bool ok,string text){if(!ok)throw new InvalidOperationException(text);checks.Add(text);}
  ChangeView("settings");
  Check(DeviceIdLabel.Text=="联网后生成"&&!CopyDeviceIdButton.IsEnabled&&FindName("HardwareIdBox")==null,"settings show no hardware identifier and no copyable device number before registration");
  var identityFile=Path.Combine(storage.Root,"downloads","server-device.json");Directory.CreateDirectory(Path.GetDirectoryName(identityFile)!);
  File.WriteAllText(identityFile,JsonSerializer.Serialize(new{token="bear-quota-fixture"}));
  var original=ClientPolicyService.CurrentHwid;
  try{
   ClientPolicyService.CurrentHwid=new string('e',64);ChangeView("library");ChangeView("settings");
   Check(DeviceIdLabel.Text=="d3b620f4fa4b"&&CopyDeviceIdButton.IsEnabled,"settings expose the existing quota device's 12-character server label without exposing its token");
   ClientPolicyService.CurrentHwid=new string('f',64);RefreshDeviceId();
   Check(DeviceIdLabel.Text=="d3b620f4fa4b","quota device number stays independent of the hardware fingerprint");
  }finally{ClientPolicyService.CurrentHwid=original;}
  int requests=0;var decision=new ClientAccessDecision{Allowed=false,Code="hardware_blocked",Message="fixture private implementation detail"};
  var session=new ClientAccessSession(new string('e',64),new RegistryClientAccessState(),ct=>{requests++;return Task.FromResult(decision);},()=>1000);
  var gate=new ClientAccessWindow(session,decision);
  try{
   gate.RaiseEvent(new RoutedEventArgs(LoadedEvent));
   gate.Measure(new Size(460,260));gate.Arrange(new Rect(0,0,460,260));gate.UpdateLayout();
   var texts=UiElements(gate).OfType<TextBlock>().Select(t=>t.Text).ToArray();
   Check(requests==0&&!UiElements(gate).OfType<TextBox>().Any()&&!texts.Any(t=>t.Contains("指纹")||t.Contains(session.Hwid)||t.Contains(decision.Message)),"unavailable dialog uses the existing result without repeating a request or displaying hardware and policy details");
  }finally{gate.Close();}
  var surface=(FrameworkElement)Content;surface.Measure(new Size(1460,900));surface.Arrange(new Rect(0,0,1460,900));surface.UpdateLayout();SettingsPanel.ScrollToBottom();surface.UpdateLayout();
  var bitmap=new RenderTargetBitmap(1460,900,96,96,PixelFormats.Pbgra32);bitmap.Render(surface);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var output=File.Create(Path.Combine(Path.GetDirectoryName(storage.Root)!,"settings-device-id.png")))encoder.Save(output);
  ChangeView("library");return checks;
 }
 static IEnumerable<DependencyObject> UiElements(DependencyObject root){
  yield return root;for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var child in UiElements(VisualTreeHelper.GetChild(root,i)))yield return child;
 }
}
