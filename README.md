RivaXtract CLI
================

RivaXtract is a .NET 8 command-line tool for exploring and manipulating DSA3 `ALF` archives. It can list and extract contents, read individual entries, export a structure-only JSON, export convenient on-disk views, modify entries deterministically, and build archives verbatim from JSON + object files.

**Project website:** <https://rivaxtract.tinion.tech/>  
**Contact:** <mailto:sautter@tinion.tech> • Issues: <https://github.com/cmsautter/RivaXtractCLI/issues>


Features
--------
- List modules and entries; optional CSV export
- Extract with module directory layout and overwrite policies
- Read a single entry to stdout or file with slicing
- Export homomorphic JSON (header, file table, module table, Mod-map)
- Export objects plus multiple folder “views” (hardlinks/symlinks/copies)
- Modify in-place if sizes are unchanged; repack if any size changes
- Build verbatim archives from JSON + objects, preserving offsets and tables

Install / Build
---------------
- Requires .NET 8 SDK.
- Build: `dotnet build -c Release`
- Publish self-contained (example Windows):
  - `dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true -p:SelfContained=true`

Usage
-----
Run `rivaxtract help` for full help.

Examples:
- Export JSON: `rivaxtract export-json RIVA.ALF --out RIVA.structure.json --pretty`
- Export views: `rivaxtract export-views RIVA.ALF --out outdir --views index,module-slot`
- Modify in-place (same size): `rivaxtract modify RIVA.ALF --set "MOD/FILE=path.bin" --inplace`
- Build from JSON: `rivaxtract build RIVA.structure.json --objects outdir --out RIVA_rebuilt.ALF`

Provenance
----------
- Inspired by Hendrik Radke’s **nltpack**; evolved into a **clean C# reimplementation**.  
  Repo: <https://github.com/Henne/BrightEyes/tree/master/tools/nltpack>

Differences vs. nltpack
-----------------------
- Verbatim writer; compact repacker
- Raw name byte round-tripping
- Extracts DOS timestamps (FAT) for entries/modules
- JSON import/export of header/tables/modmap
- Multi-view exports

Credits
-------
- Hendrik Radke’s **nltpack**; Helios’ `rivapack.rb`

License
-------
MIT © 2025 [tinion] Carl Marvin Sautter
