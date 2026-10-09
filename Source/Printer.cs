using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace ReplimatMech;

public enum OrderMode { Make, Until, Forever }

/// Строка очереди. Печать идёт пачками (batch штук); цена пачки списывается при её старте, отмена возвращает ровно списанное,
/// правка задания пересчитывает её (CompPrinter.Reprice).
public class Order : IExposable
{
    public Pattern pattern;
    public OrderMode mode;
    public int target = 1, done;
    public bool manualQuality, important, paused;
    public QualityCategory quality;
    /// mechanitor — к кому подключится механоид; editor — кто последним создал или правил задание у консоли связи, его скорость — скорость задания.
    public Pawn mechanitor, editor;
    public int slider = -1, pct, batch = 1;
    public bool paid;
    public float paidMass, paidValue, progress, batchTicks;
    public QualityCategory paidQuality;
    public string status;
    public bool warned, running;
    string patternKey;

    static Settings S => ReplimatMechMod.S;

    /// Ползунки ±40%: 0 — время, 1 — стоимость, зеркально (дольше — дешевле). Значение = выигрыш в %.
    /// У шаблона без стоимости (каменные блоки) второй — масса, в обе стороны: расщепитель возвращает такие вещи
    /// на 1 − Cap/100 массы — не больше, чем самая дешёвая печать, так что масса из ничего не появляется.
    public const int Cap = 40;

    /// Предел количества — как у заданий ванили: дальше она пишет «бесконечно».
    public const int MaxTarget = 999999;

    public bool MassSlider => Pricing.Value(pattern, Quality) <= 0f;

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

    public float UnitValue => Pricing.Value(pattern, Quality) * (MassSlider ? 1f : K(1));
    public float UnitMass => Pricing.Mass(pattern) * (MassSlider ? K(1) : 1f);
    public float UnitTicks => Pricing.Ticks(pattern) / Pricing.Speed(editor) * K(0);

    /// Стопка ванили: столько печатается за раз и на столько меняют количество кнопки; мех — по одному.
    public int Stack => pattern.IsMech ? 1 : pattern.def.stackLimit;

    /// Что печатается сейчас — как стопка в ванили: «блоки гранита x75».
    public string BatchLabel => pattern.LabelWithQuality(paidQuality) + (batch > 1 ? " x" + batch : "");

    /// Копия для формы «Шаблонов»: правится копия, «Применить» переносит только настройки — оплаченную пачку по ним пересчитает Refresh.
    public Order Copy() => (Order)MemberwiseClone();

    /// Количество 0 — задание на паузе; ушло с нуля — снова печатает.
    public void SetTarget(int v)
    {
        bool wasZero = target == 0;
        target = Mathf.Clamp(v, 0, MaxTarget);
        if (target == 0) paused = true;
        else if (wasZero) paused = false;
    }

    public void ApplySettings(Order o)
    {
        mode = o.mode;
        SetTarget(o.target);
        manualQuality = o.manualQuality;
        quality = o.quality;
        important = o.important;
        mechanitor = o.mechanitor;
        editor = o.editor;
        slider = o.slider;
        pct = o.pct;
    }

    public bool SameSettings(Order o) => mode == o.mode && target == o.target && manualQuality == o.manualQuality && quality == o.quality
                                         && important == o.important && mechanitor == o.mechanitor && slider == o.slider && pct == o.pct;

    public void ExposeData()
    {
        if (Scribe.mode == LoadSaveMode.Saving) patternKey = pattern?.Key;
        Scribe_Values.Look(ref patternKey, "pattern");
        Scribe_Values.Look(ref mode, "mode");
        Scribe_Values.Look(ref target, "target", 1);
        Scribe_Values.Look(ref done, "done");
        Scribe_Values.Look(ref manualQuality, "manualQuality");
        Scribe_Values.Look(ref important, "important");
        Scribe_Values.Look(ref paused, "paused");
        Scribe_Values.Look(ref quality, "quality");
        Scribe_References.Look(ref mechanitor, "mechanitor");
        Scribe_References.Look(ref editor, "editor");
        Scribe_Values.Look(ref slider, "slider", -1);
        Scribe_Values.Look(ref pct, "pct");
        Scribe_Values.Look(ref paid, "paid");
        Scribe_Values.Look(ref paidMass, "paidMass");
        Scribe_Values.Look(ref paidValue, "paidValue");
        Scribe_Values.Look(ref progress, "progress");
        Scribe_Values.Look(ref batchTicks, "unitTicks");
        Scribe_Values.Look(ref batch, "batch", 1);
        Scribe_Values.Look(ref paidQuality, "paidQuality");
        if (Scribe.mode != LoadSaveMode.PostLoadInit) return;
        pattern = GameComponent_RM.Get.ByKey(patternKey);
        if (slider > 1) slider = -1; // старые сохранения: ползунок массы 0.2.2
    }
}

