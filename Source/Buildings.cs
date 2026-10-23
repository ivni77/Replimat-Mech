using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ReplimatMech;

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

/// Компьютер: помнит оператора — его линки решают, какие группы печатают репликаторы сети и как быстро. Окно — только смотреть.
public class Building_RMComputer : Building
{
    CompPowerTrader power;
    public bool collectValue = true;
    public Pawn operatorPawn;
    public bool Powered => power?.PowerOn == true;
    public PowerNet Net => power?.PowerNet;

    /// Оператор, пока он жив и на карте компьютера: сон и ранение не мешают.
    public Pawn Operator => operatorPawn is { Dead: false } p && p.MapHeld == Map ? p : null;

    /// Оператор сети — у первого запитанного компьютера, где он есть (по номеру вещи: у всех игроков Multiplayer один).
    public static Pawn OperatorOf(PowerNet net) =>
        Tanks.On<Building_RMComputer>(net).Where(c => c.Powered).OrderBy(c => c.thingIDNumber).Select(c => c.Operator).FirstOrDefault(p => p != null);

    public override void SpawnSetup(Map map, bool respawningAfterLoad)
    {
        base.SpawnSetup(map, respawningAfterLoad);
        power = GetComp<CompPowerTrader>();
    }

    /// Линки поселенца списком: «риплинк, мехлинк».
    public static string LinkList(Pawn p) => string.Join(", ", Links.All.Where(l => Links.Has(p, l)).Select(l => Links.Def(l).label));

    /// Правый клик поселенцем: связаться — стать оператором. Нужен хоть один линк; новый оператор заменяет старого.
    public override IEnumerable<FloatMenuOption> GetFloatMenuOptions(Pawn p)
    {
        foreach (FloatMenuOption o in base.GetFloatMenuOptions(p)) yield return o;
        string label = "RM_UseComputer".Translate();
        string why = !Links.Any(p) ? "RM_NeedLink".Translate()
            : p == Operator ? "RM_AlreadyOperator".Translate()
            : !Powered ? "RM_ComputerNoPower".Translate()
            : !p.CanReach(this, PathEndMode.Touch, Danger.Deadly) ? "NoPath".Translate() : null;
        yield return why != null
            ? new FloatMenuOption(label + " (" + why + ")", null)
            : FloatMenuUtility.DecoratePrioritizedTask(new FloatMenuOption(RMUI.Tr("RM_UseComputerLinks", label, LinkList(p)),
                () => p.jobs.TryTakeOrderedJob(JobMaker.MakeJob(RMDefOf.RM_UseComputer, this), JobTag.Misc)), p, this);
    }

