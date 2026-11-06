using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
namespace NovaLibrary;
public static class HardwareFingerprint {
 [DllImport("kernel32.dll",EntryPoint="GetSystemFirmwareTable",SetLastError=true)] static extern uint GetSystemFirmwareTable(uint provider,uint table,byte[]? buffer,uint size);
 public static string Current(){
  uint size=GetSystemFirmwareTable(0x52534D42,0,null,0);string hardware="";
  if(size>0){var data=new byte[size];if(GetSystemFirmwareTable(0x52534D42,0,data,size)==size)hardware=FirmwareIdentity(data);}
  if(hardware.Length==0){using var machine=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64);using var key=machine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");hardware="machine:"+(key?.GetValue("MachineGuid")?.ToString()??throw new InvalidOperationException("无法读取本机设备标识。"));}
  return Digest(hardware);
 }
 internal static string Digest(string identity)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("BearLibrary/HWID/v1\0"+identity))).ToLowerInvariant();
 internal static string FirmwareIdentity(byte[] data){
  string uuid="",board="";
  for(int start=8;start+4<=data.Length;){
   int type=data[start],length=data[start+1];if(length<4||start+length>data.Length)break;
   int end=start+length;while(end+1<data.Length&&(data[end]!=0||data[end+1]!=0))end++;
   if(type==1&&length>=24){var bytes=data.AsSpan(start+8,16);bool empty=true,filled=true;foreach(var value in bytes){empty&=value==0;filled&=value==255;}if(!empty&&!filled)uuid=Convert.ToHexString(bytes);}
   if(type==2&&length>=8&&data[start+7]>0){var strings=Encoding.ASCII.GetString(data,start+length,Math.Max(0,end-(start+length))).Split('\0');int number=data[start+7]-1;if(number<strings.Length)board=strings[number].Trim();}
   if(type==127)break;start=end+2;
  }
  return uuid.Length>0?"system:"+uuid+"|board:"+board:board.Length>0?"board:"+board:"";
 }
}
