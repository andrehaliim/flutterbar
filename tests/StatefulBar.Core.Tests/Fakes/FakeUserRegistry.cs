using StatefulBar.Core.Integration;
using Microsoft.Win32;

namespace StatefulBar.Core.Tests.Fakes;

/// <summary>In-memory HKCU: tests never touch the real PATH or Run key.</summary>
public sealed class FakeUserRegistry : IUserRegistry
{
    private readonly Dictionary<(string Key, string Name), RegistryValue> _values = new(new KeyComparer());

    public int Writes { get; private set; }

    public void Seed(string subKey, string name, string data, RegistryValueKind kind) => _values[(subKey, name)] = new RegistryValue(data, kind);

    public RegistryValue? Read(string subKey, string name) => _values.TryGetValue((subKey, name), out var value) ? value : null;

    public void Write(string subKey, string name, string data, RegistryValueKind kind)
    {
        Writes++;
        _values[(subKey, name)] = new RegistryValue(data, kind);
    }

    public void Delete(string subKey, string name)
    {
        Writes++;
        _values.Remove((subKey, name));
    }

    // Registry key and value names are case-insensitive.
    private sealed class KeyComparer : IEqualityComparer<(string Key, string Name)>
    {
        public bool Equals((string Key, string Name) x, (string Key, string Name) y) =>
            string.Equals(x.Key, y.Key, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Key, string Name) obj) =>
            HashCode.Combine(obj.Key.ToUpperInvariant(), obj.Name.ToUpperInvariant());
    }
}
