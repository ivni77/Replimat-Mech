using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace ReplimatMech;

public enum OrderMode { Make, Until, Forever }

/// Задание репликатора. Печать идёт пачками (batch штук); цена пачки списывается при её старте, отмена возвращает ровно списанное.
/// Правка печати игроком (SamePrint) — пачка в баки и печать с нуля по новым настройкам (CompPrinter.Restart; 2026-10-20: иначе
/// быстро до 99%, потом дёшево — и быстро, и дёшево); скорость оператора и цель пересчитывают её (CompPrinter.Reprice).
public class Order : IExposable, IRenameable
{
    public Pattern pattern;
    /// Имя от игрока, как у задания ванили; null — название шаблона.
    public string name;
    public OrderMode mode;
    public int target = 1;
    public bool manualQuality, paused, surplus;
    public QualityCategory quality;
    /// К кому подключится механоид и в какую его группу (номер с 1, как в ванили).
    public Pawn mechanitor;
    public int group = 1;
    /// Пол животного — выбор в задании, без случайности (2026-10-18); у вида без полов — None.
    public Gender gender;
    /// Каких механоидов воскрешать — как фильтр трупов у задания ванили, внутри рецепта.
    public ThingFilter corpses;
    public int slider = -1, pct, batch = 1;
    public bool paid;
    public float paidMass, paidValue, progress, batchTicks;
    public QualityCategory paidQuality;
    public string status;
    public bool warned, running;
    /// Репликатор, в чьей очереди задание (не сохраняется — ставит CompPrinter).
    public CompPrinter printer;
    /// До 1.2.0 очередей было две: задания первой при загрузке встают вперёд.
    public bool legacyFirst;
    string patternKey;

    static Settings S => ReplimatMechMod.S;

    /// Ползунки ±40%: 0 — время, 1 — стоимость, зеркально (дольше — дешевле). Значение = выигрыш в %.
    /// У шаблона без стоимости (каменные блоки) второй — масса, в обе стороны: расщепитель возвращает такие вещи
    /// на 1 − Cap/100 массы — не больше, чем самая дешёвая печать, так что масса из ничего не появляется.
    public const int Cap = 40;

    /// Предел количества — как у заданий ванили: дальше она пишет «бесконечно».
    public const int MaxTarget = 999999;

    /// Труп в репликаторе, если он для этого задания.
    public Corpse Corpse => printer?.Corpse is { } c && Fits(c) ? c : null;

    /// Фильтр трупов строкой (имена defName через запятую) — для Multiplayer.
    public string CorpseKey
    {
        get => corpses == null ? null : string.Join(",", corpses.AllowedThingDefs.Select(d => d.defName).OrderBy(n => n));
        set
        {
            if (value == null) return;
            corpses = new ThingFilter();
            foreach (string n in value.Split(',')) if (DefDatabase<ThingDef>.GetNamedSilentFail(n) is { } d) corpses.SetAllow(d, true);
        }
    }

    public bool Fits(Corpse c) => pattern?.resurrect == true && pattern.recipe.fixedIngredientFilter.Allows(c.def) && corpses?.Allows(c.def) != false;

    public bool MassSlider => Pricing.Value(pattern, Quality, Corpse) <= 0f;

    public float K(int i) => 1f - SliderValue(i) / 100f;
    public int SliderValue(int i) => slider < 0 ? 0 : i == slider ? pct : -pct;

    public void SetSlider(int i, int v)
    {
        pct = Mathf.Clamp(v, -Cap, Cap);
        slider = pct == 0 ? -1 : i;
    }

    public QualityCategory Quality
    {
        get
        {
            QualityCategory q = manualQuality && pattern.qualities.Contains(quality) ? quality : pattern.Best;
            return q > S.qualityCap ? S.qualityCap : q;
        }
    }

    /// Скорость — от линка оператора компьютера, который нужен этому шаблону.
    public float Speed => printer?.Speed(pattern) ?? 1f;
    public float UnitValue => Pricing.Value(pattern, Quality, Corpse) * (MassSlider ? 1f : K(1));
    public float UnitMass => Pricing.Mass(pattern, Corpse) * (MassSlider ? K(1) : 1f);
    public float UnitTicks => Pricing.Ticks(pattern, Corpse) / Speed * K(0);

    /// Стопка ванили: столько печатается за раз и на столько меняют количество кнопки; существо — по одному.
    public int Stack => pattern.IsPawn ? 1 : pattern.def.stackLimit;

    /// Что печатается сейчас — как стопка в ванили: «блоки гранита x75»; животное — с полом: «корова, самка».
    public string BatchLabel => pattern.LabelWithQuality(paidQuality) + (Gendered ? ", " + GenderLabel : "") + (batch > 1 ? " x" + batch : "");

