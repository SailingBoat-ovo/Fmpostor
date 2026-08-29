namespace Fmpostor.Api.Innersloth.GameOptions;

public class HideNSeekGameOptions : IGameOptions
{
    public const int LatestVersion = 11;

    public HideNSeekGameOptions(byte version = LatestVersion)
    {
        Version = version;
        IGameOptions.EnsureVersionIsModular<HideNSeekGameOptions>(version);
    }

    /// <inheritdoc />
    public byte Version { get; }

    /// <inheritdoc />
    public GameModes GameMode => GameModes.HideNSeek;

    /// <inheritdoc />
    public SpecialGameModes SpecialMode { get; set; } = SpecialGameModes.None;

    /// <inheritdoc />
    public RulesPresets RulesPreset { get; set; } = RulesPresets.Custom;

    /// <inheritdoc />
    public byte MaxPlayers { get; set; } = 15;

    /// <inheritdoc />
    public GameKeywords Keywords { get; set; } = GameKeywords.English;

    /// <inheritdoc />
    public MapTypes Map { get; set; } = MapTypes.Skeld;

    /// <inheritdoc />
    public int NumImpostors { get; set; } = 1;

    /// <inheritdoc />
    public bool IsDefaults { get; set; } = true;

    /// <summary>
    ///     Gets or sets the Player speed modifier.
    /// </summary>
    public float PlayerSpeedMod { get; set; } = 1f;

    /// <summary>
    ///     Gets or sets the Light modifier for the players that are members of the crew as a multiplier value.
    /// </summary>
    public float CrewLightMod { get; set; } = 1f;

    /// <summary>
    ///     Gets or sets the Light modifier for the players that are Impostors as a multiplier value.
    /// </summary>
    public float ImpostorLightMod { get; set; } = 1f;

    /// <summary>
    ///     Gets or sets the number of common tasks.
    /// </summary>
    public int NumCommonTasks { get; set; } = 1;

    /// <summary>
    ///     Gets or sets the number of long tasks.
    /// </summary>
    public int NumLongTasks { get; set; } = 1;

    /// <summary>
    ///     Gets or sets the number of short tasks.
    /// </summary>
    public int NumShortTasks { get; set; } = 2;

    public int CrewmateVentUses { get; set; } = 1;

    public float CrewmateTimeInVent { get; set; } = 3f;

    public float HidingTime { get; set; } = 200f;

    public float CrewmateFlashlightSize { get; set; } = 0.35f;

    public float ImpostorFlashlightSize { get; set; } = 0.25f;

    public bool UseFlashlight { get; set; } = true;

    public bool FinalHideSeekMap { get; set; } = true;

    public float FinalHideTime { get; set; } = 50f;

    public float FinalSeekerSpeed { get; set; } = 1.2f;

    public bool FinalHidePings { get; set; } = true;

    public bool ShowNames { get; set; } = true;

    public uint SeekerPlayerId { get; set; } = 0xFFFFFFFF;

    public float MaxPingTime { get; set; } = 6f;

    /// <summary>
    ///     Gets or sets the experience level of people in the lobby: Beginner, Intermediate or Expert.
    /// </summary>
    public GameTags Tag { get; set; } = 0;

    // ===== v11 (Among Us 18.0) fields =====
    // The v11 wire layout was completely restructured by Innersloth (see the
    // disassembled HideNSeekGameOptionsV07.Serialize/Deserialize):
    // no SpecialMode/RulesPreset/Tag, new field names/order. Kept separately
    // from the v8-v10 fields above for compatibility with older clients.

    /// <summary>v11: hiding time (was HidingTime).</summary>
    public float EscapeTime { get; set; } = 200f;

    /// <summary>v11: final countdown time (was FinalHideTime).</summary>
    public float FinalCountdownTime { get; set; } = 50f;

    /// <summary>v11: the seeker player id (was SeekerPlayerId).</summary>
    public uint ImpostorPlayerID { get; set; } = 0xFFFFFFFF;

    public static HideNSeekGameOptions Deserialize(IMessageReader reader, byte version)
    {
        var options = new HideNSeekGameOptions(version);
        options.Deserialize(reader);
        return options;
    }

    public void Deserialize(IMessageReader reader)
    {
        if (Version >= 11)
        {
            DeserializeV11(reader);
            return;
        }

        if (Version >= 8)
        {
            SpecialMode = (SpecialGameModes)reader.ReadByte();
            RulesPreset = (RulesPresets)reader.ReadByte();
        }

        MaxPlayers = reader.ReadByte();
        Keywords = (GameKeywords)reader.ReadInt32();
        Map = (MapTypes)reader.ReadByte();
        PlayerSpeedMod = reader.ReadSingle();
        CrewLightMod = reader.ReadSingle();
        ImpostorLightMod = reader.ReadSingle();
        NumCommonTasks = reader.ReadByte();
        NumLongTasks = reader.ReadByte();
        NumShortTasks = reader.ReadByte();
        IsDefaults = reader.ReadBoolean();

        CrewmateVentUses = reader.ReadInt32();
        HidingTime = reader.ReadSingle();
        CrewmateFlashlightSize = reader.ReadSingle();
        ImpostorFlashlightSize = reader.ReadSingle();
        UseFlashlight = reader.ReadBoolean();
        FinalHideSeekMap = reader.ReadBoolean();
        FinalHideTime = reader.ReadSingle();
        FinalSeekerSpeed = reader.ReadSingle();
        FinalHidePings = reader.ReadBoolean();
        ShowNames = reader.ReadBoolean();
        SeekerPlayerId = reader.ReadUInt32();
        MaxPingTime = reader.ReadSingle();
        CrewmateTimeInVent = reader.ReadSingle();

        if (Version >= 9)
        {
            Tag = (GameTags)reader.ReadByte();
        }

        if (Version > LatestVersion)
        {
            IGameOptions.ThrowUnknownVersion<HideNSeekGameOptions>(Version);
        }
    }

