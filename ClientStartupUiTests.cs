using System.Windows;
using System.Windows.Threading;
namespace NovaLibrary;
public static class ClientStartupUiTests {
 sealed class State : IClientAccessStateStore {
  public ClientAccessState? Value;public ClientAccessState? Read()=>Value;public void Save(ClientAccessState value)=>Value=value;
 }
 public static string Run(string folder){
  var approval=new ClientAccessDecision{Allowed=true,OfflineUntil=86401};
  var cache=new State{Value=new(new string('a',64),approval)};
  var reply=new TaskCompletionSource<ClientAccessDecision>(TaskCreationOptions.RunContinuationsAsynchronously);int requests=0;
  var session=new ClientAccessSession(cache.Value.Hwid,cache,ct=>{Interlocked.Increment(ref requests);return reply.Task.WaitAsync(ct);},()=>1);
  var launch=session.AllowStartup(_=>throw new Exception("unexpected startup window"),CancellationToken.None);
  if(!launch.IsCompletedSuccessfully||!launch.Result)throw new Exception("cached startup waited for transport");
  var window=new MainWindow(new Storage(folder),new LibraryState{Settings=new(){Motion=false}},true){ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.Manual,Left=-20000,Top=-20000};
  var frame=new DispatcherFrame();bool renderedBeforeReply=false;
  var timeout=new DispatcherTimer{Interval=TimeSpan.FromSeconds(5)};timeout.Tick+=(_,_)=>frame.Continue=false;
  var startup=session.Attach(window);
  window.ContentRendered+=(_,_)=>{renderedBeforeReply=!startup.IsCompleted&&!Application.Current.Windows.OfType<ClientAccessWindow>().Any(w=>w.IsVisible);frame.Continue=false;};
  try{
   timeout.Start();window.Show();Dispatcher.PushFrame(frame);
   if(!renderedBeforeReply)throw new Exception("main window did not render while the access request was pending");
   reply.SetResult(approval);startup.GetAwaiter().GetResult();
   if(requests!=1)throw new Exception("startup did not reuse its single background check");
   return "the WPF main window renders while the one background access request is still pending, with no status popup";
  }finally{timeout.Stop();reply.TrySetCanceled();window.Close();}
 }
}
