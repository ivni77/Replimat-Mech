using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ReplimatMech;

/// Биосканер — как экстрактор генов ванили (Building_Enterable): «Сканировать...» — один список людей и животных, правый клик колонистом —
/// «Войти»; ручное животное
/// ведёт на верёвке колонист с работой «Животные», колонист идёт сам, заключённого и лежачего несут. Скан при питании, существо лежит
/// на столе (потребности стоят), блок сканера ездит над ним; потом оно выходит как было, ДНК записана на всю колонию (люди — одна ДНК).
[StaticConstructorOnStartup]
public class Building_RMBioscanner : Building_Enterable, IThingHolderWithDrawnPawn
{
    public const int ScanTicks = 7500;
    static readonly Texture2D InsertTex = ContentFinder<Texture2D>.Get("UI/Gizmos/InsertPawn"), CancelTex = ContentFinder<Texture2D>.Get("UI/Designators/Cancel");

    internal int ticksLeft;
    CompPowerTrader power;

    Pawn Contained => innerContainer.FirstOrDefault() as Pawn;
    bool PowerOn => power?.PowerOn == true;

    // лежит вдоль стола боком (2026-10-18): стол вдоль экрана — головой вверх, поперёк — животное как есть, человек головой
    // влево; животное в профиль и так лежит, человек стоит — ему поворот на четверть больше
    public float HeldPawnDrawPos_Y => DrawPos.y + 0.03658537f;
    public float HeldPawnBodyAngle => Rotation.IsHorizontal == (Contained?.RaceProps.Humanlike == true) ? 0f : 270f;
    public PawnPosture HeldPawnPosture => PawnPosture.LayingOnGroundNormal;
    public override Vector3 PawnDrawOffset => Vector3.zero;

    /// Блок сканера во время скана ездит вдоль стола туда и обратно (по оставшемуся времени: без питания стоит).
    public Vector3 BlockOffset
    {
        get
        {
            if (!Working) return Vector3.zero;
            float s = Mathf.Sin(ticksLeft * (2f * Mathf.PI / 600f)) * 0.9f;
            return Rotation.IsHorizontal ? new Vector3(0f, 0f, s) : new Vector3(s, 0f, 0f);
        }
    }

    public override void SpawnSetup(Map map, bool respawningAfterLoad)
    {
        base.SpawnSetup(map, respawningAfterLoad);
        power = GetComp<CompPowerTrader>();
    }

    /// Кого сканировать: живое животное этой карты — колонии или дикое лежачее; человек — как в экстрактор генов ванили: колонист,
    /// раб или заключённый колонии — и любой лежачий (2026-10-18), не гость квеста.
    public bool Fits(Pawn p) => p is { Dead: false } && p.MapHeld == Map
        && (p.RaceProps.Animal ? p.Faction == Faction.OfPlayer || p.Faction == null && p.Downed
            : p.RaceProps.Humanlike && (p.IsColonist || p.IsSlaveOfColony || p.IsPrisonerOfColony || p.Downed) && !p.IsQuestLodger());

    public override AcceptanceReport CanAcceptPawn(Pawn p)
    {
        if (!Rules.BioOn || !Fits(p) || selectedPawn != null && selectedPawn != p) return false;
        if (!PowerOn) return "NoPower".Translate().CapitalizeFirst();
        if (innerContainer.Count > 0) return "Occupied".Translate();
        if (GameComponent_RM.Get.KnowsDna(p)) return Known(p);
        return true;
    }

    /// Почему нельзя: ДНК животного записана, у человека всё уже в базе.
    static string Known(Pawn p) => (p.RaceProps.Humanlike ? "RM_BaseKnown" : "RM_DnaKnown").Translate();

    /// Сканеры карты — ближние первыми (при равных — по номеру вещи: у всех игроков Multiplayer один).
    static List<Building_RMBioscanner> Near(Pawn p) => p.Map.listerBuildings.AllBuildingsColonistOfClass<Building_RMBioscanner>()
        .OrderBy(s => s.Position.DistanceToSquared(p.Position)).ThenBy(s => s.thingIDNumber).ToList();