/// Готовый мех ждёт пропускной способности механитора; списанное держится до выхода или отмены.
public class WaitingMech : IExposable
{
    public PawnKindDef kind;
    public Pawn mechanitor;
    public float mass, value;

    public int Bandwidth => (int)kind.race.GetStatValueAbstract(StatDefOf.BandwidthCost);

    public void ExposeData()
    {
        Scribe_Defs.Look(ref kind, "kind");
        Scribe_References.Look(ref mechanitor, "mechanitor");
        Scribe_Values.Look(ref mass, "mass");
        Scribe_Values.Look(ref value, "value");
    }
}

public class CompProperties_Printer : CompProperties
{
    public bool feeder;
    public CompProperties_Printer() => compClass = typeof(CompPrinter);
}

/// Очередь печати живёт на самом репликаторе (и для животных): переживает потерю компьютера и упаковку.
public class CompPrinter : ThingComp, IRenameable
{
    public static readonly HashSet<CompPrinter> All = new();
    public List<Order> orders = new();
    public List<WaitingMech> waiting = new();
    public string name;
    CompPowerTrader power;

    public bool Feeder => ((CompProperties_Printer)props).feeder;
    public PowerNet Net => power?.PowerNet;
    public IEnumerable<Order> Ordered => orders.Where(o => o.important).Concat(orders.Where(o => !o.important));
    public Order Running => orders.FirstOrDefault(o => o.running);
    public bool CanWork => parent.Spawned && power.PowerOn && Tanks.HasComputer(Net);

    public string RenamableLabel { get => name.NullOrEmpty() ? BaseLabel : name; set => name = value; }
    public string BaseLabel => parent.def.LabelCap;
    public string InspectLabel => RenamableLabel;

    public static CompPrinter QueuedIn(ThingDef def, bool minified = false) =>
        All.FirstOrDefault(c => c.orders.Any(o => o.pattern is { IsMech: false } p && p.def == def && p.minified == minified));

    public static IEnumerable<ThingDef> QueuedDefs() =>
        All.SelectMany(c => c.orders).Where(o => o.pattern is { IsMech: false, minified: false }).Select(o => o.pattern.def).Distinct();

    public bool Accepts(Pattern p) => Feeder ? !p.IsMech && Rules.FeederFood(p.def) : true;

    public override void PostSpawnSetup(bool respawningAfterLoad)
    {
        power = parent.GetComp<CompPowerTrader>();
        All.Add(this);
        if (name.NullOrEmpty()) name = BaseLabel + " " + All.Count(c => c.parent.def == parent.def);
    }

