using System;

namespace RynthCore.App;

internal enum AcEmulatorKind
{
    Ace,
    Gdle
}

internal sealed class LaunchServerProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Alias { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public AcEmulatorKind Emulator { get; set; } = AcEmulatorKind.Ace;
    public bool RodatEnabled { get; set; }

    public string DisplayName =>
        string.IsNullOrWhiteSpace(Alias) ? Name : $"{Alias} ({Name})";

    public string ConnectionString =>
        string.IsNullOrWhiteSpace(Host) || Port <= 0 ? string.Empty : $"{Host}:{Port}";

    public LaunchServerProfile Clone()
    {
        return new LaunchServerProfile
        {
            Id = Id,
            Name = Name,
            Alias = Alias,
            Host = Host,
            Port = Port,
            Emulator = Emulator,
            RodatEnabled = RodatEnabled
        };
    }

    public void CopyFrom(LaunchServerProfile source)
    {
        Id = source.Id;
        Name = source.Name;
        Alias = source.Alias;
        Host = source.Host;
        Port = source.Port;
        Emulator = source.Emulator;
        RodatEnabled = source.RodatEnabled;
    }

    public override string ToString()
    {
        return DisplayName;
    }
}