    static Building_RMBioscanner Free(Pawn p, List<Building_RMBioscanner> near) =>
        near.FirstOrDefault(s => !s.Working && s.SelectedPawn == null && s.CanAcceptPawn(p).Accepted);

    /// Кнопка «Сканировать» на животном колонии, сбитом диком (2026-10-23) и на лежачем человеке (2026-10-18), чьей ДНК ещё нет.
    /// Нет сканера на карте или ДНК есть — кнопки нет; уже ждёт сканер (2026-10-23), сканеры заняты или без питания — кнопка серая с причиной.
    public static AcceptanceReport CanSend(Pawn p)
    {
        if (!Rules.BioOn || !p.Spawned || p.Dead || GameComponent_RM.Get.KnowsDna(p)) return false;
        if (!(p.RaceProps.Animal ? p.Faction == Faction.OfPlayer || p.Faction == null && p.Downed : p.RaceProps.Humanlike && p.Downed && !p.IsQuestLodger()))
            return false;
        List<Building_RMBioscanner> near = Near(p);
        if (near.Count == 0) return false;
        if (near.Any(s => s.SelectedPawn == p)) return "RM_ScanWaiting".Translate().ToString();
        if (Free(p, near) != null) return true;
        Building_RMBioscanner first = near[0];
        return first.Working || first.SelectedPawn != null ? "Occupied".Translate().CapitalizeFirst() : first.CanAcceptPawn(p);
    }

    /// В ближний свободный сканер — дальше как из списка «Сканировать...».
    public static void Send(Pawn p)
    {
        if (Free(p, Near(p)) is { } s) Edit.SelectScan(s, p);
    }

    /// Сканер для существа: тот, где оно уже ждёт, иначе ближний свободный; null — такого нет.
    public static Building_RMBioscanner For(Pawn p)
    {
        List<Building_RMBioscanner> near = Near(p);
        return near.FirstOrDefault(s => s.SelectedPawn == p) ?? Free(p, near);
    }