    /// У животного выбирается пол — если у вида он есть.
    public bool Gendered => pattern?.IsAnimal == true && pattern.def.race.hasGenders;
    public string GenderLabel => gender.GetLabel(true);

    /// Излишки в расщепитель — у предметов в «повторять до», кроме репликатора для животных (корм остаётся в нём).
    public bool CanSurplus => mode == OrderMode.Until && !pattern.IsPawn && printer?.Feeder != true;

    /// Новое задание, как в ванили: «повторять до» одной стопки; механоид, воскрешение и животное — «сделать 1». Механоид — к оператору
    /// (если он механитор) или первому механитору колонии, в его первую группу; животное — самка.
    public static Order Default(Pattern p, CompPrinter c) => new()
    {
        pattern = p, printer = c, mode = p.IsPawn ? OrderMode.Make : OrderMode.Until, target = p.IsPawn ? 1 : p.def.stackLimit,
        mechanitor = p.IsMech ? c?.Mechanitors().FirstOrDefault() : null, corpses = AllCorpses(p),
        gender = p.IsAnimal && p.def.race.hasGenders ? Gender.Female : Gender.None,
    };

    /// Воскрешение — все механоиды рецепта, как задание ванили без своего фильтра.
    public static ThingFilter AllCorpses(Pattern p)
    {
        if (!p.resurrect) return null;
        var f = new ThingFilter();
        f.CopyAllowancesFrom(p.recipe.fixedIngredientFilter);
        return f;
    }

    /// Копия настроек (буфер обмена, черновик окна): без оплаченной пачки. Репликатор остаётся — от него цена и скорость.
    public Order Clone()
    {
        var o = (Order)MemberwiseClone();
        (o.paid, o.running, o.progress, o.batch, o.status, o.warned) = (false, false, 0f, 1, null, false);
        o.CorpseKey = CorpseKey;
        return o;
    }

    /// Количество поставили в 0 — задание на паузе; ушло с нуля — снова печатает. Не изменилось (и «сделать X», само досчитавшее
    /// до нуля) — пауза как была.
    public void SetTarget(int v)
    {
        v = Mathf.Clamp(v, 0, MaxTarget);
        if (v != target) (paused, target) = (v == 0 || paused && target != 0, v);
    }

    public string BaseLabel => pattern.Label;
    public string InspectLabel => RenamableLabel;

    /// Задание биорепликатора — часть пешки (2026-10-20): своего имени нет, зовётся шаблоном или животным.
    public bool Renamable => !pattern.IsBio;

    /// Имя от игрока — строкой над заданием; нет имени или его не бывает — null.
    public string Title => Renamable ? name : null;

    public string RenamableLabel
    {
        get => Title ?? BaseLabel;
        set => SetName(value);
    }

    /// Имя черновика из окна — не сеттером: сеттер Multiplayer шлёт командой (у Order есть SyncWorker), черновик остался бы без имени.
    public void SetName(string v) => name = !Renamable || v.NullOrEmpty() || v == BaseLabel ? null : v;

    public void ApplySettings(Order o)
    {
        name = o.name;
        mode = o.mode;
        SetTarget(o.target);
        manualQuality = o.manualQuality;
        quality = o.quality;
        mechanitor = o.mechanitor;
        group = o.group;
        gender = o.gender;
        surplus = o.surplus;
        slider = o.slider;
        pct = o.pct;
        CorpseKey = o.CorpseKey;
    }

    public bool SameSettings(Order o) => name == o.name && mode == o.mode && target == o.target && surplus == o.surplus && mechanitor == o.mechanitor
                                         && group == o.group && SamePrint(o);

    /// Те же настройки печати — что печатается и почём: качество, пол, ползунок, труп. Название, количество, режим, излишки, механитор и
    /// группа печать не меняют (2026-10-20: количество — не с нуля).
    public bool SamePrint(Order o) => manualQuality == o.manualQuality && quality == o.quality && gender == o.gender && slider == o.slider && pct == o.pct
                                      && CorpseKey == o.CorpseKey;

