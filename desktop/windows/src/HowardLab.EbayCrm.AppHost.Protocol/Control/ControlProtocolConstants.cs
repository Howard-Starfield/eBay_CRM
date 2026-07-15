namespace HowardLab.EbayCrm.AppHost.Protocol.Control;

public static class ControlProtocolConstants
{
    public const int CurrentVersion = 2;
    public const string FixtureBuildIdentity = "HowardLab.EbayCrm.AppHost.Fixture/1";
    public const int MaxFrameBytes = 65_536;
    public const int MaxFramesPerSession = 1_024;
    public const int MaxTextFieldChars = 1_024;
    public const long MaxGeneration = 9_007_199_254_740_991;
    public const int MaxActiveWorkRemaining = int.MaxValue;
}
