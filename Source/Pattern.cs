using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ReplimatMech;

/// Шаблон: вещь как есть (материал, ингредиенты, гены, черты оружия) + отсканированные качества. Мех — сам мех.
/// Воскрешение — не шаблон, а задание по рецепту ванили («воскресить лёгкого механоида» и т. д.): труп меха колонии несут носильщики, def нет.
/// Животное — ДНК вида (GameComponent_RM.Animal), человек — шаблон человека (GameComponent_RM.Human): печатает биорепликатор,
/// в сохранение не пишутся — их дают список ДНК и шаблоны людей.
public class Pattern : IExposable
{
    static List<Pattern> resurrections;
    /// По рецепту воскрешения ванили на вес механоида, от лёгкого.
    public static List<Pattern> Resurrections => resurrections ??= Pricing.ResurrectRecipes.Select(r => new Pattern { recipe = r }).ToList();

    public ThingDef def, stuff;
    public bool minified;
    /// Рецепт воскрешения ванили — у задания воскрешения.
    public RecipeDef recipe;
    public bool resurrect => recipe != null;
    public PawnKindDef mechKind;
    /// Вид животного по ДНК — у шаблона биорепликатора; def — его раса.
    public PawnKindDef animal;
    /// Шаблон человека — у шаблона биорепликатора; def — человек.
    public Human human;
    public List<QualityCategory> qualities = new();
    public List<ThingDef> ingredients;
    public List<GeneDef> genes;
    public string xenoName;
    public XenotypeIconDef xenoIcon;
    public List<WeaponTraitDef> traits;

    string key, label;
    Thing sample;
    readonly Dictionary<QualityCategory, float> valueCache = new();

    public bool IsMech => mechKind != null || resurrect;
    public bool IsAnimal => animal != null;
    public bool IsHuman => human != null;
    /// Живое — печатает только биорепликатор.
    public bool IsBio => IsAnimal || IsHuman;
    /// Печатается существо: по одному, без образца и качества.
    public bool IsPawn => IsMech || IsBio;
    /// «Повторять до»: у воскрешения считать нечего, людей не считаем — печатаются по количеству.
    public bool CanUntil => !resurrect && !IsHuman;
    public bool HasQuality => !IsPawn && def.HasComp(typeof(CompQuality));
    public QualityCategory Best => qualities.Count > 0 ? qualities.Max() : QualityCategory.Normal;

    /// У человека — по номеру шаблона, без кэша: черновик с «Репликанта» получает номер при сохранении.
    public string Key => IsHuman ? "human:" + human.id : key ??= resurrect ? "resurrect:" + recipe.defName : IsAnimal ? "dna:" + animal.defName : string.Join("|", new[]
    {
        def.defName, stuff?.defName, minified ? "min" : null, mechKind?.defName,
        ingredients == null ? null : string.Join(",", ingredients.Select(d => d.defName).OrderBy(n => n)),
        genes == null ? null : string.Join(",", genes.Select(d => d.defName)),
        xenoName, traits == null ? null : string.Join(",", traits.Select(d => d.defName))
    });

    /// Имя шаблона человека меняет игрок — не запоминается.
    public string Label => IsHuman ? human.name : label ??= BuildLabel();

    string BuildLabel()
    {
        if (resurrect) return recipe.LabelCap;
        if (IsMech) return mechKind.LabelCap;
        if (IsAnimal) return animal.LabelCap;
        string s = genes != null ? Sample.LabelNoCount.CapitalizeFirst() : GenLabel.ThingLabel(def, stuff).CapitalizeFirst();
        if (!ingredients.NullOrEmpty()) s += " (" + string.Join(", ", ingredients.Select(d => d.label)) + ")";
        if (minified) s += " " + "RM_Packed".Translate();
        return s;
    }

    public string LabelWithQuality(QualityCategory q) => HasQuality ? Label + ", " + q.GetLabelShort() : Label;

    /// Образец для подсчёта цены и подписи; никогда не спавнится. У существа образца нет.
    public Thing Sample => IsPawn ? null : sample ??= Probe(Best);

    public float MarketValue(QualityCategory q)
    {
        if (!valueCache.TryGetValue(q, out float v))
            valueCache[q] = v = Probe(q).MarketValue;
        return v;
    }

