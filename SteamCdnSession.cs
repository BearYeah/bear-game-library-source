using System.Text.Json;
using SteamKit2;
using SteamKit2.Discovery;
namespace NovaLibrary;

public sealed class SteamCdnSession : IDisposable {
 readonly SteamClient client;
 readonly CallbackManager callbacks;
 readonly CancellationTokenSource stop=new();
 readonly TaskCompletionSource<bool> loggedOn=new(TaskCreationOptions.RunContinuationsAsynchronously);
 readonly Task pump;
 Task? login;
 public SteamCdnSession(uint cell,MemoryServerListProvider servers){
  var configuration=SteamConfiguration.Create(b=>b.WithCellID(cell).WithConnectionTimeout(TimeSpan.FromSeconds(6)).WithDirectoryFetch(false).WithServerListProvider(servers).WithProtocolTypes(ProtocolTypes.Tcp));
  client=new SteamClient(configuration);callbacks=new CallbackManager(client);
  callbacks.Subscribe<SteamClient.ConnectedCallback>(_=>client.GetHandler<SteamUser>()!.LogOnAnonymous());
  callbacks.Subscribe<SteamUser.LoggedOnCallback>(r=>{if(r.Result==EResult.OK)loggedOn.TrySetResult(true);else loggedOn.TrySetException(new InvalidOperationException(r.Result.ToString()));});
  callbacks.Subscribe<SteamClient.DisconnectedCallback>(_=>loggedOn.TrySetException(new IOException("disconnected")));
  pump=Task.Run(()=>{while(!stop.IsCancellationRequested)callbacks.RunWaitCallbacks(TimeSpan.FromMilliseconds(100));});
 }
 async Task Login(){client.Connect();await loggedOn.Task.WaitAsync(TimeSpan.FromSeconds(15));}
 public async Task<object> Token(uint app,uint depot,string host){
  try{
   await (login??=Login());
   var result=await client.GetHandler<SteamContent>()!.GetCDNAuthToken(app,depot,host).WaitAsync(TimeSpan.FromSeconds(8));
   return new {ok=result.Result==EResult.OK,token=result.Result==EResult.OK?result.Token:"",expires=new DateTimeOffset(result.Expiration.ToUniversalTime()).ToUnixTimeSeconds(),reason=result.Result.ToString()};
  }catch(Exception e) when(e is TimeoutException or IOException or InvalidOperationException or TaskCanceledException or AsyncJobFailedException){return new {ok=false,reason=e is TimeoutException?"timeout":"unavailable"};}
 }
 public static async Task<int> Run(uint cell){
  SteamCdnSession? session=null;
  try{while(await Console.In.ReadLineAsync() is { } line){
   using var request=JsonDocument.Parse(line);var r=request.RootElement;
   if(session==null){var servers=new MemoryServerListProvider();await servers.UpdateServerListAsync(r.GetProperty("servers").EnumerateArray().Select(s=>ServerRecord.CreateSocketServer(System.Net.IPEndPoint.Parse(s.GetString()!))));session=new SteamCdnSession(cell,servers);}
   var response=await session.Token(r.GetProperty("app").GetUInt32(),r.GetProperty("depot").GetUInt32(),r.GetProperty("host").GetString()!);
   await Console.Out.WriteLineAsync(JsonSerializer.Serialize(response));await Console.Out.FlushAsync();
  }}finally{session?.Dispose();}
  return 0;
 }
 public void Dispose(){stop.Cancel();client.Disconnect();pump.GetAwaiter().GetResult();stop.Dispose();}
}
