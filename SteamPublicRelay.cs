namespace NovaLibrary;
public static class SteamPublicRelay {
 static string Base=>(Environment.GetEnvironmentVariable("BEAR_PUBLIC_RELAY")??"").TrimEnd('/');
 public static string? Address(Uri uri){
  if(Base.Length==0||uri.Scheme!="https"||uri.Host!="store.steampowered.com")return null;
  var endpoint=uri.AbsolutePath switch{"/search/results/"=>"search","/api/appdetails"=>"details",_=>null};
  return endpoint==null?null:Base+"/steam-public/"+endpoint+uri.Query;
 }
 public static bool Allowed(Uri uri){
  if(!Uri.TryCreate(Base,UriKind.Absolute,out var server))return false;
  return uri.Scheme=="https"&&uri.Authority==server.Authority&&
   (uri.AbsolutePath==server.AbsolutePath.TrimEnd('/')+"/steam-public/search"||uri.AbsolutePath==server.AbsolutePath.TrimEnd('/')+"/steam-public/details");
 }
}
