using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using Morgott.Preloader.Core;
using HarmonyLib;
using UnityEngine;
using VNEI;
using VNEI.Logic;
using VNEI.Patches;

namespace Morgott.Preloader.ModHotspots
{
    /// <summary>
    /// <c>[Diagnostics] DumpModHotspots</c> with VNEI loaded: on leaving the world (<c>Game.Logout</c> or
    /// <c>Game.OnApplicationQuit</c>), the first time an index exists, writes VNEI's whole index to
    /// BepInEx\Morgott.Preloader\modhotspots-vnei.txt in its own order: every item (names, texts, type, flags, mod, prefab,
    /// icon sprite + texture + pixel hash when readable, the recipes it is result / ingredient of), both name lookups,
    /// every recipe (stations, ingredient and result groups with amounts, width, flags) and the subscriber counts of
    /// the events <c>Plugin.Update</c> and <c>Item</c> subscribe to. VNEIIndexing off vs on must give the same file.
    /// Written on leaving, not in play: the ~100 ms dump would land in the frames the profiler measures.
    /// </summary>
    internal static class VneiDump
    {
        private static bool _written;

        public static void Install(Harmony harmony)
        {
            // Low: after VneiIndexer's prefixes (Priority.High) finished an index still in progress.
            var prefix = new HarmonyMethod(AccessTools.Method(typeof(VneiDump), nameof(LeavePrefix))) { priority = Priority.Low };
            harmony.Patch(AccessTools.DeclaredMethod(typeof(Game), nameof(Game.Logout)), prefix: prefix);
            harmony.Patch(AccessTools.DeclaredMethod(typeof(Game), "OnApplicationQuit"), prefix: prefix);
        }

        private static void LeavePrefix()
        {
            // Only a whole index (VneiIndexer drained one in progress before this prefix; Pending is false when the
            // replacement is not installed).
            if (_written || Items().Count == 0 || RecipeInfo.Recipes.Count == 0 || VneiIndexer.Pending)
            {
                return;
            }
            Log.Guard("VNEI index dump", Write);
        }

        private static Dictionary<string, Item> Items() => Static<Dictionary<string, Item>>(typeof(Indexing), "<Items>k__BackingField");

        private static T Static<T>(Type type, string field) => (T)AccessTools.DeclaredField(type, field).GetValue(null);

        private static void Write()
        {
            var sb = new StringBuilder();
            List<RecipeInfo> recipes = RecipeInfo.Recipes;
            var index = new Dictionary<RecipeInfo, int>();
            for (int i = 0; i < recipes.Count; i++)
            {
                index[recipes[i]] = i;
            }
            FieldInfo iconField = AccessTools.DeclaredField(typeof(Item), "icon");
            Dictionary<string, Item> items = Items();
            sb.Append("== items ").Append(items.Count).Append('\n');
            foreach (KeyValuePair<string, Item> pair in items)
            {
                Item item = pair.Value;
                sb.Append(pair.Key).Append(" | ").Append(item.internalName).Append(" | ").Append(item.preLocalizeName)
                  .Append(" | ").Append(item.localizedName).Append(" | ").Append(Esc(item.description))
                  .Append(" | ").Append(Esc(item.localizedDescription)).Append(" | ").Append(item.itemType)
                  .Append(" | ").Append(item.itemDropType).Append(" | active=").Append(item.isActive)
                  .Append(" q=").Append(item.maxQuality).Append(" fav=").Append(item.isFavorite)
                  .Append(" black=").Append(item.isOnBlacklist).Append(" self=").Append(item.IsSelfKnown)
                  .Append(" known=").Append(item.IsKnown).Append(" | mod=").Append(item.mod?.GUID)
                  .Append(" | prefab=").Append(item.prefab ? item.prefab.name : "null")
                  .Append(" | icon=").Append(Icon((Sprite)iconField.GetValue(item)))
                  .Append(" | result=").Append(Indices(item.result, index))
                  .Append(" | ingredient=").Append(Indices(item.ingredient, index)).Append('\n');
            }
            AppendLookup(sb, "by prelocalized name", Static<Dictionary<string, Item>>(typeof(Indexing), "<ItemsByPreLocalizedName>k__BackingField"));
            AppendLookup(sb, "by localized name", Static<Dictionary<string, Item>>(typeof(Indexing), "<ItemsByLocalizedName>k__BackingField"));
            sb.Append("== recipes ").Append(recipes.Count).Append('\n');
            for (int i = 0; i < recipes.Count; i++)
            {
                RecipeInfo recipe = recipes[i];
                sb.Append(i).Append(" w=").Append(recipe.Width.ToString("R", CultureInfo.InvariantCulture))
                  .Append(" black=").Append(recipe.IsOnBlacklist).Append(" self=").Append(recipe.IsSelfKnown)
                  .Append(" | st ").Append(string.Join(",", recipe.Stations.Select(Part).ToArray()))
                  .Append(" | in ").Append(Groups(recipe.Ingredients)).Append(" | out ").Append(Groups(recipe.Results)).Append('\n');
            }
            Plugin plugin = Plugin.Instance;
            sb.Append("== plugin stations ").Append(plugin.allStations?.internalName).Append(' ')
              .Append(plugin.handStation?.internalName).Append(' ').Append(plugin.noStation?.internalName).Append('\n');
            sb.Append("== subscribers OnLanguageChange=").Append(Count(Localization.OnLanguageChange))
              .Append(" OnUpdateKnownRecipes=").Append(Count(Static<Delegate>(typeof(KnownRecipesPatches), "OnUpdateKnownRecipes")))
              .Append(" IndexFinished=").Append(Count(Static<Delegate>(typeof(Indexing), "IndexFinished")))
              .Append(" showOnlyKnown.SettingChanged=").Append(SettingChanged(typeof(Plugin), "showOnlyKnown"))
              .Append(" forceShowOnlyKnown.SettingChanged=").Append(SettingChanged(typeof(Plugin), "forceShowOnlyKnown"))
              .Append(" modNames=").Append(Static<System.Collections.ICollection>(typeof(ModNames), "SourceMod").Count).Append('\n');
            string path = Path.Combine(Paths.BepInExRootPath, "Morgott.Preloader", "modhotspots-vnei.txt");
            AtomicFile.WriteAllText(path, sb.ToString());
            _written = true;
            Log.Info($"ModHotspots: VNEI index ({items.Count} items, {recipes.Count} recipes) written to {path}");
        }

