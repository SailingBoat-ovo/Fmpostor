using System;

namespace Fmpostor.Server.WebAdmin;

public class BanEntry
{
    public int Id { get; set; }
    public string? PlayerName { get; set; }
    public string? IpAddress { get; set; }
    public string? Puid { get; set; }
    public string? FriendCode { get; set; }
    public string? Fid { get; set; }
    public string? Reason { get; set; }
    public DateTime BannedAt { get; set; }
    public string? BannedBy { get; set; }
}