    /// Выбрано в списке или правым кликом (Edit.SelectScan): животное приводит или приносит работа «Животные», человека несёт «Переноска»;
    /// колонист на ногах идёт сам, как в экстрактор генов ванили (уже выбранный — снова).
    public void Select(Pawn p)
    {
        if (Working || selectedPawn != null && selectedPawn != p || !CanAcceptPawn(p).Accepted) return;
        selectedPawn = p;
        if (p.RaceProps.Humanlike && !p.IsPrisonerOfColony && !p.Downed) p.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.EnterBuilding, this), JobTag.Misc);
    }

    /// Принимает только выбранное: отменили, пока несли, — носильщик его опустит.
    public override void TryAcceptPawn(Pawn p)
    {
        if (p != selectedPawn || !CanAcceptPawn(p).Accepted) return;
        bool selected = p.DeSpawnOrDeselect();
        if (innerContainer.TryAddOrTransfer(p))
        {
            startTick = Find.TickManager.TicksGame;
            ticksLeft = ScanTicks;
        }
        if (selected) Find.Selector.Select(p, false, false);
    }

    protected override void Tick()
    {
        base.Tick();
        // свёрнутый тикает без карты — стоит
        if (!Spawned) return;
        if (!Working)
        {
            // выбранное умерло, ушло с карты или перестало подходить (дикое встало) — выбор снимается
            if (selectedPawn != null && (!Rules.BioOn || !Fits(selectedPawn))) Cancel();
            return;
        }
        if (Contained == null || !Rules.BioOn) Cancel();
        else if (PowerOn && --ticksLeft <= 0) Finish();
    }

    /// Отмена или конец скана: существо — у места работы, как в ванили; кто вёл или нёс — бросает.
    public void Cancel()
    {
        startTick = -1;
        ticksLeft = 0;
        selectedPawn = null;
        innerContainer.TryDropAll(InteractionCell, Map, ThingPlaceMode.Near);
        foreach (Pawn w in Map.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer)
                     .Where(w => w.CurJob is { } j && ((j.def == JobDefOf.CarryToBuilding || j.def == JobDefOf.EnterBuilding) && j.targetA.Thing == this
                                                       || j.def == RMDefOf.RM_RopeToScanner && j.targetC.Thing == this)).ToList())
            w.jobs.EndCurrentJob(JobCondition.InterruptForced);
    }

    void Finish()
    {
        Pawn p = Contained;
        Cancel();
        GameComponent_RM.Get.RecordDna(p);
    }

    public override IEnumerable<Gizmo> GetGizmos()
    {
        foreach (Gizmo g in base.GetGizmos()) yield return g;
        if (!Rules.BioOn) yield break;
        if (Working || selectedPawn != null)
        {
            yield return new Command_Action
            {
                defaultLabel = (Working ? "CommandCancelSubcoreScan" : "CommandCancelLoad").Translate(),
                icon = CancelTex,
                activateSound = SoundDefOf.Designate_Cancel,
                action = () => Edit.CancelScan(this),
            };
            if (Working && DebugSettings.ShowDevGizmos) yield return new Command_Action { defaultLabel = "DEV: Finish scan", action = () => Edit.FinishScan(this) };
            yield break;
        }
        var insert = new Command_Action
        {
            defaultLabel = "RM_DesignatorScan".Translate() + "...",
            icon = InsertTex,
            action = () => Find.WindowStack.Add(new FloatMenu(Options())),
        };
        if (!PowerOn) insert.Disable("NoPower".Translate().CapitalizeFirst());
        yield return insert;
    }

    /// Люди и животные — одним списком (2026-10-18): колонисты, рабы, заключённые, другие лежачие люди, потом животные колонии
    /// и лежачие дикие. Только те, кого сканер примет: записанной ДНК в списке нет.
    List<FloatMenuOption> Options()
    {
        static int Rank(Pawn p) => p.RaceProps.Humanlike ? p.IsColonist ? 0 : p.IsSlaveOfColony ? 1 : p.IsPrisonerOfColony ? 2 : 3 : p.Faction == null ? 5 : 4;
        List<FloatMenuOption> list = Map.mapPawns.AllPawnsSpawned.Where(p => Fits(p) && CanAcceptPawn(p).Accepted)
            .OrderBy(Rank).ThenBy(p => p.KindLabel).ThenBy(p => p.LabelShort)
            .Select(p => new FloatMenuOption(Bio.Name(p), () => Edit.SelectScan(this, p), p, Color.white)).ToList();
        if (list.Count == 0) list.Add(new FloatMenuOption("RM_NoScan".Translate(), null));
        return list;
    }

    /// Правый клик выбранным колонистом по сканеру — как в экстрактор генов ванили (2026-10-18): «Войти» — идёт сам; нельзя — строка
    /// с причиной, первой — «ДНК записана».
    public override IEnumerable<FloatMenuOption> GetFloatMenuOptions(Pawn selPawn)
    {
        foreach (FloatMenuOption o in base.GetFloatMenuOptions(selPawn)) yield return o;
        if (!Rules.BioOn || !Fits(selPawn)) yield break;
        AcceptanceReport r = Enter(selPawn);
        if (r.Accepted)
            yield return FloatMenuUtility.DecoratePrioritizedTask(new FloatMenuOption("EnterBuilding".Translate(this), () => Edit.SelectScan(this, selPawn)), selPawn, this);
        else if (!r.Reason.NullOrEmpty()) yield return new FloatMenuOption("CannotEnterBuilding".Translate(this) + ": " + r.Reason.CapitalizeFirst(), null);
    }

    AcceptanceReport Enter(Pawn p)
    {
        if (GameComponent_RM.Get.KnowsDna(p)) return Known(p);
        if (!p.CanReach(this, PathEndMode.InteractionCell, Danger.Deadly)) return "NoPath".Translate().ToString();
        if (Working || selectedPawn != null && selectedPawn != p) return "Occupied".Translate().ToString();
        return CanAcceptPawn(p);
    }

    public override void DynamicDrawPhaseAt(DrawPhase phase, Vector3 drawLoc, bool flip = false)
    {
        base.DynamicDrawPhaseAt(phase, drawLoc, flip);
        if (Working && Contained is { } p) p.Drawer.renderer.DynamicDrawPhaseAt(phase, drawLoc + PawnDrawOffset, Rot4.East, true);
    }

    protected override void DrawAt(Vector3 drawLoc, bool flip = false)
    {
        base.DrawAt(drawLoc, flip);
        if (Working && PowerOn) Glow.Bioscanner(this);
    }

    public override string GetInspectString()
    {
        var sb = new StringBuilder();
        if (!Rules.BioOn) sb.AppendLine("RM_BioOff".Translate());
        else if (Working && Contained is { } p)
        {
            sb.AppendLine(RMUI.Tr("RM_Scanning", p.LabelShort));
            sb.AppendLine((PowerOn ? "DurationLeft".Translate(Fmt.Time(ticksLeft)) : "NoPower".Translate()).CapitalizeFirst());
        }
        else if (selectedPawn != null) sb.AppendLine("WaitingForPawn".Translate(selectedPawn.Named("PAWN")).Resolve());
        sb.Append(base.GetInspectString());
        return sb.ToString().TrimEndNewlines();
    }

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Values.Look(ref ticksLeft, "ticksLeft");
    }
}

