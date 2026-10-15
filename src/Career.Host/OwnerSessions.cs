using System.Security.Cryptography;
using Career.Contracts;
using Career.Core;

namespace Career.Host;

public sealed class OwnerSessions(ICareerStore store)
{
    public string PairingCode { get; }=RandomNumberGenerator.GetInt32(100000,1000000).ToString(System.Globalization.CultureInfo.InvariantCulture);
    private readonly DateTimeOffset _pairExpires=DateTimeOffset.UtcNow.AddMinutes(10);
    private readonly object _gate=new();
    private int _failures;
    private bool _used;
    public bool HasOwner=>store.ReadSetting("ownerInitialized")=="true";
    public (SessionResponse Access,string Refresh) Pair(string code)
    {
        lock(_gate)
        {
            if(_used||DateTimeOffset.UtcNow>=_pairExpires)throw new DomainException("PAIRING_EXPIRED","配对码已使用或过期。重新启动本机应用取得新配对码。",401);
            if(_failures>=5)throw new DomainException("PAIRING_RATE_LIMITED","配对尝试过多，请重新启动本机应用后使用新配对码。",429);
            var provided=DomainRules.Hash(code??"");var expected=DomainRules.Hash(PairingCode);
            if(!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(provided),Convert.FromHexString(expected))){_failures++;throw new DomainException("PAIRING_CODE_INVALID","配对码不正确，请使用本机启动窗口显示的六位配对码。",401);}
            var issued=Issue(DomainRules.Id("session"));store.SaveSession(issued.Document);store.WriteSetting("ownerInitialized","true");_used=true;return(issued.Access,issued.Refresh);
        }
    }
    private static string Token()=>Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+','-').Replace('/','_');
    private (SessionDocument Document,SessionResponse Access,string Refresh) Issue(string id)
    {
        var access=Token();var refresh=Token();var now=DateTimeOffset.UtcNow;
        var document=new SessionDocument(id,DomainRules.Hash(access),now.AddMinutes(5),DomainRules.Hash(refresh),now.AddDays(7),false,now);
        return(document,new(access,document.AccessExpires,store.WorkspaceId),refresh);
    }
    public SessionDocument? Validate(string? authorization)
    {
        if(authorization is null || !authorization.StartsWith("Bearer ",StringComparison.Ordinal))return null;
        var token=authorization[7..];if(token.Length!=43)return null;
        var session=store.FindSession(DomainRules.Hash(token),false);
        return session is {Revoked:false}&&session.AccessExpires>DateTimeOffset.UtcNow?session:null;
    }
    public (SessionResponse Access,string Refresh) Refresh(string? token)
    {
        if(token?.Length!=43)throw new DomainException("SESSION_EXPIRED","会话已经失效，请重新配对。",401);
        var hash=DomainRules.Hash(token);var current=store.FindSession(hash,true);
        if(current is null||current.Revoked||current.RefreshExpires<=DateTimeOffset.UtcNow)throw new DomainException("SESSION_EXPIRED","会话已经失效，请重新配对。",401);
        var issued=Issue(current.Id);
        if(!store.RotateSession(hash,issued.Document))throw new DomainException("REFRESH_ALREADY_USED","刷新凭据已经使用，请重新建立会话。",401);
        return(issued.Access,issued.Refresh);
    }
    public void Logout(SessionDocument? session,string? refresh)
    {
        session??=refresh is null?null:store.FindSession(DomainRules.Hash(refresh),true);
        if(session is not null)store.RevokeSession(session.Id);
    }
}
