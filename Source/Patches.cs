using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace ReplimatMech;

/// Поверх фильтра хранилища: расщепитель — очередь и «Нет баков», кормушка — только то, что уже в ней.
[HarmonyPatch(typeof(Building_Storage), nameof(Building_Storage.Accepts))]
static class Patch_StorageAccepts
{
    static void Postfix(Building_Storage __instance, Thing t, ref bool __result)
    {
        if (!__result) return;
        if (__instance is Building_RMHopper h) __result = h.Allows(t);
        else if (__instance is Building_RMFeeder f) __result = f.Holds(t);
    }
}

/// Галку в фильтре расщепителя на то, что стоит в очереди, не поставить.
[HarmonyPatch(typeof(ThingFilter), nameof(ThingFilter.SetAllow), typeof(ThingDef), typeof(bool))]
static class Patch_FilterSetAllow
{
    static int lastFrame = -1;

    static bool Prefix(ThingFilter __instance, ThingDef thingDef, bool allow)
    {
        if (!allow || !Building_RMHopper.Spawned_.Any(h => h.settings?.filter == __instance) || CompPrinter.QueuedIn(thingDef) is not { } printer)
            return true;
        if (Time.frameCount != lastFrame)
            Messages.Message("RM_MsgHopperBlocked".Translate(thingDef.label, printer.RenamableLabel), MessageTypeDefOf.RejectInput, false);
        lastFrame = Time.frameCount;
        return false;
    }
}

/// Пропускная способность механитора занята со старта печати меха.
[HarmonyPatch(typeof(Pawn_MechanitorTracker), nameof(Pawn_MechanitorTracker.UsedBandwidthFromGestation), MethodType.Getter)]
static class Patch_Bandwidth
{
    static void Postfix(Pawn_MechanitorTracker __instance, ref int __result) => __result += CompPrinter.Reserved(__instance.Pawn);
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

/// С компьютером — только через консоль связи (как вызов дьявола).
[HarmonyPatch(typeof(Building_CommsConsole), nameof(Building_CommsConsole.GetFloatMenuOptions))]
static class Patch_CommsConsole
{
    static IEnumerable<FloatMenuOption> Postfix(IEnumerable<FloatMenuOption> values, Building_CommsConsole __instance, Pawn myPawn)
    {
        foreach (FloatMenuOption o in values) yield return o;
        if (!__instance.CanUseCommsNow) yield break;
        foreach (FloatMenuOption o in Building_RMComputer.ConsoleOptions(__instance, myPawn)) yield return o;
    }
}
