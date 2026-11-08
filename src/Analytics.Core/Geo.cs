using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Analytics.Core;

/// <summary>An inclusive address range registered to a country. IPv4 values fit in the low 32 bits.</summary>
public readonly record struct GeoRange(bool IsIPv6, UInt128 Start, UInt128 End, string Country);

/// <summary>Parses the "delegated extended" statistics files published by the Regional Internet Registries.</summary>
public static class RirParser
{
    public static IEnumerable<GeoRange> Parse(TextReader reader)
    {
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0 || line[0] == '#') continue;

            // registry|cc|type|start|value|date|status[|extensions]
            // Version and summary lines have fewer fields, so they are skipped here.
            var f = line.Split('|');
            if (f.Length < 7) continue;

            var isV6 = f[2] == "ipv6";
            if (!isV6 && f[2] != "ipv4") continue;
            if (f[6] is not ("allocated" or "assigned")) continue;

            var cc = f[1];
            if (cc.Length != 2 || !char.IsAsciiLetter(cc[0]) || !char.IsAsciiLetter(cc[1])) continue;
            cc = cc.ToUpperInvariant();
            if (cc == "ZZ") continue;

            if (!IPAddress.TryParse(f[3], out var start)) continue;
            if (!ulong.TryParse(f[4], out var n) || n == 0) continue;

            if (!isV6)
            {
                // For IPv4 the value is a number of addresses, not necessarily a power of two.
                if (start.AddressFamily != AddressFamily.InterNetwork) continue;
                var s = BinaryPrimitives.ReadUInt32BigEndian(start.GetAddressBytes());
                var e = (ulong)s + n - 1;
                if (e > uint.MaxValue) continue;
                yield return new GeoRange(false, s, e, cc);
            }
            else
            {
                // For IPv6 the value is a prefix length.
                if (start.AddressFamily != AddressFamily.InterNetworkV6 || n > 128) continue;
                var s = BinaryPrimitives.ReadUInt128BigEndian(start.GetAddressBytes());
                var e = n == 128 ? s : s | (UInt128.MaxValue >> (int)n);
                yield return new GeoRange(true, s, e, cc);
            }
        }
    }
}

/// <summary>
/// Immutable in-memory IP to country lookup (binary search, no allocation per lookup).
/// If ranges are nested, the most specific (innermost) range wins.
/// </summary>
public sealed class GeoIpDatabase
{
    private readonly Table<uint> _v4;
    private readonly Table<UInt128> _v6;

    private GeoIpDatabase(Table<uint> v4, Table<UInt128> v6)
    {
        _v4 = v4;
        _v6 = v6;
    }

    public static GeoIpDatabase Empty { get; } = Build([]);

    public int Count => _v4.Count + _v6.Count;

    public static GeoIpDatabase Build(IEnumerable<GeoRange> ranges)
    {
        var v4 = new List<(uint Start, uint End, string Cc)>();
        var v6 = new List<(UInt128 Start, UInt128 End, string Cc)>();
        foreach (var r in ranges)
        {
            if (r.IsIPv6) v6.Add((r.Start, r.End, r.Country));
            else v4.Add(((uint)r.Start, (uint)r.End, r.Country));
        }
        return new GeoIpDatabase(new Table<uint>(v4), new Table<UInt128>(v6));
    }

    /// <summary>Returns the ISO 3166-1 alpha-2 country code, or null if the address is not in any known range.</summary>
    public string? Lookup(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

        Span<byte> bytes = stackalloc byte[16];
        if (!ip.TryWriteBytes(bytes, out var written)) return null;

        return written == 4
            ? _v4.Find(BinaryPrimitives.ReadUInt32BigEndian(bytes[..4]))
            : _v6.Find(BinaryPrimitives.ReadUInt128BigEndian(bytes[..16]));
    }

    private sealed class Table<T> where T : struct, IComparable<T>
    {
        private readonly T[] _starts;
        private readonly T[] _ends;
        private readonly T[] _maxEndUpTo; // running maximum of _ends, lets the backwards walk stop early
        private readonly string[] _countries;

        public Table(List<(T Start, T End, string Cc)> items)
        {
            // Sort by start ascending; for equal starts put the narrower range last so it is found first.
            items.Sort(static (a, b) =>
            {
                var c = a.Start.CompareTo(b.Start);
                return c != 0 ? c : b.End.CompareTo(a.End);
            });

            var n = items.Count;
            _starts = new T[n];
            _ends = new T[n];
            _maxEndUpTo = new T[n];
            _countries = new string[n];

            for (var i = 0; i < n; i++)
            {
                _starts[i] = items[i].Start;
                _ends[i] = items[i].End;
                _countries[i] = items[i].Cc;
                _maxEndUpTo[i] = i > 0 && _maxEndUpTo[i - 1].CompareTo(_ends[i]) > 0 ? _maxEndUpTo[i - 1] : _ends[i];
            }
        }

        public int Count => _starts.Length;

        public string? Find(T address)
        {
            // Find the first index whose start is greater than the address.
            int lo = 0, hi = _starts.Length;
            while (lo < hi)
            {
                var mid = (int)(((uint)lo + (uint)hi) >> 1);
                if (_starts[mid].CompareTo(address) <= 0) lo = mid + 1;
                else hi = mid;
            }

            // Walk back from the predecessor. For disjoint ranges this stops after one step; for nested ranges the
            // first containing range found has the greatest start, i.e. it is the innermost one.
            for (var i = lo - 1; i >= 0 && _maxEndUpTo[i].CompareTo(address) >= 0; i--)
            {
                if (_ends[i].CompareTo(address) >= 0) return _countries[i];
            }

            return null;
        }
    }
}
