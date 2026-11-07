using Microsoft.Win32;

namespace FlutterBar.Core.Integration;

/// <summary>A string value under HKCU. <paramref name="Data"/> is never environment-expanded.</summary>
public sealed record RegistryValue(string Data, RegistryValueKind Kind);

/// <summary>
/// HKCU access behind an interface so the PATH and Run-key logic is tested against a fake: tests must never touch the
/// user's real registry. The app supplies the Windows implementation.
/// </summary>
public interface IUserRegistry
{
    /// <summary>HKCU\<paramref name="subKey"/>\<paramref name="name"/> without expanding <c>%VAR%</c>; null when the key or value is missing.</summary>
    RegistryValue? Read(string subKey, string name);

    /// <summary>Writes the value with exactly <paramref name="kind"/>, creating the key when needed.</summary>
    void Write(string subKey, string name, string data, RegistryValueKind kind);

    /// <summary>Deletes the value; nothing happens when it does not exist.</summary>
    void Delete(string subKey, string name);
}
