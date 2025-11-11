// MIT License
// Copyright (c) 2025 [tinion] Carl Marvin Sautter
// See LICENSE file in the project root for full license text.

// Data transfer objects for JSON export/import
using System.Collections.Generic;
using System.Text.Json.Serialization;
namespace RivaXtractCLI;

internal sealed class JsonRoot {
    [JsonPropertyName("meta")] public JsonMeta? Meta { get; set; }
    [JsonPropertyName("header")] public JsonHeader? Header { get; set; }
    [JsonPropertyName("entries")] public List<JsonEntry>? Entries { get; set; }
    [JsonPropertyName("modules")] public List<JsonModule>? Modules { get; set; }
    [JsonPropertyName("modmap_raw_u16")] public List<ushort>? ModMapRawU16 { get; set; }
}

internal sealed class JsonMeta {
    [JsonPropertyName("format")] public string? Format { get; set; }
    [JsonPropertyName("endianness")] public string? Endianness { get; set; }
    [JsonPropertyName("generator")] public string? Generator { get; set; }
    [JsonPropertyName("note")] public string? Note { get; set; }
}

internal sealed class JsonHeader {
    [JsonPropertyName("signature_ascii")] public string? SignatureAscii { get; set; }
    [JsonPropertyName("version_u32")] public uint VersionU32 { get; set; }
    [JsonPropertyName("file_table_size_u16")] public ushort FileTableSizeU16 { get; set; }
    [JsonPropertyName("file_table_offset_u32")] public uint FileTableOffsetU32 { get; set; }
    [JsonPropertyName("file_count_u16")] public ushort FileCountU16 { get; set; }
    [JsonPropertyName("data_offset_u32")] public uint DataOffsetU32 { get; set; }
    [JsonPropertyName("module_table_size_u16")] public ushort ModuleTableSizeU16 { get; set; }
    [JsonPropertyName("module_table_offset_u32")] public uint ModuleTableOffsetU32 { get; set; }
    [JsonPropertyName("module_count_u16")] public ushort ModuleCountU16 { get; set; }
    [JsonPropertyName("modmap_offset_u32")] public uint ModMapOffsetU32 { get; set; }
    [JsonPropertyName("unknown16_hex")] public string? Unknown16Hex { get; set; }
}

internal sealed class JsonEntry {
    [JsonPropertyName("index_u16")] public ushort IndexU16 { get; set; }
    [JsonPropertyName("index_str5")] public string? IndexStr5 { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    // Fixed-length raw filename bytes (13 bytes) as lowercase hex
    [JsonPropertyName("raw_filename_hex")] public string? RawFilenameHex { get; set; }
    [JsonPropertyName("unknown1_u8")] public byte Unknown1U8 { get; set; }
    [JsonPropertyName("size_u32")] public uint SizeU32 { get; set; }

    // Packed DOS/FAT datetime (local, 2-second resolution)
    [JsonPropertyName("dos_datetime_u32")] public uint DosDateTimeU32 { get; set; }
    [JsonPropertyName("dos_datetime_local_iso")] public string? DosDateTimeLocalIso { get; set; }

    [JsonPropertyName("unknown3_u16")] public ushort Unknown3U16 { get; set; }
    [JsonPropertyName("offset_u32")] public uint OffsetU32 { get; set; }
}

internal sealed class JsonModule {
    [JsonPropertyName("index_u16")] public ushort IndexU16 { get; set; }
    [JsonPropertyName("index_str5")] public string? IndexStr5 { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    // Fixed-length raw module name bytes (14 bytes) as lowercase hex
    [JsonPropertyName("raw_filename_hex")] public string? RawFilenameHex { get; set; }
    [JsonPropertyName("size_slots_u32")] public uint SizeSlotsU32 { get; set; }

    // Packed DOS/FAT datetime (local, 2-second resolution)
    [JsonPropertyName("dos_datetime_u32")] public uint DosDateTimeU32 { get; set; }
    [JsonPropertyName("dos_datetime_local_iso")] public string? DosDateTimeLocalIso { get; set; }

    [JsonPropertyName("unknown2_u16")] public ushort Unknown2U16 { get; set; }
    [JsonPropertyName("modmap_start_u32")] public uint ModMapStartU32 { get; set; }
    [JsonPropertyName("slots")] public List<ushort>? Slots { get; set; }
}

internal sealed class MapItem {
    public string? Module { get; set; }
    public string? Filename { get; set; }
    public string? Path { get; set; }
}

