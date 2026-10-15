using System.Text;
using System.Text.Json;
using Career.Contracts;
using Career.Core;

namespace Career.Infrastructure;

public sealed class ArtifactPublisher(string workspaceDirectory) : IArtifactPublisher
{
    private readonly string _root=Path.GetFullPath(workspaceDirectory);
    private static readonly UTF8Encoding Utf8=new(false,true);
    public ArtifactResponse Publish(string taskId,string runId,int revision,int version,OperationDocument operation,string[] sources,string[] checks,string[] unverified)
    {
        DomainRules.FileName(operation.FileName);
        DomainRules.Text(operation.Content,"成果正文",800000);
        var bytes=Utf8.GetBytes(operation.Content);
        var hash=DomainRules.Hash(operation.Content);
        var artifactId="artifact_"+DomainRules.Hash(operation.Id)[..24];
        var artifactDirectory=Path.Combine(_root,"artifacts",artifactId);
        SafeDirectory(_root);SafeDirectory(Path.Combine(_root,"artifacts"));SafeDirectory(artifactDirectory);
        var finalDirectory=Path.Combine(artifactDirectory,version.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var finalFile=Path.Combine(finalDirectory,operation.FileName);
        if(Directory.Exists(finalDirectory))
        {
            SafeDirectory(finalDirectory);
            if(!File.Exists(finalFile) || DomainRules.Hash(Utf8.GetString(SafeRead(finalFile)))!=hash)throw new DomainException("ARTIFACT_PUBLICATION_CONFLICT","成果发布目录已有不同内容，已保留原文件，请人工核对。",409);
            return ReadManifest(finalDirectory,taskId,runId,revision,version,hash,operation.Content);
        }
        var stagingRoot=Path.Combine(_root,"state","staging");SafeDirectory(Path.Combine(_root,"state"));SafeDirectory(stagingRoot);
        var stage=Path.Combine(stagingRoot,operation.Id);
        if(Directory.Exists(stage))
        {
            SafeDirectory(stage);
            var quarantine=Path.Combine(_root,"state","quarantine");SafeDirectory(quarantine);
            Directory.Move(stage,Path.Combine(quarantine,operation.Id+"-"+Guid.NewGuid().ToString("N")));
        }
        SafeDirectory(stage);
        var artifact=new ArtifactResponse(artifactId,runId,revision,version,operation.FileName[..^3],operation.FileName,hash,bytes.LongLength,"text/markdown; charset=utf-8",operation.Content,sources,checks,unverified,DateTimeOffset.UtcNow);
        WriteDurable(Path.Combine(stage,operation.FileName),bytes);
        WriteDurable(Path.Combine(stage,"manifest.json"),Utf8.GetBytes(JsonSerializer.Serialize(artifact with {Content=""},CareerJson.Default.ArtifactResponse)));
        if(DomainRules.Hash(Utf8.GetString(SafeRead(Path.Combine(stage,operation.FileName))))!=hash)throw new IOException("成果暂存校验失败。");
        SafeDirectory(artifactDirectory);
        Directory.Move(stage,finalDirectory);
        return ReadManifest(finalDirectory,taskId,runId,revision,version,hash,operation.Content);
    }
    private static ArtifactResponse ReadManifest(string directory,string taskId,string runId,int revision,int version,string hash,string content)
    {
        var result=JsonSerializer.Deserialize(SafeRead(Path.Combine(directory,"manifest.json")),CareerJson.Default.ArtifactResponse) ?? throw new IOException("成果清单无法读取。");
        if(result.RunId!=runId || result.TaskRevision!=revision || result.Version!=version || result.ContentHash!=hash)throw new DomainException("ARTIFACT_MANIFEST_CONFLICT","成果清单与发布记录不一致，请人工核对。",409);
        return result with {Content=content};
    }
    public byte[] Read(ArtifactResponse artifact,string taskId)
    {
        DomainRules.FileName(artifact.FileName);
        if(!artifact.Id.StartsWith("artifact_",StringComparison.Ordinal) || artifact.Id[9..].Any(c=>!char.IsAsciiHexDigit(c)))throw new IOException("成果标识无效。");
        var dir=Path.Combine(_root,"artifacts",artifact.Id,artifact.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
        SafeDirectory(_root,false);SafeDirectory(Path.Combine(_root,"artifacts"),false);SafeDirectory(Path.GetDirectoryName(dir)!,false);SafeDirectory(dir,false);
        var bytes=SafeRead(Path.Combine(dir,artifact.FileName));
        if(DomainRules.Hash(Utf8.GetString(bytes))!=artifact.ContentHash)throw new DomainException("ARTIFACT_INTEGRITY_FAILURE","成果文件校验失败，保留原记录并等待恢复。",409);
        return bytes;
    }
    public static void SafeDirectory(string path,bool create=true)
    {
        if(Directory.Exists(path) && (File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new DomainException("UNSAFE_WORKSPACE_PATH","工作区路径包含链接，请选择真实目录。",400);
        if(create)Directory.CreateDirectory(path);
        if(!Directory.Exists(path))throw new IOException("目录不存在。");
        if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new DomainException("UNSAFE_WORKSPACE_PATH","工作区路径包含链接，请选择真实目录。",400);
        if(create&&!OperatingSystem.IsWindows())File.SetUnixFileMode(path,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
    }
    private static byte[] SafeRead(string path)
    {
        if(!File.Exists(path) || (File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("文件不存在或为不安全链接。");
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(stream.Length>1024*1024)throw new IOException("成果文件超出上限。");
        var bytes=new byte[checked((int)stream.Length)];stream.ReadExactly(bytes);return bytes;
    }
    public static void WriteDurable(string path,byte[] bytes)
    {
        using(var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)){stream.Write(bytes);stream.Flush(true);}
        if(!OperatingSystem.IsWindows())File.SetUnixFileMode(path,UnixFileMode.UserRead|UnixFileMode.UserWrite);
    }
}
