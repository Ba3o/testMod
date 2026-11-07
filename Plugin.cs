using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using MaJiang.GM;
using MaJiang.PlayMaJiang;
using MaJiang.PlayMaJiang.Player;

/*
 * =========================================================================================
 * DemonicMahjongMod — Focused Mod for Demonic Mahjong v2.70.0
 * Target: Unity 6000.3.21f1  |  BepInEx 6.0.0-be.793  |  TargetFramework net6.0
 *
 * ACTIVE MECHANICS:
 *  1. ExtraTotalDraws: Grants additional total draws/swaps per round (default: 8).
 *  2. DrawsPerTurn:    Sets the number of tiles dealt/drawn each turn (default: 6, game base: 4).
 * =========================================================================================
 */

namespace DemonicMahjongMod
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public class Plugin : BasePlugin
    {
        internal static ManualLogSource Log = null!;
        internal static PluginConfig Cfg = null!;

        public override void Load()
        {
            Log = base.Log;
            Cfg = new PluginConfig(Config);

            Log.LogInfo($"[{MyPluginInfo.PLUGIN_NAME}] Loading v{MyPluginInfo.PLUGIN_VERSION} (ExtraTotalDraws={Cfg.ExtraTotalDraws.Value}, DrawsPerTurn={Cfg.DrawsPerTurn.Value})...");

            var harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
            int successCount = 0;
            int totalCount = 0;

            foreach (var type in typeof(Plugin).Assembly.GetTypes())
            {
                var patchAttr = type.GetCustomAttribute<HarmonyPatch>();
                if (patchAttr != null)
                {
                    totalCount++;
                    try
                    {
                        harmony.CreateClassProcessor(type).Patch();
                        successCount++;
                        Log.LogInfo($"[{MyPluginInfo.PLUGIN_NAME}] [OK] Patched {type.Name}");
                    }
                    catch (Exception ex)
                    {
                        Log.LogError($"[{MyPluginInfo.PLUGIN_NAME}] [FAIL] Failed {type.Name}: {ex.Message}");
                    }
                }
            }

            Log.LogInfo($"[{MyPluginInfo.PLUGIN_NAME}] Initialized {successCount}/{totalCount} patches successfully!");
        }
    }

    // =====================================================================================
    //  Configuration
    // =====================================================================================
    internal class PluginConfig
    {
        public ConfigEntry<int> ExtraTotalDraws { get; }
        public ConfigEntry<int> DrawsPerTurn    { get; }

        internal PluginConfig(ConfigFile cfg)
        {
            const string cat = "Mechanics";

            ExtraTotalDraws = cfg.Bind(cat, "ExtraTotalDraws", 8,
                new ConfigDescription("Additional total draw/swap allowance for each round (default: 8).",
                    new AcceptableValueRange<int>(0, 100)));

            DrawsPerTurn = cfg.Bind(cat, "DrawsPerTurn", 6,
                new ConfigDescription("Number of tiles dealt/drawn into the swap tray each turn (default: 6, game base: 4).",
                    new AcceptableValueRange<int>(1, 20)));
        }
    }

    // =====================================================================================
    //  MECHANIC 1: Total Draws Per Round (ExtraTotalDraws = 8)
    // =====================================================================================

    /// <summary>
    /// Boosts round swap counter initialization parameter BEFORE it sets up.
    /// </summary>
    [HarmonyPatch(typeof(PlayerSwapPaiLeftCount), nameof(PlayerSwapPaiLeftCount.InitTotalCount))]
    internal static class Patch_PlayerSwapPaiLeftCount_InitTotalCount
    {
        [HarmonyPrefix]
        static void Prefix(ref int totalCount)
        {
            int extra = Plugin.Cfg.ExtraTotalDraws.Value;
            if (extra > 0)
            {
                totalCount += extra;
                Plugin.Log.LogInfo($"[PlayerSwapPaiLeftCount] InitTotalCount boosted totalCount to {totalCount}");
            }
        }

        [HarmonyPostfix]
        static void Postfix(PlayerSwapPaiLeftCount __instance)
        {
            try
            {
                Plugin.Log.LogInfo($"[PlayerSwapPaiLeftCount] Active round draws initialized: Total={__instance._totalCount}, Left={__instance.SwapPaiLeftCount}");
            }
            catch { }
        }
    }

    /// <summary>
    /// Extends remaining turn count in round statistics to prevent early round end.
    /// </summary>
    [HarmonyPatch(typeof(InRoundStatistics), nameof(InRoundStatistics.GetLeftTurnCount))]
    internal static class Patch_InRoundStatistics_GetLeftTurnCount
    {
        [HarmonyPostfix]
        static void Postfix(ref int __result)
        {
            int extra = Plugin.Cfg.ExtraTotalDraws.Value;
            if (extra > 0)
            {
                __result += extra;
            }
        }
    }

    // =====================================================================================
    //  MECHANIC 2: Draws Per Turn (DrawsPerTurn = 6)
    // =====================================================================================

    /// <summary>
    /// Intercepts tile popping from the deck. When 4 tiles are requested for a turn,
    /// replaces the request with the target DrawsPerTurn (default 6).
    /// </summary>
    [HarmonyPatch(typeof(AllPaiMianContainer), nameof(AllPaiMianContainer.PopCount))]
    internal static class Patch_AllPaiMianContainer_PopCount
    {
        [HarmonyPrefix]
        static void Prefix(ref int count)
        {
            int target = Plugin.Cfg.DrawsPerTurn.Value;
            Plugin.Log.LogInfo($"[AllPaiMianContainer] PopCount called with count={count}");
            if (target > 0 && count == 4)
            {
                count = target;
                Plugin.Log.LogInfo($"[AllPaiMianContainer] PopCount(4) replaced with {count}");
            }
        }
    }

    /// <summary>
    /// Overrides SwapPaiTurnTotal getter on Saver to report the target DrawsPerTurn.
    /// </summary>
    [HarmonyPatch(typeof(Saver), "get_SwapPaiTurnTotal")]
    internal static class Patch_Saver_GetSwapPaiTurnTotal
    {
        [HarmonyPostfix]
        static void Postfix(Saver __instance, ref int __result)
        {
            int target = Plugin.Cfg.DrawsPerTurn.Value;
            if (target > 0)
            {
                __result = target;
                try
                {
                    if (__instance != null)
                    {
                        __instance._swapPaiTurnTotal = target;
                        if (__instance._gameConfig != null)
                        {
                            __instance._gameConfig.swapPaiTurnTotal = target;
                        }
                    }
                }
                catch { }
            }
        }
    }

    /// <summary>
    /// Intercepts SwapPaiTurnTotal setter on Saver to persist target DrawsPerTurn.
    /// </summary>
    [HarmonyPatch(typeof(Saver), "set_SwapPaiTurnTotal")]
    internal static class Patch_Saver_SetSwapPaiTurnTotal
    {
        [HarmonyPrefix]
        static void Prefix(Saver __instance, ref int value)
        {
            int target = Plugin.Cfg.DrawsPerTurn.Value;
            if (target > 0)
            {
                value = target;
                try
                {
                    if (__instance != null)
                    {
                        __instance._swapPaiTurnTotal = target;
                        if (__instance._gameConfig != null)
                        {
                            __instance._gameConfig.swapPaiTurnTotal = target;
                        }
                    }
                }
                catch { }
            }
        }
    }

    /// <summary>
    /// Verifies and logs active displays when swap begins.
    /// </summary>
    [HarmonyPatch(typeof(PlayerSwapPaiMianContainer), nameof(PlayerSwapPaiMianContainer.StartSwap))]
    internal static class Patch_PlayerSwapPaiMianContainer_StartSwap
    {
        [HarmonyPostfix]
        static void Postfix(PlayerSwapPaiMianContainer __instance)
        {
            try
            {
                int count = __instance.PaiMianDisplays != null ? __instance.PaiMianDisplays.Count : 0;
                Plugin.Log.LogInfo($"[PlayerSwapPaiMianContainer] StartSwap active displays count = {count}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[PlayerSwapPaiMianContainer] StartSwap log error: {ex.Message}");
            }
        }
    }
}