    public void ExposeData()
    {
        if (Scribe.mode == LoadSaveMode.Saving) patternKey = pattern?.Key;
        Scribe_Values.Look(ref patternKey, "pattern");
        Scribe_Values.Look(ref name, "name");
        Scribe_Values.Look(ref mode, "mode");
        Scribe_Values.Look(ref target, "target", 1);
        // до 1.1.0 «сделать X» считало сделанное вверх до цели — теперь цель сама убывает до нуля
        int done = 0;
        Scribe_Values.Look(ref done, "done");
        if (mode == OrderMode.Make) target = Mathf.Max(0, target - done);
        if (Scribe.mode == LoadSaveMode.LoadingVars) Scribe_Values.Look(ref legacyFirst, "important");
        Scribe_Values.Look(ref manualQuality, "manualQuality");
        Scribe_Values.Look(ref paused, "paused");
        Scribe_Values.Look(ref surplus, "surplus");
        Scribe_Values.Look(ref quality, "quality");
        Scribe_References.Look(ref mechanitor, "mechanitor");
        Scribe_Values.Look(ref group, "group", 1);
        Scribe_Values.Look(ref gender, "gender");
        Scribe_Values.Look(ref slider, "slider", -1);
        Scribe_Values.Look(ref pct, "pct");
        Scribe_Values.Look(ref paid, "paid");
        Scribe_Values.Look(ref paidMass, "paidMass");
        Scribe_Values.Look(ref paidValue, "paidValue");
        Scribe_Values.Look(ref progress, "progress");
        Scribe_Values.Look(ref batchTicks, "unitTicks");
        Scribe_Values.Look(ref batch, "batch", 1);
        Scribe_Values.Look(ref paidQuality, "paidQuality");
        // статус — до первого Refresh после загрузки (игра стоит на паузе) иначе пустой: «ждёт» у готового задания (2026-10-22)
        Scribe_Values.Look(ref status, "status");
        Scribe_Values.Look(ref running, "running");
        Scribe_Values.Look(ref warned, "warned");
        string ck = CorpseKey;
        Scribe_Values.Look(ref ck, "corpses");
        if (Scribe.mode == LoadSaveMode.LoadingVars) CorpseKey = ck;
        if (Scribe.mode != LoadSaveMode.PostLoadInit) return;
        pattern = GameComponent_RM.Get.ByKey(patternKey);
        if (pattern?.resurrect == true) corpses ??= AllCorpses(pattern);
        if (slider > 1) slider = -1; // старые сохранения: ползунок массы 0.2.2
    }
}

/// Готовый мех ждёт пропускной способности механитора; списанное держится до выхода или отмены.
public class WaitingMech : IExposable
{
    public PawnKindDef kind;
    public Pawn mechanitor;
    public int group = 1;
    public float mass, value;

    public int Bandwidth => (int)kind.race.GetStatValueAbstract(StatDefOf.BandwidthCost);

    public void ExposeData()
    {
        Scribe_Defs.Look(ref kind, "kind");
        Scribe_References.Look(ref mechanitor, "mechanitor");
        Scribe_Values.Look(ref group, "group", 1);
        Scribe_Values.Look(ref mass, "mass");
        Scribe_Values.Look(ref value, "value");
    }
}

public class CompProperties_Printer : CompProperties
{
    public bool feeder, incubator;
    public CompProperties_Printer() => compClass = typeof(CompPrinter);
}

/// Задания живут на самом репликаторе (и для животных): переживают потерю компьютера и упаковку. Труп для воскрешения — внутри.
public class CompPrinter : ThingComp, IRenameable, IThingHolder
{
    public static readonly HashSet<CompPrinter> All = new();
    public List<Order> orders = new();
    public List<WaitingMech> waiting = new();
    public string name;
    ThingOwner<Thing> held;
    CompPowerTrader power;

    public CompPrinter() => held = new ThingOwner<Thing>(this);

    public bool Feeder => ((CompProperties_Printer)props).feeder;
    /// Биорепликатор (defName RM_Incubator): печатает только живое — животных по ДНК и людей по шаблонам, репликаторы — только не их.
    public bool Incubator => ((CompProperties_Printer)props).incubator;
    public PowerNet Net => power?.PowerNet;
    public Order Running => orders.FirstOrDefault(o => o.running);
    public bool CanWork => parent.Spawned && power.PowerOn && Tanks.HasComputer(Net);
    public Corpse Corpse => held.Count > 0 ? held[0] as Corpse : null;

    /// Оператор компьютера сети: печать идёт, пока он жив и на карте компьютера.
    public Pawn Operator => Building_RMComputer.OperatorOf(Net);
    public float Speed(Pattern p) => Links.Speed(Operator, Links.Of(p));

    public string RenamableLabel { get => name.NullOrEmpty() ? BaseLabel : name; set => name = value; }
    public string BaseLabel => parent.def.LabelCap;
    public string InspectLabel => RenamableLabel;
    /// «Репликатор — Кухня»; имя по умолчанию («Репликатор 1») — как есть.
    public string Title => RenamableLabel.StartsWith(BaseLabel) ? RenamableLabel : BaseLabel + " — " + RenamableLabel;

