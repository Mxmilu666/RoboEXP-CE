namespace RoboExp.Core.Install.Inno;

/// <summary>Minimal PE/COFF reader: locates an <c>RT_RCDATA</c> resource by numeric id (Inno Setup stores its loader
/// offset table in RCDATA #11111 since 5.1.5).</summary>
internal static class PeResources
{
    private const int RtRcData = 10;

    /// <summary>Absolute file offset of the first language variant of RCDATA <paramref name="id"/>, or null.</summary>
    public static long? FindRcData(Stream pe, int id)
    {
        var r = new InnoReader(pe);
        if (pe.Length < 0x40) return null;
        pe.Position = 0;
        if (r.U8() != 'M' || r.U8() != 'Z') return null;
        pe.Position = 0x3C;
        var peOffset = r.U32();
        if (peOffset + 24 > pe.Length) return null;
        pe.Position = peOffset;
        if (r.U32() != 0x00004550) return null; // "PE\0\0"
        r.U8(); r.U8(); // machine
        var sectionCount = r.I16() & 0xFFFF;
        pe.Position += 12;
        var optionalSize = r.I16() & 0xFFFF;
        r.I16(); // characteristics
        var optionalStart = pe.Position;
        var magic = r.I16() & 0xFFFF;
        var dataDirs = optionalStart + (magic == 0x20B ? 112 : 96);
        pe.Position = dataDirs + 2 * 8; // IMAGE_DIRECTORY_ENTRY_RESOURCE
        var resourceRva = r.U32();
        if (resourceRva == 0) return null;

        var sections = new List<(uint Va, uint VirtualSize, uint RawSize, uint RawPtr)>();
        pe.Position = optionalStart + optionalSize;
        for (var i = 0; i < sectionCount; i++)
        {
            pe.Position += 8; // name
            var vsize = r.U32();
            var va = r.U32();
            var rawSize = r.U32();
            var rawPtr = r.U32();
            pe.Position += 16;
            sections.Add((va, vsize, rawSize, rawPtr));
        }
        long? ToFile(uint rva)
        {
            foreach (var s in sections)
                if (rva >= s.Va && rva < s.Va + Math.Max(s.VirtualSize, s.RawSize)) return s.RawPtr + (rva - s.Va);
            return null;
        }

        var root = ToFile(resourceRva);
        if (root == null) return null;
        var typeDir = FindEntry(pe, r, root.Value, RtRcData);
        if (typeDir is not { IsDirectory: true }) return null;
        var nameDir = FindEntry(pe, r, root.Value + typeDir.Value.Offset, id);
        if (nameDir is not { IsDirectory: true }) return null;
        var langDir = FindEntry(pe, r, root.Value + nameDir.Value.Offset, null);
        if (langDir is not { IsDirectory: false }) return null;
        pe.Position = root.Value + langDir.Value.Offset; // IMAGE_RESOURCE_DATA_ENTRY
        return ToFile(r.U32());
    }

    /// <summary>Finds an entry with numeric id (or the first entry when <paramref name="id"/> is null) in a resource directory.</summary>
    private static (bool IsDirectory, uint Offset)? FindEntry(Stream pe, InnoReader r, long dir, int? id)
    {
        pe.Position = dir + 12;
        var named = r.I16() & 0xFFFF;
        var ids = r.I16() & 0xFFFF;
        for (var i = 0; i < named + ids; i++)
        {
            var name = r.U32();
            var target = r.U32();
            if (id == null || ((name & 0x80000000) == 0 && name == id))
                return ((target & 0x80000000) != 0, target & 0x7FFFFFFF);
        }
        return null;
    }
}
