// MIT License
// Copyright (c) 2025 [tinion] Carl Marvin Sautter
// See LICENSE file in the project root for full license text.

// The following code was heavily inspired by Hendrik Radke's nltpack tool,
// but evolved into a clean reimplementation in C# with additional features.
// See README.md for details.

using System.Diagnostics;
using System.IO;
using System.Text;

namespace RivaXtractCLI;

/**
   Format einer DSA3-Datendatei:
   *** Header:
   | Offset | Zweck                                          |
   |  0-3   | Signatur "ALF "=0x414C4620                     |
   |  4-7   | ?Version des Archivs (0x00000100)              |
   |  8-9   | Länge der Dateitabelle in Einträgen            |
   | 10-13  | Offset Beginn der Dateitabelle                 |
   | 14-15  | Tatsächliche Anzahl der Dateien in der Tabelle |
   | 16-19  | ?Offset Beginn der Daten ("Header-Länge")      |
   | 20-21  | Länge der Modultabelle in Einträgen            |
   | 22-25  | Offset Beginn der Modultabelle                 |
   | 26-27  | Tatsächliche Anzahl der Moduln in der Tabelle  |
   | 28-31  | Offset der unbekannten Daten am Ende der Datei |
   | 32-47  | Leer (16-mal 0x00)                             |
   
   *** Dateitabellen-Einträge: jeweils 28 Bytes
   | Offset | Zweck                                 |
   | 0-12   | Dateiname, 0-Terminiert               |
   | 13     | Unklar. (1)                           |
   | 14-17  | Dateilänge                            |
   | 18-21  | Unklar, vielleicht das Datum? (2)     |
   [ 22-23  | Unklar. (3)                           |
   | 24-27  | Offset in Big-Endian, -30 Bytes Header|

   *** Modultabellen-Einträge: jeweils 28 Bytes
   | Offset | Zweck                                 |
   |--------+---------------------------------------|
   |  0-13? | Name, 0-Terminiert (1)                |
   |  14-17 | Anzahl der entsprechenden Einträge    |
   |  18-21 | Unklar, vielleicht das Datum? (2)     |
   |  22-23 | Unklar.                               |
   |  24-27 | Startnummer in der Dateitabelle*2 (3) |
**/

public interface IHeaderEntry {
    public string Name { get; set; }
    public uint Offset { get; }
    public uint Size { get; }
    public bool Read(Istream iS);
    public void Write(Ostream oS);
    public string Type();
};

internal static class Util {
    // Convert a C-style null-terminated byte array to a .NET string (ASCII)
    public static string CStringToString(byte[] bytes) {
        int i = 0;
        string result = "";
        while (i < bytes.Length && bytes[i] != 0) {
            result += (char)bytes[i];
            i++;
        }
        return result;
    }
}

public class Config {
    public string mArchiveName = "";    
    public string mFilePattern = "*";   
    public bool mDsa3ModuleDirs;        
    public int mOverwriteMode;          
    public char mPathDelim;             
}

public class Dsa3ModuleEntry : IHeaderEntry {
    public string Name { get; set; }  // 14 Bytes
    public uint Size { get; set; }   
    public uint mDosDateTime;

    public ushort mUnknown2;
    public uint Offset { get; set; }

    public List<ushort?> mEntryIndices = new();
    // Preserve exact on-disk name bytes (including any non-zero padding/garbage)
    public byte[] NameRaw14 { get; set; }

    public bool Read(Istream strm) {
        byte[] n = strm.ReadChars(14);
        NameRaw14 = n;
        Name = Util.CStringToString(n);
        Size = strm.Read32();
        mDosDateTime = strm.Read32();
        mUnknown2 = strm.Read16();
        Offset = strm.Read32();
        return true;
    }

    public void Write(Ostream strm) {
        if (NameRaw14 != null && NameRaw14.Length == 14) {
            // write exactly what we read or what JSON provided
            strm.WriteChars(14, NameRaw14);
        } else {
            for (int i = 0; i < 14; i++)
                strm.Write8(i < Name.Length ? (byte)Name[i] : (byte)0);
        }

        strm.Write32(Size);
        strm.Write32(mDosDateTime);
        strm.Write16(mUnknown2);
        strm.Write32(Offset);
    }
    public string Type() { return "DSA3"; }
};


public class Dsa3Entry : IHeaderEntry {
    public string Name { get; set; }
    public byte mUnknown1;
    public uint Size { get; set; }
    public uint mDosDateTime;