    /// Репликаторы сети: обычные, для животных, биорепликаторы, внутри — по имени (таблица компьютера, кнопка «Репликатор» в задании).
    public static List<CompPrinter> Of(PowerNet net) => All.Where(c => c.Net != null && c.Net == net)
        .OrderBy(c => c.Incubator ? 2 : c.Feeder ? 1 : 0).ThenBy(c => c.RenamableLabel).ToList();

    /// Имя репликатора — и названием здания: в осмотре, подсказках, меню (переименовывается компонент, не здание).
    public override string TransformLabel(string label) => name.NullOrEmpty() ? label : name;

    public bool Accepts(Pattern p) => Incubator ? p.IsBio : !p.IsBio && (!Feeder || !p.IsMech && Rules.FeederFood(p.def));

    public ThingOwner GetDirectlyHeldThings() => held;
    public void GetChildHolders(List<IThingHolder> outChildren) => ThingOwnerUtility.AppendThingHoldersFromThings(outChildren, held);

    /// Механиторы на выбор: оператор первым, дальше колонисты карты по номеру.
    public List<Pawn> Mechanitors()
    {
        IEnumerable<Pawn> all = parent.MapHeld?.mapPawns.FreeColonists.Where(MechanitorUtility.IsMechanitor).OrderBy(p => p.thingIDNumber) ?? Enumerable.Empty<Pawn>();
        return all.OrderBy(p => p != Operator).ToList();
    }

    /// Задание на этот шаблон (у воскрешения и животного их может быть много — нет: свой выбор механоида, свой пол).
    public Order Same(Pattern p) => p.resurrect || p.IsAnimal ? null : orders.FirstOrDefault(o => o.pattern?.Key == p.Key);

    public void Add(Order o)
    {
        o.printer = this;
        orders.Add(o);
    }

    public override void PostSpawnSetup(bool respawningAfterLoad)
    {
        power = parent.GetComp<CompPowerTrader>();
        All.Add(this);
        if (name.NullOrEmpty()) name = BaseLabel + " " + All.Count(c => c.parent.def == parent.def);
    }

    public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
    {
        All.Remove(this);
        held.TryDropAll(parent.Position, map, ThingPlaceMode.Near);
    }


    /// Носильщик принёс труп для воскрешения.
    public void Hold(Thing t) => held.TryAddOrTransfer(t);

    /// Свёрнутый (в руках, на складе) тикает внутри MinifiedThing без карты — стоит.
    public override void CompTick()
    {
        if (parent.Spawned && parent.IsHashIntervalTick(60)) Work(60);
    }

    void Work(int ticks)
    {
        for (int i = 0; i < waiting.Count; i++)
            if (TrySpawnMech(waiting[i])) waiting.RemoveAt(i--);
        Order run = Refresh();
        if (run == null) return;
        run.progress += ticks;
        if (run.progress >= run.batchTicks) Complete(run);
    }

    /// Статусы заданий и какое печатает: сверху вниз первое не заблокированное (оплачивает пачку, если хватает баков). Зовёт и правка игрока (Edit).
    public Order Refresh()
    {
        string off = !CanWork ? "RM_StOff" : Operator == null ? "RM_StNoOperator" : null;
        // труп больше ни одному заданию не нужен (удалили, сменили фильтр) — выложить у репликатора
        if (Corpse is { } cp && parent.Spawned && !orders.Any(o => o.Fits(cp))) held.TryDropAll(parent.InteractionCell, parent.Map, ThingPlaceMode.Near);
        Order run = null;
        foreach (Order o in orders.ToList())
        {
            o.running = false;
            // запретили в настройках, не изучено, унесли труп — начатая пачка в баки
            if (o.paid && (!Rules.CanPrint(o.pattern) || o.pattern.resurrect && o.Corpse == null)) Unpay(o);
            if (o.paid) Trim(o);
            if (o.paid) Reprice(o);
            o.status = o.paused ? "RM_StSuspended" : Blocked(o) ?? off;
            if (o.status != null) continue;
            if (run != null) o.status = "RM_StWait";
            else if (o.paid || TryPay(o)) run = o;
        }
        if (run != null) run.running = true;
        return run;
    }

    /// Пачка не больше, чем осталось до цели: цель уже есть (сделано или на складе) — вся пачка в баки, осталось меньше пачки —
    /// лишнее в баки (разницу вернёт Reprice). На паузе пачка ждёт как есть.
    void Trim(Order o)
    {
        if (o.paused) return;
        int left = Need(o);
        if (left <= 0) Unpay(o);
        else if (left < o.batch) o.batch = left;
    }