    /// <summary>
    ///     v11 wire layout (Among Us 18.0, reverse-engineered from
    ///     HideNSeekGameOptionsV11.Serialize/Deserialize in GameAssembly.dll).
    ///     Note: same field ORDER as v8-v10 (only names changed: HidingTime ->
    ///     EscapeTime, FinalHideTime -> FinalCountdownTime, SeekerPlayerId ->
    ///     ImpostorPlayerID, etc.).
    /// </summary>
    private void DeserializeV11(IMessageReader reader)
    {
        SpecialMode = (SpecialGameModes)reader.ReadByte();
        RulesPreset = (RulesPresets)reader.ReadByte();
        MaxPlayers = reader.ReadByte();
        Keywords = (GameKeywords)reader.ReadInt32();
        Map = (MapTypes)reader.ReadByte();
        PlayerSpeedMod = reader.ReadSingle();
        CrewLightMod = reader.ReadSingle();
        ImpostorLightMod = reader.ReadSingle();
        NumCommonTasks = reader.ReadByte();
        NumLongTasks = reader.ReadByte();
        NumShortTasks = reader.ReadByte();
        IsDefaults = reader.ReadBoolean();
        CrewmateVentUses = reader.ReadInt32();
        EscapeTime = reader.ReadSingle();
        CrewmateFlashlightSize = reader.ReadSingle();
        ImpostorFlashlightSize = reader.ReadSingle();
        UseFlashlight = reader.ReadBoolean();
        FinalHideSeekMap = reader.ReadBoolean();
        FinalCountdownTime = reader.ReadSingle();
        FinalSeekerSpeed = reader.ReadSingle();
        FinalHidePings = reader.ReadBoolean();
        ShowNames = reader.ReadBoolean();
        ImpostorPlayerID = reader.ReadUInt32();
        MaxPingTime = reader.ReadSingle();
        CrewmateTimeInVent = reader.ReadSingle();
        Tag = (GameTags)reader.ReadByte();

        if (Version > LatestVersion)
        {
            IGameOptions.ThrowUnknownVersion<HideNSeekGameOptions>(Version);
        }
    }

    public void Serialize(IMessageWriter writer)
    {
        if (Version >= 11)
        {
            SerializeV11(writer);
            return;
        }

        if (Version >= 8)
        {
            writer.Write((byte)SpecialMode);
            writer.Write((byte)RulesPreset);
        }

        writer.Write(MaxPlayers);
        writer.Write((uint)Keywords);
        writer.Write((byte)Map);
        writer.Write(PlayerSpeedMod);
        writer.Write(CrewLightMod);
        writer.Write(ImpostorLightMod);
        writer.Write((byte)NumCommonTasks);
        writer.Write((byte)NumLongTasks);
        writer.Write((byte)NumShortTasks);
        writer.Write(IsDefaults);

        writer.Write(CrewmateVentUses);
        writer.Write(HidingTime);
        writer.Write(CrewmateFlashlightSize);
        writer.Write(ImpostorFlashlightSize);
        writer.Write(UseFlashlight);
        writer.Write(FinalHideSeekMap);
        writer.Write(FinalHideTime);
        writer.Write(FinalSeekerSpeed);
        writer.Write(FinalHidePings);
        writer.Write(ShowNames);
        writer.Write(SeekerPlayerId);
        writer.Write(MaxPingTime);
        writer.Write(CrewmateTimeInVent);

        if (Version >= 9)
        {
            writer.Write((byte)Tag);
        }

        if (Version > LatestVersion)
        {
            IGameOptions.ThrowUnknownVersion<HideNSeekGameOptions>(Version);
        }
    }

    private void SerializeV11(IMessageWriter writer)
    {
        writer.Write((byte)SpecialMode);
        writer.Write((byte)RulesPreset);
        writer.Write(MaxPlayers);
        writer.Write((uint)Keywords);
        writer.Write((byte)Map);
        writer.Write(PlayerSpeedMod);
        writer.Write(CrewLightMod);
        writer.Write(ImpostorLightMod);
        writer.Write((byte)NumCommonTasks);
        writer.Write((byte)NumLongTasks);
        writer.Write((byte)NumShortTasks);
        writer.Write(IsDefaults);
        writer.Write(CrewmateVentUses);
        writer.Write(EscapeTime);
        writer.Write(CrewmateFlashlightSize);
        writer.Write(ImpostorFlashlightSize);
        writer.Write(UseFlashlight);
        writer.Write(FinalHideSeekMap);
        writer.Write(FinalCountdownTime);
        writer.Write(FinalSeekerSpeed);
        writer.Write(FinalHidePings);
        writer.Write(ShowNames);
        writer.Write(ImpostorPlayerID);
        writer.Write(MaxPingTime);
        writer.Write(CrewmateTimeInVent);
        writer.Write((byte)Tag);

        if (Version > LatestVersion)
        {
            IGameOptions.ThrowUnknownVersion<HideNSeekGameOptions>(Version);
        }
    }
}
