using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace ReplimatMech;

/// Расщепитель материи: мгновенно разбирает принесённые вещи и трупы. Никогда не стоит, кроме «Нет баков».
/// Пока идёт разборка — светится в цвет реплимата (свечение + свет).
public class Building_RMHopper : Building_Storage, IThingGlower
{
    public static readonly HashSet<Building_RMHopper> Spawned_ = new();
    const int GlowTime = 120;
    CompPowerTrader power;
    int glowTicks;
    HashSet<ThingDef> suppressed = new();

    public PowerNet Net => power?.PowerNet;
    public bool Working => Spawned && power.PowerOn && Tanks.All(Net).Count > 0;
    public int GlowTicks => glowTicks;
    public bool ShouldBeLitNow() => glowTicks > 0;

    /// Свет — в цвет реплимата; цвет ставится, пока свет выключен, без лишней перестройки освещения.
    void SetGlow(int ticks)
    {
        bool was = glowTicks > 0, now = ticks > 0;
        CompGlower g = was != now && Spawned ? GetComp<CompGlower>() : null;
        if (now && g != null) g.GlowColor = new ColorInt(Settings.GlowColor) { a = 0 };
        glowTicks = ticks;
        g?.UpdateLit(Map);
    }

    public override void SpawnSetup(Map map, bool respawningAfterLoad)
    {
        base.SpawnSetup(map, respawningAfterLoad);
        power = GetComp<CompPowerTrader>();
        Spawned_.Add(this);
        // при загрузке репликаторы ещё не все на карте — сверка в GameComponent_RM.FinalizeInit
        if (!respawningAfterLoad) SyncQueued();
    }

    public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
    {
        Spawned_.Remove(this);
        base.DeSpawn(mode);
    }

    /// Правила приёма поверх фильтра (патч Building_Storage.Accepts). Какие трупы — решает фильтр, живого в нём нет (Startup).
    public bool Allows(Thing t) => Working && CompPrinter.QueuedIn(t.GetInnerIfMinified().def, t is MinifiedThing) == null;

    public static void SyncAll()
    {
        foreach (Building_RMHopper h in Spawned_) h.SyncQueued();
    }

    /// То, что стоит в очереди, расщепитель не берёт: снять галку и запомнить. Задание ушло — галка возвращается.
    public void SyncQueued()
    {
        HashSet<ThingDef> queued = CompPrinter.QueuedDefs().ToHashSet();
        foreach (ThingDef d in queued)
            if (settings.filter.Allows(d))
            {
                settings.filter.SetAllow(d, false);
                suppressed.Add(d);
            }
        foreach (ThingDef d in suppressed.Where(d => !queued.Contains(d)).ToList())
        {
            suppressed.Remove(d);
            settings.filter.SetAllow(d, true);
        }
    }

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Collections.Look(ref suppressed, "suppressed", LookMode.Def);
        suppressed ??= new HashSet<ThingDef>();
    }

    protected override void Tick()
    {
        base.Tick();
        if (glowTicks > 0) SetGlow(glowTicks - 1);
        if (this.IsHashIntervalTick(250)) SyncQueued();
        if (!this.IsHashIntervalTick(15) || !Working) return;
        bool any = false;
        foreach (IntVec3 c in AllSlotCells())
            foreach (Thing t in c.GetThingList(Map).ToList())
                if (t.def.category == ThingCategory.Item && Accepts(t))
                {
                    Process(t);
                    any = true;
                }
        if (any) RMDefOf.RM_Dematerialize.PlayOneShot(new TargetInfo(Position, Map));
    }

    public void Process(Thing t)
    {
        if (t is Corpse c) Demat.Corpse(c, Net);
        else Demat.Item(t, Net);
        SetGlow(GlowTime);
    }

    protected override void DrawAt(Vector3 drawLoc, bool flip = false)
    {
        base.DrawAt(drawLoc, flip);
        Glow.Hopper(this);
    }

    public override string GetInspectString()
    {
        string s = base.GetInspectString();
        if (Spawned && Tanks.All(Net).Count == 0) s = "RM_NoTanks".Translate() + "\n" + s;
        return s.Trim();
    }
}