    /// Оплаченная пачка — по текущим настройкам задания и скорости линка оператора: время — с той же долей готовности, разница массы
    /// и стоимости — из баков или в баки. Доплатить нечем — пачка целиком в баки, задание ждёт оплаты заново.
    void Reprice(Order o)
    {
        float t = o.batch * o.UnitTicks, m = o.batch * o.UnitMass, v = o.batch * o.UnitValue, dm = m - o.paidMass, dv = v - o.paidValue;
        if (Mathf.Abs(t - o.batchTicks) < 1f && Mathf.Abs(dm) < 0.001f && Mathf.Abs(dv) < 0.001f) return;
        if (!Tanks.TryTake(Net, Mathf.Max(0f, dm), Mathf.Max(0f, dv)))
        {
            Unpay(o);
            return;
        }
        Refund(Mathf.Max(0f, -dm), Mathf.Max(0f, -dv));
        o.progress *= t / Mathf.Max(1f, o.batchTicks);
        (o.batchTicks, o.paidMass, o.paidValue, o.paidQuality) = (t, m, v, o.Quality);
    }

    /// Почему задание не может начать штуку; начатое доделывается (сверх цели Trim вернул в баки), если у оператора есть линк. Как гестатор:
    /// механоида без свободной пропускной способности не начинают, а начатый стоит, пока её не хватает (её заняли уже во время печати).
    string Blocked(Order o)
    {
        Pattern p = o.pattern;
        bool noLink = Operator is { } op && !Links.Has(op, Links.Of(p));
        if (o.paid)
        {
            if (noLink) return "RM_StNoLink";
            if (p.resurrect && !MechanitorOk(o.mechanitor)) return "RM_StNoMechanitor";
            return p.IsMech && NoBandwidth(o.mechanitor, p, o.Corpse, true) ? "RM_StNoBandwidth" : null;
        }
        if (!Rules.Allowed(p)) return "RM_StForbidden";
        if (Rules.MissingResearch(p) != null) return "RM_StNoResearch";
        if (p.IsMech && !MechanitorOk(o.mechanitor)) return "RM_StNoMechanitor";
        // мех напечатан, но не вышел (не хватает пропускной способности или механитора) — не «готово»
        if (Remaining(o) <= 0) return p.mechKind != null && waiting.Any(w => w.kind == p.mechKind) ? "RM_StMechWaits" : "RM_StDone";
        if (noLink) return "RM_StNoLink";
        if (p.resurrect && o.Corpse == null) return "RM_StNoCorpse";
        if (p.IsMech && NoBandwidth(o.mechanitor, p, o.Corpse)) return "RM_StNoBandwidth";
        if (Feeder && Room(p) <= 0) return "RM_StNoRoom";
        return null;
    }

    static bool MechanitorOk(Pawn p) => p is { Dead: false } && MechanitorUtility.IsMechanitor(p);

    /// Механоид не влезает в пропускную способность механитора; started — уже начат, она занята им самим.
    public static bool NoBandwidth(Pawn mechanitor, Pattern p, Corpse c, bool started = false) =>
        mechanitor?.mechanitor is { } m && m.UsedBandwidth + (started ? 0 : (int)Pricing.Bandwidth(p, c)) > m.TotalBandwidth;

    /// Сколько ещё нужно, с начатой пачкой: «сделать X» — само количество (убывает с каждой готовой пачкой), «повторять до X» — до цели
    /// на складе. «Бесконечно» — без предела.
    public int Need(Order o) => o.mode switch
    {
        OrderMode.Make => o.target,
        OrderMode.Until => o.target - Stock(o),
        _ => int.MaxValue,
    };

    /// Сколько штук ещё начать (без уже оплаченной пачки).
    public int Remaining(Order o) => Need(o) - (o.paid ? o.batch : 0);

    /// Задание ещё печатать: не на паузе и не готово — так его считает осмотр.
    public static bool Pending(Order o) => o.status is not ("RM_StSuspended" or "RM_StDone" or "RM_StMechWaits");

    /// Пачка — до стопки ванили за раз (осталось меньше — всё), сколько хватает баков; мех и штучное — по одному.
    bool TryPay(Order o)
    {
        float m = o.UnitMass, v = o.UnitValue;
        int n = Mathf.Min(o.Stack, Remaining(o));
        if (Feeder) n = Mathf.Min(n, Room(o.pattern));
        if (m > 0f) n = Mathf.Min(n, Mathf.FloorToInt(Tanks.Mass(Net) / m));
        if (v > 0f) n = Mathf.Min(n, Mathf.FloorToInt(Tanks.Value(Net) / v));
        if (n > 1 && !Tanks.CanTake(Net, n * m, n * v, out _)) n--; // округление: 75 × 1,3 чуть больше 97,5
        if (n < 1 || !Tanks.TryTake(Net, n * m, n * v))
        {
            Tanks.CanTake(Net, m, v, out bool massShort);
            o.status = massShort ? "RM_StLowMass" : "RM_StLowSilver";
            if (!o.warned)
                Messages.Message("RM_MsgShort".Translate(RenamableLabel, o.pattern.Label, o.status.Translate()), parent, MessageTypeDefOf.CautionInput, false);
            o.warned = true;
            return false;
        }
        o.warned = false;
        o.paid = true;
        o.batch = n;
        o.paidMass = n * m;
        o.paidValue = n * v;
        o.paidQuality = o.Quality;
        o.progress = 0f;
        o.batchTicks = n * o.UnitTicks;
        return true;
    }