    public ushort mUnknown3;
    public uint Offset { get; set; }
    // Preserve exact on-disk name bytes (including any non-zero padding/garbage)
    public byte[] NameRaw13 { get; set; }

    public bool Read(Istream strm) {
        byte[] n = strm.ReadChars(13);
        NameRaw13 = n;
        Name = Util.CStringToString(n);
        mUnknown1 = strm.Read8();
        Size = strm.Read32();
        mDosDateTime = strm.Read32();
        mUnknown3 = strm.Read16();
        Offset = strm.Read32();
        return true;
    }

    public void Write(Ostream strm) {
        if (NameRaw13 != null && NameRaw13.Length == 13) {
            strm.WriteChars(13, NameRaw13);
        } else {
            for (int i = 0; i < 13; i++) {
                strm.Write8(i < Name.Length ? (byte)Name[i] : (byte)0);
            }
        }
        strm.Write8(mUnknown1);
        strm.Write32(Size);
        strm.Write32(mDosDateTime);
        strm.Write16(mUnknown3);
        strm.Write32(Offset);
    }
    public string Type() { return "DSA3"; }
};


public class Dsa3 {
    public ushort Count { get; set; }
    public List<IHeaderEntry> Entries { get; set; } = new();
    public uint HeaderSize { get; set; }
    public uint FileSize { get; set; }

    public byte[] mSignature;
    public byte[] mVersion;
    public ushort mFiletableSize;
    public uint mFiletableOffset;
    public uint mDataOffset;
    public ushort mModuleTableSize;
    public uint mModuleTableOffset;
    public ushort mModuleCount;
    public uint mModMapOffset;
    public byte[] mUnknown1;

    public List<Dsa3ModuleEntry> mModules = new();
    public uint mModMapSize;
    public List<uint> mModMap = new();
    public readonly Config mConfig;
    private byte[][][] mEntryData;

    public Dsa3(Config c) {
        mConfig = c;
    }

    public IList<byte[]> CollectEntryData(Istream inStream) {
        var dataStart = HeaderSize;
        var list = new List<byte[]>((int)Count);
        for (int i = 0; i < Count; i++) {
            var e = (Dsa3Entry)Entries[i];
            inStream.Seekg(e.Offset + dataStart);
            list.Add(inStream.ReadChars((int)e.Size));
        }
        return list;
    }

    public void BuildEntryDataFromIndex(IList<byte[]> entryDataByIndex) {
        if (entryDataByIndex == null || entryDataByIndex.Count != Count)
            throw new ArgumentException("entryDataByIndex count must equal Count.");

        mEntryData = new byte[mModuleCount][][];
        for (int mi = 0; mi < mModuleCount; mi++) {
            var mod = mModules[mi];
            mEntryData[mi] = new byte[mod.Size][];
            for (int slot = 0; slot < mod.Size; slot++) {
                int mapIdx = (int)(mod.Offset / 2 + slot);
                int idx = (int)mModMap[mapIdx];
                if (idx == 0xFFFF) continue; // dummy
                mEntryData[mi][slot] = entryDataByIndex[idx];
            }
        }
    }

    // Strict, verbatim write: DOES NOT touch header offsets or entry offsets.
    // Validates payload sizes match the JSON/file-table sizes.
    public void WriteVerbatim(Ostream outStream, IList<byte[]> entryDataByIndex, bool strictSizes = true) {
        if (entryDataByIndex == null || entryDataByIndex.Count != Count)
            throw new ArgumentException("entryDataByIndex count must equal Count.");

        if (strictSizes) {
            for (int i = 0; i < Count; i++) {
                var e = (Dsa3Entry)Entries[i];
                if ((uint)entryDataByIndex[i].Length != e.Size)
                    throw new InvalidOperationException(
                        $"Size mismatch for entry {i}: JSON={e.Size}, bytes={entryDataByIndex[i].Length}");
            }
        }

        BuildEntryDataFromIndex(entryDataByIndex);
        Write(outStream);
    }


