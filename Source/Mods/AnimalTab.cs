using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;

namespace Multiplayer.Compat
{
    /// <summary>Animal Tab by Fluffy</summary>
    /// <see href="https://github.com/fluffy-mods/AnimalTab"/>
    /// <see href="https://steamcommunity.com/workshop/filedetails/?id=712141500"/>
    [MpCompatFor("Fluffy.AnimalTab")]
    internal class AnimalTab
    {
        private static ConstructorInfo commandCtor;
        private static AccessTools.FieldRef<object, ThingComp> compField;
        private static Type compHandlerSettingsType;
        private static ISyncField modeField;
        private static ISyncField levelField;

        public AnimalTab(ModContentPack mod)
        {
            var type = AccessTools.TypeByName("AnimalTab.Command_HandlerSettings");
            commandCtor = AccessTools.Constructor(type, new[] { AccessTools.TypeByName("AnimalTab.CompHandlerSettings") });
            compField = AccessTools.FieldRefAccess<ThingComp>(type, "comp");
            MP.RegisterSyncMethod(type, "MassSetMode").SetContext(SyncContext.MapSelected);
            MP.RegisterSyncMethod(type, "MassSetHandler").SetContext(SyncContext.MapSelected);
            MP.RegisterSyncMethod(type, "MassSetLevel").SetContext(SyncContext.MapSelected);
            MP.RegisterSyncWorker<Command_Action>(SyncHandlerSettingsCommand, type);

            type = AccessTools.TypeByName("AnimalTab.PawnColumnWorker_Handler");
            MpCompat.RegisterLambdaDelegate(type, "DoHandlerFloatMenu", 0, 1, 2);
            MpCompat.RegisterLambdaDelegate(type, "DoMassHandlerFloatMenu", 0, 1, 4);
            MpCompat.harmony.Patch(AccessTools.Method(type, nameof(PawnColumnWorker.DoHeader)),
                prefix: new HarmonyMethod(typeof(AnimalTab), nameof(PreDoHeader)),
                postfix: new HarmonyMethod(typeof(AnimalTab), nameof(StopWatch)));
            MpCompat.harmony.Patch(AccessTools.Method(type, nameof(PawnColumnWorker.DoCell)),
                prefix: new HarmonyMethod(typeof(AnimalTab), nameof(PreDoCell)),
                postfix: new HarmonyMethod(typeof(AnimalTab), nameof(StopWatch)));

            type = compHandlerSettingsType = AccessTools.TypeByName("AnimalTab.CompHandlerSettings");
            modeField = MP.RegisterSyncField(type, "_mode");
            levelField = MP.RegisterSyncField(type, "_level");
            // FIXES row 412(a): Alert_HandlerInvalid runs on each PC outside the tick and calls IsValid, which resets the mode
            // through Notify_HandlerGone (Mode = Any, then SetDefaults) when the handler is destroyed. Unsynced, that changed
            // _mode/_handler/_level on the PCs whose alert fired first. Synced, the alert path sends a command and every PC
            // applies it on the same tick; the tick path (Pawn.Kill -> Notify_HandlerDied) still runs it directly.
            MP.RegisterSyncMethod(type, "Notify_HandlerGone");
        }

        private static void PreDoHeader(PawnTable table)
        {
            if (!MP.IsInMultiplayer)
                return;

            MP.WatchBegin();
            foreach (var pawn in table.PawnsListForReading)
            {
                var comp = pawn.comps.FirstOrDefault(c => c.GetType() == compHandlerSettingsType);
                if (comp == null) continue;
                modeField.Watch(comp);
                levelField.Watch(comp);
            }
        }

        private static void PreDoCell(Pawn target)
        {
            if (!MP.IsInMultiplayer)
                return;

            MP.WatchBegin();
            var comp = target.comps.FirstOrDefault(c => c.GetType() == compHandlerSettingsType);
            if (comp == null) return;
            modeField.Watch(comp);
            levelField.Watch(comp);
        }

        private static void StopWatch()
        {
            if (MP.IsInMultiplayer)
                MP.WatchEnd();
        }

        private static void SyncHandlerSettingsCommand(SyncWorker sync, ref Command_Action obj)
        {
            if (sync.isWriting)
                sync.Write(compField(obj));
            else
                obj = (Command_Action)commandCtor.Invoke(new object[] { sync.Read<ThingComp>() });
        }
    }
}
