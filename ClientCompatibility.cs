using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Text.Json;
namespace NovaLibrary;
public sealed class CompatibilityProfile {
 public string ProfileId {get;set;}="";public string ReviewedClientSha256 {get;set;}="";
 public List<CompatibilityFunction> Functions {get;set;}=[];public int SwitchTableOffset {get;set;}
 public int[] SwitchTargetOffsets {get;set;}=[];public int TargetFunctionSize {get;set;}
 public List<CompatibilityPattern> FamiliesPatterns {get;set;}=[];
}
public sealed class CompatibilityFunction {
 public string Name {get;set;}="";public int AnchorOffset {get;set;}public string Bytes {get;set;}="";public string Mask {get;set;}="";
 public List<CompatibilityReference> References {get;set;}=[];
}
public sealed class CompatibilityReference {public int Offset {get;set;}public int NextOffset {get;set;}public string Kind {get;set;}="";}
public sealed class CompatibilityPattern {public string Name {get;set;}="";public string Bytes {get;set;}="";public string Mask {get;set;}="";public int PatchJumpDisplacement {get;set;}public string CompareBefore {get;set;}="";}
public sealed class ClientCompatibilityResult {
 public string File {get;set;}="";public string ClientSha256 {get;set;}="";public string Mode {get;set;}="";public string ProfileId {get;set;}="";
 public DateTime CheckedUtc {get;set;}=DateTime.UtcNow;public bool Compatible {get;set;}public bool RuntimeVerified {get;set;}
 public string Evidence {get;set;}="只读静态检查；不运行或加载补丁，不代表实际注入、下载或联网已验证。";
 public string Failure {get;set;}="";public Dictionary<string,int> Locations {get;set;}=[];public List<string> Checks {get;set;}=[];
 public string Summary=>Compatible?(RuntimeVerified?"兼容性检查通过 · 此版本标准模式已有运行验证":"静态兼容性检查通过 · 实机效果待验证"):"兼容性检查未通过："+Failure;
}
public static class ClientCompatibility {
 public static CompatibilityProfile LoadProfile(){
  using var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("NovaLibrary.Payloads.compatibility-profile.json")??throw new InvalidDataException("兼容性模板缺失。");
  return JsonSerializer.Deserialize<CompatibilityProfile>(stream)??throw new InvalidDataException("兼容性模板损坏。");
 }
 public static ClientCompatibilityResult Check(string file,string mode="standard"){
  var result=new ClientCompatibilityResult{File=Path.GetFullPath(file),Mode=mode};
  try{
   if(mode is not ("standard" or "families"))throw new InvalidDataException("未知补丁模式。");
   var info=new FileInfo(file);if(!info.Exists)throw new InvalidDataException("未找到 steamclient64.dll。");
   if(info.Length<512||info.Length>128L*1024*1024)throw new InvalidDataException("客户端文件大小不符合 PE 检测范围。");
   var raw=System.IO.File.ReadAllBytes(file);result.ClientSha256=SafeFiles.Hash(raw);
   var profile=LoadProfile();result.ProfileId=profile.ProfileId;
   var image=new CompatibilityImage(raw);result.Checks.Add("有效的 x64 原生 DLL，文件布局与代码区边界完整");
   int target=image.Unique("整数提取入口","48895c2410574883ec304d85c0","ffffffffffffffffffffffffff");
   int preflight=image.Unique("已挂钩预检查入口","574883ec304d85c0","ffffffffffffffff");
   int caller=image.Unique("映射调用点","448bf083f8ff7400488b5740","ffffffffffffff00ffffffff");
   int sentinel=image.Unique("补丁扫描边界","40554154415541564157488bec","ffffffffffffffffffffffffff");
   result.Locations["hookTargetRva"]=target;result.Locations["callerReturnRva"]=caller;result.Locations["scanBoundaryRva"]=sentinel;
   Require(preflight==target+5,"预检查特征与整数提取入口关系改变。");
   // The native scanner compares an RVA counter with VA-based limits. Do not reinterpret
   // those limits as RVA distances: require every unique match to lie inside valid code.
   Require(image.IsCode(target,profile.TargetFunctionSize)&&image.IsCode(caller-55,181)&&image.IsCode(sentinel,13),"关键特征落在非代码区或越过区段边界。");
   Require(image.RuntimeFunction(target)==(target,target+profile.TargetFunctionSize),"整数提取函数边界与已审查结构不同。");
   Require(image.Byte(caller-5)==0xe8&&caller+(long)image.Int32(caller-4)==target,"映射调用点不再调用预期的整数提取函数。");
   Require(image.RuntimeFunctionRoot(caller-55)==image.RuntimeFunctionRoot(caller+125),"映射调用上下文跨越了不同的函数。");
   Require(image.RuntimeFunction(sentinel).Item1==sentinel,"下载源标记不再是函数入口。");
   result.Checks.Add("4 个关键特征均唯一，调用关系、函数归属及可执行映像边界一致");
   foreach(var body in profile.Functions){
    int anchor=body.Name=="integer-extractor"?target:caller;int start=checked(anchor+body.AnchorOffset);
    var expected=Convert.FromHexString(body.Bytes);var mask=Convert.FromHexString(body.Mask);
    Require(image.MatchAt(start,expected,mask)&&image.IsCode(start,expected.Length),body.Name+" 的关键指令结构发生变化。");
    foreach(var reference in body.References){
     long destination;
     if(reference.Kind=="jump-table")destination=image.UInt32(start+reference.Offset);
     else destination=(long)start+reference.NextOffset+image.Int32(start+reference.Offset);
     if(reference.Kind=="image-base")Require(destination==0,"整数提取函数的模块基址寻址发生变化。");
     else if(reference.Kind=="jump-table")Require(destination==target+profile.SwitchTableOffset,"类型分派表寻址发生变化。");
     else if(reference.Kind=="call")Require(image.IsCode(destination,1),"关键调用跳出可执行代码区。");
     else Require(image.IsReadable(destination,4),"关键指令引用了无效文件地址。");
    }
    result.Checks.Add(body.Name+" 指令模板通过，仅忽略并核对可重定位地址");
   }
   for(int i=0;i<profile.SwitchTargetOffsets.Length;i++)Require(image.UInt32(target+profile.SwitchTableOffset+i*4)==(uint)(target+profile.SwitchTargetOffsets[i]),"整数类型分派表目标发生变化。");
   result.Checks.Add("11 个整数类型分派目标与已审查结构一致");
   if(mode=="families")foreach(var pattern in profile.FamiliesPatterns){
    int rva=image.Unique(pattern.Name,pattern.Bytes,pattern.Mask);result.Locations[pattern.Name]=rva;
    Require(image.Byte(rva)==0x0f&&(image.Byte(rva+1)&0xf0)==0x80,"SF 目标不再是预期的条件跳转。");
    var compare=Convert.FromHexString(pattern.CompareBefore);Require(compare.Length>0&&image.IsCode(rva-compare.Length,compare.Length+6)&&image.IsCode(rva,pattern.Bytes.Length/2),"SF 分支或比较指令越过可执行区段。");
    Require(image.MatchAt(rva-compare.Length,compare,Enumerable.Repeat((byte)255,compare.Length).ToArray()),pattern.Name+" 的比较指令或字段发生变化。");
    long original=(long)rva+6+image.Int32(rva+2),patched=(long)rva+5+pattern.PatchJumpDisplacement;
    Require(original==patched&&image.IsCode(original,1),"SF 固定跳转目标与客户端原分支不一致。");
    Require(image.RuntimeFunctionRoot(rva-compare.Length)==image.RuntimeFunctionRoot(checked((int)original)),"SF 分支与目标不属于同一个链式展开函数。");
    result.Checks.Add(pattern.Name+" 唯一，比较语义、链式函数归属及跳转目标一致");
   }
   result.RuntimeVerified=mode=="standard"&&result.ClientSha256==PatchService.ClientHash;
   result.Compatible=true;
  }catch(Exception e) when(e is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidDataException or ArgumentException or OverflowException){result.Failure=e.Message;}
  return result;
 }
 static void Require(bool condition,string message){if(!condition)throw new InvalidDataException(message);}
 internal sealed class CompatibilityImage {
  readonly byte[] image;readonly List<(int Start,int Size,int Flags)> sections=[];readonly int exceptionStart,exceptionSize;
  public CompatibilityImage(byte[] raw){
   using var stream=new MemoryStream(raw,false);using var pe=new PEReader(stream);var h=pe.PEHeaders;var p=h.PEHeader??throw new InvalidDataException("缺少 PE 可选头。");
   Require(h.CoffHeader.Machine==Machine.Amd64&&p.Magic==PEMagic.PE32Plus&&(h.CoffHeader.Characteristics&Characteristics.Dll)!=0&&h.CorHeader==null,"客户端必须是 x64 原生 DLL。");
   Require(p.SizeOfImage>=512&&p.SizeOfImage<=256*1024*1024&&p.SizeOfHeaders>0&&p.SizeOfHeaders<=raw.Length&&p.SizeOfHeaders<=p.SizeOfImage,"无效的客户端映像大小。");
   image=new byte[p.SizeOfImage];Array.Copy(raw,image,p.SizeOfHeaders);
   foreach(var s in h.SectionHeaders){
    int size=Math.Max(s.VirtualSize,s.SizeOfRawData);Require(s.VirtualAddress>=p.SizeOfHeaders&&size>=0&&(long)s.VirtualAddress+size<=image.Length&&s.PointerToRawData>=0&&s.SizeOfRawData>=0&&(long)s.PointerToRawData+s.SizeOfRawData<=raw.Length,"PE 区段越界或截断。");
    Require(!sections.Any(x=>s.VirtualAddress<(long)x.Start+x.Size&&x.Start<(long)s.VirtualAddress+size),"PE 区段重叠。");
    sections.Add((s.VirtualAddress,size,(int)s.SectionCharacteristics));Array.Copy(raw,s.PointerToRawData,image,s.VirtualAddress,s.SizeOfRawData);
   }
   exceptionStart=p.ExceptionTableDirectory.RelativeVirtualAddress;exceptionSize=p.ExceptionTableDirectory.Size;
   Require(exceptionSize>0&&exceptionSize%12==0&&IsReadable(exceptionStart,exceptionSize),"客户端缺少有效的函数展开表。");
  }
  public bool IsReadable(long rva,int length)=>rva>=0&&length>0&&rva+length<=image.Length&&sections.Any(s=>rva>=s.Start&&rva+length<=(long)s.Start+s.Size);
  public bool IsCode(long rva,int length)=>IsReadable(rva,length)&&sections.Any(s=>rva>=s.Start&&rva+length<=(long)s.Start+s.Size&&(s.Flags&0x20000000)!=0);
  void Range(int rva,int length){Require(IsReadable(rva,length),"兼容性检查读取超出有效区段。");}
  public byte Byte(int rva){Range(rva,1);return image[rva];}
  public int Int32(int rva){Range(rva,4);return BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(rva,4));}
  public uint UInt32(int rva){Range(rva,4);return BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(rva,4));}
  (int Start,int End,int Unwind) RuntimeRecord(int rva){
   (int,int,int) answer=(-1,-1,-1);int matches=0;
   for(int off=exceptionStart;off<exceptionStart+exceptionSize;off+=12){uint a=UInt32(off),b=UInt32(off+4);if(a<=(uint)rva&&(uint)rva<b){answer=(checked((int)a),checked((int)b),checked((int)UInt32(off+8)));matches++;}}
   Require(matches==1,"关键函数展开表匹配不唯一。");return answer;
  }
  public (int,int) RuntimeFunction(int rva){var f=RuntimeRecord(rva);return(f.Start,f.End);}
  public (int,int,int) RuntimeFunctionRoot(int rva){
   var f=RuntimeRecord(rva);var visited=new HashSet<int>();
   for(int depth=0;depth<32;depth++){
    Require(visited.Add(f.Unwind),"函数链式展开信息出现循环。");Require(IsReadable(f.Unwind,4),"函数展开信息头越界。");
    int header=Byte(f.Unwind),version=header&7,flags=header>>3;Require(version is 1 or 2,"函数展开信息版本未知。");
    Require((flags&~7)==0,"函数展开标志无效。");if((flags&4)==0)return(f.Start,f.End,f.Unwind);
    Require((flags&3)==0,"链式展开与异常处理标志冲突。");int next=checked(f.Unwind+4+((Byte(f.Unwind+2)+1)&~1)*2);Require(IsReadable(next,12),"链式展开记录越界。");
    var chained=(Start:checked((int)UInt32(next)),End:checked((int)UInt32(next+4)),Unwind:checked((int)UInt32(next+8)));
    Require(chained.Start<chained.End&&IsCode(chained.Start,1)&&IsCode(chained.End-1,1),"链式展开函数范围无效。");
    Require(RuntimeRecord(chained.Start)==chained,"链式展开未指向唯一的真实函数记录。");f=chained;
   }
   throw new InvalidDataException("函数链式展开层级过深。");
  }
  public bool MatchAt(int rva,byte[] bytes,byte[] mask){
   if(bytes.Length!=mask.Length||rva<0||(long)rva+bytes.Length>image.Length)return false;
   for(int i=0;i<bytes.Length;i++)if((image[rva+i]&mask[i])!=(bytes[i]&mask[i]))return false;return true;
  }
  public int Unique(string name,string hex,string maskHex){
   var bytes=Convert.FromHexString(hex);var mask=Convert.FromHexString(maskHex);Require(bytes.Length>0&&bytes.Length==mask.Length&&mask[0]==255,"兼容性特征格式无效。");
   int found=-1,count=0,index=0;
   while(index<=image.Length-bytes.Length){int hit=image.AsSpan(index).IndexOf(bytes[0]);if(hit<0)break;index+=hit;if(MatchAt(index,bytes,mask)){found=index;if(++count>1)break;}index++;}
   Require(count==1,name+(count==0?"未找到，需要更新补丁适配。":"匹配不唯一，已停止安装。"));return found;
  }
 }
}