    // Returns the packed (tight) file size if the archive were rebuilt with the
    // current tables + the provided entry bytes.
    public uint ComputePackedSize(IList<byte[]> entryDataByIndex) {
        if (entryDataByIndex == null || entryDataByIndex.Count != Count)
            throw new ArgumentException("entryDataByIndex count must equal Count.");

        // Recompute modmap size (defensive)
        mModMapSize = 0;
        for (int mi = 0; mi < mModuleCount; mi++)
            mModMapSize = Math.Max(mModMapSize, mModules[mi].Offset / 2 + mModules[mi].Size);

        uint header =
            48u +                                  // fixed header
            (uint)mModuleCount * 28u +              // module table
            (uint)Count * 28u +                    // file table
            (uint)mModMapSize * 2u;                 // modmap (uint16 each)

        ulong sum = 0;
        for (int i = 0; i < Count; i++)
            sum += (ulong)entryDataByIndex[i].Length;

        var total = header + (uint)sum;
        return total;
    }

    // Rebuilds offsets and writes a compact archive to the provided output stream.
    // The output stream must be pre-sized (use Ofstream with ComputePackedSize).
    public void RepackTo(Ostream outStream, IList<byte[]> entryDataByIndex) {
        if (entryDataByIndex == null || entryDataByIndex.Count != Count)
            throw new ArgumentException("entryDataByIndex count must equal Count.");

        // 1) Update entry sizes and compute compact relative offsets
        uint rel = 0;
        for (int i = 0; i < Count; i++) {
            var e = (Dsa3Entry)Entries[i];
            e.Size = (uint)entryDataByIndex[i].Length;
            e.Offset = rel;
            rel += e.Size;
        }

        // 2) Layout: header (48), module table, file table, modmap, then data.
        mModuleTableOffset = 48;
        mModuleTableSize = (ushort)mModuleCount;

        mFiletableOffset = mModuleTableOffset + (uint)(mModuleCount * 28);
        mFiletableSize = Count;

        // Recompute modmap size defensively
        mModMapSize = 0;
        for (int mi = 0; mi < mModuleCount; mi++)
            mModMapSize = Math.Max(mModMapSize, mModules[mi].Offset / 2 + mModules[mi].Size);

        mModMapOffset = mFiletableOffset + (uint)Count * 28u;
        mDataOffset = mModMapOffset + (uint)mModMapSize * 2u;
        HeaderSize = mDataOffset;

        // 3) Provide Write(...) with per-module-slot payloads
        mEntryData = new byte[mModuleCount][][];
        for (int mi = 0; mi < mModuleCount; mi++) {
            var mod = mModules[mi];
            mEntryData[mi] = new byte[mod.Size][];
            for (int slot = 0; slot < mod.Size; slot++) {
                int mapIdx = (int)(mod.Offset / 2 + slot);
                int idx = (int)mModMap[mapIdx];
                if (idx == 0xFFFF) continue; // dummy
                mEntryData[mi][slot] = entryDataByIndex[idx];
            }
        }

        // 4) Finally write everything
        Write(outStream);
    }

    public void PrintAllIndices(bool includeDummies = false, bool oneBased = false, string file = "") {
        TextWriter writer = string.IsNullOrEmpty(file) ? DebugTextWriter.Instance : new StreamWriter(file);
        try {
            writer.WriteLine("Filename,Module,ModuleSlot,FileTableIndex");

            for (int mi = 0; mi < mModuleCount; mi++) {
                var module = mModules[mi];

                for (int slot = 0; slot < module.Size; slot++) {
                    int mapIdx = (int)(module.Offset / 2 + slot);
                    int entryIndex = (int)mModMap[mapIdx];
                    var moduleId = mi;

                    if (entryIndex == 0xFFFF) {
                        if (includeDummies) {
                            writer.WriteLine($"{moduleId}, {module.Name}, {(oneBased ? slot + 1 : slot)}, - , - ");
                            //writer.WriteLine($"-,{module.Name},{(oneBased ? slot + 1 : slot)},-");
                        }
                        continue;
                    }

                    if (entryIndex < 0 || entryIndex >= Entries.Count) {
                        Debug.Print($"[WARN] Out-of-range entry index {entryIndex} at module {mi} slot {slot}");
                        continue;
                    }

                    var entry = Entries[entryIndex];
                    string filename = entry.Name; // or: $"{module.Name}{mConfig.mPathDelim}{entry.Name}" if you prefer
                    int slotOut = oneBased ? slot + 1 : slot;
                    int idxOut = oneBased ? entryIndex + 1 : entryIndex;

                    writer.WriteLine($"{moduleId}, {module.Name}, {slotOut}, {idxOut}, {filename}");
                }
            }
        } finally {
            if (writer is StreamWriter sw) sw.Dispose();
        }
    }

