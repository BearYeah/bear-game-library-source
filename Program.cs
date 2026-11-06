using System.Windows;
using System.Text.Json;
namespace NovaLibrary;
public static class Program {
 [STAThread] public static int Main(string[] args){
  if(args.Length>=2&&args[0]=="--cdn-auth")return SteamCdnSession.Run(uint.Parse(args[1])).GetAwaiter().GetResult();
  if(args.Length>=3&&args[0]=="--lan-test")return LanTests.Run(args[1],args[2]);
  if(args.Length>=3&&args[0]=="--lan-native-peer")return LanTests.Peer(args[1],args[2]);
  if(args.Length>=2&&args[0]=="--lan-prepare-core")return LanTests.PrepareCore(args[1]).GetAwaiter().GetResult();
  if(args.Length>=2&&args[0]=="--lan-launch-child")return LanTests.LaunchChild(args[1]);
  if(args.Length>=2&&args[0]=="--direct-network-test")return SelfTest.Network(args[1],null,true).GetAwaiter().GetResult();
  if(args.Length>=2&&args[0]=="--backup-retention-test")return BackupRetentionTests.Run(args[1]);
  if(args.Length>=2&&args[0]=="--client-policy-test")return ClientPolicyTests.Run(args[1]);
  if(args.Length>=3&&args[0]=="--client-policy-probe"){
   var storage=new Storage(args[1]);var settings=storage.Load().Settings;var hwid=HardwareFingerprint.Current();
   var result=ClientPolicyService.Request<ClientAccessDecision>(storage,settings,"access",hwid,CancellationToken.None).GetAwaiter().GetResult();
   Directory.CreateDirectory(args[2]);File.WriteAllText(Path.Combine(args[2],"client-policy-probe.json"),JsonSerializer.Serialize(new{result.Allowed,result.Code,result.Mode,result.Revision,result.OfflineUntil,hwid,update=result.Update,contentDownloaded=false},Storage.Json));return result.Allowed?0:3;
  }
  if(args.Length>=2&&args[0]=="--download-problem-test")return DownloadProblemTest.Run(args[1],args.Length>2?args[2]:"");
  if(args.Length>=3&&args[0]=="--update-probe"){
   var storage=new Storage(args[1]);var output=args[2];Directory.CreateDirectory(output);var settings=storage.Load().Settings;if(args.Length>3)settings.Proxy=args[3];var service=new UpdateService(storage,settings,new Version(0,0,0,0));
   var update=service.Check(CancellationToken.None).GetAwaiter().GetResult();if(update==null)return 2;
   File.WriteAllText(Path.Combine(output,"update-probe.json"),JsonSerializer.Serialize(new{version=update.Version,url=UpdatePromptFlow.DownloadPage,notes=update.Notes,downloaded=false,installed=false,scope="version metadata only; no browser opened or program installed"},Storage.Json));return 0;
  }
  if(args.Length>=2&&args[0]=="--update-test")return UpdateTests.Run(args[1]);
  if(args.Length>=5&&args[0]=="--prepare-release")return UpdateTrust.PrepareRelease(args[1],args[2],args[3],args[4]);
  if(args.Length>=2&&args[0]=="--compatibility-test")return CompatibilityTests.Run(args[1]);
  if(args.Length>=3&&args[0]=="--compatibility-check"){var result=ClientCompatibility.Check(Path.Combine(PatchService.ValidateSteam(args[1]),"steamclient64.dll"),args.Length>3?args[3]:"standard");Directory.CreateDirectory(args[2]);File.WriteAllText(Path.Combine(args[2],"compatibility.json"),JsonSerializer.Serialize(result,Storage.Json));return result.Compatible?0:1;}
  if(args.Length>=2&&args[0]=="--build-free-catalog")return SelfTest.BuildCatalog(args[1]);
  if(args.Length>=2&&args[0]=="--workflow-test")return UiSelfTest.Run(args[1]);
  if(args.Length>=2&&args[0]=="--lifecycle-test")return SteamLifecycleTests.Run(args[1]).GetAwaiter().GetResult();
  if(args.Length>=2&&args[0]=="--self-test")return SelfTest.Run(args[1]);
  if(args.Length>=2&&args[0]=="--network-test")return SelfTest.Network(args[1],args.Length>2?args[2]:null).GetAwaiter().GetResult();
  if(args.Length>=2&&args[0]=="--public-fixture-test")return SelfTest.PublicFixtures(args[1]);
  string root=Path.Combine(AppContext.BaseDirectory,"data");
  int i=Array.IndexOf(args,"--data-dir");if(i>=0&&i+1<args.Length)root=args[i+1];
  var application=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
  application.DispatcherUnhandledException+=(_,e)=>{if(args.Contains("--render-preview")){Console.Error.WriteLine(e.Exception);Environment.Exit(1);}MessageBox.Show(e.Exception.Message,"Bear游戏库 · 操作未完成");e.Handled=true;};
  try{
   var storage=new Storage(root);var state=storage.Load();bool preview=args.Contains("--preview");
   if(preview){state.Games=[
    new(){AppId=281990,Name="Stellaris · 群星",SourceId=90,Enabled=true},
    new(){AppId=1091500,Name="Cyberpunk 2077",Enabled=false},
    new(){AppId=1245620,Name="ELDEN RING",Enabled=false},
    new(){AppId=570,Name="Dota 2",IsFree=true},
    new(){AppId=1145360,Name="Hades",Enabled=false},
    new(){AppId=105600,Name="Terraria",Enabled=false}];
    foreach(var g in state.Games){var cover=Path.Combine(storage.Covers,g.AppId+".jpg");if(File.Exists(cover))g.CoverPath=cover;}
   }
   ClientAccessSession? access=null;
   if(ClientPolicyService.AccessControlEnabled&&!args.Contains("--render-preview")){
    access=new ClientAccessSession(storage,()=>state.Settings);
    if(new ClientAccessWindow(access).ShowDialog()!=true)return 1;
    ClientPolicyService.StartupUpdate=state.Settings.AutoCheckUpdates?access.LastDecision?.Update:null;
   }
   var window=new MainWindow(storage,state,preview);window.ShowHardwareId();
   int render=Array.IndexOf(args,"--render-preview");if(render>=0&&render+1<args.Length){window.ExportPreview(args[render+1],args.Contains("--preview-small")?1190:1460,args.Contains("--preview-small")?750:900);var download=new DownloadWindow(storage,state.Settings,state.Games[0],()=>{});download.ExportPreview(args[render+1]);download.Close();return 0;}
   application.MainWindow=window;application.ShutdownMode=ShutdownMode.OnMainWindowClose;access?.Attach(window);application.Run(window);return 0;
  }catch(Exception e){MessageBox.Show(e.Message,"Bear游戏库 · 启动失败");return 1;}
 }
}