    /// Готово: опыт линку оператора — стоимость напечатанного.
    void Complete(Order o)
    {
        o.paid = o.running = false;
        o.progress = 0f;
        if (o.mode == OrderMode.Make) o.target = Mathf.Max(0, o.target - o.batch);
        Links.Gain(Operator, Links.Of(o.pattern), o.paidValue);
        RMDefOf.RM_Replicate.PlayOneShot(new TargetInfo(parent.Position, parent.Map));
        if (o.pattern.resurrect)
        {
            Resurrect(o);
            return;
        }
        if (o.pattern.IsBio)
        {
            Bio.Hatch(o, parent);
            return;
        }
        if (o.pattern.IsMech)
        {
            var w = new WaitingMech { kind = o.pattern.mechKind, mechanitor = o.mechanitor, group = o.group, mass = o.paidMass, value = o.paidValue };
            if (TrySpawnMech(w)) return;
            waiting.Add(w);
            Find.LetterStack.ReceiveLetter("RM_LetterMechWaitLabel".Translate(w.kind.label),
                "RM_LetterMechWait".Translate(w.kind.label, RenamableLabel, w.mechanitor.LabelShort), LetterDefOf.NeutralEvent, parent);
            return;
        }
        Thing t = o.pattern.Make(o.paidQuality, ArtGenerationContext.Colony);
        t.stackCount = o.batch;
        Place(t);
    }

    /// Как гестатор ванили: механоид встаёт с половиной энергии, без ран, к механитору задания.
    void Resurrect(Order o)
    {
        Pawn mech = o.Corpse.InnerPawn;
        ResurrectionUtility.TryResurrect(mech, new ResurrectionParams { dontSpawn = true });
        mech.needs.energy.CurLevel = mech.needs.energy.MaxLevel * 0.5f;
        mech.health.RemoveAllHediffs();
        if (mech.IsWorldPawn()) Find.WorldPawns.RemovePawn(mech);
        GenPlace.TryPlaceThing(mech, parent.InteractionCell, parent.Map, ThingPlaceMode.Near);
        Connect(mech, o.mechanitor, o.group, "RM_MsgMechResurrected");
    }

    /// Механоид — к механитору, в его группу; сообщение сверху слева.
    static void Connect(Pawn mech, Pawn m, int group, string key)
    {
        m.relations.AddDirectRelation(PawnRelationDefOf.Overseer, mech);
        List<MechanitorControlGroup> groups = m.mechanitor.controlGroups;
        MechanitorControlGroup g = groups.Count == 0 ? null : groups[Mathf.Clamp(group, 1, groups.Count) - 1];
        g?.Assign(mech);
        Messages.Message(RMUI.Tr(key, mech.LabelShort, m.LabelShort, g?.Index ?? 1), mech, MessageTypeDefOf.PositiveEvent);
    }

    void Place(Thing t)
    {
        Map map = parent.Map;
        if (!Feeder)
        {
            GenPlace.TryPlaceThing(t, parent.InteractionCell, map, ThingPlaceMode.Near);
            return;
        }
        Thing stack = parent.Position.GetThingList(map).FirstOrDefault(x => x.CanStackWith(t) && x.stackCount < x.def.stackLimit);
        if (stack == null || !stack.TryAbsorbStack(t, true)) GenSpawn.Spawn(t, parent.Position, map);
    }

    /// Сколько штук влезет в репликатор для животных: в начатые стопки и в свободные места.
    int Room(Pattern p)
    {
        List<Thing> items = parent.Position.GetThingList(parent.Map).Where(x => x.def.category == ThingCategory.Item).ToList();
        return items.Where(x => Matches(x, p)).Sum(x => x.def.stackLimit - x.stackCount)
               + Mathf.Max(0, parent.def.building.maxItemsInCell - items.Count) * p.def.stackLimit;
    }

    public static bool Matches(Thing t, Pattern p)
    {
        Thing i = t.GetInnerIfMinified();
        return i.def == p.def && i.Stuff == p.stuff && t is MinifiedThing == p.minified;
    }

