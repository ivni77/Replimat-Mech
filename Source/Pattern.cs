using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace ReplimatMech;

/// Шаблон: вещь как есть (материал, ингредиенты, гены, черты оружия) + отсканированные качества. Мех — сам мех.
public class Pattern : IExposable
{
    public ThingDef def, stuff;
    public bool minified;
    public PawnKindDef mechKind;
    public List<QualityCategory> qualities = new();
    public List<ThingDef> ingredients;
    public List<GeneDef> genes;
    public string xenoName;
    public XenotypeIconDef xenoIcon;
    public List<WeaponTraitDef> traits;

    string key, label;
    Thing sample;
    readonly Dictionary<QualityCategory, float> valueCache = new();

    public bool IsMech => mechKind != null;
    public bool HasQuality => !IsMech && def.HasComp(typeof(CompQuality));
    public QualityCategory Best => qualities.Count > 0 ? qualities.Max() : QualityCategory.Normal;

    public string Key => key ??= string.Join("|", new[]
    {
        def.defName, stuff?.defName, minified ? "min" : null, mechKind?.defName,
        ingredients == null ? null : string.Join(",", ingredients.Select(d => d.defName).OrderBy(n => n)),
        genes == null ? null : string.Join(",", genes.Select(d => d.defName)),
        xenoName, traits == null ? null : string.Join(",", traits.Select(d => d.defName))
    });

    public string Label => label ??= BuildLabel();

    string BuildLabel()
    {
        if (IsMech) return mechKind.LabelCap;
        string s = genes != null ? Sample.LabelNoCount.CapitalizeFirst() : GenLabel.ThingLabel(def, stuff).CapitalizeFirst();
        if (!ingredients.NullOrEmpty()) s += " (" + string.Join(", ", ingredients.Select(d => d.label)) + ")";
        if (minified) s += " " + "RM_Packed".Translate();
        return s;
    }

    public string LabelWithQuality(QualityCategory q) => HasQuality ? Label + ", " + q.GetLabelShort() : Label;

    /// Образец для подсчёта цены и подписи; никогда не спавнится. У меха образца нет.
    public Thing Sample => IsMech ? null : sample ??= Make(Best);

    public float MarketValue(QualityCategory q)
    {
        if (!valueCache.TryGetValue(q, out float v))
            valueCache[q] = v = Make(q).MarketValue;
        return v;
    }

    public void Notify_QualitiesChanged()
    {
        sample = null;
        valueCache.Clear();
    }

    /// Новая вещь по шаблону: целая, без привязки к владельцу.
    public Thing Make(QualityCategory q)
    {
        Thing t = ThingMaker.MakeThing(def, stuff);
        t.TryGetComp<CompQuality>()?.SetQuality(q, ArtGenerationContext.Colony);
        if (ingredients != null && t.TryGetComp<CompIngredients>() is { } ci)
        {
            ci.ingredients.Clear();
            ci.ingredients.AddRange(ingredients);
        }
        if (genes != null)
        {
            if (t is Genepack gp) gp.Initialize(genes);
            else if (t is Xenogerm xg)
            {
                var pack = (Genepack)ThingMaker.MakeThing(ThingDefOf.Genepack);
                pack.Initialize(genes);
                xg.Initialize(new List<Genepack> { pack }, xenoName, xenoIcon);
            }
        }
        if (traits != null && t.TryGetComp<CompBladelinkWeapon>() is { } bl)
        {
            bl.TraitsListForReading.Clear();
            bl.TraitsListForReading.AddRange(traits);
        }
        return minified && def.Minifiable ? t.MakeMinified() : t;
    }

    /// Шаблон с вещи или трупа меха; null — шаблона нет (живое, труп не меха, незавершёнка).
    public static Pattern From(Thing thing, out QualityCategory quality)
    {
        quality = QualityCategory.Normal;
        if (thing is Corpse corpse)
        {
            Pawn p = corpse.InnerPawn;
            // мех — только тот, кого игрок может выращивать в ванили (апокритона, термита и боевого ежа — нет)
            return p?.RaceProps.IsMechanoid == true && Pricing.Gestation(p.def) != null ? new Pattern { def = p.def, mechKind = p.kindDef } : null;
        }
        Thing inner = thing.GetInnerIfMinified();
        if (!Rules.Printable(inner.def)) return null;
        var pattern = new Pattern
        {
            def = inner.def,
            stuff = inner.Stuff,
            minified = thing is MinifiedThing,
            ingredients = inner.TryGetComp<CompIngredients>()?.ingredients.ToList(),
            genes = (inner as GeneSetHolderBase)?.GeneSet?.GenesListForReading.ToList(),
            traits = inner.TryGetComp<CompBladelinkWeapon>()?.TraitsListForReading.ToList(),
        };
        if (inner is Xenogerm xg)
        {
            pattern.xenoName = xg.xenotypeName;
            pattern.xenoIcon = xg.iconDef;
        }
        if (inner.TryGetQuality(out QualityCategory q)) quality = q;
        if (pattern.ingredients?.Count == 0) pattern.ingredients = null;
        if (pattern.traits?.Count == 0) pattern.traits = null;
        return pattern;
    }

    public void ExposeData()
    {
        Scribe_Defs.Look(ref def, "def");
        Scribe_Defs.Look(ref stuff, "stuff");
        Scribe_Values.Look(ref minified, "minified");
        Scribe_Defs.Look(ref mechKind, "mechKind");
        Scribe_Collections.Look(ref qualities, "qualities", LookMode.Value);
        Scribe_Collections.Look(ref ingredients, "ingredients", LookMode.Def);
        Scribe_Collections.Look(ref genes, "genes", LookMode.Def);
        Scribe_Values.Look(ref xenoName, "xenoName");
        Scribe_Defs.Look(ref xenoIcon, "xenoIcon");
        Scribe_Collections.Look(ref traits, "traits", LookMode.Def);
        qualities ??= new List<QualityCategory>();
    }
}

