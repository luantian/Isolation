namespace IsolationLeakage.App.Services;

public static class AppServices
{
    public static MasterDataStore MasterData { get; } = new();
}