/// Репликатор для животных (кормушка): корм только печатается в неё. Носильщики сюда не носят и отсюда не уносят
/// (принимает лишь то, что уже лежит в ней, приоритет «Критический»), вкладки и кнопок склада нет.
[StaticConstructorOnStartup]
public class Building_RMFeeder : Building_Storage
{
    static readonly HashSet<Texture> StorageIcons = new[] { "CopySettings", "PasteSettings", "LinkStorageSettings", "UnlinkStorageSettings", "SelectAllLinked" }
        .Select(n => (Texture)ContentFinder<Texture2D>.Get("UI/Commands/" + n)).ToHashSet();

    public bool Holds(Thing t) => t.Spawned && t.Position == Position;

    public override IEnumerable<Gizmo> GetGizmos() => base.GetGizmos().Where(g => !(g is Command c && StorageIcons.Contains(c.icon)));
}

/// Компьютер: очереди и шаблоны, условие печати в своей сети. Связь — только через консоль связи.
public class Building_RMComputer : Building
{
    CompPowerTrader power;
    public bool collectValue = true;
    public bool Powered => power?.PowerOn == true;
    public PowerNet Net => power?.PowerNet;

    public override void SpawnSetup(Map map, bool respawningAfterLoad)
    {
        base.SpawnSetup(map, respawningAfterLoad);
        power = GetComp<CompPowerTrader>();
    }

    /// Пункты меню консоли связи: по строке на каждый компьютер карты, сразу после вызова дьявола.
    public static IEnumerable<FloatMenuOption> ConsoleOptions(Building_CommsConsole console, Pawn p)
    {
        List<Building_RMComputer> computers = console.Map.listerBuildings.AllBuildingsColonistOfClass<Building_RMComputer>().ToList();
        for (int i = 0; i < computers.Count; i++)
        {
            Building_RMComputer c = computers[i];
            string label = computers.Count == 1 ? "RM_UseComputer".Translate() : "RM_UseComputerN".Translate(i + 1);
            string why = ReplimatMechMod.S.needMechanitor && !MechanitorUtility.IsMechanitor(p) ? "RM_NeedMechanitor"
                : !c.Powered ? "RM_ComputerNoPower"
                : !p.CanReach(console, PathEndMode.InteractionCell, Danger.Some) ? "CannotUseNoPath" : null;
            // Иконка — компьютер: без своей меню ставит иконку консоли.
            yield return why != null
                ? new FloatMenuOption(label + " (" + why.Translate() + ")", null, c.def)
                : new FloatMenuOption(label, () => p.jobs.TryTakeOrderedJob(JobMaker.MakeJob(RMDefOf.RM_UseComputer, console, c), JobTag.Misc),
                    c.def, priority: MenuOptionPriority.SummonThreat, orderInPriority: -1);
        }
    }

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Values.Look(ref collectValue, "collectValue", true);
    }

    public override string GetInspectString()
    {
        var sb = new StringBuilder();
        if (Spawned)
        {
            List<CompPrinter> printers = CompPrinter.All.Where(c => c.Net == Net && Net != null).ToList();
            int tanks = Tanks.All(Net).Count, reps = printers.Count(c => !c.Feeder), feeders = printers.Count - reps;
            if (tanks == 0) sb.AppendLine("RM_NoTanks".Translate());
            else
            {
                sb.AppendLine("RM_MassLine".Translate(Fmt.MassNum(Tanks.Mass(Net)), Fmt.Mass(Tanks.Capacity(Net))));
                if (Tanks.Value(Net) > 0f) sb.AppendLine("RM_ValueLine".Translate(Fmt.Silver(Tanks.Value(Net))));
                sb.AppendLine("RM_TanksCount".Translate(tanks));
            }
            if (reps > 0) sb.AppendLine("RM_PrintersCount".Translate(reps));
            if (feeders > 0) sb.AppendLine("RM_FeedersCount".Translate(feeders));
            if (!collectValue) sb.AppendLine("RM_ValueOff".Translate());
        }
        sb.Append(base.GetInspectString());
        return sb.ToString().TrimEndNewlines();
    }

    protected override void DrawAt(Vector3 drawLoc, bool flip = false)
    {
        base.DrawAt(drawLoc, flip);
        if (Powered) Glow.Computer(this);
    }
}

/// Механитор идёт к консоли связи (A) и открывает окно компьютера (B).
public class JobDriver_RMUseComputer : JobDriver
{
    public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

