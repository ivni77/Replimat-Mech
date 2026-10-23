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
    const float WorkPerSilver = 14f, MassPerBodySize = 60f;
    static readonly Dictionary<ThingDef, RecipeDef> gestation = new();
    static readonly Dictionary<(ThingDef, ThingDef), float> workCache = new();
    static readonly Dictionary<ThingDef, float?> sourceCache = new();

    public static RecipeDef Gestation(ThingDef race)
    {
        if (!gestation.TryGetValue(race, out RecipeDef r))
            gestation[race] = r = DefDatabase<RecipeDef>.AllDefs.FirstOrDefault(x => x.gestationCycles > 0 && x.ProducedThingDef == race);
        return r;
    }

    /// Ванильная работа на штуку при скорости 1,0: явный рецепт (работа / сколько штук даёт, пеммикан — 16 за раз),
    /// иначе «работа изготовления» (и у построек: скульптуры делают на верстаке), иначе «работа строительства», иначе по стоимости, как у плавильни.
    public static float Work(ThingDef def, ThingDef stuff)
    {
        if (workCache.TryGetValue((def, stuff), out float w)) return w;
        RecipeDef r = def.category == ThingCategory.Building ? null
            : DefDatabase<RecipeDef>.AllDefs.FirstOrDefault(x => x.products.Count == 1 && x.products[0].thingDef == def && x.workAmount > 0f);
        if (r != null) w = r.workAmount / r.products[0].count;
        else if (def.statBases?.StatListContains(StatDefOf.WorkToMake) == true) w = def.GetStatValueAbstract(StatDefOf.WorkToMake, stuff);
        else if (def.category == ThingCategory.Building) w = def.GetStatValueAbstract(StatDefOf.WorkToBuild, stuff);
        else w = def.GetStatValueAbstract(StatDefOf.MarketValue, stuff) * WorkPerSilver;
        return workCache[(def, stuff)] = w;
    }

    static List<RecipeDef> resurrect;

    /// Рецепты воскрешения ванили по весу механоида, от самого дешёвого (лёгкий).
    public static List<RecipeDef> ResurrectRecipes => resurrect ??= DefDatabase<RecipeDef>.AllDefs.Where(r => r.mechResurrection)
        .OrderBy(r => Fixed(r).Sum(m => m.count * m.def.BaseMarketValue)).ToList();

    /// Труп механоида колонии, которого ваниль умеет воскрешать.
    public static bool Resurrectable(Corpse c) => c.InnerPawn is { } p && p.RaceProps.IsMechanoid && p.Faction == Faction.OfPlayer && ResurrectRecipes.Any(r => r.fixedIngredientFilter.Allows(c.def));

    static IEnumerable<(ThingDef def, int count)> Fixed(RecipeDef r) =>
        r == null ? Enumerable.Empty<(ThingDef, int)>() : r.ingredients.Where(i => i.IsFixedIngredient).Select(i => (i.FixedIngredient, Mathf.RoundToInt(i.GetBaseCount())));

    /// Материалы механоида: рецепт гестации; воскрешение — сталь рецепта ванили (сам труп не в счёт).
    static IEnumerable<(ThingDef def, int count)> Materials(Pattern p, Corpse c)
    {
        if (p.resurrect) return Fixed(p.recipe);
        RecipeDef r = Gestation(p.def);
        return r == null ? Enumerable.Empty<(ThingDef, int)>()
            : r.ingredients.Where(i => i.filter.AnyAllowedDef != null).Select(i => (i.filter.AnyAllowedDef, Mathf.RoundToInt(i.GetBaseCount())));
    }

    /// Мех = его материалы по цене их печати (наценка только на чипы и субъядра); животное — цена взрослого с наценкой биорепликации,
    /// человек — цена шаблона (Human.Value) с ней же.
    public static float Value(Pattern p, QualityCategory q, Corpse c = null)
    {
        if (p.IsAnimal) return p.def.GetStatValueAbstract(StatDefOf.MarketValue) * Rules.BioMarkup;
        if (p.IsHuman) return p.human.Value * Rules.BioMarkup;
        if (!p.IsMech) return Rules.NoValue(p.def) ? 0f : p.MarketValue(q) * Rules.Markup(p.def);
        return Materials(p, c).Sum(m => m.count * m.def.BaseMarketValue * Rules.Markup(m.def));
    }

    public static float Mass(Pattern p, Corpse c = null) =>
        p.resurrect ? Materials(p, c).Sum(m => m.count * m.def.GetStatValueAbstract(StatDefOf.Mass))
        : p.IsPawn ? MassPerBodySize * p.def.race.baseBodySize : p.def.GetStatValueAbstract(StatDefOf.Mass, p.stuff);

    /// Время штуки в тиках при скорости печати 100%. Механоид и воскрешение: материалы + циклы гестации своего рецепта.
    public static float Ticks(Pattern p, Corpse c = null)
    {
        if (p.IsAnimal) return GrowDays(p.def) / 2f * GenDate.TicksPerDay;
        if (p.IsHuman) return p.human.Ticks;
        if (!p.IsMech) return Work(p.def, p.stuff);
        RecipeDef r = p.resurrect ? p.recipe : Gestation(p.def);
        return r == null ? 0f : Materials(p, c).Sum(m => m.count * Work(m.def, null)) + r.gestationCycles * (r.formingTicks + r.workAmount);
    }

    /// Рост тела, как в капсуле роста ванили: половина вынашивания (у несушек его нет) + 3 дня на год до взрослого.
    /// Печать — вдвое быстрее («средне»): курица 7 ч, корова 2,2 дня, трумбо 7 дней.
    public static float GrowDays(ThingDef race) =>
        (race.HasComp(typeof(CompEggLayer)) ? 0f : Mathf.Max(0f, race.race.gestationPeriodDays) / 2f) + 3f * AdultAge(race);

    /// Возраст начала взрослой стадии, лет.
    public static float AdultAge(ThingDef race) => race.race.lifeStageAges.Last().minAge;

    public static float Bandwidth(Pattern p, Corpse c = null) =>
        p.resurrect ? c?.InnerPawn.GetStatValue(StatDefOf.BandwidthCost) ?? 0f : p.def.GetStatValueAbstract(StatDefOf.BandwidthCost);

    /// Материалы вещи — самый дешёвый способ её получить, без работы и качества: costList + материал, иначе источник (Source), иначе сама вещь.
    /// Так по кругу «печать → верстак или бочка → расщепитель» стоимость не растёт.
    static float MaterialsValue(Thing inner)
    {
        List<ThingDefCountClass> cost = inner.CostListAdjusted();
        if (cost.Count > 0) return cost.Sum(c => c.count * c.thingDef.BaseMarketValue);
        return Source(inner.def) ?? inner.def.GetStatValueAbstract(StatDefOf.MarketValue, inner.Stuff);
    }

    /// Пиво — из сусла в бочке, один к одному; паста — из сырья в раздатчике; остальное — самый дешёвый рецепт, где ингредиент
    /// «любое из» — самое дешёвое из разрешённых (еда — за питательность). null — получить не из чего.
    static float? Source(ThingDef def)
    {
        if (sourceCache.TryGetValue(def, out float? v)) return v;
        if (def == ThingDefOf.Beer) v = ThingDefOf.Wort.BaseMarketValue;
        else if (def == ThingDefOf.MealNutrientPaste)
            v = ThingDefOf.NutrientPasteDispenser.building.nutritionCostPerDispense * DefDatabase<ThingDef>.AllDefs
                .Where(d => Building_NutrientPasteDispenser.IsAcceptableFeedstock(d) && d.BaseMarketValue > 0f)
                .Select(d => d.BaseMarketValue / d.ingestible.CachedNutrition).DefaultIfEmpty(0f).Min();
        else
        {
            var recipes = DefDatabase<RecipeDef>.AllDefs.Where(r => !r.IsSurgery && r.products.Count == 1 && r.products[0].thingDef == def && r.ingredients.Count > 0).ToList();
            if (recipes.Count > 0) v = recipes.Min(r => r.ingredients.Sum(i => Cheapest(r, i)) / r.products[0].count);
        }
        return sourceCache[def] = v;
    }

    static float Cheapest(RecipeDef r, IngredientCount i) =>
        i.filter.AllowedThingDefs.Where(d => r.fixedIngredientFilter?.Allows(d) != false && d.BaseMarketValue > 0f && r.IngredientValueGetter.ValuePerUnitOf(d) > 0f)
            .Select(d => i.GetBaseCount() / r.IngredientValueGetter.ValuePerUnitOf(d) * d.BaseMarketValue).DefaultIfEmpty(0f).Min();

    /// Разборка одной штуки (до процента возврата): меньшее из материалов и рыночной цены.
    public static float DematValue(Thing t) => Rules.NoValue(t.GetInnerIfMinified().def) ? 0f : Mathf.Min(MaterialsValue(t.GetInnerIfMinified()), t.MarketValue);
}

