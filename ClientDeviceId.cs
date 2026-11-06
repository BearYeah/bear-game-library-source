using System.Text;
using System.Text.Json;
namespace NovaLibrary;
public static class ClientDeviceId {
 public static string Read(Storage storage){
  var file=Path.Combine(storage.Root,"downloads","server-device.json");
  if(!File.Exists(file))return "";
  using var identity=JsonDocument.Parse(File.ReadAllText(file));
  var token=identity.RootElement.GetProperty("token").GetString()!;
  return SafeFiles.Hash(Encoding.UTF8.GetBytes(token))[..12];
 }
}
