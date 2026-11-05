using System.Buffers.Binary;
using System.Formats.Asn1;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
namespace NovaLibrary;
// Uses stateless Windows CNG primitives, never the user's persisted CNG key store.
internal static class UpdateSignature {
 [DefaultDllImportSearchPaths(DllImportSearchPath.System32),DllImport("bcrypt.dll",CharSet=CharSet.Unicode)] static extern int BCryptOpenAlgorithmProvider(out nint algorithm,string name,string? implementation,uint flags);
 [DefaultDllImportSearchPaths(DllImportSearchPath.System32),DllImport("bcrypt.dll",CharSet=CharSet.Unicode)] static extern int BCryptImportKeyPair(nint algorithm,nint importKey,string type,out nint key,byte[] blob,int size,uint flags);
 [DefaultDllImportSearchPaths(DllImportSearchPath.System32),DllImport("bcrypt.dll")] static extern int BCryptVerifySignature(nint key,nint padding,byte[] hash,int hashSize,byte[] signature,int signatureSize,uint flags);
 [DefaultDllImportSearchPaths(DllImportSearchPath.System32),DllImport("bcrypt.dll")] static extern int BCryptSignHash(nint key,nint padding,byte[] hash,int hashSize,byte[] signature,int signatureSize,out int resultSize,uint flags);
 [DefaultDllImportSearchPaths(DllImportSearchPath.System32),DllImport("bcrypt.dll")] static extern int BCryptDestroyKey(nint key);
 [DefaultDllImportSearchPaths(DllImportSearchPath.System32),DllImport("bcrypt.dll")] static extern int BCryptCloseAlgorithmProvider(nint algorithm,uint flags);
 static void Check(int status){if(status!=0)throw new CryptographicException("Windows 签名组件返回错误 0x"+((uint)status).ToString("x8"));}
 static void Algorithm(AsnReader algorithm){if(algorithm.ReadObjectIdentifier()!="1.2.840.10045.2.1"||algorithm.ReadObjectIdentifier()!="1.2.840.10045.3.1.7")throw new CryptographicException("仅接受 ECDSA P-256 发布密钥。");algorithm.ThrowIfNotEmpty();}
 static byte[] PublicPoint(){var r=new AsnReader(Convert.FromBase64String(UpdateTrust.PublicKey),AsnEncodingRules.DER);var sequence=r.ReadSequence();Algorithm(sequence.ReadSequence());var point=sequence.ReadBitString(out int unused);if(unused!=0||point.Length!=65||point[0]!=4)throw new CryptographicException("发布公钥格式无效。");sequence.ThrowIfNotEmpty();r.ThrowIfNotEmpty();return point;}
 static byte[] Blob(byte[] point,byte[]? scalar=null){if(point.Length!=65||point[0]!=4||scalar!=null&&scalar.Length!=32)throw new CryptographicException("发布密钥大小无效。");var blob=new byte[scalar==null?72:104];BinaryPrimitives.WriteUInt32LittleEndian(blob,scalar==null?0x31534345u:0x32534345u);BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(4),32);point.AsSpan(1).CopyTo(blob.AsSpan(8));scalar?.CopyTo(blob,72);return blob;}
 public static bool Verify(byte[] payload,byte[] signature){if(signature.Length!=64)return false;Check(BCryptOpenAlgorithmProvider(out var algorithm,"ECDSA_P256",null,0));nint key=0;try{var blob=Blob(PublicPoint());Check(BCryptImportKeyPair(algorithm,0,"ECCPUBLICBLOB",out key,blob,blob.Length,0));var hash=SHA256.HashData(payload);return BCryptVerifySignature(key,0,hash,hash.Length,signature,signature.Length,0)==0;}finally{if(key!=0)BCryptDestroyKey(key);BCryptCloseAlgorithmProvider(algorithm,0);}}
 public static byte[] Sign(byte[] payload,string pem){
  var fields=PemEncoding.Find(pem);if(pem[fields.Label]!="PRIVATE KEY")throw new CryptographicException("请提供 PKCS8 私钥。");var der=Convert.FromBase64String(pem[fields.Base64Data]);
  var reader=new AsnReader(der,AsnEncodingRules.DER);var outer=reader.ReadSequence();if(outer.ReadInteger()!=0)throw new CryptographicException("不支持的私钥格式。");Algorithm(outer.ReadSequence());var innerReader=new AsnReader(outer.ReadOctetString(),AsnEncodingRules.DER);var inner=innerReader.ReadSequence();if(inner.ReadInteger()!=1)throw new CryptographicException("不支持的 EC 私钥格式。");var scalar=inner.ReadOctetString();byte[]? point=null;
  while(inner.HasData){var tag=inner.PeekTag();if(tag.HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific,1,true))){var value=inner.ReadSequence(new Asn1Tag(TagClass.ContextSpecific,1,true));point=value.ReadBitString(out int unused);if(unused!=0)throw new CryptographicException("私钥公钥字段无效。");value.ThrowIfNotEmpty();}else inner.ReadEncodedValue();}
  if(point==null)throw new CryptographicException("私钥缺少公钥字段。");var blob=Blob(point,scalar);Check(BCryptOpenAlgorithmProvider(out var algorithm,"ECDSA_P256",null,0));nint key=0;
  try{Check(BCryptImportKeyPair(algorithm,0,"ECCPRIVATEBLOB",out key,blob,blob.Length,0));var hash=SHA256.HashData(payload);var signature=new byte[64];Check(BCryptSignHash(key,0,hash,hash.Length,signature,signature.Length,out var length,0));if(length!=64)throw new CryptographicException("签名大小无效。");return signature;}finally{CryptographicOperations.ZeroMemory(scalar);CryptographicOperations.ZeroMemory(blob);if(key!=0)BCryptDestroyKey(key);BCryptCloseAlgorithmProvider(algorithm,0);}
 }
}
