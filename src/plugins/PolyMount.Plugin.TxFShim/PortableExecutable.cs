namespace PolyMount.Plugins.TxFShim;

/// <summary>Just enough of a PE reader to answer one question: what does this image import.
///
/// The answer it serves is narrow — which images reach a transactional API, directly or through a
/// DLL that sits beside them — so this walks the import directory and nothing else. An image that
/// cannot be read is reported as importing nothing: a directory of engine binaries holds plenty of
/// files that are not worth failing an install over, and the caller records the reason.
/// </summary>
internal static class PortableExecutable
{
    /// <summary>What one image imports: the DLLs by name, and the functions it asks for by name.
    /// Import-by-ordinal entries are left out of <paramref name="Names"/> — an ordinal cannot be
    /// matched against a name, and none of the APIs this looks for are imported that way.</summary>
    internal sealed record Imports(IReadOnlyList<string> Modules, IReadOnlyList<string> Names)
    {
        internal static Imports Empty { get; } = new(Array.Empty<string>(), Array.Empty<string>());

        internal bool IsEmpty => Modules.Count == 0 && Names.Count == 0;
    }

    private const uint PeSignature = 0x00004550; // "PE\0\0"
    private const ushort Pe32Magic = 0x010B;
    private const ushort Pe32PlusMagic = 0x020B;

    internal static Imports Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return Read(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Imports.Empty;
        }
    }

    private static Imports Read(Stream stream)
    {
        if (stream.Length < 0x40) return Imports.Empty;
        var reader = new BinaryReader(stream);

        stream.Position = 0x3C; // e_lfanew
        int peOffset = reader.ReadInt32();
        if (peOffset <= 0 || peOffset + 24 > stream.Length) return Imports.Empty;

        stream.Position = peOffset;
        if (reader.ReadUInt32() != PeSignature) return Imports.Empty;

        reader.ReadUInt16();                    // Machine
        int sectionCount = reader.ReadUInt16();
        stream.Position += 12;                  // TimeDateStamp, PointerToSymbolTable, NumberOfSymbols
        int optionalHeaderSize = reader.ReadUInt16();
        stream.Position += 2;                   // Characteristics

        long optionalStart = stream.Position;
        if (optionalStart + optionalHeaderSize > stream.Length) return Imports.Empty;
        ushort magic = reader.ReadUInt16();
        bool pe32Plus = magic == Pe32PlusMagic;
        if (!pe32Plus && magic != Pe32Magic) return Imports.Empty;

        // The data directories follow a fixed-size prefix whose length differs between PE32 and
        // PE32+. Reading the count first is what keeps a short header from being walked past.
        stream.Position = optionalStart + (pe32Plus ? 108 : 92); // NumberOfRvaAndSizes
        uint directoryCount = reader.ReadUInt32();
        if (directoryCount < 2) return Imports.Empty;

        stream.Position = optionalStart + (pe32Plus ? 112 : 96) + 8; // import directory (index 1)
        uint importRva = reader.ReadUInt32();
        uint importSize = reader.ReadUInt32();
        if (importRva == 0 || importSize == 0) return Imports.Empty;

        long sectionStart = optionalStart + optionalHeaderSize;
        var sections = new List<(uint VirtualAddress, uint VirtualSize, uint RawSize, uint RawPointer)>(sectionCount);
        for (int i = 0; i < sectionCount; i++)
        {
            long entry = sectionStart + (long)i * 40;
            if (entry + 40 > stream.Length) return Imports.Empty;
            stream.Position = entry + 8;        // Name
            uint virtualSize = reader.ReadUInt32();
            uint virtualAddress = reader.ReadUInt32();
            uint rawSize = reader.ReadUInt32();
            uint rawPointer = reader.ReadUInt32();
            sections.Add((virtualAddress, virtualSize, rawSize, rawPointer));
        }

        long? OffsetOf(uint rva)
        {
            foreach (var section in sections)
            {
                uint span = Math.Max(section.VirtualSize, section.RawSize);
                if (rva >= section.VirtualAddress && rva < section.VirtualAddress + (ulong)span)
                {
                    long offset = section.RawPointer + (long)(rva - section.VirtualAddress);
                    return offset < stream.Length ? offset : null;
                }
            }
            return null;
        }

        string? AsciiAt(uint rva)
        {
            if (OffsetOf(rva) is not { } offset) return null;
            stream.Position = offset;
            var bytes = new List<byte>(32);
            for (int i = 0; i < 512 && stream.Position < stream.Length; i++)
            {
                byte value = reader.ReadByte();
                if (value == 0) break;
                bytes.Add(value);
            }
            return bytes.Count == 0 ? null : System.Text.Encoding.ASCII.GetString(bytes.ToArray());
        }

        var modules = new List<string>();
        var names = new List<string>();

        if (OffsetOf(importRva) is not { } descriptorOffset) return Imports.Empty;
        for (int index = 0; index < 4096; index++)
        {
            long descriptor = descriptorOffset + (long)index * 20;
            if (descriptor + 20 > stream.Length) break;
            stream.Position = descriptor;
            uint originalThunk = reader.ReadUInt32();
            reader.ReadUInt32();                // TimeDateStamp
            reader.ReadUInt32();                // ForwarderChain
            uint nameRva = reader.ReadUInt32();
            uint firstThunk = reader.ReadUInt32();
            if (originalThunk == 0 && nameRva == 0 && firstThunk == 0) break; // terminator

            if (AsciiAt(nameRva) is { } module && !modules.Contains(module, StringComparer.OrdinalIgnoreCase))
                modules.Add(module);

            uint thunkRva = originalThunk != 0 ? originalThunk : firstThunk;
            if (OffsetOf(thunkRva) is not { } thunkOffset) continue;
            int entrySize = pe32Plus ? 8 : 4;
            for (int slot = 0; slot < 65536; slot++)
            {
                long at = thunkOffset + (long)slot * entrySize;
                if (at + entrySize > stream.Length) break;
                stream.Position = at;
                ulong value = pe32Plus ? reader.ReadUInt64() : reader.ReadUInt32();
                if (value == 0) break;
                ulong ordinalFlag = pe32Plus ? 0x8000000000000000UL : 0x80000000UL;
                if ((value & ordinalFlag) != 0) continue; // by ordinal: no name to match
                if (AsciiAt((uint)value + 2) is { } function && !names.Contains(function, StringComparer.Ordinal))
                    names.Add(function);
            }
        }

        return modules.Count == 0 && names.Count == 0 ? Imports.Empty : new Imports(modules, names);
    }
}