public static class Demat
{
    static Settings S => ReplimatMechMod.S;

    /// Вещь: масса и стоимость — процентом из настроек, шаблон. Сама вещь исчезает.
    public static void Item(Thing t, PowerNet net, Thing splitter)
    {
        GameComponent_RM.Get.Record(t, splitter);
        // без стоимости (каменные блоки) — масса как у самой дешёвой печати, см. Order.Cap
        float mass = t.GetStatValue(StatDefOf.Mass) * t.stackCount * (Rules.NoValue(t.GetInnerIfMinified().def) ? 1f - Order.Cap / 100f : 1f);
        float value = Pricing.DematValue(t) * t.stackCount;
        float tankValue = t.GetInnerIfMinified() is Building_RMTank tank ? tank.value : 0f;
        Tanks.Add(net, mass * S.massReturn / 100f, (Tanks.CollectsValue(net) ? value * S.valueReturn / 100f : 0f) + tankValue);
        t.Destroy();
    }

    /// Труп как кремация: вещи на нём — каждая как отдельная вещь; свежий — мясо и кожа по правилу 50%; мех — продукты разборки и шаблон;
    /// свежее животное — ДНК вида.
    public static void Corpse(Corpse corpse, PowerNet net, Thing splitter)
    {
        Pawn pawn = corpse.InnerPawn;
        var gear = new List<Thing>();
        if (pawn.apparel != null) gear.AddRange(pawn.apparel.WornApparel);
        if (pawn.equipment != null) gear.AddRange(pawn.equipment.AllEquipmentListForReading);
        if (pawn.inventory != null) gear.AddRange(pawn.inventory.innerContainer);
        foreach (Thing g in gear) Item(g, net, splitter);

        GameComponent_RM.Get.Record(corpse, splitter);
        if (Bio.CorpseDna(corpse)) GameComponent_RM.Get.RecordDna(pawn, splitter);
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
