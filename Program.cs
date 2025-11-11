// MIT License
// Copyright (c) 2025 [tinion] Carl Marvin Sautter
// See LICENSE file in the project root for full license text.

﻿// Program.cs
// Build as: rivaxtract
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace RivaXtractCLI;
internal static class Program {

    // Add to Program class
    private static bool WriteFileWithPolicy(string path, byte[] data, int overwriteMode) {
        // overwriteMode: 0 = never, 1 = ask, 2 = always
        bool exists = File.Exists(path);

        if (!exists) {
            WriteBytes(path, data);
            return true;
        }

        switch (overwriteMode) {
            case 0: // never
                return false;

            case 1: // ask
            {
                // If input is redirected (non-interactive), default to "No"
                if (Console.IsInputRedirected) return false;

                Console.Error.Write($"File exists: {path}. Overwrite? [y/N] ");
                var ans = Console.ReadLine();
                if (!string.IsNullOrWhiteSpace(ans) &&
                    (ans.Equals("y", StringComparison.OrdinalIgnoreCase) ||
                     ans.Equals("yes", StringComparison.OrdinalIgnoreCase))) {
                    WriteBytes(path, data);
                    return true;
                }
                return false;
            }

            case 2: // always
                WriteBytes(path, data);
                return true;

            default:
                throw new ArgumentOutOfRangeException(nameof(overwriteMode), "Expected 0 (never), 1 (ask), or 2 (always).");
        }
    }

    private static void WriteBytes(string path, byte[] data) {
        // Clear read-only if set
        if (File.Exists(path)) {
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
        }
        File.WriteAllBytes(path, data);
    }



    private static readonly StringComparison CI = StringComparison.InvariantCultureIgnoreCase;

    static int Main(string[] args) {
        if (args.Length == 0 || IsHelp(args[0]))
            return PrintTopHelp();

        try {
            var cmd = args[0].ToLowerInvariant();
            var rest = args.Skip(1).ToArray();

            return cmd switch {
                "list" => CmdList(rest),
                "extract" => CmdExtract(rest),
                "read" => CmdRead(rest),
                "build" => CmdBuildFromJson(rest),
                "modify" => CmdModify(rest),
                "repack" => CmdRepack(rest),
                "help" => PrintTopHelp(),
                "export-json" => CmdExportJson(rest),
                "export-views" => CmdExportViews(rest),
                _ => Fail($"Unknown command '{cmd}'. Try 'rivaxtract help'.")
            };

        } catch (Exception ex) {
            Console.Error.WriteLine("Error: " + ex.Message);
            return 1;
        }
    }