    /// <summary>
    /// Redirects WriteLine calls to Debug.WriteLine.
    /// </summary>
    class DebugTextWriter : TextWriter {
        public static readonly DebugTextWriter Instance = new DebugTextWriter();
        public override Encoding Encoding => Encoding.UTF8;
        public override void WriteLine(string? value) => Debug.WriteLine(value);
    }

    public void Write(Ostream strm) {
        strm.WriteChars(4, mSignature);
        strm.WriteChars(4, mVersion);
        strm.Write16(mFiletableSize);
        strm.Write32(mFiletableOffset);
        strm.Write16(Count);
        strm.Write32(mDataOffset);
        strm.Write16(mModuleTableSize);
        strm.Write32(mModuleTableOffset);
        strm.Write16(mModuleCount);
        strm.Write32(mModMapOffset);
        strm.WriteChars(16, mUnknown1);

        strm.Seekg(mModuleTableOffset);

        mModMapSize = 0;
        for (int i = 0; i < mModules.Count; i++) {
            mModules[i].Write(strm);
        }

        strm.Seekg(mFiletableOffset);
        for (int i = 0; i < Entries.Count; i++) {
            Entries[i].Write(strm);
        }

        strm.Seekg(mModMapOffset);
        for (int i = 0; i < mModMap.Count; i++) {
            strm.Write16((ushort)mModMap[i]);
        }

        for (int i = 0; i < mModules.Count; i++) {
            Dsa3ModuleEntry module = mModules[i];

            for (int j = 0; j < mModules[i].Size; j++) {
                int entryIndex = (int)mModMap[(int)mModules[i].Offset / 2 + j];
                if (entryIndex == 0xFFFF) {
                    continue;
                }

                IHeaderEntry entry = Entries[entryIndex];
                strm.Seekg(entry.Offset + HeaderSize);
                strm.WriteChars((int)entry.Size, mEntryData[i][j]);
            }
        }
    }

    public bool Read(Istream strm) {
        FileSize = Filesize(strm);
        HeaderSize = 48; // initial default; overwritten by mDataOffset below
        mSignature = strm.ReadChars(4);
        mVersion = strm.ReadChars(4);
        mFiletableSize = strm.Read16();
        mFiletableOffset = strm.Read32();
        Count = strm.Read16();
        mDataOffset = strm.Read32();
        mModuleTableSize = strm.Read16();
        mModuleTableOffset = strm.Read32();
        mModuleCount = strm.Read16();
        mModMapOffset = strm.Read32();
        mUnknown1 = strm.ReadChars(16);
        HeaderSize = mDataOffset;

        strm.Seekg(mModuleTableOffset);

        mModMapSize = 0;
        for (uint i = 0; i < mModuleCount; i++) {
            Dsa3ModuleEntry module = new Dsa3ModuleEntry();
            module.Read(strm);
            mModules.Add(module);
            if ((module.Offset / 2 + module.Size) > mModMapSize) {
                mModMapSize = (module.Offset / 2) + module.Size;
            }
        }

        strm.Seekg(mFiletableOffset);
        for (uint i = 0; i < Count; i++) {
            Dsa3Entry entry = new Dsa3Entry();
            entry.Read(strm);
            Entries.Add(entry);
        }

        strm.Seekg(mModMapOffset);
        for (uint i = 0; i < mModMapSize; i++) {
            mModMap.Add(strm.Read16());
        }

        for (int i = 0; i < mModuleCount; i++) {
            for (int j = 0; j < mModules[i].Size; j++) {
                ushort entryIndex = (ushort)mModMap[(int)mModules[i].Offset / 2 + j];
                if (entryIndex == 0xFFFF) {
                    mModules[i].mEntryIndices.Add(null);
                    continue;
                }


                if (entryIndex < Entries.Count) {
                    mModules[i].mEntryIndices.Add(entryIndex);
                    Dsa3Entry entry = (Dsa3Entry)(Entries[entryIndex]);
                    Debug.Assert(entry is not null);
                } else {
                    Debug.Print("Alf Entry " + entryIndex + " does not exist!");
                }


                Debug.Assert(mModules[i].Offset / 2 + j < mModMap.Count);
            }
        }
        return true;
    }

    private uint Filesize(Istream strm) {
        return (uint)strm.mFilesize;
    }

    public string Type() { return "DSA3"; }
    public IHeaderEntry NewHeaderEntry() { return new Dsa3Entry(); }

    // Glob/match functionality removed; filtering handled at CLI layer.

    // Legacy listing removed; use 'rivaxtract list'

}

