using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Fmpostor.Api.Events.Client;
using Fmpostor.Api.Net;
using Microsoft.Extensions.Logging;

namespace Fmpostor.Server.WebAdmin;

/// <summary>
///     Parses the Reactor mod-handshake payload that modded clients append after the
///     normal Among Us handshake, and exposes the mod list per client.
///     Protocol reference: https://github.com/NuclearPowered/Reactor.Empostor
/// </summary>
public sealed class ReactorModService
{
    // "reactor" in ASCII (7 bytes), packed as upper 56 bits of a uint64 in little-endian.
    private const ulong ReactorMagic = 0x72656163746f72;

    private readonly ILogger<ReactorModService> _logger;
    private readonly ConcurrentDictionary<IHazelConnection, (ReactorModInfo Info, DateTime Time)> _pending = new();

    public ReactorModService(ILogger<ReactorModService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    ///     Parses the handshake data of a client connection event. Safe to call for
    ///     every connection; non-Reactor clients are simply skipped.
    /// </summary>
    public void ParseAndStore(IClientConnectionEvent e)
    {
        var reader = e.HandshakeData;
        var savedPos = reader.Position;

        try
        {
            reader.Seek(0);
            if (!SkipAuHandshake(reader))
            {
                return;
            }

            if (!TryFindReactorHeader(reader, out var protocolVersion))
            {
                return;
            }

            var mods = Array.Empty<ClientMod>();
            if (protocolVersion >= 2) // ReactorProtocolVersion.V3 = 2
            {
                mods = ReadModList(reader);
            }

            var info = new ReactorModInfo($"v{protocolVersion}", mods);
            _pending[e.Connection] = (info, DateTime.UtcNow);

            if (mods.Length > 0)
            {
                _logger.LogInformation(
                    "ReactorMods {Name} has {Count} mod(s) (protocol {Proto}): {Mods}",
                    e.Connection.Client?.Name ?? "unknown",
                    mods.Length,
                    protocolVersion,
                    string.Join(", ", mods.Select(m => $"{m.Id} {m.Version}")));
            }
        }
        catch
        {
            // Not a Reactor client or parse error — silently skip.
        }
        finally
        {
            reader.Seek(savedPos);
        }
    }

    /// <summary>
    ///     Moves the parsed mod info (if any) into the client's Items dictionary
    ///     under "ReactorMods". Call after the client is registered.
    /// </summary>
    public void AttachToClient(IClient client)
    {
        if (client == null)
        {
            return;
        }

        if (_pending.TryRemove(client.Connection, out var entry))
        {
            client.Items["ReactorMods"] = entry.Info;
            return;
        }

        // Stale entries from connections that never got registered.
        var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(5);
        foreach (var kv in _pending)
        {
            if (kv.Value.Time < cutoff)
            {
                _pending.TryRemove(kv.Key, out _);
            }
        }
    }

    /// <summary>
    ///     Formats the stored mod list as "id version" strings, or an empty list.
    /// </summary>
    public static List<string> FormatMods(IClient client)
    {
        if (client == null || !client.Items.TryGetValue("ReactorMods", out var obj) || obj is not ReactorModInfo info)
        {
            return new List<string>();
        }

        return info.Mods.Select(m => m.RequiredOnAllClients ? $"{m.Id} {m.Version} (必需)" : $"{m.Id} {m.Version}").ToList();
    }

    private static bool SkipAuHandshake(IMessageReader reader)
    {
        // GameVersion (int32, 4 bytes)
        if (!CanRead(reader, 4))
        {
            return false;
        }

        reader.ReadInt32();

        // Name (Hazel string: packed int length + UTF-8 bytes)
        if (!TrySkipString(reader))
        {
            return false;
        }

        // LastNonce (uint32, 4 bytes)
        if (!CanRead(reader, 4))
        {
            return false;
        }

        reader.ReadUInt32();

        // Language (uint32, 4 bytes)
        if (!CanRead(reader, 4))
        {
            return false;
        }

        reader.ReadUInt32();

        // ChatMode (byte)
        if (!CanRead(reader, 1))
        {
            return false;
        }

        reader.ReadByte();

        // PlatformSpecificData (Hazel sub-message)
        if (!TrySkipMessage(reader))
        {
            return false;
        }

        // ProductUserId (Hazel string) — may be absent for older clients
        if (CanRead(reader, 1))
        {
            TrySkipString(reader);
        }

        // CrossplayFlags (uint32, 4 bytes) — may be absent for older clients
        if (CanRead(reader, 4))
        {
            reader.ReadUInt32();
        }

        return true;
    }

    // Scan remaining bytes for the Reactor header magic.
    private static bool TryFindReactorHeader(IMessageReader reader, out int protocolVersion)
    {
        protocolVersion = 0;

        while (reader.Position + 8 <= reader.Length)
        {
            var pos = reader.Position;
            var value = reader.ReadUInt64();
            var magic = value >> 8;

            if (magic == ReactorMagic)
            {
                protocolVersion = unchecked((byte)(value & 0xFF));
                return true;
            }

            // Not found at this position, advance by 1 byte and retry.
            reader.Seek(pos + 1);
        }

        return false;
    }

    private static ClientMod[] ReadModList(IMessageReader reader)
    {
        if (reader.Position >= reader.Length)
        {
            return Array.Empty<ClientMod>();
        }

        var modCount = reader.ReadPackedInt32();
        if (modCount < 0 || modCount > 512)
        {
            return Array.Empty<ClientMod>();
        }

        var mods = new ClientMod[modCount];

        for (var i = 0; i < modCount; i++)
        {
            var id = reader.ReadString();
            var version = reader.ReadString();
            var flags = reader.ReadUInt16();
            var requiredOnAll = (flags & 0x01) != 0;

            // When RequireOnAllClients flag is set, an extra name string follows.
            if (requiredOnAll && reader.Position < reader.Length)
            {
                _ = reader.ReadString();
            }

            mods[i] = new ClientMod(id, version, requiredOnAll);
        }

        return mods;
    }

    private static bool CanRead(IMessageReader reader, int count)
        => reader.Position + count <= reader.Length;

    private static bool TrySkipString(IMessageReader reader)
    {
        if (!CanRead(reader, 1))
        {
            return false;
        }

        var pos = reader.Position;
        var len = reader.ReadPackedInt32();

        if (len < 0 || reader.Position + len > reader.Length)
        {
            reader.Seek(pos);
            return false;
        }

        reader.Seek(reader.Position + len);
        return true;
    }

    private static bool TrySkipMessage(IMessageReader reader)
    {
        if (!CanRead(reader, 2))
        {
            return false;
        }

        var pos = reader.Position;
        var len = reader.ReadUInt16();

        if (reader.Position + len > reader.Length)
        {
            reader.Seek(pos);
            return false;
        }

        reader.Seek(reader.Position + len);
        return true;
    }
}

public sealed class ReactorModInfo
{
    public ReactorModInfo(string protocolVersion, IReadOnlyList<ClientMod> mods)
    {
        ProtocolVersion = protocolVersion;
        Mods = mods;
    }

    public string ProtocolVersion { get; }

    public IReadOnlyList<ClientMod> Mods { get; }
}

public sealed class ClientMod
{
    public ClientMod(string id, string version, bool requiredOnAllClients)
    {
        Id = id;
        Version = version;
        RequiredOnAllClients = requiredOnAllClients;
    }

    public string Id { get; }

    public string Version { get; }

    public bool RequiredOnAllClients { get; }
}