    // -----------------------
    // export-json (homomorphic structure-only JSON)
    // -----------------------
    private static int CmdExportJson(string[] args) {
        // rivaxtract export-json <archive> --out structure.json [--pretty] [--include-modmap-raw]
        if (args.Length < 1)
            return Fail("Usage: rivaxtract export-json <archive> --out structure.json [--pretty] [--include-modmap-raw]");

        var archive = args[0];
        string outJson = "";
        bool pretty = false;
        bool includeModMapRaw = false;

        for (int i = 1; i < args.Length; i++) {
            switch (args[i]) {
                case "--out": outJson = NeedValue(args, ref i, "output json"); break;
                case "--pretty": pretty = true; break;
                case "--include-modmap-raw": includeModMapRaw = true; break;
                default: throw new ArgumentException($"Unknown option for export-json: {args[i]}");
            }
        }

        if (string.IsNullOrEmpty(outJson))
            return Fail("Missing --out <json>");

        var (_, dsa) = LoadDsa3(archive);

        // Compute modmap size (defensive)
        uint modmapSize = 0;
        for (int mi = 0; mi < dsa.mModuleCount; mi++)
            modmapSize = Math.Max(modmapSize, dsa.mModules[mi].Offset / 2 + dsa.mModules[mi].Size);

        var root = new JsonRoot {
            Meta = new JsonMeta {
                Format = "DSA3-ALF",
                Endianness = "LE",
                Generator = "rivaxtract",
                Note = "Offsets are relative to data_offset; 65535 marks a dummy slot."
            },
            Header = new JsonHeader {
                SignatureAscii = Encoding.ASCII.GetString(dsa.mSignature),
                VersionU32 = BitConverter.ToUInt32(dsa.mVersion, 0),
                FileTableSizeU16 = dsa.mFiletableSize,
                FileTableOffsetU32 = dsa.mFiletableOffset,
                FileCountU16 = dsa.Count,
                DataOffsetU32 = dsa.mDataOffset,
                ModuleTableSizeU16 = dsa.mModuleTableSize,
                ModuleTableOffsetU32 = dsa.mModuleTableOffset,
                ModuleCountU16 = dsa.mModuleCount,
                ModMapOffsetU32 = dsa.mModMapOffset,
                Unknown16Hex = BytesToHex(dsa.mUnknown1)
            },
            Entries = new List<JsonEntry>(dsa.Count),
            Modules = new List<JsonModule>(dsa.mModuleCount)
        };

        // File table
        for (int i = 0; i < dsa.Count; i++) {
            var e = (Dsa3Entry)dsa.Entries[i];
            var dt = DosDateTime.Decode(e.mDosDateTime);

            root.Entries!.Add(new JsonEntry {
                IndexU16 = (ushort)i,
                IndexStr5 = i.ToString("D5"),
                Name = e.Name ?? "",
                RawFilenameHex = BytesToHex(e.NameRaw13 ?? Array.Empty<byte>()),
                Unknown1U8 = e.mUnknown1,
                SizeU32 = e.Size,

                DosDateTimeU32 = e.mDosDateTime,
                DosDateTimeLocalIso = DosDateTime.ToLocalIso(dt),

                Unknown3U16 = e.mUnknown3,
                OffsetU32 = e.Offset
            });
        }

        // Modules + slots
        for (int mi = 0; mi < dsa.mModuleCount; mi++) {
            var m = dsa.mModules[mi];
            var start = (int)(m.Offset / 2);
            var slots = new List<ushort>((int)m.Size);
            for (int s = 0; s < m.Size; s++)
                slots.Add((ushort)dsa.mModMap[start + s]);

            var mdt = DosDateTime.Decode(m.mDosDateTime);

            root.Modules!.Add(new JsonModule {
                IndexU16 = (ushort)mi,
                IndexStr5 = mi.ToString("D5"),
                Name = m.Name ?? "",
                RawFilenameHex = BytesToHex(m.NameRaw14 ?? Array.Empty<byte>()),
                SizeSlotsU32 = m.Size,

                DosDateTimeU32 = m.mDosDateTime,
                DosDateTimeLocalIso = DosDateTime.ToLocalIso(mdt),

                Unknown2U16 = m.mUnknown2,
                ModMapStartU32 = (uint)start,
                Slots = slots
            });
        }

        if (includeModMapRaw) {
            var raw = new List<ushort>((int)modmapSize);
            for (int i = 0; i < modmapSize; i++) raw.Add((ushort)dsa.mModMap[i]);
            root.ModMapRawU16 = raw;
        }

        var opts = new JsonSerializerOptions {
            WriteIndented = pretty,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            TypeInfoResolver = RivaJsonContext.Default // ← turn off reflection and use SG metadata
        };
        var json = JsonSerializer.Serialize(root, opts);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outJson)) ?? ".");
        File.WriteAllText(outJson, json, new UTF8Encoding(false));

        Console.Error.WriteLine($"wrote JSON structure → {outJson}");
        return 0;
    }


    // -----------------------
    // export-views (objects + views)
    // -----------------------
    private static int CmdExportViews(string[] args) {
        // rivaxtract export-views <archive> --out <root>
        //   [--views index,module-slot,module-name,name|all]
        //   [--mode hard|symlink|copy] [--dummy omit|marker]
        if (args.Length < 1)
            return Fail("Usage: rivaxtract export-views <archive> --out <root> [--views ...] [--mode hard|symlink|copy] [--dummy omit|marker]");

        var archive = args[0];
        string outRoot = "";
        string viewsArg = "index,module-slot"; // sensible default
        string mode = "hard";
        string dummy = "marker";

        for (int i = 1; i < args.Length; i++) {
            switch (args[i]) {
                case "--out": outRoot = NeedValue(args, ref i, "output root"); break;
                case "--views": viewsArg = NeedValue(args, ref i, "views list"); break;
                case "--mode": mode = NeedValue(args, ref i, "mode"); break;
                case "--dummy": dummy = NeedValue(args, ref i, "dummy policy"); break;
                default: throw new ArgumentException($"Unknown option for export-views: {args[i]}");
            }
        }

        if (string.Equals(mode, "symlink", CI)) {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && !CanCreateSymlinksWindows()) {
                return Fail("Symlink mode requires Administrator privileges or Developer Mode on Windows. " +
                            "Enable Developer Mode (Settings → For developers) or run as Administrator, " +
                            "or use --mode hard|copy. Aborting without writing anything.");
            }
        }

        if (string.IsNullOrEmpty(outRoot)) return Fail("Missing --out <root>");

        var (_, dsa) = LoadDsa3(archive);
        using var ifs = new Ifstream(archive);

        // 1) objects/idx/IIIII.dat
        var objectsIdx = Path.Combine(outRoot, "objects", "idx");
        Directory.CreateDirectory(objectsIdx);
        for (int i = 0; i < dsa.Count; i++) {
            var e = (Dsa3Entry)dsa.Entries[i];
            var path = Path.Combine(objectsIdx, i.ToString("D5") + ".dat");
            var bytes = ReadBytes(ifs, dsa, e);
            WriteBytes(path, bytes);

            var dt = DosDateTime.Decode(e.mDosDateTime);
            if (dt.HasValue) {
                try {
                    File.SetCreationTime(path, dt.Value);
                    File.SetLastWriteTime(path, dt.Value);
                } catch { /* non-fatal */ }
            }
        }


        // Parse views
        var views = ParseViews(viewsArg);

        // 2) by-index
        if (views.Contains("index")) {
            var root = Path.Combine(outRoot, "view.by-index");
            Directory.CreateDirectory(root);
            for (int i = 0; i < dsa.Count; i++) {
                var e = (Dsa3Entry)dsa.Entries[i];
                var safeName = SanitizeFileName(string.IsNullOrEmpty(e.Name) ? "NO_NAME" : e.Name);
                var linkName = $"{i:D5}__{safeName}";
                var dest = Path.Combine(root, linkName);
                LinkToObject(dest, objectsIdx, i, mode);
            }
        }

        // 3) by-module-slot
        if (views.Contains("module-slot")) {
            var root = Path.Combine(outRoot, "view.by-module-slot");
            Directory.CreateDirectory(root);

            for (int mi = 0; mi < dsa.mModuleCount; mi++) {
                var m = dsa.mModules[mi];
                var modDir = Path.Combine(root, $"{mi:D5}_{SanitizeDirName(m.Name ?? "NO_MODULE")}");
                Directory.CreateDirectory(modDir);

                var start = (int)(m.Offset / 2);
                for (int s = 0; s < m.Size; s++) {
                    var slotName = s.ToString("D5");
                    int idx = (int)dsa.mModMap[start + s];
                    if (idx == 0xFFFF) {
                        if (dummy.Equals("marker", CI)) {
                            var dummyPath = Path.Combine(modDir, $"{slotName}__DUMMY");
                            WriteBytes(dummyPath, Array.Empty<byte>());
                        }
                        continue;
                    }
                    var e = (Dsa3Entry)dsa.Entries[idx];
                    var safeName = SanitizeFileName(string.IsNullOrEmpty(e.Name) ? "NO_NAME" : e.Name);
                    var dest = Path.Combine(modDir, $"{slotName}__{idx:D5}__{safeName}");
                    LinkToObject(dest, objectsIdx, idx, mode);
                }
            }
        }

        // 4) by-module-name
        if (views.Contains("module-name")) {
            var root = Path.Combine(outRoot, "view.by-module-name");
            Directory.CreateDirectory(root);

            for (int mi = 0; mi < dsa.mModuleCount; mi++) {
                var m = dsa.mModules[mi];
                var modDir = Path.Combine(root, $"{mi:D5}_{SanitizeDirName(m.Name ?? "NO_MODULE")}");
                Directory.CreateDirectory(modDir);

                var seen = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
                var start = (int)(m.Offset / 2);

                for (int s = 0; s < m.Size; s++) {
                    int idx = (int)dsa.mModMap[start + s];
                    if (idx == 0xFFFF) continue;

                    var e = (Dsa3Entry)dsa.Entries[idx];
                    var baseName = string.IsNullOrEmpty(e.Name) ? "NO_NAME" : e.Name;
                    var safe = SanitizeFileName(baseName);
                    var dest = Path.Combine(modDir, safe);

                    if (seen.Contains(safe) || File.Exists(dest)) {
                        // disambiguate
                        dest = Path.Combine(modDir, $"{safe}__slot-{s:D5}__idx-{idx:D5}");
                    }
                    seen.Add(safe);
                    LinkToObject(dest, objectsIdx, idx, mode);
                }
            }
        }

        // 5) by-name (cross-module aggregation)
        if (views.Contains("name")) {
            var root = Path.Combine(outRoot, "view.by-name");
            Directory.CreateDirectory(root);

            for (int mi = 0; mi < dsa.mModuleCount; mi++) {
                var m = dsa.mModules[mi];
                var start = (int)(m.Offset / 2);
                for (int s = 0; s < m.Size; s++) {
                    int idx = (int)dsa.mModMap[start + s];
                    if (idx == 0xFFFF) continue;

                    var e = (Dsa3Entry)dsa.Entries[idx];
                    var folder = SanitizeDirName(string.IsNullOrEmpty(e.Name) ? "NO_NAME" : e.Name);
                    var dir = Path.Combine(root, folder);
                    Directory.CreateDirectory(dir);

                    var alias = $"{mi:D5}_{SanitizeFileName(m.Name ?? "NO_MODULE")}__slot-{s:D5}__idx-{idx:D5}";
                    var dest = Path.Combine(dir, alias);
                    LinkToObject(dest, objectsIdx, idx, mode);
                }
            }
        }

        Console.Error.WriteLine($"exported objects + views → {outRoot}");
        return 0;
    }

    private static bool CanCreateSymlinksWindows() {
        // Create a tiny temp probe and try to make a file symlink to it.
        string tempRoot = Path.Combine(Path.GetTempPath(), "rivax_symlink_probe_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        string target = Path.Combine(tempRoot, "target.tmp");
        string link = Path.Combine(tempRoot, "link.tmp");

        File.WriteAllText(target, "probe");

        bool ok = false;
        try {
#if NET8_0_OR_GREATER
            // Use relative link target to avoid needing absolute permissions quirks
            var result = File.CreateSymbolicLink(link, "target.tmp");
            ok = result.Exists;
#else
        // Prefer unprivileged flag (Windows 10 1703+ with Dev Mode), then fallback
        ok = WinCreateSymbolicLink(link, "target.tmp", 0x2 /* FILE_SYMLINK_FLAG_ALLOW_UNPRIVILEGED_CREATE */);
        if (!ok) ok = WinCreateSymbolicLink(link, "target.tmp", 0x0 /* file */);
#endif
        } catch (UnauthorizedAccessException) { ok = false; } catch (System.ComponentModel.Win32Exception) { ok = false; } catch { ok = false; } finally {
            try { if (File.Exists(link)) File.Delete(link); } catch { }
            try { if (File.Exists(target)) File.Delete(target); } catch { }
            try { Directory.Delete(tempRoot, true); } catch { }
        }
        return ok;
    }



    // --- helpers for export-views ---

    private static HashSet<string> ParseViews(string v) {
        if (string.Equals(v, "all", CI))
            return new HashSet<string>(new[] { "index", "module-slot", "module-name", "name" }, StringComparer.OrdinalIgnoreCase);

        var parts = v.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0);
        return new HashSet<string>(parts, StringComparer.OrdinalIgnoreCase);
    }

    private static string SanitizeFileName(string name) {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    private static string SanitizeDirName(string name) {
        foreach (var c in Path.GetInvalidPathChars())
            name = name.Replace(c, '_');
        return SanitizeFileName(name);
    }

    private static void TryCopyTimesFrom(string src, string dst) {
        try {
            File.SetCreationTime(dst, File.GetCreationTime(src));
            File.SetLastWriteTime(dst, File.GetLastWriteTime(src));
        } catch { /* ignore */ }
    }



    private static void LinkToObject(string destPath, string objectsIdxDir, int entryIndex, string mode) {
        var src = Path.Combine(objectsIdxDir, entryIndex.ToString("D5") + ".dat");
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

        switch (mode.ToLowerInvariant()) {
            case "hard":
                if (!TryCreateHardLink(destPath, src)) {
                    File.Copy(src, destPath, overwrite: true);
                    Console.Error.WriteLine($"warn: hardlink fallback to copy: {destPath}");
                }
                break;

            case "symlink":
                if (!TryCreateSymlinkFile(destPath, GetRelativePath(Path.GetDirectoryName(destPath)!, src))) {
                    File.Copy(src, destPath, overwrite: true);
                    Console.Error.WriteLine($"warn: symlink fallback to copy: {destPath}");
                }
                break;

            case "copy":
                File.Copy(src, destPath, overwrite: true);
                TryCopyTimesFrom(src, destPath);
                break;


            default:
                throw new ArgumentException("mode must be hard|symlink|copy");
        }
    }

    // Relative path helper (for prettier symlinks)
    private static string GetRelativePath(string fromDir, string toPath) {
        var from = Path.GetFullPath(fromDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var to = Path.GetFullPath(toPath);
        return Path.GetRelativePath(from, to);
    }


    // Try to create a hardlink cross-platform
    private static bool TryCreateHardLink(string linkPath, string targetPath) {
        try {
//#if NET9_0_OR_GREATER
//            File.CreateHardLink(linkPath, targetPath);
//            return true;
//#else
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return WinCreateHardLink(linkPath, targetPath);
        else
            return UnixLink(targetPath, linkPath) == 0;
//#endif
        } catch { return false; }
    }

    private static bool TryCreateSymlinkFile(string linkPath, string targetRelative) {
        try {
#if NET8_0_OR_GREATER
            var r = File.CreateSymbolicLink(linkPath, targetRelative);
            return r.Exists; // creation succeeded if link now exists
#else
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return WinCreateSymbolicLink(linkPath, targetRelative, 0x0 /* FILE_SYMLINK_FLAG_FILE */);
        else
            return UnixSymlink(targetRelative, linkPath) == 0;
#endif
        } catch { return false; }
    }

//#if !NET8_0_OR_GREATER
// ----- P/Invoke fallbacks -----
[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

private static bool WinCreateHardLink(string linkPath, string targetPath)
    => CreateHardLink(linkPath, targetPath, IntPtr.Zero);

[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
private static extern bool CreateSymbolicLink(string lpSymlinkFileName, string lpTargetFileName, int dwFlags);
// dwFlags: 0x0 file, 0x1 directory, 0x2 allow unprivileged (Win 11+ / DevMode)

private static bool WinCreateSymbolicLink(string linkPath, string target, int flags)
    => CreateSymbolicLink(linkPath, target, flags);

[DllImport("libc", SetLastError = true)]
private static extern int link(string oldpath, string newpath);

private static int UnixLink(string existing, string @new) => link(existing, @new);

[DllImport("libc", SetLastError = true)]
private static extern int symlink(string oldpath, string newpath);

private static int UnixSymlink(string target, string @new) => symlink(target, @new);
//#endif

    private static string BytesToHex(byte[] bytes) {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }


    // -----------------------
    // list
    // -----------------------
    private static int CmdList(string[] args) {
        if (args.Length < 1) return Fail("Usage: rivaxtract list <archive> [--pattern <glob>] [--modules-only] [--csv <path>]");
        var archive = args[0];
        var (pattern, modulesOnly, csvPath) = ParseListOptions(args.Skip(1).ToArray());

        var (_, dsa) = LoadDsa3(archive);
        ApplyPattern(dsa, pattern); // mutates modmap, but this run is ephemeral

        if (modulesOnly) {
            foreach (var m in dsa.mModules)
                Console.WriteLine($"{m.Name} ({m.Size} slots)");
        } else {
            foreach (var (mod, slot, entryIndex) in EnumerateVisibleSlots(dsa)) {
                if (entryIndex == null) continue;
                var e = (Dsa3Entry)dsa.Entries[entryIndex.Value];
                Console.WriteLine($"{mod.Name}{PathDelim()}{e.Name} | {e.Size}");
            }
        }

        if (!string.IsNullOrEmpty(csvPath)) {
            dsa.PrintAllIndices(includeDummies: true, oneBased: false, file: csvPath);
        }

        return 0;
    }

    private static (string pattern, bool modulesOnly, string csvPath) ParseListOptions(string[] opts) {
        string pattern = "*";
        bool modulesOnly = false;
        string csv = "";
        for (int i = 0; i < opts.Length; i++) {
            switch (opts[i]) {
                case "-p":
                case "--pattern":
                    pattern = NeedValue(opts, ref i, "pattern");
                    break;
                case "--modules-only":
                    modulesOnly = true;
                    break;
                case "--csv":
                    csv = NeedValue(opts, ref i, "csv path");
                    break;
                default:
                    throw new ArgumentException($"Unknown option for list: {opts[i]}");
            }
        }
        return (pattern, modulesOnly, csv);
    }

    // -----------------------
    // extract
    // -----------------------
    private static int CmdExtract(string[] args) {
        if (args.Length < 1) return Fail("Usage: rivaxtract extract <archive> -o <outdir> [--pattern <glob>] [--module-dirs] [--overwrite ask|never|always]");
        var archive = args[0];
        string outdir = "";
        string pattern = "*";
        bool moduleDirs = false;
        int overwriteMode = 1; // 0=never,1=ask,2=always

        for (int i = 1; i < args.Length; i++) {
            switch (args[i]) {
                case "-o":
                case "--out":
                    outdir = NeedValue(args, ref i, "outdir");
                    break;
                case "-p":
                case "--pattern":
                    pattern = NeedValue(args, ref i, "pattern");
                    break;
                case "--module-dirs":
                    moduleDirs = true;
                    break;
                case "--overwrite":
                    var v = NeedValue(args, ref i, "overwrite mode");
                    overwriteMode = v switch {
                        "never" => 0,
                        "ask" => 1,
                        "always" => 2,
                        _ => throw new ArgumentException("overwrite must be ask|never|always")
                    };
                    break;
                default:
                    throw new ArgumentException($"Unknown option for extract: {args[i]}");
            }
        }

        if (string.IsNullOrWhiteSpace(outdir))
            return Fail("Missing -o/--out <outdir>");

        Directory.CreateDirectory(outdir);

        var (cfg, dsa) = LoadDsa3(archive);
        cfg.mDsa3ModuleDirs = moduleDirs;
        cfg.mOverwriteMode = overwriteMode;
        ApplyPattern(dsa, pattern);

        using var ifs = new Ifstream(archive);

        foreach (var (mod, slot, entryIndex) in EnumerateVisibleSlots(dsa)) {
            if (entryIndex == null) continue;
            var e = (Dsa3Entry)dsa.Entries[entryIndex.Value];

            var bytes = ReadBytes(ifs, dsa, e);
            string rel =
                moduleDirs
                ? Path.Combine(mod.Name, e.Name)
                : e.Name;

            string dest = Path.Combine(outdir, rel);

            if (!moduleDirs) {
                // guard against collisions across modules
                var candidate = dest;
                if (File.Exists(candidate))
                    throw new IOException($"Filename collision for '{e.Name}'. Use --module-dirs to keep files separated by module.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            WriteFileWithPolicy(dest, bytes, overwriteMode);

            // Set timestamps (creation + last write) using FAT/DOS local time
            var dt = DosDateTime.Decode(e.mDosDateTime);
            if (dt.HasValue) {
                try {
                    File.SetCreationTime(dest, dt.Value);
                    File.SetLastWriteTime(dest, dt.Value);
                } catch (Exception ex) {
                    Console.Error.WriteLine($"warn: failed to set timestamps for {rel}: {ex.Message}");
                }
            }

            Console.Error.WriteLine($"wrote {rel} ({bytes.Length} bytes)");

        }

        return 0;
    }

    // -----------------------
    // read (single entry)
    // -----------------------
    private static int CmdRead(string[] args) {
        // rivaxtract read <archive> <module> <filename> [-o <path>] [--offset N] [--length N] [--info]
        if (args.Length < 3)
            return Fail("Usage: rivaxtract read <archive> <module> <filename> [-o <path>] [--offset N] [--length N] [--info]");

        var archive = args[0];
        var module = args[1];
        var filename = args[2];

        string outPath = "";
        long offset = 0;
        long length = -1;
        bool infoOnly = false;

        for (int i = 3; i < args.Length; i++) {
            switch (args[i]) {
                case "-o":
                case "--out":
                    outPath = NeedValue(args, ref i, "out path");
                    break;
                case "--offset":
                    offset = long.Parse(NeedValue(args, ref i, "offset"), CultureInfo.InvariantCulture);
                    break;
                case "--length":
                    length = long.Parse(NeedValue(args, ref i, "length"), CultureInfo.InvariantCulture);
                    break;
                case "--info":
                    infoOnly = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown option for read: {args[i]}");
            }
        }

        var (_, dsa) = LoadDsa3(archive);

        var (mod, entry, moduleSlot, index) = FindEntry(dsa, module, filename)
            ?? throw new FileNotFoundException($"Entry '{module}{PathDelim()}{filename}' not found.");

        if (infoOnly) {
            Console.WriteLine($"Module      : {mod.Name}");
            Console.WriteLine($"Module slot : {moduleSlot}");
            Console.WriteLine($"Index       : {index}");
            Console.WriteLine($"Name        : {entry.Name}");
            Console.WriteLine($"Size        : {entry.Size}");
            Console.WriteLine($"Offset(Data): {entry.Offset} (absolute in file: {entry.Offset + dsa.HeaderSize})");
            return 0;
        }

        using var ifs = new Ifstream(archive);
        var data = ReadBytes(ifs, dsa, entry);

        // slice if requested
        if (offset < 0 || (length < -1))
            return Fail("offset must be >= 0, length must be >= -1");
        if (offset > data.LongLength) offset = data.LongLength;
        if (length == -1) length = data.LongLength - offset;
        length = Math.Min(length, data.LongLength - offset);
        var slice = data.Skip((int)offset).Take((int)length).ToArray();

        if (string.IsNullOrEmpty(outPath)) {
            // write to stdout (binary)
            using var stdout = Console.OpenStandardOutput();
            stdout.Write(slice, 0, slice.Length);
        } else {
            File.WriteAllBytes(outPath, slice);
            Console.Error.WriteLine($"wrote {outPath} ({slice.Length} bytes)");
        }

        return 0;
    }

    // -----------------------
    // modify (preserve indices/slots/dummies; in-place if same-size, else repack)
    // -----------------------
    private static int CmdModify(string[] args) {
        // rivaxtract modify <archive> [--out <new-archive>]
        //     [--set MOD/FILE=path]... [--from-dir <root>] [--map <csv|json>]
        //     [--strict] [--dry-run] [--inplace] [--touch]
        //
        // Behavior:
        // - If ALL replacements keep the original size:
        //     * In-place patch (default). If --out is given, we copy the archive and patch the copy.
        // - If ANY replacement changes size:
        //     * Repack to --out (required). Preserves table order, indices, modmap, and dummies.
        //       Offsets and sizes are recomputed deterministically. (No index/slot swaps.)
        // - --touch: set FAT/DOS timestamp on modified entries (only applied in repack path).
        if (args.Length < 1)
            return Fail("Usage: rivaxtract modify <archive> [--out <new-archive>] [--set MOD/FILE=path]... [--from-dir <root>] [--map <csv|json>] [--strict] [--dry-run] [--inplace] [--touch]");

        var archive = args[0];

        string outArc = "";
        var sets = new List<(string module, string filename, string path)>();
        var fromDir = new List<string>();
        var maps = new List<string>();
        bool strict = false;
        bool dryRun = false;
        bool forceInplace = false;
        bool touch = false;

        for (int i = 1; i < args.Length; i++) {
            switch (args[i]) {
                case "--out": outArc = NeedValue(args, ref i, "output archive"); break;
                case "--set": sets.Add(ParseSetSpec(NeedValue(args, ref i, "MOD/FILE=path"))); break;
                case "--from-dir": fromDir.Add(NeedValue(args, ref i, "root folder")); break;
                case "--map": maps.Add(NeedValue(args, ref i, "map file")); break;
                case "--strict": strict = true; break;
                case "--dry-run": dryRun = true; break;
                case "--inplace": forceInplace = true; break;
                case "--touch": touch = true; break;
                default: throw new ArgumentException($"Unknown option for modify: {args[i]}");
            }
        }

        if (!File.Exists(archive))
            return Fail($"Archive not found: {archive}");

        // Load archive model (no mutation yet)
        var (_, dsa) = LoadDsa3(archive);

        // Gather replacements from sources (later wins on conflicts)
        var replByMF = new Dictionary<string, byte[]>(StringComparer.InvariantCultureIgnoreCase);
        foreach (var root in fromDir) MergeReplacementsFromDir(replByMF, root);
        foreach (var map in maps) MergeReplacementsFromMap(replByMF, map);
        foreach (var (module, filename, path) in sets) replByMF[MakeMFKey(module, filename)] = File.ReadAllBytes(path);

        if (replByMF.Count == 0)
            return Fail("No replacements specified. Use --set, --from-dir, or --map.");

        // Map replacements to entry indices
        var missing = new List<string>();
        var replByIndex = new Dictionary<int, byte[]>();
        var plan = new List<(string module, string filename, int index, uint oldSize, int newSize)>();

        foreach (var kv in replByMF) {
            var (module, filename) = SplitMFKey(kv.Key);
            var found = FindEntry(dsa, module, filename);
            if (found == null) {
                if (strict) missing.Add($"{module}{PathDelim()}{filename}");
                else Console.Error.WriteLine($"warn: skip missing {module}{PathDelim()}{filename}");
                continue;
            }
            int idx = found.Value.index;
            var e = (Dsa3Entry)dsa.Entries[idx];
            replByIndex[idx] = kv.Value; // last one wins
            plan.Add((module, filename, idx, e.Size, kv.Value.Length));
        }

        if (missing.Count > 0)
            return Fail("Missing entries: " + string.Join(", ", missing));
        if (replByIndex.Count == 0)
            return Fail("Nothing to modify after resolving entries.");

        // Decide path: in-place vs repack
        bool allSameSize = true;
        foreach (var (module, filename, index, oldSize, newSize) in plan)
            if (newSize != oldSize) { allSameSize = false; break; }

        // Dry-run report
        if (dryRun) {
            Console.Error.WriteLine(allSameSize
                ? "Mode: in-place patch (no header/table changes)"
                : "Mode: repack preserving indices/slots/dummies (recompute offsets/sizes)");
            foreach (var p in plan)
                Console.Error.WriteLine($"  {p.module}{PathDelim()}{p.filename}  idx={p.index:D5}  size {p.oldSize} -> {p.newSize}");
            if (!allSameSize && string.IsNullOrEmpty(outArc))
                Console.Error.WriteLine("note: a changed size requires --out <new-archive>.");
            if (forceInplace && !allSameSize)
                Console.Error.WriteLine("note: --inplace ignored because some sizes changed.");
            if (touch && allSameSize)
                Console.Error.WriteLine("note: --touch has no effect for in-place patch (header not edited).");
            return 0;
        }

        // === In-place path (fast) ===
        if (allSameSize && (forceInplace || string.IsNullOrEmpty(outArc))) {
            string target = string.IsNullOrEmpty(outArc) ? archive : outArc;

            if (!string.IsNullOrEmpty(outArc)) {
                // Copy archive then patch the copy
                File.Copy(archive, outArc, overwrite: true);
            }

            using var fs = new FileStream(target, FileMode.Open, FileAccess.Write, FileShare.Read);
            foreach (var kv in replByIndex) {
                int idx = kv.Key;
                var bytes = kv.Value;
                var e = (Dsa3Entry)dsa.Entries[idx];
                long pos = (long)dsa.HeaderSize + e.Offset;
                fs.Seek(pos, SeekOrigin.Begin);
                fs.Write(bytes, 0, bytes.Length);
            }

            if (touch)
                Console.Error.WriteLine("note: --touch ignored (in-place does not change header timestamps).");

            Console.Error.WriteLine($"patched {(string.IsNullOrEmpty(outArc) ? "in-place" : $"→ '{outArc}'")} ({replByIndex.Count} entr{(replByIndex.Count == 1 ? "y" : "ies")})");
            return 0;
        }

        // === Repack path (preserving mapping; indices/slots/dummies unchanged) ===
        if (!allSameSize && string.IsNullOrEmpty(outArc))
            return Fail("At least one replacement changes size; specify --out <new-archive>.");

        using var ifs = new Ifstream(archive);
        var data = dsa.CollectEntryData(ifs); // by entry index (existing helper)

        // Apply replacements to entry-indexed data
        foreach (var kv in replByIndex)
            data[kv.Key] = kv.Value;

        // Optionally touch modified entries' FAT/DOS timestamps
        if (touch) {
            var now = DateTime.Now;
            uint packed = DosDateTime.Encode(now);
            foreach (var kv in replByIndex) {
                var e = (Dsa3Entry)dsa.Entries[kv.Key];
                e.mDosDateTime = packed;
            }
        }

        // Pack to new file while keeping tables/mapping shape
        var total = dsa.ComputePackedSize(data);
        using var ofs = new Ofstream(outArc, (int)total);
        dsa.RepackTo(ofs, data);
        ofs.Close();

        Console.Error.WriteLine($"modified {replByIndex.Count} entr{(replByIndex.Count == 1 ? "y" : "ies")} → '{outArc}' (indices/slots/dummies preserved)");
        return 0;
    }


    // --- helpers for modify ---

    private static (string module, string filename, string path) ParseSetSpec(string spec) {
        // Expect: MOD/FILE=path    (accepts MOD\FILE=path too)
        var eq = spec.IndexOf('=');
        if (eq <= 0 || eq == spec.Length - 1)
            throw new ArgumentException("Bad --set format. Expected MOD/FILE=path");

        var mf = spec.Substring(0, eq);
        var path = spec.Substring(eq + 1).Trim('"');
        var (module, filename) = SplitModuleFilename(mf);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Replacement file not found: {path}");
        return (module, filename, path);
    }

    private static (string module, string filename) SplitModuleFilename(string mf) {
        mf = mf.Replace('\\', '/');
        var slash = mf.IndexOf('/');
        if (slash <= 0 || slash == mf.Length - 1)
            throw new ArgumentException("Expected MOD/FILE (or MOD\\FILE)");
        var module = mf.Substring(0, slash);
        var filename = mf.Substring(slash + 1);
        return (module, filename);
    }

    private static string MakeMFKey(string module, string filename)
        => $"{module}\0{filename}";

    private static (string module, string filename) SplitMFKey(string key) {
        var parts = key.Split('\0');
        return (parts[0], parts[1]);
    }

    private static void MergeReplacementsFromDir(Dictionary<string, byte[]> dst, string root) {
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"from-dir not found: {root}");

        foreach (var moduleDir in Directory.GetDirectories(root)) {
            var module = Path.GetFileName(moduleDir)!;
            foreach (var file in Directory.GetFiles(moduleDir)) {
                var filename = Path.GetFileName(file)!;
                dst[MakeMFKey(module, filename)] = File.ReadAllBytes(file);
            }
        }
    }

    private static void MergeReplacementsFromMap(Dictionary<string, byte[]> dst, string mapFile) {
        if (!File.Exists(mapFile))
            throw new FileNotFoundException($"map file not found: {mapFile}");

        var ext = Path.GetExtension(mapFile).ToLowerInvariant();
        if (ext is ".csv") {
            // Very small CSV reader: expects header row with Module,Filename,Path
            var lines = File.ReadAllLines(mapFile)
                            .Where(s => !string.IsNullOrWhiteSpace(s))
                            .ToList();
            if (lines.Count == 0) return;

            var header = lines[0].Split(',').Select(s => s.Trim()).ToArray();
            int iMod = Array.FindIndex(header, h => h.Equals("Module", StringComparison.InvariantCultureIgnoreCase));
            int iFile = Array.FindIndex(header, h => h.Equals("Filename", StringComparison.InvariantCultureIgnoreCase));
            int iPath = Array.FindIndex(header, h => h.Equals("Path", StringComparison.InvariantCultureIgnoreCase));
            if (iMod < 0 || iFile < 0 || iPath < 0)
                throw new ArgumentException("CSV must have headers: Module,Filename,Path");

            for (int i = 1; i < lines.Count; i++) {
                var cols = SplitCsvLine(lines[i], header.Length);
                var module = cols[iMod].Trim();
                var filename = cols[iFile].Trim();
                var path = cols[iPath].Trim().Trim('"');
                if (string.IsNullOrEmpty(module) || string.IsNullOrEmpty(filename) || string.IsNullOrEmpty(path))
                    continue;
                if (!File.Exists(path))
                    throw new FileNotFoundException($"Replacement file not found (CSV): {path}");
                dst[MakeMFKey(module, filename)] = File.ReadAllBytes(path);
            }
        } else if (ext is ".json") {
            // Minimal JSON array parsing via System.Text.Json
            var txt = File.ReadAllText(mapFile);
            var jopts = new JsonSerializerOptions { TypeInfoResolver = RivaJsonContext.Default };
            var items = System.Text.Json.JsonSerializer.Deserialize<List<MapItem>>(txt, jopts)
                        ?? new List<MapItem>();
            foreach (var it in items) {
                if (it?.Module is null || it.Filename is null || it.Path is null) continue;
                if (!File.Exists(it.Path))
                    throw new FileNotFoundException($"Replacement file not found (JSON): {it.Path}");
                dst[MakeMFKey(it.Module, it.Filename)] = File.ReadAllBytes(it.Path);
            }
        } else {
            throw new ArgumentException("Unsupported map extension. Use .csv or .json");
        }
    }

    // MapItem defined in JsonDtos.cs for source generation; use that type here

    // Tiny CSV splitter: handles simple quoted fields and commas
    private static string[] SplitCsvLine(string line, int expectedCols) {
        var result = new List<string>(expectedCols);
        var sb = new System.Text.StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++) {
            char c = line[i];
            if (c == '"') {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"') {
                    sb.Append('"'); i++; // escaped quote
                } else {
                    inQuotes = !inQuotes;
                }
            } else if (c == ',' && !inQuotes) {
                result.Add(sb.ToString());
                sb.Clear();
            } else {
                sb.Append(c);
            }
        }
        result.Add(sb.ToString());
        return result.ToArray();
    }



    private static int CmdRepack(string[] args) {
        // Usage: rivaxtract repack <old-archive> <new-archive>
        if (args.Length != 2)
            return Fail("Usage: rivaxtract repack <old-archive> <new-archive>");

        var oldPath = args[0];
        var newPath = args[1];

        var (_, dsa) = LoadDsa3(oldPath);

        using var ifs = new Ifstream(oldPath);
        var data = dsa.CollectEntryData(ifs);

        var total = dsa.ComputePackedSize(data);
        using var ofs = new Ofstream(newPath, (int)total);
        dsa.RepackTo(ofs, data);
        ofs.Close();

        Console.Error.WriteLine($"repacked '{oldPath}' → '{newPath}'");
        return 0;
    }




    // extend build router
    // -----------------------
    // build (verbatim from JSON + objects)
    // -----------------------
    private static int CmdBuildFromJson(string[] args) {
        // rivaxtract build <structure.json> --objects <root> --out <archive>
        // - Verbatim build: preserves indices, slots, dummies, entry offsets and table offsets.
        if (args.Length < 1)
            return Fail("Usage: rivaxtract build <structure.json> --objects <root> --out <archive>");

        var jsonPath = args[0];
        string objectsRoot = "";
        string outArc = "";

        for (int i = 1; i < args.Length; i++) {
            switch (args[i]) {
                case "--objects": objectsRoot = NeedValue(args, ref i, "objects root"); break;
                case "--out": outArc = NeedValue(args, ref i, "output archive"); break;
                default: throw new ArgumentException($"Unknown option for build: {args[i]}");
            }
        }

        if (!File.Exists(jsonPath)) return Fail($"JSON not found: {jsonPath}");
        if (string.IsNullOrEmpty(objectsRoot)) return Fail("Missing --objects <root>");
        if (string.IsNullOrEmpty(outArc)) return Fail("Missing --out <archive>");

        // Deserialize with source-generated context (AOT-safe)
        var opts = new System.Text.Json.JsonSerializerOptions { TypeInfoResolver = RivaJsonContext.Default };
        var root = System.Text.Json.JsonSerializer.Deserialize<JsonRoot>(File.ReadAllText(jsonPath), opts)
                   ?? throw new InvalidOperationException("Invalid JSON.");

        ValidateJsonStructure(root);

        // Construct a Dsa3 model exactly from JSON tables/fields
        var (cfg, dsa) = NewDsaFromJson(root, outArc);

        // Load payloads by entry index from objects/idx
        var idxDir = ResolveIdxDir(objectsRoot);
        var entryData = LoadEntryDataByIndex(root, idxDir); // validates presence & size

        // Compute exact final file length from JSON offsets+sizes
        var total = ComputeExactArchiveSize(root);
        using var ofs = new Ofstream(outArc, (int)total);

        // Strict verbatim write: no relayout, no touching offsets
        try {
            dsa.WriteVerbatim(ofs, entryData, strictSizes: true);
        } catch (IndexOutOfRangeException ex) {
            throw new InvalidOperationException(
                "Buffer write exceeded allocated size. Check that table offsets/sizes and modmap extents " +
                "in JSON are consistent. (We now allocate for module/file tables, modmap, and data.)", ex);
        }
        ofs.Close();

        Console.Error.WriteLine($"built (verbatim) '{outArc}' from JSON '{jsonPath}' and objects '{idxDir}'");
        return 0;
    }


    

    // Find the module name (and slot) for a given entry index
    private static (string moduleName, int slot) FindModuleOfEntry(Dsa3 dsa, int entryIndex) {
        for (int mi = 0; mi < dsa.mModuleCount; mi++) {
            var mod = dsa.mModules[mi];
            for (int slot = 0; slot < mod.Size; slot++) {
                int mapIdx = (int)(mod.Offset / 2 + slot);
                int idx = (int)dsa.mModMap[mapIdx];
                if (idx == entryIndex)
                    return (mod.Name, slot);
            }
        }
        throw new InvalidOperationException($"Entry index {entryIndex} not mapped to any module/slot.");
    }

    private static (Config cfg, Dsa3 dsa) NewDsaFromJson(JsonRoot root, string archiveName) {
        if (root.Header is null) throw new InvalidOperationException("JSON.header missing");
        if (root.Entries is null) throw new InvalidOperationException("JSON.entries missing");
        if (root.Modules is null) throw new InvalidOperationException("JSON.modules missing");

        var h = root.Header;

        var cfg = new Config {
            mArchiveName = archiveName,
            mFilePattern = "*",
            mPathDelim = PathDelim()
        };

        var dsa = new Dsa3(cfg) {
            mSignature = Encoding.ASCII.GetBytes(h.SignatureAscii ?? "ALF "),
            mVersion = BitConverter.GetBytes(h.VersionU32),
            mFiletableSize = h.FileTableSizeU16,
            mFiletableOffset = h.FileTableOffsetU32,
            Count = h.FileCountU16,
            mDataOffset = h.DataOffsetU32,
            mModuleTableSize = h.ModuleTableSizeU16,
            mModuleTableOffset = h.ModuleTableOffsetU32,
            mModuleCount = h.ModuleCountU16,
            mModMapOffset = h.ModMapOffsetU32,
            mUnknown1 = HexToBytes16(h.Unknown16Hex ?? "00000000000000000000000000000000"),
            HeaderSize = h.DataOffsetU32,
            Entries = new List<IHeaderEntry>(h.FileCountU16),
            mModules = new List<Dsa3ModuleEntry>(h.ModuleCountU16),
            mModMap = new List<uint>()
        };

        // File table
        if (root.Entries.Count != h.FileCountU16)
            throw new InvalidOperationException("entries length != header.file_count_u16");

        for (int i = 0; i < root.Entries.Count; i++) {
            var je = root.Entries[i];
            var e = new Dsa3Entry {
                Name = je.Name ?? "",
                mUnknown1 = je.Unknown1U8,
                Size = je.SizeU32,
                mDosDateTime = je.DosDateTimeU32, // renamed field
                mUnknown3 = je.Unknown3U16,
                Offset = je.OffsetU32
            };
            if (!string.IsNullOrEmpty(je.RawFilenameHex)) {
                var raw = HexToBytes(je.RawFilenameHex);
                if (raw.Length != 13)
                    throw new InvalidOperationException($"entry {i} raw_filename_hex must be 13 bytes (26 hex chars), got {raw.Length} bytes");
                e.NameRaw13 = raw;
            }
            dsa.Entries.Add(e);
        }

        // Modules + modmap
        if (root.Modules.Count != h.ModuleCountU16)
            throw new InvalidOperationException("modules length != header.module_count_u16");

        uint maxMapEnd = 0;
        for (int mi = 0; mi < root.Modules.Count; mi++) {
            var jm = root.Modules[mi];
            var m = new Dsa3ModuleEntry {
                Name = jm.Name ?? "",
                Size = jm.SizeSlotsU32,
                mDosDateTime = jm.DosDateTimeU32,
                mUnknown2 = jm.Unknown2U16,
                Offset = jm.ModMapStartU32 * 2
            };
            if (!string.IsNullOrEmpty(jm.RawFilenameHex)) {
                var raw = HexToBytes(jm.RawFilenameHex);
                if (raw.Length != 14)
                    throw new InvalidOperationException($"module {mi} raw_filename_hex must be 14 bytes (28 hex chars), got {raw.Length} bytes");
                m.NameRaw14 = raw;
            }
            dsa.mModules.Add(m);

            if (jm.Slots is null) throw new InvalidOperationException($"module {mi} has null slots");
            int start = (int)jm.ModMapStartU32;
            EnsureModMapCapacity(dsa.mModMap, start + jm.Slots.Count);
            for (int s = 0; s < jm.Slots.Count; s++)
                dsa.mModMap[start + s] = jm.Slots[s];

            uint end = jm.ModMapStartU32 + (uint)jm.Slots.Count;
            if (end > maxMapEnd) maxMapEnd = end;
        }

        

        // Sanity: every modmap entry either 0xFFFF or < file_count
        for (int i = 0; i < dsa.mModMap.Count; i++) {
            uint v = dsa.mModMap[i];
            if (v == 0xFFFF) continue;
            if (v >= dsa.Count)
                throw new InvalidOperationException($"modmap index {i} points to out-of-range entry {v}");
        }

        // Trim or pad to the exact max extent
        if (dsa.mModMap.Count > maxMapEnd)
            dsa.mModMap.RemoveRange((int)maxMapEnd, dsa.mModMap.Count - (int)maxMapEnd);
        else
            EnsureModMapCapacity(dsa.mModMap, (int)maxMapEnd);

        return (cfg, dsa);
    }

    private static byte[] HexToBytes16(string hex) {
        if (hex is null) throw new ArgumentNullException(nameof(hex));
        if (hex.Length != 32)
            throw new ArgumentException("header.unknown16_hex must be exactly 32 hex characters.");
        return HexToBytes(hex);
    }


    private static void EnsureModMapCapacity(List<uint> modmap, int size) {
        while (modmap.Count < size) modmap.Add(0);
    }

    private static string ResolveIdxDir(string objectsRoot) {
        // Accept either <root>/objects/idx, <root>/idx or direct path to idx.
        var a = Path.Combine(objectsRoot, "objects", "idx");
        var b = Path.Combine(objectsRoot, "idx");
        if (Directory.Exists(a)) return a;
        if (Directory.Exists(b)) return b;
        if (Directory.Exists(objectsRoot)) return objectsRoot;
        throw new DirectoryNotFoundException($"Could not find objects/idx under '{objectsRoot}'");
    }

    private static IList<byte[]> LoadEntryDataByIndex(JsonRoot root, string idxDir) {
        if (root.Entries is null) throw new InvalidOperationException("JSON.entries missing");
        var list = new List<byte[]>(root.Entries.Count);
        for (int i = 0; i < root.Entries.Count; i++) {
            var p = Path.Combine(idxDir, i.ToString("D5") + ".dat");
            if (!File.Exists(p))
                throw new FileNotFoundException($"Missing payload object: {p}");
            var bytes = File.ReadAllBytes(p);
            var expected = root.Entries[i].SizeU32;
            if (bytes.Length != expected)
                throw new InvalidOperationException($"Size mismatch for {Path.GetFileName(p)}: bytes={bytes.Length}, JSON={expected}");
            list.Add(bytes);
        }
        return list;
    }

    private static uint ComputeExactArchiveSize(JsonRoot root) {
        if (root.Header is null || root.Modules is null || root.Entries is null)
            throw new InvalidOperationException("JSON missing header/modules/entries");

        var h = root.Header;

        // 28 bytes per module/file entry in your format
        const ulong ModRec = 28UL;
        const ulong FileRec = 28UL;

        // Module table end
        ulong modulesEnd = (ulong)h.ModuleTableOffsetU32 + (ulong)root.Modules.Count * ModRec;

        // File table end
        ulong filesEnd = (ulong)h.FileTableOffsetU32 + (ulong)root.Entries.Count * FileRec;

        // Modmap size: max over (modmap_start + slot_count) across modules
        uint modMapSize = 0;
        foreach (var m in root.Modules) {
            uint slots = (uint)(m.Slots?.Count ?? 0);
            uint end = checked(m.ModMapStartU32 + slots);
            if (end > modMapSize) modMapSize = end;
        }
        ulong modmapEnd = (ulong)h.ModMapOffsetU32 + (ulong)modMapSize * 2UL; // uint16 entries

        // Data region end: data_offset + max(entry.offset + size)
        ulong dataEnd = (ulong)h.DataOffsetU32;
        foreach (var e in root.Entries) {
            ulong end = (ulong)h.DataOffsetU32 + (ulong)e.OffsetU32 + (ulong)e.SizeU32;
            if (end > dataEnd) dataEnd = end;
        }

        // Final file size must cover all four regions
        ulong maxEnd = modulesEnd;
        if (filesEnd > maxEnd) maxEnd = filesEnd;
        if (modmapEnd > maxEnd) maxEnd = modmapEnd;
        if (dataEnd > maxEnd) maxEnd = dataEnd;

        return checked((uint)maxEnd);
    }

    private static void ValidateJsonStructure(JsonRoot root) {
        if (root.Header is null) throw new InvalidOperationException("JSON.header missing");
        if (root.Entries is null) throw new InvalidOperationException("JSON.entries missing");
        if (root.Modules is null) throw new InvalidOperationException("JSON.modules missing");

        var h = root.Header;

        if (root.Entries.Count != h.FileCountU16)
            throw new InvalidOperationException($"entries length ({root.Entries.Count}) != header.file_count_u16 ({h.FileCountU16})");

        if (root.Modules.Count != h.ModuleCountU16)
            throw new InvalidOperationException($"modules length ({root.Modules.Count}) != header.module_count_u16 ({h.ModuleCountU16})");

        for (int mi = 0; mi < root.Modules.Count; mi++) {
            var m = root.Modules[mi];
            int slotsLen = m.Slots?.Count ?? 0;
            if ((uint)slotsLen != m.SizeSlotsU32)
                throw new InvalidOperationException($"module {mi} '{m.Name}' slots length ({slotsLen}) != size_slots_u32 ({m.SizeSlotsU32})");

            if (m.ModMapStartU32 > 100_000_000) // sanity guard for absurd values
                throw new InvalidOperationException($"module {mi} modmap_start_u32 looks invalid: {m.ModMapStartU32}");

            if (m.Slots != null) {
                for (int s = 0; s < m.Slots.Count; s++) {
                    uint v = m.Slots[s];
                    if (v == 0xFFFF) continue;
                    if (v >= h.FileCountU16)
                        throw new InvalidOperationException($"module {mi} slot {s}: modmap index {v} >= file_count ({h.FileCountU16})");
                }
            }
        }

        // Header unknown16 must be 16 bytes (32 hex chars)
        var unk = root.Header.Unknown16Hex ?? "00000000000000000000000000000000";
        if (unk.Length != 32)
            throw new InvalidOperationException($"header.unknown16_hex must be 32 hex chars, got {unk.Length}");
    }



    private static byte[] HexToBytes(string hex) {
        if (hex.Length % 2 != 0) throw new ArgumentException("hex len");
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }


    // (export-master removed: .FN/.MOD tooling is out of scope)



    // -----------------------
    // Helpers
    // -----------------------
    private static (Config cfg, Dsa3 dsa) LoadDsa3(string archive) {
        if (!File.Exists(archive))
            throw new FileNotFoundException($"Archive not found: {archive}");

        var cfg = new Config {
            mArchiveName = archive,
            mFilePattern = "*",
            mPathDelim = PathDelim()
        };
        var dsa = new Dsa3(cfg);
        using var ifs = new Ifstream(archive);
        dsa.Read(ifs);
        return (cfg, dsa);
    }

    private static void ApplyPattern(Dsa3 dsa, string pattern) {
        if (string.IsNullOrEmpty(pattern) || pattern == "*") return;
        var pat = pattern.Replace('\\', '/');
        for (int mi = 0; mi < dsa.mModuleCount; mi++) {
            var mod = dsa.mModules[mi];
            for (int slot = 0; slot < mod.Size; slot++) {
                int mapIdx = (int)(mod.Offset / 2 + slot);
                if (mapIdx < 0) continue;
                var idx = (int)dsa.mModMap[mapIdx];
                if (idx == 0xFFFF) continue;
                var e = (Dsa3Entry)dsa.Entries[idx];
                var name = mod.Name + "/" + e.Name;
                if (!GlobMatch(name, pat))
                    dsa.mModMap[mapIdx] = 0xFFFF;
            }
        }
    }

    private static bool GlobMatch(string name, string pattern)
        => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true);

    private static IEnumerable<(Dsa3ModuleEntry mod, int slot, int? entryIndex)> EnumerateVisibleSlots(Dsa3 dsa) {
        for (int mi = 0; mi < dsa.mModuleCount; mi++) {
            var mod = dsa.mModules[mi];
            for (int slot = 0; slot < mod.Size; slot++) {
                int mapIdx = (int)(mod.Offset / 2 + slot);
                if (mapIdx < 0 || mapIdx >= dsa.mModMap.Count) continue;
                var idx = (int)dsa.mModMap[mapIdx];
                if (idx == 0xFFFF) yield return (mod, slot, null);
                else yield return (mod, slot, idx);
            }
        }
    }

    private static (Dsa3ModuleEntry mod, Dsa3Entry entry, int moduleSlot, int index)? FindEntry(Dsa3 dsa, string moduleName, string filename) {
        for (int mi = 0; mi < dsa.mModuleCount; mi++) {
            var mod = dsa.mModules[mi];
            if (!string.Equals(mod.Name, moduleName, CI)) continue;

            for (int slot = 0; slot < mod.Size; slot++) {
                int mapIdx = (int)(mod.Offset / 2 + slot);
                var idx = (int)dsa.mModMap[mapIdx];
                if (idx == 0xFFFF) continue;
                var e = (Dsa3Entry)dsa.Entries[idx];
                if (string.Equals(e.Name, filename, CI))
                    return (mod, e, slot, idx);
            }
        }
        return null;
    }

    private static byte[] ReadBytes(Ifstream ifs, Dsa3 dsa, Dsa3Entry entry) {
        ifs.Seekg(entry.Offset + dsa.HeaderSize);
        return ifs.ReadChars((int)entry.Size);
    }

    private static char PathDelim() {
        // Default to OS separator; patterns accept either '/' or '\'
        return Path.DirectorySeparatorChar;
    }

    private static string NeedValue(string[] args, ref int i, string name) {
        if (i + 1 >= args.Length) throw new ArgumentException($"Missing value for {name}");
        return args[++i];
    }

    private static bool IsHelp(string s) => s is "-h" or "--help" or "/?";

    private static int PrintTopHelp() {
        Console.WriteLine(@"
RivaXtract — DSA3 archive tooling

Usage:
  rivaxtract <command> [args]

  Commands:
  list          <archive> [--pattern <glob>] [--modules-only] [--csv <path>]
                List contents. Pattern matches ""Module\Filename"".

  extract       <archive> -o <outdir> [--pattern <glob>] [--module-dirs] [--overwrite ask|never|always]
                Extract files to disk. Use --module-dirs to create Module/Filename layout.

  read          <archive> <module> <filename> [-o <path>] [--offset N] [--length N] [--info]
                Output one entry's bytes (to stdout or file) or print metadata with --info.

  export-json   <archive> --out structure.json [--pretty] [--include-modmap-raw]
                Export a homomorphic JSON (header/tables/modmap, no payload bytes).

  export-views  <archive> --out <root>
                [--views index,module-slot,module-name,name|all]
                [--mode hard|symlink|copy] [--dummy omit|marker]
                Write objects/idx/IIIII.dat and link/copy views.

  modify        <archive> [--out <new-archive>]
                [--set MOD/FILE=path]... [--from-dir <root>] [--map <csv|json>]
                [--strict] [--dry-run] [--inplace] [--touch]
                Replace entry data while preserving indices, slots, and dummies.
                - Same-size edits: in-place patch (no header changes; --out copies then patches).
                - Size changes: repack to --out; recomputes offsets/sizes, mapping unchanged.
                - --touch: set FAT/DOS timestamp on modified entries (repack path only).

  repack        <old-archive> <new-archive>
                Rebuild archive with compacted layout while preserving indices,
                slots, dummies and mapping. Offsets/sizes are recomputed.

  build         <structure.json> --objects <root> --out <archive>
                Verbatim build from JSON + objects. Preserves indices, slots, dummies,
                table offsets and entry offsets. Fails on any size mismatch.

Examples:
  rivaxtract export-json GAME.ALF --out GAME.structure.json --pretty
  rivaxtract export-views GAME.ALF --out outdir --views index,module-slot
  rivaxtract build GAME.structure.json --objects outdir --out GAME_rebuilt.ALF
");
        return 0;
    }





    private static int Fail(string msg) {
        Console.Error.WriteLine(msg);
        return 1;
    }
}

