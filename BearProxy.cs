using System.Net;
namespace NovaLibrary;

public sealed class BearProxy : IWebProxy {
 internal static bool ForceDirect;
 public ICredentials? Credentials {get;set;}=CredentialCache.DefaultCredentials;
#pragma warning disable SYSLIB0014
 internal static IWebProxy Current=>ForceDirect?new WebProxy():WebRequest.GetSystemWebProxy();
#pragma warning restore SYSLIB0014
 public Uri? GetProxy(Uri destination)=>Current.GetProxy(destination);
 public bool IsBypassed(Uri host)=>Current.IsBypassed(host);
}
