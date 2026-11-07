using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using MaJiang.Bag;
using MaJiang.PlayMaJiang;
using MaJiang.PlayMaJiang.Player;
using MaJiang.PlayMaJiang.Player.HuPai;

/*
 * =========================================================================================
 * DemonicMahjongMod — Focused Mod for Demonic Mahjong v2.70.0
 * Target: Unity 6000.3.21f1  |  BepInEx 6.0.0-be.793  |  TargetFramework net6.0
 *
 * FOCUSED MECHANICS:
 *  1. Draw per turn (ExtraDrawsPerTurn): Allows drawing/swapping more tiles in each action.
 *  2. Total draws   (ExtraTotalDraws / InfiniteDraws): Expands total draw/swap round budget.
 *  3. Hu slot       (BonusHuSlots): Expands the player's winning hand / Hu slots.
 *
 * CRITICAL TECHNICAL CONSTRAINTS FOR UNITY 6000.3.21f1:
 *  1. DO NOT call ClassInjector.RegisterTypeInIl2Cpp() — it crashes due to missing Class::Init.
 *  2. DO NOT use AddComponent<T>() or custom MonoBehaviour injection.
 *  3. BepInEx.cfg should have [Logging.Unity] UnityLogListening = false.
 *  4. Rely purely on BasePlugin and HarmonyLib hooks targeting concrete game classes.
 *  5. Apply patches individually to guarantee resilience against any single patch issue.
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

            // Patch classes individually with per-patch error isolation
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
        // ── 1. Draw Per Turn ─────────────────────────────────────────────────────────────
        public ConfigEntry<int> ExtraDrawsPerTurn { get; }

        // ── 2. Total Draws ───────────────────────────────────────────────────────────────
        public ConfigEntry<int>  ExtraTotalDraws { get; }
        public ConfigEntry<bool> InfiniteDraws   { get; }
        public ConfigEntry<int>  ExtraDeckTiles  { get; }

        // ── 3. Hu Slots ──────────────────────────────────────────────────────────────────
        public ConfigEntry<int> BonusHuSlots     { get; }

        internal PluginConfig(ConfigFile cfg)
        {
            const string catDrawPerTurn = "1_DrawPerTurn";
            const string catTotalDraws  = "2_TotalDraws";
            const string catHuSlots     = "3_HuSlots";

            // 1. Draw Per Turn
            ExtraDrawsPerTurn = cfg.Bind(catDrawPerTurn, "ExtraDrawsPerTurn", 2,
                new ConfigDescription("Additional tiles drawn/swapped per turn action (boosts CanFetchNumber and DiscardSwap limits).",
                    new AcceptableValueRange<int>(0, 10)));

            // 2. Total Draws
            ExtraTotalDraws = cfg.Bind(catTotalDraws, "ExtraTotalDraws", 5,
                new ConfigDescription("Additional total draw/swap allowance granted for the round (boosts PlayerSwapPaiLeftCount and round turn budget).",
                    new AcceptableValueRange<int>(0, 50)));

            InfiniteDraws = cfg.Bind(catTotalDraws, "InfiniteDraws", false,
                new ConfigDescription("If true, remaining draw/swap count is locked and never decreases when swapping or drawing tiles."));

            ExtraDeckTiles = cfg.Bind(catTotalDraws, "ExtraDeckTiles", 0,
                new ConfigDescription("Extra tile capacity added to the wall/deck container (AllPaiMianContainer._fullCount) to prevent running out of tiles (0 = default).",
                    new AcceptableValueRange<int>(0, 144)));

            // 3. Hu Slots
            BonusHuSlots = cfg.Bind(catHuSlots, "BonusHuSlots", 3,
                new ConfigDescription("Additional winning hand / Hu slots available to the player (dynamically instantiates new HuPaiSlots and updates pipeline capacity).",
                    new AcceptableValueRange<int>(0, 10)));
        }
    }

    // =====================================================================================
    //  SECTION 1: Draw Per Turn Patches
    // =====================================================================================

    /// <summary>
    /// Increases the number of tiles the player is allowed to fetch during swap/draw (PlayerSwapPaiMianContainer.get_CanFetchNumber).
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
    /// Ensures CanFetchNumber is immediately boosted when swap phase starts (PlayerSwapPaiMianContainer.StartSwap).
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
    /// Prevents selection limit check from blocking multi-tile selections up to the boosted limit (PlayerSwapPaiMianContainer.IsLimitExceeded).
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
                return false; // Skip original check, allow selection
            }
            return true;
        }
    }

    /// <summary>
    /// Expands DiscardSwap._limitCount when swap UI is opened/activated (DiscardSwap.set_IsActive).
    /// </summary>
    [HarmonyPatch(typeof(DiscardSwap), "set_IsActive")]
    internal static class Patch_DiscardSwap_SetIsActive
    {
        [HarmonyPostfix]
        static void Postfix(DiscardSwap __instance, bool value)
        {
            if (!value) return;
            int bonus = Plugin.Cfg.ExtraDrawsPerTurn.Value;
            if (bonus <= 0) return;
            try
            {
                __instance._limitCount += bonus;
                Plugin.Log.LogInfo($"[DiscardSwap] set_IsActive -> _limitCount = {__instance._limitCount}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[DiscardSwap] set_IsActive error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Expands DiscardSwap._limitCount dynamically on all overloads of DiscardSwap.Enable.
    /// </summary>
    [HarmonyPatch]
    internal static class Patch_DiscardSwap_Enable
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var m in AccessTools.GetDeclaredMethods(typeof(DiscardSwap)))
            {
                if (m.Name == "Enable")
                    yield return m;
            }
        }

        [HarmonyPostfix]
        static void Postfix(DiscardSwap __instance)
        {
            int bonus = Plugin.Cfg.ExtraDrawsPerTurn.Value;
            if (bonus <= 0) return;
            try
            {
                __instance._limitCount += bonus;
                Plugin.Log.LogInfo($"[DiscardSwap] Enable -> _limitCount = {__instance._limitCount}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[DiscardSwap] Enable error: {ex.Message}");
            }
        }
    }

    // =====================================================================================
    //  SECTION 2: Total Draws / Swaps Per Round Patches
    // =====================================================================================

    /// <summary>
    /// Increases total draw allowance when the round swap counter is initialized (PlayerSwapPaiLeftCount.InitTotalCount).
    /// </summary>
    [HarmonyPatch(typeof(PlayerSwapPaiLeftCount), nameof(PlayerSwapPaiLeftCount.InitTotalCount))]
    internal static class Patch_PlayerSwapPaiLeftCount_InitTotalCount
    {
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
                Plugin.Log.LogInfo($"[PlayerSwapPaiLeftCount] Boosted total draws by +{extra} (Total: {__instance._totalCount}, Left: {__instance.SwapPaiLeftCount})");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[PlayerSwapPaiLeftCount] InitTotalCount error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Ensures draw counter remains topped off when swap counter component is re-enabled (PlayerSwapPaiLeftCount.OnEnable).
    /// </summary>
    [HarmonyPatch(typeof(PlayerSwapPaiLeftCount), nameof(PlayerSwapPaiLeftCount.OnEnable))]
    internal static class Patch_PlayerSwapPaiLeftCount_OnEnable
    {
        [HarmonyPostfix]
        static void Postfix(PlayerSwapPaiLeftCount __instance)
        {
            int extra = Plugin.Cfg.ExtraTotalDraws.Value;
            if (extra <= 0) return;
            try
            {
                if (__instance._totalCount > 0 && __instance.SwapPaiLeftCount < __instance._totalCount)
                {
                    __instance.SwapPaiLeftCount = __instance._totalCount;
                    __instance.UpdateText(false);
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Freezes remaining draw/swap count if InfiniteDraws is toggled on (PlayerSwapPaiLeftCount.AddLeftCount).
    /// </summary>
    [HarmonyPatch(typeof(PlayerSwapPaiLeftCount), nameof(PlayerSwapPaiLeftCount.AddLeftCount))]
    internal static class Patch_PlayerSwapPaiLeftCount_AddLeftCount
    {
        [HarmonyPrefix]
        static bool Prefix(PlayerSwapPaiLeftCount __instance, int leftCount)
        {
            if (Plugin.Cfg.InfiniteDraws.Value && leftCount < 0)
            {
                __instance.SwapPaiLeftCount = __instance._totalCount;
                __instance.UpdateText(false);
                Plugin.Log.LogDebug("[PlayerSwapPaiLeftCount] Blocked draw/swap decrement (InfiniteDraws active)");
                return false; // Skip decrement
            }
            return true;
        }
    }

    /// <summary>
    /// Boosts initial round swap count queried by player pipeline (PlayerPipeline.OnBeforeInitSwap).
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
                Plugin.Log.LogDebug($"[PlayerPipeline] OnBeforeInitSwap boosted -> {__result}");
            }
        }
    }

    /// <summary>
    /// Extends remaining turn count in round statistics (InRoundStatistics.GetLeftTurnCount).
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

    /// <summary>
    /// Expands the tile wall / deck capacity on initialization (AllPaiMianContainer.Init).
    /// </summary>
    [HarmonyPatch(typeof(AllPaiMianContainer), nameof(AllPaiMianContainer.Init))]
    internal static class Patch_AllPaiMianContainer_Init
    {
        [HarmonyPostfix]
        static void Postfix(AllPaiMianContainer __instance)
        {
            int extra = Plugin.Cfg.ExtraDeckTiles.Value;
            if (extra <= 0) return;
            try
            {
                __instance._fullCount += extra;
                Plugin.Log.LogInfo($"[AllPaiMianContainer] Deck capacity boosted: _fullCount -> {__instance._fullCount}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[AllPaiMianContainer] Init error: {ex.Message}");
            }
        }
    }

    // =====================================================================================
    //  SECTION 3: Hu Slot Patches
    // =====================================================================================

    /// <summary>
    /// Dynamically instantiates additional Hu slots and updates CaoWei UI right after initialization (HuPaiSlots.OnInitFinished).
    /// </summary>
    [HarmonyPatch(typeof(HuPaiSlots), nameof(HuPaiSlots.OnInitFinished))]
    internal static class Patch_HuPaiSlots_OnInitFinished
    {
        [HarmonyPostfix]
        static void Postfix(HuPaiSlots __instance)
        {
            int bonus = Plugin.Cfg.BonusHuSlots.Value;
            if (bonus <= 0) return;
            try
            {
                Plugin.Log.LogInfo($"[HuPaiSlots] Calling DynamicChangeSlotsCountDelta(+{bonus})...");
                __instance.DynamicChangeSlotsCountDelta(bonus);
                Plugin.Log.LogInfo($"[HuPaiSlots] Hu slots successfully expanded! Total slots: {__instance.GetTotalCount()}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[HuPaiSlots] Failed to expand Hu slots: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Boosts player pipeline query for Hu slot capacity (PlayerPipeline.OnHuPaiSlotsCount).
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
                Plugin.Log.LogDebug($"[PlayerPipeline] OnHuPaiSlotsCount boosted -> {__result}");
            }
        }
    }

    // =====================================================================================
    //  SECTION 4: UI & Character Stat Display Synchronization (Bag / PlayerInfoCtr)
    // =====================================================================================

    /// <summary>
    /// Synchronizes the character bag / status inspect screen with the boosted draw and slot values (PlayerInfoCtr.Refresh).
    /// </summary>
    [HarmonyPatch(typeof(PlayerInfoCtr), nameof(PlayerInfoCtr.Refresh))]
    internal static class Patch_PlayerInfoCtr_Refresh
    {
        [HarmonyPostfix]
        static void Postfix(PlayerInfoCtr __instance)
        {
            try
            {
                int extraDraws = Plugin.Cfg.ExtraDrawsPerTurn.Value;
                if (extraDraws > 0)
                {
                    __instance._moPaiNum += extraDraws;
                }

                int extraTotal = Plugin.Cfg.ExtraTotalDraws.Value;
                if (extraTotal > 0)
                {
                    __instance._totalPaiNum += extraTotal;
                }

                int bonusSlots = Plugin.Cfg.BonusHuSlots.Value;
                if (bonusSlots > 0)
                {
                    __instance._slotsCount += bonusSlots;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[PlayerInfoCtr] Refresh UI error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Synchronizes the character inspect screen in out-game / shop view (PlayerInfoCtr.RefreshOutGame).
    /// </summary>
    [HarmonyPatch(typeof(PlayerInfoCtr), nameof(PlayerInfoCtr.RefreshOutGame))]
    internal static class Patch_PlayerInfoCtr_RefreshOutGame
    {
        [HarmonyPostfix]
        static void Postfix(PlayerInfoCtr __instance)
        {
            try
            {
                int extraDraws = Plugin.Cfg.ExtraDrawsPerTurn.Value;
                if (extraDraws > 0)
                {
                    __instance._moPaiNum += extraDraws;
                }

                int extraTotal = Plugin.Cfg.ExtraTotalDraws.Value;
                if (extraTotal > 0)
                {
                    __instance._totalPaiNum += extraTotal;
                }

                int bonusSlots = Plugin.Cfg.BonusHuSlots.Value;
                if (bonusSlots > 0)
                {
                    __instance._slotsCount += bonusSlots;
                }
            }
            catch { }
        }
    }
}
