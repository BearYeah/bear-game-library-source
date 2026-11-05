using System.Security.Cryptography;
using System.Text.Json;
namespace NovaLibrary;
public static class UpdateTests {
 public static int Run(string output,string privateKey){
  Directory.CreateDirectory(output);var root=Path.Combine(Path.GetFullPath(output),"update-fixtures-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
  var pem=File.ReadAllText(privateKey);var rows=new List<object>();int failed=0;
  void Assert(bool ok,string message){if(!ok)throw new Exception(message);}
  void Reject(Action action){try{action();}catch(Exception e) when(e is InvalidDataException or FormatException or System.Security.Cryptography.CryptographicException or IOException){return;}throw new Exception("expected rejection");}
  void Test(string name,Action body){try{body();rows.Add(new{name,result="pass"});}catch(Exception e){failed++;rows.Add(new{name,result="fail",error=e.Message});}}
  var bytes=Enumerable.Range(0,2048).Select(x=>(byte)x).ToArray();var file=Path.Combine(root,"payload.exe");File.WriteAllBytes(file,bytes);
  ReleaseManifest Manifest()=>new(){Version="99.0.0",AssetUrl="https://github.com/"+UpdateTrust.Repository+"/releases/download/v99.0.0/"+Uri.EscapeDataString(UpdateTrust.AssetName),Sha256=SafeFiles.Hash(bytes),Size=bytes.Length};
  SignedRelease Sign(ReleaseManifest m){var data=JsonSerializer.SerializeToUtf8Bytes(m);return new(){Payload=Convert.ToBase64String(data),Signature=Convert.ToBase64String(UpdateSignature.Sign(data,pem))};}
  Test("author-signed manifest verifies",()=>Assert(UpdateTrust.Verify(Sign(Manifest())).Version=="99.0.0","valid signature rejected"));
  Test("tampered manifest is rejected",()=>{var x=Sign(Manifest());var data=Convert.FromBase64String(x.Payload);data[^2]^=1;x.Payload=Convert.ToBase64String(data);Reject(()=>UpdateTrust.Verify(x));});
  Test("untrusted signature bytes are rejected",()=>{var x=Sign(Manifest());x.Signature=Convert.ToBase64String(Enumerable.Repeat((byte)0x22,64).ToArray());Reject(()=>UpdateTrust.Verify(x));});
  Test("HTTP update URLs are rejected",()=>{var m=Manifest();m.AssetUrl=m.AssetUrl.Replace("https:","http:");Reject(()=>UpdateTrust.Verify(Sign(m)));});
  Test("other repositories cannot supply binaries",()=>{var m=Manifest();m.AssetUrl=m.AssetUrl.Replace(UpdateTrust.Repository,"someone/other");Reject(()=>UpdateTrust.Verify(Sign(m)));});
  Test("oversized update binaries are rejected before download",()=>{var m=Manifest();m.Size=300L*1024*1024;Reject(()=>UpdateTrust.Verify(Sign(m)));});
  Test("verified payload bytes match signed size and hash",()=>UpdateTrust.VerifyFile(file,Manifest(),false));
  Test("wrong payload bytes are rejected",()=>{var wrong=Path.Combine(root,"wrong.exe");File.WriteAllBytes(wrong,new byte[2048]);Reject(()=>UpdateTrust.VerifyFile(wrong,Manifest(),false));});
  Test("truncated payload is rejected",()=>{var wrong=Path.Combine(root,"short.exe");File.WriteAllBytes(wrong,bytes[..1000]);Reject(()=>UpdateTrust.VerifyFile(wrong,Manifest(),false));});
  Test("a signed non-application payload is not installable",()=>Reject(()=>UpdateTrust.VerifyFile(file,Manifest(),true)));
  Test("old versions are rejected before networking",()=>{var m=Manifest();m.Version="0.1.0";var storage=new Storage(Path.Combine(root,"data"));Reject(()=>new UpdateService(storage,new()).Stage(Sign(m),CancellationToken.None).GetAwaiter().GetResult());});
  Test("version normalization avoids revision mismatches",()=>Assert(UpdateTrust.Normalize("v1.5.0")==UpdateTrust.Normalize("1.5.0.0"),"bad version comparison"));
  Test("old saved settings acquire automatic update defaults",()=>{var s=JsonSerializer.Deserialize<Settings>("{\"Proxy\":\"http://127.0.0.1:10808\"}")!;Assert(s.AutoCheckUpdates&&s.AutoDownloadUpdates&&s.Proxy=="http://127.0.0.1:10808","settings migration lost defaults or proxy");});
  Test("forged helper jobs cannot change target or write status outside a trusted task",()=>{var id=Guid.NewGuid().ToString("N");var data=Path.Combine(root,"job-data");Directory.CreateDirectory(Path.Combine(data,"updates"));var target=Path.Combine(root,"KEEP.txt");File.WriteAllText(target,"keep");var job=new ApplyUpdateJob{Id=id,DataRoot=data,Target=target,Staged=file,Envelope=Sign(Manifest())};var path=Path.Combine(data,"updates","apply-"+id+".json");File.WriteAllText(path,JsonSerializer.Serialize(job));Assert(UpdateInstaller.Run(path)!=0&&File.ReadAllText(target)=="keep"&&!File.Exists(Path.Combine(data,"updates","last-update.json")),"forged job had effects");});
  File.WriteAllText(Path.Combine(output,"update-tests.json"),JsonSerializer.Serialize(new{passed=rows.Count-failed,total=rows.Count,failed,scope="signed metadata and isolated filesystem tests; no real Steam files or installed application replaced",checks=rows},Storage.Json));return failed==0?0:1;
 }
}