    public void Notify_QualitiesChanged()
    {
        sample = null;
        valueCache.Clear();
    }

    static readonly AccessTools.FieldRef<UniqueIDsManager, int> NextThingID = AccessTools.FieldRefAccess<UniqueIDsManager, int>("nextThingID");

    /// Образец не меняет игру, кто бы его ни создал (окно или тик): без сюжета для искусства, случайность и номера вещей — как были.
    /// Иначе в Multiplayer игроки разойдутся.
    Thing Probe(QualityCategory q)
    {
        UniqueIDsManager ids = Find.UniqueIDsManager;
        int next = NextThingID(ids);
        Rand.PushState();
        try
        {
            Thing t = Make(q, null);
            // из окна Multiplayer даёт вещам отрицательные номера, а ваниль по номеру выбирает картинку (Graphic_Random) — за краем списка
            t.thingIDNumber = t.GetInnerIfMinified().thingIDNumber = 0;
            return t;
        }
        finally
        {
            Rand.PopState();
            NextThingID(ids) = next;
        }
    }

    /// Новая вещь по шаблону: целая, без привязки к владельцу; art — откуда сюжет у произведения искусства.
    public Thing Make(QualityCategory q, ArtGenerationContext? art)
    {
        Thing t = ThingMaker.MakeThing(def, stuff);
        t.TryGetComp<CompQuality>()?.SetQuality(q, art);
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

/// Шаблоны общие на всю колонию.
public class GameComponent_RM : GameComponent
{
    public List<Pattern> patterns = new();
    readonly Dictionary<string, Pattern> byKey = new();
    /// ДНК видов — с биосканера и свежих трупов в расщепителе, на всю колонию.
    public List<PawnKindDef> dna = new();
    /// База людей (2026-10-18): гены и черты («def:степень»), что дали скан и свежие трупы в расщепителе; только растёт —
    /// шаблоны берут из неё. Внешность и навыки собирать не надо.
    public List<GeneDef> geneBase = new();
    public List<string> traitBase = new();
    readonly Dictionary<PawnKindDef, Pattern> animals = new();
    /// Шаблоны людей колонии; номер — с 1, по порядку создания.
    public List<Human> humans = new();
    int nextHuman = 1;
    public int NextHuman => nextHuman;
    readonly Dictionary<Human, Pattern> humanPatterns = new();
    /// Корзина (2026-10-18, 2026-10-21): ключи того, что убрал игрок, — шаблонов, воскрешений, черт и генов
    /// базы людей; не печатаются и не предлагаются, пока он их не восстановит. Скан того же предмета или вида их оттуда не достаёт.
    public List<string> trash = new();

    public bool Trashed(Pattern p) => trash.Contains(p.Key);
    public bool Trashed((TraitDef def, int degree) t) => trash.Contains(BinKey(t));
    public bool Trashed(GeneDef g) => trash.Contains(BinKey(g));

    /// Ключ черты и гена базы в корзине; у шаблонов — Pattern.Key.
    public static string BinKey((TraitDef def, int degree) t) => "trait:" + TraitKey(t);
    public static string BinKey(GeneDef g) => "gene:" + g.defName;

    /// Что может лежать в корзине: шаблон («Репликант» тоже), черта или ген базы.
    public bool Trashable(string key) =>
        key?.StartsWith("trait:") == true ? traitBase.Contains(key.Substring(6))
        : key?.StartsWith("gene:") == true ? geneBase.Any(g => BinKey(g) == key)
        : ByKey(key) != null;

    public static GameComponent_RM Get => Current.Game.GetComponent<GameComponent_RM>();

    /// Новая игра или загрузка: списки прошлой партии не тянем.
    public GameComponent_RM(Game game)
    {
        CompPrinter.All.Clear();
        Building_RMHopper.Spawned_.Clear();
    }

    bool linksGiven;

    /// Шаблоны предметов и механоидов (trashed — те, что в корзине): группы, выключенные в настройках, скрыты.
    public List<Pattern> Shown(bool trashed) => patterns.Where(p => !Rules.Off(p.def) && Trashed(p) == trashed).ToList();

    /// «resurrect» без рецепта — сохранения до 1.2.0: воскрешение лёгкого.
    public Pattern ByKey(string key) =>
        key == "resurrect" ? Pattern.Resurrections.FirstOrDefault()
        : key?.StartsWith("resurrect:") == true ? Pattern.Resurrections.FirstOrDefault(p => p.Key == key)
        : key?.StartsWith("dna:") == true ? Animal(DefDatabase<PawnKindDef>.GetNamedSilentFail(key.Substring(4)))
        : key?.StartsWith("human:") == true ? HumanPattern(humans.FirstOrDefault(h => h.id.ToString() == key.Substring(6)))
        : key != null && byKey.TryGetValue(key, out Pattern p) ? p : null;

    /// Шаблон инкубатора по ДНК вида — один на вид.
    public Pattern Animal(PawnKindDef k)
    {
        if (k == null) return null;
        if (!animals.TryGetValue(k, out Pattern p)) animals[k] = p = new Pattern { def = k.race, animal = k };
        return p;
    }

    /// Шаблон биорепликатора по шаблону человека — один на шаблон.
    public Pattern HumanPattern(Human h)
    {
        if (h == null) return null;
        if (!humanPatterns.TryGetValue(h, out Pattern p)) humanPatterns[h] = p = new Pattern { def = ThingDefOf.Human, human = h };
        return p;
    }

    /// Что печатает биорепликатор: записанные ДНК животных и шаблоны людей (биорепликация выключена — ничего); trashed — что из них
    /// в корзине.
    public List<Pattern> Bio(bool trashed) => !Rules.BioOn ? new List<Pattern>()
        : dna.Select(Animal).Concat(humans.Select(HumanPattern)).Where(p => Trashed(p) == trashed).ToList();

    /// Черты базы — по названию.
    public List<(TraitDef def, int degree)> Traits => traitBase.Select(TraitOf).Where(t => t.def != null).ToList();

    public static string TraitKey((TraitDef def, int degree) t) => t.def.defName + ":" + t.degree;

    static (TraitDef def, int degree) TraitOf(string key)
    {
        string[] kv = key.Split(':');
        TraitDef d = DefDatabase<TraitDef>.GetNamedSilentFail(kv[0]);
        return kv.Length == 2 && int.TryParse(kv[1], out int deg) && d?.degreeDatas.Any(x => x.degree == deg) == true ? (d, deg) : (null, 0);
    }

    /// Чего у человека нет в базе: черты не от генов и гены не внешности.
    (List<(TraitDef def, int degree)> traits, List<GeneDef> genes) Fresh(Pawn p) => (
        p.story?.traits.allTraits.Where(t => t.sourceGene == null).Select(t => (t.def, t.Degree)).Where(t => !traitBase.Contains(TraitKey(t))).Distinct().ToList()
            ?? new List<(TraitDef, int)>(),
        p.genes?.GenesListForReading.Select(g => g.def).Where(g => !Human.Look(g) && !geneBase.Contains(g)).Distinct().ToList() ?? new List<GeneDef>());

    /// ДНК этого существа уже записана; у людей — всё его уже в базе: сканировать нечего (2026-10-18).
    public bool KnowsDna(Pawn p)
    {
        if (!p.RaceProps.Humanlike) return dna.Contains(p.kindDef);
        var (t, g) = Fresh(p);
        return t.Count == 0 && g.Count == 0;
    }

    void ToBase(IEnumerable<(TraitDef def, int degree)> traits, IEnumerable<GeneDef> genes)
    {
        foreach (string k in traits.Select(TraitKey)) if (!traitBase.Contains(k)) traitBase.Add(k);
        foreach (GeneDef g in genes.Where(g => g != null && !Human.Look(g))) if (!geneBase.Contains(g)) geneBase.Add(g);
    }

    public Human Blank => humans.FirstOrDefault(h => h.blank);

    /// Навсегда — шаблон человека и его ключ корзины (номер не освобождается).
    public void DeleteHuman(Human h)
    {
        trash.Remove(HumanPattern(h).Key);
        humanPatterns.Remove(h);
        humans.Remove(h);
    }

    /// Сохранить шаблон человека. Нового нет — заводится с номером черновика (конструктор берёт следующий свободный), занят — следующим.
    /// Задания печатают уже новый.
    public Human SaveHuman(int id, string data)
    {
        Human h = humans.FirstOrDefault(x => x.id == id);
        if (h == null)
        {
            humans.Add(h = new Human { id = Math.Max(id, nextHuman) });
            nextHuman = h.id + 1;
        }
        h.Decode(data);
        return h;
    }

    /// Шаблоны по умолчанию — как ванильный верстак без станков: каменные блоки и древесина; у людей — основа «Репликант».
    public override void FinalizeInit()
    {
        foreach (ThingDef d in DefDatabase<ThingDef>.AllDefs.Where(d => d == ThingDefOf.WoodLog || Rules.NoValue(d)))
            if (!byKey.ContainsKey(new Pattern { def = d }.Key)) Add(new Pattern { def = d });
        if (Blank == null) humans.Insert(0, Human.Blank(nextHuman++));
    }

    public override void StartedNewGame() => linksGiven = true;

    /// Сохранение до линков (до 1.2.0): с компьютером связывались механиторы — им риплинк, компьютеру — оператор, один раз.
    public override void LoadedGame()
    {
        if (linksGiven) return;
        linksGiven = true;
        if (!RMDefOf.RM_Replication.IsFinished) return;
        List<Pawn> mechanitors = PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonists.Where(MechanitorUtility.IsMechanitor)
            .OrderBy(p => p.thingIDNumber).ToList();
        foreach (Pawn p in mechanitors) Links.Give(p, Link.Rep);
        foreach (Map map in Find.Maps)
            foreach (Building_RMComputer c in map.listerBuildings.AllBuildingsColonistOfClass<Building_RMComputer>())
                c.operatorPawn ??= mechanitors.FirstOrDefault(p => p.MapHeld == map);
    }

    void Add(Pattern p)
    {
        patterns.Add(p);
        byKey[p.Key] = p;
    }

    /// Записать шаблон вещи; новое — сообщение сверху, смотреть — at (расщепитель: вещь уже не на карте). Лучшее качество сразу подхватят строки, которые ещё не начали печать.
    /// Шаблон в корзине там и остаётся, качество записывается молча.
    public Pattern Record(Thing thing, Thing at)
    {
        Pattern fresh = Pattern.From(thing, out QualityCategory q);
        if (fresh == null) return null;
        if (!byKey.TryGetValue(fresh.Key, out Pattern p))
        {
            Add(p = fresh);
            Messages.Message("RM_MsgPatternNew".Translate(p.LabelWithQuality(q)), at, MessageTypeDefOf.PositiveEvent, false);
        }
        else if (p.HasQuality && q > p.Best && !Trashed(p))
            Messages.Message("RM_MsgPatternBetter".Translate(p.Label, q.GetLabel()), at, MessageTypeDefOf.PositiveEvent, false);
        if (!p.IsMech && !p.qualities.Contains(q))
        {
            p.qualities.Add(q);
            p.Notify_QualitiesChanged();
        }
        return p;
    }

    /// ДНК вида записана; новая — сообщение сверху. at — куда смотреть (труп в расщепителе исчезает). У человека — его новые черты и гены
    /// в базу (шаблоном он не становится: «все первозданные одинаковые, различие только в генах»), в сообщении — что записано.
    public void RecordDna(Pawn p, LookTargets at = null)
    {
        if (p == null || KnowsDna(p)) return;
        string what = p.KindLabel;
        if (p.RaceProps.Humanlike)
        {
            var (traits, genes) = Fresh(p);
            ToBase(traits, genes);
            Gender g = p.gender == Gender.Male ? Gender.Male : Gender.Female;
            what = string.Join("; ", new[]
            {
                traits.Count == 0 ? null : "Traits".Translate().CapitalizeFirst() + " — " + string.Join(", ", traits.Select(t => t.def.DataAtDegree(t.degree).GetLabelFor(g))),
                genes.Count == 0 ? null : "Genes".Translate().CapitalizeFirst() + " — " + string.Join(", ", genes.Select(x => x.label)),
            }.Where(x => x != null));
        }
        else dna.Add(p.kindDef);
        Messages.Message(RMUI.Tr(p.RaceProps.Humanlike ? "RM_MsgBase" : "RM_MsgDna", what), at ?? p, MessageTypeDefOf.PositiveEvent, false);
    }

    public override void ExposeData()
    {
        Scribe_Collections.Look(ref patterns, "patterns", LookMode.Deep);
        Scribe_Collections.Look(ref dna, "dna", LookMode.Def);
        Scribe_Collections.Look(ref geneBase, "geneBase", LookMode.Def);
        Scribe_Collections.Look(ref traitBase, "traitBase", LookMode.Value);
        // сохранения до «Шаблонов v2»: ДНК людей по ксенотипу — гены ванильных ксенотипов идут в базу
        List<string> xenoDna = null;
        if (Scribe.mode == LoadSaveMode.LoadingVars) Scribe_Collections.Look(ref xenoDna, "xenoDna", LookMode.Value);
        Scribe_Collections.Look(ref humans, "humans", LookMode.Deep);
        Scribe_Values.Look(ref nextHuman, "nextHuman", 1);
        Scribe_Values.Look(ref linksGiven, "linksGiven");
        Scribe_Collections.Look(ref trash, "trash", LookMode.Value);
        if (Scribe.mode == LoadSaveMode.LoadingVars)
        {
            dna ??= new List<PawnKindDef>();
            dna.RemoveAll(k => k == null);
            geneBase ??= new List<GeneDef>();
            geneBase.RemoveAll(g => g == null);
            traitBase ??= new List<string>();
            humans ??= new List<Human>();
            // старые шаблоны остаются (удаляемыми), их черты и гены — в базу
            foreach (string k in xenoDna ?? new List<string>()) ToBase(new List<(TraitDef, int)>(), DefDatabase<XenotypeDef>.GetNamedSilentFail(k)?.genes ?? new List<GeneDef>());
            foreach (Human h in humans) ToBase(h.traits, h.genes);
            humanPatterns.Clear();
            patterns ??= new List<Pattern>();
            patterns.RemoveAll(p => p?.def == null || p.IsMech && Pricing.Gestation(p.def) == null);
            byKey.Clear();
            foreach (Pattern p in patterns) byKey[p.Key] = p;
            trash ??= new List<string>();
            trash.RemoveAll(k => !Trashable(k));
        }
    }
}

public static class Rules
{
    static readonly HashSet<string> LivingDefs = new() { "HumanEmbryo", "HumanOvum" };
    static readonly HashSet<string> Chips = new() { "SignalChip", "PowerfocusChip", "NanostructuringChip", "SubcoreRegular", "SubcoreHigh" };
    static readonly HashSet<string> BioDefs = new() { "ArchiteCapsule", "MechSerumResurrector", "MechSerumHealer" };

    /// Всё, из чего появляется жизнь, — живое: не печатается (расщепляется, как любой предмет).
    static bool Living(ThingDef def) => def.HasComp(typeof(CompHatcher)) || LivingDefs.Contains(def.defName);

    public static bool Printable(ThingDef def) =>
        !Living(def) && !Off(def) &&!def.IsCorpse && !def.isUnfinishedThing && (def.category == ThingCategory.Item || def.category == ThingCategory.Building);

    /// Номера групп — как в настройках 1.1.0: биорепликация — на месте «архотеха», уникальное — на месте «генов и уникального».
    public const int Bio = 1, Unique = 2;

    /// Особая группа: 0 — чипы и субъядра, Bio — биорепликация (гены, архотех, сыворотки), Unique — уникальное, -1 — обычный предмет.
    public static int Group(ThingDef def)
    {
        if (Chips.Contains(def.defName)) return 0;
        if (BioDefs.Contains(def.defName) || typeof(GeneSetHolderBase).IsAssignableFrom(def.thingClass)
            || def.thingCategories?.Any(c => c.defName == "BodyPartsArchotech") == true) return Bio;
        if (def.defName == "AIPersonaCore" || def.HasComp(typeof(CompTechprint)) || def.HasComp(typeof(CompBladelinkWeapon)) || def.HasComp(typeof(CompUniqueWeapon))) return Unique;
        return -1;
    }

    /// Биорепликация включена в настройках: биосканер в меню, ДНК записывается и видна.
    public static bool BioOn => ReplimatMechMod.S.groupAllowed[Bio];

    /// Группа выключена в настройках — печати её предметов нет: шаблон не записывается, записанные скрыты. Расщепитель берёт их, как всё остальное.
    public static bool Off(ThingDef def) => Group(def) is var g && g >= 0 && !ReplimatMechMod.S.groupAllowed[g];

    /// Каменные блоки — без стоимости и при печати, и при разборке: торговцы их не покупают.
    public static bool NoValue(ThingDef def) => def.IsWithinCategory(ThingCategoryDefOf.StoneBlocks);

    public static float BioMarkup => ReplimatMechMod.S.groupMarkup[Bio];

    public static float Markup(ThingDef def)
    {
        int g = Group(def);
        return g < 0 ? 1f : ReplimatMechMod.S.groupMarkup[g];
    }

    /// Печатать можно: разрешено настройками и исследование изучено.
    public static bool CanPrint(Pattern p) => Allowed(p) && MissingResearch(p) == null;

    /// Группа разрешена в настройках; упакованное печатается, только если постройку можно упаковать.
    public static bool Allowed(Pattern p)
    {
        if (p.resurrect) return true;
        if (p.IsBio) return BioOn;
        if (p.minified && !p.def.Minifiable) return false;
        int g = Group(p.def);
        return g < 0 || ReplimatMechMod.S.groupAllowed[g];
    }

    static readonly Dictionary<ThingDef, List<Func<ResearchProjectDef>>> sources = new();

    /// Какое исследование не изучено (null — изучено всё), как в ванили: постройка — её исследования; механоид и предмет — хоть один
    /// источник из Sources, где изучено всё. Источники — один раз на шаблон: ванильный AllRecipeUsers перебирает все вещи.
    public static ResearchProjectDef MissingResearch(Pattern p)
    {
        if (p.resurrect) return RecipeMissing(p.recipe);
        if (p.IsBio) return null;
        if (p.def.category == ThingCategory.Building) return Missing(p.def.researchPrerequisites);
        if (!sources.TryGetValue(p.def, out var src)) sources[p.def] = src = Sources(p.def).ToList();
        ResearchProjectDef first = null;
        foreach (Func<ResearchProjectDef> missing in src)
        {
            ResearchProjectDef m = missing();
            if (m == null) return null;
            first ??= m;
        }
        return first;
    }

    /// Откуда предмет в ванили и что для этого изучить: рецепт — его исследование и хоть один изученный верстак; посадка — исследование
    /// растения (дьявольская нить, какао); чертёж — его проект; чип — проект, который открывает его анализ. Материал, добываемый из породы
    /// (сталь, серебро), дикорастущее (дерево) и то, что не делается вовсе (только купить или найти), — без исследований. Изделие, которое
    /// ещё и добывают (компоненты из спрессованных механизмов), — после исследования, как на верстаке (2026-10-17).
    static IEnumerable<Func<ResearchProjectDef>> Sources(ThingDef d)
    {
        if (d.IsStuff && DefDatabase<ThingDef>.AllDefs.Any(b => b.building?.mineableThing == d)
            || DefDatabase<BiomeDef>.AllDefs.Any(b => b.AllWildPlants.Any(x => x.plant?.harvestedThingDef == d))) yield break;
        if (d.GetCompProperties<CompProperties_Techprint>()?.project is { } tp) yield return () => tp.IsFinished ? null : tp;
        foreach (ResearchProjectDef rp in DefDatabase<ResearchProjectDef>.AllDefs.Where(r => r.requiredAnalyzed?.Contains(d) == true))
            yield return () => rp.IsFinished ? null : rp;
        foreach (ThingDef plant in DefDatabase<ThingDef>.AllDefs.Where(x => x.plant?.harvestedThingDef == d && x.plant.Sowable))
            yield return () => Missing(plant.plant.sowResearchPrerequisites);
        foreach (RecipeDef r in DefDatabase<RecipeDef>.AllDefs.Where(r => r.products.Any(x => x.thingDef == d)))
        {
            List<ThingDef> users = r.AllRecipeUsers.ToList();
            if (users.Count > 0)
                yield return () => RecipeMissing(r) ?? (users.Any(u => u.IsResearchFinished) ? null : Missing(users[0].researchPrerequisites));
        }
    }

    static ResearchProjectDef Missing(List<ResearchProjectDef> list) => list?.FirstOrDefault(r => !r.IsFinished);

    static ResearchProjectDef RecipeMissing(RecipeDef r) => r.researchPrerequisite is { IsFinished: false } one ? one : Missing(r.researchPrerequisites);

    public static bool FeederFood(ThingDef def) => def.IsNutritionGivingIngestible && !def.IsDrug;
}