    protected override IEnumerable<Toil> MakeNewToils()
    {
        this.FailOnDespawnedOrNull(TargetIndex.A);
        this.FailOnDespawnedOrNull(TargetIndex.B);
        yield return Toils_Goto.GotoCell(TargetIndex.A, PathEndMode.InteractionCell)
            .FailOn(() => !((Building_CommsConsole)TargetThingA).CanUseCommsNow);
        Toil open = ToilMaker.MakeToil("RM_OpenComputer");
        open.initAction = () => Find.WindowStack.Add(new Dialog_Computer((Building_RMComputer)TargetThingB, pawn));
        yield return open;
    }
}

// ---- сканирование ----

public class Designator_RMScan : Designator
{
    protected override DesignationDef Designation => RMDefOf.RM_Scan;
    public override DrawStyleCategoryDef DrawStyleCategory => DrawStyleCategoryDefOf.FilledRectangle;
    public override bool Visible => RMDefOf.RM_Replication.IsFinished && base.Visible;

    public Designator_RMScan()
    {
        defaultLabel = "RM_DesignatorScan".Translate();
        icon = ContentFinder<Texture2D>.Get("UI/Designators/ReplimatScanPattern");
        soundDragSustain = SoundDefOf.Designate_DragStandard;
        soundDragChanged = SoundDefOf.Designate_DragStandard_Changed;
        soundSucceeded = SoundDefOf.Designate_Haul;
        useMouseIcon = true;
    }

    public override AcceptanceReport CanDesignateCell(IntVec3 c)
    {
        if (!c.InBounds(Map) || c.Fogged(Map)) return false;
        Thing t = c.GetThingList(Map).FirstOrDefault(x => CanDesignateThing(x).Accepted);
        return t != null;
    }

    public override void DesignateSingleCell(IntVec3 c)
    {
        foreach (Thing t in c.GetThingList(Map).ToList())
            if (CanDesignateThing(t).Accepted) DesignateThing(t);
    }

    /// Сканировать — только то, чего нет в шаблонах, или вещь лучше шаблона по качеству. Кнопка есть и на самой вещи.
    public override AcceptanceReport CanDesignateThing(Thing t)
    {
        if (!RMDefOf.RM_Replication.IsFinished || t.def.category != ThingCategory.Item || !t.def.EverHaulable || t is Corpse
            || Rules.Living(t.GetInnerIfMinified().def) || Map.designationManager.DesignationOn(t, Designation) != null) return false;
        Pattern fresh = Pattern.From(t, out QualityCategory q);
        if (fresh == null) return false;
        Pattern known = GameComponent_RM.Get.ByKey(fresh.Key);
        return known == null || known.HasQuality && q > known.Best;
    }

    public override void DesignateThing(Thing t) => Map.designationManager.AddDesignation(new Designation(t, Designation));

    public override void SelectedUpdate() => GenUI.RenderMouseoverBracket();
}

public class WorkGiver_RMScan : WorkGiver_Scanner
{
    public override PathEndMode PathEndMode => PathEndMode.ClosestTouch;
    public override Danger MaxPathDanger(Pawn pawn) => Danger.Deadly;

    public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn) =>
        pawn.Map.designationManager.SpawnedDesignationsOfDef(RMDefOf.RM_Scan).Select(d => d.target.Thing).ToList();

    public override bool ShouldSkip(Pawn pawn, bool forced = false) => !pawn.Map.designationManager.AnySpawnedDesignationOfDef(RMDefOf.RM_Scan);

    public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
    {
        if (pawn.Map.designationManager.DesignationOn(t, RMDefOf.RM_Scan) == null || t.IsForbidden(pawn) || t.IsBurning()
            || !pawn.CanReserve(t, 1, -1, null, forced)) return false;
        if (FindHopper(pawn, t) != null) return true;
        JobFailReason.Is("RM_NoHopper".Translate());
        return false;
    }

    public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
    {
        Building_RMHopper hopper = FindHopper(pawn, t);
        if (hopper == null) return null;
        Job job = JobMaker.MakeJob(RMDefOf.RM_ScanItem, t, hopper);
        job.count = 1;
        return job;
    }

    static Building_RMHopper FindHopper(Pawn pawn, Thing t) =>
        Building_RMHopper.Spawned_.Where(h => h.Map == pawn.Map && h.Working && !h.IsForbidden(pawn)
                                               && pawn.CanReach(h, PathEndMode.Touch, Danger.Deadly))
            .OrderBy(h => h.Position.DistanceToSquared(t.Position)).FirstOrDefault();
}

