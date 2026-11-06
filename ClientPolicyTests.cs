using System.Text.Json;
namespace NovaLibrary;
public static class ClientPolicyTests {
 sealed class MemoryState : IClientAccessStateStore {public ClientAccessState? Value;public ClientAccessState? Read()=>Value;public void Save(ClientAccessState value)=>Value=value;}
 public static int Run(string output){
  Directory.CreateDirectory(output);var checks=new List<string>();void Check(bool pass,string text){if(!pass)throw new InvalidOperationException(text);checks.Add(text);}
  try{
   const string hwid="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";double now=1000;
   var cache=new MemoryState();bool connected=true,allowed=true;long revision=1;
   Task<ClientAccessDecision> Fetch(CancellationToken ct){ct.ThrowIfCancellationRequested();if(!connected)throw new IOException("fixture offline");return Task.FromResult(new ClientAccessDecision{Allowed=allowed,Code=allowed?"allowed":"hardware_blocked",Message=allowed?"":"Fixture suspended",Revision=revision,OfflineUntil=allowed?now+86400:0});}
   var session=new ClientAccessSession(hwid,cache,Fetch,()=>now);
   int prompts=0;bool Unavailable(ClientAccessDecision decision){prompts++;return false;}
   Check(session.AllowStartup(Unavailable,CancellationToken.None).GetAwaiter().GetResult()&&prompts==0,"approved startup completes without opening a status window");
   var first=session.Refresh(CancellationToken.None).GetAwaiter().GetResult();Check(first.Allowed&&cache.Value?.Hwid==hwid,"online approval is cached for the current hardware only");
   connected=false;now+=86399;var offline=session.Refresh(CancellationToken.None).GetAwaiter().GetResult();Check(offline.Allowed&&offline.Offline,"an approved device remains usable for 24 hours during a connection failure");
   Check(session.AllowStartup(Unavailable,CancellationToken.None).GetAwaiter().GetResult()&&prompts==0,"startup with a valid offline lease also opens no status window");
   now+=1;var expired=session.Refresh(CancellationToken.None).GetAwaiter().GetResult();Check(!expired.Allowed&&expired.Code=="connection_required"&&cache.Value!.Decision.Allowed,"expiry requires a connection without inventing a persistent cloud ban");
   Check(!session.AllowStartup(Unavailable,CancellationToken.None).GetAwaiter().GetResult()&&prompts==1,"an expired offline lease displays one recoverable message");
   connected=true;allowed=false;revision++;var banned=session.Refresh(CancellationToken.None).GetAwaiter().GetResult();Check(!banned.Allowed&&!cache.Value!.Decision.Allowed,"a live ban replaces an older approval immediately");
   connected=false;Check(!session.Refresh(CancellationToken.None).GetAwaiter().GetResult().Allowed,"cached suspension continues offline");
   connected=true;allowed=true;revision++;Check(session.Refresh(CancellationToken.None).GetAwaiter().GetResult().Allowed&&cache.Value!.Decision.Allowed,"a later server approval clears the cached suspension");
   Check(session.AllowStartup(Unavailable,CancellationToken.None).GetAwaiter().GetResult()&&prompts==1,"unblocked startup returns to the silent path");
   connected=false;var other=new ClientAccessSession(new string('b',64),cache,Fetch,()=>now);Check(!other.Refresh(CancellationToken.None).GetAwaiter().GetResult().Allowed,"copying another machine's approval does not authorize the current hardware");
   using(var canceled=new CancellationTokenSource()){canceled.Cancel();bool stopped=false;try{session.Refresh(canceled.Token).GetAwaiter().GetResult();}catch(OperationCanceledException){stopped=true;}Check(stopped,"cancellation stops a check instead of creating an offline allowance");}
   var response=JsonSerializer.Deserialize<ClientAccessDecision>("{\"allowed\":true,\"checked_at\":10,\"offline_until\":86410,\"revision\":5}",ClientPolicyService.Json)!;Check(response.Allowed&&response.OfflineUntil==86410&&response.Revision==5,"the desktop reads the server lease fields correctly");
   var firmware=new byte[8+25+2+6];firmware[8]=1;firmware[9]=25;for(int n=0;n<16;n++)firmware[16+n]=(byte)(n+1);firmware[35]=127;firmware[36]=4;
   var identity=HardwareFingerprint.FirmwareIdentity(firmware);var hash=HardwareFingerprint.Digest(identity);Check(identity.Length>0&&hash.Length==64&&hash==HardwareFingerprint.Digest(identity)&&hash!=HardwareFingerprint.Digest(identity+"changed"),"firmware identity has a stable SHA-256 digest and changes with the machine identity");
   File.WriteAllText(Path.Combine(output,"client-policy-tests.json"),JsonSerializer.Serialize(new{result="pass",checks},Storage.Json));return 0;
  }catch(Exception error){File.WriteAllText(Path.Combine(output,"client-policy-tests.json"),JsonSerializer.Serialize(new{result="fail",error=error.ToString(),checks},Storage.Json));return 1;}
 }
}