        private static void AppendLookup(StringBuilder sb, string title, Dictionary<string, Item> lookup)
        {
            sb.Append("== ").Append(title).Append(' ').Append(lookup.Count).Append('\n');
            foreach (KeyValuePair<string, Item> pair in lookup)
            {
                sb.Append(pair.Key).Append(" -> ").Append(pair.Value.internalName).Append('\n');
            }
        }

        private static string Esc(string s) => s?.Replace("\n", "\\n").Replace("\r", "\\r");

        // HashSet<RecipeInfo> order follows object hash codes (differs per run): sorted.
        private static string Indices(IEnumerable<RecipeInfo> set, Dictionary<RecipeInfo, int> index) =>
            string.Join(",", set.Select(r => index.TryGetValue(r, out int i) ? i : -1).OrderBy(i => i)
                .Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray());

        private static string Part(Part part) =>
            (part.item?.internalName ?? "null") + ":" + Amount(part.amount) + ":q" + part.quality;

        private static string Amount(Amount a) =>
            a.min + "-" + a.max + "@" + a.chance.ToString("R", CultureInfo.InvariantCulture);

        private static string Groups(Dictionary<Amount, List<Part>> groups) =>
            string.Join(" ", groups.Select(g => "[" + Amount(g.Key) + ": " +
                string.Join(",", (g.Value ?? new List<Part>()).Select(Part).ToArray()) + "]").ToArray());

        private static int Count(Delegate d) => d?.GetInvocationList().Length ?? 0;

        private static int SettingChanged(Type type, string field)
        {
            var entry = (ConfigEntryBase)AccessTools.DeclaredField(type, field).GetValue(null);
            // The event is declared on ConfigEntry<T>.
            return Count((Delegate)AccessTools.Field(entry.GetType(), "SettingChanged").GetValue(entry));
        }

        private static string Icon(Sprite sprite)
        {
            if (!sprite)
            {
                return "none";
            }
            Texture2D texture = sprite.texture;
            string pixels = "-";
            if (texture && texture.isReadable)
            {
                using (SHA1 sha = SHA1.Create())
                {
                    pixels = string.Concat(sha.ComputeHash(texture.GetRawTextureData()).Take(8).Select(b => b.ToString("x2")).ToArray());
                }
            }
            Rect rect = sprite.rect;
            return string.Format(CultureInfo.InvariantCulture, "{0} tex={1} {2}x{3} {4} rect={5},{6},{7},{8} px={9}", sprite.name,
                texture ? texture.name : "null", texture ? texture.width : 0, texture ? texture.height : 0,
                texture ? texture.format.ToString() : "-", rect.x, rect.y, rect.width, rect.height, pixels);
        }
    }
}