    public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish) => All.Remove(this);

    public override void CompTick()
    {
        if (parent.IsHashIntervalTick(60)) Work(60);
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

    /// Статусы строк и какая печатает: первая не заблокированная (оплачивает пачку, если хватает баков). Зовут и окна: компьютер
    /// ставит игру на паузу — без этого правки очереди (новое задание, количество, пауза) были бы видны только после закрытия окна.
    public Order Refresh()
    {
        bool can = CanWork;
        Order run = null;
        foreach (Order o in Ordered.ToList())
        {
            o.running = false;
            if (o.paid) Reprice(o);
            o.status = o.paused ? "RM_StSuspended" : Blocked(o);
            if (o.status != null) continue;
            if (run != null || !can) o.status = can ? "RM_StWait" : "RM_StOff";
            else if (o.paid || TryPay(o)) run = o;
        }
        if (run != null) run.running = true;
        return run;
    }

    /// Оплаченная пачка — по текущим настройкам задания и скорости того, кто его правил: время — с той же долей готовности, разница массы
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

    /// Почему строка не может начать штуку; начатая доделывается всегда.
    string Blocked(Order o)
    {
        if (o.paid) return null;
        if (!Rules.CanPrint(o.pattern)) return "RM_StForbidden";
        if (o.pattern.IsMech && !MechanitorOk(o.mechanitor)) return "RM_StNoMechanitor";
        if (Remaining(o) <= 0) return "RM_StDone";
        if (Feeder && Room(o.pattern) <= 0) return "RM_StNoRoom";
        return null;
    }

    static bool MechanitorOk(Pawn p) => p is { Dead: false } && MechanitorUtility.IsMechanitor(p);

    /// Сколько штук ещё начать (без уже оплаченной пачки). «Бесконечно» — без предела.
    public int Remaining(Order o) => o.mode switch
    {
        OrderMode.Make => o.target - o.done - (o.paid ? o.batch : 0),
        OrderMode.Until => o.target - Stock(o) - (o.paid ? o.batch : 0),
        _ => int.MaxValue,
    };

    /// «Есть» для строки: сделано (сделать X) или на складе (повторять до X).
    public int Have(Order o) => o.mode == OrderMode.Until ? Stock(o) : o.done;

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

    void Complete(Order o)
    {
        o.paid = o.running = false;
        o.progress = 0f;
        o.done += o.batch;
        RMDefOf.RM_Replicate.PlayOneShot(new TargetInfo(parent.Position, parent.Map));
        if (o.pattern.IsMech)
        {
            var w = new WaitingMech { kind = o.pattern.mechKind, mechanitor = o.mechanitor, mass = o.paidMass, value = o.paidValue };
            if (TrySpawnMech(w)) return;
            waiting.Add(w);
            Find.LetterStack.ReceiveLetter("RM_LetterMechWaitLabel".Translate(w.kind.label),
                "RM_LetterMechWait".Translate(w.kind.label, RenamableLabel, w.mechanitor.LabelShort), LetterDefOf.NeutralEvent, parent);
            return;
        }
        Thing t = o.pattern.Make(o.paidQuality);
        t.stackCount = o.batch;
        Place(t);
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

    static bool Matches(Thing t, Pattern p)
    {
        Thing i = t.GetInnerIfMinified();
        return i.def == p.def && i.Stuff == p.stuff && t is MinifiedThing == p.minified;
    }

    /// Склад: хранилища, только что напечатанное у репликатора, руки носильщиков. У репликатора для животных — только он сам.
    public int Stock(Order o)
    {
        Map map = parent.Map;
        if (map == null) return 0;
        Pattern p = o.pattern;
        if (Feeder) return parent.Position.GetThingList(map).Where(x => Matches(x, p)).Sum(x => x.stackCount);
        int n = 0;
        foreach (Thing t in map.listerThings.ThingsOfDef(p.minified ? ThingDefOf.MinifiedThing : p.def))
            if (Matches(t, p) && (t.IsInAnyStorage() || t.Position.InHorDistOf(parent.InteractionCell, 3f))) n += t.stackCount;
        foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned)
            if (pawn.carryTracker.CarriedThing is { } c && Matches(c, p)) n += c.stackCount;
        return n;
    }

    // ---- мехи ----

    /// Пропускная способность занята со старта печати меха и пока он ждёт.
    public static int Reserved(Pawn mechanitor) => All.Sum(c =>
        c.orders.Where(o => o.paid && o.pattern?.IsMech == true && o.mechanitor == mechanitor).Sum(o => (int)Pricing.Bandwidth(o.pattern))
        + c.waiting.Where(w => w.mechanitor == mechanitor).Sum(w => w.Bandwidth));

    static int ReservedWaiting(Pawn mechanitor) => All.Sum(c => c.waiting.Where(w => w.mechanitor == mechanitor).Sum(w => w.Bandwidth));

    bool TrySpawnMech(WaitingMech w)
    {
        Pawn m = w.mechanitor;
        if (!parent.Spawned || !MechanitorOk(m)) return false;
        bool inList = waiting.Contains(w);
        int used = m.mechanitor.UsedBandwidth - ReservedWaiting(m);
        if (used + w.Bandwidth > m.mechanitor.TotalBandwidth) return false;
        Pawn mech = PawnGenerator.GeneratePawn(new PawnGenerationRequest(w.kind, Faction.OfPlayer, forceGenerateNewPawn: true,
            developmentalStages: DevelopmentalStage.Newborn));
        GenPlace.TryPlaceThing(mech, parent.InteractionCell, parent.Map, ThingPlaceMode.Near);
        m.relations.AddDirectRelation(PawnRelationDefOf.Overseer, mech);
        if (inList) Messages.Message("RM_MsgMechReady".Translate(mech.LabelShort, m.LabelShort), mech, MessageTypeDefOf.PositiveEvent);
        return true;
    }

    public void Refund(float mass, float value) => Tanks.Add(Net, mass, value);

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

    /// Строка на другой репликатор: начатая штука отменяется с возвратом, как при удалении.
    public void MoveTo(Order o, CompPrinter to)
    {
        Remove(o);
        to.orders.Add(o);
    }

    /// Что осталось напечатать: время, масса, стоимость; important — только первая (true) или вторая (false) очередь.
    public (float ticks, float mass, float value) Totals(bool? important = null)
    {
        float t = 0f, m = 0f, v = 0f;
        foreach (Order o in orders)
        {
            if (o.pattern == null || o.paused || important != null && o.important != important) continue;
            if (o.paid) t += Mathf.Max(0f, o.batchTicks - o.progress);
            if (o.mode == OrderMode.Forever || !Rules.CanPrint(o.pattern)) continue;
            int n = Mathf.Max(0, Remaining(o));
            if (n == 0) continue;
            t += n * o.UnitTicks;
            m += n * o.UnitMass;
            v += n * o.UnitValue;
        }
        return (t, m, v);
    }

    public string StatusLabel(Order o) => o.running ? Fmt.Time(Mathf.Max(0f, o.batchTicks - o.progress)) : (o.status ?? "RM_StWait").Translate();

    public override string CompInspectStringExtra()
    {
        var sb = new StringBuilder();
        if (parent.Spawned)
        {
            if (Tanks.All(Net).Count == 0) sb.AppendLine("RM_NoTanks".Translate());
            if (!Tanks.HasComputer(Net)) sb.AppendLine("RM_NoComputer".Translate());
        }
        int food = Feeder && parent.Spawned ? parent.Position.GetThingList(parent.Map).Where(x => x.def.category == ThingCategory.Item).Sum(x => x.stackCount) : 0;
        if (food > 0) sb.AppendLine("RM_FeederFood".Translate(food));
        if (Running is { } r) sb.AppendLine("RM_Printing".Translate(r.BatchLabel, StatusLabel(r)));
        if (orders.Count > 0) sb.AppendLine("RM_InQueue".Translate(orders.Count));
        float ticks = Totals().ticks;
        if (ticks > 0f) sb.AppendLine("RM_QueueEnd".Translate(Fmt.Time(ticks)));
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
        foreach (WaitingMech w in waiting)
        {
            yield return new Command_Action
            {
                defaultLabel = "RM_Reassign".Translate(w.kind.label),
                icon = w.kind.race.uiIcon,
                action = () => Find.WindowStack.Add(new FloatMenu(Find.Maps.SelectMany(m => m.mapPawns.FreeColonists)
                    .Where(MechanitorOk).Select(p => new FloatMenuOption(p.LabelShort, () => w.mechanitor = p)).ToList())),
            };
            yield return new Command_Action
            {
                defaultLabel = "RM_CancelMech".Translate(w.kind.label),
                icon = TexCommand.ClearPrioritizedWork,
                action = () =>
                {
                    Refund(w.mass, w.value);
                    waiting.Remove(w);
                },
            };
        }
    }

    public override void PostDraw() => Glow.Printer(this);

    public override void PostExposeData()
    {
        Scribe_Values.Look(ref name, "name");
        Scribe_Collections.Look(ref orders, "orders", LookMode.Deep);
        Scribe_Collections.Look(ref waiting, "waiting", LookMode.Deep);
        if (Scribe.mode == LoadSaveMode.PostLoadInit)
        {
            orders ??= new List<Order>();
            waiting ??= new List<WaitingMech>();
            orders.RemoveAll(o => o.pattern == null);
            waiting.RemoveAll(w => w.kind == null);
        }
    }
}

public class Dialog_RenamePrinter : Dialog_Rename<CompPrinter>
{
    public Dialog_RenamePrinter(CompPrinter printer) : base(printer) { }
}
