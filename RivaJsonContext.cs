// MIT License
// Copyright (c) 2025 [tinion] Carl Marvin Sautter
// See LICENSE file in the project root for full license text.

﻿// Source generation context for JSON serialization
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RivaXtractCLI;

[JsonSourceGenerationOptions(WriteIndented = false)] // base; can still pass runtime options
[JsonSerializable(typeof(JsonRoot))]
[JsonSerializable(typeof(List<MapItem>))]   // for modify --map JSON
[JsonSerializable(typeof(MapItem))]
internal partial class RivaJsonContext : JsonSerializerContext {
}