/// Правый клик колонистом по лежачему животному, чьей ДНК нет (2026-10-23): «Отнести к биосканеру», как «Перенести в саркофаг криптосна»
/// ванили, даже если оно не выбрано в сканере (Edit.CarryToScan). Сканеры заняты или без питания — строка серая с причиной.
public class FloatMenuOptionProvider_RMCarryToBioscanner : FloatMenuOptionProvider
{
    protected override bool Drafted => true;
    protected override bool Undrafted => true;
    protected override bool Multiselect => false;
    protected override bool RequiresManipulation => true;

    protected override FloatMenuOption GetSingleOptionFor(Pawn clickedPawn, FloatMenuContext context)
    {
        Pawn carrier = context.FirstSelectedPawn;
        if (!clickedPawn.RaceProps.Animal || !clickedPawn.Downed || Building_RMBioscanner.CanSend(clickedPawn) is { Accepted: false, Reason: null or "" }
            || !carrier.CanReserveAndReach(clickedPawn, PathEndMode.OnCell, Danger.Deadly, 1, -1, null, true)) return null;
        Building_RMBioscanner s = Building_RMBioscanner.For(clickedPawn);
        Thing target = s ?? clickedPawn.Map.listerBuildings.AllBuildingsColonistOfClass<Building_RMBioscanner>().FirstOrDefault();
        string text = "CarryToSpecificThing".Translate(target);
        return FloatMenuUtility.DecoratePrioritizedTask(s != null
            ? new FloatMenuOption(text, () => Edit.CarryToScan(carrier, clickedPawn), MenuOptionPriority.Default, null, clickedPawn)
            : new FloatMenuOption(text + " (" + Building_RMBioscanner.CanSend(clickedPawn).Reason + ")", null, MenuOptionPriority.Default, null, clickedPawn),
            carrier, clickedPawn);
    }
}

/// Работа «Животные»: выбранное в сканере животное — на верёвке, лежачее — на руках (ванильная CarryToBuilding).
public class WorkGiver_RMBioscanner : WorkGiver_Scanner
{
    public override ThingRequest PotentialWorkThingRequest => ThingRequest.ForDef(RMDefOf.RM_Bioscanner);
    public override PathEndMode PathEndMode => PathEndMode.InteractionCell;

    public override bool ShouldSkip(Pawn pawn, bool forced = false) => !Rules.BioOn;

    public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false) =>
        t is Building_RMBioscanner { Working: false, SelectedPawn: { RaceProps.Animal: true } p } s && s.CanAcceptPawn(p).Accepted && !p.InMentalState
        && (p.Downed || p.roping.RopedByPawn == null)
        && pawn.Map.designationManager.DesignationOn(t, DesignationDefOf.Deconstruct) == null
        && pawn.CanReach(t, PathEndMode.InteractionCell, Danger.Deadly)
        && pawn.CanReserveAndReach(p, PathEndMode.Touch, Danger.Deadly, 1, -1, null, forced);

    public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
    {
        var s = (Building_RMBioscanner)t;
        Pawn p = s.SelectedPawn;
        if (!p.Downed) return JobMaker.MakeJob(RMDefOf.RM_RopeToScanner, p, s.InteractionCell, s);
        Job job = JobMaker.MakeJob(JobDefOf.CarryToBuilding, s, p);
        job.count = 1;
        return job;
    }
}

