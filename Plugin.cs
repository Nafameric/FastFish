  // ───────────────────────────────────────────────────────────────
// FastFish – faster bites + reward multipliers
// Requires: BepInEx 5.x (or 6.x) + HarmonyX 2.x
// Target   : netstandard2.1 | C# 10
// ───────────────────────────────────────────────────────────────
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;
using Tableflip;

namespace FastFish;

[BepInPlugin("fastfish", "Fast Fish", "1.3.0")]
public class Plugin : BaseUnityPlugin
{
    

    // ──────────────────────────────────  config
    internal static ConfigEntry<float> BiteChanceMultiplier = null!;
    internal static ConfigEntry<float> BiteTimeMultiplier = null!;   // NEW – ontrols nibble→hook window
    internal static ConfigEntry<float> FishQualityMultiplier = null!;
    internal static ConfigEntry<float> MagicChanceMultiplier = null!;
    internal static ConfigEntry<float> SocketChanceMultiplier = null!;
    internal static ConfigEntry<float> XpMultiplier = null!;
    internal static ConfigEntry<float> GoldMultiplier = null!;
    internal static ConfigEntry<float> FameMultiplier = null!;
    

    // ──────────────────────────────────  logger
    internal static new ManualLogSource Logger = null!;
    internal static int ScaledChance(int baseChance, float multiplier)
    => Mathf.Clamp(Mathf.RoundToInt(baseChance * multiplier), 0, 100);

    private void Awake()
    {
        Logger = base.Logger;

        BiteChanceMultiplier = Config.Bind(
            "Fishing", "BiteChanceMultiplier",
            5f,
            "Multiplies the base 32/20000 bite roll that happens every FixedUpdate. " +
            "Avrage wait = 25s ÷ this value (e.g. 10 ⇒ =2.5s  \n");
            
        BiteTimeMultiplier = Config.Bind(
            "Fishing", "BiteTimeMultiplier",
            0.5f,
            "Scales the delay between nibble and hook.\n" +
            "0.5 = half the wait, 0.25 = quarter, 2 = twice as long. Leave at 1 for vanilla timing.");

        MagicChanceMultiplier = Config.Bind(
            "Fishing-Loot", "MagicChanceMultiplier", 1f,
            "Scales the 20 % roll that decides whether an item is given magic/legendary powers.\n" +
            "2 = 40 %  |  5 = 100 %  |  0.5 = 10 %");

        SocketChanceMultiplier = Config.Bind(
            "Fishing-Loot", "SocketChanceMultiplier", 1f,
            "Scales the 10 % roll that decides whether a weapon/armour piece spawns with sockets.");
        
        FishQualityMultiplier = Config.Bind(
            "Fishing-Loot", "FishQualityMultiplier", 1f,
            "Scales the 20 % ‘bestow-powers’ roll on fishing loot.\n" +
            "1 = vanilla, 2 = 40 %, 5 = guaranteed legendary.");


        XpMultiplier = Config.Bind(
            "Rewards", "XpMultiplier", 1f,
            "Multiply all experience gains.");

        GoldMultiplier = Config.Bind(
            "Rewards", "GoldMultiplier", 1f,
            "Multiply all gold picked up or rewarded.");

        FameMultiplier = Config.Bind(
            "Rewards", "FameMultiplier", 1f,
            "Multiply all fame (renown) gains.");

        new Harmony("fastfish").PatchAll();
        Logger.LogInfo("Fast Fish v1.3.0 initialised!");
    }
}

// ═══════════════════════════════════════════════════════════════
//  FISHING patches – bite chance  & bite‑time window
// ═══════════════════════════════════════════════════════════════
[HarmonyPatch(typeof(FishingSpotInteractable), nameof(FishingSpotInteractable.FishingUpdate))]
static class Patch_Fishing
{
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        /*
         * Two targets inside the coroutine:
         *   1) Random.Range(0, 20000) < 32     → multiply 32 by BiteChanceMultiplier
         *   2) biteSpeed = Random.Range(0.2f, 0.5f)  → divide result by BiteTimeMultiplier
         */

        var list = new List<CodeInstruction>(instructions);
        var rngFlt = AccessTools.Method(typeof(Random), nameof(Random.Range), new[] { typeof(float), typeof(float) });

        for (int i = 0; i < list.Count; i++)
        {
            // ------ 1) widen bite CHANCE threshold (constant 32) ------
            if (list[i].opcode == OpCodes.Ldc_I4_S && (sbyte)list[i].operand == 32)
            {
                // original 32 already on stack – now multiply by cfg
                list.Insert(i + 1, new CodeInstruction(OpCodes.Ldsfld,
                        AccessTools.Field(typeof(Plugin), nameof(Plugin.BiteChanceMultiplier))));
                list.Insert(i + 2, new CodeInstruction(OpCodes.Callvirt,
                        AccessTools.PropertyGetter(typeof(ConfigEntry<float>), "Value")));
                list.Insert(i + 3, new CodeInstruction(OpCodes.Conv_I4));
                list.Insert(i + 4, new CodeInstruction(OpCodes.Mul));
                i += 4;
                continue;
            }

            // ------ 2) shorten / lengthen nibble–hook WINDOW ------
            if (list[i].opcode == OpCodes.Call && list[i].operand is MethodInfo mi && mi == rngFlt)
            {
                // Stack: [result]
                list.Insert(i + 1, new CodeInstruction(OpCodes.Ldsfld,
                        AccessTools.Field(typeof(Plugin), nameof(Plugin.BiteTimeMultiplier))));
                list.Insert(i + 2, new CodeInstruction(OpCodes.Callvirt,
                        AccessTools.PropertyGetter(typeof(ConfigEntry<float>), "Value")));
                list.Insert(i + 3, new CodeInstruction(OpCodes.Div)); // result / multiplier
                i += 3;
            }
        }
        return list;
    }
}