/// Шаблоны общие на всю колонию и не удаляются.
public class GameComponent_RM : GameComponent
{
    public List<Pattern> patterns = new();
    readonly Dictionary<string, Pattern> byKey = new();

    public static GameComponent_RM Get => Current.Game.GetComponent<GameComponent_RM>();

    /// Новая игра или загрузка: списки прошлой партии не тянем.
    public GameComponent_RM(Game game)
    {
        CompPrinter.All.Clear();
        Building_RMHopper.Spawned_.Clear();
    }

    public Pattern ByKey(string key) => key != null && byKey.TryGetValue(key, out Pattern p) ? p : null;

    /// Шаблоны по умолчанию — как ванильный верстак без станков: каменные блоки и древесина.
    public override void FinalizeInit()
    {
        foreach (ThingDef d in DefDatabase<ThingDef>.AllDefs.Where(d => d == ThingDefOf.WoodLog || Rules.NoValue(d)))
            if (!byKey.ContainsKey(new Pattern { def = d }.Key)) Add(new Pattern { def = d });
        Building_RMHopper.SyncAll();
    }

    void Add(Pattern p)
    {
        patterns.Add(p);
        byKey[p.Key] = p;
    }

    /// Записать шаблон вещи; новое — сообщение сверху. Лучшее качество сразу подхватят строки, которые ещё не начали печать.
    public Pattern Record(Thing thing)
    {
        Pattern fresh = Pattern.From(thing, out QualityCategory q);
        if (fresh == null) return null;
        var at = new TargetInfo(thing.PositionHeld, thing.MapHeld);
        if (!byKey.TryGetValue(fresh.Key, out Pattern p))
        {
            Add(p = fresh);
            Messages.Message("RM_MsgPatternNew".Translate(p.LabelWithQuality(q)), at, MessageTypeDefOf.PositiveEvent, false);
        }
        else if (p.HasQuality && q > p.Best)
            Messages.Message("RM_MsgPatternBetter".Translate(p.Label, q.GetLabel()), at, MessageTypeDefOf.PositiveEvent, false);
        if (!p.IsMech && !p.qualities.Contains(q))
        {
            p.qualities.Add(q);
            p.Notify_QualitiesChanged();
        }
        return p;
    }

    public override void ExposeData()
    {
        Scribe_Collections.Look(ref patterns, "patterns", LookMode.Deep);
        if (Scribe.mode == LoadSaveMode.LoadingVars)
        {
            patterns ??= new List<Pattern>();
            patterns.RemoveAll(p => p?.def == null || p.IsMech && Pricing.Gestation(p.def) == null);
            byKey.Clear();
            foreach (Pattern p in patterns) byKey[p.Key] = p;
        }
    }
}

public static class Rules
{
    static readonly HashSet<string> LivingDefs = new() { "HumanEmbryo", "HumanOvum" };
    static readonly HashSet<string> Chips = new() { "SignalChip", "PowerfocusChip", "NanostructuringChip", "SubcoreRegular", "SubcoreHigh" };
    static readonly HashSet<string> Archo = new() { "AIPersonaCore", "ArchiteCapsule", "RM_IsolinearModule", "MechSerumResurrector", "MechSerumHealer" };

    /// Всё, из чего появляется жизнь, — живое.
    public static bool Living(ThingDef def) => def.HasComp(typeof(CompHatcher)) || LivingDefs.Contains(def.defName) || def.category == ThingCategory.Plant || def.category == ThingCategory.Pawn;

    public static bool Printable(ThingDef def) =>
        !Living(def) && !def.IsCorpse && !def.isUnfinishedThing && (def.category == ThingCategory.Item || def.category == ThingCategory.Building);

    /// Особая группа: 0 — чипы и субъядра, 1 — архотех и особые, 2 — гены и уникальное, -1 — обычная вещь.
    public static int Group(ThingDef def)
    {
        if (Chips.Contains(def.defName)) return 0;
        if (Archo.Contains(def.defName) || def.thingCategories?.Any(c => c.defName == "BodyPartsArchotech") == true) return 1;
        if (typeof(GeneSetHolderBase).IsAssignableFrom(def.thingClass) || def.HasComp(typeof(CompTechprint))
            || def.HasComp(typeof(CompBladelinkWeapon)) || def.HasComp(typeof(CompUniqueWeapon))) return 2;
        return -1;
    }

    /// Каменные блоки — без стоимости и при печати, и при разборке: торговцы их не покупают.
    public static bool NoValue(ThingDef def) => def.IsWithinCategory(ThingCategoryDefOf.StoneBlocks);

    public static float Markup(ThingDef def)
    {
        int g = Group(def);
        return g < 0 ? 1f : ReplimatMechMod.S.groupMarkup[g];
    }

    /// Группа разрешена в настройках; упакованное печатается, только если постройку можно упаковать.
    public static bool CanPrint(Pattern p)
    {
        if (p.minified && !p.def.Minifiable) return false;
        if (p.IsMech) return ReplimatMechMod.S.needMechanitor;
        int g = Group(p.def);
        return g < 0 || ReplimatMechMod.S.groupAllowed[g];
    }

    public static bool FeederFood(ThingDef def) => def.IsNutritionGivingIngestible && !def.IsDrug;
}