/// Носильщик несёт одну штуку в расщепитель, тот её сканирует (разбор + шаблон), даже если фильтр её не берёт.
public class JobDriver_RMScan : JobDriver
{
    Building_RMHopper Hopper => (Building_RMHopper)job.GetTarget(TargetIndex.B).Thing;

    public override bool TryMakePreToilReservations(bool errorOnFailed) => pawn.Reserve(job.GetTarget(TargetIndex.A), job, 1, 1, null, errorOnFailed);

    protected override IEnumerable<Toil> MakeNewToils()
    {
        this.FailOnDespawnedNullOrForbidden(TargetIndex.B);
        this.FailOn(() => !Hopper.Working);
        yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.ClosestTouch).FailOnDespawnedNullOrForbidden(TargetIndex.A)
            .FailOn(() => Map.designationManager.DesignationOn(job.GetTarget(TargetIndex.A).Thing, RMDefOf.RM_Scan) == null);
        yield return Toils_General.DoAtomic(() => Map.designationManager.TryRemoveDesignationOn(job.GetTarget(TargetIndex.A).Thing, RMDefOf.RM_Scan));
        yield return Toils_Haul.StartCarryThing(TargetIndex.A, false, true).FailOnDestroyedNullOrForbidden(TargetIndex.A);
        yield return Toils_Goto.GotoThing(TargetIndex.B, PathEndMode.Touch);
        Toil scan = ToilMaker.MakeToil("RM_Scan");
        scan.initAction = () =>
        {
            if (pawn.carryTracker.CarriedThing is { } t)
            {
                pawn.carryTracker.innerContainer.Remove(t);
                Hopper.Process(t);
                RMDefOf.RM_Dematerialize.PlayOneShot(new TargetInfo(Hopper.Position, Map));
            }
        };
        yield return scan;
    }
}

// ---- графика ----

public class CompProperties_SecondLayer : CompProperties
{
    public GraphicData graphicData;
    public AltitudeLayer altitudeLayer = AltitudeLayer.Building;
    public CompProperties_SecondLayer() => compClass = typeof(CompSecondLayer);
}

/// Крышка расщепителя поверх лежащих в нём вещей (и её полосы цвета реплимата).
public class CompSecondLayer : ThingComp
{
    Graphic graphic;
    CompProperties_SecondLayer Props => (CompProperties_SecondLayer)props;

    public override void PostDraw()
    {
        (graphic ??= Props.graphicData.GraphicColoredFor(parent))
            .Draw(GenThing.TrueCenter(parent.Position, parent.Rotation, parent.def.size, Props.altitudeLayer.AltitudeFor()), parent.Rotation, parent);
        CompAccent.DrawLayer(parent, Props.graphicData, Props.altitudeLayer);
    }
}

/// Иконки меню с полосами (баки, расщепитель): своя читаемая текстура = иконка + маска <иконка>Accent в цвет реплимата.
/// Перекрашивается на месте — меню строительства, «Построить копию» и карточка подхватывают сразу.
public static class AccentIcons
{
    static readonly List<(Texture2D tex, Color32[] icon, Color32[] mask)> Icons = new();

    public static void Init()
    {
        foreach (ThingDef def in DefDatabase<ThingDef>.AllDefs.Where(d => d.HasComp(typeof(CompAccent)) && d.uiIconPath != null))
        {
            string path = Path.Combine(ReplimatMechMod.Pack.RootDir, "Textures", def.uiIconPath);
            if (!File.Exists(path + "Accent.png")) continue;
            Texture2D icon = Load(path + ".png"), mask = Load(path + "Accent.png");
            var tex = new Texture2D(icon.width, icon.height, TextureFormat.RGBA32, true) { name = def.uiIconPath, filterMode = FilterMode.Trilinear };
            Icons.Add((tex, icon.GetPixels32(), mask.GetPixels32()));
            def.uiIcon = tex;
        }
    }

    static Texture2D Load(string path)
    {
        var t = new Texture2D(2, 2);
        t.LoadImage(File.ReadAllBytes(path));
        return t;
    }

    public static void Paint()
    {
        Color c = ReplimatMechMod.S.accent;
        foreach (var (tex, icon, mask) in Icons)
        {
            var px = new Color32[icon.Length];
            for (int i = 0; i < px.Length; i++)
            {
                float v = mask[i].r / 255f;
                px[i] = Color32.Lerp(icon[i], new Color(c.r * v, c.g * v, c.b * v, icon[i].a / 255f), mask[i].a / 255f);
            }
            tex.SetPixels32(px);
            tex.Apply(true);
        }
    }
}

