namespace Fmpostor.Api.Innersloth.GameOptions.RoleOptions;

/// <summary>
///     Role options for the Spirit Guide (Among Us 19.0), internally referred to as
///     the "Influencer" in some localisations.
/// </summary>
public class SpiritGuideRoleOptions : IRoleOptions
{
    public SpiritGuideRoleOptions(byte version)
    {
        Version = version;
    }

    public byte Version { get; }

    public RoleTypes Type => RoleTypes.SpiritGuide;

    /// <summary>Ability cooldown, in seconds. Serialised as a single byte.</summary>
    public float SpiritGuideCooldownSeconds { get; set; } = 20f;

    public static SpiritGuideRoleOptions Deserialize(IMessageReader reader, byte version)
    {
        var options = new SpiritGuideRoleOptions(version);

        options.SpiritGuideCooldownSeconds = reader.ReadByte();

        return options;
    }

    public void Serialize(IMessageWriter writer)
    {
        writer.Write((byte)SpiritGuideCooldownSeconds);
    }
}