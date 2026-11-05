using System.Buffers.Binary;
using System.Text.Json;
namespace NovaLibrary;
public static class CompatibilityTests {
 const int Target=0x5000,Caller=0x2000,Sentinel=0x3000,Headers=0x200,TextStart=0x1000,TextLength=0xf000;
 static void I32(byte[] bytes,int at,int value)=>BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at,4),value);
 static void I16(byte[] bytes,int at,ushort value)=>BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at,2),value);
 static int Raw(int rva)=>rva-TextStart+Headers;
 static void Put(byte[] bytes,int rva,byte[] value)=>value.CopyTo(bytes,Raw(rva));
 internal static byte[] Fixture(){
  var raw=new byte[0xf400];raw[0]=0x4d;raw[1]=0x5a;I32(raw,0x3c,0x80);raw[0x80]=0x50;raw[0x81]=0x45;
  I16(raw,0x84,0x8664);I16(raw,0x86,2);I16(raw,0x94,0xf0);I16(raw,0x96,0x2002);
  int op=0x98;I16(raw,op,0x20b);BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(op+24,8),0x180000000);I32(raw,op+32,0x1000);I32(raw,op+36,0x200);
  I32(raw,op+56,0x12000);I32(raw,op+60,Headers);I16(raw,op+68,2);I32(raw,op+108,16);I32(raw,op+112+3*8,0x10000);I32(raw,op+116+3*8,84);
  int sh=0x188;System.Text.Encoding.ASCII.GetBytes(".text").CopyTo(raw,sh);I32(raw,sh+8,TextLength);I32(raw,sh+12,TextStart);I32(raw,sh+16,TextLength);I32(raw,sh+20,Headers);I32(raw,sh+36,0x60000020);
  sh+=40;System.Text.Encoding.ASCII.GetBytes(".pdata").CopyTo(raw,sh);I32(raw,sh+8,0x200);I32(raw,sh+12,0x10000);I32(raw,sh+16,0x200);I32(raw,sh+20,0xf200);I32(raw,sh+36,0x40000040);
  var profile=ClientCompatibility.LoadProfile();
  foreach(var body in profile.Functions){int start=(body.Name=="integer-extractor"?Target:Caller)+body.AnchorOffset;Put(raw,start,Convert.FromHexString(body.Bytes));
   foreach(var reference in body.References){int destination=reference.Kind switch{"image-base"=>0,"jump-table"=>Target+profile.SwitchTableOffset,"call"=>0x8000,_=>0x9000};
    I32(raw,Raw(start+reference.Offset),reference.Kind=="jump-table"?destination:destination-(start+reference.NextOffset));
   }
  }
  I32(raw,Raw(Caller-4),Target-Caller);Put(raw,Sentinel,Convert.FromHexString("40554154415541564157488bec"));raw[Raw(0x8000)]=0xc3;
  for(int i=0;i<profile.SwitchTargetOffsets.Length;i++)I32(raw,Raw(Target+profile.SwitchTableOffset+i*4),Target+profile.SwitchTargetOffsets[i]);
  var records=new[]{(Target,Target+profile.TargetFunctionSize),(Caller-0x80,Caller+0x180),(Sentinel,Sentinel+0x200),(0x5ffc,0x6006),(0x6006,0x6240),(0x63fd,0x65c0),(0x67fc,0x6a40)};
  for(int i=0;i<records.Length;i++){int entry=0x10000+i*12,unwind=0x10080+i*16;I32(raw,Raw(entry),records[i].Item1);I32(raw,Raw(entry+4),records[i].Item2);I32(raw,Raw(entry+8),unwind);raw[Raw(unwind)]=1;}
  raw[Raw(0x100c0)]=0x21;I32(raw,Raw(0x100c4),0x5ffc);I32(raw,Raw(0x100c8),0x6006);I32(raw,Raw(0x100cc),0x100b0);
  int[] places=[0x6000,0x6400,0x6800];for(int i=0;i<profile.FamiliesPatterns.Count;i++){var pattern=profile.FamiliesPatterns[i];var compare=Convert.FromHexString(pattern.CompareBefore);Put(raw,places[i]-compare.Length,compare);Put(raw,places[i],Convert.FromHexString(pattern.Bytes));I32(raw,Raw(places[i]+2),pattern.PatchJumpDisplacement-1);}
  return raw;
 }
 public static int Run(string output){
  Directory.CreateDirectory(output);var root=Path.Combine(Path.GetFullPath(output),"compatibility-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
  var source=Fixture();var reports=new List<object>();int failed=0;void Assert(bool ok,string message){if(!ok)throw new Exception(message);}
  void Test(string name,Action body){try{body();reports.Add(new{name,result="pass"});}catch(Exception e){failed++;reports.Add(new{name,result="fail",error=e.Message});}}
  ClientCompatibilityResult Check(Action<byte[]>? mutate=null,string mode="standard"){var bytes=source.ToArray();mutate?.Invoke(bytes);var file=Path.Combine(root,Guid.NewGuid().ToString("N")+".dll");File.WriteAllBytes(file,bytes);return ClientCompatibility.Check(file,mode);}
  Test("a structurally compatible client is accepted without a whole-file hash whitelist",()=>{var r=Check();Assert(r.Compatible&&!r.RuntimeVerified&&r.Locations["hookTargetRva"]==Target,r.Summary);});
  Test("changes outside critical code do not require rebuilding Bear",()=>{var a=Check();var b=Check(x=>x[Raw(0x9500)]=0x37);Assert(b.Compatible&&a.ClientSha256!=b.ClientSha256,b.Summary);});
  Test("duplicate hook signatures are rejected",()=>Assert(!Check(x=>Put(x,0x7800,x.AsSpan(Raw(Target),13).ToArray())).Compatible,"ambiguous signature accepted"));
  Test("missing scanner boundary is rejected before native patch runs",()=>Assert(!Check(x=>x[Raw(Sentinel)]=0xcc).Compatible,"missing boundary accepted"));
  Test("caller must still invoke the selected hook target",()=>Assert(!Check(x=>I32(x,Raw(Caller-4),0x8000-Caller)).Compatible,"wrong call destination accepted"));
  Test("changed integer type layout is rejected even with matching entry signatures",()=>Assert(!Check(x=>x[Raw(Target+0x31)]=0x18).Compatible,"changed instruction accepted"));
  Test("changed switch-table branch is rejected",()=>Assert(!Check(x=>I32(x,Raw(Target+0x1f0),Target+478)).Compatible,"wrong switch branch accepted"));
  Test("invalid external call destination is rejected",()=>Assert(!Check(x=>I32(x,Raw(Target+217),0x11000-(Target+221))).Compatible,"invalid external call accepted"));
  Test("module-base address reference must resolve to the image base",()=>Assert(!Check(x=>I32(x,Raw(Target+65),0x1000-(Target+69))).Compatible,"wrong module base accepted"));
  Test("changed unwind function boundary is rejected",()=>Assert(!Check(x=>I32(x,Raw(0x10004),Target+0x220)).Compatible,"wrong function size accepted"));
  Test("code signatures in a non-executable section are rejected",()=>Assert(!Check(x=>I32(x,0x188+36,0x40000040)).Compatible,"data-section match accepted"));
  Test("ARM64 binaries are rejected",()=>Assert(!Check(x=>I16(x,0x84,0xaa64)).Compatible,"wrong machine accepted"));
  Test("truncated PE files are rejected",()=>{var file=Path.Combine(root,"truncated.dll");File.WriteAllBytes(file,source[..700]);Assert(!ClientCompatibility.Check(file).Compatible,"truncation accepted");});
  Test("SF mode checks all three additional branch targets",()=>Assert(Check(mode:"families").Compatible,"valid SF structure rejected"));
  Test("all modes reject a caller context crossing function ownership",()=>{foreach(var mode in new[]{"standard","families"})Assert(!Check(x=>I32(x,Raw(0x10010),Caller+20),mode).Compatible,"cross-function context accepted: "+mode);});
  Test("download scan marker must remain a real function entry",()=>Assert(!Check(x=>I32(x,Raw(0x10018),Sentinel-1)).Compatible,"interior marker accepted"));
  Test("SF source and target in distinct chained unwind fragments are accepted",()=>Assert(Check(mode:"families").Compatible,"valid chained function rejected"));
  Test("SF comparison field offset changes are rejected",()=>Assert(!Check(x=>x[Raw(0x6000)-1]=0x24,"families").Compatible,"changed SF1 field accepted"));
  Test("SF integer comparison constants are checked",()=>{foreach(var at in new[]{0x6400,0x6800})Assert(!Check(x=>x[Raw(at)-1]=0xfe,"families").Compatible,"changed SF comparison constant accepted");});
  Test("cyclic SF unwind metadata is rejected",()=>Assert(!Check(x=>{I32(x,Raw(0x100c4),0x6006);I32(x,Raw(0x100c8),0x6240);I32(x,Raw(0x100cc),0x100c0);},"families").Compatible,"cyclic chain accepted"));
  Test("SF chained target cannot move into another function root",()=>Assert(!Check(x=>{I32(x,Raw(0x100c4),0x63fd);I32(x,Raw(0x100c8),0x65c0);I32(x,Raw(0x100cc),0x100d0);},"families").Compatible,"foreign root accepted"));
  Test("SF branch displacement changes are rejected despite wildcard signature",()=>Assert(!Check(x=>x[Raw(0x6002)]++,"families").Compatible,"SF target mismatch accepted"));
  Test("missing SF-only pattern blocks SF without blocking standard mode",()=>{Assert(Check(x=>x[Raw(0x6800)]=0xcc).Compatible,"standard incorrectly depends on SF");Assert(!Check(x=>x[Raw(0x6800)]=0xcc,"families").Compatible,"SF missing pattern accepted");});
  Test("installation uses structural checks and saves a read-only compatibility report",()=>{
   var steam=Path.Combine(root,"fake-steam");Directory.CreateDirectory(steam);File.WriteAllText(Path.Combine(steam,"steam.exe"),"fixture; never run");File.WriteAllBytes(Path.Combine(steam,"steamclient64.dll"),source);
   var storage=new Storage(Path.Combine(root,"data"));var patch=new PatchService(storage,()=>{},true);var games=new[]{new GameItem{AppId=281990,SourceId=90,Enabled=true}};
   patch.PreflightInstall(steam,"standard",games);Assert(!File.Exists(Path.Combine(steam,"user32.dll")),"preflight mutated Steam");Assert(File.Exists(patch.CompatibilityReportPath),"report missing");
   patch.Install(steam,"standard",games);Assert(SafeFiles.HashFile(Path.Combine(steam,"user32.dll"))==PatchService.StandardHash,"payload mismatch");
   var client=source.ToArray();client[Raw(Target+0x31)]=0x18;File.WriteAllBytes(Path.Combine(steam,"steamclient64.dll"),client);bool rejected=false;
   try{patch.PreflightConfig(steam,games,true);}catch(InvalidOperationException){rejected=true;}Assert(rejected,"restart with incompatible installed patch was not blocked");patch.Uninstall(steam);Assert(!File.Exists(Path.Combine(steam,"user32.dll")),"incompatible client prevented uninstall");
  });
  File.WriteAllText(Path.Combine(output,"compatibility-tests.json"),JsonSerializer.Serialize(new{passed=reports.Count-failed,total=reports.Count,failed,scope="synthetic PE files and isolated fake Steam directory only; native fixture and patch are never executed",checks=reports},Storage.Json));
  return failed==0?0:1;
 }
}
