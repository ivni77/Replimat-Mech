using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace ReplimatMech;

/// Цена печати за штуку: время (тики при скорости 100%), стоимость (серебро), масса (кг).
public static class Pricing
{
    const float WorkPerSilver = 14f, MechMassPerBodySize = 60f;
    static readonly Dictionary<ThingDef, RecipeDef> gestation = new();
    static readonly Dictionary<(ThingDef, ThingDef), float> workCache = new();
    static readonly Dictionary<ThingDef, RecipeDef> fixedRecipe = new();

    public static RecipeDef Gestation(ThingDef race)
    {
        if (!gestation.TryGetValue(race, out RecipeDef r))
            gestation[race] = r = DefDatabase<RecipeDef>.AllDefs.FirstOrDefault(x => x.gestationCycles > 0 && x.ProducedThingDef == race);
        return r;
    }

    /// Ванильная работа на штуку при скорости 1,0: явный рецепт (работа / сколько штук даёт, пеммикан — 16 за раз),
    /// иначе «работа изготовления», иначе по стоимости, как у плавильни.
    public static float Work(ThingDef def, ThingDef stuff)
    {
        if (workCache.TryGetValue((def, stuff), out float w)) return w;
        RecipeDef r = def.category == ThingCategory.Building ? null
            : DefDatabase<RecipeDef>.AllDefs.FirstOrDefault(x => x.products.Count == 1 && x.products[0].thingDef == def && x.workAmount > 0f);
        if (def.category == ThingCategory.Building) w = def.GetStatValueAbstract(StatDefOf.WorkToBuild, stuff);
        else if (r != null) w = r.workAmount / r.products[0].count;
        else if (def.statBases?.StatListContains(StatDefOf.WorkToMake) == true) w = def.GetStatValueAbstract(StatDefOf.WorkToMake, stuff);
        else w = def.GetStatValueAbstract(StatDefOf.MarketValue, stuff) * WorkPerSilver;
        return workCache[(def, stuff)] = w;
    }

    static IEnumerable<(ThingDef def, int count)> MechMaterials(ThingDef race)
    {
        RecipeDef r = Gestation(race);
        if (r == null) yield break;
        foreach (IngredientCount ing in r.ingredients)
            if (ing.filter.AnyAllowedDef is { } d) yield return (d, Mathf.RoundToInt(ing.GetBaseCount()));
    }

    /// Мех = его материалы по цене их печати (наценка только на чипы и субъядра).
    public static float Value(Pattern p, QualityCategory q)
    {
        if (!p.IsMech) return Rules.NoValue(p.def) ? 0f : p.MarketValue(q) * Rules.Markup(p.def);
        return MechMaterials(p.def).Sum(m => m.count * m.def.BaseMarketValue * Rules.Markup(m.def));
    }

    public static float Mass(Pattern p) =>
        p.IsMech ? MechMassPerBodySize * p.def.race.baseBodySize : p.def.GetStatValueAbstract(StatDefOf.Mass, p.stuff);

    /// Время штуки в тиках при скорости печати 100%. Механоид: материалы + циклы гестации.
    public static float Ticks(Pattern p)
    {
        if (!p.IsMech) return Work(p.def, p.stuff);
        RecipeDef r = Gestation(p.def);
        return MechMaterials(p.def).Sum(m => m.count * Work(m.def, null)) + r.gestationCycles * (r.formingTicks + r.workAmount);
    }

    /// Скорость печати — скорость гестации механитора (ремесло + гестационные процессоры); игра без механитора или его нет — 100%.
    public static float Speed(Pawn editor) => ReplimatMechMod.S.needMechanitor && editor != null ? editor.GetStatValue(RMDefOf.MechFormingSpeed) : 1f;

    public static float Bandwidth(Pattern p) => p.def.GetStatValueAbstract(StatDefOf.BandwidthCost);

