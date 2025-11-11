// MIT License
// Copyright (c) 2025 [tinion] Carl Marvin Sautter
// See LICENSE file in the project root for full license text.

﻿namespace RivaXtractCLI {
    internal static class DosDateTime {
        // Decode packed FAT datetime (LE) to a local DateTime.
        // Returns null for clearly invalid values (month/day == 0).
        public static DateTime? Decode(uint packed) {
            ushort t = (ushort)(packed & 0xFFFF);
            ushort d = (ushort)((packed >> 16) & 0xFFFF);

            int sec = (t & 0x1F) * 2;
            int min = (t >> 5) & 0x3F;
            int hour = (t >> 11) & 0x1F;

            int day = d & 0x1F;
            int month = (d >> 5) & 0x0F;
            int year = ((d >> 9) & 0x7F) + 1980;

            // basic guard: month/day must be in range
            if (month == 0 || day == 0) return null;

            try {
                // Interpret as LOCAL time (DOS stores local time, no TZ).
                return new DateTime(year, month, day, hour, min, sec, DateTimeKind.Local);
            } catch {
                return null;
            }
        }

        // Encode local DateTime into packed FAT datetime (truncates to even seconds).
        public static uint Encode(DateTime local) {
            if (local.Kind == DateTimeKind.Utc)
                local = local.ToLocalTime();

            int year = Math.Clamp(local.Year - 1980, 0, 127);
            int month = Math.Clamp(local.Month, 1, 12);
            int day = Math.Clamp(local.Day, 1, 31);
            int hour = Math.Clamp(local.Hour, 0, 23);
            int min = Math.Clamp(local.Minute, 0, 59);
            int sec2 = Math.Clamp(local.Second / 2, 0, 29); // 2-second resolution

            ushort d = (ushort)((year << 9) | (month << 5) | day);
            ushort t = (ushort)((hour << 11) | (min << 5) | sec2);
            return (uint)((d << 16) | t);
        }

        // ISO 8601 without timezone (local wall-clock). Example: 1996-11-18T11:32:06
        public static string ToLocalIso(DateTime? dt) =>
            dt?.ToString("yyyy-MM-dd'T'HH:mm:ss") ?? null;
    }
}
