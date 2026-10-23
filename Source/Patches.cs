using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace ReplimatMech;

/// Кормушка принимает только то, что уже лежит в ней. Патч — на фильтр, не на Accepts: ваниль ищет склад по фильтру, а клетку проверяет
/// через Accepts; разойдутся — носильщики без конца берут и срывают «отнести на склад».
[HarmonyPatch(typeof(StorageSettings), nameof(StorageSettings.AllowedToAccept), typeof(Thing))]
static class Patch_StorageAccepts
{
    static void Postfix(StorageSettings __instance, Thing t, ref bool __result)
    {
        if (__result && __instance.owner is Building_RMFeeder f) __result = f.Holds(t);
    }
}

/// Пропускная способность механитора занята со старта печати меха.
[HarmonyPatch(typeof(Pawn_MechanitorTracker), nameof(Pawn_MechanitorTracker.UsedBandwidthFromGestation), MethodType.Getter)]
static class Patch_Bandwidth
{
    static void Postfix(Pawn_MechanitorTracker __instance, ref int __result) => __result += CompPrinter.Reserved(__instance.Pawn);
}

/// Мехлинк — как риплинк и биолинк: в скобках уровень (гестационные процессоры) и скорость печати механоидов, в подсказке — пунктами.
[HarmonyPatch(typeof(HediffWithComps), nameof(HediffWithComps.LabelInBrackets), MethodType.Getter)]
static class Patch_MechlinkLabel
{
    static void Postfix(HediffWithComps __instance, ref string __result)
    {
        if (__instance.def != HediffDefOf.MechlinkImplant || __instance.pawn == null) return;
        string b = Links.Bracket(__instance.pawn, Link.Mech);
        __result = __result.NullOrEmpty() ? b : __result + ", " + b;
    }
}

[HarmonyPatch(typeof(HediffWithComps), nameof(HediffWithComps.TipStringExtra), MethodType.Getter)]
static class Patch_MechlinkTip
{
    static void Postfix(HediffWithComps __instance, ref string __result)
    {
        if (__instance.def == HediffDefOf.MechlinkImplant && __instance.pawn != null)
            __result = Links.Tip(__result, RMUI.Tr("RM_LinkLevelTip", Links.Level(__instance.pawn, Link.Mech)), Links.SpeedTip(Links.Speed(__instance.pawn, Link.Mech)));
    }
}

/// Стоимость в баках — часть богатства колонии.
[HarmonyPatch(typeof(WealthWatcher), nameof(WealthWatcher.ForceRecount))]
static class Patch_Wealth
{
    static void Postfix(Map ___map, ref float ___wealthBuildings) =>
        ___wealthBuildings += ___map.listerBuildings.AllBuildingsColonistOfClass<Building_RMTank>().Sum(t => t.value);
}

/// Бак пометили на разборку — предупредить, сколько пропадёт.
[HarmonyPatch(typeof(Designator_Deconstruct), nameof(Designator_Deconstruct.DesignateThing))]
static class Patch_Deconstruct
{
    static void Postfix(Thing t)
    {
        if (t is Building_RMTank tank)
            Messages.Message(RMUI.Tr("RM_MsgTankDeconstruct", Fmt.Mass(tank.mass), Fmt.Silver(tank.value)), tank, MessageTypeDefOf.CautionInput, false);
    }
}

/// «Сканировать» — кнопкой на выделенной вещи, которой нет в шаблонах.
[HarmonyPatch(typeof(ReverseDesignatorDatabase), "InitDesignators")]
static class Patch_ReverseDesignators
{
    static void Postfix(List<Designator> ___desList) => ___desList.Add(new Designator_RMScan());
}

/// Биорепликация выключена в настройках — рецепта биолинка на станке нет.
[HarmonyPatch(typeof(RecipeDef), nameof(RecipeDef.AvailableNow), MethodType.Getter)]
static class Patch_RecipeAvailable
{
    static void Postfix(RecipeDef __instance, ref bool __result) =>
        __result = __result && (__instance.ProducedThingDef != RMDefOf.RM_Biolink || Rules.BioOn);
}

/// Подсказка детства «репликант» на карточке — одно описание (2026-10-22): без пустых строк, навыков и запретов работы ванили.
[HarmonyPatch(typeof(BackstoryDef), nameof(BackstoryDef.FullDescriptionFor))]
static class Patch_BackstoryDescription
{
    static void Postfix(BackstoryDef __instance, ref TaggedString __result)
    {
        if (__instance == RMDefOf.RM_Bioreplicated) __result = __instance.description;
    }
}

/// Рыночная стоимость напечатанного человека — не ниже стоимости шаблона до наценки (2026-10-22): ваниль ценит здоровье, возраст
/// и навыки, гены и черты — нет; раны, возраст и рост навыков дальше по ванили. В пояснении стата — строка.
[HarmonyPatch(typeof(StatWorker_MarketValue), nameof(StatWorker_MarketValue.GetValueUnfinalized))]
static class Patch_PawnValue
{
    public static float Floor(StatRequest req) =>
        req.Thing is Pawn p && p.health?.hediffSet.GetFirstHediffOfDef(RMDefOf.RM_Replicant) is Hediff_RMReplicant m ? m.value : 0f;

    static void Postfix(StatRequest req, ref float __result) => __result = Mathf.Max(__result, Floor(req));
}

[HarmonyPatch(typeof(StatWorker_MarketValue), nameof(StatWorker_MarketValue.GetExplanationUnfinalized))]
static class Patch_PawnValueExplanation
{
    static void Postfix(StatRequest req, ref string __result)
    {
        float v = Patch_PawnValue.Floor(req);
        if (v > 0f) __result = __result.TrimEndNewlines() + "\n" + RMUI.Tr("RM_ValueFloor", Fmt.Silver(v));
    }
}

/// Биорепликация выключена — биосканера и инкубатора нет в меню строительства.
[HarmonyPatch(typeof(Designator_Build), nameof(Designator_Build.Visible), MethodType.Getter)]
static class Patch_BuildVisible
{
    static void Postfix(Designator_Build __instance, ref bool __result) =>
        __result = __result && (Rules.BioOn || __instance.PlacingDef != RMDefOf.RM_Bioscanner && __instance.PlacingDef != RMDefOf.RM_Incubator);
}