    /// Считается в «есть»: лежит на складе или у репликатора (только что напечатано).
    public bool Counted(Thing t) => t.Spawned && (t.IsInAnyStorage() || t.Position.InHorDistOf(parent.InteractionCell, 3f));

    /// Склад: хранилища, только что напечатанное у репликатора, руки носильщиков (кроме несущих в расщепитель).
    /// У репликатора для животных — только он сам.
    public int Stock(Order o)
    {
        Map map = parent.Map;
        Pattern p = o.pattern;
        if (map == null || p.resurrect) return 0;
        // животные — колонии на карте этого вида и пола
        if (p.IsAnimal) return map.mapPawns.SpawnedColonyAnimals.Count(a => a.kindDef == p.animal && (!o.Gendered || a.gender == o.gender));
        // механоиды — колонии на карте, как считает ваниль, и напечатанные, что ждут механитора
        if (p.IsMech) return map.mapPawns.SpawnedColonyMechs.Count(m => m.def == p.def) + waiting.Count(w => w.kind == p.mechKind);
        if (Feeder) return parent.Position.GetThingList(map).Where(x => Matches(x, p)).Sum(x => x.stackCount);
        int n = 0;
        foreach (Thing t in map.listerThings.ThingsOfDef(p.minified ? ThingDefOf.MinifiedThing : p.def))
            if (Matches(t, p) && Counted(t)) n += t.stackCount;
        foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned)
            if (pawn.carryTracker.CarriedThing is { } c && Matches(c, p) && pawn.CurJobDef != RMDefOf.RM_Carry) n += c.stackCount;
        return n;
    }

    /// Сколько штук сверх цели задания можно нести в расщепитель: то, за чем носильщики уже идут, — не в счёт.
    public int Surplus(Order o) => o.surplus && o.CanSurplus ? Stock(o) - o.target - JobDriver_RMCarry.Incoming(parent.Map, o.pattern) : 0;

    // ---- мехи ----

    /// Пропускная способность занята со старта печати меха и пока он ждёт.
    public static int Reserved(Pawn mechanitor) => All.Sum(c =>
        c.orders.Where(o => o.paid && o.pattern?.IsMech == true && o.mechanitor == mechanitor).Sum(o => (int)Pricing.Bandwidth(o.pattern, o.Corpse))
        + c.waiting.Where(w => w.mechanitor == mechanitor).Sum(w => w.Bandwidth));

    static int ReservedWaiting(Pawn mechanitor) => All.Sum(c => c.waiting.Where(w => w.mechanitor == mechanitor).Sum(w => w.Bandwidth));

    bool TrySpawnMech(WaitingMech w)
    {
        Pawn m = w.mechanitor;
        if (!parent.Spawned || !MechanitorOk(m)) return false;
        int used = m.mechanitor.UsedBandwidth - ReservedWaiting(m);
        if (used + w.Bandwidth > m.mechanitor.TotalBandwidth) return false;
        Pawn mech = PawnGenerator.GeneratePawn(new PawnGenerationRequest(w.kind, Faction.OfPlayer, forceGenerateNewPawn: true,
            developmentalStages: DevelopmentalStage.Newborn));
        GenPlace.TryPlaceThing(mech, parent.InteractionCell, parent.Map, ThingPlaceMode.Near);
        Connect(mech, m, w.group, "RM_MsgMechPrinted");
        return true;
    }

    public void Refund(float mass, float value) => Tanks.Add(Net, mass, value);

    /// Ждущий механоид отменён: материя — обратно в баки, пропускная способность механитора свободна.
    public void CancelWaiting(int i)
    {
        Refund(waiting[i].mass, waiting[i].value);
        waiting.RemoveAt(i);
    }

    /// Правка задания игроком: начатая пачка — в баки, печать с нуля (оплатит заново Refresh).
    public void Restart(Order o)
    {
        if (o.paid) Unpay(o);
    }

    /// Оплаченная пачка — обратно в баки, задание ждёт оплаты заново.
    void Unpay(Order o)
    {
        Refund(o.paidMass, o.paidValue);
        o.paid = o.running = false;
        o.progress = 0f;
    }

    /// Удаление задания: недопечатанная пачка возвращается в баки.
    public void Remove(Order o)
    {
        if (o.paid) Unpay(o);
        orders.Remove(o);
    }

    /// Что осталось напечатать. Время — только того, что может печататься (2026-10-21): у задания без помехи — начатая пачка
    /// и остальные штуки, если баки оплатят одну сейчас; масса и стоимость — всех заданий в очереди (по ним видно, чего не хватает).
    public (float ticks, float mass, float value) Totals()
    {
        float t = 0f, m = 0f, v = 0f;
        foreach (Order o in orders)
        {
            if (o.pattern == null || o.paused) continue;
            bool go = o.status is null or "RM_StWait";
            if (o.paid && go) t += Mathf.Max(0f, o.batchTicks - o.progress);
            if (o.mode == OrderMode.Forever || !Rules.CanPrint(o.pattern)) continue;
            int n = Mathf.Max(0, Remaining(o));
            if (n == 0) continue;
            if (go && Tanks.CanTake(Net, o.UnitMass, o.UnitValue, out _)) t += n * o.UnitTicks;
            m += n * o.UnitMass;
            v += n * o.UnitValue;
        }
        return (t, m, v);
    }

    public string StatusLabel(Order o) => o.running ? Fmt.Time(Mathf.Max(0f, o.batchTicks - o.progress)) : (o.status ?? "RM_StWait").Translate();

    public override string CompInspectStringExtra()
    {
        var sb = new StringBuilder();
        if (Incubator && !Rules.BioOn) sb.AppendLine("RM_BioOff".Translate());
        if (parent.Spawned)
        {
            if (Tanks.All(Net).Count == 0) sb.AppendLine("RM_NoTanks".Translate());
            if (!Tanks.HasComputer(Net)) sb.AppendLine("RM_NoComputer".Translate());
            else if (Operator == null) sb.AppendLine("RM_StNoOperatorFull".Translate());
        }
        int food = Feeder && parent.Spawned ? parent.Position.GetThingList(parent.Map).Where(x => x.def.category == ThingCategory.Item).Sum(x => x.stackCount) : 0;
        if (food > 0) sb.AppendLine("RM_FeederFood".Translate(food));
        if (Corpse is { } c) sb.AppendLine(RMUI.Tr("RM_HeldCorpse", c.InnerPawn.LabelShort));
        if (Running is { } r) sb.AppendLine(RMUI.Tr("RM_Printing", r.BatchLabel, StatusLabel(r)));
        int pending = orders.Count(Pending);
        if (pending > 0) sb.AppendLine(RMUI.Tr("RM_QueueLeft", pending, Fmt.Time(Totals().ticks)));
        foreach (WaitingMech w in waiting) sb.AppendLine("RM_WaitsMechanitor".Translate(w.kind.label));
        return sb.ToString().TrimEndNewlines();
    }

    public override IEnumerable<Gizmo> CompGetGizmosExtra()
    {
        yield return new Command_Action
        {
            defaultLabel = "RM_Rename".Translate(),
            icon = TexButton.Rename,
            action = () => Find.WindowStack.Add(new Dialog_RenamePrinter(this)),
        };
        if (DebugSettings.ShowDevGizmos && Running is { } r) yield return new Command_Action { defaultLabel = "DEV: Finish print", action = () => Edit.FinishPrint(this, r) };
    }

    public override void PostDraw()
    {
        if (Incubator) Bio.DrawGrowing(this);
        Glow.Printer(this);
    }

    /// Названия до 2026-10-20 (английское и русское): вид — в скобках после «репликатор».
    static readonly Dictionary<string, string[]> OldLabels = new()
    {
        ["RM_ReplicatorWall"] = new[] { "Replicator (Wall)", "Репликатор (Настенный)" },
        ["RM_Feeder"] = new[] { "Replicator (Animals)", "Репликатор (Животные)" },
    };

    public override void PostExposeData()
    {
        Scribe_Values.Look(ref name, "name");
        Scribe_Collections.Look(ref orders, "orders", LookMode.Deep);
        Scribe_Collections.Look(ref waiting, "waiting", LookMode.Deep);
        Scribe_Deep.Look(ref held, "held", this);
        if (Scribe.mode != LoadSaveMode.PostLoadInit) return;
        // старое имя по умолчанию — «Репликатор (Настенный) 1» — по новому названию: «Настенный репликатор 1» (2026-10-20)
        if (name != null && OldLabels.TryGetValue(parent.def.defName, out string[] olds))
            foreach (string l in olds)
                if (name.StartsWith(l + " ") && int.TryParse(name.Substring(l.Length + 1), out _))
                    name = BaseLabel + name.Substring(l.Length);
        orders ??= new List<Order>();
        waiting ??= new List<WaitingMech>();
        held ??= new ThingOwner<Thing>(this);
        orders.RemoveAll(o => o.pattern == null);
        waiting.RemoveAll(w => w.kind == null);
        orders = orders.OrderBy(o => !o.legacyFirst).ToList();
        foreach (Order o in orders) o.printer = this;
    }
}

public class Dialog_RenamePrinter : Dialog_Rename<CompPrinter>
{
    public Dialog_RenamePrinter(CompPrinter printer) : base(printer) { }
}