    /// Материалы вещи: costList + материал, иначе фиксированные ингредиенты рецепта, иначе сама вещь. Без работы и качества.
    static float MaterialsValue(Thing inner)
    {
        List<ThingDefCountClass> cost = inner.CostListAdjusted();
        if (cost.Count > 0) return cost.Sum(c => c.count * c.thingDef.BaseMarketValue);
        if (!fixedRecipe.TryGetValue(inner.def, out RecipeDef r))
            fixedRecipe[inner.def] = r = DefDatabase<RecipeDef>.AllDefs.FirstOrDefault(x => x.products.Count == 1 && x.products[0].thingDef == inner.def
                                                                                   && x.ingredients.Count > 0 && x.ingredients.All(i => i.IsFixedIngredient));
        if (r != null) return r.ingredients.Sum(i => i.GetBaseCount() * i.FixedIngredient.BaseMarketValue) / r.products[0].count;
        return inner.def.GetStatValueAbstract(StatDefOf.MarketValue, inner.Stuff);
    }

    /// Разборка одной штуки (до процента возврата): меньшее из материалов и рыночной цены.
    public static float DematValue(Thing t) => Rules.NoValue(t.GetInnerIfMinified().def) ? 0f : Mathf.Min(MaterialsValue(t.GetInnerIfMinified()), t.MarketValue);
}

public static class Demat
{
    static Settings S => ReplimatMechMod.S;

    /// Вещь: масса 100%, стоимость 50% от материалов, шаблон. Сама вещь исчезает.
    public static void Item(Thing t, PowerNet net)
    {
        GameComponent_RM.Get.Record(t);
        // без стоимости (каменные блоки) — масса как у самой дешёвой печати, см. Order.Cap
        float mass = t.GetStatValue(StatDefOf.Mass) * t.stackCount * (Rules.NoValue(t.GetInnerIfMinified().def) ? 1f - Order.Cap / 100f : 1f);
        float value = Pricing.DematValue(t) * t.stackCount;
        float tankValue = t.GetInnerIfMinified() is Building_RMTank tank ? tank.value : 0f;
        Tanks.Add(net, mass * S.massReturn / 100f, (Tanks.CollectsValue(net) ? value * S.valueReturn / 100f : 0f) + tankValue);
        t.Destroy();
    }

    /// Труп как кремация: вещи на нём — каждая как отдельная вещь; свежий — мясо и кожа по правилу 50%; мех — продукты разборки и шаблон.
    public static void Corpse(Corpse corpse, PowerNet net)
    {
        Pawn pawn = corpse.InnerPawn;
        var gear = new List<Thing>();
        if (pawn.apparel != null) gear.AddRange(pawn.apparel.WornApparel);
        if (pawn.equipment != null) gear.AddRange(pawn.equipment.AllEquipmentListForReading);
        if (pawn.inventory != null) gear.AddRange(pawn.inventory.innerContainer);
        foreach (Thing g in gear) Item(g, net);

        GameComponent_RM.Get.Record(corpse);
        float value = 0f;
        if (pawn.RaceProps.IsMechanoid || corpse.GetRotStage() == RotStage.Fresh)
            value = pawn.ButcherProducts(null, 1f).Sum(p => Pricing.DematValue(p) * p.stackCount);
        Tanks.Add(net, corpse.GetStatValue(StatDefOf.Mass) * S.massReturn / 100f, Tanks.CollectsValue(net) ? value * S.valueReturn / 100f : 0f);
        corpse.Destroy();
    }
}

/// Баки электросети. Печать — только из запитанных. Масса и стоимость — по объёму: большой бак берёт вчетверо больше малого.
public static class Tanks
{
    public static IEnumerable<T> On<T>(PowerNet net) where T : Thing =>
        net == null ? Enumerable.Empty<T>() : net.powerComps.Select(c => c.parent).OfType<T>();

    public static List<Building_RMTank> All(PowerNet net) => On<Building_RMTank>(net).ToList();

    public static List<Building_RMTank> Powered(PowerNet net) => On<Building_RMTank>(net).Where(t => t.Powered).ToList();

