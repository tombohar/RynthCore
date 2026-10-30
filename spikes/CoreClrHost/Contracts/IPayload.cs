namespace Contracts;

/// <summary>What the Shim (default ALC) sees of a payload living in a collectible ALC.</summary>
public interface IPayload
{
    /// <summary>Address of an [UnmanagedCallersOnly] stdcall (int,int)->int method, or 0 with an error.</summary>
    nint GetUcoPointer(out string? error);

    /// <summary>Marshal.GetFunctionPointerForDelegate of a stdcall (int,int)->int delegate the payload keeps alive.</summary>
    nint GetDelegatePointer();

    /// <summary>Does the work selected by the mode bits; returns a short description.</summary>
    string Work(int mode);

    /// <summary>Stops everything Work started.</summary>
    void Shutdown();
}
