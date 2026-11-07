using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using MaJiang.PlayMaJiang;
using MaJiang.PlayMaJiang.InOut;
using MaJiang.PlayMaJiang.Player;
using MaJiang.PlayMaJiang.Player.HuPai;

/*
 * =========================================================================================
 * DemonicMahjongMod — Focused Mod for Demonic Mahjong v2.70.0
 * Target: Unity 6000.3.21f1  |  BepInEx 6.0.0-be.793  |  TargetFramework net6.0
 *
 * STRICTLY FOCUSED MECHANICS:
 *  1. ExtraDrawsPerTurn: Tiles drawn/swapped per turn action.
 *  2. ExtraTotalDraws:   Total round draw/swap allowance.
 *  3. BonusHuSlots:      Winning hand / Hu slots capacity.
 *
 * STABILITY GUARANTEES:
 *  - NO GameManager or GlobalStaticDataManager MonoBehaviour hooks (avoids 0xc0000005 crash).
 *  - NO ClassInjector or custom MonoBehaviour injection.
 *  - Per-patch error isolation during initialization.
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

            Log.LogInfo($"[{MyPluginInfo.PLUGIN_NAME}] Loading v{MyPluginInfo.PLUGIN_VERSION}...");

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
    //  Configuration (STRICTLY ONLY THE 3 REQUESTED MECHANICS)
    // =====================================================================================
    internal class PluginConfig
    {
        public ConfigEntry<int> ExtraDrawsPerTurn { get; }
        public ConfigEntry<int> ExtraTotalDraws   { get; }
        public ConfigEntry<int> BonusHuSlots     { get; }

        internal PluginConfig(ConfigFile cfg)
        {
            const string cat = "Mechanics";

            ExtraDrawsPerTurn = cfg.Bind(cat, "ExtraDrawsPerTurn", 2,
                new ConfigDescription("Additional tiles drawn/swapped per turn action.",
                    new AcceptableValueRange<int>(0, 10)));

            ExtraTotalDraws = cfg.Bind(cat, "ExtraTotalDraws", 20,
                new ConfigDescription("Additional total draw/swap allowance for the round.",
                    new AcceptableValueRange<int>(0, 100)));

            BonusHuSlots = cfg.Bind(cat, "BonusHuSlots", 2,
                new ConfigDescription("Additional winning hand / Hu slots available to the player.",
                    new AcceptableValueRange<int>(0, 10)));
        }
    }

    // =====================================================================================
    //  MECHANIC 1: Draw Per Turn (ExtraDrawsPerTurn)
    // =====================================================================================

    /// <summary>
    /// Expands the allowed fetch capacity in the swap tray.
    /// </summary>
    [HarmonyPatch(typeof(PlayerSwapPaiMianContainer), "get_CanFetchNumber")]
    internal static class Patch_PlayerSwapPaiMianContainer_CanFetchNumber
    {
        [HarmonyPostfix]
        static void Postfix(ref int __result)
        {
            int bonus = Plugin.Cfg.ExtraDrawsPerTurn.Value;
            if (bonus > 0)
            {
                __result += bonus;
            }
        }
    }

    /// <summary>
    /// Boosts active CanFetchNumber when swap phase begins.
    /// </summary>
    [HarmonyPatch(typeof(PlayerSwapPaiMianContainer), nameof(PlayerSwapPaiMianContainer.StartSwap))]
    internal static class Patch_PlayerSwapPaiMianContainer_StartSwap
    {
        [HarmonyPostfix]
        static void Postfix(PlayerSwapPaiMianContainer __instance)
        {
            int bonus = Plugin.Cfg.ExtraDrawsPerTurn.Value;
            if (bonus > 0)
            {
                try
                {
                    __instance.CanFetchNumber += bonus;
                    Plugin.Log.LogInfo($"[PlayerSwapPaiMianContainer] StartSwap -> CanFetchNumber = {__instance.CanFetchNumber}");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"[PlayerSwapPaiMianContainer] StartSwap error: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Prevents selection limit check from blocking multi-tile selections up to the boosted limit.
    /// </summary>
    [HarmonyPatch(typeof(PlayerSwapPaiMianContainer), nameof(PlayerSwapPaiMianContainer.IsLimitExceeded))]
    internal static class Patch_PlayerSwapPaiMianContainer_IsLimitExceeded
    {
        [HarmonyPrefix]
        static bool Prefix(int count, ref bool __result)
        {
            int bonus = Plugin.Cfg.ExtraDrawsPerTurn.Value;
            if (bonus > 0 && count <= 1 + bonus)
            {
                __result = false;
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Increases tile fetch count in pipeline output when swap phase initiates.
    /// </summary>
    [HarmonyPatch(typeof(PlayerPipeline), nameof(PlayerPipeline.OnBeforeSwapStart))]
    internal static class Patch_PlayerPipeline_OnBeforeSwapStart
    {
        [HarmonyPostfix]
        static void Postfix(ref OnBeforeSwapStartOutput __result)
        {
            int bonus = Plugin.Cfg.ExtraDrawsPerTurn.Value;
            if (bonus > 0 && __result != null)
            {
                __result.AddSwapCount += bonus;
                Plugin.Log.LogInfo($"[PlayerPipeline] OnBeforeSwapStart -> AddSwapCount = {__result.AddSwapCount}");
            }
        }
    }

    /// <summary>
    /// Increases initial hand swap tile count in opening hand deal.
    /// </summary>
    [HarmonyPatch(typeof(PlayerHandPaiMianContainer), "get_InitSwapTotal")]
    internal static class Patch_PlayerHandPaiMianContainer_InitSwapTotal
    {
        [HarmonyPostfix]
        static void Postfix(ref int __result)
        {
            int bonus = Plugin.Cfg.ExtraDrawsPerTurn.Value;
            if (bonus > 0)
            {
                __result += bonus;
            }
        }
    }

    // =====================================================================================
    //  MECHANIC 2: Total Draws Per Round (ExtraTotalDraws)
    // =====================================================================================

    /// <summary>
    /// Boosts round swap counter initialization both prefix and postfix.
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
                Plugin.Log.LogInfo($"[PlayerSwapPaiLeftCount] InitTotalCount boosted: totalCount -> {totalCount}");
            }
        }

        [HarmonyPostfix]
        static void Postfix(PlayerSwapPaiLeftCount __instance)
        {
            int extra = Plugin.Cfg.ExtraTotalDraws.Value;
            if (extra <= 0) return;
            try
            {
                __instance.AddTotalCount(extra, false);
                __instance.AddLeftCount(extra, false);
                __instance.UpdateText(false);
                Plugin.Log.LogInfo($"[PlayerSwapPaiLeftCount] Boosted total draws (Total: {__instance._totalCount}, Left: {__instance.SwapPaiLeftCount})");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[PlayerSwapPaiLeftCount] InitTotalCount postfix error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Ensures UI left count getter always reflects the boosted total.
    /// </summary>
    [HarmonyPatch(typeof(PlayerSwapPaiLeftCount), "get_SwapPaiLeftCount")]
    internal static class Patch_PlayerSwapPaiLeftCount_SwapPaiLeftCount
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

    /// <summary>
    /// Ensures swap container left count getter reflects the boosted total.
    /// </summary>
    [HarmonyPatch(typeof(PlayerSwapPaiMianContainer), "get_SwapPaiLeftCount")]
    internal static class Patch_PlayerSwapPaiMianContainer_SwapPaiLeftCount
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

    /// <summary>
    /// Boosts initial round swap count queried by player pipeline.
    /// </summary>
    [HarmonyPatch(typeof(PlayerPipeline), nameof(PlayerPipeline.OnBeforeInitSwap))]
    internal static class Patch_PlayerPipeline_OnBeforeInitSwap
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
    //  MECHANIC 3: Hu Slots (BonusHuSlots)
    // =====================================================================================

    /// <summary>
    /// Dynamically expands physical Hu slots when a round starts.
    /// </summary>
    [HarmonyPatch(typeof(PlayerPipeline), nameof(PlayerPipeline.OnMaJiangRoundStart))]
    internal static class Patch_PlayerPipeline_OnMaJiangRoundStart
    {
        [HarmonyPostfix]
        static void Postfix(PlayerPipeline __instance)
        {
            int bonus = Plugin.Cfg.BonusHuSlots.Value;
            if (bonus <= 0) return;
            try
            {
                Plugin.Log.LogInfo($"[PlayerPipeline] OnMaJiangRoundStart: Expanding Hu slots by +{bonus}...");
                __instance.DynamicChangeSlotsCountDelta(bonus);
                Plugin.Log.LogInfo($"[PlayerPipeline] Hu slots expanded! Current count: {__instance.OnHuPaiSlotsCount()}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[PlayerPipeline] OnMaJiangRoundStart error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Boosts player pipeline query for Hu slot capacity.
    /// </summary>
    [HarmonyPatch(typeof(PlayerPipeline), nameof(PlayerPipeline.OnHuPaiSlotsCount))]
    internal static class Patch_PlayerPipeline_OnHuPaiSlotsCount
    {
        [HarmonyPostfix]
        static void Postfix(ref int __result)
        {
            int bonus = Plugin.Cfg.BonusHuSlots.Value;
            if (bonus > 0)
            {
                __result += bonus;
            }
        }
    }

    /// <summary>
    /// Ensures HuPaiSlots.GetTotalCount reflects the bonus slots.
    /// </summary>
    [HarmonyPatch(typeof(HuPaiSlots), nameof(HuPaiSlots.GetTotalCount))]
    internal static class Patch_HuPaiSlots_GetTotalCount
    {
        [HarmonyPostfix]
        static void Postfix(ref int __result)
        {
            int bonus = Plugin.Cfg.BonusHuSlots.Value;
            if (bonus > 0)
            {
                __result += bonus;
            }
        }
    }

    /// <summary>
    /// Ensures HuPaiSlots.GetLeftCount reflects the bonus slots.
    /// </summary>
    [HarmonyPatch(typeof(HuPaiSlots), nameof(HuPaiSlots.GetLeftCount))]
    internal static class Patch_HuPaiSlots_GetLeftCount
    {
        [HarmonyPostfix]
        static void Postfix(ref int __result)
        {
            int bonus = Plugin.Cfg.BonusHuSlots.Value;
            if (bonus > 0)
            {
                __result += bonus;
            }
        }
    }
}
