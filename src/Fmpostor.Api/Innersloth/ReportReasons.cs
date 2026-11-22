namespace Fmpostor.Api.Innersloth
{
    /// <summary>Reason picked in the game's own report menu (the button next to the chat box).</summary>
    public enum ReportReasons : byte
    {
        InappropriateName = 0,
        InappropriateChat = 1,
        CheatingHacking = 2,
        HarassmentMisconduct = 3,
    }
}