// ═══════════════════════════════════════════════════════════════
//  REWARD PATCHES
// ═══════════════════════════════════════════════════════════════
[HarmonyPatch(typeof(Character), nameof(Character.AwardExperience))]
class Patch_XP
{
    static void Prefix(ref int award)
    {
        if (!Plugin.XpMultiplier.Value.Equals(1f))
            award = Mathf.RoundToInt(award * Plugin.XpMultiplier.Value);
    }
}

[HarmonyPatch(typeof(GoldPileTrigger), nameof(GoldPileTrigger.Init))]
class Patch_Gold
{
    static void Prefix(ref int totalGold)
    {
        if (!Plugin.GoldMultiplier.Value.Equals(1f))
            totalGold = Mathf.RoundToInt(totalGold * Plugin.GoldMultiplier.Value);
    }
}

[HarmonyPatch(typeof(Character), nameof(Character.AwardFame))]
class Patch_Fame
{
    static void Prefix(ref int fame)
    {
        if (!Plugin.FameMultiplier.Value.Equals(1f))
            fame = Mathf.RoundToInt(fame * Plugin.FameMultiplier.Value);
    }
}
//  ════════════════════════════════════════════════════════════════
//  FISH-LOOT – transpile GiveAward so the 20 & 10 % rolls respect
//              MagicChanceMultiplier  /  SocketChanceMultiplier
//  ════════════════════════════════════════════════════════════════
[HarmonyPatch(typeof(FishingSpotInteractable), nameof(FishingSpotInteractable.GiveAward))]
static class Patch_FishLoot
{
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        bool multiplierNeedsQualityPatch = true;

        foreach (var ins in instructions)
        {
            // first roll: Random.Range(0,100) < 20   (magic / legendary)
            if (ins.opcode == OpCodes.Ldc_I4_S && (sbyte)ins.operand == 20)
            {
                // push (int)(20 * MagicChanceMultiplier)
                yield return new CodeInstruction(OpCodes.Ldc_I4_S, (sbyte)20);
                yield return new CodeInstruction(OpCodes.Ldsfld,
                    AccessTools.Field(typeof(Plugin), nameof(Plugin.MagicChanceMultiplier)));
                yield return new CodeInstruction(OpCodes.Callvirt,
                    AccessTools.PropertyGetter(typeof(ConfigEntry<float>), "Value"));
                yield return new CodeInstruction(OpCodes.Call,
                    AccessTools.Method(typeof(Plugin), nameof(Plugin.ScaledChance)));
                continue;                          // skip the original 20 literal
            }

            // second roll: Random.Range(0,100) < 10   (sockets)
            if (ins.opcode == OpCodes.Ldc_I4_S && (sbyte)ins.operand == 10)
            {
                yield return new CodeInstruction(OpCodes.Ldc_I4_S, (sbyte)10);
                yield return new CodeInstruction(OpCodes.Ldsfld,
                    AccessTools.Field(typeof(Plugin), nameof(Plugin.SocketChanceMultiplier)));
                yield return new CodeInstruction(OpCodes.Callvirt,
                    AccessTools.PropertyGetter(typeof(ConfigEntry<float>), "Value"));
                yield return new CodeInstruction(OpCodes.Call,
                    AccessTools.Method(typeof(Plugin), nameof(Plugin.ScaledChance)));
                continue;
            }
            
            if (ins.opcode == OpCodes.Ldc_I4_S && (sbyte)ins.operand == 20)
            {
                // (this block is already there for MagicChanceMultiplier)
                yield return new CodeInstruction(OpCodes.Ldc_I4_S, (sbyte)20);
                yield return new CodeInstruction(OpCodes.Ldsfld,
                    AccessTools.Field(typeof(Plugin), nameof(Plugin.MagicChanceMultiplier)));
                yield return new CodeInstruction(OpCodes.Callvirt,
                    AccessTools.PropertyGetter(typeof(ConfigEntry<float>), "Value"));
                yield return new CodeInstruction(OpCodes.Call,
                    AccessTools.Method(typeof(Plugin), nameof(Plugin.ScaledChance)));
                continue;
            }

            if (ins.opcode == OpCodes.Ldc_I4_S && (sbyte)ins.operand == 20 &&
                multiplierNeedsQualityPatch)            // (tiny guard so we hit the 2nd 20)
            {
                yield return new CodeInstruction(OpCodes.Ldc_I4_S, (sbyte)20);
                yield return new CodeInstruction(OpCodes.Ldsfld,
                    AccessTools.Field(typeof(Plugin), nameof(Plugin.FishQualityMultiplier)));
                yield return new CodeInstruction(OpCodes.Callvirt,
                    AccessTools.PropertyGetter(typeof(ConfigEntry<float>), "Value"));
                yield return new CodeInstruction(OpCodes.Call,
                    AccessTools.Method(typeof(Plugin), nameof(Plugin.ScaledChance)));
                multiplierNeedsQualityPatch = false;    // only replace once
                continue;
            }
            // everything else stays untouched
            yield return ins;
        }
    }
}
