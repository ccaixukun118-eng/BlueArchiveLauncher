namespace BlueArchiveLauncher.Core.Models;

public sealed record ServerDefinition(
    ServerId Id,
    string Code,
    string DisplayName,
    string RegionName,
    string DefaultDataSource,
    string DefaultMumuInstance);
