using System.Collections.Generic;
using System.Linq;
using Multiplayer.API;
using UnityEngine;
using Verse;

namespace ReplimatMech;

/// Правки игрока в очередях и компьютере — только отсюда. В Multiplayer каждая уходит командой и выполняется у всех игроков в один тик,
/// в одиночной игре — сразу. После правки — статусы очередей сети и галки расщепителей, как в тике: окно компьютера ставит игру на паузу.
[StaticConstructorOnStartup]
public static class Edit
{
    static Edit()
    {
        if (MP.enabled) MP.RegisterAll();
    }

    /// Порядок репликаторов — по номеру вещи: у всех игроков один, кто первым оплатит пачку из общих баков.
    static void Done(CompPrinter c)
    {
        foreach (CompPrinter x in CompPrinter.All.Where(x => x.Net == c.Net).OrderBy(x => x.parent.thingIDNumber)) x.Refresh();
        Building_RMHopper.SyncAll();
    }

    [SyncMethod(cancelIfAnyArgNull = true)]
    public static void Create(CompPrinter c, Order draft)
    {
        draft.SetTarget(draft.target);
        c.orders.Add(draft);
        Done(c);
    }

    /// Настройки черновика — заданию в очереди (форма «Шаблонов», количество и режим в строке).
    [SyncMethod(cancelIfAnyArgNull = true)]
    public static void Apply(CompPrinter c, Order o, Order draft)
    {
        o.ApplySettings(draft);
        Done(c);
    }

    [SyncMethod(cancelIfAnyArgNull = true)]
    public static void Pause(CompPrinter c, Order o, bool paused)
    {
        o.paused = paused;
        Done(c);
    }

    [SyncMethod(cancelIfAnyArgNull = true)]
    public static void Remove(CompPrinter c, Order o)
    {
        c.Remove(o);
        Done(c);
    }

    [SyncMethod(cancelIfAnyArgNull = true)]
    public static void Move(CompPrinter c, Order o, CompPrinter to)
    {
        c.MoveTo(o, to);
        Done(c);
        if (to.Net != c.Net) Done(to);
    }

    /// Перетаскивание строки: номер в своей очереди (первой — important) на место в той же или другой.
    [SyncMethod]
    public static void Reorder(CompPrinter c, bool fromImp, int from, bool toImp, int to)
    {
        List<Order> src = c.orders.Where(o => o.important == fromImp).ToList();
        if (from < 0 || from >= src.Count) return;
        Order moved = src[from];
        if (fromImp == toImp && to > from) to--;
        moved.important = toImp;
        List<Order> imp = c.orders.Where(o => o.important && o != moved).ToList(), norm = c.orders.Where(o => !o.important && o != moved).ToList();
        List<Order> dst = toImp ? imp : norm;
        dst.Insert(Mathf.Clamp(to, 0, dst.Count), moved);
        c.orders = imp.Concat(norm).ToList();
        Done(c);
    }

    [SyncMethod(cancelIfAnyArgNull = true)]
    public static void CollectValue(Building_RMComputer computer, bool on) => computer.collectValue = on;

    /// Готовый механоид ждёт механитора: i — номер в списке ожидания.
    [SyncMethod]
    public static void Reassign(CompPrinter c, int i, Pawn mechanitor)
    {
        if (i >= 0 && i < c.waiting.Count) c.waiting[i].mechanitor = mechanitor;
    }

    [SyncMethod]
    public static void CancelMech(CompPrinter c, int i)
    {
        if (i < 0 || i >= c.waiting.Count) return;
        c.Refund(c.waiting[i].mass, c.waiting[i].value);
        c.waiting.RemoveAt(i);
    }

    /// Задание из очереди — репликатор и номер; черновик формы (ещё не в очереди) — его настройки.
    [SyncWorker]
    static void SyncOrder(SyncWorker sync, ref Order o)
    {
        Order live = o;
        CompPrinter c = sync.isWriting ? CompPrinter.All.FirstOrDefault(x => x.orders.Contains(live)) : null;
        bool queued = c != null;
        sync.Bind(ref queued);
        if (queued)
        {
            int i = sync.isWriting ? c.orders.IndexOf(o) : 0;
            sync.Bind(ref c);
            sync.Bind(ref i);
            if (!sync.isWriting) o = c != null && i < c.orders.Count ? c.orders[i] : null;
            return;
        }
        o ??= new Order();
        string key = o.pattern?.Key;
        sync.Bind(ref key);
        o.pattern = GameComponent_RM.Get.ByKey(key);
        sync.Bind(ref o.mode);
        sync.Bind(ref o.target);
        sync.Bind(ref o.manualQuality);
        sync.Bind(ref o.quality);
        sync.Bind(ref o.important);
        sync.Bind(ref o.mechanitor);
        sync.Bind(ref o.editor);
        sync.Bind(ref o.slider);
        sync.Bind(ref o.pct);
    }
}