/// Довести животное на верёвке до сканера, как в загон: дошло — сканер принимает его.
public class JobDriver_RMRopeToScanner : JobDriver_RopeToDestination
{
    Building_RMBioscanner Scanner => job.GetTarget(TargetIndex.C).Thing as Building_RMBioscanner;

    protected override IEnumerable<Toil> MakeNewToils()
    {
        this.FailOn(() => Scanner is not { Spawned: true, Working: false } s || s.SelectedPawn != job.GetTarget(TargetIndex.A).Thing);
        return base.MakeNewToils();
    }

    protected override bool HasRopeeArrived(Pawn ropee, bool roperWaitingAtDest) =>
        roperWaitingAtDest || ropee.Position.InHorDistOf(job.GetTarget(TargetIndex.B).Cell, 1.5f);

    protected override void ProcessArrivedRopee(Pawn ropee) => Scanner?.TryAcceptPawn(ropee);

    protected override bool ShouldOpportunisticallyRopeAnimal(Pawn animal) => false;
}

/// «Репликант» напечатанного: у животного — в здоровье; у человека скрыт (2026-10-22: на карточке только детство) и хранит
/// стоимость шаблона до наценки — рыночная стоимость человека не ниже неё (Patch_PawnValue).
public class Hediff_RMReplicant : HediffWithComps
{
    public float value;

    public override bool Visible => base.Visible && !pawn.RaceProps.Humanlike;

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Values.Look(ref value, "value");
    }
}

/// Биорепликатор — репликатор живого (CompPrinter с incubator): животные по ДНК, люди по шаблонам; печать и вид за стеклом.
public static class Bio
{
    /// Стекло инкубатора 3×2 в клетках (Tools/textures.py): середина от центра здания — вверх у стоящего поперёк экрана, вбок у стоящего
    /// вдоль; картинка животного длиннее Fit уменьшается до неё (корова, осёл — в полный размер, слон и трумбо — меньше).
    const float GlassZ = 60f / 128f, GlassX = 21f / 128f, Fit = 3f;

    /// Свежий труп животного или человека, чьей ДНК ещё нет: расщепитель её запишет.
    public static bool CorpseDna(Corpse c) => Rules.BioOn && c.InnerPawn is { } p && (p.RaceProps.Animal || p.RaceProps.Humanlike)
                                              && c.GetRotStage() == RotStage.Fresh && !GameComponent_RM.Get.KnowsDna(p);

    /// «Бэлла, корова» или «Корова», если имени нет.
    public static string Name(Pawn p) => p.Name != null ? p.LabelShortCap + ", " + p.KindLabel : p.KindLabel.CapitalizeFirst();

    /// Готово: человек шаблона или взрослое ручное животное вида (как прирученное в ванили: без хозяина и обучения, родни нет) — у места
    /// работы инкубатора, у обоих «репликант» (Hediff_RMReplicant), у человека — со стоимостью шаблона. Возраст животного — начало
    /// взрослой стадии (чуть больше: из тиков возраст выходит на волос меньше и стадия была бы прошлой).
    public static void Hatch(Order o, Thing at)
    {
        bool human = o.pattern.IsHuman;
        Pawn p = human ? o.pattern.human.Make(Human.AdultAge) : Animal(o.pattern.animal);
        if (p.health.AddHediff(RMDefOf.RM_Replicant) is Hediff_RMReplicant mark && human) mark.value = o.pattern.human.Value;
        GenPlace.TryPlaceThing(p, at.InteractionCell, at.Map, ThingPlaceMode.Near);
        Messages.Message(human ? RMUI.Tr("RM_MsgPersonPrinted", p.LabelShortCap) : RMUI.Tr("RM_MsgAnimalPrinted", Name(p)), p, MessageTypeDefOf.PositiveEvent);

        Pawn Animal(PawnKindDef k)
        {
            float age = Pricing.AdultAge(k.race) + 0.001f;
            return PawnGenerator.GeneratePawn(new PawnGenerationRequest(k, Faction.OfPlayer, forceGenerateNewPawn: true, canGeneratePawnRelations: false,
                fixedBiologicalAge: age, fixedChronologicalAge: age, fixedGender: o.Gendered ? o.gender : null));
        }
    }

