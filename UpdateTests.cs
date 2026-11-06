using System.Text.Json;
namespace NovaLibrary;
public static class UpdateTests {
 public static int Run(string output,string? unusedPrivateKey=null){
  Directory.CreateDirectory(output);var root=Path.Combine(Path.GetFullPath(output),"update-fixtures-"+Guid.NewGuid().ToString("N"));
  var storage=new Storage(root);var rows=new List<object>();int failed=0;
  void Assert(bool ok,string message){if(!ok)throw new Exception(message);}
  void Test(string name,Action body){try{body();rows.Add(new{name,result="pass"});}catch(Exception e){failed++;rows.Add(new{name,result="fail",error=e.Message});}}
  var next=new ClientUpdateInfo("1.8.20","https://unused.invalid/update.exe","DLC 下载与界面改进。");
  ClientUpdateInfo? Check(ClientUpdateInfo? fixture,Version? baseline=null)=>new UpdateService(storage,new(),baseline??new Version(1,8,19,0),_=>Task.FromResult(fixture)).Check(CancellationToken.None).GetAwaiter().GetResult();
  Test("new versions are offered from service metadata",()=>Assert(Check(next)==next,"new version missing"));
  Test("equal and older versions produce no update prompt",()=>Assert(Check(next with {Version="1.8.19"})==null&&Check(next with {Version="1.8.18"})==null,"old or current version offered"));
  Test("missing published metadata produces no update",()=>Assert(Check(null)==null,"missing metadata offered"));
  Test("a check makes one metadata request and downloads no update file",()=>{int calls=0;var service=new UpdateService(storage,new(),new Version(1,8,19,0),_=>{calls++;return Task.FromResult<ClientUpdateInfo?>(next);});Assert(service.Check(CancellationToken.None).GetAwaiter().GetResult()==next&&calls==1&&!Directory.Exists(Path.Combine(root,"updates")),"check downloaded or queried more than once");});
  Test("declining update confirmation never opens a webpage",()=>{int prompts=0,opened=0;var result=UpdatePromptFlow.TryOpen(next,message=>{prompts++;Assert(message.Contains("1.8.20")&&message.Contains(next.Notes)&&message.Contains("蓝奏云"),"version, notes or channel missing");return false;},_=>opened++);Assert(!result&&prompts==1&&opened==0,"decline opened browser");});
  Test("accepting confirmation opens only the owner Lanzou folder once",()=>{var opened=new List<string>();int prompts=0;var result=UpdatePromptFlow.TryOpen(next,_=>{prompts++;return true;},opened.Add);Assert(result&&prompts==1&&opened.SequenceEqual(new[]{"https://wwbhp.lanzoul.com/b004jv6lfi"}),"unapproved destination or repeated browser launch");});
  Test("cancelled metadata checks stay cancelled",()=>{using var ct=new CancellationTokenSource();ct.Cancel();var service=new UpdateService(storage,new(),fetch:token=>Task.FromCanceled<ClientUpdateInfo?>(token));try{service.Check(ct.Token).GetAwaiter().GetResult();throw new Exception("cancellation ignored");}catch(OperationCanceledException){}});
  Test("failed metadata checks remain errors instead of claiming latest",()=>{var service=new UpdateService(storage,new(),fetch:_=>Task.FromException<ClientUpdateInfo?>(new IOException("fixture unavailable")));try{service.Check(CancellationToken.None).GetAwaiter().GetResult();throw new Exception("failure ignored");}catch(IOException){}});
  Test("old automatic download preference is discarded while startup preference persists",()=>{var settings=JsonSerializer.Deserialize<Settings>("{\"AutoCheckUpdates\":false,\"AutoDownloadUpdates\":true,\"Proxy\":\"http://127.0.0.1:10808\"}")!;Assert(!settings.AutoCheckUpdates&&settings.Proxy=="http://127.0.0.1:10808"&&!JsonSerializer.Serialize(settings).Contains("AutoDownloadUpdates"),"legacy preference retained");Assert(!new Settings().AutoCheckUpdates,"public startup update checks must default off");});
  File.WriteAllText(Path.Combine(output,"update-tests.json"),JsonSerializer.Serialize(new{passed=rows.Count-failed,total=rows.Count,failed,scope="isolated metadata and confirmation tests; no browser opened, program downloaded or installation changed",checks=rows},Storage.Json));return failed==0?0:1;
 }
}
