using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BotPlus.RT;

internal sealed record ReleaseInfo(Version Version,string Name,Uri Download,long Size,string Sha256,string Page);
internal sealed class ReleaseClient(HttpClient http)
{
    internal const string Repository="Davud77/bp-revit-tools";
    public static ReleaseInfo Parse(string json)
    {
        using var doc=JsonDocument.Parse(json);var r=doc.RootElement;string tag=r.GetProperty("tag_name").GetString()??"";
        if(r.GetProperty("draft").GetBoolean()||r.GetProperty("prerelease").GetBoolean()||!Regex.IsMatch(tag,@"^v?\d+\.\d+(\.\d+)?$"))throw new InvalidDataException("Релиз не является стабильной версией.");
        string version=tag.TrimStart('v'),name=$"BotPlus.RT-{version}-Setup.exe";
        var assets=r.GetProperty("assets").EnumerateArray().Where(a=>a.GetProperty("name").GetString()==name).ToArray();
        if(assets.Length!=1)throw new InvalidDataException("В релизе отсутствует единственный установщик "+name);
        var a=assets[0];var url=new Uri(a.GetProperty("browser_download_url").GetString()!);
        string expected=$"https://github.com/{Repository}/releases/download/{tag}/{name}";
        string digest=a.TryGetProperty("digest",out var d)?d.GetString()??"":"";long size=a.GetProperty("size").GetInt64();
        if(url.AbsoluteUri!=expected||!Regex.IsMatch(digest,@"^sha256:[0-9a-fA-F]{64}$")||size<=0||size>2L*1024*1024*1024)throw new InvalidDataException("Адрес, размер или SHA-256 установщика не прошли проверку.");
        var parsed=Version.Parse(version);
        return new(new Version(parsed.Major,parsed.Minor,Math.Max(0,parsed.Build)),name,url,size,digest[7..].ToUpperInvariant(),$"https://github.com/{Repository}/releases/tag/{tag}");
    }
    public async Task<ReleaseInfo> Latest(CancellationToken cancellation)
    {
        using var request=new HttpRequestMessage(HttpMethod.Get,$"https://api.github.com/repos/{Repository}/releases/latest");
        request.Headers.UserAgent.ParseAdd("BotPlusRT/1.6.3");request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response=await http.SendAsync(request,cancellation);
        if(response.StatusCode==HttpStatusCode.NotFound)throw new InvalidOperationException("На GitHub пока нет опубликованного релиза.");
        response.EnsureSuccessStatusCode();return Parse(await response.Content.ReadAsStringAsync(cancellation));
    }
    public static bool Verify(string path,ReleaseInfo release)
    {if(new FileInfo(path).Length!=release.Size)return false;using var stream=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(stream))==release.Sha256;}
    public async Task<string> Download(ReleaseInfo release,string folder,IProgress<int>? progress,CancellationToken cancellation)
    {
        Directory.CreateDirectory(folder);string file=Path.Combine(folder,Guid.NewGuid().ToString("N")+"-"+release.Name);
        try
        {
            using var request=new HttpRequestMessage(HttpMethod.Get,release.Download);request.Headers.UserAgent.ParseAdd("BotPlusRT/1.6.3");
            using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,cancellation);response.EnsureSuccessStatusCode();
            if(response.Content.Headers.ContentLength is long size&&size!=release.Size)throw new InvalidDataException("Размер загрузки отличается от релиза.");
            await using(var output=new FileStream(file,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            await using(var input=await response.Content.ReadAsStreamAsync(cancellation))
            {
                byte[] buffer=new byte[128*1024];long total=0;int read;
                while((read=await input.ReadAsync(buffer,cancellation))>0)
                {total+=read;if(total>release.Size)throw new InvalidDataException("Превышен размер установщика.");await output.WriteAsync(buffer.AsMemory(0,read),cancellation);progress?.Report((int)(total*100/release.Size));}
            }
            cancellation.ThrowIfCancellationRequested();if(!Verify(file,release))throw new InvalidDataException("Контрольная сумма SHA-256 не совпала. Установщик удалён.");return file;
        }
        catch{if(File.Exists(file))File.Delete(file);throw;}
    }
}
