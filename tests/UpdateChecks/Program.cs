using BotPlus.RT;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

byte[] bytes="installer fixture"u8.ToArray();string hash=Convert.ToHexString(SHA256.HashData(bytes));
string Json(string url="https://github.com/Davud77/bp-revit-tools/releases/download/v1.6/BotPlus.RT-1.6-Setup.exe",string? digest=null,bool draft=false)=>JsonSerializer.Serialize(new{tag_name="v1.6",draft,prerelease=false,assets=new[]{new{name="BotPlus.RT-1.6-Setup.exe",browser_download_url=url,digest=digest??"sha256:"+hash,size=bytes.Length}}});
var release=ReleaseClient.Parse(Json());
foreach(var malformed in new[]{Json(url:"https://example.com/installer.exe"),Json(digest:"sha256:bad"),Json(draft:true),Json().Replace("v1.6","v1.6-beta")})
{try{ReleaseClient.Parse(malformed);throw new Exception("Unsafe release accepted");}catch(InvalidDataException){}}
string folder=Path.Combine(Path.GetTempPath(),"BotPlusUpdateChecks-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
using var handler=new Handler(bytes);using var client=new HttpClient(handler);var api=new ReleaseClient(client);
string file=await api.Download(release,folder,null,CancellationToken.None);if(!ReleaseClient.Verify(file,release))throw new Exception("Verified download failed");
File.AppendAllText(file,"modified");if(ReleaseClient.Verify(file,release))throw new Exception("Post-download change accepted");File.Delete(file);
handler.Bytes=bytes.Select(b=>(byte)(b^1)).ToArray();
try{await api.Download(release,folder,null,CancellationToken.None);throw new Exception("Corrupt installer accepted");}catch(InvalidDataException){}
if(Directory.EnumerateFiles(folder).Any())throw new Exception("Rejected download left an executable");
using var cancelled=new CancellationTokenSource();cancelled.Cancel();try{await api.Download(release,folder,null,cancelled.Token);throw new Exception("Cancellation ignored");}catch(OperationCanceledException){}
Directory.Delete(folder);Console.WriteLine("PASS release URL/tag/digest checks, streaming download, changed file, corrupt cleanup and cancellation");
sealed class Handler(byte[] bytes):HttpMessageHandler
{
    public byte[] Bytes=bytes;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
    {cancellationToken.ThrowIfCancellationRequested();return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(Bytes)});}
}
