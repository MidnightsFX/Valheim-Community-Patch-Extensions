using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

#pragma warning disable IDE0130
namespace CommunityPatchExtras {
#pragma warning restore IDE0130

    // Argument readers and option lists commands share for tab-completion.
    internal static class TerminalArgs {
        internal static string GetString(this string[] args, int index, string fallback = "") {
            if (args == null || args.Length <= index) { return fallback; }
            return args[index];
        }

        // Everything from `index` on, joined back together. For free-text trailing arguments.
        internal static string GetStringFrom(this string[] args, int index, string fallback = "") {
            if (args == null || args.Length <= index) { return fallback; }
            return string.Join(" ", args.Skip(index));
        }

        internal static int GetInt(this string[] args, int index, int fallback = 0) {
            string raw = args.GetString(index, null);
            if (raw == null) { return fallback; }
            return int.TryParse(raw, out int parsed) ? parsed : fallback;
        }

        internal static float GetFloat(this string[] args, int index, float fallback = 0f) {
            string raw = args.GetString(index, null);
            if (raw == null) { return fallback; }
            return float.TryParse(raw, out float parsed) ? parsed : fallback;
        }

        internal static T GetEnum<T>(this string[] args, int index, T fallback) where T : struct, Enum {
            string raw = args.GetString(index, null);
            if (raw == null) { return fallback; }
            return Enum.TryParse(raw, true, out T parsed) ? parsed : fallback;
        }

        // Reports the fallback rather than silently using it, and clamps so a fat-fingered radius cannot
        // ask for the whole world.
        internal static float ReadRadius(this ModCommandArgs args, int index, float fallback, float max) {
            string raw = args.Args.GetString(index, null);
            if (raw == null) { return fallback; }
            if (float.TryParse(raw, out float parsed) == false) {
                args.Output.Warning($"Radius must be a number; using {fallback}.");
                return fallback;
            }
            return Mathf.Clamp(parsed, 0f, max);
        }

        internal static List<string> Names<T>() where T : struct, Enum {
            return Enum.GetNames(typeof(T)).ToList();
        }

        internal static List<string> RadiusPresets(string[] input) {
            return new List<string>() { "8", "16", "32", "64", "128", "256" };
        }

        // Example of a volatile list: rebuilt from game state, cached so it is not recomputed on every
        // keystroke. Copy this shape for item names, prefab names, config keys and so on.
        private const float CacheSeconds = 60f;
        private static readonly List<string> creatureNames = new List<string>();
        private static float creatureNamesAt;

        internal static List<string> CreatureNames(string[] input) {
            if (ZNetScene.instance == null) { return creatureNames; }
            if (creatureNames.Count == 0 || Time.realtimeSinceStartup - creatureNamesAt > CacheSeconds) {
                creatureNames.Clear();
                creatureNames.AddRange(ZNetScene.instance.m_prefabs
                    .Where(p => p != null && p.GetComponent<Character>() != null)
                    .Select(p => p.name));
                creatureNamesAt = Time.realtimeSinceStartup;
            }
            return creatureNames;
        }
    }
}