    /// Связь состоялась: оператор — этот поселенец, репликаторы сети сразу пересчитывают статусы.
    public void Bind(Pawn p)
    {
        operatorPawn = p;
        Messages.Message(RMUI.Tr("RM_MsgOperator", p.LabelShort, LinkList(p)), this, MessageTypeDefOf.PositiveEvent);
        foreach (CompPrinter c in CompPrinter.All.Where(c => c.Net == Net).OrderBy(c => c.parent.thingIDNumber)) c.Refresh();
    }

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Values.Look(ref collectValue, "collectValue", true);
        Scribe_References.Look(ref operatorPawn, "operator");
    }

    public override string GetInspectString()
    {
        var sb = new StringBuilder();
        if (Spawned)
        {
            sb.AppendLine(Operator is { } op ? RMUI.Tr("RM_OperatorLine", op.LabelShort, LinkList(op)) : "RM_StNoOperatorFull".Translate().ToString());
            List<CompPrinter> printers = CompPrinter.All.Where(c => c.Net == Net && Net != null).ToList();
            int tanks = Tanks.All(Net).Count, feeders = printers.Count(c => c.Feeder), incubators = printers.Count(c => c.Incubator),
                reps = printers.Count - feeders - incubators;
            if (tanks == 0) sb.AppendLine("RM_NoTanks".Translate());
            else
            {
                sb.AppendLine("RM_MassLine".Translate(Fmt.MassNum(Tanks.Mass(Net)), Fmt.Mass(Tanks.Capacity(Net))));
                if (Tanks.Value(Net) > 0f) sb.AppendLine("RM_ValueLine".Translate(Fmt.Silver(Tanks.Value(Net))));
                sb.AppendLine("RM_TanksCount".Translate(tanks));
            }
            if (reps > 0) sb.AppendLine("RM_PrintersCount".Translate(reps));
            if (feeders > 0) sb.AppendLine("RM_FeedersCount".Translate(feeders));
            if (incubators > 0) sb.AppendLine("RM_IncubatorsCount".Translate(incubators));
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

/// Поселенец с линком подходит к компьютеру и связывается с ним: становится оператором.
public class JobDriver_RMUseComputer : JobDriver
{
    Building_RMComputer Computer => TargetThingA as Building_RMComputer;

    public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

    protected override IEnumerable<Toil> MakeNewToils()
    {
        this.FailOn(() => Computer is not { Spawned: true, Powered: true });
        yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
        yield return Toils_General.Wait(120, TargetIndex.A).WithProgressBarToilDelay(TargetIndex.A);
        yield return Toils_General.DoAtomic(() => Computer.Bind(pawn));
    }
}

// ---- графика ----

public class CompProperties_SecondLayer : CompProperties
{
    public GraphicData graphicData;
    public AltitudeLayer altitudeLayer = AltitudeLayer.Building;
    public CompProperties_SecondLayer() => compClass = typeof(CompSecondLayer);
}

/// Слой поверх того, что внутри (и его полосы цвета реплимата): крышка расщепителя над вещами, блок биосканера над существом — он ездит.
public class CompSecondLayer : ThingComp
{
    Graphic graphic;
    CompProperties_SecondLayer Props => (CompProperties_SecondLayer)props;

    public override void PostDraw()
    {
        Vector3 off = parent is Building_RMBioscanner s ? s.BlockOffset : Vector3.zero;
        (graphic ??= Props.graphicData.GraphicColoredFor(parent))
            .Draw(GenThing.TrueCenter(parent.Position, parent.Rotation, parent.def.size, Props.altitudeLayer.AltitudeFor()) + off, parent.Rotation, parent);
        CompAccent.DrawLayer(parent, Props.graphicData, Props.altitudeLayer, off);
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

    public static void DrawLayer(Thing t, GraphicData gd, AltitudeLayer layer, Vector3 offset = default)
    {
        if (!Layers.TryGetValue(gd, out Graphic g))
        {
            string path = gd.texPath + "Accent";
            bool multi = gd.graphicClass == typeof(Graphic_Multi);
            Layers[gd] = g = ContentFinder<Texture2D>.Get(multi ? path + "_north" : path, false) == null ? null
                : GraphicDatabase.Get(gd.graphicClass, path, ShaderDatabase.Transparent, gd.drawSize, Color.white, Color.white);
        }
        g?.GetColoredVersion(ShaderDatabase.Transparent, ReplimatMechMod.S.accent, Color.white)
            .Draw(GenThing.TrueCenter(t.Position, t.Rotation, t.def.size, layer.AltitudeFor(2f)) + offset, t.Rotation, t);
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
        ComputerScreen = G("FX/replimatComputerScreenGlow_north", false), Scanner = G("FX/replimatBioscannerGlow"),
        IncubatorScreen = G("FX/replimatIncubatorScreenGlow"), IncubatorGlass = G("FX/replimatIncubatorGlow");

    static readonly Graphic[] HopperWork = { G("FX/replimatHopperGlow0"), G("FX/replimatHopperGlow1"), G("FX/replimatHopperGlow2") };

    static void Draw(Thing t, Graphic g, float alpha = 1f, AltitudeLayer layer = AltitudeLayer.MoteOverhead, Vector3 offset = default)
    {
        Vector3 pos = t.DrawPos + t.Graphic.DrawOffset(t.Rotation) + offset;
        pos.y = layer.AltitudeFor() + 0.03f;
        g = g.GetColoredVersion(g.Shader, Settings.GlowColor, Color.white);
        Graphics.DrawMesh(g.MeshAt(t.Rotation), pos, Quaternion.identity, FadedMaterialPool.FadedVersionOf(g.MatAt(t.Rotation), alpha), 0);
    }

    public static void Printer(CompPrinter c)
    {
        Thing t = c.parent;
        if (t.TryGetComp<CompPowerTrader>()?.PowerOn != true) return;
        bool wall = t.def == RMDefOf.RM_ReplicatorWall, running = c.Running != null;
        // инкубатор: экран базы горит при питании, стекло — пока печатает
        if (c.Incubator)
        {
            Draw(t, IncubatorScreen, 1f, t.def.altitudeLayer);
            if (!running) return;
            Draw(t, IncubatorGlass);
            Progress(t, c.Running.progress / Mathf.Max(1f, c.Running.batchTicks));
            return;
        }
        if (c.Feeder)
        {
            if (running) Draw(t, Feeder);
            return;
        }
        if (t.Rotation == Rot4.North) Draw(t, wall ? WallScreen : TermScreen, 1f, t.def.altitudeLayer);
        if (running) Draw(t, wall ? WallPrint : TermPrint, 1f, t.def.altitudeLayer);
    }

    /// Экран биорепликатора — полоса готовности (2026-10-21): одна тусклая полоса в 1/3 длины здания (128 точек текстуры из 384),
    /// по ходу печати заливается синим слева направо, на боковом экране — снизу вверх. Полоса — как STRIP_N/STRIP_E в Tools/textures.py;
    /// у южного вида экрана нет.
    static readonly (float x0, float y0, float x1, float y1) StripN = (39f, 257f, 167f, 281f), StripE = (65f, 35f, 87f, 163f);
    static readonly Material Bar = SolidColorMaterials.SimpleSolidColorMaterial(new Color(91f / 255f, 147f / 255f, 1f));

    static void Progress(Thing t, float done)
    {
        if (t.Rotation == Rot4.South || done <= 0f) return;
        done = Mathf.Min(1f, done);
        var (x0, y0, x1, y1) = t.Rotation == Rot4.North ? StripN : StripE;
        if (t.Rotation == Rot4.North) x1 = x0 + (x1 - x0) * done;
        else y0 = y1 - (y1 - y0) * done;
        float cx = ((x0 + x1) / 2f - 192f) / 128f, cz = (192f - (y0 + y1) / 2f) / 128f;
        if (t.Rotation == Rot4.West) cx = -cx;  // запад — отражённый восток
        Vector3 pos = t.DrawPos + t.Graphic.DrawOffset(t.Rotation) + new Vector3(cx, 0f, cz);
        pos.y = t.def.altitudeLayer.AltitudeFor() + 0.025f;
        Graphics.DrawMesh(MeshPool.plane10, Matrix4x4.TRS(pos, Quaternion.identity, new Vector3((x1 - x0) / 128f, 1f, (y1 - y0) / 128f)), Bar, 0);
    }

    public static void Hopper(Building_RMHopper h)
    {
        if (!h.Working) return;
        Draw(h, HopperScreen, 1f, h.def.altitudeLayer);
        if (h.GlowTicks > 0) Draw(h, HopperWork[Find.TickManager.TicksGame / 5 % HopperWork.Length], Mathf.Min(1f, h.GlowTicks / 30f));
    }

    /// Луч и экран блока биосканера — вместе с блоком.
    public static void Bioscanner(Building_RMBioscanner s) => Draw(s, Scanner, 1f, AltitudeLayer.MoteOverhead, s.BlockOffset);

    public static void Computer(Building_RMComputer c)
    {
        if (c.Rotation == Rot4.North || c.Rotation == Rot4.South) Draw(c, ComputerScreen, 1f, c.def.altitudeLayer);
    }
}