    /// Человек шаблона на плитке, в задании и в очереди — во весь рост посередине, как портрет в окне шаблона (полоса колонистов
    /// сдвигает фигуру вниз и обрезает).
    public static void Face(Rect r, Pawn p)
    {
        if (p != null) GUI.DrawTexture(r, PortraitsCache.Get(p, r.size, Rot4.South, default, 1f));
    }

    /// Человек за стеклом — портрет образца стадии (2×2 клетки при приближении 1), лёжа: у стоящего поперёк экрана — головой влево,
    /// у стоящего вдоль — головой вверх.
    static void DrawPerson(Thing t, Pawn p)
    {
        if (p == null) return;
        bool wide = !t.Rotation.IsHorizontal;
        Vector3 pos = t.DrawPos + (wide ? new Vector3(0f, 0f, GlassZ) : new Vector3(t.Rotation == Rot4.East ? GlassX : -GlassX, 0f, 0f));
        pos.y = t.DrawPos.y + 0.03658537f;
        Material mat = MaterialPool.MatFrom(new MaterialRequest(PortraitsCache.Get(p, new Vector2(256f, 256f), Rot4.South, default, 1f, true, false), ShaderDatabase.Transparent));
        Graphics.DrawMesh(MeshPool.plane10, Matrix4x4.TRS(pos, Quaternion.AngleAxis(wide ? 270f : 0f, Vector3.up), new Vector3(2f, 1f, 2f)), mat, 0);
    }

    /// Начатое животное лежит за стеклом и растёт по стадиям ванили: картинка стадии — по доле готовности (детёныш, молодняк, взрослое);
    /// крупное — уменьшено до стекла. Лежит как в биосканере: боком, у стоящего вдоль экрана — головой вверх.
    public static void DrawGrowing(CompPrinter c)
    {
        if ((c.Running ?? c.orders.FirstOrDefault(x => x.paid)) is not { pattern.IsBio: true } o) return;
        Thing t = c.parent;
        if (o.pattern.IsHuman)
        {
            DrawPerson(t, o.pattern.human.Stage(o.progress / Mathf.Max(1f, o.batchTicks)));
            return;
        }
        PawnKindDef k = o.pattern.animal;
        List<LifeStageAge> ages = k.race.race.lifeStageAges;
        float age = Mathf.Clamp01(o.progress / Mathf.Max(1f, o.batchTicks)) * ages[ages.Count - 1].minAge;
        PawnKindLifeStage stage = k.lifeStages[Mathf.Clamp(ages.FindLastIndex(a => a.minAge <= age), 0, k.lifeStages.Count - 1)];
        GraphicData gd = o.gender == Gender.Female && stage.femaleGraphicData != null ? stage.femaleGraphicData : stage.bodyGraphicData;
        if (gd == null) return;
        bool wide = !t.Rotation.IsHorizontal;
        float fit = Mathf.Min(1f, Fit / gd.drawSize.x);
        Vector3 pos = t.DrawPos + (wide ? new Vector3(0f, 0f, GlassZ) : new Vector3(t.Rotation == Rot4.East ? GlassX : -GlassX, 0f, 0f));
        pos.y = t.DrawPos.y + 0.03658537f;
        Graphic g = gd.Graphic;
        Graphics.DrawMesh(g.MeshAt(Rot4.East), Matrix4x4.TRS(pos, Quaternion.AngleAxis(wide ? 0f : 270f, Vector3.up), new Vector3(fit, 1f, fit)), g.MatAt(Rot4.East), 0);
    }
}