public class Istream : IDisposable {
    protected byte[] mContent;
    protected int mPosition;

    public int mFilesize;

    public byte[] ReadChars(int num) {
        var result = new byte[num];
        Array.Copy(mContent, mPosition, result, 0, num);
        mPosition += num;
        return result;
    }

    public uint Read32() {
        uint result =
            (uint)mContent[mPosition]
          | (uint)mContent[mPosition + 1] << 8
          | (uint)mContent[mPosition + 2] << 16
          | (uint)mContent[mPosition + 3] << 24;
        mPosition += 4;
        return result;
    }

    public ushort Read16() {
        ushort result = (ushort)(mContent[mPosition] | (mContent[mPosition + 1] << 8));
        mPosition += 2;
        return result;
    }

    public byte Read8() => ReadChars(1)[0];

    public void Seekg(uint position) => mPosition = (int)position;

    // --- IDisposable (no-op here, so callers can uniformly 'using' any *stream) ---
    private bool _disposed;
    protected virtual void Dispose(bool disposing) {
        if (_disposed) return;
        _disposed = true;
        // No unmanaged resources here; subclasses may override.
        // Help GC by releasing large buffers earlier:
        mContent = null;
    }

    public void Dispose() {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}

internal class Ifstream : Istream {
    public Ifstream(string path) {
        // Read entire file and dispose the FileStream immediately
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        mFilesize = (int)fs.Length;
        mContent = new byte[mFilesize];
        int read = 0;
        while (read < mFilesize) {
            int n = fs.Read(mContent, read, mFilesize - read);
            if (n == 0) break;
            read += n;
        }
        mPosition = 0;
    }

    public Ifstream(byte[] content) {
        mContent = content;
        mFilesize = content?.Length ?? 0;
        mPosition = 0;
    }

    public bool Good() => true;
    public bool Eof() => mPosition >= (mContent?.Length ?? 0);

    public string Read0String() {
        var result = "";
        while (mContent[mPosition] != 0) {
            result += (char)mContent[mPosition];
            mPosition += 1;
        }
        mPosition += 1; // skip terminating 0
        return result;
    }

    public void Close() {
        // nothing to do; kept for API compatibility
    }
}

public class Ostream : IDisposable {
    protected byte[] mContent;
    protected int mPosition;
    public uint mFilesize;

    public void WriteChars(int num, byte[] chars) {
        Array.Copy(chars, 0, mContent, mPosition, num);
        mPosition += num;
    }

    public void Write32(uint val) {
        mContent[mPosition] = (byte)(val & 0xFF);
        mContent[mPosition + 1] = (byte)((val >> 8) & 0xFF);
        mContent[mPosition + 2] = (byte)((val >> 16) & 0xFF);
        mContent[mPosition + 3] = (byte)((val >> 24) & 0xFF);
        mPosition += 4;
    }

    public void Write16(ushort val) {
        mContent[mPosition] = (byte)(val & 0xFF);
        mContent[mPosition + 1] = (byte)((val >> 8) & 0xFF);
        mPosition += 2;
    }

    public void Write8(byte val) {
        mContent[mPosition] = val;
        mPosition++;
    }

    public void Seekg(uint position) => mPosition = (int)position;

    // --- IDisposable base (no-op; subclass may commit to disk) ---
    private bool _disposed;
    protected virtual void Dispose(bool disposing) {
        if (_disposed) return;
        _disposed = true;
        mContent = null;
    }

    public void Dispose() {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}

internal class Ofstream : Ostream {
    private FileStream _fs;
    private bool _disposed;

    public Ofstream(string path, int maxLength) {
        // Create (truncate) so no stale bytes remain
        _fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        mContent = new byte[maxLength];
        mFilesize = (uint)maxLength;
        mPosition = 0;
    }

    public void Close() {
        // Back-compat. Prefer using() or Dispose().
        Dispose();
    }

    protected override void Dispose(bool disposing) {
        if (_disposed) return;
        _disposed = true;

        try {
            if (disposing && _fs is not null) {
                // Commit buffer to disk exactly once
                _fs.Position = 0;
                _fs.Write(mContent, 0, mContent.Length);
                _fs.SetLength(mContent.Length);
                _fs.Flush();
                _fs.Dispose();
            }
        } finally {
            _fs = null;
            // release the big buffer early
            mContent = null;
            base.Dispose(disposing);
        }
    }

    ~Ofstream() {
        // Safety net in case Dispose() wasn't called
        Dispose(false);
    }
}