    public static float Mass(PowerNet net) => Powered(net).Sum(t => t.mass);
    public static float Value(PowerNet net) => Powered(net).Sum(t => t.value);
    public static float Capacity(PowerNet net) => Powered(net).Sum(t => t.Capacity);

    public static bool HasComputer(PowerNet net) => On<Building_RMComputer>(net).Any(c => c.Powered);

    /// Компьютер сети может выключить сбор стоимости: разборка тогда даёт только массу (утилизация без роста богатства).
    public static bool CollectsValue(PowerNet net) => !On<Building_RMComputer>(net).Any(c => !c.collectValue);

    /// Масса сверх объёма пропадает, стоимость добавляется всегда.
    public static void Add(PowerNet net, float mass, float value)
    {
        List<Building_RMTank> list = Powered(net);
        if (list.Count == 0) list = All(net);
        if (list.Count == 0) return;
        float free = list.Sum(t => t.Capacity - t.mass), cap = list.Sum(t => t.Capacity);
        mass = Mathf.Min(mass, Mathf.Max(0f, free));
        foreach (Building_RMTank t in list)
        {
            if (free > 0f) t.mass += mass * (t.Capacity - t.mass) / free;
            t.value += value * t.Capacity / cap;
        }
        Rebalance(list);
    }

    public static bool CanTake(PowerNet net, float mass, float value, out bool massShort)
    {
        massShort = Mass(net) < mass;
        return !massShort && Value(net) >= value;
    }

    public static bool TryTake(PowerNet net, float mass, float value)
    {
        if (!CanTake(net, mass, value, out _)) return false;
        List<Building_RMTank> list = Powered(net);
        float m = list.Sum(t => t.mass), v = list.Sum(t => t.value);
        foreach (Building_RMTank t in list)
        {
            if (m > 0f) t.mass -= mass * t.mass / m;
            if (v > 0f) t.value -= value * t.value / v;
        }
        Rebalance(list);
        return true;
    }

    public static void Rebalance(List<Building_RMTank> list)
    {
        if (list.Count == 0) return;
        float m = list.Sum(t => t.mass), v = list.Sum(t => t.value), cap = list.Sum(t => t.Capacity);
        foreach (Building_RMTank t in list)
        {
            t.mass = cap > 0f ? Mathf.Min(t.Capacity, m * t.Capacity / cap) : 0f;
            t.value = cap > 0f ? v * t.Capacity / cap : 0f;
        }
    }
}

public class Building_RMTank : Building
{
    public float mass, value;
    CompPowerTrader power;

    public bool Powered => power?.PowerOn == true;
    public float Capacity => ReplimatMechMod.S.tankCapacity * (def == RMDefOf.RM_TankLarge ? 4f : 1f);

    public override void SpawnSetup(Map map, bool respawningAfterLoad)
    {
        base.SpawnSetup(map, respawningAfterLoad);
        power = GetComp<CompPowerTrader>();
    }

    protected override void Tick()
    {
        base.Tick();
        if (this.IsHashIntervalTick(250) && Powered) Tanks.Rebalance(Tanks.Powered(power.PowerNet));
    }

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Values.Look(ref mass, "mass");
        Scribe_Values.Look(ref value, "value");
    }

    public override string GetInspectString()
    {
        var sb = new StringBuilder();
        sb.AppendLine("RM_MassLine".Translate(Fmt.MassNum(mass), Fmt.Mass(Capacity)));
        if (value > 0f) sb.AppendLine("RM_ValueLine".Translate(Fmt.Silver(value)));
        List<Building_RMTank> net = Spawned ? Tanks.All(power?.PowerNet) : new List<Building_RMTank>();
        if (net.Count > 1) sb.AppendLine("RM_TankShare".Translate((Capacity / net.Sum(t => t.Capacity)).ToStringPercent()));
        sb.Append(base.GetInspectString());
        return sb.ToString().TrimEndNewlines();
    }
}