public class CompProperties_Accent : CompProperties
{
    public CompProperties_Accent() => compClass = typeof(CompAccent);
}

/// Цвет реплимата (настройки мода, один на все здания): полосы рисуются слоем «<текстура>Accent»
/// (серый, красится в выбранный цвет), синий по умолчанию запечён в основную текстуру.
public class CompAccent : ThingComp
{
    static readonly Dictionary<GraphicData, Graphic> Layers = new();

    public static void DrawLayer(Thing t, GraphicData gd, AltitudeLayer layer)
    {
        if (!Layers.TryGetValue(gd, out Graphic g))
        {
            string path = gd.texPath + "Accent";
            bool multi = gd.graphicClass == typeof(Graphic_Multi);
            Layers[gd] = g = ContentFinder<Texture2D>.Get(multi ? path + "_north" : path, false) == null ? null
                : GraphicDatabase.Get(gd.graphicClass, path, ShaderDatabase.Transparent, gd.drawSize, Color.white, Color.white);
        }
        g?.GetColoredVersion(ShaderDatabase.Transparent, ReplimatMechMod.S.accent, Color.white)
            .Draw(GenThing.TrueCenter(t.Position, t.Rotation, t.def.size, layer.AltitudeFor(2f)), t.Rotation, t);
    }

    public override void PostDraw() => DrawLayer(parent, parent.def.graphicData, parent.def.altitudeLayer);
}

[StaticConstructorOnStartup]
public static class Glow
{
    static Graphic G(string path, bool multi = true) => multi
        ? GraphicDatabase.Get<Graphic_Multi>(path, ShaderDatabase.MoteGlow, new Vector2(3f, 3f), Color.white)
        : GraphicDatabase.Get<Graphic_Single>(path, ShaderDatabase.MoteGlow, new Vector2(3f, 3f), Color.white);

    static readonly Graphic TermScreen = G("FX/replimatTerminalScreenGlow_north", false), TermPrint = G("FX/replimatTerminalGlow"),
        WallScreen = G("FX/replimatTerminalWallScreenGlow_north", false), WallPrint = G("FX/replimatTerminalWallGlow"),
        Feeder = G("FX/replimatAnimalFeederGlow", false), HopperScreen = G("FX/replimatHopperScreenGlow"),
        ComputerScreen = G("FX/replimatComputerScreenGlow_north", false);

    static readonly Graphic[] HopperWork = { G("FX/replimatHopperGlow0"), G("FX/replimatHopperGlow1"), G("FX/replimatHopperGlow2") };

    static void Draw(Thing t, Graphic g, float alpha = 1f, AltitudeLayer layer = AltitudeLayer.MoteOverhead)
    {
        Vector3 pos = t.DrawPos + t.Graphic.DrawOffset(t.Rotation);
        pos.y = layer.AltitudeFor() + 0.03f;
        g = g.GetColoredVersion(g.Shader, Settings.GlowColor, Color.white);
        Graphics.DrawMesh(g.MeshAt(t.Rotation), pos, Quaternion.identity, FadedMaterialPool.FadedVersionOf(g.MatAt(t.Rotation), alpha), 0);
    }

    public static void Printer(CompPrinter c)
    {
        Thing t = c.parent;
        if (t.TryGetComp<CompPowerTrader>()?.PowerOn != true) return;
        bool wall = t.def == RMDefOf.RM_ReplicatorWall, running = c.Running != null;
        if (c.Feeder)
        {
            if (running) Draw(t, Feeder);
            return;
        }
        if (t.Rotation == Rot4.North) Draw(t, wall ? WallScreen : TermScreen, 1f, t.def.altitudeLayer);
        if (running) Draw(t, wall ? WallPrint : TermPrint, 1f, t.def.altitudeLayer);
    }

    public static void Hopper(Building_RMHopper h)
    {
        if (!h.Working) return;
        Draw(h, HopperScreen, 1f, h.def.altitudeLayer);
        if (h.GlowTicks > 0) Draw(h, HopperWork[Find.TickManager.TicksGame / 5 % HopperWork.Length], Mathf.Min(1f, h.GlowTicks / 30f));
    }

    public static void Computer(Building_RMComputer c)
    {
        if (c.Rotation == Rot4.North || c.Rotation == Rot4.South) Draw(c, ComputerScreen, 1f, c.def.altitudeLayer);
    }
}
